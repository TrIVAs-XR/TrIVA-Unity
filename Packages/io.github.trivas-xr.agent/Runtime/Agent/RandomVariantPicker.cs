using UnityEngine;

namespace TrIVA.Agent
{
    /// <summary>
    /// Sets an Animator parameter to a random variant index whenever its sub-state machine is entered, so entry transitions (integer parameter) or a blend tree (float parameter) can pick one of several clips for the same agent state.
    /// </summary>
    public class RandomVariantPicker : StateMachineBehaviour
    {
        [SerializeField, Tooltip("Integer or float Animator parameter that picks the variant.")] private string Parameter = "Variant";
        [SerializeField, Min(1), Tooltip("Number of clip variants in this sub-state machine.")] private int VariantCount = 1;
        [SerializeField, Tooltip("Avoid repeating the previous variant when more than one exists.")] private bool AvoidRepeat = true;

        private int _lastVariant = -1;

        /// <summary>
        /// Picks the variant for this pass through the sub-state machine before its entry transitions are evaluated.
        /// </summary>
        public override void OnStateMachineEnter(Animator animator, int stateMachinePathHash)
        {
            int variant = Random.Range(0, VariantCount);
            if (AvoidRepeat && VariantCount > 1 && variant == _lastVariant)
                variant = (variant + Random.Range(1, VariantCount)) % VariantCount;

            _lastVariant = variant;
            if (IsFloatParameter(animator))
                animator.SetFloat(Parameter, variant);
            else
                animator.SetInteger(Parameter, variant);
        }

        /// <summary>
        /// Whether the parameter is a float, as a blend tree that picks the variant needs.
        /// </summary>
        private bool IsFloatParameter(Animator animator)
        {
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                if (parameter.name == Parameter)
                    return parameter.type == AnimatorControllerParameterType.Float;
            }
            return false;
        }
    }
}
