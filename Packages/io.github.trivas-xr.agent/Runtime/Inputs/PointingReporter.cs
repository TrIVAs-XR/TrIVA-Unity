using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TrIVA.Connection;
using TrIVA.Objects;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

namespace TrIVA.Inputs
{
    /// <summary>
    /// Tells the backend what the participant points at with each hand: sends a pointing client message with the description of the object each far ray has rested on, whenever either hand's target changes or a pointed object changes.
    /// </summary>
    public class PointingReporter : MonoBehaviour
    {
        private const string PointingType = "pointing";

        [SerializeField, Tooltip("RTVI client the messages are sent through.")]
        private LiveKitRtviClient RtviClient;
        [SerializeField, Tooltip("Near-far interactors whose far ray counts as pointing, such as each hand's and each controller's; each needs its handedness set to left or right.")]
        private List<NearFarInteractor> Interactors = new();
        [SerializeField, Min(0f), Tooltip("Seconds a ray has to rest on an object before it counts as pointed at, so sweeping over objects reports nothing.")]
        private float DwellSeconds = 1f;
        [SerializeField, Min(0f), Tooltip("Seconds a ray may slip off its object before that counts as leaving it, so jitter at an object's edge does not reset the dwell or end the pointing.")]
        private float GraceSeconds = 0.3f;
        [SerializeField, Tooltip("Layers searched along the ray for referable objects that are no XRI interactables, such as stations; trigger colliders count, and a solid collider without a ReferableObject blocks the ray. The search follows the XRI ray only up to its own first hit, so keep these layers in line with the interactor's raycast mask.")]
        private LayerMask RaycastLayers = Physics.DefaultRaycastLayers;

        private static readonly RaycastHit[] RaycastHits = new RaycastHit[64];

        private readonly Hand _left = new();
        private readonly Hand _right = new();
        private readonly List<ReferableObject> _watched = new();
        private bool _reportedAny;

        /// <summary>
        /// What one hand points at: nothing, an interactable without a ReferableObject, or a referable object.
        /// </summary>
        private readonly struct Target
        {
            public readonly bool Pointing;
            public readonly ReferableObject Referable;

            /// <summary>
            /// Creates a target; a pointing target without a referable is an object that cannot be referred to.
            /// </summary>
            public Target(bool pointing, ReferableObject referable)
            {
                Pointing = pointing;
                Referable = referable;
            }

            /// <summary>
            /// Whether the target was a referable object that has since been destroyed.
            /// </summary>
            public bool Gone => !ReferenceEquals(Referable, null) && !Referable;

            /// <summary>
            /// Whether both name the same target; a destroyed referable is still told apart from an object that never had one.
            /// </summary>
            public bool Same(Target other)
            {
                return Pointing == other.Pointing && ReferenceEquals(Referable, other.Referable);
            }

            /// <summary>
            /// Describes the target as the backend receives it: null for nothing, unknown for an object that cannot be referred to.
            /// </summary>
            public JToken Describe()
            {
                if (!Pointing)
                    return JValue.CreateNull();
                if (!Referable)
                    return new JObject { ["id"] = null, ["kind"] = "unknown" };
                return Referable.Describe();
            }
        }

        /// <summary>
        /// Turns one hand's per-frame ray hits into a settled target: a new object counts once the ray rested on it for the dwell, and the settled object counts as left once the ray was off it for longer than the grace; slips to nothing shorter than the grace are ignored for both.
        /// </summary>
        private class Hand
        {
            private Target _candidate;
            private float _candidateSince;
            private float _candidateLastSeen;
            private float _reportedLastSeen;

            /// <summary>
            /// The settled target last reported for this hand.
            /// </summary>
            public Target Reported { get; private set; }

            /// <summary>
            /// Takes this frame's ray hit and returns whether the settled target changed.
            /// </summary>
            public bool Update(Target hit, float now, float dwell, float grace)
            {
                if (_candidate.Gone)
                    _candidate = default;
                if (Reported.Gone)
                {
                    Reported = default;
                    return true;
                }
                if (hit.Same(Reported))
                    _reportedLastSeen = now;
                if (hit.Same(_candidate))
                {
                    _candidateLastSeen = now;
                }
                else if (hit.Pointing || !_candidate.Pointing || _candidate.Same(Reported) || now - _candidateLastSeen > grace)
                {
                    _candidate = hit;
                    _candidateSince = now;
                    _candidateLastSeen = now;
                }
                if (_candidate.Pointing && !_candidate.Same(Reported) && hit.Same(_candidate) && now - _candidateSince >= dwell)
                {
                    Reported = _candidate;
                    _reportedLastSeen = now;
                    return true;
                }
                if (Reported.Pointing && now - _reportedLastSeen > grace)
                {
                    Reported = default;
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Forgets the candidate and the settled target, as if the hand had pointed at nothing all along.
            /// </summary>
            public void Clear()
            {
                _candidate = default;
                Reported = default;
            }
        }

        /// <summary>
        /// Checks the references, disabling the component with an error when the client is missing, and starts from both hands pointing at nothing.
        /// </summary>
        private void OnEnable()
        {
            if (!RtviClient)
            {
                Debug.LogError($"PointingReporter on '{name}' needs a LiveKitRtviClient assigned. The backend will not be told what the participant points at.", this);
                enabled = false;
                return;
            }
            foreach (NearFarInteractor interactor in Interactors)
            {
                if (interactor && interactor.handedness == InteractorHandedness.None)
                    Debug.LogWarning($"PointingReporter: interactor '{interactor.name}' has no handedness, so what it points at is not reported.", this);
            }
        }

        /// <summary>
        /// Tells the backend both hands point at nothing when they were reported pointing, and forgets both hands.
        /// </summary>
        private void OnDisable()
        {
            _left.Clear();
            _right.Clear();
            Watch();
            if (_reportedAny && RtviClient)
                Report();
            _reportedAny = false;
        }

        /// <summary>
        /// Feeds both hands this frame's ray hits and sends the targets when either settled target changed.
        /// </summary>
        private void LateUpdate()
        {
            float now = Time.unscaledTime;
            bool leftChanged = _left.Update(FindHit(InteractorHandedness.Left), now, DwellSeconds, GraceSeconds);
            bool rightChanged = _right.Update(FindHit(InteractorHandedness.Right), now, DwellSeconds, GraceSeconds);
            if (!leftChanged && !rightChanged)
                return;
            Watch();
            Report();
        }

        /// <summary>
        /// Reports the targets again when a pointed object changed, such as a pointed flask's substance.
        /// </summary>
        private void OnPointedObjectChanged(ReferableObject changed)
        {
            Report();
        }

        /// <summary>
        /// Sends both hands' settled targets.
        /// </summary>
        private void Report()
        {
            JObject hands = new JObject
            {
                ["left"] = _left.Reported.Describe(),
                ["right"] = _right.Reported.Describe()
            };
            _reportedAny = _left.Reported.Pointing || _right.Reported.Pointing;
            RtviClient.SendClientMessage(PointingType, hands);
        }

        /// <summary>
        /// Listens for changes on exactly the referable objects the hands currently point at.
        /// </summary>
        private void Watch()
        {
            foreach (ReferableObject watched in _watched)
            {
                if (watched)
                    watched.Changed -= OnPointedObjectChanged;
            }
            _watched.Clear();
            WatchOne(_left.Reported.Referable);
            WatchOne(_right.Reported.Referable);
        }

        /// <summary>
        /// Listens for changes on one pointed object, unless it is missing or already watched.
        /// </summary>
        private void WatchOne(ReferableObject referable)
        {
            if (!referable || _watched.Contains(referable))
                return;
            referable.Changed += OnPointedObjectChanged;
            _watched.Add(referable);
        }

        /// <summary>
        /// Finds what the hand's far ray hits this frame: an interactable the ray validly hits, else a referable object along the ray that is no interactable, such as a station; a hand that holds something does not point.
        /// </summary>
        private Target FindHit(InteractorHandedness hand)
        {
            foreach (NearFarInteractor interactor in Interactors)
            {
                if (!interactor || interactor.handedness != hand || !interactor.isActiveAndEnabled || interactor.hasSelection)
                    continue;
                ICurveInteractionDataProvider curve = interactor;
                if (!curve.isActive)
                    continue;
                EndPointType end = interactor.TryGetCurveEndPoint(out Vector3 _);
                if (end == EndPointType.ValidCastHit)
                {
                    Transform hit = ((IXRRayProvider)interactor).rayEndTransform;
                    if (hit)
                        return new Target(true, hit.GetComponentInParent<ReferableObject>());
                }
                else if (end != EndPointType.UI)
                {
                    ReferableObject referable = FindReferableAlong(curve.samplePoints);
                    if (referable)
                        return new Target(true, referable);
                }
            }
            return default;
        }

        /// <summary>
        /// Casts along the ray's curve segments and returns the nearest referable object it hits, triggers included, unless a solid collider without one is in front of it.
        /// </summary>
        private ReferableObject FindReferableAlong(NativeArray<Vector3> points)
        {
            for (int i = 1; i < points.Length; i++)
            {
                Vector3 from = points[i - 1];
                Vector3 segment = points[i] - from;
                float length = segment.magnitude;
                if (length <= 0f)
                    continue;
                int count = Physics.RaycastNonAlloc(from, segment / length, RaycastHits, length, RaycastLayers, QueryTriggerInteraction.Collide);
                ReferableObject nearest = null;
                float nearestDistance = float.PositiveInfinity;
                float blockedAt = float.PositiveInfinity;
                for (int h = 0; h < count; h++)
                {
                    RaycastHit hit = RaycastHits[h];
                    ReferableObject referable = hit.collider.GetComponentInParent<ReferableObject>();
                    if (referable && hit.distance < nearestDistance)
                    {
                        nearest = referable;
                        nearestDistance = hit.distance;
                    }
                    else if (!referable && !hit.collider.isTrigger && hit.distance < blockedAt)
                    {
                        blockedAt = hit.distance;
                    }
                }
                if (nearestDistance < blockedAt)
                    return nearest;
                if (blockedAt < float.PositiveInfinity)
                    return null;
            }
            return null;
        }
    }
}
