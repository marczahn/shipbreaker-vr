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
    // The rest of the frame hangs on the VR camera's callbacks (VrCamera), which run the phases below, each step on its
    // own as here, so the whole order is in this file:
    //   Update        frame statistics, load trace, F8 (diagnostics only)
    //   FixedUpdate   VrClimb: holding on, each physics step; the physics step count (frame statistics)
    //   LateUpdate    after the game's animation: VrTorso turns the model upright, VrToolModels bends the arm for the demo charge, VrHands poses the
    //                 suit's hands, VrCrosshair hides the crosshair; then the rig itself (VrCamera)
    //   before render the rig is placed on the game camera (VrCamera.FollowGameCamera, also in LateUpdate); then VrClimb
    //                 measures the hands on it and VrToolModels puts the tool models into the hands at the latest pose
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

        private static readonly (string Name, System.Action Step)[] UpdateSteps =
        {
            ("frame statistics", VrPerformance.FrameStart),
            ("load trace", () => VrLoadTrace.EndFrame(VrCamera.Instance)),
            ("diagnostics", () => VrDiagnostics.Update(VrCamera.Instance)),
        };

        private static readonly (string Name, System.Action Step)[] FixedSteps =
        {
            ("climbing", VrClimb.FixedUpdate),
            ("frame statistics", VrPerformance.FixedStep),
        };

        private static readonly (string Name, System.Action Step)[] LateSteps =
        {
            ("torso", VrTorso.LateUpdate),
            ("demo charge arm", VrToolModels.PoseArm),
            ("hands", VrHands.LateUpdate),
            ("crosshair", VrCrosshair.LateUpdate),
        };

        private static readonly (string Name, System.Action Step)[] PlacedSteps =
        {
            ("climbing", VrClimb.RigPlaced),
            ("tool models", VrToolModels.Place),
        };

        // From the VR camera's Update, FixedUpdate and LateUpdate, and from FollowGameCamera once the rig is placed.
        public static void Update() => RunAll(UpdateSteps);
        public static void Fixed() => RunAll(FixedSteps);
        public static void Late() => RunAll(LateSteps);
        public static void RigPlaced() => RunAll(PlacedSteps);

        private static int ranFrame = -1;
        private static int errors;
        private static float nextCheck;

        // Each step on its own, so one that fails leaves the others working.
        private static void Run()
        {
            if (ranFrame == Time.frameCount) return;
            ranFrame = Time.frameCount;
            RunAll(Steps);
        }

        private static void RunAll((string Name, System.Action Step)[] steps)
        {
            foreach (var (name, step) in steps)
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
