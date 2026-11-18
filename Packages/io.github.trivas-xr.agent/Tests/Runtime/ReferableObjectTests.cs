#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TrIVA.Objects;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TrIVA.Tests
{
    /// <summary>
    /// Checks how ReferableObject assigns and looks up IDs and builds its description.
    /// </summary>
    [PrebuildSetup(typeof(XrOffDuringTests)), PostBuildCleanup(typeof(XrOffDuringTests))]
    public class ReferableObjectTests
    {
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject created in _created)
                Object.DestroyImmediate(created);
            _created.Clear();
        }

        /// <summary>
        /// Objects without an authored ID get distinct IDs from their kind, and each can be found by its ID.
        /// </summary>
        [Test]
        public void GeneratesDistinctIdsFromKind()
        {
            ReferableObject first = Create("", "testflask");
            ReferableObject second = Create("", "testflask");

            StringAssert.StartsWith("testflask-", first.ObjectId);
            StringAssert.StartsWith("testflask-", second.ObjectId);
            Assert.AreNotEqual(first.ObjectId, second.ObjectId);
            Assert.IsTrue(ReferableObject.TryFind(second.ObjectId, out ReferableObject found));
            Assert.AreSame(second, found);
        }

        /// <summary>
        /// An authored ID is kept; a second object with the same ID is reported and cannot be looked up in place of the first.
        /// </summary>
        [Test]
        public void KeepsAuthoredIdAndReportsDuplicates()
        {
            ReferableObject station = Create("test_burner", "station");
            Assert.AreEqual("test_burner", station.ObjectId);

            LogAssert.Expect(LogType.Error, new Regex("already holds"));
            Create("test_burner", "station");
            Assert.IsTrue(ReferableObject.TryFind("test_burner", out ReferableObject found));
            Assert.AreSame(station, found);
        }

        /// <summary>
        /// Disabling an object removes it from the lookup.
        /// </summary>
        [Test]
        public void DisabledObjectsCannotBeFound()
        {
            ReferableObject station = Create("test_stirrer", "station");
            station.gameObject.SetActive(false);
            Assert.IsFalse(ReferableObject.TryFind("test_stirrer", out _));
        }

        /// <summary>
        /// The description holds the ID and kind plus what a state component adds, and a state change is passed on.
        /// </summary>
        [Test]
        public void DescriptionIncludesStateAndChanges()
        {
            ReferableObject flask = Create("test_flask", "flask", typeof(TestState));
            int changes = 0;
            flask.Changed += _ => changes++;

            JObject description = flask.Describe();
            Assert.AreEqual("test_flask", (string)description["id"]);
            Assert.AreEqual("flask", (string)description["kind"]);
            Assert.AreEqual("red", (string)description["color"]);

            TestState state = flask.GetComponent<TestState>();
            state.Color = "blue";
            Assert.AreEqual(1, changes);
            Assert.AreEqual("blue", (string)flask.Describe()["color"]);
        }

        /// <summary>
        /// Creates an object with the given authored ID and kind, adding the given components first so the object sees them on Awake.
        /// </summary>
        private ReferableObject Create(string id, string kind, params Type[] components)
        {
            GameObject gameObject = new GameObject($"Referable {kind}");
            _created.Add(gameObject);
            gameObject.SetActive(false);
            foreach (Type component in components)
                gameObject.AddComponent(component);
            ReferableObject referable = gameObject.AddComponent<ReferableObject>();
            SerializedObject serialized = new SerializedObject(referable);
            serialized.FindProperty("Id").stringValue = id;
            serialized.FindProperty("Kind").stringValue = kind;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            gameObject.SetActive(true);
            return referable;
        }
    }

    /// <summary>
    /// State component for the tests that describes the object by a colour.
    /// </summary>
    public class TestState : MonoBehaviour, IObjectState
    {
        private string _color = "red";

        /// <summary>
        /// Raised when the colour changes.
        /// </summary>
        public event Action StateChanged;

        /// <summary>
        /// Colour the object is described by; setting it raises StateChanged.
        /// </summary>
        public string Color
        {
            get => _color;
            set
            {
                _color = value;
                StateChanged?.Invoke();
            }
        }

        /// <summary>
        /// Adds the colour to the description.
        /// </summary>
        public void Describe(JObject description)
        {
            description["color"] = _color;
        }
    }
}
#endif
