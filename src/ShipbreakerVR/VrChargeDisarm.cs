using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // A demo charge on a wall is taken off through a little game of timing (DemoChargeRetrievalController.OnInteractStart
    // starts a GolfSwingController: a ring fills, and the input has to come inside its safe zone; outside it the charge
    // goes off). In VR that input never came, so the charge could not be disarmed (Raik, 2026-11-18). As he asked, the
    // interact button (A or X) pressed once more takes the charge off, wherever the ring stands: the game's own way out
    // through its success. Only for a ring that a demo charge started; and not within a moment of its start, so the
    // press that started it does not end it.
    internal static class VrChargeDisarm
    {
        private const float StartsWithin = 1f;
        private const float NotBefore = 0.25f;

        private static readonly AccessTools.FieldRef<GolfSwingController, bool> InSafeZone =
            AccessTools.FieldRefAccess<GolfSwingController, bool>("mInSafeZone");
        private static readonly AccessTools.FieldRef<GolfSwingController, float> DelayTime =
            AccessTools.FieldRefAccess<GolfSwingController, float>("mCurrentDelayTime");
        private static readonly AccessTools.FieldRef<GolfSwingController, float> DelayAmount =
            AccessTools.FieldRefAccess<GolfSwingController, float>("mStartDelayAmount");

        private static float chargeAt = -10f;
        private static float ringAt;
        private static bool mine;
        private static int logs;

        // A demo charge is being interacted with: a ring that starts right after is its own.
        public static void ChargeStarts() => chargeAt = Time.unscaledTime;

        public static void RingStarts()
        {
            mine = VrXr.IsRunning && Time.unscaledTime - chargeAt < StartsWithin;
            ringAt = Time.unscaledTime;
            if (mine && logs++ < 20) Plugin.Log.LogInfo("Charge disarm: the ring is up, the interact button once more takes the charge off");
        }

        public static void RingEnds() => mine = false;

        // Each frame while the ring runs.
        public static void Tick(GolfSwingController ring)
        {
            if (!mine || !ring.enabled || Time.unscaledTime - ringAt < NotBefore) return;
            if (!VrInput.Left.Primary.Pressed && !VrInput.Right.Primary.Pressed) return;
            // The game's own input, counted as in time whatever the ring shows, also during its starting delay.
            DelayTime(ring) = DelayAmount(ring);
            InSafeZone(ring) = true;
            if (logs++ < 20) Plugin.Log.LogInfo("Charge disarm: taken off with the interact button");
            ring.OnInput();
        }
    }

    [HarmonyPatch(typeof(DemoChargeRetrievalController), nameof(DemoChargeRetrievalController.OnInteractStart))]
    internal static class ChargeDisarmStartPatch
    {
        private static void Prefix() => VrChargeDisarm.ChargeStarts();
    }

    [HarmonyPatch(typeof(GolfSwingController), "OnListenerRegistered")]
    internal static class ChargeDisarmRingPatch
    {
        private static void Postfix() => VrChargeDisarm.RingStarts();
    }

    [HarmonyPatch(typeof(GolfSwingController), "Update")]
    internal static class ChargeDisarmTickPatch
    {
        private static void Postfix(GolfSwingController __instance) => VrChargeDisarm.Tick(__instance);
    }

    [HarmonyPatch(typeof(GolfSwingController), "ExitGolfSwingScreen")]
    internal static class ChargeDisarmEndPatch
    {
        private static void Postfix() => VrChargeDisarm.RingEnds();
    }
}
