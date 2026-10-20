using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SpatialTracking;

namespace ShipbreakerVR
{
    // Eine Stereo-Kamera hängt an einem eigenen Rig, das der Position der Spielkamera folgt und Kopfdrehung und Kopfposition übernimmt.
    // Die Drehung des Rigs ist der "Spielraum": Mit KoerperFolgtKopf setzt VrBody die Figur jedes Frame auf Spielraum * Kopf,
    // sonst dreht sich der Spielraum einfach mit der Spielkamera (Kopf schaut frei, Maus dreht die Figur).
    // Die Spielkamera bleibt die Ziel-Kamera der Werkzeuge, rendert aber nicht, solange VR läuft.
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

        // Kopfposition im Tracking, die auf der Spielkamera liegen soll (F4).
        private Vector3 recenterPosition;

        // Gierung, die ohne KoerperFolgtKopf den Kopf auf die Blickrichtung der Spielkamera dreht (F4).
        private Quaternion recenterRotation = Quaternion.identity;

        // Drehung des Spielraums in der Welt; der Kopf blickt in Playspace * HeadRotation.
        public Quaternion Playspace { get; set; } = Quaternion.identity;

        public Quaternion HeadRotation => transform.localRotation;

        public Camera Camera => vrCamera;

        public bool BodyFollowsHead => VrXr.IsRunning && Plugin.BodyFollowsHead.Value;

        public static void Attach(Camera gameCamera)
        {
            // Das Rig ist bewusst kein Kind der Spielkamera: Folgt die Figur dem Kopf, würde sich die Drehung sonst doppelt auswirken.
            var rig = new GameObject("ShipbreakerVR Rig").transform;
            rig.SetPositionAndRotation(gameCamera.transform.position, gameCamera.transform.rotation);

            var head = new GameObject("ShipbreakerVR Head");
            head.transform.SetParent(rig, false);
            var vr = head.AddComponent<VrCamera>();
            vr.gameCamera = gameCamera;
            vr.rig = rig;
            vr.Playspace = gameCamera.transform.rotation;
            Instance = vr;

            vr.vrCamera = head.AddComponent<Camera>();
            vr.vrCamera.CopyFrom(gameCamera);
            vr.vrCamera.ResetProjectionMatrix();
            vr.vrCamera.ResetWorldToCameraMatrix();
            vr.vrCamera.nearClipPlane = 0.05f;
            vr.vrCamera.stereoTargetEye = StereoTargetEyeMask.Both;
            vr.vrCamera.enabled = false;

            // HDRP-Einstellungen der Spielkamera übernehmen (Volumes, Belichtung, Frame Settings).
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

            Plugin.Log.LogInfo($"VR-Kamera angelegt, folgt der Spielkamera (HDRP-Daten übernommen: {gameHd != null})");
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
            // Die Spielkamera gehört zur Szene; geht sie, geht das Rig mit.
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
            wasRunning = running;

            if (running) ApplyQuality();
            vrCamera.enabled = running;
            gameCamera.enabled = !running;

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
                Plugin.Log.LogInfo($"Kantenglättung der VR-Kamera: {mode} (Spiel: {gameAntialiasing})");
            }
            var scale = Mathf.Clamp(Plugin.ResolutionScale.Value, 0.5f, 2f);
            if (!Mathf.Approximately(UnityEngine.XR.XRSettings.eyeTextureResolutionScale, scale))
            {
                UnityEngine.XR.XRSettings.eyeTextureResolutionScale = scale;
                Plugin.Log.LogInfo($"Renderauflösung pro Auge: {scale:0.00}x");
            }
        }

        // Kurz vor dem Rendern noch einmal, weil Cinemachine die Spielkamera erst spät im Frame setzt.
        private void FollowGameCamera()
        {
            if (gameCamera == null || rig == null) return;
            var cam = gameCamera.transform;
            if (!BodyFollowsHead) Playspace = cam.rotation * recenterRotation;
            rig.rotation = Playspace;
            rig.position = cam.position - Playspace * recenterPosition;
        }

        // Legt die aktuelle Kopfposition auf die Spielkamera; ohne KoerperFolgtKopf auch die Blickrichtung (nur Gierung).
        private void Recenter()
        {
            var head = transform;
            recenterPosition = head.localPosition;
            recenterRotation = Quaternion.Inverse(Quaternion.Euler(0f, head.localEulerAngles.y, 0f));
            if (VrUi.Instance != null) VrUi.Instance.Recenter();
            Plugin.Log.LogInfo($"Kopf zentriert (Kopfposition im Tracking {head.localPosition})");
        }
    }
}
