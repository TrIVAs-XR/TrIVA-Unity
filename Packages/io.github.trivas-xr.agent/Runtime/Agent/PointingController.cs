using System;
using UnityEngine;
using TrIVA.Utils;

namespace TrIVA.Agent
{
    /// <summary>
    /// Points at an object by playing a directional pointing clip on an animator layer, optionally holding its peak, and turning torso and arm the rest of the way to the target.
    /// </summary>
    [DefaultExecutionOrder(90)]
    [RequireComponent(typeof(Animator))]
    public class PointingController : MonoBehaviour
    {
        /// <summary>
        /// Bones of one arm, the two joints whose line counts as the pointing direction, and whether the clips play mirrored for it.
        /// </summary>
        private class Arm
        {
            public Transform UpperArm;
            public Transform LowerArm;
            public Transform Hand;
            public Transform MiddleBase;
            public Transform PointerBase;
            public Transform PointerTip;
            public bool Mirrored;
        }

        [Header("Animator")]
        [SerializeField] private string LayerName = "Pointing";
        [SerializeField] private string StateName = "Point";
        [SerializeField, Tooltip("Empty state of the layer the gesture returns to.")] private string RestStateName = "Idle";
        [SerializeField, Tooltip("Seconds a stopped or replaced gesture takes to blend back to rest instead of playing out, or into the next gesture of the same arm.")] private float CancelBlendTime = 0.4f;
        [SerializeField, Range(0, 1), Tooltip("Normalized time of the pointing state a new gesture of the same arm starts at when it replaces one that is already lowering, so the arm moves over to the new target instead of dropping first. Keep it below Hold At.")] private float SwitchFrom = 0.25f;
        [SerializeField] private string TriggerParameter = "Point";
        [SerializeField, Tooltip("Float parameter of the blend tree that picks the clips by horizontal direction, in degrees with positive to the agent's right for the unmirrored arm.")] private string DirectionParameter = "PointYaw";
        [SerializeField] private string MirrorParameter = "PointMirror";
        [SerializeField] private string SpeedParameter = "PointSpeed";
        [SerializeField, Tooltip("Float parameter that picks the gesture style: 0 points with the index finger, 1 gestures with an open hand.")] private string StyleParameter = "PointStyle";
        [SerializeField, Range(0, 1), Tooltip("Chance that a gesture uses the open-hand style instead of pointing.")] private float OpenHandChance = 0.5f;
        [SerializeField, Tooltip("Freezes the gesture at its peak for the duration PointAt is given; when off, the clip always plays through at its own pace.")] private bool HoldAtPeak;
        [SerializeField, Range(0, 1), Tooltip("Normalized time of the pointing state at which every clip is at its peak, where Hold At Peak freezes it.")] private float HoldAt = 0.4f;
        [SerializeField, Tooltip("Smallest and largest direction in degrees the blend tree covers.")] private Vector2 DirectionRange = new(-55f, 65f);
        [SerializeField, Tooltip("Seconds the direction parameter takes to follow a moving target.")] private float DirectionDampTime = 0.15f;

        [Header("Arm Choice")]
        [SerializeField, Tooltip("Targets further to the agent's left than this angle in degrees are pointed at with the mirrored left arm.")] private float LeftArmBelowYaw = -40f;
        [SerializeField, Range(0, 180), Tooltip("Largest horizontal angle in degrees between the agent's forward and a target it still points at.")] private float MaxPointAngle = 80f;

        [Header("Arm Correction")]
        [SerializeField, Tooltip("Arm elevation in degrees from horizontal at which the clip counts as resting (x) and as fully raised (y); correction and torso turn scale between them.")] private Vector2 RestToRaisedPitch = new(-65f, -25f);
        [SerializeField, Range(0, 90), Tooltip("Largest turn in degrees the upper arm gets on top of the clip to line the finger up with the target.")] private float MaxArmCorrection = 40f;
        [SerializeField, Range(0, 1), Tooltip("Share of the clip's wrist bend that is straightened while pointing, so the hand continues the forearm.")] private float WristStraighten = 0.8f;

        [Header("Torso")]
        [SerializeField, Range(0, 1), Tooltip("Share of the target's horizontal angle that spine and chest turn toward it.")] private float TorsoFollow = 0.35f;
        [SerializeField, Range(0, 45)] private float MaxTorsoYaw = 20f;
        [SerializeField, Range(0, 1), Tooltip("Share of the target's angle below the chest that the upper body leans forward.")] private float LeanFollow = 0.2f;
        [SerializeField, Range(0, 30)] private float MaxLean = 8f;

        [Header("Debug")]
        [SerializeField, Tooltip("Draws gizmos in Play mode while pointing: the index finger's line as far as the target (red) and the target point (yellow).")] private bool DrawPointingDebugRay;

        private Animator _animator;
        private int _layer = -1;
        private int _stateHash;
        private int _restStateHash;
        private Arm _left;
        private Arm _right;
        private BoneChain _torso;

        private Arm _arm;
        private Transform _target;
        private Vector3 _targetPoint;
        private float _holdUntil;
        private bool _active;
        private bool _entered;
        private bool _released;
        private bool _switching;
        private Transform _pendingTarget;
        private float _pendingDuration;

        /// <summary>
        /// Raised once per target when the agent is done with it: when its gesture passes the peak and the arm starts lowering, or when the gesture or the waiting target is dropped before that.
        /// </summary>
        public event Action<Transform> Released;

        /// <summary>
        /// Checks that the avatar and the animator controller have what pointing needs, disabling pointing with an error otherwise, and collects arm and torso bones.
        /// </summary>
        private void Awake()
        {
            _animator = GetComponent<Animator>();
            if (!_animator.avatar || !_animator.avatar.isHuman)
            {
                Debug.LogWarning("PointingController needs a Humanoid Avatar. Pointing disabled.", this);
                enabled = false;
                return;
            }

            _layer = _animator.GetLayerIndex(LayerName);
            _stateHash = Animator.StringToHash(StateName);
            _restStateHash = Animator.StringToHash(RestStateName);
            string missing = FindMissingSetup();
            if (missing != null)
            {
                Debug.LogError($"PointingController: the animator controller has no {missing}. Pointing disabled.", this);
                enabled = false;
                return;
            }

            _left = CreateArm(true);
            _right = CreateArm(false);
            _torso = new BoneChain(_animator, new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest }, new[] { 0.3f, 0.4f, 0.3f });
        }

        /// <summary>
        /// Follows the target, drives the running gesture, and starts a waiting target once the previous gesture has ended.
        /// </summary>
        private void Update()
        {
            if (_target)
                _targetPoint = _target.position;

            if (_active)
                TrackState();

            if (!_active && _pendingTarget)
            {
                Transform next = _pendingTarget;
                _pendingTarget = null;
                Begin(next, _pendingDuration);
            }
        }

        /// <summary>
        /// Turns torso and arm on top of the clip toward the target, as far as the clip has raised the arm.
        /// </summary>
        private void LateUpdate()
        {
            if (!_active || _arm == null)
                return;

            float weight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(RestToRaisedPitch.x, RestToRaisedPitch.y, Elevation(_arm)));
            if (weight <= 0f)
                return;

            TurnTorso(weight);
            StraightenWrist(weight);
            CorrectArm(weight);
        }

        /// <summary>
        /// Blends a running gesture back to rest and releases the running and the waiting target, so nothing waits for a gesture this component no longer drives.
        /// </summary>
        private void OnDisable()
        {
            if (_active)
            {
                Cancel();
                _active = false;
                Release();
            }
            DropPending();
        }

        private void OnDrawGizmos()
        {
            if (!DrawPointingDebugRay || !Application.isPlaying || !_active || _arm == null)
                return;

            Vector3 pointerBase = _arm.PointerBase.position;
            Vector3 direction = (_arm.PointerTip.position - pointerBase).normalized;
            Gizmos.color = Color.red;
            Gizmos.DrawRay(pointerBase, direction * Vector3.Distance(pointerBase, _targetPoint));
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(_targetPoint, 0.05f);
        }

        /// <summary>
        /// Points at the target; with Hold At Peak the gesture's peak lasts the given seconds, or until Stop when the duration is zero or less. Returns false and does not point when the target is too far to the side or behind.
        /// </summary>
        public bool PointAt(Transform target, float duration)
        {
            if (!target || !enabled)
                return false;

            float yaw = HorizontalAngle(target.position);
            if (Mathf.Abs(yaw) > MaxPointAngle)
                return false;

            if (_active && ChooseArm(yaw) == _arm)
            {
                Retarget(target, duration);
                return true;
            }

            if (_active)
            {
                Cancel();
                DropPending();
                _pendingTarget = target;
                _pendingDuration = duration;
                return true;
            }

            Begin(target, duration);
            return true;
        }

        /// <summary>
        /// Blends the running gesture back to rest and drops any target waiting for it to end.
        /// </summary>
        [Button("Stop Pointing", PlayModeOnly = true)]
        public void Stop()
        {
            if (_active)
                Cancel();
            DropPending();
        }

        /// <summary>
        /// Ends the running gesture early: blends the layer back to rest over Cancel Blend Time, or ends the gesture at once when the layer has not started it yet.
        /// </summary>
        private void Cancel()
        {
            _holdUntil = Time.time;
            _switching = false;
            _animator.ResetTrigger(TriggerParameter);
            _animator.SetFloat(SpeedParameter, 1f);

            if (!InPointState())
            {
                _active = false;
                Release();
                return;
            }

            if (!ReturningToRest())
                _animator.CrossFadeInFixedTime(_restStateHash, CancelBlendTime, _layer);
        }

        /// <summary>
        /// Moves the running gesture over to a new target of the same arm: a gesture still rising or holding just follows the new target, one already lowering blends into a new gesture started at Switch From with the same style.
        /// </summary>
        private void Retarget(Transform target, float duration)
        {
            if (target != _target && !_released)
                Released?.Invoke(_target);
            bool restart = _released || ReturningToRest();

            _target = target;
            _targetPoint = target.position;
            _holdUntil = HoldEnd(duration);
            _released = false;
            if (!restart)
                return;

            _switching = true;
            _animator.ResetTrigger(TriggerParameter);
            _animator.SetFloat(SpeedParameter, 1f);
            _animator.CrossFadeInFixedTime(_stateHash, CancelBlendTime, _layer, SwitchFrom);
        }

        /// <summary>
        /// Whether the layer is blending from the pointing state back to its empty rest state.
        /// </summary>
        private bool ReturningToRest()
        {
            return _animator.IsInTransition(_layer) && _animator.GetNextAnimatorStateInfo(_layer).shortNameHash == _restStateHash;
        }

        /// <summary>
        /// Whether the layer plays the pointing state or is blending into it.
        /// </summary>
        private bool InPointState()
        {
            return _animator.GetCurrentAnimatorStateInfo(_layer).shortNameHash == _stateHash || _animator.GetNextAnimatorStateInfo(_layer).shortNameHash == _stateHash;
        }

        /// <summary>
        /// Sets the arm, direction and a random style for the target and triggers the pointing state.
        /// </summary>
        private void Begin(Transform target, float duration)
        {
            _target = target;
            _targetPoint = target.position;
            _arm = ChooseArm(HorizontalAngle(_targetPoint));
            _holdUntil = HoldEnd(duration);
            _active = true;
            _entered = false;
            _released = false;
            _switching = false;

            _animator.SetBool(MirrorParameter, _arm.Mirrored);
            _animator.SetFloat(DirectionParameter, DirectionFor(_targetPoint));
            _animator.SetFloat(SpeedParameter, 1f);
            _animator.SetFloat(StyleParameter, UnityEngine.Random.value < OpenHandChance ? 1f : 0f);
            _animator.SetTrigger(TriggerParameter);
        }

        /// <summary>
        /// Follows the target with the direction parameter, freezes the newest pointing gesture at its peak while the hold lasts, releases the target once that peak is passed, and notices when the layer has left the pointing state.
        /// </summary>
        private void TrackState()
        {
            if (InPointState())
                _entered = true;
            else if (_entered)
            {
                _active = false;
                _switching = false;
                _animator.ResetTrigger(TriggerParameter);
                Release();
                return;
            }

            AnimatorStateInfo next = _animator.GetNextAnimatorStateInfo(_layer);
            bool blendingIn = _animator.IsInTransition(_layer) && next.shortNameHash == _stateHash;
            if (blendingIn)
                _switching = false;
            AnimatorStateInfo newest = blendingIn ? next : _animator.GetCurrentAnimatorStateInfo(_layer);

            _animator.SetFloat(DirectionParameter, DirectionFor(_targetPoint), DirectionDampTime, Time.deltaTime);
            bool atPeak = !_switching && newest.shortNameHash == _stateHash && newest.normalizedTime >= HoldAt;
            bool holding = HoldAtPeak && atPeak && Time.time < _holdUntil;
            _animator.SetFloat(SpeedParameter, holding ? 0f : 1f);

            if (atPeak && !holding)
                Release();
        }

        /// <summary>
        /// Raises Released for the running gesture's target unless that already happened.
        /// </summary>
        private void Release()
        {
            if (_released)
                return;
            _released = true;
            Released?.Invoke(_target);
        }

        /// <summary>
        /// Forgets the target waiting for the current gesture to end and raises Released for it.
        /// </summary>
        private void DropPending()
        {
            Transform dropped = _pendingTarget;
            _pendingTarget = null;
            if (dropped)
                Released?.Invoke(dropped);
        }

        /// <summary>
        /// Turns spine and chest toward the target and leans the upper body forward for targets below the shoulder.
        /// </summary>
        private void TurnTorso(float weight)
        {
            float yaw = Mathf.Clamp(HorizontalAngle(_targetPoint) * TorsoFollow, -MaxTorsoYaw, MaxTorsoYaw) * weight;
            Vector3 fromShoulder = (_targetPoint - _arm.UpperArm.position).normalized;
            float below = -Mathf.Asin(Mathf.Clamp(fromShoulder.y, -1f, 1f)) * Mathf.Rad2Deg;
            float lean = Mathf.Clamp(below * LeanFollow, 0f, MaxLean) * weight;
            _torso.Turn(transform.rotation, lean, yaw);
        }

        /// <summary>
        /// Turns the hand so its centre line continues the forearm, removing the clip's wrist bend toward the palm and to the side; skipped when the rig lacks the middle finger.
        /// </summary>
        private void StraightenWrist(float weight)
        {
            if (!_arm.MiddleBase)
                return;

            Vector3 wrist = _arm.Hand.position;
            Quaternion straighten = Quaternion.FromToRotation(_arm.MiddleBase.position - wrist, wrist - _arm.LowerArm.position);
            _arm.Hand.rotation = Quaternion.Slerp(Quaternion.identity, straighten, WristStraighten * weight) * _arm.Hand.rotation;
        }

        /// <summary>
        /// Turns the upper arm so the pointing line passes through the target, limited to a natural correction; repeats once because the turn also moves the line.
        /// </summary>
        private void CorrectArm(float weight)
        {
            Quaternion animated = _arm.UpperArm.rotation;
            for (int i = 0; i < 2; i++)
            {
                Vector3 pointerBase = _arm.PointerBase.position;
                Quaternion turn = Quaternion.FromToRotation(_arm.PointerTip.position - pointerBase, _targetPoint - pointerBase);
                _arm.UpperArm.rotation = turn * _arm.UpperArm.rotation;
            }

            Quaternion corrected = Quaternion.RotateTowards(animated, _arm.UpperArm.rotation, MaxArmCorrection);
            _arm.UpperArm.rotation = Quaternion.Slerp(animated, corrected, weight);
        }

        /// <summary>
        /// Right arm for targets ahead and to the right, mirrored left arm for targets further left.
        /// </summary>
        private Arm ChooseArm(float yaw) => yaw < LeftArmBelowYaw ? _left : _right;

        /// <summary>
        /// Blend tree direction for a point, flipped for the mirrored arm and clamped to the range the clips cover.
        /// </summary>
        private float DirectionFor(Vector3 point)
        {
            float yaw = HorizontalAngle(point);
            return Mathf.Clamp(_arm.Mirrored ? -yaw : yaw, DirectionRange.x, DirectionRange.y);
        }

        /// <summary>
        /// Time the hold ends for a duration, never for zero or less.
        /// </summary>
        private static float HoldEnd(float duration) => duration > 0f ? Time.time + duration : float.PositiveInfinity;

        /// <summary>
        /// Horizontal angle in degrees from the agent's forward to a point, positive to the agent's right.
        /// </summary>
        private float HorizontalAngle(Vector3 point)
        {
            Vector3 local = transform.InverseTransformPoint(point);
            return Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Angle in degrees of the line from shoulder to the pointer's tip above the horizontal, negative while the arm hangs.
        /// </summary>
        private static float Elevation(Arm arm)
        {
            Vector3 direction = (arm.PointerTip.position - arm.UpperArm.position).normalized;
            return Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Names the first layer, state or parameter pointing needs that the animator controller lacks, or returns null when nothing is missing.
        /// </summary>
        private string FindMissingSetup()
        {
            if (_layer < 0)
                return $"layer '{LayerName}'";
            foreach (string state in new[] { StateName, RestStateName })
            {
                if (!_animator.HasState(_layer, Animator.StringToHash(state)) && !_animator.HasState(_layer, Animator.StringToHash($"{LayerName}.{state}")))
                    return $"state '{state}' on layer '{LayerName}'";
            }

            string[] required = { TriggerParameter, DirectionParameter, MirrorParameter, SpeedParameter, StyleParameter };
            foreach (string parameter in required)
            {
                if (Array.FindIndex(_animator.parameters, existing => existing.name == parameter) < 0)
                    return $"parameter '{parameter}'";
            }
            return null;
        }

        /// <summary>
        /// Collects one arm's bones; the pointing line runs along the index finger, or from hand to middle finger, or along the forearm, whichever pair the rig has. The left arm plays the clips mirrored.
        /// </summary>
        private Arm CreateArm(bool left)
        {
            Transform lowerArm = _animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            Transform hand = _animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            Transform middleBase = _animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            Transform indexBase = _animator.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            Transform indexTip = _animator.GetBoneTransform(left ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal);
            (Transform pointerBase, Transform pointerTip) = indexBase && indexTip ? (indexBase, indexTip) : middleBase ? (hand, middleBase) : (lowerArm, hand);

            return new Arm
            {
                UpperArm = _animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm),
                LowerArm = lowerArm,
                Hand = hand,
                MiddleBase = middleBase,
                PointerBase = pointerBase,
                PointerTip = pointerTip,
                Mirrored = left
            };
        }
    }
}
