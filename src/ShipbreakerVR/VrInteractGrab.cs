using BBI.Unity.Game;
using HarmonyLib;

namespace ShipbreakerVR
{
    // Using a control, the game starts a grab of its own (GrabController.StartInteractGrab): its hand reaches for the
    // control's HandTarget and the player's body is moved to where that hand can reach it (HandGrab.DesiredPlayerPosition).
    // In VR the hands work the controls themselves (VrClimb, VrLever, VrNacelle, VrButtons), so that grab is not started:
    // turning the airlock lever and letting go, the body was moved to where the game's animation would have put it
    // (Raik, 2026-11-08). Only for levers and push buttons (VrLeverPrompt.WorkedByHand); they are used all the same.
    // While aiming with the hand and with holding on (Steuerung/Festhalten) on, as VrInteractHeld.
    [HarmonyPatch(typeof(GrabController), "StartInteractGrab")]
    internal static class VrInteractGrab
    {
        private static int logs;

        private static bool Prefix(InteractableObject __0)
        {
            if (!VrXr.IsRunning || !VrAim.Active || VrSettings.Climbing == null || !VrSettings.Climbing.Value) return true;
            // Only for levers and buttons, which the hands work (review): things to collect and breakers keep the game's
            // grab, which may bring the thing to the hand, and so does anything that waits for it.
            if (__0 == null || __0.WaitForGrab || !VrLeverPrompt.WorkedByHand(__0)) return true;
            if (logs++ < 10) Plugin.Log.LogInfo($"Interaction: the game's own grab of '{(__0 != null ? __0.name : "?")}' left out, the hand works it");
            return false;
        }
    }
}
