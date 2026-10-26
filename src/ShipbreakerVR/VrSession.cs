using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The mod's lifetime in one place: what starts with the plugin (the patches, the frame update), what runs from the
    // plugin's Update (starting VR, F3, putting the rig onto the game's camera), and the teardown on unload (hot reload).
    // Modules register their own teardown (OnUnload), from their static constructor, which runs on their first use, and
    // unloading runs them in reverse order: what was used last goes first, the patches go last. Before, Plugin.OnDestroy
    // called a hand-kept list of eleven teardowns, and a new module had to remember to add itself.
    internal static class VrSession
    {
        private static readonly List<(string What, System.Action Teardown)> Teardowns = new List<(string, System.Action)>();
        private static Harmony harmony;
        private static bool launched;
        private static float nextCameraSearch;

        // Called once per module, from its static constructor.
        public static void OnUnload(string what, System.Action teardown) => Teardowns.Add((what, teardown));

        // From Plugin.Awake, after the config is bound. reloaded: loaded again by ScriptEngine while VR runs, so VR is
        // not started (toggled) once more.
        public static void Start(bool reloaded)
        {
            launched = reloaded;
            // Apply each patch separately so one failing patch doesn't take down all the others (and F3).
            // ScriptEngine renames the assembly on every load; with that name in the ID, unloading an old instance
            // only removes its own patches.
            harmony = new Harmony($"{Plugin.Guid}/{typeof(Plugin).Assembly.GetName().Name}");
            OnUnload("patches", () => harmony.UnpatchSelf());
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (System.Exception e) { Plugin.Log.LogError($"Patch {type.Name} failed: {e}"); }
            }
            PatchReport.LogSummary();
            try { VrFrame.Install(); }
            catch (System.Exception e) { Plugin.Log.LogError($"Installing the frame update failed, running it from Update: {e}"); }
        }

        // From Plugin.OnDestroy (not when the game quits). Everything the mod changed in the game goes back to how the
        // game had it, as when VR stops, so the new instance starts from the game's own state. OpenXR keeps running; the
        // new instance takes it over (VrXr.Adopt).
        public static void Unload()
        {
            var names = new List<string>();
            for (var i = Teardowns.Count - 1; i >= 0; i--)
            {
                var (what, teardown) = Teardowns[i];
                names.Add(what);
                try { teardown(); }
                catch (System.Exception e) { Plugin.Log.LogError($"Unloading the {what} failed: {e}"); }
            }
            Teardowns.Clear();
            Plugin.Log.LogInfo($"Unloaded: {string.Join(", ", names)}");
        }

        // From Plugin.Update.
        public static void Update()
        {
            if (!launched)
            {
                launched = true;
                if (Plugin.StartVrAtLaunch.Value) VrXr.Toggle();
            }
            if (Input.GetKeyDown(KeyCode.F3)) VrXr.Toggle();
            VrFrame.Check();
            VrMenuInput.EnsureAttached();
            AttachCamera();
        }

        // The main menu has no LynxCameraController yet; the VR camera then attaches to the menu camera.
        // Without any camera (intro, loading screen) the rig still exists and shows the UI on black.
        // In the Hab the game shows the station through its own camera; back in gameplay the main camera takes over again.
        private static void AttachCamera()
        {
            var vr = VrCamera.Instance;
            if (!VrXr.IsRunning) return;
            // The rig moves onto the Hab camera by itself (VrCamera.LateUpdate); here only when VR starts in the Hab.
            var hab = VrHab.Camera;
            if (hab != null)
            {
                if (vr == null)
                {
                    Plugin.Log.LogInfo($"VR camera attached to the Hab camera '{hab.name}'");
                    VrCamera.Attach(hab);
                }
            }
            else if (Time.unscaledTime >= nextCameraSearch && (vr == null || vr.GameCamera == null || vr.FollowsHab))
            {
                nextCameraSearch = Time.unscaledTime + 0.25f;
                var cam = LynxCameraController.MainCamera != null ? LynxCameraController.MainCamera : FindMenuCamera();
                if (cam != null) Plugin.Log.LogInfo($"VR camera attached to '{cam.name}'");
                if (cam != null || vr == null || vr.GameCamera != null) VrCamera.Attach(cam);
            }
        }

        // The camera currently producing the image: active, no target texture, highest depth. Not one that only draws
        // canvases, like the load screen camera: its volume layers hid our UI pass and it shows no world anyway.
        private static Camera FindMenuCamera()
        {
            Camera best = null;
            foreach (var cam in Camera.allCameras)
            {
                if (cam.targetTexture != null || cam.CompareTag("UI_Camera") || cam.name.StartsWith("ShipbreakerVR") || VrCamera.IsUiOnly(cam)) continue;
                // Only while the game shows the Hab (VrHab.Camera); a leftover Hab camera during a load is not a menu.
                if (VrHab.IsHabCamera(cam)) continue;
                if (best == null || cam.depth > best.depth) best = cam;
            }
            return best;
        }
    }
}
