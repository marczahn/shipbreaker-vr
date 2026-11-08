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

        // Every lever and button is worked by hand now (Raik): their prompts go, and the game's own grab is left out for
        // them (VrInteractGrab). Things to collect and breakers to remove keep both.
        public static bool WorkedByHand(InteractableObject control)
        {
            if (control == null) return false;
            if (VrLever.Turnable(control) || VrNacelle.Is(control)) return true;
            var type = VrLever.TypeName(control);
            return type.StartsWith("Lever") || type.StartsWith("PushButton");
        }

        private static void Prefix()
        {
            if (hidden != null) hidden.alpha = gameAlpha;
            hidden = null;
        }

        private static void Postfix(InteractionUIController __instance)
        {
            if (!VrXr.IsRunning) return;
            var control = Current(__instance);
            if (!WorkedByHand(control)) return;
            var group = Group(__instance);
            if (group == null) return;
            gameAlpha = group.alpha;
            group.alpha = 0f;
            hidden = group;
            if (logs++ < 5) Plugin.Log.LogInfo($"Lever: no prompt for '{control.name}' ({VrLever.TypeName(control)}), it is worked by hand");
        }
    }
}
