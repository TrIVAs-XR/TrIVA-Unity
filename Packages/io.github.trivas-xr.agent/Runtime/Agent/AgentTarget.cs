using System.Collections.Generic;
using UnityEngine;
using TrIVA.Utils;

namespace TrIVA.Agent
{
    /// <summary>
    /// Marks an object in the scene the agent can refer to, with the points it looks and points at.
    /// </summary>
    public class AgentTarget : MonoBehaviour
    {
        [SerializeField, Tooltip("Name the object is referred to by, unique in the scene.")] private string Id;
        [SerializeField, Tooltip("Point the agent looks at; this object when empty.")] private Transform LookAnchor;
        [SerializeField, Tooltip("Point the agent points at; this object when empty.")] private Transform PointAnchor;

        private static readonly List<AgentTarget> ActiveTargets = new();

        /// <summary>
        /// Every enabled target in the loaded scenes.
        /// </summary>
        public static IReadOnlyList<AgentTarget> All => ActiveTargets;

        /// <summary>
        /// Name the object is referred to by.
        /// </summary>
        public string TargetId => Id;

        /// <summary>
        /// Point the agent looks at.
        /// </summary>
        public Transform LookPoint => LookAnchor ? LookAnchor : transform;

        /// <summary>
        /// Point the agent points at.
        /// </summary>
        public Transform PointPoint => PointAnchor ? PointAnchor : transform;

        private void OnEnable() => ActiveTargets.Add(this);

        private void OnDisable() => ActiveTargets.Remove(this);

        /// <summary>
        /// Inspector button that makes the agent in the scene refer to this object.
        /// </summary>
        [Button("Agent Refers Here", PlayModeOnly = true)]
        private void DebugRefer(bool look = true, bool point = true, float duration = 3f)
        {
            ReferenceController agent = FindAnyObjectByType<ReferenceController>();
            if (!agent)
            {
                Debug.LogWarning("No ReferenceController in the scene to refer to this target.", this);
                return;
            }
            agent.Refer(this, look, point, duration);
        }
    }
}
