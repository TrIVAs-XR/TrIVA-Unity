#if UNITY_EDITOR
using LiveKit;
using TrIVA.Connection;
using UnityEditor;
using UnityEngine;

namespace TrIVA.Tests
{
    /// <summary>
    /// Builds an RTVI client whose LiveKit connector is off, so it never connects and only a FakeRtviBackend reaches it.
    /// </summary>
    public static class OfflineRtviClient
    {
        /// <summary>
        /// Creates the client on a new GameObject with a literal token config, which the caller destroys with the GameObject afterwards.
        /// </summary>
        public static LiveKitRtviClient Create(out TokenSourceComponentConfig tokenConfig)
        {
            GameObject link = new GameObject("Agent Link");
            link.SetActive(false);
            LiveKitRtviClient client = link.AddComponent<LiveKitRtviClient>();
            link.GetComponent<LiveKitConnector>().enabled = false;

            tokenConfig = ScriptableObject.CreateInstance<TokenSourceComponentConfig>();
            SerializedObject configSerialized = new SerializedObject(tokenConfig);
            configSerialized.FindProperty("_tokenSourceType").enumValueIndex = (int)TokenSourceType.Literal;
            configSerialized.FindProperty("_serverUrl").stringValue = "ws://fake-rtvi-backend";
            configSerialized.FindProperty("_token").stringValue = "fake";
            configSerialized.ApplyModifiedPropertiesWithoutUndo();
            SerializedObject tokenSerialized = new SerializedObject(link.GetComponent<TokenSourceComponent>());
            tokenSerialized.FindProperty("_config").objectReferenceValue = tokenConfig;
            tokenSerialized.ApplyModifiedPropertiesWithoutUndo();

            link.SetActive(true);
            return client;
        }
    }
}
#endif
