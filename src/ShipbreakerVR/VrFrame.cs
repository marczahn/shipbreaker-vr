using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ShipbreakerVR
{
    // What the hands and the input mean for this frame, computed once and in a fixed order, at the end of Unity's
    // EarlyUpdate: after the runtime delivered the controllers' state, before the physics steps and before any of the
    // game's updates. The patched game code (tool input, aim, equipment checks, thrust, turning) and VrClimb in the
    // physics step only read the results. Before, each module worked its state out on the first read in a frame, so the
    // order depended on which game method happened to ask first, and a module could cache the state of another one
    // that changed later in the same frame (the aim still on the old hand for a frame after the other hand took the tool).
    //   1. VrInput      the controllers' buttons, triggers, grips and sticks
    //   2. VrToolInput  what the hands do (VrHand, brought up to date with the game first): drawing, holding and putting
    //                   away tools, the tool hand, the buttons' actions (and VrDualTools.Check)
    //   3. VrAim        the aim camera on the tool hand that step 2 settled
    //   4. VrDualTools  the second tool's aim camera
    //   5. VrControls   flying, braking and turning, which rest while the left hand holds a tool or holds on
    // Later in the frame VrCamera.LateUpdate poses the arms and hides the crosshair, and the tool models and the laser
    // follow the latest pose right before rendering.
    internal static class VrFrame
    {
        static VrFrame() => VrSession.OnUnload("frame update", Uninstall);

        private static readonly (string Name, System.Action Step)[] Steps =
        {
            ("input", VrInput.Update),
            ("tool input", VrToolInput.Update),
            ("aim", VrAim.Update),
            ("second aim", VrDualTools.UpdateAim),
            ("flying", VrControls.Update),
        };

        private static int ranFrame = -1;
        private static int errors;
        private static float nextCheck;

        // Each step on its own, so one that fails leaves the others working.
        private static void Run()
        {
            if (ranFrame == Time.frameCount) return;
            ranFrame = Time.frameCount;
            foreach (var (name, step) in Steps)
            {
                try { step(); }
                catch (System.Exception e)
                {
                    if (errors++ < 10) Plugin.Log.LogError($"Frame update, {name}: {e}");
                }
            }
        }

        // From Plugin.Awake.
        public static void Install()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!Insert(ref loop)) return;
            PlayerLoop.SetPlayerLoop(loop);
            Plugin.Log.LogInfo("Frame update installed at the end of EarlyUpdate");
        }

        // From Plugin.Update, every frame. Should something have rebuilt the player loop without us (the game's ECS
        // world sets up its own systems in it), the frame is run from here meanwhile, a frame late at worst for the
        // code that already ran, and the step is put back once a second.
        public static void Check()
        {
            if (ranFrame == Time.frameCount) return;
            Run();
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!Insert(ref loop)) return;
            PlayerLoop.SetPlayerLoop(loop);
            Plugin.Log.LogWarning("Frame update was missing from the player loop, installed again");
        }

        // On unload (hot reload): the new instance installs its own.
        public static void Uninstall()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (Remove(ref loop, false)) PlayerLoop.SetPlayerLoop(loop);
        }

        // Adds our step at the end of EarlyUpdate; false when nothing changed. Steps of an earlier instance (a hot
        // reload that did not unload cleanly) are removed first.
        private static bool Insert(ref PlayerLoopSystem loop)
        {
            var removed = Remove(ref loop, true);
            var systems = loop.subSystemList;
            if (systems == null) return removed;
            for (var i = 0; i < systems.Length; i++)
            {
                if (systems[i].type != typeof(UnityEngine.PlayerLoop.EarlyUpdate)) continue;
                var steps = new List<PlayerLoopSystem>(systems[i].subSystemList ?? new PlayerLoopSystem[0]);
                if (steps.Exists(s => s.type == typeof(VrFrame))) return removed;
                steps.Add(new PlayerLoopSystem { type = typeof(VrFrame), updateDelegate = Run });
                systems[i].subSystemList = steps.ToArray();
                return true;
            }
            Plugin.Log.LogError("Frame update: no EarlyUpdate in the player loop");
            return removed;
        }

        // Removes our step (only other instances' with othersOnly); true if one was removed.
        private static bool Remove(ref PlayerLoopSystem loop, bool othersOnly)
        {
            var systems = loop.subSystemList;
            if (systems == null) return false;
            var removed = false;
            for (var i = 0; i < systems.Length; i++)
            {
                var steps = systems[i].subSystemList;
                if (steps == null) continue;
                var kept = new List<PlayerLoopSystem>(steps.Length);
                foreach (var step in steps)
                {
                    var ours = step.type != null && step.type.FullName == typeof(VrFrame).FullName;
                    if (ours && (!othersOnly || step.type != typeof(VrFrame))) removed = true;
                    else kept.Add(step);
                }
                if (kept.Count != steps.Length) systems[i].subSystemList = kept.ToArray();
            }
            return removed;
        }
    }
}
