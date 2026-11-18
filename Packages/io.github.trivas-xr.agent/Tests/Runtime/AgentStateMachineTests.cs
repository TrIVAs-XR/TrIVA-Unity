#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using LiveKit;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TrIVA.Agent;
using TrIVA.Connection;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace TrIVA.Tests
{
    /// <summary>
    /// Sends RTVI events from a fake backend to an AgentStateMachine and checks the states it goes through and the changes it announces.
    /// </summary>
    [PrebuildSetup(typeof(XrOffDuringTests)), PostBuildCleanup(typeof(XrOffDuringTests))]
    public class AgentStateMachineTests
    {
        private const float LongTimeout = 60f;
        private const float ShortTimeout = 0.1f;

        private readonly List<GameObject> _created = new();
        private readonly List<(AgentState Previous, AgentState Next)> _changes = new();
        private LiveKitRtviClient _client;
        private TokenSourceComponentConfig _tokenConfig;
        private FakeRtviBackend _backend;

        [SetUp]
        public void SetUp()
        {
            _client = OfflineRtviClient.Create(out _tokenConfig);
            _created.Add(_client.gameObject);
            _backend = new FakeRtviBackend(_client);
            _backend.Connect();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject created in _created)
                Object.DestroyImmediate(created);
            _created.Clear();
            _changes.Clear();
            Object.DestroyImmediate(_tokenConfig);
        }

        /// <summary>
        /// A turn goes from Idle through Listening, Thinking and Speaking back to Idle, announcing each change with the state before it.
        /// </summary>
        [Test]
        public void NormalTurn()
        {
            AgentStateMachine machine = CreateMachine(LongTimeout, LongTimeout);
            Assert.AreEqual(AgentState.Idle, machine.CurrentState);

            _backend.Send("user-started-speaking", null);
            _backend.Send("user-stopped-speaking", null);
            _backend.Send("bot-started-speaking", null);
            _backend.Send("bot-stopped-speaking", null);

            CollectionAssert.AreEqual(new[]
            {
                (AgentState.Idle, AgentState.Listening),
                (AgentState.Listening, AgentState.Thinking),
                (AgentState.Thinking, AgentState.Speaking),
                (AgentState.Speaking, AgentState.Idle)
            }, _changes);
            Assert.AreEqual(AgentState.Idle, machine.CurrentState);
        }

        /// <summary>
        /// A final transcription while listening moves to Thinking, and an interim one while thinking goes back to Listening, as the participant kept talking.
        /// </summary>
        [Test]
        public void TranscriptionsMoveBetweenListeningAndThinking()
        {
            AgentStateMachine machine = CreateMachine(LongTimeout, LongTimeout);
            _backend.Send("user-started-speaking", null);

            _backend.Send("user-transcription", Transcription(true));
            Assert.AreEqual(AgentState.Thinking, machine.CurrentState);

            _backend.Send("user-transcription", Transcription(false));
            Assert.AreEqual(AgentState.Listening, machine.CurrentState);
        }

        /// <summary>
        /// The participant stopping speaking only ends Listening; while idle or speaking it changes nothing.
        /// </summary>
        [Test]
        public void UserStoppedSpeakingOnlyEndsListening()
        {
            AgentStateMachine machine = CreateMachine(LongTimeout, LongTimeout);
            _backend.Send("user-stopped-speaking", null);
            Assert.AreEqual(AgentState.Idle, machine.CurrentState);

            _backend.Send("bot-started-speaking", null);
            _backend.Send("user-stopped-speaking", null);
            Assert.AreEqual(AgentState.Speaking, machine.CurrentState);
        }

        /// <summary>
        /// The participant speaking over the agent moves to Listening, and the agent's speech ending afterwards leaves it there.
        /// </summary>
        [Test]
        public void BargeInListens()
        {
            AgentStateMachine machine = CreateMachine(LongTimeout, LongTimeout);
            _backend.Send("bot-started-speaking", null);

            _backend.Send("user-started-speaking", null);
            Assert.AreEqual(AgentState.Listening, machine.CurrentState);

            _backend.Send("bot-stopped-speaking", null);
            Assert.AreEqual(AgentState.Listening, machine.CurrentState);
        }

        /// <summary>
        /// The LLM starting without the participant having spoken, as for a game reaction, moves from Idle to Thinking.
        /// </summary>
        [Test]
        public void LlmStartingThinks()
        {
            AgentStateMachine machine = CreateMachine(LongTimeout, LongTimeout);
            _backend.Send("bot-llm-started", null);
            Assert.AreEqual(AgentState.Thinking, machine.CurrentState);
        }

        /// <summary>
        /// Thinking falls back to Idle after its timeout, Idle becomes Inactive after its own, and the participant speaking wakes the agent again.
        /// </summary>
        [UnityTest]
        public IEnumerator TimeoutsAndWakingUp()
        {
            AgentStateMachine machine = CreateMachine(ShortTimeout, ShortTimeout);
            _backend.Send("bot-llm-started", null);

            float giveUpAt = Time.time + 2f;
            while (machine.CurrentState != AgentState.Inactive && Time.time < giveUpAt)
                yield return null;
            CollectionAssert.AreEqual(new[]
            {
                (AgentState.Idle, AgentState.Thinking),
                (AgentState.Thinking, AgentState.Idle),
                (AgentState.Idle, AgentState.Inactive)
            }, _changes);

            _backend.Send("user-started-speaking", null);
            Assert.AreEqual(AgentState.Listening, machine.CurrentState);
        }

        /// <summary>
        /// An event that leads to the state the agent is already in announces nothing.
        /// </summary>
        [Test]
        public void SameStateIsNotAnnounced()
        {
            CreateMachine(LongTimeout, LongTimeout);
            _backend.Send("user-started-speaking", null);
            _backend.Send("user-started-speaking", null);
            Assert.AreEqual(1, _changes.Count);
        }

        /// <summary>
        /// Without an RTVI client the machine reports the missing reference and disables itself.
        /// </summary>
        [Test]
        public void MissingClientDisables()
        {
            LogAssert.Expect(LogType.Error, "AgentStateMachine has no LiveKitRtviClient assigned.");
            GameObject agent = new GameObject("Agent Without Client");
            _created.Add(agent);
            AgentStateMachine machine = agent.AddComponent<AgentStateMachine>();
            Assert.IsFalse(machine.enabled);
        }

        /// <summary>
        /// Creates a state machine listening to the test's client with the given timeouts in seconds, recording every change it announces.
        /// </summary>
        private AgentStateMachine CreateMachine(float thinkingTimeout, float inactiveTimeout)
        {
            GameObject agent = new GameObject("Agent");
            _created.Add(agent);
            agent.SetActive(false);
            AgentStateMachine machine = agent.AddComponent<AgentStateMachine>();
            SerializedObject serialized = new SerializedObject(machine);
            serialized.FindProperty("RtviClient").objectReferenceValue = _client;
            serialized.FindProperty("ThinkingTimeout").floatValue = thinkingTimeout;
            serialized.FindProperty("InactiveTimeout").floatValue = inactiveTimeout;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            machine.StateChanged += (previous, next) => _changes.Add((previous, next));
            agent.SetActive(true);
            return machine;
        }

        /// <summary>
        /// Data of a user-transcription event, final or interim.
        /// </summary>
        private static JObject Transcription(bool final)
        {
            return new JObject { ["text"] = "hello", ["user_id"] = "unity", ["timestamp"] = "", ["final"] = final };
        }
    }
}
#endif
