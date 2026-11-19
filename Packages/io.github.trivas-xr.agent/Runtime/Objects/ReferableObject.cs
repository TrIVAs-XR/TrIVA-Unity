using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TrIVA.Agent;
using TrIVA.Utils;
using UnityEngine;

namespace TrIVA.Objects
{
    /// <summary>
    /// Marks an object the agent and the backend can refer to: gives it an ID that is unique among the loaded objects, a kind, the points the agent looks and points at, and a description of what it currently is.
    /// </summary>
    public class ReferableObject : MonoBehaviour
    {
        [SerializeField, Tooltip("ID the object is referred to by, unique among the loaded objects; empty generates one from the kind when the object appears, as spawned objects need.")] private string Id;
        [SerializeField, Tooltip("What sort of object this is, such as flask or station.")] private string Kind = "object";
        [SerializeField, Tooltip("Point the agent looks at; this object when empty.")] private Transform LookAnchor;
        [SerializeField, Tooltip("Point the agent points at; this object when empty.")] private Transform PointAnchor;

        private static readonly List<ReferableObject> ActiveObjects = new();
        private static readonly Dictionary<string, ReferableObject> ObjectsById = new();
        private static readonly Dictionary<string, int> LastNumberByKind = new();

        private string _id;
        private IObjectState[] _states;

        /// <summary>
        /// Every enabled object in the loaded scenes.
        /// </summary>
        public static IReadOnlyList<ReferableObject> All => ActiveObjects;

        /// <summary>
        /// Raised when what the object's description adds beyond its ID and kind has changed, such as the substance a flask holds.
        /// </summary>
        public event Action<ReferableObject> Changed;

        /// <summary>
        /// ID the object is referred to by: the authored one, or the one generated when it appeared.
        /// </summary>
        public string ObjectId => _id;

        /// <summary>
        /// What sort of object this is.
        /// </summary>
        public string ObjectKind => Kind;

        /// <summary>
        /// Point the agent looks at.
        /// </summary>
        public Transform LookPoint => LookAnchor ? LookAnchor : transform;

        /// <summary>
        /// Point the agent points at.
        /// </summary>
        public Transform PointPoint => PointAnchor ? PointAnchor : transform;

        /// <summary>
        /// Takes the authored ID or generates one, and listens to the state components next to it.
        /// </summary>
        private void Awake()
        {
            _id = string.IsNullOrEmpty(Id) ? NextId(Kind) : Id;
            _states = GetComponents<IObjectState>();
            foreach (IObjectState state in _states)
                state.StateChanged += OnStateChanged;
        }

        /// <summary>
        /// Registers the object so its ID can be looked up; an ID another enabled object already holds is reported and not registered.
        /// </summary>
        private void OnEnable()
        {
            ActiveObjects.Add(this);
            if (ObjectsById.TryGetValue(_id, out ReferableObject other) && other != this)
            {
                Debug.LogError($"ReferableObject '{name}' has the ID '{_id}', which '{other.name}' already holds; it cannot be looked up by ID.", this);
                return;
            }
            ObjectsById[_id] = this;
        }

        private void OnDisable()
        {
            ActiveObjects.Remove(this);
            if (ObjectsById.TryGetValue(_id, out ReferableObject registered) && registered == this)
                ObjectsById.Remove(_id);
        }

        private void OnDestroy()
        {
            if (_states == null)
                return;
            foreach (IObjectState state in _states)
                state.StateChanged -= OnStateChanged;
        }

        /// <summary>
        /// Finds the enabled object with the given ID, as the backend names it.
        /// </summary>
        public static bool TryFind(string id, out ReferableObject found)
        {
            found = null;
            return id != null && ObjectsById.TryGetValue(id, out found);
        }

        /// <summary>
        /// Describes the object as it is now: its ID and kind, plus whatever the state components next to it add.
        /// </summary>
        public JObject Describe()
        {
            JObject description = new JObject
            {
                ["id"] = _id,
                ["kind"] = Kind
            };
            foreach (IObjectState state in _states)
                state.Describe(description);
            return description;
        }

        /// <summary>
        /// Describes an object that has no ReferableObject, so the backend at least learns its name: no ID, kind unknown, and the GameObject's name without the clone suffix.
        /// </summary>
        public static JObject DescribeUnreferable(GameObject source)
        {
            return new JObject
            {
                ["id"] = null,
                ["kind"] = "unknown",
                ["name"] = source.name.Replace("(Clone)", "").Trim()
            };
        }

        /// <summary>
        /// Generates the next free ID for a kind, such as flask-3.
        /// </summary>
        private static string NextId(string kind)
        {
            LastNumberByKind.TryGetValue(kind, out int number);
            string id;
            do
            {
                number++;
                id = $"{kind}-{number}";
            } while (ObjectsById.ContainsKey(id));
            LastNumberByKind[kind] = number;
            return id;
        }

        /// <summary>
        /// Passes a change of a state component on to listeners of the object.
        /// </summary>
        private void OnStateChanged()
        {
            Changed?.Invoke(this);
        }

        /// <summary>
        /// Inspector button that makes the agent in the scene refer to this object.
        /// </summary>
        [Button("Agent Refers Here", PlayModeOnly = true)]
        private void DebugRefer(bool look = true, bool point = true, float duration = 3f)
        {
            ReferenceController agent = FindAnyObjectByType<ReferenceController>();
            if (!agent)
            {
                Debug.LogWarning("No ReferenceController in the scene to refer to this object.", this);
                return;
            }
            agent.Refer(this, look, point, duration);
        }
    }
}
