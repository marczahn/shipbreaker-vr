using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // Every active game camera without a target texture also renders into the headset (HDRP's XRSystem only checks
    // that and xrRendering). The load screen's camera (depth 10, after ours) thus covered our image with its grey
    // background and its CRT and scanline effects, which are not made for VR. In VR its canvas is on our UI canvas,
    // so UI-only cameras stay off while VR runs. So does the Hab's camera (depth 2) while the rig follows another
    // one: the Hab scene is loaded behind the load screen, and its camera painted the Hab over the load screen and
    // its "press a key" prompt, fixed to the face as if the game had frozen. The cameras go back on, as far as the game
    // wants them on, when VR stops.
    internal static class VrCameraSilencer
    {
        private const int UiLayer = 5;

        private static readonly HashSet<Camera> Silenced = new HashSet<Camera>();
        private static Camera[] buffer = new Camera[16];

        // A camera that draws nothing but the UI layer, like the load screen's. Not a view for the VR camera to follow.
        public static bool IsUiOnly(Camera camera) => camera.cullingMask != 0 && (camera.cullingMask & ~(1 << UiLayer)) == 0;

        // Whether the game itself has this camera on. It switches the main camera off while it shows the Hab with its own.
        public static bool GameWantsEnabled(Camera camera) => camera != LynxCameraController.MainCamera || !VrHab.MainCameraOff;

        // A game camera the rig no longer follows stays off until VR stops: loading the bay, the rig first follows a
        // 'Main Camera' the scene brings along, then LynxCameraController's; the first one, switched back on, rendered
        // next to the rig behind the load screen (log: Other cameras rendering next to 'Main Camera': 'Main Camera').
        public static void Keep(Camera camera)
        {
            camera.enabled = false;
            Silenced.RemoveWhere(c => c == null);
            Silenced.Add(camera);
        }

        // From VrCamera.LateUpdate, every frame: switches the others off while VR runs, back on once it stops.
        public static void Update(bool running, Camera vrCamera, Camera gameCamera)
        {
            if (!running)
            {
                Restore();
                return;
            }
            if (Camera.allCamerasCount > buffer.Length) buffer = new Camera[Camera.allCamerasCount * 2];
            var count = Camera.GetAllCameras(buffer);
            for (var i = 0; i < count; i++)
            {
                var camera = buffer[i];
                buffer[i] = null;
                if (camera == vrCamera || camera == gameCamera || camera.cameraType != CameraType.Game || camera.targetTexture != null) continue;
                if (camera.CompareTag("UI_Camera") || camera.name.StartsWith("ShipbreakerVR")) continue;
                var uiOnly = IsUiOnly(camera);
                if (!uiOnly && !VrHab.IsHabCamera(camera)) continue;
                var hd = camera.GetComponent<HDAdditionalCameraData>();
                if (hd != null && !hd.xrRendering) continue;
                camera.enabled = false;
                Silenced.RemoveWhere(c => c == null);
                if (Silenced.Add(camera)) Plugin.Log.LogInfo($"Camera '{camera.name}' ({(uiOnly ? "UI only" : "Hab")}, depth {camera.depth}) switched off while VR runs");
            }
        }

        // The silenced cameras back on, as far as the game wants them (VR stopped, or unloading).
        public static void Restore()
        {
            if (Silenced.Count == 0) return;
            foreach (var camera in Silenced)
                if (camera != null) camera.enabled = GameWantsEnabled(camera);
            Silenced.Clear();
        }
    }
}
