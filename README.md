# TrIVA-Unity

**An Intelligent Virtual Agent (IVA) pipeline built for developing and studying trustworthy IVAs in XR.**

Unity XR client of TrIVA, an embodied AI agent in VR/AR. LiveKit carries audio and data between this client and the Python backend in [TrIVA-Backend](https://github.com/TrIVAs-XR/TrIVA-Backend); the agent's speech drives lip sync, blinking and gaze on a humanoid avatar.

Besides audio, client and backend exchange messages over the LiveKit data channel in Pipecat's RTVI format. `LiveKitRtviClient` in the agent package sends the handshake and gives any component `Send` and a `MessageReceived` event, so a scenario can tell the agent what the participant does and react to what the agent sends. The message types are listed in the backend's [Unity contract](https://github.com/TrIVAs-XR/TrIVA-Backend/blob/main/ARCHITECTURE.md#unity-contract).

This repository is a complete Unity project: clone it, open it, import the Rocketbox Agent sample and press Play.

![The agent package holds the agent and its LiveKit connection; the Rocketbox Agent sample holds the avatar, the demo scene and optional scenarios; the LiveKit server links the client to the backend.](docs/overview.svg)

## Requirements

- **Unity 6000.3 LTS** (the project is saved with 6000.3.28f1) or newer, installed through Unity Hub, with URP, XR Interaction Toolkit 3.7.0 and OpenXR (all already set up in this project). The TrIVA Agent package also works in newer Unity 6 versions such as 6000.7.
- **Git**. The LiveKit Unity SDK and uLipSync are pulled from Git repositories when the project opens.
- **A PC VR headset** on an OpenXR runtime, such as Meta Quest Link or SteamVR, and a microphone.
- **The backend**, [TrIVA-Backend](https://github.com/TrIVAs-XR/TrIVA-Backend), which serves LiveKit tokens and runs the agent. The token endpoint is set in `Packages/io.github.trivas-xr.agent/Runtime/Config/TokenSourceConfig.asset` (default `http://localhost:8080/getToken`).

## Getting started

1. Clone this repository and open the folder in Unity Hub. The first import resolves the Git packages and takes a while.
2. Open **Window > Package Manager**, select **TrIVA Agent** under *In Project*, open its **Samples** tab and import **Rocketbox Agent**. This copies the sample to `Assets/Samples/TrIVA Agent/`.
3. Start the backend with the `chat` scenario. Its `lab` scenario greets and chats, but its task runs on game snapshots, which the demo does not send.

   ```powershell
   uv run python -m agent.run --scenario chat
   ```

4. Open `Assets/Samples/TrIVA Agent/<version>/Rocketbox Agent/Rocketbox Agent Demo.unity` and press Play.

The demo scene has an XR setup, the Rocketbox avatar Female_Adult_01 as the agent, the LiveKit connection and a world-space debug panel. The imported sample belongs to the project and can be changed freely: the avatar swapped, objects added, further scenes and scenarios built next to it.

![The demo scene in Play mode: the debug panel with connection state, microphone and the RTVI feed next to the Rocketbox avatar.](docs/demo.jpg)

More free avatars are available in the [Microsoft Rocketbox](https://github.com/microsoft/Microsoft-Rocketbox) repository (MIT), with the same skeleton and blendshape naming as the sample avatar. Lip sync, blinking and gaze need the variants with facial blendshapes (`_facial`).

## Where the files are

Unity projects keep their own files in `Assets/`, but they can also hold code in `Packages/`. This project does both. In the Project window, the agent code appears under **Packages > TrIVA Agent**, not under Assets, and opens and edits there like any other file.

| Path | Contents |
|---|---|
| `Packages/io.github.trivas-xr.agent/` | The agent code, shared by every scene and project that uses the agent |
| `Assets/Samples/TrIVA Agent/` | The imported Rocketbox Agent sample: avatar, materials, animations, animator and demo scene |
| `Assets/Scenes/LiveKitConnectionTest.unity` | Minimal scene that only connects to LiveKit, for testing the backend |
| `Assets/Samples/XR Interaction Toolkit/`, `Assets/Samples/XR Hands/` | Samples of those packages; Starter Assets and World Space UI are used by the demo scene and the debug panel |

The agent code inside `Packages/io.github.trivas-xr.agent/`:

| Folder | Contents |
|---|---|
| `Runtime/Connection/` | LiveKit connection with retry, agent audio playback, microphone publishing with push-to-talk and mute, the RTVI message channel |
| `Runtime/Agent/` | Agent state machine, behaviour driver (animation and gaze), blinking, lip sync binding, face rig profile, random animation variants, foot planting, looking and pointing at scene targets |
| `Runtime/Debugging/` | World-space debug panel and an Editor-only recorder for RTVI messages |
| `Runtime/UI/` | Layout, theme and fonts of the debug panel |
| `Runtime/Utils/` | Inspector button attribute and a singleton base class |
| `Runtime/Prefabs/` | `LiveKitConnector` and `DebugLogUI` prefabs |
| `Runtime/Config/` | The token endpoint |
| `Runtime/Scenes/` | Additive scene loading |
| `Editor/` | Inspector buttons and the additive scene opener |
| `Tests/` | PlayMode tests against a fake RTVI backend |
| `Samples~/` | Source of the Rocketbox Agent sample; hidden from Unity until imported |

## Looking and pointing

The agent can refer to objects in the scene by looking at them, pointing at them, or both.

- **`AgentTarget`** marks an object the agent can refer to. It has an ID to refer to it by, and optional anchors for the points the agent looks and points at.
- **`ReferenceController`** sits on the agent and makes it refer to a target with `Refer(target, look, point, duration)`. In Play mode, its **Refer** button refers to a random target and each target's **Agent Refers Here** button to that one.
- **`GazeController`** turns eyes and head to the target while the agent looks at it, then hands the gaze back to the agent's state.
- **`PointingController`** plays a pointing clip toward the target and turns torso, wrist and arm on top of it so the index finger lines up with the target. Targets far to the left are pointed at with the mirrored left arm; targets too far to the side or behind the agent are not pointed at.

`PointingController` needs a Humanoid avatar and these in the agent's animator controller, under the default names that its Inspector can change:

- A layer `Pointing` with an empty default state `Idle` and a state `Point`. `Idle` goes to `Point` on the trigger, and `Point` returns to `Idle` at its exit time.
- A trigger `Point`, a float `PointYaw`, a bool `PointMirror` and a float `PointSpeed` (default 1).
- `Point`'s motion is a pointing clip of the right arm, or a blend tree over `PointYaw` with clips pointing in several directions (degrees, positive to the agent's right). The state's Mirror parameter is `PointMirror` and its Speed multiplier is `PointSpeed`.

The Rocketbox Agent sample points with a single clip and lets the corrections do the aiming.

## LiveKit pin

LiveKit is pinned in `Packages/manifest.json` to an upstream `main` commit that includes the fix for entering Play mode without a domain reload ([livekit/client-sdk-unity#396](https://github.com/livekit/client-sdk-unity/pull/396)), until a release includes that fix.

## Using the agent in another Unity project

Optional, for adding the agent to an existing project instead of working in this one. Add the LiveKit Unity SDK and uLipSync Git packages from this project's `Packages/manifest.json` to that project's manifest (Unity packages cannot declare Git dependencies themselves), then the agent package, either as a local path (`"file:<path to>/Packages/io.github.trivas-xr.agent"`) or as a Git URL with `?path=/Packages/io.github.trivas-xr.agent`. Import the XR Interaction Toolkit samples **Starter Assets** and **World Space UI** there too.