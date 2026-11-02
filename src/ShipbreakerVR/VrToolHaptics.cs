using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The tools in the controller, as Raik asked: the hand holding the cutter hums while it cuts, stronger with the
    // Splitsaw than with the Stinger; the hand holding the grapple hums while the grapple holds a part, more for a heavier
    // one; and each tether end the grapple fires gives a short buzz (TetherController.SpawnFireFX).
    internal static class VrToolHaptics
    {
        // Each pulse a little longer than the time to the next, so the hum does not break up.
        private const float Every = 0.05f;
        private const float Length = 0.08f;
        private const float Stinger = 0.3f;
        private const float Splitsaw = 0.6f;
        private const float GrappleLight = 0.12f;
        private const float GrappleMedium = 0.2f;
        private const float GrappleHeavy = 0.3f;
        private const float TetherAmplitude = 0.6f;
        private const float TetherLength = 0.06f;

        private static readonly AccessTools.FieldRef<CuttingToolController, CuttingController> CuttingOf =
            AccessTools.FieldRefAccess<CuttingToolController, CuttingController>("m_CuttingController");

        private static float next;
        private static int logs;

        // From VrFrame, once per frame.
        public static void Update()
        {
            if (!VrXr.IsRunning || !VrAim.Active || Time.unscaledTime < next) return;
            next = Time.unscaledTime + Every;
            Cutter();
            Grapple();
        }

        private static void Cutter()
        {
            var tool = GameRefs.Cutter;
            var hand = Holding(EquipmentController.Equipment.CuttingTool);
            if (tool == null || hand == null) return;
            var cutting = CuttingOf(tool);
            if (cutting == null || !cutting.IsCutting) return;
            hand.Input.Buzz(tool.CurrentMode == CuttingToolController.CutterMode.Scalpel ? Stinger : Splitsaw, Length);
        }

        private static void Grapple()
        {
            var grapple = GameRefs.Grapple;
            var hand = Holding(EquipmentController.Equipment.GrappleHook);
            if (grapple == null || hand == null || VrTetherMode.On || grapple.GrappledRigidbody == null) return;
            hand.Input.Buzz(grapple.IsGrapplingHeavy ? GrappleHeavy : grapple.IsGrapplingMedium ? GrappleMedium : GrappleLight, Length);
        }

        // The hand that works that tool, or null.
        private static VrHand Holding(EquipmentController.Equipment tool)
        {
            foreach (var hand in VrHand.Both)
                if (hand.HoldsTool && hand.Tool == tool) return hand;
            return null;
        }

        public static void TetherFired()
        {
            if (!VrXr.IsRunning) return;
            var hand = Holding(EquipmentController.Equipment.GrappleHook) ?? VrHand.GameToolHand;
            hand.Input.Buzz(TetherAmplitude, TetherLength);
            if (logs++ < 10) Plugin.Log.LogInfo($"Tool haptics: tether end fired, {hand.Name} hand buzzes");
        }
    }

    [HarmonyPatch(typeof(TetherController), "SpawnFireFX")]
    internal static class TetherFirePatch
    {
        private static void Postfix() => VrToolHaptics.TetherFired();
    }
}
