#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine.TestTools;
using UnityEngine.XR.Management;

namespace TrIVA.Tests
{
    /// <summary>
    /// Keeps XR from starting and mutes the Editor's audio while a PlayMode test run is in Play mode, so the tests never wait on the OpenXR runtime and play no sound, and restores both after the run; the XR change stays in memory and is never saved to the XR settings asset.
    /// </summary>
    public class QuietTestRun : IPrebuildSetup, IPostBuildCleanup
    {
        // Session keys holding the value before the run (1 or 0), absent while no run has changed it; kept in the session since the run reloads the domain in between.
        private const string PreviousXrKey = "TrIVA.Tests.XrInitOnStartBeforeRun";
        private const string PreviousMuteKey = "TrIVA.Tests.AudioMuteBeforeRun";
        private const int NotRemembered = -1;

        /// <summary>
        /// Remembers whether the Standalone XR settings start XR on Play mode and whether the Editor's audio is muted, then switches XR start off and mutes; a second call in the same run keeps the first remembered values.
        /// </summary>
        public void Setup()
        {
            if (SessionState.GetInt(PreviousMuteKey, NotRemembered) == NotRemembered)
                SessionState.SetInt(PreviousMuteKey, EditorUtility.audioMasterMute ? 1 : 0);
            EditorUtility.audioMasterMute = true;

            XRGeneralSettings settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            if (!settings)
                return;
            if (SessionState.GetInt(PreviousXrKey, NotRemembered) == NotRemembered)
                SessionState.SetInt(PreviousXrKey, settings.InitManagerOnStart ? 1 : 0);
            settings.InitManagerOnStart = false;
        }

        /// <summary>
        /// Gives the Editor's audio mute and the Standalone XR settings back the values remembered before the run, where one was remembered.
        /// </summary>
        public void Cleanup()
        {
            int previousMute = SessionState.GetInt(PreviousMuteKey, NotRemembered);
            if (previousMute != NotRemembered)
            {
                EditorUtility.audioMasterMute = previousMute == 1;
                SessionState.EraseInt(PreviousMuteKey);
            }

            int previousXr = SessionState.GetInt(PreviousXrKey, NotRemembered);
            if (previousXr == NotRemembered)
                return;
            XRGeneralSettings settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            if (settings)
                settings.InitManagerOnStart = previousXr == 1;
            SessionState.EraseInt(PreviousXrKey);
        }
    }
}
#endif
