using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR.Management;

namespace ShipbreakerVR.Dev
{
    // What ShipbreakerVR cannot undo or redo on its own yet when it is reloaded. Goes through reflection with the
    // names the plugin uses today; a missing name only skips that step with a warning. Once the plugin tears itself
    // down in OnDestroy (docs/TESTING.md, "Plugin integration"), most of this can go.
    internal static class ModHooks
    {
        // Every GameObject the plugin creates is named like this ("ShipbreakerVR Rig", "ShipbreakerVR VR: Helmet", ...).
        public const string ObjectPrefix = "ShipbreakerVR ";

        private const string XrBundle = "xrmanager";

        internal class State
        {
            public bool WasVrRunning;
            public int FramesToWait;
            public readonly List<VideoOptionsScreenController> OptionScreens = new List<VideoOptionsScreenController>();
        }

        public static State BeforeUnload(Assembly assembly)
        {
            var state = new State();
            var xr = assembly.GetType("ShipbreakerVR.VrXr");
            // Switch VR off through the old plugin: on the next frames its components turn the game camera back on and
            // put canvases and helmet back, and the new plugin starts from a clean state.
            if (xr != null && Get<bool>(xr, "IsRunning"))
            {
                Call(xr, "Toggle");
                state.WasVrRunning = true;
                state.FramesToWait = 5;
            }
            // The plugin adds its option rows in OptionsScreenController.Awake, which does not run again for screens
            // that already exist. Remember those that have rows; the new plugin gets to inject into them.
            foreach (var screen in SceneObjects<VideoOptionsScreenController>())
                if (screen.GetComponentsInChildren<Transform>(true).Any(t => t.name.StartsWith(ObjectPrefix))) state.OptionScreens.Add(screen);
            return state;
        }

        // The new plugin loads the XR settings AssetBundle again, which fails while the old copy is loaded.
        public static void AfterUnload(State state)
        {
            try
            {
                var settings = XRGeneralSettings.Instance;
                if (settings != null && settings.Manager != null && settings.Manager.activeLoader != null)
                {
                    DevPlugin.Log.LogWarning("XR is still running, keeping the XR settings; VR may not start again until the game restarts");
                    return;
                }
                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles().ToList())
                {
                    if (bundle == null || bundle.name != XrBundle) continue;
                    bundle.Unload(true);
                    DevPlugin.Log.LogInfo($"AssetBundle '{XrBundle}' unloaded");
                }
            }
            catch (Exception e)
            {
                DevPlugin.Log.LogError($"Unloading the XR settings failed: {e}");
            }
        }

        public static IEnumerator AfterLoad(Assembly assembly, State state)
        {
            if (state == null) yield break;
            var options = assembly.GetType("ShipbreakerVR.VrOptions");
            if (options != null)
                foreach (var screen in state.OptionScreens)
                    if (screen != null) Call(options, "Inject", screen);
            if (!state.WasVrRunning) yield break;
            yield return null;
            var xr = assembly.GetType("ShipbreakerVR.VrXr");
            if (xr != null) Call(xr, "Toggle");
        }

        public static IEnumerable<T> SceneObjects<T>() where T : Component =>
            Resources.FindObjectsOfTypeAll<T>().Where(o => o != null && o.gameObject.scene.IsValid());

        private static T Get<T>(Type type, string name)
        {
            try
            {
                var property = AccessTools.Property(type, name);
                if (property != null) return (T)property.GetValue(null, null);
                var field = AccessTools.Field(type, name);
                if (field != null) return (T)field.GetValue(null);
                DevPlugin.Log.LogWarning($"{type.FullName}.{name} not found, skipping that reload step");
            }
            catch (Exception e)
            {
                DevPlugin.Log.LogError($"Reading {type.FullName}.{name} failed: {e}");
            }
            return default;
        }

        private static void Call(Type type, string name, params object[] args)
        {
            try
            {
                var method = AccessTools.Method(type, name);
                if (method == null)
                {
                    DevPlugin.Log.LogWarning($"{type.FullName}.{name} not found, skipping that reload step");
                    return;
                }
                method.Invoke(null, args);
            }
            catch (Exception e)
            {
                DevPlugin.Log.LogError($"{type.FullName}.{name} failed: {(e as TargetInvocationException)?.InnerException ?? e}");
            }
        }
    }
}
