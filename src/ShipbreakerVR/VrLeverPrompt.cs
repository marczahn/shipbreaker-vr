using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // No prompt for a lever turned by hand (VrLever): the game shows what to press for the interactable offered, and A
    // does not use such a lever (Raik). The prompt's group is made invisible after the game's own Update, and the game's
    // own value is put back before its next one, so the game fades it as before and any other prompt shows as it would.
    [HarmonyPatch(typeof(InteractionUIController), "Update")]
    internal static class VrLeverPrompt
    {
        private static readonly AccessTools.FieldRef<InteractionUIController, InteractableObject> Current =
            AccessTools.FieldRefAccess<InteractionUIController, InteractableObject>("mCurrentInteractable");
        private static readonly AccessTools.FieldRef<InteractionUIController, CanvasGroup> Group =
            AccessTools.FieldRefAccess<InteractionUIController, CanvasGroup>("m_InteractionGroup");

        private static CanvasGroup hidden;
        private static float gameAlpha;
        private static int logs;

        private static void Prefix()
        {
            if (hidden != null) hidden.alpha = gameAlpha;
            hidden = null;
        }

        private static void Postfix(InteractionUIController __instance)
        {
            if (!VrXr.IsRunning) return;
            var control = Current(__instance);
            if (!VrLever.Turnable(control) && !VrNacelle.Is(control)) return;
            var group = Group(__instance);
            if (group == null) return;
            gameAlpha = group.alpha;
            group.alpha = 0f;
            hidden = group;
            if (logs++ < 5) Plugin.Log.LogInfo($"Lever: no prompt for '{control.name}', it is turned by hand");
        }
    }
}
