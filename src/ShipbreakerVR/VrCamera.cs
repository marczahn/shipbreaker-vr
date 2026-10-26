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
    // The rig's rotation is the "playspace": with body-follows-head, VrBody sets the body to playspace * head every frame,
    // otherwise the playspace simply turns with the game camera (head looks freely, mouse turns the body).
    // The game camera stays the tools' aiming camera, but does not render while VR is running.
    internal class VrCamera : MonoBehaviour
    {
        private const float RecenterDelay = 0.5f;

        public enum AntiAliasing { TAA, SMAA, FXAA, Keine, Spiel }

        private HDAdditionalCameraData vrHd;
        private HDAdditionalCameraData.AntialiasingMode gameAntialiasing;

        public static VrCamera Instance { get; private set; }

        private Camera gameCamera;
        private Camera vrCamera;
        private Transform rig;
        private float recenterAt = -1f;
        private bool wasRunning;

        // Tracked head position that should coincide with the game camera (F4).
        private Vector3 recenterPosition;

        // Yaw that, without body-follows-head, turns the head to the game camera's view direction (F4).
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
        public static float WorldScale => Mathf.Clamp(Plugin.WorldScale.Value, 0.5f, 3f);

        // Shift of the eyes against the game camera, in meters of the player's size: right (along the head's heading, so
        // it stays on the same side of the body when turning) and up. For players whose head sits off center or too low.
        public static Vector3 HeadOffset => new Vector3(
            Mathf.Clamp(Plugin.HeadSideways.Value, -HeadOffsetRange, HeadOffsetRange) * 0.01f,
            Mathf.Clamp(Plugin.HeadHeight.Value, -HeadOffsetRange, HeadOffsetRange) * 0.01f, 0f);

        public const float HeadOffsetRange = 10f;

        // The head offset in the world: how far the eyes sit from where the game puts them (the helmet stays there).
        public Vector3 WorldHeadOffset => Playspace * (Yaw(HeadRotation) * HeadOffset) * WorldScale;

        private float loggedScale = -1f;

        // Rough estimate: HDRP needs about 250 bytes of render targets per pixel and eye (G-buffer, depth, color,
        // history, post effects, and the old targets while the new ones are allocated). Half of the video memory stays
        // free for the game's own textures and meshes. Calibrated on an RTX 4070 Ti (12 GB, 2688x2880 per eye):
        // 1.55 and 2.0 ran out of memory, 1.0 works; the estimate gives 1.25.
        private const float BytesPerPixelAndEye = 250f;
        private const float VideoMemoryShare = 0.5f;
        public const float ResolutionSliderMax = 2f;

        private static float maxResolutionScale = -1f;

        private float pendingResolution = -1f;
        private float resolutionChangedAt;
        private float shrinkAt = -1f;

        // Render resolution while only the loading UI shows: the headset's native one, so the load screen stays sharp
        // while the level being loaded gets the video memory (at 1.25x the level start ran out of it and crashed).
        private const float LoadingResolution = 1f;

        // Highest render resolution in the Hab. The bay stays loaded next to it, and at 1.25x the 12 GB card ran out of
        // video memory each time the Hab appeared (Failed to create RenderTexture ... 0x8007000e). It also leaves the
        // Hab more room to reach the headset's frame rate.
        private const float HabResolution = 1f;

        // The Hab costs the GPU more than the bay: on an RTX 4070 Ti at 2688x2880 per eye it ran at 45 fps at 1.0x and at
        // the headset's 90 at 0.7x. Below the headset's rate Virtual Desktop halves it and synthesizes every second frame,
        // and even a few missed frames (3 % at 0.8x) wobble close to the monitors. So the Hab has its own resolution
        // setting ([Bild] AufloesungStation), independent of the one for everywhere else, at most HabResolution.
        private static float HabScale => Mathf.Clamp(Plugin.StationResolution.Value, 0.5f, HabResolution);
        private bool habScaleLogged;

        public bool BodyFollowsHead => VrXr.IsRunning && Plugin.BodyFollowsHead.Value;

        // True while loading (no game camera, or the game's load screen covers the image): only the UI canvas on black.
        private bool worldHidden;

        private const int UiLayer = 5;

        // UI-only game cameras switched off while VR runs (see SilenceUiCameras), back on when it stops.
        private readonly HashSet<Camera> silenced = new HashSet<Camera>();
        private Camera[] cameraBuffer = new Camera[16];

        // A camera that draws nothing but the UI layer, like the load screen's. Not a view for the VR camera to follow.
        public static bool IsUiOnly(Camera camera) => camera.cullingMask != 0 && (camera.cullingMask & ~(1 << UiLayer)) == 0;

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
            VrHelmet.Attach(vr);
            VrPerformance.Attach(vr);
        }

        private void Retarget(Camera camera)
        {
            // Hand the screen back to the previous camera if it still exists and the game wants it on. While VR runs it
            // stays off until VR stops, like the cameras SilenceOtherCameras switches off: every active camera without a
            // target texture also renders into the headset. Loading the bay, the rig first follows a 'Main Camera' the
            // scene brings along, then LynxCameraController's; the first one, switched back on, rendered next to the
            // rig behind the load screen (log: Other cameras rendering next to 'Main Camera': 'Main Camera').
            if (gameCamera != null && gameCamera != camera)
            {
                if (VrXr.IsRunning)
                {
                    gameCamera.enabled = false;
                    silenced.RemoveWhere(c => c == null);
                    silenced.Add(gameCamera);
                }
                else gameCamera.enabled = GameWantsEnabled(gameCamera);
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
            gameAntialiasing = vrHd.antialiasing;

            // Like starting VR: recenter on the new camera and let the body sync again.
            if (VrXr.IsRunning)
            {
                recenterAt = Time.unscaledTime + RecenterDelay;
                VrBody.Resync();
            }

            Plugin.Log.LogInfo($"VR camera follows '{camera.name}' (HDRP data copied: {gameHd != null})");
            VrPerformance.LogOtherCameras(camera);
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

            var current = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
            if (VrXr.IsRunning && current > LoadingResolution + 0.01f)
            {
                UnityEngine.XR.XRSettings.eyeTextureResolutionScale = LoadingResolution;
                shrinkAt = Time.unscaledTime + 0.5f;
                Plugin.Log.LogInfo($"Render resolution per eye while loading: {LoadingResolution:0.00}x (was {current:0.00}x)");
            }
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
            if (vr == null) return;
            if (vr.gameCamera != null) vr.gameCamera.enabled = GameWantsEnabled(vr.gameCamera);
            vr.SilenceOtherCameras(false);
            VrHab.Calm(false);
            VrHab.CutBlends(false);
            VrPerformance.Update(false, null, false, true);
            DestroyImmediate(vr.rig.gameObject);
        }

        private void Update()
        {
            VrPerformance.FrameStart();
            VrLoadTrace.EndFrame(this);
            if (VrXr.IsRunning && Input.GetKeyDown(KeyCode.F4)) Recenter(true);
            UpdateCenterCombo();
            VrDiagnostics.Update(this);
        }

        private void LateUpdate()
        {
            VrToolModels.PoseArm();
            VrHands.LateUpdate();
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
                LowerResolutionAtStart();
                shrinkAt = Time.unscaledTime + 2f;
            }
            // Don't blindly re-enable on stop: in the Hab the game disabled its camera itself.
            if (!running && wasRunning)
            {
                if (gameCamera != null) gameCamera.enabled = GameWantsEnabled(gameCamera);
                shrinkAt = Time.unscaledTime + 2f;
            }
            wasRunning = running;
            SilenceOtherCameras(running);
            VrHab.Calm(running && FollowsHab);
            VrHab.CutBlends(running && FollowsHab);
            VrPerformance.Update(running, gameCamera, FollowsHab, worldHidden);
            ShrinkRenderTargets(running);

            if (running)
            {
                ApplyQuality();
                LogEyeSeparation();
            }
            vrCamera.enabled = running;
            if (running && gameCamera != null) gameCamera.enabled = false;

            if (recenterAt > 0 && Time.unscaledTime >= recenterAt)
            {
                recenterAt = -1f;
                Recenter(false);
            }

            FollowGameCamera();
        }

        // Whether the game itself has this camera on. It switches the main camera off while it shows the Hab with its own.
        private static bool GameWantsEnabled(Camera camera) => camera != LynxCameraController.MainCamera || !VrHab.MainCameraOff;

        // Every active game camera without a target texture also renders into the headset (HDRP's XRSystem only checks
        // that and xrRendering). The load screen's camera (depth 10, after ours) thus covered our image with its grey
        // background and its CRT and scanline effects, which are not made for VR. In VR its canvas is on our UI canvas,
        // so UI-only cameras stay off while VR runs. So does the Hab's camera (depth 2) while the rig follows another
        // one: the Hab scene is loaded behind the load screen, and its camera painted the Hab over the load screen and
        // its "press a key" prompt, fixed to the face as if the game had frozen.
        private void SilenceOtherCameras(bool running)
        {
            if (!running)
            {
                if (silenced.Count == 0) return;
                foreach (var camera in silenced)
                    if (camera != null) camera.enabled = GameWantsEnabled(camera);
                silenced.Clear();
                return;
            }
            if (Camera.allCamerasCount > cameraBuffer.Length) cameraBuffer = new Camera[Camera.allCamerasCount * 2];
            var count = Camera.GetAllCameras(cameraBuffer);
            for (var i = 0; i < count; i++)
            {
                var camera = cameraBuffer[i];
                cameraBuffer[i] = null;
                if (camera == vrCamera || camera == gameCamera || camera.cameraType != CameraType.Game || camera.targetTexture != null) continue;
                if (camera.CompareTag("UI_Camera") || camera.name.StartsWith("ShipbreakerVR")) continue;
                var uiOnly = IsUiOnly(camera);
                if (!uiOnly && !VrHab.IsHabCamera(camera)) continue;
                var hd = camera.GetComponent<HDAdditionalCameraData>();
                if (hd != null && !hd.xrRendering) continue;
                camera.enabled = false;
                silenced.RemoveWhere(c => c == null);
                if (silenced.Add(camera)) Plugin.Log.LogInfo($"Camera '{camera.name}' ({(uiOnly ? "UI only" : "Hab")}, depth {camera.depth}) switched off while VR runs");
            }
        }

        private void ApplyQuality()
        {
            var mode = Plugin.AntiAliasing.Value switch
            {
                AntiAliasing.TAA => HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing,
                AntiAliasing.SMAA => HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing,
                AntiAliasing.FXAA => HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing,
                AntiAliasing.Keine => HDAdditionalCameraData.AntialiasingMode.None,
                _ => gameAntialiasing,
            };
            if (vrHd.antialiasing != mode)
            {
                vrHd.antialiasing = mode;
                Plugin.Log.LogInfo($"VR camera anti-aliasing: {mode} (game: {gameAntialiasing})");
            }
            // While loading the resolution stays at LoadingResolution (HideWorld); once the world shows again it goes
            // back to the configured one, through the memory probe below.
            if (worldHidden) return;
            // Each change reallocates every render target. Wait until the slider has rested, otherwise dragging it
            // through all steps fragments video memory until allocations fail and the image smears.
            var max = MaxResolutionScale();
            if (max > 0f && Plugin.ResolutionScale.Value > max)
            {
                Plugin.ResolutionScale.Value = max;
                VrOptions.Refresh(Plugin.ResolutionScale);
            }
            var scale = Mathf.Clamp(FollowsHab ? HabScale : Plugin.ResolutionScale.Value, 0.5f, max > 0f ? max : 1f);
            if (FollowsHab != habScaleLogged)
            {
                habScaleLogged = FollowsHab;
                if (FollowsHab) Plugin.Log.LogInfo($"Render resolution in the Hab: {scale:0.00}x (station setting, at most {HabResolution:0.00}x since the bay stays in video memory next to it)");
            }
            if (!Mathf.Approximately(scale, pendingResolution))
            {
                pendingResolution = scale;
                resolutionChangedAt = Time.unscaledTime;
            }
            if (Time.unscaledTime - resolutionChangedAt < 1f) return;
            var current = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
            if (!Mathf.Approximately(current, scale))
            {
                // Going up: first check that the new targets can be allocated while the old ones still exist,
                // otherwise stay at the previous value instead of ending up with a black or smeared image.
                if (scale > current && !FitsIntoMemory(scale))
                {
                    Plugin.Log.LogWarning($"Render resolution {scale:0.00}x does not fit into video memory, staying at {current:0.00}x");
                    var entry = FollowsHab ? Plugin.StationResolution : Plugin.ResolutionScale;
                    entry.Value = current;
                    pendingResolution = current;
                    VrOptions.Refresh(entry);
                    return;
                }
                UnityEngine.XR.XRSettings.eyeTextureResolutionScale = scale;
                Plugin.Log.LogInfo($"Render resolution per eye: {scale:0.00}x");
                if (scale < current) shrinkAt = Time.unscaledTime + 1f;
            }
        }

        // When VR starts the eye textures are created at 1.0 and, a second later, again at the configured scale.
        // During a mission load that second allocation ran out of video memory. So lower the scale right away;
        // raising above the current value still waits and goes through the memory probe in ApplyQuality.
        private void LowerResolutionAtStart()
        {
            var max = MaxResolutionScale();
            var scale = Mathf.Clamp(Plugin.ResolutionScale.Value, 0.5f, max > 0f ? max : 1f);
            var current = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
            if (scale >= current || Mathf.Approximately(scale, current)) return;
            UnityEngine.XR.XRSettings.eyeTextureResolutionScale = scale;
            Plugin.Log.LogInfo($"Render resolution per eye at VR start: {scale:0.00}x");
        }

        // HDRP's shared render targets only grow: they keep the largest width and height any camera rendered at
        // (RTHandleSystem), and the game never shrinks them. After VR starts or stops, or the render resolution goes
        // down, shrink them once to what is rendered now, so memory held for the flat view or a higher resolution is
        // freed. A camera that needs more simply grows them again.
        private void ShrinkRenderTargets(bool running)
        {
            if (shrinkAt < 0f || Time.unscaledTime < shrinkAt) return;
            shrinkAt = -1f;
            int width, height;
            if (running)
            {
                width = UnityEngine.XR.XRSettings.eyeTextureWidth;
                height = UnityEngine.XR.XRSettings.eyeTextureHeight;
            }
            else if (gameCamera != null)
            {
                width = gameCamera.pixelWidth;
                height = gameCamera.pixelHeight;
            }
            else
            {
                width = Screen.width;
                height = Screen.height;
            }
            int maxWidth = RTHandles.maxWidth, maxHeight = RTHandles.maxHeight;
            // Compare with what they grow to once this size renders, and skip resets that would free next to nothing.
            if (width <= 0 || height <= 0 || (float)Mathf.Max(maxWidth, width) * Mathf.Max(maxHeight, height) < 1.05f * width * height) return;
            // Only the shared targets: each camera's history goes away with the camera (HDCamera.CleanUnused), and
            // HDRenderPipeline.ResetRTHandleReferenceSize would also stretch other cameras' history to this size.
            RTHandles.ResetReferenceSize(width, height);
            Plugin.Log.LogInfo($"HDRP render targets reduced from {maxWidth}x{maxHeight} to {width}x{height} ({(running ? "eye" : "screen")} size)");
        }

        private static readonly List<XRDisplaySubsystem> Displays = new List<XRDisplaySubsystem>();

        // Highest render resolution that should fit into video memory, rounded down to 0.05; -1 until the headset's
        // native eye size is known.
        private static float MaxResolutionScale()
        {
            if (maxResolutionScale > 0f) return maxResolutionScale;
            var current = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
            var width = UnityEngine.XR.XRSettings.eyeTextureWidth / current;
            var height = UnityEngine.XR.XRSettings.eyeTextureHeight / current;
            if (width <= 0f || height <= 0f || SystemInfo.graphicsMemorySize <= 0) return -1f;
            var budget = SystemInfo.graphicsMemorySize * 1024f * 1024f * VideoMemoryShare;
            var fit = Mathf.Sqrt(budget / (width * height * 2f * BytesPerPixelAndEye));
            maxResolutionScale = Mathf.Clamp(Mathf.Floor(fit * 20f) / 20f, 0.5f, ResolutionSliderMax);
            Plugin.Log.LogInfo($"{SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsMemorySize} MB video memory, {width:0}x{height:0} per eye: render resolution limited to {maxResolutionScale:0.00}x");
            return maxResolutionScale;
        }

        // Allocates test textures as large as the render targets at this scale would be (about 160 bytes per pixel
        // and eye) and frees them again. Fails if the graphics driver refuses one of them.
        private static bool FitsIntoMemory(float scale)
        {
            var current = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
            var width = Mathf.CeilToInt(UnityEngine.XR.XRSettings.eyeTextureWidth / current * scale);
            var height = Mathf.CeilToInt(UnityEngine.XR.XRSettings.eyeTextureHeight / current * scale);
            if (width <= 0 || height <= 0) return true;
            const int bytesPerTexel = 8;
            var count = Mathf.CeilToInt(160f * 2f / bytesPerTexel);
            var probes = new List<RenderTexture>();
            var ok = true;
            try
            {
                for (var i = 0; i < count && ok; i++)
                {
                    var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf) { name = "ShipbreakerVR memory probe" };
                    probes.Add(rt);
                    ok = rt.Create();
                }
            }
            finally
            {
                foreach (var rt in probes)
                {
                    rt.Release();
                    Destroy(rt);
                }
            }
            Plugin.Log.LogInfo($"Video memory probe for {scale:0.00}x ({width}x{height}, {probes.Count} of {count} textures): {(ok ? "fits" : "too large")}");
            return ok;
        }

        // Logs once per scale how far apart the eyes are in the world, to confirm the scale reaches the stereo rendering.
        // Reads the view matrices HDRP renders with; they only exist once the display has render passes, so retry each frame.
        private void LogEyeSeparation()
        {
            var scale = WorldScale;
            vrCamera.nearClipPlane = 0.05f * scale;
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
            else if (!BodyFollowsHead) Playspace = cam.rotation * recenterRotation;
            // Cinemachine sets the lens every frame: in the Hab the rooms reach 500 m, the view out of the window
            // 5000 m (ship and dock). Copied only once, the ship stayed cut off at 500 m. In the Hab at least as far
            // as its farthest view: at the terminal (2555 m) the earth outside was cut off depending on the head.
            vrCamera.farClipPlane = FollowsHab ? Mathf.Max(gameCamera.farClipPlane, VrHab.FarClip) : gameCamera.farClipPlane;
            var scale = WorldScale;
            rig.localScale = new Vector3(scale, scale, scale);
            rig.rotation = Playspace;
            rig.position = position - Playspace * (recenterPosition * scale) + WorldHeadOffset;
            VrToolModels.Place();
        }

        // Lying in bed (waking up, the monitor above the bed) the Hab camera looks up at the ceiling. There the playspace
        // tilts along, as if lying on one's back: looking straight ahead shows what the flat game shows, the monitor
        // with its menu. The tilt changes only during the fade between two spots. Elsewhere only the heading.
        private const float LyingPitch = 30f;

        private static Quaternion HabHeading(Quaternion rotation)
        {
            var forward = rotation * Vector3.forward;
            return forward.y > Mathf.Sin(LyingPitch * Mathf.Deg2Rad) ? Quaternion.LookRotation(forward, Vector3.up) : Yaw(rotation);
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

        // B + Y together on the controllers: the same as F4. From the press until both buttons are up again they count as
        // the combo, so neither acts on its own meanwhile (Y brakes, B is Back in menus; VrControls, VrMenuInput).
        public static bool CenterComboHeld { get; private set; }
        private static InputDevice comboLeft;
        private static InputDevice comboRight;

        private void UpdateCenterCombo()
        {
            if (!VrXr.IsRunning)
            {
                CenterComboHeld = false;
                return;
            }
            var y = Pressed(ref comboLeft, XRNode.LeftHand);
            var b = Pressed(ref comboRight, XRNode.RightHand);
            if (y && b && !CenterComboHeld)
            {
                CenterComboHeld = true;
                Plugin.Log.LogInfo("B + Y pressed: centering the view");
                Recenter(true);
            }
            else if (!y && !b) CenterComboHeld = false;
        }

        private static bool Pressed(ref InputDevice device, XRNode node)
        {
            if (!device.isValid) device = InputDevices.GetDeviceAtXRNode(node);
            return device.isValid && device.TryGetFeatureValue(CommonUsages.secondaryButton, out var pressed) && pressed;
        }

        // "Center view" in the VR options: the same as F4.
        public static void RecenterView()
        {
            if (VrXr.IsRunning && Instance != null) Instance.Recenter(true);
        }

        // Puts the current head position on the game camera; without body-follows-head also the view direction (yaw only).
        // With F4 (toGaze) the view direction is also centered in menus, where the body does not turn with the head.
        // The menu canvas is placed in front of the head again, upright at eye height.
        private void Recenter(bool toGaze)
        {
            var head = transform;
            recenterPosition = head.localPosition;
            var headYaw = Quaternion.Euler(0f, head.localEulerAngles.y, 0f);
            recenterRotation = Quaternion.Inverse(headYaw);
            var gameplay = GameSession.CurrentGameState == GameSession.GameState.Gameplay && gameCamera == LynxCameraController.MainCamera;
            if (toGaze && BodyFollowsHead && !gameplay && gameCamera != null)
            {
                Playspace = Quaternion.Euler(0f, gameCamera.transform.eulerAngles.y, 0f) * Quaternion.Inverse(headYaw);
                // Back in gameplay the body takes its playspace from where it actually points.
                VrBody.Resync();
            }
            if (VrUi.Instance != null) VrUi.Instance.Recenter();
            Plugin.Log.LogInfo($"Head recentered{(toGaze ? " (F4, B + Y or options)" : "")} (tracked head position {head.localPosition})");
        }
    }
}
