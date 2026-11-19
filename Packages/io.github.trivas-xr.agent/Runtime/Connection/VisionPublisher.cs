using System;
using System.Collections;
using LiveKit;
using LiveKit.Proto;
using UnityEngine;
using UnityEngine.Rendering;

namespace TrIVA.Connection
{
    /// <summary>
    /// Renders the vision camera it sits on at a low, fixed frame rate and publishes the frames to the connected LiveKit room as a camera video track, so the backend sees the virtual scene from the participant's view. The camera is switched off and rendered only at that frame rate, never to the headset; its field of view is the track's.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class VisionPublisher : MonoBehaviour
    {
        private const string TrackName = "vision";

        [SerializeField, Tooltip("Connector whose room the track is published to.")]
        private LiveKitConnector Connector;
        [SerializeField, Min(16), Tooltip("Frame width in pixels.")]
        private int Width = 640;
        [SerializeField, Min(16), Tooltip("Frame height in pixels.")]
        private int Height = 480;
        [SerializeField, Range(0.1f, 30f), Tooltip("Frames rendered and sent per second.")]
        private float FramesPerSecond = 2f;
        [SerializeField, Min(10000), Tooltip("Upper limit of the encoded video bitrate in bits per second.")]
        private int MaxBitrate = 300000;

        private Camera _camera;
        private RenderTexture _texture;
        private Room _room;
        private OnDemandTextureVideoSource _source;
        private LocalVideoTrack _track;
        private float _nextCaptureTime;
        private bool _awaitingSend;

        /// <summary>
        /// The texture the vision camera renders into, holding the latest captured frame; null when no connector is assigned.
        /// </summary>
        public Texture Frame => _texture;

        /// <summary>
        /// Raised on the main thread each time a frame has been handed to LiveKit.
        /// </summary>
        public event Action FrameSent;

        /// <summary>
        /// Prepares the camera to render only on demand into its own texture, and listens for connections; disables the component with an error when no connector is assigned.
        /// </summary>
        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _camera.enabled = false;
            _camera.stereoTargetEye = StereoTargetEyeMask.None;

            if (!Connector)
            {
                Debug.LogError($"VisionPublisher on '{name}' needs a LiveKitConnector assigned. The backend will not see the scene.", this);
                enabled = false;
                return;
            }

            _texture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = TrackName };
            _camera.targetTexture = _texture;

            Connector.Connected += OnConnected;
        }

        /// <summary>
        /// Hands the previous readback to LiveKit once it is ready, then renders and reads back the next frame when it is due.
        /// </summary>
        private void Update()
        {
            if (_source == null)
                return;

            if (_awaitingSend)
            {
                if (_source.SendPendingFrame())
                {
                    _awaitingSend = false;
                    FrameSent?.Invoke();
                }
                else if (!_source.ReadbackInFlight)
                {
                    _awaitingSend = false;
                }
            }

            if (_awaitingSend || Time.unscaledTime < _nextCaptureTime)
                return;

            _nextCaptureTime = Time.unscaledTime + 1f / FramesPerSecond;
            Render();
            _source.RequestReadback();
            _awaitingSend = true;
        }

        private void OnDestroy()
        {
            if (Connector)
                Connector.Connected -= OnConnected;
            TearDown();
            if (_texture)
            {
                _texture.Release();
                Destroy(_texture);
            }
        }

        /// <summary>
        /// Starts publishing once the connector reports a successful connection, releasing anything left from an earlier room.
        /// </summary>
        private void OnConnected(Room room)
        {
            TearDown();
            _room = room;
            _room.Disconnected += OnDisconnected;
            StartCoroutine(Publish(room));
        }

        /// <summary>
        /// Releases the track when the room disconnects, so the next connection publishes a fresh one.
        /// </summary>
        private void OnDisconnected(Room room)
        {
            TearDown();
        }

        /// <summary>
        /// Creates the video source over the camera's texture and publishes it as the room's camera track.
        /// </summary>
        private IEnumerator Publish(Room room)
        {
            OnDemandTextureVideoSource source = new(_texture);
            LocalVideoTrack track = LocalVideoTrack.CreateVideoTrack(TrackName, source, room);

            TrackPublishOptions options = new()
            {
                VideoEncoding = new VideoEncoding { MaxBitrate = (ulong)MaxBitrate, MaxFramerate = FramesPerSecond },
                Simulcast = false,
                Source = TrackSource.SourceCamera
            };
            PublishTrackInstruction publish = room.LocalParticipant.PublishTrack(track, options);
            yield return publish;

            if (publish.IsError || _room != room)
            {
                if (publish.IsError)
                    Debug.LogError("Failed to publish vision camera track.");
                source.Dispose();
                yield break;
            }

            _source = source;
            _track = track;
            _awaitingSend = false;
            _nextCaptureTime = Time.unscaledTime;

            Debug.Log($"Vision camera published ({Width}x{Height} at {FramesPerSecond} fps)");
        }

        /// <summary>
        /// Renders the vision camera once into its texture, through the render pipeline's request API where it supports one.
        /// </summary>
        private void Render()
        {
            RenderPipeline.StandardRequest request = new() { destination = _texture };
            if (RenderPipeline.SupportsRenderRequest(_camera, request))
                RenderPipeline.SubmitRenderRequest(_camera, request);
            else
                _camera.Render();
        }

        /// <summary>
        /// Drops the room reference and disposes the video source, first waiting for a readback in flight so its buffer is not freed under it.
        /// </summary>
        private void TearDown()
        {
            if (_room != null)
            {
                _room.Disconnected -= OnDisconnected;
                _room = null;
            }

            if (_source != null && _source.ReadbackInFlight)
                AsyncGPUReadback.WaitAllRequests();
            _source?.Dispose();
            _source = null;
            _track = null;
            _awaitingSend = false;
        }

        /// <summary>
        /// Texture video source whose readback and send are triggered by the publisher instead of running every frame.
        /// </summary>
        private class OnDemandTextureVideoSource : TextureVideoSource
        {
            /// <summary>
            /// Wraps the texture whose contents are sent as frames.
            /// </summary>
            public OnDemandTextureVideoSource(Texture texture) : base(texture)
            {
            }

            /// <summary>
            /// Whether a readback was started and has neither failed nor been sent yet.
            /// </summary>
            public bool ReadbackInFlight => _reading;

            /// <summary>
            /// Starts an asynchronous GPU readback of the texture; does nothing while an earlier readback has not been sent.
            /// </summary>
            public void RequestReadback()
            {
                ReadBuffer();
            }

            /// <summary>
            /// Hands the completed readback to LiveKit and returns true, or returns false while the readback is still in flight.
            /// </summary>
            public bool SendPendingFrame()
            {
                return SendFrame();
            }
        }
    }
}
