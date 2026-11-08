using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Only what a hand holds can be used (Raik): a door switch, an air conditioner, a data cassette or the bunny figure
    // to collect is offered, with its prompt, only while one hand holds it or holds on right at it (VrClimb, a grip near a
    // control goes onto the control). Not from afar any more, and not just what the middle of the view rests on: the game
    // picks the closest interactable along the view (InteractionController.UpdateClosestInteractable), and here that pick
    // becomes what a hand holds, or nothing. While aiming with the hand and with holding on (Steuerung/Festhalten) on;
    // without holding on nothing could be grabbed, so the game's own pick stays.
    [HarmonyPatch(typeof(InteractionController), "UpdateClosestInteractable")]
    internal static class VrInteractHeld
    {
        private static readonly AccessTools.FieldRef<InteractionController, InteractableObject> Closest =
            AccessTools.FieldRefAccess<InteractionController, InteractableObject>("mClosestInteractable");
        private static readonly AccessTools.FieldRef<InteractionController, RaycastHit?> ClosestHit =
            AccessTools.FieldRefAccess<InteractionController, RaycastHit?>("mClosestInteractableHitInfo");

        private static InteractableObject loggedHeld;
        private static InteractableObject loggedSeen;
        private static int logs;

        private static void Postfix(InteractionController __instance)
        {
            if (!VrXr.IsRunning || !VrAim.Active || VrSettings.Climbing == null || !VrSettings.Climbing.Value) return;
            // A button pushed in by hand offers its control for a moment (VrButtons), unless a hand works a lever or switch:
            // that one is what a press is for (review). Unity's null check, for a control destroyed meanwhile.
            var button = VrButtons.Offered;
            var held = button != null && !VrLever.Holds && !VrNacelle.Holds ? button : VrClimb.HeldInteractable();
            // A nacelle switch being completed stays offered, also once the hand let go of it (VrNacelle.Finish).
            var finishing = VrNacelle.Completing;
            if (finishing != null) held = finishing;
            var closest = Closest(__instance);
            if (held == closest) return;
            // The game's own hit stays only for the thing it found itself; a held thing it did not see has none.
            Closest(__instance) = held;
            ClosestHit(__instance) = null;
            if ((held != loggedHeld || closest != loggedSeen) && logs++ < 30)
                Plugin.Log.LogInfo(held != null
                    ? $"Interaction: '{held.name}' offered because a hand holds it{(closest != null ? $", not '{closest.name}' in view" : "")}"
                    : $"Interaction: '{closest.name}' in view not offered, no hand holds it");
            loggedHeld = held;
            loggedSeen = closest;
        }
    }
}
