using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SpatialTracking;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // A stereo camera sits on its own rig that follows the game camera's position and applies head rotation and head position.
    // The rig's rotation is the "playspace": VrBody sets the body to playspace * head every frame, so the body always
    // follows the head (the option to turn that off, letting the head look around freely, was dropped).
    // The game camera stays the tools' aiming camera, but does not render while VR is running.
    internal class VrCamera : MonoBehaviour
    {
        static VrCamera() => VrSession.OnUnload("VR camera", Unload);

        private const float RecenterDelay = 0.5f;

        private HDAdditionalCameraData vrHd;
        private VrHelmet helmet;

        public static VrCamera Instance { get; private set; }

        private Camera gameCamera;
        private Camera vrCamera;
        private Transform rig;
        private float recenterAt = -1f;
        private bool wasRunning;

        // Tracked head position that should coincide with the game camera (F4).
        private Vector3 recenterPosition;

        // Yaw that turns the head to the game camera's view direction in the Hab (F4).
        private Quaternion recenterRotation = Quaternion.identity;

        // Rotation of the playspace in the world; the head looks along Playspace * HeadRotation.
        public Quaternion Playspace { get; set; } = Quaternion.identity;

        public Quaternion HeadRotation => transform.localRotation;

        public Camera Camera => vrCamera;

        public Camera GameCamera => gameCamera;

        public bool WorldHidden => worldHidden;

        // True while the rig follows the Hab's own camera instead of the player's.
        public bool FollowsHab { get; private set; }

        // Player size relative to the world. Scaling the rig also scales eye distance and tracked head movement.
        public static float WorldScale => VrSettings.WorldScale.Value;

        // Shift of the eyes against the game camera, in meters of the player's size: right (along the head's heading, so
        // it stays on the same side of the body when turning) and up. For players whose head sits off center or too low.
        public static Vector3 HeadOffset => new Vector3(VrSettings.HeadSideways.Value * 0.01f, VrSettings.HeadHeight.Value * 0.01f, 0f);

        // Range of the head offset settings, in centimeters either way.
        public const float HeadOffsetRange = 10f;

        // The head offset in the world: how far the eyes sit from where the game puts them (the helmet stays there).
        public Vector3 WorldHeadOffset => Playspace * (Yaw(HeadRotation) * HeadOffset) * WorldScale;

        private float loggedScale = -1f;

        public bool BodyFollowsHead => VrXr.IsRunning;

        // True while loading (no game camera, or the game's load screen covers the image): only the UI canvas on black.
        private bool worldHidden;

        // Creates the rig on first use, afterwards switches it to another game camera (main menu -> game).
        // gameCamera may be null (loading screen, intro without a camera yet).
        public static void Attach(Camera gameCamera)
        {
            if (Instance != null)
            {
                Instance.Retarget(gameCamera);
                return;
            }

            // The rig is deliberately not a child of the game camera: when the body follows the head, rotation would otherwise apply twice.
            // It survives scene changes, because while the game loads there is no camera and the headset still needs an image.
            var rig = new GameObject("ShipbreakerVR Rig").transform;
            DontDestroyOnLoad(rig.gameObject);

            var head = new GameObject("ShipbreakerVR Head");
            head.transform.SetParent(rig, false);
            var vr = head.AddComponent<VrCamera>();
            vr.rig = rig;
            Instance = vr;

            vr.vrCamera = head.AddComponent<Camera>();
            vr.vrCamera.enabled = false;
            vr.vrHd = head.AddComponent<HDAdditionalCameraData>();

            var pose = head.AddComponent<TrackedPoseDriver>();
            pose.SetPoseSource(TrackedPoseDriver.DeviceType.GenericXRDevice, TrackedPoseDriver.TrackedPose.Center);
            pose.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            pose.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

            vr.Retarget(gameCamera);
            VrUi.Attach(vr);
            vr.helmet = VrHelmet.Attach(vr);
            VrPerformance.Attach(vr);
        }

        private void Retarget(Camera camera)
        {
            // Hand the screen back to the previous camera if it still exists and the game wants it on. While VR runs it
            // stays off until VR stops, like the other cameras VrCameraSilencer switches off: every active camera
            // without a target texture also renders into the headset.
            if (gameCamera != null && gameCamera != camera)
            {
                if (VrXr.IsRunning) VrCameraSilencer.Keep(gameCamera);
                else gameCamera.enabled = VrCameraSilencer.GameWantsEnabled(gameCamera);
            }
            gameCamera = camera;
            FollowsHab = VrHab.IsHabCamera(camera);
            if (camera == null)
            {
                HideWorld();
                return;
            }
            worldHidden = false;

            rig.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
            // Only the heading: a tilted menu camera would otherwise tilt the whole room in VR.
            Playspace = Quaternion.Euler(0f, camera.transform.eulerAngles.y, 0f);

            vrCamera.CopyFrom(camera);
            // Nothing of the game is on our pass layer, but the screen fades hidden from the menu canvas are (VrCanvases).
            vrCamera.cullingMask &= ~(1 << VrUi.PassLayer);
            vrCamera.ResetProjectionMatrix();
            vrCamera.ResetWorldToCameraMatrix();
            vrCamera.nearClipPlane = 0.05f;
            vrCamera.stereoTargetEye = StereoTargetEyeMask.Both;
            vrCamera.targetTexture = null;

            // Copy the game camera's HDRP settings (volumes, exposure, frame settings).
            var gameHd = camera.GetComponent<HDAdditionalCameraData>();
            if (gameHd != null) gameHd.CopyTo(vrHd);
            else vrHd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
            // Our UI pass must stay visible whatever volume layers the game camera uses.
            vrHd.volumeLayerMask |= 1 << VrUi.PassLayer;
            vrHd.xrRendering = true;
            VrResolution.Retargeted(vrHd.antialiasing);

            // Like starting VR: recenter on the new camera and let the body sync again.
            if (VrXr.IsRunning)
            {
                recenterAt = Time.unscaledTime + RecenterDelay;
                VrBody.Resync();
            }

            Plugin.Log.LogInfo($"VR camera follows '{camera.name}' (HDRP data copied: {gameHd != null})");
            VrPerformance.LogOtherCameras(camera);
            VrRenderInfo.Schedule();
        }

        // Nothing of the (half-loaded) world, only black behind the UI canvas. Retarget restores the camera settings.
        private void HideWorld()
        {
            worldHidden = true;
            vrCamera.cullingMask = 0;
            vrCamera.clearFlags = CameraClearFlags.SolidColor;
            vrCamera.backgroundColor = Color.black;
            vrHd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            vrHd.backgroundColorHDR = Color.black;
            // Only our UI pass: no post-processing volumes of the level or the load screen on the black.
            vrHd.volumeLayerMask = 1 << VrUi.PassLayer;
            vrHd.xrRendering = true;
            Plugin.Log.LogInfo(gameCamera == null ? "No game camera (loading): VR shows only the UI" : "Load screen: VR shows only the UI");
            VrResolution.Loading();
        }

        private void OnEnable()
        {
            Application.onBeforeRender += FollowGameCamera;
            RenderPipelineManager.beginCameraRendering += VrLoadTrace.OnBeginCamera;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= FollowGameCamera;
            RenderPipelineManager.beginCameraRendering -= VrLoadTrace.OnBeginCamera;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // Before the plugin unloads (ScriptEngine reload): gives the cameras, the Hab and the truck viewer back to the
        // game, as when VR stops, and removes the rig with everything on it (UI, helmet, passes, volumes). Right away,
        // so nothing of it is left when the new instance starts.
        public static void Unload()
        {
            var vr = Instance;
            if (vr != null)
            {
                if (vr.gameCamera != null) vr.gameCamera.enabled = VrCameraSilencer.GameWantsEnabled(vr.gameCamera);
                VrCameraSilencer.Restore();
                VrHab.Calm(false);
                VrHab.CutBlends(false);
                VrPerformance.Update(false, null, false, true);
                DestroyImmediate(vr.rig.gameObject);
            }
            // Attach creates the frame statistics with the rig; they go with it.
            VrPerformance.Detach();
        }

        private void Update()
        {
            VrFrame.Update();
            if (VrXr.IsRunning && VrSettings.Pressed(VrSettings.CenterKey)) Recenter(true);
            UpdateCenterPress();
        }

        private void FixedUpdate() => VrFrame.Fixed();

        private void LateUpdate()
        {
            VrFrame.Late();
            var running = VrXr.IsRunning;
            // The game may switch to the Hab and hide its load screen in the same frame, after Plugin.Update looked.
            // Without this the bay showed for a frame and the resolution went up for it.
            var hab = running ? VrHab.Camera : null;
            if (hab != null && hab != gameCamera)
            {
                Plugin.Log.LogInfo($"VR camera attached to the Hab camera '{hab.name}'");
                Retarget(hab);
                VrDiagnostics.Schedule(6f, "Hab entered 6 s ago");
            }

            // The game camera belongs to a scene and goes with it; the rig stays until the next camera.
            // In between, and while the game's load screen covers the image, only the UI shows.
            var hideWorld = gameCamera == null || (SceneLoader.Instance != null && SceneLoader.Instance.LoadScreenVisible);
            if (hideWorld != worldHidden)
            {
                if (hideWorld) HideWorld();
                else Retarget(gameCamera);
            }
            VrLoadTrace.Track(running && hideWorld);

            if (running && !wasRunning)
            {
                recenterAt = Time.unscaledTime + RecenterDelay;
                VrBody.Resync();
                VrResolution.Started();
            }
            // Don't blindly re-enable on stop: in the Hab the game disabled its camera itself.
            if (!running && wasRunning)
            {
                if (gameCamera != null) gameCamera.enabled = VrCameraSilencer.GameWantsEnabled(gameCamera);
                VrResolution.Stopped();
            }
            wasRunning = running;
            VrCameraSilencer.Update(running, vrCamera, gameCamera);
            VrHab.Calm(running && FollowsHab);
            VrHab.CutBlends(running && FollowsHab);
            VrPerformance.Update(running, gameCamera, FollowsHab, worldHidden);
            VrResolution.Update(running, vrHd, gameCamera, FollowsHab, worldHidden);

            if (running)
            {
                // The near plane scales with the player, so close things are cut off as far as before.
                vrCamera.nearClipPlane = 0.05f * WorldScale;
                LogEyeSeparation();
                HdrpXrSettings.Update(VrSettings.AntiAliasing.Value == VrResolution.AntiAliasing.TAA);
                HdrpXrSettings.CheckSlices();
                VrRenderInfo.Update(this);
            }
            vrCamera.enabled = running;
            if (running && gameCamera != null) gameCamera.enabled = false;

            if (recenterAt > 0 && Time.unscaledTime >= recenterAt)
            {
                recenterAt = -1f;
                Recenter(false);
            }

            FollowGameCamera();
            // The UI and the helmet sit on this object too; called from here, their order no longer depends on Unity's.
            if (VrUi.Instance != null) VrUi.Instance.Late();
            if (helmet != null) helmet.Late();
        }

        private static readonly List<XRDisplaySubsystem> Displays = new List<XRDisplaySubsystem>();

        // Logs once per scale how far apart the eyes are in the world, to confirm the scale reaches the stereo rendering.
        // Reads the view matrices HDRP renders with; they only exist once the display has render passes, so retry each frame.
        private void LogEyeSeparation()
        {
            var scale = WorldScale;
            if (Mathf.Approximately(scale, loggedScale)) return;
            SubsystemManager.GetSubsystems(Displays);
            if (Displays.Count == 0) return;
            var display = Displays[0];
            Vector3? first = null;
            for (var p = 0; p < display.GetRenderPassCount(); p++)
            {
                display.GetRenderPass(p, out var pass);
                for (var i = 0; i < pass.GetRenderParameterCount(); i++)
                {
                    pass.GetRenderParameter(vrCamera, i, out var param);
                    Vector3 eye = param.view.inverse.GetColumn(3);
                    if (first == null)
                    {
                        first = eye;
                        continue;
                    }
                    // Before the first tracked frame both eyes sit at the same spot; wait for real poses.
                    var separation = Vector3.Distance(first.Value, eye);
                    if (separation < 0.001f) return;
                    loggedScale = scale;
                    Plugin.Log.LogInfo($"World scale {scale:0.00}: eye separation in the world {separation * 1000f:0} mm");
                    return;
                }
            }
        }

        // Once more right before rendering, because Cinemachine sets the game camera late in the frame.
        private void FollowGameCamera()
        {
            if (gameCamera == null || rig == null) return;
            // Hab camera moves become fades in VR: until the view is black the rig stays on the old spot. At the monitor
            // above the bed it stays where it was.
            VrHab.UpdateTransition();
            if (FollowsHab && (VrHab.Holding || VrHab.Staying)) return;
            var cam = gameCamera.transform;
            // At a monitor in the Hab the head goes in front of it instead of right up to it (VrHab.Monitor).
            var position = cam.position;
            if (FollowsHab && VrHab.Monitor != null) position = VrHab.MonitorViewpoint(VrHab.Monitor, position);
            // The Hab camera swings between bed, kitchen and terminal; the playspace turns along, but only around the
            // vertical axis: its pitch would tilt the room. Looking up or down is up to the head. Except lying in bed.
            if (FollowsHab) Playspace = HabHeading(cam.rotation) * recenterRotation;
            // Cinemachine sets the lens every frame: in the Hab the rooms reach 500 m, the view out of the window
            // 5000 m (ship and dock). Copied only once, the ship stayed cut off at 500 m. In the Hab at least as far
            // as its farthest view: at the terminal (2555 m) the earth outside was cut off depending on the head.
            vrCamera.farClipPlane = FollowsHab ? Mathf.Max(gameCamera.farClipPlane, VrHab.FarClip) : gameCamera.farClipPlane;
            var scale = WorldScale;
            rig.localScale = new Vector3(scale, scale, scale);
            rig.rotation = Playspace;
            rig.position = position - Playspace * (recenterPosition * scale) + WorldHeadOffset;
            VrFrame.RigPlaced();
        }

        // Lying in bed (waking up, the monitor above the bed) the Hab camera looks up at the ceiling. There the playspace
        // tilts along, as if lying on one's back: looking straight ahead shows what the flat game shows, the monitor
        // with its menu. The tilt changes only during the fade between two spots. Elsewhere only the heading.
        private const float LyingPitch = 30f;

        private static Quaternion HabHeading(Quaternion rotation)
        {
            var up = VrHab.RoomUp;
            var forward = rotation * Vector3.forward;
            if (Vector3.Dot(forward, up) > Mathf.Sin(LyingPitch * Mathf.Deg2Rad)) return Quaternion.LookRotation(forward, up);
            // Its heading in the room, standing on the room's floor: the Hab is tilted in the world.
            var ahead = Vector3.ProjectOnPlane(forward, up);
            if (ahead.sqrMagnitude < 1e-4f) ahead = Vector3.ProjectOnPlane(rotation * Vector3.up, up);
            return ahead.sqrMagnitude < 1e-8f ? Yaw(rotation) : Quaternion.LookRotation(ahead, up);
        }

        // Heading of a rotation (turn around the world's up axis), from where it looks or, looking straight down or up, where its top points.
        private static Quaternion Yaw(Quaternion rotation)
        {
            var forward = rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = rotation * Vector3.up;
                forward.y = 0f;
            }
            return forward.sqrMagnitude < 1e-8f ? Quaternion.identity : Quaternion.LookRotation(forward, Vector3.up);
        }

        // Pressing the left stick: the same as F4. Nothing else uses that press (VrMenuInput sends no L3).
        private void UpdateCenterPress()
        {
            if (!VrXr.IsRunning || !VrInput.Left.StickClick.Pressed) return;
            Plugin.Log.LogInfo("Left stick pressed: centering the view");
            Recenter(true);
        }

        // "Center view" in the VR options: the same as F4.
        public static void RecenterView()
        {
            if (VrXr.IsRunning && Instance != null) Instance.Recenter(true);
        }

        // Puts the current head position on the game camera.
        // With F4 (toGaze) the view direction is also centered in menus, where the body does not turn with the head.
        // The menu canvas is placed in front of the head again, upright at eye height.
        private void Recenter(bool toGaze)
        {
            var head = transform;
            recenterPosition = head.localPosition;
            var headYaw = Quaternion.Euler(0f, head.localEulerAngles.y, 0f);
            recenterRotation = Quaternion.Inverse(headYaw);
            var gameplay = GameSession.CurrentGameState == GameSession.GameState.Gameplay && gameCamera == LynxCameraController.MainCamera;
            if (toGaze && !gameplay && gameCamera != null)
            {
                Playspace = Quaternion.Euler(0f, gameCamera.transform.eulerAngles.y, 0f) * Quaternion.Inverse(headYaw);
                // Back in gameplay the body takes its playspace from where it actually points.
                VrBody.Resync();
            }
            if (VrUi.Instance != null) VrUi.Instance.Recenter();
            Plugin.Log.LogInfo($"Head recentered{(toGaze ? " (F4, left stick or options)" : "")} (tracked head position {head.localPosition})");
        }
    }
}
