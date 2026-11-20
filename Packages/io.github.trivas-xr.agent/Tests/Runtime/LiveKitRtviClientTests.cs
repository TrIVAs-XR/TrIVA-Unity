#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using LiveKit;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TrIVA.Connection;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TrIVA.Tests
{
    /// <summary>
    /// Checks the RTVI client's bot-ready state and its custom client messages against a fake backend.
    /// </summary>
    [PrebuildSetup(typeof(QuietTestRun)), PostBuildCleanup(typeof(QuietTestRun))]
    public class LiveKitRtviClientTests
    {
        private LiveKitRtviClient _client;
        private TokenSourceComponentConfig _tokenConfig;
        private FakeRtviBackend _backend;

        [SetUp]
        public void SetUp()
        {
            _client = OfflineRtviClient.Create(out _tokenConfig);
            _backend = new FakeRtviBackend(_client);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_client.gameObject);
            Object.DestroyImmediate(_tokenConfig);
        }

        /// <summary>
        /// BotReady is raised once per connection, and a disconnect clears IsBotReady until the next bot-ready.
        /// </summary>
        [Test]
        public void BotReadyOncePerConnection()
        {
            int raised = 0;
            _client.BotReady += () => raised++;
            _backend.Connect();
            Assert.IsFalse(_client.IsBotReady);

            _backend.SendBotReady();
            _backend.SendBotReady();
            Assert.IsTrue(_client.IsBotReady);
            Assert.AreEqual(1, raised);

            _backend.Disconnect();
            Assert.IsFalse(_client.IsBotReady);
            _backend.Connect();
            _backend.SendBotReady();
            Assert.AreEqual(2, raised);
        }

        /// <summary>
        /// A client message goes out in the {t, d} envelope while the transport is open and is dropped without a log while it is closed.
        /// </summary>
        [Test]
        public void SendClientMessageWrapsOrDrops()
        {
            _client.SendClientMessage("held", new JObject { ["left"] = null });
            LogAssert.NoUnexpectedReceived();

            _backend.Connect();
            _backend.TakeMessages();
            _client.SendClientMessage("held", new JObject { ["left"] = null });
            JObject[] messages = _backend.TakeMessages();
            Assert.AreEqual(1, messages.Length);
            Assert.AreEqual("client-message", (string)messages[0]["type"]);
            Assert.AreEqual("held", (string)messages[0]["data"]["t"]);
            Assert.AreEqual(JTokenType.Null, messages[0]["data"]["d"]["left"].Type);
        }

        /// <summary>
        /// Packets that are not JSON, carry another label or have no type are dropped without raising MessageReceived or logging anything.
        /// </summary>
        [Test]
        public void DropsMalformedPackets()
        {
            int received = 0;
            _client.MessageReceived += (type, data) => received++;
            List<string> logs = CaptureLogs(() =>
            {
                Receive("not json");
                Receive("{\"label\":\"other\",\"type\":\"bot-ready\",\"data\":{}}");
                Receive("{\"label\":\"rtvi-ai\",\"data\":{}}");
            });
            Assert.AreEqual(0, received);
            CollectionAssert.IsEmpty(logs);
        }

        /// <summary>
        /// A fatal server error is logged as an error and a non-fatal one as a warning; both still reach MessageReceived.
        /// </summary>
        [Test]
        public void LogsServerErrorsBySeverity()
        {
            List<string> types = new();
            _client.MessageReceived += (type, data) => types.Add(type);

            LogAssert.Expect(LogType.Error, "RTVI fatal error: pipeline broke");
            _backend.Send("error", new JObject { ["error"] = "pipeline broke", ["fatal"] = true });
            LogAssert.Expect(LogType.Warning, "RTVI error: version mismatch");
            _backend.Send("error", new JObject { ["error"] = "version mismatch", ["fatal"] = false });

            CollectionAssert.AreEqual(new[] { "error", "error" }, types);
        }

        /// <summary>
        /// An unknown message type is logged for debugging, a known one is not.
        /// </summary>
        [Test]
        public void LogsOnlyUnknownTypes()
        {
            List<string> known = CaptureLogs(() => _backend.Send("bot-tts-text", new JObject { ["text"] = "Hi" }));
            CollectionAssert.IsEmpty(known);

            List<string> unknown = CaptureLogs(() => _backend.Send("made-up-type", new JObject()));
            Assert.AreEqual(1, unknown.Count);
            StringAssert.StartsWith("Unknown RTVI message type 'made-up-type'", unknown[0]);
        }

        /// <summary>
        /// MessageReceived passes the data object on as sent, and null for a message whose data is null or missing.
        /// </summary>
        [Test]
        public void PassesDataOn()
        {
            List<JObject> payloads = new();
            _client.MessageReceived += (type, data) => payloads.Add(data);

            _backend.Send("bot-tts-text", new JObject { ["text"] = "Hello there" });
            _backend.Send("bot-started-speaking", null);
            Receive("{\"label\":\"rtvi-ai\",\"type\":\"bot-stopped-speaking\"}");

            Assert.AreEqual(3, payloads.Count);
            Assert.AreEqual("Hello there", (string)payloads[0]["text"]);
            Assert.IsNull(payloads[1]);
            Assert.IsNull(payloads[2]);
        }

        /// <summary>
        /// Hands the client a packet with the given text as if it arrived over LiveKit.
        /// </summary>
        private void Receive(string packet)
        {
            _client.Receive(Encoding.UTF8.GetBytes(packet));
        }

        /// <summary>
        /// Runs the action and returns every message logged meanwhile, of any severity.
        /// </summary>
        private static List<string> CaptureLogs(Action action)
        {
            List<string> logs = new();
            Application.LogCallback capture = (condition, stackTrace, type) => logs.Add(condition);
            Application.logMessageReceived += capture;
            try
            {
                action();
            }
            finally
            {
                Application.logMessageReceived -= capture;
            }
            return logs;
        }
    }
}
#endif
