using System.Collections.Generic;
using TrIVA.Objects;
using TrIVA.Utils;
using UnityEngine;

namespace TrIVA.Agent
{
    /// <summary>
    /// Makes the agent refer to an object in the scene for a while by looking at it, pointing at it, or both.
    /// </summary>
    public class ReferenceController : MonoBehaviour
    {
        [SerializeField] private GazeController Gaze;
        [SerializeField] private PointingController Pointing;
        [SerializeField, Tooltip("Seconds the gaze stays on a pointed-at target after the arm passes its peak, picked at random between x and y each time.")] private Vector2 LookAfterPeak = new(0.2f, 0.6f);

        private bool _looking;
        private float _lookUntil;
        private Transform _awaitedRelease;
        private ReferableObject _lastDebugTarget;

        /// <summary>
        /// Checks the references, disabling the component with an error when one is missing, and listens for the arm releasing its target.
        /// </summary>
        private void OnEnable()
        {
            if (!Gaze || !Pointing)
            {
                Debug.LogError("ReferenceController needs a GazeController and a PointingController assigned.", this);
                enabled = false;
                return;
            }
            Pointing.Released += OnPointingReleased;
        }

        /// <summary>
        /// Stops listening and hands the gaze back, so no focus outlives the component.
        /// </summary>
        private void OnDisable()
        {
            if (Pointing)
                Pointing.Released -= OnPointingReleased;
            if (_looking)
                EndLook();
        }

        private void Update()
        {
            if (_looking && Time.time >= _lookUntil)
                EndLook();
        }

        /// <summary>
        /// Inspector button that refers to the given target, or to a random other one in the scene when none is given.
        /// </summary>
        [Button("Refer", PlayModeOnly = true)]
        private void DebugRefer(ReferableObject target = null, bool look = true, bool point = true, float duration = 3f)
        {
            if (!target)
            {
                List<ReferableObject> candidates = new(ReferableObject.All);
                if (candidates.Count > 1)
                    candidates.Remove(_lastDebugTarget);
                if (candidates.Count == 0)
                {
                    Debug.LogWarning("ReferenceController: no ReferableObject in the scene to refer to.", this);
                    return;
                }
                target = candidates[Random.Range(0, candidates.Count)];
            }

            _lastDebugTarget = target;
            Refer(target, look, point, duration);
        }

        /// <summary>
        /// Looks at and points at the target as asked; a part not asked for ends any earlier one. Looking alone lasts the given seconds, or until Stop when the duration is zero or less; looking while pointing ends shortly after the arm passes its peak.
        /// </summary>
        public void Refer(ReferableObject target, bool look = true, bool point = true, float duration = 3f)
        {
            if (!target || !enabled)
                return;

            bool pointing = point && Pointing.PointAt(target.PointPoint, duration);
            if (!pointing)
                Pointing.Stop();
            if (point && !pointing)
                Debug.Log($"ReferenceController: the agent does not point at '{target.name}', because pointing is disabled or the target is too far to the side.", this);

            if (!look)
            {
                EndLook();
                return;
            }

            Gaze.Focus(target.LookPoint);
            _looking = true;
            _awaitedRelease = pointing ? target.PointPoint : null;
            _lookUntil = pointing || duration <= 0f ? float.PositiveInfinity : Time.time + duration;
        }

        /// <summary>
        /// Ends looking and pointing, returning gaze and arm to the agent's state.
        /// </summary>
        [Button("Stop", PlayModeOnly = true)]
        public void Stop()
        {
            EndLook();
            Pointing.Stop();
        }

        /// <summary>
        /// Starts the countdown to look away once the arm releases the current reference's target, after its peak or because the gesture was dropped.
        /// </summary>
        private void OnPointingReleased(Transform target)
        {
            if (!_looking || !_awaitedRelease || target != _awaitedRelease)
                return;
            _awaitedRelease = null;
            _lookUntil = Time.time + Random.Range(LookAfterPeak.x, LookAfterPeak.y);
        }

        /// <summary>
        /// Hands the gaze back to the mode the agent's state sets.
        /// </summary>
        private void EndLook()
        {
            _looking = false;
            _awaitedRelease = null;
            Gaze.ReleaseFocus();
        }
    }
}
