using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // No prompt for a lever turned by hand (VrLever): the game shows what to press for the interactable offered, and A
    // does not use such a lever (Raik). The prompt's group is made invisible after the game's own Update, and the game's
    // own value is put back before its next one, so the game fades it as before and any other prompt shows as it would.
    // Only while the hands can work it: with holding on switched off (Steuerung/Festhalten) or the tools not aimed with
    // the hand, no hand takes hold of a lever, the game offers it from the view and A uses it, so its prompt shows
    // again (Raik, 2026-11-23: where an option switches the hand's way off and the button does it, the prompt is back).
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

        static VrLeverPrompt() => VrSession.OnUnload("lever prompt", Release);

        // A prompt hidden when the mod is unloaded shows again: the prefix that puts it back is gone then.
        private static void Release()
        {
            if (hidden != null) hidden.alpha = gameAlpha;
            hidden = null;
        }

        // That group's alpha as the game has it, also while it is hidden here (VrStowPrompt keeps it to put it back).
        internal static float GameAlpha(CanvasGroup group) => group != null && group == hidden ? gameAlpha : group != null ? group.alpha : 1f;

        // Every lever and button is worked by hand now (Raik): their prompts go, and the game's own grab is left out for
        // them (VrInteractGrab). Things to collect and breakers to remove keep both.
        public static bool WorkedByHand(InteractableObject control)
        {
            if (control == null || VrKiosk.UsedFromView(control)) return false;
            if (VrLever.Turnable(control) || VrNacelle.Is(control)) return true;
            var type = VrLever.TypeName(control);
            return type.StartsWith("Lever") || type.StartsWith("PushButton");
        }

        // What a hand holds to pull at, where the prompt's button does nothing meanwhile (VrToolInput leaves the
        // interaction out): a demo charge on a wall, which the hand pulls off (VrChargeDisarm), and a device stripped
        // for its part (VrPullOff). Their prompts named a button that did nothing (Raik, 2026-11-23: hide them).
        private static bool PulledByHand(InteractableObject control)
        {
            if (control == null) return false;
            if (VrPullOff.Extracting && VrPullOff.Extractable(control)) return true;
            return VrChargeDisarm.Holds && VrLever.TypeName(control).StartsWith("DemoCharge_Disarm");
        }

        private static void Prefix()
        {
            if (hidden != null) hidden.alpha = gameAlpha;
            hidden = null;
        }

        private static void Postfix(InteractionUIController __instance)
        {
            if (!VrXr.IsRunning || !VrAim.Active || VrSettings.Climbing == null || !VrSettings.Climbing.Value) return;
            var control = Current(__instance);
            if (!WorkedByHand(control) && !PulledByHand(control)) return;
            var group = Group(__instance);
            if (group == null) return;
            gameAlpha = group.alpha;
            group.alpha = 0f;
            hidden = group;
            if (logs++ < 8) Plugin.Log.LogInfo($"Lever: no prompt for '{control.name}' ({VrLever.TypeName(control)}), it is worked by hand");
        }
    }
}
