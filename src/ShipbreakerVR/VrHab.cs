using System.Collections.Generic;
using BBI.Unity.Game;
using Cinemachine;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The Hab (the station between shifts) is an extra scene loaded next to the bay. It has its own camera
    // (Hab3DController's CinemachineBrain on "Unity_Camera", depth 2) that Cinemachine moves between bed, kitchen,
    // terminal and so on, while LynxCameraController switches the bay's main camera off. While the VR rig stayed on
    // the main camera, the headset got the whole bay rendered by our camera and then the Hab on top by the Hab camera:
    // twice the work and video memory, no head tracking, and our UI canvas painted over.
    // So in the Hab the rig follows the Hab camera, and the camera sway of its virtual cameras is turned off in VR.
    internal static class VrHab
    {
        private static Hab3DController controller;
        private static float nextControllerSearch;

        // Original amplitude of each sway (Perlin noise) we silenced; the game itself toggles their enabled flag.
        private static readonly Dictionary<CinemachineBasicMultiChannelPerlin, float> calmed = new Dictionary<CinemachineBasicMultiChannelPerlin, float>();
        private static float nextNoiseScan;

        // Mirrors what LynxCameraController did with the main camera: off in the Hab, on in gameplay. The camera's own
        // enabled flag doesn't tell, because the VR camera keeps the camera it follows switched off.
        public static bool MainCameraOff { get; set; }

        // The camera the game shows the Hab with, or null outside the Hab.
        public static Camera Camera
        {
            get
            {
                // The Hab is only shown next to a loaded bay; without its main camera there is nothing to look for.
                if (!MainCameraOff || LynxCameraController.MainCamera == null) return null;
                // Asked every frame; searching all objects is only worth it now and then (e.g. while the Hab unloads).
                if (controller == null && Time.unscaledTime >= nextControllerSearch)
                {
                    nextControllerSearch = Time.unscaledTime + 0.25f;
                    controller = Object.FindObjectOfType<Hab3DController>();
                }
                if (controller == null || !controller.isActiveAndEnabled || controller.CinemachineBrain == null) return null;
                var camera = controller.CinemachineBrain.OutputCamera;
                return camera != null && camera.gameObject.activeInHierarchy ? camera : null;
            }
        }

        public static bool IsHabCamera(Camera camera) => camera != null && camera.GetComponentInParent<Hab3DController>() != null;

        // In VR the sway moves the whole room around the player. Silenced while the rig follows the Hab camera,
        // restored afterwards. Rescanned now and then, since locations may be activated later.
        public static void Calm(bool calm)
        {
            if (!calm)
            {
                if (calmed.Count == 0) return;
                foreach (var entry in calmed)
                    if (entry.Key != null) entry.Key.m_AmplitudeGain = entry.Value;
                calmed.Clear();
                return;
            }
            if (controller == null || Time.unscaledTime < nextNoiseScan) return;
            nextNoiseScan = Time.unscaledTime + 1f;
            var added = 0;
            foreach (var noise in controller.GetComponentsInChildren<CinemachineBasicMultiChannelPerlin>(true))
            {
                if (calmed.ContainsKey(noise)) continue;
                calmed[noise] = noise.m_AmplitudeGain;
                noise.m_AmplitudeGain = 0f;
                added++;
            }
            if (added > 0) Plugin.Log.LogInfo($"Hab camera sway off in VR ({added} virtual camera(s))");
        }
    }

    [HarmonyPatch(typeof(LynxCameraController), "OnGameStateChangedEvent")]
    internal static class LynxCameraControllerGameStatePatch
    {
        private static void Postfix(GameStateChangedEvent ev)
        {
            if (ev.GameState == GameSession.GameState.Hab) VrHab.MainCameraOff = true;
            else if (ev.GameState == GameSession.GameState.Gameplay) VrHab.MainCameraOff = false;
        }
    }
}
