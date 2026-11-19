using System.Collections.Generic;
using System.Text;
using TrIVA.Connection;
using LiveKit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace TrIVA.Debugging
{
    /// <summary>
    /// Drives the debug panel's LiveKit tab: connection, agent-speaking and audio-track status chips, a microphone
    /// selector with input level meter and mute toggle, a preview of the vision camera's latest frame, a manual disconnect control, a timestamped transcript, and raw inbound and outbound RTVI feeds.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class LiveKitDebugPanel : MonoBehaviour
    {
        [SerializeField] private LiveKitConnector _connector;
        [SerializeField] private LiveKitRtviClient _rtviClient;
        [SerializeField] private LiveKitMicrophonePublisher _microphonePublisher;
        [Tooltip("Optional; without it the vision camera section is hidden.")]
        [SerializeField] private VisionPublisher _visionPublisher;
        [SerializeField] private int _maxTranscriptLines = 100;
        [SerializeField] private int _maxFeedLines = 100;
        [Tooltip("Payload keys left out of the RTVI feed, matched by name at any depth.")]
        [SerializeField] private List<string> _hiddenFeedKeys = new() { "segment_id", "aggregated_by" };

        private const string ConnectionChipName = "connection-chip";
        private const string ConnectionStatusLabelName = "connection-status-label";
        private const string DisconnectButtonName = "disconnect-button";
        private const string MicrophoneDropdownName = "microphone-dropdown";
        private const string SystemDefaultLabel = "System default";
        private const string MicLevelBarName = "mic-level-bar";
        private const string MicLevelLabelName = "mic-level-label";
        private const string MuteButtonName = "mute-button";
        private const string VisionSectionName = "vision-section";
        private const string VisionPreviewName = "vision-preview";
        private const float MicLevelMinDb = -60f;
        private const float MicLevelHotDb = -6f;
        private const float MicLevelFallPerSecond = 1.5f;
        private const string BotSpeakingChipName = "bot-speaking-chip";
        private const string BotSpeakingLabelName = "bot-speaking-label";
        private const string AudioTrackChipName = "audio-track-chip";
        private const string AudioTrackLabelName = "audio-track-label";
        private const string LastMessageLabelName = "last-message-label";
        private const string TranscriptScrollViewName = "transcript-scroll-view";
        private const string TranscriptClearButtonName = "transcript-clear-button";
        private const string FeedScrollViewName = "message-feed-scroll-view";
        private const string FeedPauseButtonName = "feed-pause-button";
        private const string FeedClearButtonName = "feed-clear-button";
        private const string OutboundScrollViewName = "outbound-scroll-view";
        private const string OutboundPauseButtonName = "outbound-pause-button";
        private const string OutboundClearButtonName = "outbound-clear-button";

        private const string ChipOkClass = "dbg-chip--ok";
        private const string ChipErrorClass = "dbg-chip--error";
        private const string MeterHotClass = "dbg-meter--hot";
        private const string UserEntryClass = "dbg-entry--user";
        private const string AgentEntryClass = "dbg-entry--agent";
        private const string FeedEntryClass = "dbg-entry--feed";
        private const string DroppedEntryClass = "dbg-entry--dropped";
        private const string FeedFieldSeparator = " · ";
        private const int NoMicLevel = int.MinValue;

        private VisualElement _connectionChip;
        private Label _connectionStatusLabel;
        private Button _disconnectButton;
        private DropdownField _microphoneDropdown;
        private string _systemDefaultChoice = SystemDefaultLabel;
        private ProgressBar _micLevelBar;
        private Label _micLevelLabel;
        private float _displayedMicLevel;
        private int _shownMicDb = int.MaxValue;
        private Button _muteButton;
        private Image _visionPreview;
        private VisualElement _botSpeakingChip;
        private Label _botSpeakingLabel;
        private VisualElement _audioTrackChip;
        private Label _audioTrackLabel;
        private Label _lastMessageLabel;
        private Button _transcriptClearButton;
        private Button _feedPauseButton;
        private Button _feedClearButton;
        private Button _outboundPauseButton;
        private Button _outboundClearButton;

        private bool _botSpeaking;
        private bool _audioTrackSubscribed;
        private string _lastMessageType;

        private CappedLogList _transcript;
        private CappedLogList _feed;
        private CappedLogList _outbound;
        private UIDocument _document;
        private Room _room;

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
            _transcript = new CappedLogList(_maxTranscriptLines);
            _feed = new CappedLogList(_maxFeedLines);
            _outbound = new CappedLogList(_maxFeedLines);
        }

        private void OnEnable()
        {
            _connector.Connected += OnConnected;
            _connector.Disconnected += OnConnectorDisconnected;
            _rtviClient.MessageReceived += OnMessageReceived;
            _rtviClient.MessageSent += OnMessageSent;
            _rtviClient.MessageDropped += OnMessageDropped;
            if (_visionPublisher)
                _visionPublisher.FrameSent += OnVisionFrameSent;

            BindElements(_document.rootVisualElement);
        }

        private void OnDisable()
        {
            _connector.Connected -= OnConnected;
            _connector.Disconnected -= OnConnectorDisconnected;
            _rtviClient.MessageReceived -= OnMessageReceived;
            _rtviClient.MessageSent -= OnMessageSent;
            _rtviClient.MessageDropped -= OnMessageDropped;
            if (_visionPublisher)
                _visionPublisher.FrameSent -= OnVisionFrameSent;

            if (_disconnectButton != null)
                _disconnectButton.clicked -= OnDisconnectClicked;
            if (_microphoneDropdown != null)
            {
                _microphoneDropdown.UnregisterValueChangedCallback(OnMicrophoneChanged);
                _microphoneDropdown.UnregisterCallback<PointerDownEvent>(OnMicrophoneDropdownPointerDown, TrickleDown.TrickleDown);
            }
            if (_muteButton != null)
                _muteButton.clicked -= OnMuteClicked;
            if (_transcriptClearButton != null)
                _transcriptClearButton.clicked -= OnTranscriptClearClicked;
            if (_feedPauseButton != null)
                _feedPauseButton.clicked -= OnFeedPauseClicked;
            if (_feedClearButton != null)
                _feedClearButton.clicked -= OnFeedClearClicked;
            if (_outboundPauseButton != null)
                _outboundPauseButton.clicked -= OnOutboundPauseClicked;
            if (_outboundClearButton != null)
                _outboundClearButton.clicked -= OnOutboundClearClicked;
            _transcript.Unbind();
            _feed.Unbind();
            _outbound.Unbind();

            UnsubscribeFromRoom();
        }

        /// <summary>
        /// Maps the publisher's microphone RMS onto a dB scale and drives the level meter, rising instantly and falling at a fixed rate; the dB readout turns the meter hot near clipping.
        /// </summary>
        private void Update()
        {
            if (_micLevelBar == null)
                return;

            float rms = _microphonePublisher.InputLevel;
            float db = rms > 0f ? 20f * Mathf.Log10(rms) : MicLevelMinDb;
            float target = Mathf.Clamp01((db - MicLevelMinDb) / -MicLevelMinDb);

            _displayedMicLevel = target > _displayedMicLevel
                ? target
                : Mathf.MoveTowards(_displayedMicLevel, target, MicLevelFallPerSecond * Time.deltaTime);
            _micLevelBar.value = _displayedMicLevel;

            int shownDb = _displayedMicLevel > 0f
                ? Mathf.RoundToInt(MicLevelMinDb - _displayedMicLevel * MicLevelMinDb)
                : NoMicLevel;
            if (shownDb == _shownMicDb)
                return;

            _shownMicDb = shownDb;
            _micLevelLabel.text = shownDb == NoMicLevel ? "—" : $"{shownDb} dB";
            _micLevelBar.EnableInClassList(MeterHotClass, shownDb != NoMicLevel && shownDb > MicLevelHotDb);
        }

        /// <summary>
        /// Queries the LiveKit tab's elements from the document's visual tree, wires their callbacks and shows the current state.
        /// </summary>
        private void BindElements(VisualElement rootElement)
        {
            _connectionChip = rootElement.Q(ConnectionChipName);
            _connectionStatusLabel = rootElement.Q<Label>(ConnectionStatusLabelName);
            _disconnectButton = rootElement.Q<Button>(DisconnectButtonName);
            _microphoneDropdown = rootElement.Q<DropdownField>(MicrophoneDropdownName);
            _micLevelBar = rootElement.Q<ProgressBar>(MicLevelBarName);
            _micLevelLabel = rootElement.Q<Label>(MicLevelLabelName);
            _muteButton = rootElement.Q<Button>(MuteButtonName);
            _visionPreview = rootElement.Q<Image>(VisionPreviewName);
            _botSpeakingChip = rootElement.Q(BotSpeakingChipName);
            _botSpeakingLabel = rootElement.Q<Label>(BotSpeakingLabelName);
            _audioTrackChip = rootElement.Q(AudioTrackChipName);
            _audioTrackLabel = rootElement.Q<Label>(AudioTrackLabelName);
            _lastMessageLabel = rootElement.Q<Label>(LastMessageLabelName);
            _transcriptClearButton = rootElement.Q<Button>(TranscriptClearButtonName);
            _feedPauseButton = rootElement.Q<Button>(FeedPauseButtonName);
            _feedClearButton = rootElement.Q<Button>(FeedClearButtonName);
            _outboundPauseButton = rootElement.Q<Button>(OutboundPauseButtonName);
            _outboundClearButton = rootElement.Q<Button>(OutboundClearButtonName);

            _disconnectButton.clicked += OnDisconnectClicked;
            RefreshMicrophoneChoices();
            _microphoneDropdown.RegisterValueChangedCallback(OnMicrophoneChanged);
            _microphoneDropdown.RegisterCallback<PointerDownEvent>(OnMicrophoneDropdownPointerDown, TrickleDown.TrickleDown);
            _muteButton.clicked += OnMuteClicked;
            _muteButton.text = _microphonePublisher.Muted ? "Unmute" : "Mute";
            rootElement.Q(VisionSectionName).style.display = _visionPublisher ? DisplayStyle.Flex : DisplayStyle.None;
            _visionPreview.image = _visionPublisher ? _visionPublisher.Frame : null;
            _transcriptClearButton.clicked += OnTranscriptClearClicked;
            _feedPauseButton.clicked += OnFeedPauseClicked;
            _feedClearButton.clicked += OnFeedClearClicked;
            _feedPauseButton.text = _feed.Paused ? "Resume" : "Pause";
            _outboundPauseButton.clicked += OnOutboundPauseClicked;
            _outboundClearButton.clicked += OnOutboundClearClicked;
            _outboundPauseButton.text = _outbound.Paused ? "Resume" : "Pause";

            _shownMicDb = int.MaxValue;
            _transcript.Bind(rootElement.Q<ScrollView>(TranscriptScrollViewName));
            _feed.Bind(rootElement.Q<ScrollView>(FeedScrollViewName));
            _outbound.Bind(rootElement.Q<ScrollView>(OutboundScrollViewName));
            RefreshState();
        }

        private void OnDisconnectClicked()
        {
            _connector.Disconnect();
        }

        private void OnTranscriptClearClicked()
        {
            _transcript.Clear();
        }

        private void OnFeedPauseClicked()
        {
            _feed.Paused = !_feed.Paused;
            _feedPauseButton.text = _feed.Paused ? "Resume" : "Pause";
        }

        private void OnFeedClearClicked()
        {
            _feed.Clear();
        }

        private void OnOutboundPauseClicked()
        {
            _outbound.Paused = !_outbound.Paused;
            _outboundPauseButton.text = _outbound.Paused ? "Resume" : "Pause";
        }

        private void OnOutboundClearClicked()
        {
            _outbound.Clear();
        }

        /// <summary>
        /// Refreshes the device list right before the dropdown opens, so devices plugged in after startup appear.
        /// </summary>
        private void OnMicrophoneDropdownPointerDown(PointerDownEvent evt)
        {
            RefreshMicrophoneChoices();
        }

        /// <summary>
        /// Fills the dropdown with the system default entry, naming the resolved device when known, plus every device Unity lists, and shows the publisher's current selection.
        /// </summary>
        private void RefreshMicrophoneChoices()
        {
            string defaultName = WindowsDefaultMicrophone.GetName();
            _systemDefaultChoice = defaultName != null ? $"{SystemDefaultLabel} ({defaultName})" : SystemDefaultLabel;

            List<string> choices = new() { _systemDefaultChoice };
            choices.AddRange(Microphone.devices);
            _microphoneDropdown.choices = choices;
            _microphoneDropdown.SetValueWithoutNotify(_microphonePublisher.Device ?? _systemDefaultChoice);
        }

        private void OnMicrophoneChanged(ChangeEvent<string> evt)
        {
            _microphonePublisher.SetDevice(evt.newValue == _systemDefaultChoice ? null : evt.newValue);
        }

        private void OnMuteClicked()
        {
            _microphonePublisher.SetMuted(!_microphonePublisher.Muted);
            _muteButton.text = _microphonePublisher.Muted ? "Unmute" : "Mute";
        }

        /// <summary>
        /// Points the preview at the publisher's frame and repaints it, since the panel does not notice a render texture changing.
        /// </summary>
        private void OnVisionFrameSent()
        {
            if (_visionPreview == null)
                return;

            _visionPreview.image = _visionPublisher.Frame;
            _visionPreview.MarkDirtyRepaint();
        }

        /// <summary>
        /// Reflects the new connection and starts tracking the room's audio track subscription state.
        /// </summary>
        private void OnConnected(Room room)
        {
            _room = room;
            _room.TrackSubscribed += OnTrackSubscribed;
            _room.TrackUnsubscribed += OnTrackUnsubscribed;
            RefreshState();
        }

        private void OnConnectorDisconnected()
        {
            _botSpeaking = false;
            _audioTrackSubscribed = false;
            UnsubscribeFromRoom();
            RefreshState();
        }

        private void UnsubscribeFromRoom()
        {
            if (_room == null)
                return;

            _room.TrackSubscribed -= OnTrackSubscribed;
            _room.TrackUnsubscribed -= OnTrackUnsubscribed;
            _room = null;
        }

        private void OnTrackSubscribed(IRemoteTrack track, RemoteTrackPublication publication, RemoteParticipant participant)
        {
            if (track is not RemoteAudioTrack)
                return;

            _audioTrackSubscribed = true;
            RefreshState();
        }

        private void OnTrackUnsubscribed(IRemoteTrack track, RemoteTrackPublication publication, RemoteParticipant participant)
        {
            if (track is not RemoteAudioTrack)
                return;

            _audioTrackSubscribed = false;
            RefreshState();
        }

        /// <summary>
        /// Logs every inbound RTVI message to the raw feed, and extracts speaking state and transcript lines
        /// from the message types that carry them. Lines are buffered even before the UI exists.
        /// </summary>
        private void OnMessageReceived(string type, JObject data)
        {
            _lastMessageType = type;
            _feed.Add(type, FormatFeedPayload(data), FeedEntryClass);

            switch (type)
            {
                case "bot-started-speaking":
                    _botSpeaking = true;
                    break;
                case "bot-stopped-speaking":
                    _botSpeaking = false;
                    break;
                case "bot-output":
                    if (data != null && (string)data["spoken_status"] == "new")
                        AppendTranscript("Agent", (string)data["text"], AgentEntryClass);
                    break;
                case "user-transcription":
                    if (data != null && (bool?)data["final"] == true)
                        AppendTranscript("You", (string)data["text"], UserEntryClass);
                    break;
            }

            RefreshState();
        }

        /// <summary>
        /// Logs every outbound RTVI message handed to the data channel to the outbound feed.
        /// </summary>
        private void OnMessageSent(string type, JObject data)
        {
            _outbound.Add(type, FormatFeedPayload(data), FeedEntryClass);
        }

        /// <summary>
        /// Logs an outbound RTVI message that was dropped because no transport was open, marked as dropped in the outbound feed.
        /// </summary>
        private void OnMessageDropped(string type, JObject data)
        {
            string payload = FormatFeedPayload(data);
            _outbound.Add(type, payload.Length == 0 ? "dropped" : $"dropped{FeedFieldSeparator}{payload}", DroppedEntryClass);
        }

        private void AppendTranscript(string speaker, string text, string entryClass)
        {
            if (string.IsNullOrEmpty(text))
                return;

            _transcript.Add(speaker, text, entryClass);
        }

        /// <summary>
        /// Renders an RTVI payload as one wrapping line of unquoted "key: value" fields, nested keys dotted, hidden keys left out.
        /// </summary>
        private string FormatFeedPayload(JObject data)
        {
            if (data == null)
                return "";

            StringBuilder builder = new StringBuilder();
            AppendFeedFields(builder, data, "");
            return builder.ToString();
        }

        private void AppendFeedFields(StringBuilder builder, JToken token, string path)
        {
            if (token is JObject obj && obj.HasValues)
            {
                foreach (JProperty property in obj.Properties())
                {
                    if (!_hiddenFeedKeys.Contains(property.Name))
                        AppendFeedFields(builder, property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}");
                }
                return;
            }

            if (token is JArray array && array.HasValues)
            {
                for (int i = 0; i < array.Count; i++)
                    AppendFeedFields(builder, array[i], $"{path}[{i}]");
                return;
            }

            if (path.Length == 0)
                return;

            if (builder.Length > 0)
                builder.Append(FeedFieldSeparator);
            builder.Append(path).Append(": ").Append(FeedValueText(token));
        }

        /// <summary>
        /// Shows strings without quotes (an empty string as ""), and everything else as compact JSON.
        /// </summary>
        private static string FeedValueText(JToken token)
        {
            if (token.Type != JTokenType.String)
                return token.ToString(Formatting.None);

            string text = (string)token;
            return text.Length == 0 ? "\"\"" : text;
        }

        /// <summary>
        /// Writes the tracked connection, speaking, audio-track and last-message state onto the status chips.
        /// </summary>
        private void RefreshState()
        {
            if (_connectionChip == null)
                return;

            bool connected = _connector.IsConnected;
            SetChip(_connectionChip, _connectionStatusLabel, connected ? "Connected" : "Disconnected", connected ? ChipOkClass : ChipErrorClass);
            SetChip(_botSpeakingChip, _botSpeakingLabel, _botSpeaking ? "Agent speaking" : "Agent silent", _botSpeaking ? ChipOkClass : null);
            SetChip(_audioTrackChip, _audioTrackLabel, _audioTrackSubscribed ? "Audio track subscribed" : "No audio track", _audioTrackSubscribed ? ChipOkClass : null);
            _lastMessageLabel.text = $"last: {_lastMessageType ?? "none"}";
            _disconnectButton.SetEnabled(connected);
        }

        private static void SetChip(VisualElement chip, Label label, string text, string stateClass)
        {
            label.text = text;
            chip.EnableInClassList(ChipOkClass, stateClass == ChipOkClass);
            chip.EnableInClassList(ChipErrorClass, stateClass == ChipErrorClass);
        }
    }
}
