using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrIVA.Connection;
using UnityEngine;
using UnityEngine.UIElements;

namespace TrIVA.Debugging
{
    /// <summary>
    /// Drives the debug panel's Inputs tab: one card per LLM call from the agent's input records, listing each input with where it was processed and its data as key: value fields, and the inputs the call missed. The newest calls sit in fixed slots, newest on top (the next call's draft, then the current call, then the ones before it), and older calls in a scrollable history on a sub-tab of their own; toggles hide drafts and individual input kinds.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class InputsDebugPanel : MonoBehaviour
    {
        [SerializeField] private LiveKitRtviClient _rtviClient;
        [Tooltip("Calls kept in all, in the fixed slots and the history together.")]
        [SerializeField] private int _maxRecords = 50;

        private const int SlotCount = 5;
        private const string ServerMessageType = "server-message";
        private const string InputsMessageType = "inputs";
        private const string DraftState = "draft";
        private const string GameKind = "game";
        private const string SlotsName = "inputs-slots";
        private const string HistoryScrollViewName = "inputs-history-scroll-view";
        private const string ClearButtonName = "inputs-clear-button";
        private const string DraftsToggleName = "inputs-drafts-toggle";
        private const string KindToggleNameFormat = "inputs-{0}-toggle";

        private const string SlotClass = "dbg-input-slot";
        private const string CardClass = "dbg-input-card";
        private const string CardStateClassPrefix = "dbg-input-card--";
        private const string HeaderClass = "dbg-input-card__header";
        private const string TimeClass = "dbg-input-card__time";
        private const string IdClass = "dbg-input-card__id";
        private const string StateClass = "dbg-input-card__state";
        private const string CallClass = "dbg-input-card__call";
        private const string RowClass = "dbg-input-row";
        private const string DroppedRowClass = "dbg-input-row--dropped";
        private const string KindClass = "dbg-input-row__kind";
        private const string WhereClass = "dbg-input-row__where";
        private const string DataClass = "dbg-input-row__data";
        private const string EmptyClass = "dbg-input-card__empty";
        private const string FieldSeparator = " · ";
        private const string NoValue = "—";
        private const string TimeFormat = "HH:mm:ss";

        /// <summary>
        /// The input kinds of the wire contract, each with a toggle in the tab's toolbar.
        /// </summary>
        private static readonly string[] Kinds = { "transcript", "emotion", "held", "pointing", "game" };

        private readonly Dictionary<string, InputRecord> _records = new();
        private readonly List<InputRecord> _order = new();
        private readonly Dictionary<string, Toggle> _kindToggles = new();
        private readonly List<ScrollView> _slots = new();
        private readonly string[] _slotIds = new string[SlotCount];
        private UIDocument _document;
        private ScrollView _history;
        private Button _clearButton;
        private Toggle _draftsToggle;

        /// <summary>
        /// The latest known state of one LLM call's input record.
        /// </summary>
        private class InputRecord
        {
            public string Id;
            public string Time;
            public string State;
            public string Trigger;
            public string Llm;
            public JArray Inputs;
            public JArray Dropped;
        }

        private void Awake()
        {
            _document = GetComponent<UIDocument>();
        }

        private void OnEnable()
        {
            _rtviClient.MessageReceived += OnMessageReceived;
            BindElements(_document.rootVisualElement);
        }

        private void OnDisable()
        {
            _rtviClient.MessageReceived -= OnMessageReceived;
            if (_clearButton != null)
                _clearButton.clicked -= OnClearClicked;
            _draftsToggle?.UnregisterValueChangedCallback(OnFilterChanged);
            foreach (Toggle toggle in _kindToggles.Values)
                toggle.UnregisterValueChangedCallback(OnFilterChanged);
            _kindToggles.Clear();
            _slots.Clear();
            _history = null;
        }

        /// <summary>
        /// Queries the Inputs tab's elements from the document's visual tree, builds the fixed slots, wires the clear button and filter toggles, and renders the records that arrived before the UI was ready.
        /// </summary>
        private void BindElements(VisualElement rootElement)
        {
            VisualElement slotsContainer = rootElement.Q(SlotsName);
            slotsContainer.Clear();
            for (int i = 0; i < SlotCount; i++)
            {
                ScrollView slot = new ScrollView(ScrollViewMode.Vertical);
                slot.AddToClassList(SlotClass);
                slotsContainer.Add(slot);
                _slots.Add(slot);
            }

            _history = rootElement.Q<ScrollView>(HistoryScrollViewName);
            _clearButton = rootElement.Q<Button>(ClearButtonName);
            _draftsToggle = rootElement.Q<Toggle>(DraftsToggleName);
            _clearButton.clicked += OnClearClicked;
            _draftsToggle.RegisterValueChangedCallback(OnFilterChanged);
            foreach (string kind in Kinds)
            {
                Toggle toggle = rootElement.Q<Toggle>(string.Format(KindToggleNameFormat, kind));
                toggle.RegisterValueChangedCallback(OnFilterChanged);
                _kindToggles.Add(kind, toggle);
            }
            RenderAll();
        }

        /// <summary>
        /// Drops every buffered record and empties the slots and the history; outcomes for cleared calls that arrive later are ignored.
        /// </summary>
        private void OnClearClicked()
        {
            _records.Clear();
            _order.Clear();
            RenderAll();
        }

        /// <summary>
        /// Re-renders every card when a filter toggle changes.
        /// </summary>
        private void OnFilterChanged(ChangeEvent<bool> _)
        {
            RenderAll();
        }

        /// <summary>
        /// Merges an inputs server message into the record it names, creating it from a draft or sent message and dropping the oldest beyond the cap; other messages are ignored.
        /// </summary>
        private void OnMessageReceived(string type, JObject data)
        {
            if (type != ServerMessageType || data == null || (string)data["t"] != InputsMessageType)
                return;
            if (data["d"] is not JObject message)
                return;

            string id = (string)message["id"];
            if (id == null)
                return;

            if (!_records.TryGetValue(id, out InputRecord record))
            {
                if (!message.ContainsKey("inputs"))
                    return;

                record = new InputRecord { Id = id, Time = DateTime.Now.ToString(TimeFormat) };
                _records.Add(id, record);
                _order.Add(record);
                if (_order.Count > _maxRecords)
                {
                    _records.Remove(_order[0].Id);
                    _order.RemoveAt(0);
                }
            }

            record.State = (string)message["state"] ?? record.State;
            if (message.ContainsKey("trigger"))
                record.Trigger = (string)message["trigger"];
            if (message.ContainsKey("llm"))
                record.Llm = (string)message["llm"];
            if (message["inputs"] is JArray inputs)
                record.Inputs = inputs;
            if (message["dropped"] is JArray dropped)
                record.Dropped = dropped;

            RenderAll();
        }

        /// <summary>
        /// Places every record, newest first: the newest record's draft in the top slot, the calls after it in the remaining slots, and the rest in the history; does nothing before the UI is bound.
        /// </summary>
        private void RenderAll()
        {
            if (_history == null)
                return;

            string[] slotIds = new string[SlotCount];
            _history.Clear();

            int nextSlot = 1;
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                InputRecord record = _order[i];
                bool draft = record.State == DraftState;
                if (draft && !_draftsToggle.value)
                    continue;

                if (draft && i == _order.Count - 1)
                    slotIds[0] = record.Id;
                else if (nextSlot < SlotCount)
                    slotIds[nextSlot++] = record.Id;
                else
                    _history.Add(NewCard(record));
            }

            for (int i = 0; i < SlotCount; i++)
                FillSlot(i, slotIds[i]);
        }

        /// <summary>
        /// Shows a record's card in a slot, or empties the slot for a null ID; the slot scrolls back to the top when it gets a different record.
        /// </summary>
        private void FillSlot(int index, string id)
        {
            ScrollView slot = _slots[index];
            slot.Clear();
            if (_slotIds[index] != id)
                slot.scrollOffset = Vector2.zero;
            _slotIds[index] = id;
            if (id != null)
                slot.Add(NewCard(_records[id]));
        }

        /// <summary>
        /// Builds a record's card: a header with time, call ID, state, trigger and where the LLM ran, then one row per input whose kind is not toggled off.
        /// </summary>
        private VisualElement NewCard(InputRecord record)
        {
            VisualElement card = new VisualElement();
            card.AddToClassList(CardClass);
            if (record.State != null)
                card.AddToClassList(CardStateClassPrefix + record.State);

            VisualElement header = new VisualElement();
            header.AddToClassList(HeaderClass);
            header.Add(NewLabel(record.Time, TimeClass));
            header.Add(NewLabel($"Call {record.Id}", IdClass));
            header.Add(NewLabel(record.State ?? "?", StateClass));
            if (record.Trigger != null || record.Llm != null)
                header.Add(NewLabel($"{Words(record.Trigger)}{FieldSeparator}{Words(record.Llm)} LLM", CallClass));
            card.Add(header);

            AddInputRows(card, record.Inputs, false);
            AddInputRows(card, record.Dropped, true);
            bool hasEntries = (record.Inputs != null && record.Inputs.HasValues) || (record.Dropped != null && record.Dropped.HasValues);
            if (!hasEntries)
                card.Add(NewLabel("no inputs", EmptyClass));
            return card;
        }

        /// <summary>
        /// Adds a row for each input entry whose kind is not toggled off.
        /// </summary>
        private void AddInputRows(VisualElement card, JArray entries, bool dropped)
        {
            if (entries == null)
                return;

            foreach (JToken token in entries)
            {
                if (token is not JObject entry)
                    continue;

                string kind = (string)entry["input"];
                if (kind != null && _kindToggles.TryGetValue(kind, out Toggle toggle) && !toggle.value)
                    continue;

                card.Add(NewInputRow(kind, dropped ? "missed" : (string)entry["where"], DataText(kind, entry["data"]), dropped ? DroppedRowClass : null));
            }
        }

        /// <summary>
        /// Builds one input line: its kind, where it was processed (or that it was missed) and its data.
        /// </summary>
        private static VisualElement NewInputRow(string kind, string where, string data, string modifierClass)
        {
            VisualElement row = new VisualElement();
            row.AddToClassList(RowClass);
            if (modifierClass != null)
                row.AddToClassList(modifierClass);

            row.Add(NewLabel(Capitalized(kind ?? "?"), KindClass));
            row.Add(NewLabel(where ?? "?", WhereClass));
            row.Add(NewLabel(data, DataClass));
            return row;
        }

        /// <summary>
        /// Renders input data as "key: value" fields, nested keys dotted; a game snapshot shows only its top-level plain values, since its nested parts are scenario-specific and long.
        /// </summary>
        private static string DataText(string kind, JToken data)
        {
            List<string> fields = new();
            AppendFields(fields, data, "", kind != GameKind);
            return fields.Count == 0 ? NoValue : string.Join(FieldSeparator, fields);
        }

        /// <summary>
        /// Adds one field per plain value below the token; with deep off, values nested below the top level are left out.
        /// </summary>
        private static void AppendFields(List<string> fields, JToken token, string path, bool deep)
        {
            if (token is JContainer container && container.HasValues)
            {
                if (!deep && path.Length > 0)
                    return;

                if (token is JObject obj)
                {
                    foreach (JProperty property in obj.Properties())
                        AppendFields(fields, property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", deep);
                }
                else if (token is JArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                        AppendFields(fields, array[i], $"{path}[{i}]", deep);
                }
                return;
            }

            string value = ValueText(token);
            fields.Add(path.Length == 0 ? value : $"{path}: {value}");
        }

        /// <summary>
        /// Shows a missing, null or empty value as a dash, strings without quotes, and numbers and booleans as written.
        /// </summary>
        private static string ValueText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token is JContainer)
                return NoValue;
            if (token.Type == JTokenType.String)
            {
                string text = (string)token;
                return text.Length == 0 ? NoValue : text;
            }
            return token.ToString(Formatting.None);
        }

        /// <summary>
        /// Turns a wire value such as user_turn into words; null shows as a question mark.
        /// </summary>
        private static string Words(string value)
        {
            return value == null ? "?" : value.Replace('_', ' ');
        }

        /// <summary>
        /// Starts a kind name with a capital letter for its row label.
        /// </summary>
        private static string Capitalized(string text)
        {
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        /// <summary>
        /// Builds a plain-text label carrying the given USS class.
        /// </summary>
        private static Label NewLabel(string text, string className)
        {
            Label label = new Label(text) { enableRichText = false };
            label.AddToClassList(className);
            return label;
        }
    }
}
