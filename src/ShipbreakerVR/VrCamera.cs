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

        // Player size relative to the world. Scaling the rig also scales eye distance and tracked head movement.
        public static float WorldScale => Mathf.Clamp(Plugin.WorldScale.Value, 0.5f, 3f);

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

        // Switches to another game camera (main menu -> game): remove the old rig, re-enable the camera.
        public void Detach()
        {
            if (gameCamera != null) gameCamera.enabled = true;
            if (Instance == this) Instance = null;
            Destroy(rig.gameObject);
        }

        public bool BodyFollowsHead => VrXr.IsRunning && Plugin.BodyFollowsHead.Value;

        public static void Attach(Camera gameCamera)
        {
            // The rig is deliberately not a child of the game camera: when the body follows the head, rotation would otherwise apply twice.
            var rig = new GameObject("ShipbreakerVR Rig").transform;
            rig.SetPositionAndRotation(gameCamera.transform.position, gameCamera.transform.rotation);

            var head = new GameObject("ShipbreakerVR Head");
            head.transform.SetParent(rig, false);
            var vr = head.AddComponent<VrCamera>();
            vr.gameCamera = gameCamera;
            vr.rig = rig;
            // Only the heading: a tilted menu camera would otherwise tilt the whole room in VR.
            vr.Playspace = Quaternion.Euler(0f, gameCamera.transform.eulerAngles.y, 0f);
            Instance = vr;

            vr.vrCamera = head.AddComponent<Camera>();
            vr.vrCamera.CopyFrom(gameCamera);
            vr.vrCamera.ResetProjectionMatrix();
            vr.vrCamera.ResetWorldToCameraMatrix();
            vr.vrCamera.nearClipPlane = 0.05f;
            vr.vrCamera.stereoTargetEye = StereoTargetEyeMask.Both;
            vr.vrCamera.enabled = false;

            // Copy the game camera's HDRP settings (volumes, exposure, frame settings).
            var gameHd = gameCamera.GetComponent<HDAdditionalCameraData>();
            var vrHd = head.AddComponent<HDAdditionalCameraData>();
            if (gameHd != null) gameHd.CopyTo(vrHd);
            vrHd.xrRendering = true;
            vr.vrHd = vrHd;
            vr.gameAntialiasing = vrHd.antialiasing;

            var pose = head.AddComponent<TrackedPoseDriver>();
            pose.SetPoseSource(TrackedPoseDriver.DeviceType.GenericXRDevice, TrackedPoseDriver.TrackedPose.Center);
            pose.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            pose.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

            VrUi.Attach(vr);
            VrHelmet.Attach(vr);

            Plugin.Log.LogInfo($"VR camera created, following the game camera (HDRP data copied: {gameHd != null})");
        }

        private void OnEnable() => Application.onBeforeRender += FollowGameCamera;

        private void OnDisable() => Application.onBeforeRender -= FollowGameCamera;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (VrXr.IsRunning && Input.GetKeyDown(KeyCode.F4)) Recenter();
        }

        private void LateUpdate()
        {
            // The game camera belongs to the scene; when it goes, the rig goes too.
            if (gameCamera == null)
            {
                Destroy(rig.gameObject);
                return;
            }

            var running = VrXr.IsRunning;
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
                gameCamera.enabled = gameCamera != LynxCameraController.MainCamera || GameSession.CurrentGameState != GameSession.GameState.Hab;
                shrinkAt = Time.unscaledTime + 2f;
            }
            wasRunning = running;
            ShrinkRenderTargets(running);

            if (running)
            {
                ApplyQuality();
                LogEyeSeparation();
            }
            vrCamera.enabled = running;
            if (running) gameCamera.enabled = false;

            if (recenterAt > 0 && Time.unscaledTime >= recenterAt)
            {
                recenterAt = -1f;
                Recenter();
            }

            FollowGameCamera();
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
            // Each change reallocates every render target. Wait until the slider has rested, otherwise dragging it
            // through all steps fragments video memory until allocations fail and the image smears.
            var max = MaxResolutionScale();
            if (max > 0f && Plugin.ResolutionScale.Value > max)
            {
                Plugin.ResolutionScale.Value = max;
                VrOptions.Refresh(Plugin.ResolutionScale);
            }
            var scale = Mathf.Clamp(Plugin.ResolutionScale.Value, 0.5f, max > 0f ? max : 1f);
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
                    Plugin.ResolutionScale.Value = current;
                    pendingResolution = current;
                    VrOptions.Refresh(Plugin.ResolutionScale);
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
            else
            {
                width = gameCamera.pixelWidth;
                height = gameCamera.pixelHeight;
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
            var cam = gameCamera.transform;
            if (!BodyFollowsHead) Playspace = cam.rotation * recenterRotation;
            var scale = WorldScale;
            rig.localScale = new Vector3(scale, scale, scale);
            rig.rotation = Playspace;
            rig.position = cam.position - Playspace * (recenterPosition * scale);
        }

        // Puts the current head position on the game camera; without body-follows-head also the view direction (yaw only).
        private void Recenter()
        {
            var head = transform;
            recenterPosition = head.localPosition;
            recenterRotation = Quaternion.Inverse(Quaternion.Euler(0f, head.localEulerAngles.y, 0f));
            if (VrUi.Instance != null) VrUi.Instance.Recenter();
            Plugin.Log.LogInfo($"Head recentered (tracked head position {head.localPosition})");
        }
    }
}
