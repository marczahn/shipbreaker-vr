using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The tools in the controller, as Raik asked, as a warning before the tool overdoes it: the hand holding the cutter
    // hums with the Splitsaw while it cuts, and with the Stinger only once the cutter is half way to overheating, from
    // faint to strong just before it overheats (CuttingToolController.CurrentHeatPercent); the hand holding the grapple
    // hums with the pull on the part, the further the part lags behind where the rope holds it, the stronger, strongest
    // just before the rope lets go (VrGrappleHold.Lag against the rope's stretch); and each tether end the grapple fires
    // gives a short buzz (TetherController.SpawnFireFX).
    internal static class VrToolHaptics
    {
        // Each pulse a little longer than the time to the next, so the hum does not break up.
        private const float Every = 0.05f;
        private const float Length = 0.08f;
        private const float Splitsaw = 0.6f;
        // The Stinger from HeatFrom of its heat on, StingerLeast to StingerMost at overheating.
        private const float HeatFrom = 0.5f;
        private const float StingerLeast = 0.08f;
        private const float StingerMost = 0.9f;
        // The grapple: GrappleLeast holding without pull, up to GrappleMost when the lag reaches the rope's stretch.
        private const float GrappleLeast = 0.04f;
        private const float GrappleMost = 0.9f;
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
            if (tool.CurrentMode != CuttingToolController.CutterMode.Scalpel)
            {
                hand.Input.Buzz(Splitsaw, Length);
                return;
            }
            var heat = tool.CurrentHeatPercent;
            if (heat > 1.5f) heat /= 100f;
            LogHeat(heat);
            if (heat < HeatFrom) return;
            hand.Input.Buzz(Mathf.Lerp(StingerLeast, StingerMost, Mathf.InverseLerp(HeatFrom, 1f, heat)), Length);
        }

        private static void Grapple()
        {
            var grapple = GameRefs.Grapple;
            var hand = Holding(EquipmentController.Equipment.GrappleHook);
            var holds = grapple != null && hand != null && !VrTetherMode.On && grapple.GrappledRigidbody != null;
            if (!holds)
            {
                // The pull at letting go: how far the rope stretches before it lets go, to set GrappleMost by.
                if (pulling && pullLogs++ < 20) Plugin.Log.LogInfo($"Tool haptics: grapple let go at a lag of {lastLag:0.00} m (rope stretch {lastStretch:0.00} m, most {mostLag:0.00} m)");
                pulling = false;
                mostLag = 0f;
                return;
            }
            var stretch = VrGrappleHold.StretchLength > 0.01f ? VrGrappleHold.StretchLength : 1f;
            lastLag = VrGrappleHold.Lag;
            lastStretch = stretch;
            mostLag = Mathf.Max(mostLag, lastLag);
            pulling = true;
            hand.Input.Buzz(Mathf.Lerp(GrappleLeast, GrappleMost, Mathf.Clamp01(lastLag / stretch)), Length);
        }

        private static bool pulling;
        private static float lastLag;
        private static float lastStretch;
        private static float mostLag;
        private static int pullLogs;
        private static int heatLogs;
        private static float nextHeatLog;

        // While the Stinger cuts, its heat now and then, to check the scale of CurrentHeatPercent.
        private static void LogHeat(float heat)
        {
            if (heatLogs >= 20 || Time.unscaledTime < nextHeatLog) return;
            nextHeatLog = Time.unscaledTime + 2f;
            heatLogs++;
            Plugin.Log.LogInfo($"Tool haptics: Stinger heat {heat:0.00}");
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
