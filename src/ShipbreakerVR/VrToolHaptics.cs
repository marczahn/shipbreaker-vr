using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The tools in the controller, as Raik asked, as a warning before the tool overdoes it: the hand holding the cutter
    // hums with the Splitsaw while it cuts, and with the Stinger only once the cutter is half way to overheating, from
    // faint to strong just before it overheats (CuttingToolController.CurrentHeatPercent); the hand holding the grapple
    // hums the closer the part comes to where the grapple loses it, strongest just before: the game snaps the rope when
    // the part stays out of the aim's view a while (GrapplingHook.CheckIfObjectInBounds, mOutOfBoundsTimer) or when it is
    // too far (MaxDistance), so the hum follows how far off the aim's middle the part is, and from NearFar of the range
    // how far away; and each tether end the grapple fires gives a short buzz (TetherController.SpawnFireFX).
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
        // The grapple: GrappleLeast with the part in the aim's middle, up to GrappleMost at the edge of its view or the
        // end of its range, and GrappleMost while the part is out of view.
        private const float GrappleLeast = 0.04f;
        private const float GrappleMost = 0.9f;
        private const float NearFar = 0.8f;
        private const float TetherAmplitude = 0.6f;
        private const float TetherLength = 0.06f;

        private static readonly AccessTools.FieldRef<CuttingToolController, CuttingController> CuttingOf =
            AccessTools.FieldRefAccess<CuttingToolController, CuttingController>("m_CuttingController");
        private static readonly AccessTools.FieldRef<GrapplingHook, float> OutOfBounds = AccessTools.FieldRefAccess<GrapplingHook, float>("mOutOfBoundsTimer");

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
                // Where the part was when the grapple let go, to check where the hum should be strongest.
                if (pulling && pullLogs++ < 20) Plugin.Log.LogInfo($"Tool haptics: grapple let go, last off the aim's middle {lastOff:0.00}, at {lastFar:0.00} of its range, out of view {lastOut:0.00} s");
                pulling = false;
                return;
            }
            var camera = VrAim.CameraFor((int)GameTools.Family.Grapple);
            var part = grapple.GrappledRigidbody.worldCenterOfMass;
            lastOff = 1f;
            if (camera != null)
            {
                var view = camera.WorldToViewportPoint(part);
                if (view.z > 0f) lastOff = Mathf.Clamp01(2f * Mathf.Max(Mathf.Abs(view.x - 0.5f), Mathf.Abs(view.y - 0.5f)));
            }
            var range = grapple.MaxDistance;
            lastFar = range > 0f && camera != null ? Vector3.Distance(camera.transform.position, part) / range : 0f;
            lastOut = OutOfBounds(grapple);
            pulling = true;
            var pull = Mathf.Max(lastOff * lastOff, Mathf.InverseLerp(NearFar, 1f, lastFar));
            // Out of view with the game counting towards the snap; the timer alone may stay set after the part is back.
            if (lastOut > 0f && lastOff >= 1f) pull = 1f;
            hand.Input.Buzz(Mathf.Lerp(GrappleLeast, GrappleMost, pull), Length);
        }

        private static bool pulling;
        private static float lastOff;
        private static float lastFar;
        private static float lastOut;
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
