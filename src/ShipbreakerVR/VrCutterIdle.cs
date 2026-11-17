using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The cutter's model sways while nothing happens: its Animator (controller "Anim_CuttingTool") loops a clip of 2.5
    // seconds in its resting state (Anim_CuttingTool_Idle) that moves and turns the Animator's own transform. In the
    // flat game the tool hangs in the view and this gives it some life; in VR it lies in the hand and must lie still
    // there (Raik, 2026-11-17). While VR runs, the resting state is held at its start. The recoil of a cut (the trigger
    // FIRE, state Anim_CuttingTool_Recoil) plays as before, as does the way back into the resting state.
    internal static class VrCutterIdle
    {
        static VrCutterIdle() => VrSession.OnUnload("cutter idle", Release);

        private static readonly int Idle = Animator.StringToHash("Anim_CuttingTool_Idle");
        // After a trigger the Animator runs at least this long, so the state it leads to is taken up.
        private const float AfterTrigger = 0.25f;

        private static readonly AccessTools.FieldRef<CuttingToolController, Animator> Tool =
            AccessTools.FieldRefAccess<CuttingToolController, Animator>("m_CuttingToolAnimator");
        private static readonly AccessTools.FieldRef<CuttingToolController, Animator> Shadow =
            AccessTools.FieldRefAccess<CuttingToolController, Animator>("m_CuttingToolShadowAnimator");

        private static Animator tool, shadow;
        private static float runsUntil;
        private static bool logged;

        // From CutterAnimationPatch: the game has just set a trigger on the cutter's Animators.
        public static void Triggered()
        {
            runsUntil = Time.unscaledTime + AfterTrigger;
            Run(tool);
            Run(shadow);
        }

        // Each frame (VrFrame).
        public static void Update()
        {
            var cutter = GameRefs.Cutter;
            tool = cutter != null ? Tool(cutter) : null;
            shadow = cutter != null ? Shadow(cutter) : null;
            var still = VrXr.IsRunning && Time.unscaledTime >= runsUntil;
            Hold(tool, still);
            Hold(shadow, still);
        }

        private static void Hold(Animator animator, bool still)
        {
            if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) return;
            if (!still || animator.IsInTransition(0) || animator.GetCurrentAnimatorStateInfo(0).shortNameHash != Idle)
            {
                Run(animator);
                return;
            }
            if (animator.speed == 0f) return;
            animator.Play(Idle, 0, 0f);
            animator.speed = 0f;
            if (logged) return;
            logged = true;
            Plugin.Log.LogInfo($"Cutter idle: '{animator.name}' held still in its resting state (controller '{animator.runtimeAnimatorController.name}')");
        }

        private static void Run(Animator animator)
        {
            if (animator != null && animator.speed == 0f) animator.speed = 1f;
        }

        // On unload (hot reload), and so with VR switched off: the Animators run as the game left them.
        public static void Release()
        {
            Run(tool);
            Run(shadow);
            tool = shadow = null;
        }
    }

    [HarmonyPatch(typeof(CuttingToolController), nameof(CuttingToolController.TriggerCutterAnimation))]
    internal static class CutterAnimationPatch
    {
        private static void Prefix() => VrCutterIdle.Triggered();
    }
}
