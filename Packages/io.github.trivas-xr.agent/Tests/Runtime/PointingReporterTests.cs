#if UNITY_EDITOR
using System.Collections;
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
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace TrIVA.Tests
{
    /// <summary>
    /// Aims a near-far interactor at objects in front of it and checks the pointing messages a fake backend receives.
    /// </summary>
    [PrebuildSetup(typeof(QuietTestRun)), PostBuildCleanup(typeof(QuietTestRun))]
    public class PointingReporterTests
    {
        private const float Dwell = 0.4f;
        private const float Grace = 0.3f;
        private const float Settled = Dwell + 0.6f;
        private static readonly Quaternion AimAtObject = Quaternion.identity;
        private static readonly Quaternion AimAway = Quaternion.Euler(0f, 90f, 0f);

        private readonly List<GameObject> _created = new();
        private LiveKitRtviClient _client;
        private TokenSourceComponentConfig _tokenConfig;
        private FakeRtviBackend _backend;
        private XRInteractionManager _manager;
        private NearFarInteractor _right;
        private PointingReporter _reporter;

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
            _right = CreateHand(InteractorHandedness.Right);

            GameObject reporterObject = new GameObject("Pointing Reporter");
            _created.Add(reporterObject);
            reporterObject.SetActive(false);
            _reporter = reporterObject.AddComponent<PointingReporter>();
            SerializedObject serialized = new SerializedObject(_reporter);
            serialized.FindProperty("RtviClient").objectReferenceValue = _client;
            serialized.FindProperty("DwellSeconds").floatValue = Dwell;
            serialized.FindProperty("GraceSeconds").floatValue = Grace;
            SerializedProperty interactors = serialized.FindProperty("Interactors");
            interactors.arraySize = 1;
            interactors.GetArrayElementAtIndex(0).objectReferenceValue = _right;
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
        /// Resting on an object sends its ID and kind for that hand after the dwell and before the grace adds to it, holding still sends nothing more, and aiming away sends null after the grace.
        /// </summary>
        [UnityTest]
        public IEnumerator ReportsTargetAfterDwellAndNullAfterLeaving()
        {
            CreateGrabbable("pointing_test_flask", "flask", true);

            yield return Wait(Dwell * 0.5f);
            CollectionAssert.IsEmpty(_backend.TakeMessages());

            yield return Wait(Dwell * 0.5f + Grace * 0.5f);
            JObject pointing = TakeSingle();
            Assert.AreEqual(JTokenType.Null, pointing["left"].Type);
            Assert.AreEqual("pointing_test_flask", (string)pointing["right"]["id"]);
            Assert.AreEqual("flask", (string)pointing["right"]["kind"]);

            yield return Wait(Dwell);
            CollectionAssert.IsEmpty(_backend.TakeMessages());

            _right.transform.rotation = AimAway;
            yield return Wait(Grace * 2f);
            Assert.AreEqual(JTokenType.Null, TakeSingle()["right"].Type);
        }

        /// <summary>
        /// A ray that sweeps over an object for less than the dwell reports nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator SweepingOverAnObjectReportsNothing()
        {
            _right.transform.rotation = AimAway;
            CreateGrabbable("pointing_test_swept", "flask", true);
            yield return Wait(Grace);

            _right.transform.rotation = AimAtObject;
            yield return Wait(Dwell * 0.25f);
            _right.transform.rotation = AimAway;
            yield return Wait(Dwell * 2f);
            CollectionAssert.IsEmpty(_backend.TakeMessages());
        }

        /// <summary>
        /// A ray that slips off its object for less than the grace keeps the object reported and sends nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator JitterWithinGraceKeepsTheTarget()
        {
            CreateGrabbable("pointing_test_jitter", "flask", true);
            yield return Wait(Settled);
            TakeSingle();

            _right.transform.rotation = AimAway;
            yield return null;
            yield return null;
            _right.transform.rotation = AimAtObject;
            yield return Wait(Dwell);
            CollectionAssert.IsEmpty(_backend.TakeMessages());
        }

        /// <summary>
        /// An interactable without a ReferableObject is reported as unknown without an ID, named after its GameObject without the clone suffix, and a second one is told apart from the first.
        /// </summary>
        [UnityTest]
        public IEnumerator ReportsObjectsWithoutReferableAsUnknown()
        {
            CreateGrabbable(null, null, false).gameObject.name = "Chemical Bottle(Clone)";
            CreateGrabbable(null, null, false, new Vector3(2f, 0f, 2f)).gameObject.name = "UI Grabber";

            yield return Wait(Settled);
            JObject pointing = TakeSingle();
            Assert.AreEqual(JTokenType.Null, pointing["right"]["id"].Type);
            Assert.AreEqual("unknown", (string)pointing["right"]["kind"]);
            Assert.AreEqual("Chemical Bottle", (string)pointing["right"]["name"]);

            _right.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            yield return Wait(Settled);
            JObject[] messages = _backend.TakeMessages();
            Assert.AreEqual("UI Grabber", (string)messages[messages.Length - 1]["data"]["d"]["right"]["name"]);
        }

        /// <summary>
        /// A change of a pointed object's state is reported again.
        /// </summary>
        [UnityTest]
        public IEnumerator ReportsChangesOfPointedObjects()
        {
            XRGrabInteractable flask = CreateGrabbable("pointing_test_changing", "flask", true);
            TestState state = flask.GetComponent<TestState>();
            yield return Wait(Settled);
            Assert.AreEqual("red", (string)TakeSingle()["right"]["color"]);

            state.Color = "blue";
            Assert.AreEqual("blue", (string)TakeSingle()["right"]["color"]);
        }

        /// <summary>
        /// A hand that holds something does not point, so its target turns null after the grace.
        /// </summary>
        [UnityTest]
        public IEnumerator HoldingHandDoesNotPoint()
        {
            XRGrabInteractable flask = CreateGrabbable("pointing_test_beaker", "flask", true);
            yield return Wait(Settled);
            TakeSingle();

            _manager.SelectEnter((IXRSelectInteractor)_right, flask);
            yield return Wait(Grace * 2f);
            Assert.AreEqual(JTokenType.Null, TakeSingle()["right"].Type);
        }

        /// <summary>
        /// Disabling the reporter while a hand points tells the backend both hands point at nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator DisablingSendsNull()
        {
            CreateGrabbable("pointing_test_disabled", "flask", true);
            yield return Wait(Settled);
            TakeSingle();

            _reporter.enabled = false;
            JObject pointing = TakeSingle();
            Assert.AreEqual(JTokenType.Null, pointing["left"].Type);
            Assert.AreEqual(JTokenType.Null, pointing["right"].Type);
        }

        /// <summary>
        /// Moving the ray straight from one object to another sends null once the first was left for the grace, then the second after its dwell.
        /// </summary>
        [UnityTest]
        public IEnumerator SwitchingObjectsReportsTheNewOne()
        {
            CreateGrabbable("pointing_test_first", "flask", true);
            CreateGrabbable("pointing_test_second", "flask", true, new Vector3(2f, 0f, 2f));
            yield return Wait(Settled);
            Assert.AreEqual("pointing_test_first", (string)TakeSingle()["right"]["id"]);

            _right.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            yield return Wait(Settled);
            JObject[] messages = _backend.TakeMessages();
            Assert.AreEqual(2, messages.Length, $"Expected null then the second object, got {messages.Length} messages.");
            Assert.AreEqual(JTokenType.Null, messages[0]["data"]["d"]["right"].Type);
            Assert.AreEqual("pointing_test_second", (string)messages[1]["data"]["d"]["right"]["id"]);
        }

        /// <summary>
        /// After a disable and enable the reporter starts over and reports the object the ray rests on again.
        /// </summary>
        [UnityTest]
        public IEnumerator ReEnablingReportsAgain()
        {
            CreateGrabbable("pointing_test_reenabled", "flask", true);
            yield return Wait(Settled);
            TakeSingle();

            _reporter.enabled = false;
            TakeSingle();
            _reporter.enabled = true;
            yield return Wait(Settled);
            Assert.AreEqual("pointing_test_reenabled", (string)TakeSingle()["right"]["id"]);
        }

        /// <summary>
        /// Destroying the pointed object sends null for that hand on the next frame.
        /// </summary>
        [UnityTest]
        public IEnumerator DestroyingThePointedObjectSendsNull()
        {
            XRGrabInteractable flask = CreateGrabbable("pointing_test_destroyed", "flask", true);
            yield return Wait(Settled);
            TakeSingle();

            _created.Remove(flask.gameObject);
            Object.Destroy(flask.gameObject);
            yield return null;
            yield return null;
            Assert.AreEqual(JTokenType.Null, TakeSingle()["right"].Type);
        }

        /// <summary>
        /// A referable object that is no interactable and only has a trigger collider, like a station, can be pointed at.
        /// </summary>
        [UnityTest]
        public IEnumerator ReportsReferableTriggersThatAreNoInteractables()
        {
            CreateStation("pointing_test_station", new Vector3(0f, 0f, 2f));

            yield return Wait(Settled);
            JObject pointing = TakeSingle();
            Assert.AreEqual("pointing_test_station", (string)pointing["right"]["id"]);
            Assert.AreEqual("station", (string)pointing["right"]["kind"]);
        }

        /// <summary>
        /// A solid collider without a ReferableObject in front of a station blocks the ray, so nothing is reported.
        /// </summary>
        [UnityTest]
        public IEnumerator SolidCollidersBlockTheRayToAStation()
        {
            CreateStation("pointing_test_hidden_station", new Vector3(0f, 0f, 3f));
            GameObject wall = new GameObject("Wall");
            _created.Add(wall);
            wall.transform.position = new Vector3(0f, 0f, 1.5f);
            wall.AddComponent<BoxCollider>();

            yield return Wait(Settled);
            CollectionAssert.IsEmpty(_backend.TakeMessages());
        }

        /// <summary>
        /// A solid collider of the participant's own rig in front of the hand does not block the ray when it is on Ignore Raycast, which both the XRI caster and the reporter leave out, as on the XR rig.
        /// </summary>
        [UnityTest]
        public IEnumerator RigCollidersOnIgnoreRaycastDoNotBlockTheRay()
        {
            int ignoreRaycast = LayerMask.NameToLayer("Ignore Raycast");
            ((CurveInteractionCaster)_right.farInteractionCaster).raycastMask = ~(1 << ignoreRaycast);
            GameObject rig = new GameObject("Rig Body");
            _created.Add(rig);
            rig.layer = ignoreRaycast;
            rig.transform.position = new Vector3(0f, 0f, 0.5f);
            rig.AddComponent<BoxCollider>().size = new Vector3(0.5f, 0.5f, 0.2f);
            CreateStation("pointing_test_station_behind_rig", new Vector3(0f, 0f, 2f));

            yield return Wait(Settled);
            Assert.AreEqual("pointing_test_station_behind_rig", (string)TakeSingle()["right"]["id"]);
        }

        /// <summary>
        /// A world-space UI panel the ray hits is reported as the referable object it belongs to.
        /// </summary>
        [UnityTest]
        public IEnumerator ReportsReferableUiPanels()
        {
            GameObject eventSystem = new GameObject("Event System");
            _created.Add(eventSystem);
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<XRUIInputModule>();
            GameObject cameraObject = new GameObject("Camera");
            _created.Add(cameraObject);
            Camera eventCamera = cameraObject.AddComponent<Camera>();

            GameObject panel = new GameObject("Panel");
            _created.Add(panel);
            panel.SetActive(false);
            Canvas canvas = panel.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = eventCamera;
            panel.AddComponent<TrackedDeviceGraphicRaycaster>();
            RectTransform panelRect = (RectTransform)panel.transform;
            panelRect.position = new Vector3(0f, 0f, 2f);
            panelRect.sizeDelta = new Vector2(100f, 100f);
            panelRect.localScale = Vector3.one * 0.01f;
            ReferableObject referableObject = panel.AddComponent<ReferableObject>();
            SerializedObject serialized = new SerializedObject(referableObject);
            serialized.FindProperty("Id").stringValue = "pointing_test_panel";
            serialized.FindProperty("Kind").stringValue = "panel";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            GameObject image = new GameObject("Button", typeof(RectTransform), typeof(Image));
            image.transform.SetParent(panel.transform, false);
            ((RectTransform)image.transform).sizeDelta = new Vector2(100f, 100f);
            panel.SetActive(true);
            _right.gameObject.SetActive(false);
            _right.gameObject.SetActive(true);

            yield return Wait(Settled);
            JObject pointing = TakeSingle();
            Assert.AreEqual("pointing_test_panel", (string)pointing["right"]["id"]);
            Assert.AreEqual("panel", (string)pointing["right"]["kind"]);
        }

        /// <summary>
        /// Creates a referable object of kind station at the given position with only a trigger collider and no interactable.
        /// </summary>
        private void CreateStation(string id, Vector3 position)
        {
            GameObject station = new GameObject("Station");
            _created.Add(station);
            station.SetActive(false);
            station.transform.position = position;
            station.AddComponent<BoxCollider>().isTrigger = true;
            ReferableObject referableObject = station.AddComponent<ReferableObject>();
            SerializedObject serialized = new SerializedObject(referableObject);
            serialized.FindProperty("Id").stringValue = id;
            serialized.FindProperty("Kind").stringValue = "station";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            station.SetActive(true);
        }

        /// <summary>
        /// Waits the given real time, so the interaction manager casts the far ray and the reporter reads it every frame.
        /// </summary>
        private static IEnumerator Wait(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            yield return null;
        }

        /// <summary>
        /// Creates a near-far interactor of the given handedness at the origin, aiming along +Z, registered with the test's interaction manager.
        /// </summary>
        private NearFarInteractor CreateHand(InteractorHandedness handedness)
        {
            GameObject hand = new GameObject($"{handedness} Hand");
            _created.Add(hand);
            hand.SetActive(false);
            NearFarInteractor interactor = hand.AddComponent<NearFarInteractor>();
            interactor.interactionManager = _manager;
            interactor.handedness = handedness;
            hand.SetActive(true);
            return interactor;
        }

        /// <summary>
        /// Creates a grab interactable at the given position, 2 m in front of the hand by default, with a ReferableObject of the given ID and kind and a TestState when referable is set.
        /// </summary>
        private XRGrabInteractable CreateGrabbable(string id, string kind, bool referable, Vector3? position = null)
        {
            GameObject grabbable = new GameObject("Grabbable");
            _created.Add(grabbable);
            grabbable.SetActive(false);
            grabbable.transform.position = position ?? new Vector3(0f, 0f, 2f);
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
        /// Asserts the backend received exactly one message since the last check, a pointing client message, and returns its data.
        /// </summary>
        private JObject TakeSingle()
        {
            JObject[] messages = _backend.TakeMessages();
            Assert.AreEqual(1, messages.Length, $"Expected one pointing message, got {messages.Length}.");
            Assert.AreEqual("client-message", (string)messages[0]["type"]);
            Assert.AreEqual("pointing", (string)messages[0]["data"]["t"]);
            return (JObject)messages[0]["data"]["d"];
        }
    }
}
#endif
