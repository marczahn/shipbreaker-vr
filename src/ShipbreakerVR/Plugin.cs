using BBI.Unity.Game;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "de.marczahn.shipbreakervr";
        public const string Name = "ShipbreakerVR";
        public const string Version = "0.18.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            VrSettings.Bind(Config);
            // Loaded again by ScriptEngine (F6) while the game runs: OpenXR and the Hab state carry over from the
            // previous instance, and VR must not be toggled off by the start-at-launch (VrSession).
            var reloaded = VrXr.Adopt();
            if (reloaded) VrHab.Adopt();
            Log.LogInfo($"{Name} {Version} loaded{(reloaded ? $" again (VR {(VrXr.IsRunning ? "running" : "off")})" : "")}. Unity {Application.unityVersion}, game {Application.version}");
            if (VrSettings.HeadHeight.Value != 0f || VrSettings.HeadSideways.Value != 0f) Log.LogInfo($"Head offset: {VrSettings.HeadHeight.Value:0} cm up, {VrSettings.HeadSideways.Value:0} cm right");
            VrSession.Start(reloaded);
            Application.quitting += OnQuitting;
        }

        private bool quitting;

        private void OnQuitting() => quitting = true;

        // ScriptEngine destroys the plugin before loading the new build (VrSession.Unload).
        private void OnDestroy()
        {
            Application.quitting -= OnQuitting;
            if (quitting) return;
            Log.LogInfo($"{Name} unloading");
            VrSession.Unload();
            Log.LogInfo($"{Name} unloaded");
            BepInEx.Logging.Logger.Sources.Remove(Log);
        }

        private void Update() => VrSession.Update();
    }

    // Checks that we find the game camera: all tools aim via LynxCameraController.MainCamera.
    [HarmonyPatch(typeof(LynxCameraController), "Start")]
    internal static class LynxCameraControllerStartPatch
    {
        private static void Postfix()
        {
            var cam = LynxCameraController.MainCamera;
            Plugin.Log.LogInfo(cam == null
                ? "LynxCameraController started, but MainCamera is null"
                : $"Game camera found: '{cam.name}', FOV {cam.fieldOfView}, resolution {LynxCameraController.ScreenWidth}x{LynxCameraController.ScreenHeight}");
            // A new main camera starts switched on; the game turns it off again when it shows the Hab.
            VrHab.MainCameraOff = false;
            if (cam == null) return;
            if (VrCamera.Instance == null || VrCamera.Instance.GameCamera != cam) VrCamera.Attach(cam);
        }
    }
}
