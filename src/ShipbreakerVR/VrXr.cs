using System;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace ShipbreakerVR
{
    // Starts and stops OpenXR at runtime. The XR settings come from the AssetBundle
    // built in the Unity helper project (unity/ShipbreakerVRUnity, menu "ShipbreakerVR").
    internal static class VrXr
    {
        private static bool settingsLoaded;

        public static bool IsRunning { get; private set; }

        public static void Toggle()
        {
            if (IsRunning) Stop();
            else Start();
        }

        private static void Start()
        {
            if (!settingsLoaded) LoadSettings();

            var manager = XRGeneralSettings.Instance.Manager;
            if (manager.activeLoader == null) manager.InitializeLoaderSync();
            if (manager.activeLoader == null)
            {
                Plugin.Log.LogError("OpenXR failed to start. Is Virtual Desktop or SteamVR running?");
                return;
            }

            manager.StartSubsystems();
            IsRunning = true;
            Plugin.Log.LogInfo($"VR started with {manager.activeLoader.name}");
        }

        private static void Stop()
        {
            var manager = XRGeneralSettings.Instance.Manager;
            manager.StopSubsystems();
            manager.DeinitializeLoader();
            IsRunning = false;
            Plugin.Log.LogInfo("VR stopped");
        }

        private static void LoadSettings()
        {
            var path = Path.Combine(Path.Combine(Paths.PluginPath, "ShipbreakerVR"), Path.Combine("AssetBundles", "xrmanager"));
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) throw new Exception("AssetBundle not found: " + path);
            bundle.LoadAllAssets();

            if (XRGeneralSettings.Instance == null) throw new Exception("XRGeneralSettings missing from AssetBundle");
            var openXr = OpenXRSettings.Instance;
            if (openXr != null && Plugin.MultiPass.Value) openXr.renderMode = OpenXRSettings.RenderMode.MultiPass;
            Plugin.Log.LogInfo($"XR settings loaded, OpenXR render mode: {openXr?.renderMode}");
            settingsLoaded = true;
        }
    }
}
