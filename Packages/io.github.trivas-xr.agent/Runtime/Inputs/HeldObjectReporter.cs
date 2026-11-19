using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TrIVA.Connection;
using TrIVA.Objects;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrIVA.Inputs
{
    /// <summary>
    /// Tells the backend what the participant holds in each hand: sends a held client message with the description of the object in the left and the right hand whenever a grab starts or ends, or a held object changes.
    /// </summary>
    public class HeldObjectReporter : MonoBehaviour
    {
        private const string HeldType = "held";

        [SerializeField, Tooltip("RTVI client the messages are sent through.")]
        private LiveKitRtviClient RtviClient;
        [SerializeField, Tooltip("Interactors whose selections count as holding, such as each hand's near-far interactor; each needs its handedness set to left or right.")]
        private List<XRBaseInteractor> Interactors = new();

        private readonly List<ReferableObject> _watched = new();

        /// <summary>
        /// Checks the references, disabling the component with an error when the client is missing, and listens to the interactors' selections.
        /// </summary>
        private void OnEnable()
        {
            if (!RtviClient)
            {
                Debug.LogError($"HeldObjectReporter on '{name}' needs a LiveKitRtviClient assigned. The backend will not be told what the participant holds.", this);
                enabled = false;
                return;
            }
            foreach (XRBaseInteractor interactor in Interactors)
            {
                if (!interactor)
                    continue;
                if (interactor.handedness == InteractorHandedness.None)
                    Debug.LogWarning($"HeldObjectReporter: interactor '{interactor.name}' has no handedness, so what it holds is not reported.", this);
                interactor.selectEntered.AddListener(OnSelectEntered);
                interactor.selectExited.AddListener(OnSelectExited);
            }
        }

        private void OnDisable()
        {
            foreach (XRBaseInteractor interactor in Interactors)
            {
                if (!interactor)
                    continue;
                interactor.selectEntered.RemoveListener(OnSelectEntered);
                interactor.selectExited.RemoveListener(OnSelectExited);
            }
            Watch(new List<ReferableObject>());
        }

        /// <summary>
        /// Reports the hands after a grab started.
        /// </summary>
        private void OnSelectEntered(SelectEnterEventArgs args)
        {
            Report();
        }

        /// <summary>
        /// Reports the hands after a grab ended.
        /// </summary>
        private void OnSelectExited(SelectExitEventArgs args)
        {
            Report();
        }

        /// <summary>
        /// Reports the hands again when an object in them changed, such as a held flask's substance.
        /// </summary>
        private void OnHeldObjectChanged(ReferableObject changed)
        {
            Report();
        }

        /// <summary>
        /// Sends what each hand holds now and watches the held objects for changes.
        /// </summary>
        private void Report()
        {
            List<ReferableObject> held = new List<ReferableObject>();
            JObject hands = new JObject
            {
                ["left"] = DescribeHeld(InteractorHandedness.Left, held),
                ["right"] = DescribeHeld(InteractorHandedness.Right, held)
            };
            Watch(held);
            RtviClient.SendClientMessage(HeldType, hands);
        }

        /// <summary>
        /// Describes the first object an interactor of the hand holds and adds it to the held objects; null when the hand is empty. An object without a ReferableObject is described as unknown with its name, as it cannot be referred to.
        /// </summary>
        private JToken DescribeHeld(InteractorHandedness hand, List<ReferableObject> held)
        {
            foreach (XRBaseInteractor interactor in Interactors)
            {
                if (!interactor || interactor.handedness != hand || !interactor.hasSelection)
                    continue;
                IXRSelectInteractable interactable = interactor.interactablesSelected[0];
                ReferableObject referable = interactable.transform.GetComponentInParent<ReferableObject>();
                if (!referable)
                    return ReferableObject.DescribeUnreferable(interactable.transform.gameObject);
                held.Add(referable);
                return referable.Describe();
            }
            return JValue.CreateNull();
        }

        /// <summary>
        /// Listens for changes on exactly the given objects from now on.
        /// </summary>
        private void Watch(List<ReferableObject> objects)
        {
            foreach (ReferableObject watched in _watched)
            {
                if (watched)
                    watched.Changed -= OnHeldObjectChanged;
            }
            _watched.Clear();
            foreach (ReferableObject referable in objects)
            {
                if (_watched.Contains(referable))
                    continue;
                referable.Changed += OnHeldObjectChanged;
                _watched.Add(referable);
            }
        }
    }
}
