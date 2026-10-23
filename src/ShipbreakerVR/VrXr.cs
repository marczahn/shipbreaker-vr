using System;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace ShipbreakerVR
{
    // Starts and stops OpenXR at runtime. The XR settings come from the AssetBundle
    // built in the Unity helper project (unity/ShipbreakerVRUnity, menu "ShipbreakerVR").
    internal static class VrXr
    {
        private static bool settingsLoaded;

        public static bool IsRunning { get; private set; }

        // After a reload (ScriptEngine, F6) the XR settings from the AssetBundle are still loaded, and OpenXR may still run
        // for the previous instance; the AssetBundle cannot be loaded a second time. Takes both over. True if the
        // settings were already loaded, i.e. this is not the first instance in this game session.
        public static bool Adopt()
        {
            var settings = XRGeneralSettings.Instance;
            if (settings == null) return false;
            settingsLoaded = true;
            IsRunning = settings.Manager != null && settings.Manager.activeLoader != null;
            if (IsRunning) Plugin.Log.LogInfo($"VR still running with {settings.Manager.activeLoader.name}, taken over");
            return true;
        }

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
            if (openXr != null) EnableControllerProfile(openXr);
            settingsLoaded = true;
        }

        // OpenXR only creates controller actions for enabled interaction profiles. The profiles in the AssetBundle
        // arrive disabled, so OpenXR reported no controllers; enable the Oculus Touch profile (or add one) before the
        // loader starts.
        private static void EnableControllerProfile(OpenXRSettings settings)
        {
            var field = AccessTools.Field(typeof(OpenXRSettings), "features");
            var enabled = AccessTools.Field(typeof(OpenXRFeature), "m_enabled");
            var features = (OpenXRFeature[])field.GetValue(settings) ?? new OpenXRFeature[0];
            Plugin.Log.LogInfo($"OpenXR features from the settings: {string.Join(", ", features.Where(f => f != null).Select(f => $"{f.GetType().Name} ({((bool)enabled.GetValue(f) ? "on" : "off")})"))}");

            var profile = features.OfType<OculusTouchControllerProfile>().FirstOrDefault();
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<OculusTouchControllerProfile>();
                profile.name = "Oculus Touch Controller Profile";
                AccessTools.Field(typeof(OpenXRFeature), "nameUi").SetValue(profile, "Oculus Touch Controller Profile");
                AccessTools.Field(typeof(OpenXRFeature), "version").SetValue(profile, "0.0.1");
                field.SetValue(settings, features.Concat(new OpenXRFeature[] { profile }).ToArray());
            }
            enabled.SetValue(profile, true);
            Plugin.Log.LogInfo("OpenXR: Oculus Touch controller profile enabled");
        }
    }
}
