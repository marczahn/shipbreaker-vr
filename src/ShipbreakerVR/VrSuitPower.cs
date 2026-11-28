using BBI;
using HarmonyLib;

namespace ShipbreakerVR
{
    // The suit shut down by the campaign (the industrial action, act 3: HUDRebootController.ShutDownSuit from the PAT
    // PAT_CMP_A3_SC07_Suit_Off, back on with RebootSuit). The game then stops the thrust and the roll keys
    // (OrientationController and ThrustController, mRebootShutdown from the HUDRebootEvent Off until On). Raik
    // (2026-11-28): the thrust stopped in VR, but he could still turn; with the suit off the sticks and the hand's tilt
    // do not turn or roll at all, and straightening up is off too, so only the head moves (VrControls, VrFlightComfort).
    [HarmonyPatch(typeof(OrientationController), "OnHUDReboot")]
    internal static class VrSuitPower
    {
        private static readonly AccessTools.FieldRef<OrientationController, bool> Shutdown =
            AccessTools.FieldRefAccess<OrientationController, bool>("mRebootShutdown");
        private static OrientationController controller;
        private static bool was;

        // True while the game holds the suit shut down; a new level's controller starts with it on.
        public static bool Off => controller != null && Shutdown(controller);

        private static void Postfix(OrientationController __instance)
        {
            controller = __instance;
            var off = Shutdown(__instance);
            if (off == was) return;
            was = off;
            Plugin.Log.LogInfo(off
                ? "Suit power: the suit is shut down, no turning, rolling or straightening up, only the head moves"
                : "Suit power: the suit is back on, turning works again");
        }
    }
}
