using System;
using System.Collections;
using System.Threading;
using LiveKit;
using LiveKit.Proto;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TrIVA.Connection
{
    /// <summary>
    /// Captures the local microphone and publishes it to the connected LiveKit room, either always on or gated by an
    /// optional push-to-talk input action, and can be muted manually. Captures from the system default device unless another device is selected, or publishes an audio source in its place when told to.
    /// </summary>
    [RequireComponent(typeof(LiveKitConnector))]
    public class LiveKitMicrophonePublisher : MonoBehaviour
    {
        private const string TrackName = "microphone";
        private const int MaxBitrate = 64000;
        private const string DevicePrefsKey = "Connection.MicrophoneDevice";

        [Header("Push-to-talk")]
        [SerializeField] private bool PushToTalk;
        [SerializeField] private InputActionReference PushToTalkAction;

        [Header("Audio Processing")]
        [SerializeField] private bool EchoCancellation = true;
        [SerializeField] private bool NoiseSuppression = true;
        [SerializeField] private bool AutoGainControl = true;

        private Room _room;
        private GameObject _sourceObject;
        private RtcAudioSource _source;
        private AudioSource _voiceSource;
        private RtcAudioSource _pendingSource;
        private GameObject _pendingSourceObject;
        private LocalAudioTrack _track;
        private bool _trackMuted;
        private bool _publishing;
        private float _inputLevel;

        /// <summary>
        /// The selected capture device name, or null for the system default device.
        /// </summary>
        public string Device { get; private set; }

        /// <summary>
        /// Whether the microphone is muted manually, overriding push-to-talk; kept across republishes.
        /// </summary>
        public bool Muted { get; private set; }

        /// <summary>
        /// RMS level (0 to 1) of the latest raw buffer of the published microphone or audio source, before audio processing; 0 while no source is live.
        /// </summary>
        public float InputLevel => _source != null ? Volatile.Read(ref _inputLevel) : 0f;

        private void Awake()
        {
            string savedDevice = PlayerPrefs.GetString(DevicePrefsKey, null);
            if (IsAvailable(savedDevice))
                Device = savedDevice;

            GetComponent<LiveKitConnector>().Connected += OnConnected;

            if (PushToTalk && PushToTalkAction != null)
                PushToTalkAction.action.Enable();
        }

        private void Update()
        {
            ApplyMute();
        }

        private void OnDestroy()
        {
            TearDown();
        }

        /// <summary>
        /// Selects the capture device (null for the system default), remembers it across sessions and republishes the track if one is live.
        /// </summary>
        public void SetDevice(string device)
        {
            if (device == Device)
                return;

            Device = device;
            if (device == null)
                PlayerPrefs.DeleteKey(DevicePrefsKey);
            else
                PlayerPrefs.SetString(DevicePrefsKey, device);
            PlayerPrefs.Save();

            if (_room != null && !_publishing && !_voiceSource)
                StartCoroutine(PublishSelectedDevice(_room));
        }

        /// <summary>
        /// Publishes the audio source as the participant's voice instead of the microphone, such as a simulated participant's, or the microphone again for null; republishes the track if one is live. The source must keep a clip playing, silence between utterances, or the track carries no audio.
        /// </summary>
        public void PublishFrom(AudioSource source)
        {
            if (ReferenceEquals(source, _voiceSource))
                return;

            _voiceSource = source;
            if (_room != null && !_publishing && gameObject.activeInHierarchy)
                StartCoroutine(PublishSelectedDevice(_room));
        }

        /// <summary>
        /// Mutes or unmutes the microphone manually; while muted, push-to-talk has no effect.
        /// </summary>
        public void SetMuted(bool muted)
        {
            Muted = muted;
            ApplyMute();
        }

        /// <summary>
        /// Starts the publish flow once the connector reports a successful connection.
        /// </summary>
        private void OnConnected(Room room)
        {
            _room = room;
            _room.Disconnected += OnDisconnected;
            StartCoroutine(PublishSelectedDevice(room));
        }

        /// <summary>
        /// Releases the microphone when the room disconnects, so the next connection publishes a fresh track.
        /// </summary>
        private void OnDisconnected(Room room)
        {
            TearDown();
        }

        /// <summary>
        /// Publishes the audio source told to, or else the selected device, replacing any live track, and repeats while either selection changed during publishing.
        /// </summary>
        private IEnumerator PublishSelectedDevice(Room room)
        {
            _publishing = true;
            string device;
            AudioSource voiceSource;
            do
            {
                device = Device;
                voiceSource = _voiceSource;
                if (_track != null)
                    yield return Unpublish(room);
                if (_room != room)
                    yield break;
                yield return Publish(room, device, voiceSource);
            } while (_room == room && (!ReferenceEquals(voiceSource, _voiceSource) || (!voiceSource && device != Device)));

            if (_room == room)
                _publishing = false;
        }

        /// <summary>
        /// Unpublishes the live microphone track and releases its source.
        /// </summary>
        private IEnumerator Unpublish(Room room)
        {
            UnpublishTrackInstruction unpublish = room.LocalParticipant.UnpublishTrack(_track, true);
            ReleaseSource();
            yield return unpublish;

            if (unpublish.IsError)
                Debug.LogError("Failed to unpublish microphone track.");
        }

        /// <summary>
        /// Publishes the audio source if there is one, otherwise requests microphone permission and creates the processed microphone source for the device (null for system default), as the room's microphone track.
        /// </summary>
        private IEnumerator Publish(Room room, string device, AudioSource voiceSource)
        {
            if (PushToTalk && !PushToTalkAction)
                Debug.LogError("Push-to-talk is enabled but no input action is assigned, microphone will stay muted.");

            RtcAudioSource source;
            GameObject sourceObject = null;
            string deviceLabel;
            if (voiceSource)
            {
                source = new BasicAudioSource(voiceSource, RtcAudioSourceType.AudioSourceMicrophone);
                deviceLabel = $"audio source {voiceSource.name}";
            }
            else
            {
                yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
                if (_room != room)
                    yield break;
                if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
                {
                    Debug.LogError("Microphone permission denied, not publishing microphone.");
                    yield break;
                }

                if (Microphone.devices.Length == 0)
                {
                    Debug.LogError("No microphone device found, not publishing microphone.");
                    yield break;
                }

                if (!IsAvailable(device))
                {
                    Debug.LogWarning($"Microphone '{device}' not found, using system default.");
                    device = null;
                }

                deviceLabel = device ?? "system default";
                sourceObject = new($"Microphone: {deviceLabel}");
                sourceObject.transform.SetParent(transform, false);

                AudioProcessingOptions processing = new()
                {
                    EchoCancellation = EchoCancellation,
                    NoiseSuppression = NoiseSuppression,
                    AutoGainControl = AutoGainControl
                };
                source = new MicrophoneSource(device, sourceObject, processing);
            }
            _pendingSource = source;
            _pendingSourceObject = sourceObject;
            LocalAudioTrack track = LocalAudioTrack.CreateAudioTrack(TrackName, source, room);

            TrackPublishOptions options = new()
            {
                AudioEncoding = new AudioEncoding { MaxBitrate = MaxBitrate },
                Source = TrackSource.SourceMicrophone
            };
            PublishTrackInstruction publish = room.LocalParticipant.PublishTrack(track, options);
            yield return publish;

            if (_pendingSource != source)
                yield break;
            _pendingSource = null;
            _pendingSourceObject = null;

            if (publish.IsError || _room != room)
            {
                Debug.LogError("Failed to publish microphone track.");
                source.Dispose();
                if (sourceObject)
                    Destroy(sourceObject);
                yield break;
            }

            _sourceObject = sourceObject;
            _source = source;
            _track = track;
            _source.AudioRead += OnAudioRead;
            _source.Start();
            _trackMuted = false;
            ApplyMute();

            Debug.Log($"Microphone '{deviceLabel}' published (push-to-talk: {PushToTalk}, audio processing: {_source.AudioProcessingEnabled})");
        }

        /// <summary>
        /// Mutes the published track while muted manually or while push-to-talk is not held, which also signals the
        /// mute state to the backend. Push-to-talk without an assigned action stays muted.
        /// </summary>
        private void ApplyMute()
        {
            if (_track == null)
                return;

            bool muted = Muted || (PushToTalk && (!PushToTalkAction || !PushToTalkAction.action.IsPressed()));
            if (muted == _trackMuted)
                return;

            ((ILocalTrack)_track).SetMute(muted);
            _trackMuted = muted;
        }

        /// <summary>
        /// Stores the RMS of each captured buffer as the input level; runs on the audio thread.
        /// </summary>
        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            if (data.Length == 0)
                return;

            float sum = 0f;
            for (int i = 0; i < data.Length; i++)
                sum += data[i] * data[i];

            Volatile.Write(ref _inputLevel, Mathf.Sqrt(sum / data.Length));
        }

        /// <summary>
        /// Returns whether the device is null (system default) or currently listed by Unity.
        /// </summary>
        private static bool IsAvailable(string device)
        {
            return device == null || Array.IndexOf(Microphone.devices, device) >= 0;
        }

        /// <summary>
        /// Drops the room reference and releases the microphone source, also one whose publish is still under way.
        /// </summary>
        private void TearDown()
        {
            if (_room != null)
            {
                _room.Disconnected -= OnDisconnected;
                _room = null;
            }

            _publishing = false;
            ReleasePendingSource();
            ReleaseSource();
        }

        /// <summary>
        /// Disposes the source of a publish still under way and destroys its host object.
        /// </summary>
        private void ReleasePendingSource()
        {
            if (_pendingSource != null)
            {
                _pendingSource.Dispose();
                _pendingSource = null;
            }

            if (_pendingSourceObject)
            {
                Destroy(_pendingSourceObject);
                _pendingSourceObject = null;
            }
        }

        /// <summary>
        /// Stops and disposes the microphone source, destroys its host object and drops the track reference.
        /// </summary>
        private void ReleaseSource()
        {
            if (_source != null)
            {
                _source.AudioRead -= OnAudioRead;
                try
                {
                    _source.Stop();
                    _source.Dispose();
                }
                catch (MissingReferenceException)
                {
                    // The published audio source was destroyed first; its finalizer releases the native source.
                }
                _source = null;
            }

            if (_sourceObject)
            {
                Destroy(_sourceObject);
                _sourceObject = null;
            }

            _track = null;
        }
    }
}
