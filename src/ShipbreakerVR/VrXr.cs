using System;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace ShipbreakerVR
{
    // Startet und stoppt OpenXR zur Laufzeit. Die XR-Einstellungen kommen aus dem AssetBundle,
    // das im Unity-Hilfsprojekt gebaut wird (unity/ShipbreakerVRUnity, Menü "ShipbreakerVR").
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
                Plugin.Log.LogError("OpenXR ließ sich nicht starten. Läuft Virtual Desktop bzw. SteamVR?");
                return;
            }

            manager.StartSubsystems();
            IsRunning = true;
            Plugin.Log.LogInfo($"VR gestartet mit {manager.activeLoader.name}");
        }

        private static void Stop()
        {
            var manager = XRGeneralSettings.Instance.Manager;
            manager.StopSubsystems();
            manager.DeinitializeLoader();
            IsRunning = false;
            Plugin.Log.LogInfo("VR beendet");
        }

        private static void LoadSettings()
        {
            var path = Path.Combine(Path.Combine(Paths.PluginPath, "ShipbreakerVR"), Path.Combine("AssetBundles", "xrmanager"));
            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) throw new Exception("AssetBundle nicht gefunden: " + path);
            bundle.LoadAllAssets();

            if (XRGeneralSettings.Instance == null) throw new Exception("XRGeneralSettings fehlt im AssetBundle");
            var openXr = OpenXRSettings.Instance;
            if (openXr != null && Plugin.MultiPass.Value) openXr.renderMode = OpenXRSettings.RenderMode.MultiPass;
            Plugin.Log.LogInfo($"XR-Einstellungen geladen, OpenXR-Rendermodus: {openXr?.renderMode}");
            settingsLoaded = true;
        }
    }
}
