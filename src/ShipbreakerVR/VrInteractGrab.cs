using BBI.Unity.Game;
using HarmonyLib;

namespace ShipbreakerVR
{
    // Using a control, the game starts a grab of its own (GrabController.StartInteractGrab): its hand reaches for the
    // control's HandTarget and the player's body is moved to where that hand can reach it (HandGrab.DesiredPlayerPosition).
    // In VR the hands work the controls themselves (VrClimb, VrLever, VrNacelle, VrButtons), so that grab is not started:
    // turning the airlock lever and letting go, the body was moved to where the game's animation would have put it
    // (Raik, 2026-11-08). The control is used all the same; one that waits for the grab (WaitForGrab) keeps it.
    // While aiming with the hand and with holding on (Steuerung/Festhalten) on, as VrInteractHeld.
    [HarmonyPatch(typeof(GrabController), "StartInteractGrab")]
    internal static class VrInteractGrab
    {
        private static int logs;

        private static bool Prefix(InteractableObject __0)
        {
            if (!VrXr.IsRunning || !VrAim.Active || VrSettings.Climbing == null || !VrSettings.Climbing.Value) return true;
            // Things to collect may wait for the game's hand to reach them: those keep their grab.
            if (__0 == null || __0.WaitForGrab) return true;
            if (logs++ < 10) Plugin.Log.LogInfo($"Interaction: the game's own grab of '{(__0 != null ? __0.name : "?")}' left out, the hand works it");
            return false;
        }
    }
}
