using UnityEngine;

namespace TrIVA.Agent
{
    /// <summary>
    /// Keeps the agent's feet planted on the floor while the animation shifts its body, and takes a short procedural step when the animation keeps wanting a foot elsewhere.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class FootPlanter : MonoBehaviour
    {
        /// <summary>
        /// What a foot is doing: pinned to the floor, moving to a new spot, or following the animation while it lifts the foot.
        /// </summary>
        private enum FootPhase { Planted, Stepping, Free }

        /// <summary>
        /// Leg bones and planting state of one foot.
        /// </summary>
        private class Foot
        {
            public Transform UpperLeg;
            public Transform LowerLeg;
            public Transform Ankle;
            public FootPhase Phase;
            public Vector3 LockPosition;
            public Quaternion LockRotation;
            public Vector3 StepFromPosition;
            public Quaternion StepFromRotation;
            public float StepTime;
            public float StepLength;
            public Vector3 LastAnimatedPosition;
            public float RestHeight;
            public Vector3 AnimatedPosition;
            public Quaternion AnimatedRotation;
            public bool Lifted;
        }

        [SerializeField, Range(0, 1)] private float Weight = 1f;
        [SerializeField, Tooltip("Horizontal distance in metres between a planted foot and where the animation wants it before the foot steps over.")] private float StepThreshold = 0.06f;
        [SerializeField, Tooltip("Seconds one procedural step takes.")] private float StepDuration = 0.35f;
        [SerializeField, Tooltip("Peak lift of the foot during a procedural step, in metres.")] private float StepHeight = 0.04f;
        [SerializeField, Tooltip("Height in metres above its rest height at which the animation counts as lifting the foot, which hands the foot over to the animation.")] private float LiftThreshold = 0.04f;
        [SerializeField, Tooltip("Speed in metres per second below which a lifted foot that is back down gets planted again.")] private float PlantSpeed = 0.15f;
        [SerializeField, Tooltip("Distance in metres beyond which a planted foot snaps to the animation instead of stepping, e.g. after the agent was moved.")] private float ResetDistance = 0.5f;
        [SerializeField, Range(0, 1), Tooltip("How much the hips stay over the actual feet while a stance change waits for its steps, instead of moving ahead with the animation.")] private float BodyFollow = 1f;
        [SerializeField, Tooltip("Largest horizontal hips offset in metres that Body Follow applies.")] private float MaxBodyOffset = 0.15f;

        private Transform _hips;
        private Foot[] _feet;
        private bool _initialized;

        /// <summary>
        /// Finds the leg bones of the humanoid rig and disables the component when there are none.
        /// </summary>
        private void Awake()
        {
            Animator animator = GetComponent<Animator>();
            if (!animator.avatar || !animator.avatar.isHuman)
            {
                Debug.LogWarning("FootPlanter needs a Humanoid Avatar. Foot planting disabled.", this);
                enabled = false;
                return;
            }

            _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            _feet = new Foot[]
            {
                new Foot { UpperLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg), LowerLeg = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg), Ankle = animator.GetBoneTransform(HumanBodyBones.LeftFoot) },
                new Foot { UpperLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg), LowerLeg = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg), Ankle = animator.GetBoneTransform(HumanBodyBones.RightFoot) }
            };
        }

        /// <summary>
        /// Starts over from the animated foot positions whenever the component is enabled.
        /// </summary>
        private void OnEnable() => _initialized = false;

        /// <summary>
        /// Corrects the legs after the Animator has written this frame's pose.
        /// </summary>
        private void LateUpdate() => Plant(Time.deltaTime);

        /// <summary>
        /// Advances each foot's phase from the animated pose, keeps the hips over the actual feet and moves each leg so the foot ends up where its phase puts it.
        /// </summary>
        private void Plant(float deltaTime)
        {
            if (deltaTime <= 0f)
                return;

            if (!_initialized)
            {
                foreach (Foot foot in _feet)
                {
                    foot.Phase = FootPhase.Planted;
                    foot.LockPosition = foot.LastAnimatedPosition = foot.Ankle.position;
                    foot.LockRotation = foot.Ankle.rotation;
                    foot.RestHeight = foot.Ankle.position.y - transform.position.y;
                }
                _initialized = true;
            }

            foreach (Foot foot in _feet)
            {
                foot.AnimatedPosition = foot.Ankle.position;
                foot.AnimatedRotation = foot.Ankle.rotation;
                float speed = (foot.AnimatedPosition - foot.LastAnimatedPosition).magnitude / deltaTime;
                foot.LastAnimatedPosition = foot.AnimatedPosition;
                foot.Lifted = foot.AnimatedPosition.y - transform.position.y - foot.RestHeight > LiftThreshold;
                UpdatePhase(foot, speed, deltaTime);
            }

            if (_feet[0].Phase != FootPhase.Stepping && _feet[1].Phase != FootPhase.Stepping)
            {
                Foot stepper = ChooseStepper();
                if (stepper != null)
                    StartStep(stepper, StepDuration);
            }

            Vector3[] targetPositions = new Vector3[_feet.Length];
            Quaternion[] targetRotations = new Quaternion[_feet.Length];
            for (int i = 0; i < _feet.Length; i++)
            {
                Foot foot = _feet[i];
                Target(foot, out Vector3 targetPosition, out Quaternion targetRotation);
                targetPositions[i] = Vector3.Lerp(foot.AnimatedPosition, targetPosition, Weight);
                targetRotations[i] = Quaternion.Slerp(foot.AnimatedRotation, targetRotation, Weight);
            }

            Vector3 bodyOffset = ((targetPositions[0] + targetPositions[1]) - (_feet[0].AnimatedPosition + _feet[1].AnimatedPosition)) * 0.5f * BodyFollow;
            bodyOffset.y = 0f;
            _hips.position += Vector3.ClampMagnitude(bodyOffset, MaxBodyOffset);

            for (int i = 0; i < _feet.Length; i++)
            {
                SolveLeg(_feet[i], targetPositions[i]);
                _feet[i].Ankle.rotation = targetRotations[i];
            }
        }

        /// <summary>
        /// Where the foot should be this frame for its phase: its pinned spot, a point along its step arc, or where the animation puts it.
        /// </summary>
        private void Target(Foot foot, out Vector3 position, out Quaternion rotation)
        {
            switch (foot.Phase)
            {
                case FootPhase.Planted:
                    position = foot.LockPosition;
                    rotation = foot.LockRotation;
                    break;
                case FootPhase.Stepping:
                    float progress = Mathf.SmoothStep(0f, 1f, foot.StepTime / foot.StepLength);
                    float arc = foot.Lifted ? 0f : StepHeight * Mathf.Sin(progress * Mathf.PI);
                    position = Vector3.Lerp(foot.StepFromPosition, foot.AnimatedPosition, progress) + Vector3.up * arc;
                    rotation = Quaternion.Slerp(foot.StepFromRotation, foot.AnimatedRotation, progress);
                    break;
                default:
                    position = foot.AnimatedPosition;
                    rotation = foot.AnimatedRotation;
                    break;
            }
        }

        /// <summary>
        /// Moves one foot between planted, stepping and free when the animation lifts or puts down the foot, or when the agent was moved too far for a step.
        /// </summary>
        private void UpdatePhase(Foot foot, float speed, float deltaTime)
        {
            switch (foot.Phase)
            {
                case FootPhase.Planted:
                    if (Drift(foot).magnitude > ResetDistance)
                        Lock(foot, foot.AnimatedPosition, foot.AnimatedRotation);
                    else if (foot.Lifted)
                        StartStep(foot, StepDuration * 0.5f);
                    break;
                case FootPhase.Stepping:
                    foot.StepTime += deltaTime;
                    if (foot.StepTime >= foot.StepLength)
                    {
                        if (foot.Lifted)
                            foot.Phase = FootPhase.Free;
                        else
                            Lock(foot, foot.AnimatedPosition, foot.AnimatedRotation);
                    }
                    break;
                case FootPhase.Free:
                    if (!foot.Lifted && speed < PlantSpeed)
                        Lock(foot, foot.AnimatedPosition, foot.AnimatedRotation);
                    break;
            }
        }

        /// <summary>
        /// Picks the planted foot that steps next once either foot is past the step threshold: the one that moves most outward, away from the other foot, or else the one that is furthest off.
        /// </summary>
        private Foot ChooseStepper()
        {
            bool due = false;
            foreach (Foot foot in _feet)
                due |= foot.Phase == FootPhase.Planted && Drift(foot).magnitude > StepThreshold;
            if (!due)
                return null;

            Foot outwardFoot = null, furthestFoot = null;
            float bestOutward = 0f, bestDrift = 0f;
            for (int i = 0; i < _feet.Length; i++)
            {
                Foot foot = _feet[i], other = _feet[1 - i];
                Vector3 drift = Drift(foot);
                if (foot.Phase != FootPhase.Planted || drift.magnitude <= StepThreshold * 0.5f)
                    continue;

                Vector3 otherPosition = other.Phase == FootPhase.Planted ? other.LockPosition : other.AnimatedPosition;
                Vector3 away = foot.LockPosition - otherPosition;
                away.y = 0f;
                float outward = Vector3.Dot(drift, away.normalized);
                if (outward > bestOutward)
                {
                    bestOutward = outward;
                    outwardFoot = foot;
                }
                if (drift.magnitude > bestDrift)
                {
                    bestDrift = drift.magnitude;
                    furthestFoot = foot;
                }
            }
            return outwardFoot ?? furthestFoot;
        }

        /// <summary>
        /// Horizontal offset from where a planted foot is pinned to where the animation wants it.
        /// </summary>
        private static Vector3 Drift(Foot foot)
        {
            Vector3 drift = foot.AnimatedPosition - foot.LockPosition;
            drift.y = 0f;
            return drift;
        }

        /// <summary>
        /// Pins the foot to the given spot on the floor.
        /// </summary>
        private static void Lock(Foot foot, Vector3 position, Quaternion rotation)
        {
            foot.Phase = FootPhase.Planted;
            foot.LockPosition = position;
            foot.LockRotation = rotation;
        }

        /// <summary>
        /// Begins moving a planted foot from its locked spot towards the animated one over the given number of seconds.
        /// </summary>
        private static void StartStep(Foot foot, float duration)
        {
            foot.Phase = FootPhase.Stepping;
            foot.StepFromPosition = foot.LockPosition;
            foot.StepFromRotation = foot.LockRotation;
            foot.StepTime = 0f;
            foot.StepLength = duration;
        }

        /// <summary>
        /// Bends the knee and swings the thigh so the ankle reaches the target, keeping the knee in its animated bending plane.
        /// </summary>
        private void SolveLeg(Foot foot, Vector3 target)
        {
            Vector3 hip = foot.UpperLeg.position, knee = foot.LowerLeg.position, ankle = foot.Ankle.position;
            float thighLength = (knee - hip).magnitude, shinLength = (ankle - knee).magnitude;
            float reach = Mathf.Clamp((target - hip).magnitude, 0.01f, thighLength + shinLength - 0.001f);
            float currentAngle = Vector3.Angle(hip - knee, ankle - knee);
            float wantedAngle = Mathf.Acos(Mathf.Clamp((thighLength * thighLength + shinLength * shinLength - reach * reach) / (2f * thighLength * shinLength), -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 bendAxis = Vector3.Cross(hip - knee, ankle - knee);
            // A straight leg has no bending plane, so bend the knee forward.
            if (bendAxis.sqrMagnitude < 1e-8f)
                bendAxis = -transform.right;

            foot.LowerLeg.rotation = Quaternion.AngleAxis(wantedAngle - currentAngle, bendAxis.normalized) * foot.LowerLeg.rotation;
            foot.UpperLeg.rotation = Quaternion.FromToRotation(foot.Ankle.position - hip, target - hip) * foot.UpperLeg.rotation;
        }
    }
}
