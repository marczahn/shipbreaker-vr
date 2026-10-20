using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SpatialTracking;

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
            }
            // Don't blindly re-enable on stop: in the Hab the game disabled its camera itself.
            if (!running && wasRunning)
                gameCamera.enabled = gameCamera != LynxCameraController.MainCamera || GameSession.CurrentGameState != GameSession.GameState.Hab;
            wasRunning = running;

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
            var scale = Mathf.Clamp(Plugin.ResolutionScale.Value, 0.5f, 2f);
            if (!Mathf.Approximately(UnityEngine.XR.XRSettings.eyeTextureResolutionScale, scale))
            {
                UnityEngine.XR.XRSettings.eyeTextureResolutionScale = scale;
                Plugin.Log.LogInfo($"Render resolution per eye: {scale:0.00}x");
            }
        }

        // Logs once per scale how far apart the eyes are in the world, to confirm the scale reaches the stereo rendering.
        private void LogEyeSeparation()
        {
            var scale = WorldScale;
            if (Mathf.Approximately(scale, loggedScale)) return;
            loggedScale = scale;
            vrCamera.nearClipPlane = 0.05f * scale;
            var left = vrCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3);
            var right = vrCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3);
            Plugin.Log.LogInfo($"World scale {scale:0.00}: eye separation in the world {Vector3.Distance(left, right) * 1000f:0} mm");
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
