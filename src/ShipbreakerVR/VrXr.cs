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
    // The runtime can also end the session on its own (Virtual Desktop closed, the headset's link lost): Unity's OpenXR
    // plugin then shuts the loader down and tries to restart it (session loss) or quits the game (the runtime exits).
    // Watch follows that, so the game camera comes back instead of staying off for a VR view nobody sees, and the game
    // keeps running instead of quitting.
    internal static class VrXr
    {
        private static bool settingsLoaded;
        // Why VR cannot start (the AssetBundle missing or broken); it cannot be loaded a second time, so F3 says so.
        private static string unavailable;
        private static float lostSince = -1f;

        // How long the loader may be gone before VR counts as stopped: the plugin's restart takes a few frames.
        private const float LossGrace = 3f;

        public static bool IsRunning { get; private set; }

        // xrGetInstanceProcAddr, for the features that hook it (VrAlternateEyesFeature, VrTestFeature). One type for
        // all of them: Unity hands each feature the pointer the one before returned, and for a pointer Mono made from a
        // managed delegate, GetDelegateForFunctionPointer returns that delegate, which only casts to its own type.
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
        public delegate int GetInstanceProcAddrFn(ulong instance, IntPtr name, IntPtr function);

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
            if (unavailable != null)
            {
                Plugin.Log.LogWarning($"VR cannot start: {unavailable}");
                return;
            }
            if (!settingsLoaded)
            {
                try { LoadSettings(); }
                catch (Exception e)
                {
                    unavailable = e.Message;
                    Plugin.Log.LogError($"VR cannot start: {unavailable}. Reinstall the mod (see docs/INSTALL.md) and restart the game.");
                    return;
                }
            }

            var manager = XRGeneralSettings.Instance.Manager;
            if (manager.activeLoader == null && VrSettings.TestMode.Value) VrTestXr.Register();
            if (manager.activeLoader == null) VrTestRuntime.Select();
            if (manager.activeLoader == null) manager.InitializeLoaderSync();
            if (manager.activeLoader == null)
            {
                Plugin.Log.LogError("OpenXR failed to start. Is Virtual Desktop or SteamVR running?");
                return;
            }

            manager.StartSubsystems();
            IsRunning = true;
            lostSince = -1f;
            KeepGameOnExit(manager.activeLoader);
            Plugin.Log.LogInfo($"VR started with {manager.activeLoader.name}");
        }

        // From VrSession.Update, every frame: VR runs as long as the OpenXR loader does. A loader gone for LossGrace
        // (the runtime ended the session, the plugin's restart failed) stops VR here too, back to the flat view; a
        // loader back after a restart starts it again.
        public static void Watch()
        {
            if (!settingsLoaded) return;
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            if (manager == null) return;
            var active = manager.activeLoader != null;
            if (active == IsRunning)
            {
                lostSince = -1f;
                return;
            }
            if (active)
            {
                IsRunning = true;
                KeepGameOnExit(manager.activeLoader);
                Plugin.Log.LogInfo($"VR session restarted by the runtime with {manager.activeLoader.name}");
                return;
            }
            if (lostSince < 0f) lostSince = Time.unscaledTime;
            if (Time.unscaledTime - lostSince < LossGrace) return;
            lostSince = -1f;
            IsRunning = false;
            Plugin.Log.LogWarning("VR session ended by the runtime (Virtual Desktop closed or the headset disconnected): back to the flat view. F3 starts VR again.");
        }

        // When the runtime exits, the OpenXR plugin (OpenXRRestarter) shuts the loader down and then quits the
        // application unless its ShouldCancelQuit returns false. The game keeps running instead, in the flat view (Watch).
        private static void KeepGameOnExit(XRLoader loader)
        {
            try
            {
                var restarter = AccessTools.Method(typeof(OpenXRLoaderBase), "GetRestarter")?.Invoke(loader, null);
                var field = restarter != null ? AccessTools.Field(restarter.GetType(), "ShouldCancelQuit") : null;
                if (field == null)
                {
                    Plugin.Log.LogWarning("OpenXR: no restarter found; the game may quit when the runtime exits");
                    return;
                }
                field.SetValue(restarter, (Func<bool>)(() => false));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OpenXR: could not keep the game running when the runtime exits: {e.Message}");
            }
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
            if (openXr != null && VrSettings.MultiPass.Value) openXr.renderMode = OpenXRSettings.RenderMode.MultiPass;
            Plugin.Log.LogInfo($"XR settings loaded, OpenXR render mode: {openXr?.renderMode}");
            if (openXr != null) EnableControllerProfile(openXr);
            if (openXr != null) AddAlternateEyesFeature(openXr);
            if (openXr != null && VrSettings.TestMode.Value) AddFeature<VrTestFeature>(openXr, "ShipbreakerVR Test Input");
            settingsLoaded = true;
        }

        // The feature that hooks xrEndFrame for VrAlternateEyes; it hands frames on unchanged while that is off.
        private static void AddAlternateEyesFeature(OpenXRSettings settings)
        {
            var field = AccessTools.Field(typeof(OpenXRSettings), "features");
            var features = (OpenXRFeature[])field.GetValue(settings) ?? new OpenXRFeature[0];
            if (features.Any(f => f != null && f.GetType().FullName == typeof(VrAlternateEyesFeature).FullName)) return;
            var feature = ScriptableObject.CreateInstance<VrAlternateEyesFeature>();
            feature.name = "ShipbreakerVR Alternate Eyes";
            AccessTools.Field(typeof(OpenXRFeature), "nameUi").SetValue(feature, "ShipbreakerVR Alternate Eyes");
            AccessTools.Field(typeof(OpenXRFeature), "version").SetValue(feature, "0.0.1");
            AccessTools.Field(typeof(OpenXRFeature), "m_enabled").SetValue(feature, true);
            field.SetValue(settings, features.Concat(new OpenXRFeature[] { feature }).ToArray());
            Plugin.Log.LogInfo("OpenXR: alternate eyes feature added");
        }

        private static void AddFeature<T>(OpenXRSettings settings, string name) where T : OpenXRFeature
        {
            var field = AccessTools.Field(typeof(OpenXRSettings), "features");
            var features = (OpenXRFeature[])field.GetValue(settings) ?? new OpenXRFeature[0];
            if (features.Any(f => f != null && f.GetType().FullName == typeof(T).FullName)) return;
            var feature = ScriptableObject.CreateInstance<T>();
            feature.name = name;
            AccessTools.Field(typeof(OpenXRFeature), "nameUi").SetValue(feature, name);
            AccessTools.Field(typeof(OpenXRFeature), "version").SetValue(feature, "0.0.1");
            AccessTools.Field(typeof(OpenXRFeature), "m_enabled").SetValue(feature, true);
            field.SetValue(settings, features.Concat(new OpenXRFeature[] { feature }).ToArray());
            Plugin.Log.LogInfo($"OpenXR: {name} feature added");
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

    // The thumb rest's touch sensor (0.33.0, Raik: a thumb lying on the flat face beside the buttons is down, not up).
    // Unity's Oculus Touch profile (OpenXR plugin 1.2.8) names the path '/input/thumbrest/touch', which the profile
    // has in the OpenXR specification, but binds no action to it, so the controller had no such feature. Before the
    // plugin hands its action maps to the runtime (OpenXRInput.SendToOpenXR, at the loader's start), an action for it
    // is added to that profile's map, made as the trigger's touch action is: the controller then has the feature
    // 'ThumbrestTouch' (VrInput). The map's types are the plugin's own nested ones, hence the reflection. Should
    // anything be other than expected, the map is left as it is and the thumb simply stays on (VrInput.ThumbRestSensed).
    [HarmonyPatch]
    internal static class ThumbRestPatch
    {
        public const string Usage = "ThumbrestTouch";
        private const string Profile = "/interaction_profiles/oculus/touch_controller";
        private const string TriggerTouch = "/input/trigger/touch";
        private const string ThumbRest = "/input/thumbrest/touch";

        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.XR.OpenXR.Input.OpenXRInput"), "SendToOpenXR");

        private static void Prefix(System.Collections.IList actionMaps)
        {
            try
            {
                if (actionMaps == null) return;
                foreach (var map in actionMaps)
                    if (map != null && (string)Traverse.Create(map).Field("desiredInteractionProfile").GetValue() == Profile) Add(map);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OpenXR: the thumb rest's touch sensor could not be added, the thumb stays on: {e.Message}");
            }
        }

        private static void Add(object map)
        {
            if (!(Traverse.Create(map).Field("actions").GetValue() is System.Collections.IList actions)) return;
            object like = null;
            foreach (var action in actions)
            {
                if (Binding(action, ThumbRest) != null) return;
                if (like == null && Binding(action, TriggerTouch) != null) like = action;
            }
            if (like == null)
            {
                Plugin.Log.LogInfo("OpenXR: the Oculus Touch profile has no trigger touch action to make the thumb rest's like");
                return;
            }
            var from = Traverse.Create(like);
            var bindings = (System.Collections.IList)Activator.CreateInstance(from.Field("bindings").GetValue().GetType());
            var binding = Traverse.Create(Binding(like, TriggerTouch));
            var added = Activator.CreateInstance(binding.GetValue().GetType());
            var to = Traverse.Create(added);
            to.Field("interactionProfileName").SetValue(binding.Field("interactionProfileName").GetValue());
            to.Field("interactionPath").SetValue(ThumbRest);
            to.Field("userPaths").SetValue(binding.Field("userPaths").GetValue());
            bindings.Add(added);
            var rest = Activator.CreateInstance(like.GetType());
            var made = Traverse.Create(rest);
            var name = (string)from.Field("name").GetValue() ?? "";
            made.Field("name").SetValue(name.Contains("trigger") ? name.Replace("trigger", "thumbrest") : "thumbrestTouched");
            made.Field("localizedName").SetValue("Thumbrest Touched");
            made.Field("type").SetValue(from.Field("type").GetValue());
            made.Field("usages").SetValue(new System.Collections.Generic.List<string> { Usage });
            made.Field("bindings").SetValue(bindings);
            actions.Add(rest);
            Plugin.Log.LogInfo($"OpenXR: thumb rest touch added to the Oculus Touch profile as '{made.Field("name").GetValue()}', made like '{name}'");
        }

        // That action's binding to that input path, or null.
        private static object Binding(object action, string path)
        {
            if (!(Traverse.Create(action).Field("bindings").GetValue() is System.Collections.IList bindings)) return null;
            foreach (var binding in bindings)
                if (binding != null && (string)Traverse.Create(binding).Field("interactionPath").GetValue() == path) return binding;
            return null;
        }
    }
}
