using UnityEngine;

namespace TrIVA.Inputs
{
    /// <summary>
    /// Moves the vision camera after the participant's head with an exponential lag, so a quick glance barely turns the view the backend receives.
    /// </summary>
    public class VisionCameraFollower : MonoBehaviour
    {
        [SerializeField, Tooltip("The participant's head, usually the XR main camera. Must not be a parent of this object.")]
        private Transform Head;
        [SerializeField, Min(0f), Tooltip("Seconds until the camera has covered about 63% of the way to a new head pose; 0 follows without lag.")]
        private float SmoothingTime = 0.5f;

        /// <summary>
        /// Checks the head reference, disabling the component with an error when it is missing, and starts at the head pose.
        /// </summary>
        private void OnEnable()
        {
            if (!Head)
            {
                Debug.LogError($"VisionCameraFollower on '{name}' needs a head assigned. The vision camera will not follow the participant.", this);
                enabled = false;
                return;
            }
            transform.SetPositionAndRotation(Head.position, Head.rotation);
        }

        /// <summary>
        /// Eases position and rotation toward the head pose after the head has been tracked this frame.
        /// </summary>
        private void LateUpdate()
        {
            float t = SmoothingTime > 0f ? 1f - Mathf.Exp(-Time.deltaTime / SmoothingTime) : 1f;
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, Head.position, t),
                Quaternion.Slerp(transform.rotation, Head.rotation, t));
        }
    }
}
