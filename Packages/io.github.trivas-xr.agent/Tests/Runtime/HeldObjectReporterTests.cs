#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using LiveKit;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TrIVA.Connection;
using TrIVA.Inputs;
using TrIVA.Objects;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrIVA.Tests
{
    /// <summary>
    /// Grabs objects with two hand interactors through the interaction manager and checks the held messages a fake backend receives.
    /// </summary>
    [PrebuildSetup(typeof(XrOffDuringTests)), PostBuildCleanup(typeof(XrOffDuringTests))]
    public class HeldObjectReporterTests
    {
        private readonly List<GameObject> _created = new();
        private LiveKitRtviClient _client;
        private TokenSourceComponentConfig _tokenConfig;
        private FakeRtviBackend _backend;
        private XRInteractionManager _manager;
        private XRBaseInteractor _left;
        private XRBaseInteractor _right;

        [SetUp]
        public void SetUp()
        {
            _client = OfflineRtviClient.Create(out _tokenConfig);
            _created.Add(_client.gameObject);
            _backend = new FakeRtviBackend(_client);
            _backend.Connect();
            _backend.TakeMessages();

            GameObject managerObject = new GameObject("Interaction Manager");
            _created.Add(managerObject);
            _manager = managerObject.AddComponent<XRInteractionManager>();
            _left = CreateHand(InteractorHandedness.Left);
            _right = CreateHand(InteractorHandedness.Right);

            GameObject reporterObject = new GameObject("Held Object Reporter");
            _created.Add(reporterObject);
            reporterObject.SetActive(false);
            HeldObjectReporter reporter = reporterObject.AddComponent<HeldObjectReporter>();
            SerializedObject serialized = new SerializedObject(reporter);
            serialized.FindProperty("RtviClient").objectReferenceValue = _client;
            SerializedProperty interactors = serialized.FindProperty("Interactors");
            interactors.arraySize = 2;
            interactors.GetArrayElementAtIndex(0).objectReferenceValue = _left;
            interactors.GetArrayElementAtIndex(1).objectReferenceValue = _right;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            reporterObject.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject created in Enumerable.Reverse(_created))
                Object.DestroyImmediate(created);
            _created.Clear();
            Object.DestroyImmediate(_tokenConfig);
        }

        /// <summary>
        /// Grabbing and releasing sends what each hand holds, with the held object's ID and kind and null for an empty hand.
        /// </summary>
        [Test]
        public void ReportsGrabAndRelease()
        {
            XRGrabInteractable flask = CreateGrabbable("held_test_flask", "flask", true);

            _manager.SelectEnter((IXRSelectInteractor)_right, flask);
            JObject held = TakeSingleHeld();
            Assert.AreEqual(JTokenType.Null, held["left"].Type);
            Assert.AreEqual("held_test_flask", (string)held["right"]["id"]);
            Assert.AreEqual("flask", (string)held["right"]["kind"]);

            _manager.SelectExit((IXRSelectInteractor)_right, flask);
            held = TakeSingleHeld();
            Assert.AreEqual(JTokenType.Null, held["left"].Type);
            Assert.AreEqual(JTokenType.Null, held["right"].Type);
        }

        /// <summary>
        /// A grabbed object without a ReferableObject is reported as unknown without an ID, named after its GameObject without the clone suffix.
        /// </summary>
        [Test]
        public void ReportsObjectsWithoutReferableAsUnknown()
        {
            XRGrabInteractable thing = CreateGrabbable(null, null, false);
            thing.gameObject.name = "Chemical Bottle(Clone)";

            _manager.SelectEnter((IXRSelectInteractor)_left, thing);
            JObject held = TakeSingleHeld();
            Assert.AreEqual(JTokenType.Null, held["left"]["id"].Type);
            Assert.AreEqual("unknown", (string)held["left"]["kind"]);
            Assert.AreEqual("Chemical Bottle", (string)held["left"]["name"]);
        }

        /// <summary>
        /// A change of a held object's state is reported again, and no longer once it was released.
        /// </summary>
        [Test]
        public void ReportsChangesOfHeldObjects()
        {
            XRGrabInteractable flask = CreateGrabbable("held_test_beaker", "flask", true);
            TestState state = flask.GetComponent<TestState>();

            _manager.SelectEnter((IXRSelectInteractor)_left, flask);
            Assert.AreEqual("red", (string)TakeSingleHeld()["left"]["color"]);

            state.Color = "blue";
            Assert.AreEqual("blue", (string)TakeSingleHeld()["left"]["color"]);

            _manager.SelectExit((IXRSelectInteractor)_left, flask);
            TakeSingleHeld();
            state.Color = "green";
            CollectionAssert.IsEmpty(_backend.TakeMessages());
        }

        /// <summary>
        /// Creates a hand interactor of the given handedness, registered with the test's interaction manager.
        /// </summary>
        private XRBaseInteractor CreateHand(InteractorHandedness handedness)
        {
            GameObject hand = new GameObject($"{handedness} Hand");
            _created.Add(hand);
            hand.SetActive(false);
            hand.AddComponent<SphereCollider>().isTrigger = true;
            XRDirectInteractor interactor = hand.AddComponent<XRDirectInteractor>();
            interactor.interactionManager = _manager;
            interactor.handedness = handedness;
            hand.SetActive(true);
            return interactor;
        }

        /// <summary>
        /// Creates a grab interactable, with a ReferableObject of the given ID and kind and a TestState when referable is set.
        /// </summary>
        private XRGrabInteractable CreateGrabbable(string id, string kind, bool referable)
        {
            GameObject grabbable = new GameObject("Grabbable");
            _created.Add(grabbable);
            grabbable.SetActive(false);
            grabbable.AddComponent<BoxCollider>();
            grabbable.AddComponent<Rigidbody>().isKinematic = true;
            XRGrabInteractable interactable = grabbable.AddComponent<XRGrabInteractable>();
            interactable.interactionManager = _manager;
            if (referable)
            {
                grabbable.AddComponent<TestState>();
                ReferableObject referableObject = grabbable.AddComponent<ReferableObject>();
                SerializedObject serialized = new SerializedObject(referableObject);
                serialized.FindProperty("Id").stringValue = id;
                serialized.FindProperty("Kind").stringValue = kind;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            grabbable.SetActive(true);
            return interactable;
        }

        /// <summary>
        /// Asserts the backend received exactly one message since the last check, a held client message, and returns its data.
        /// </summary>
        private JObject TakeSingleHeld()
        {
            JObject[] messages = _backend.TakeMessages();
            Assert.AreEqual(1, messages.Length, $"Expected one held message, got {messages.Length}.");
            Assert.AreEqual("client-message", (string)messages[0]["type"]);
            Assert.AreEqual("held", (string)messages[0]["data"]["t"]);
            return (JObject)messages[0]["data"]["d"];
        }
    }
}
#endif
