using BBI.Unity.Game;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ShipbreakerVR
{
    // With stowing over the shoulder switched off (Steuerung/UeberDieSchulterEinpacken), an AI node or the radio in a hand
    // is collected with A or X of that hand, as the game's own things to collect are with their prompt's button. The
    // game's things show that prompt; the node and the radio are the mod's and had none, so nothing told of the button
    // (Raik, 2026-11-23: "taking it with the button instead of over the shoulder, the prompt has to come back").
    // The game's own prompt is used for it (InteractionUIController: the button's picture, the action and the thing's
    // name, at its place on the HUD), filled in by the mod while a hand holds such a thing and the game shows no prompt
    // of its own there: the game switches that widget on with an interaction and off when it ends, and fills it only as
    // an interaction begins or changes, so in between it is free. An interaction that begins takes it over at once.
    internal static class VrStowPrompt
    {
        static VrStowPrompt() => VrSession.OnUnload("stow prompt", Release);

        private static readonly AccessTools.FieldRef<InteractionUIController, CanvasGroup> Group =
            AccessTools.FieldRefAccess<InteractionUIController, CanvasGroup>("m_InteractionGroup");
        private static readonly AccessTools.FieldRef<InteractionUIController, LocalizedTextMeshProUGUI> ActionLabel =
            AccessTools.FieldRefAccess<InteractionUIController, LocalizedTextMeshProUGUI>("m_ActionLabel");
        private static readonly AccessTools.FieldRef<InteractionUIController, LocalizedTextMeshProUGUI> NameLabel =
            AccessTools.FieldRefAccess<InteractionUIController, LocalizedTextMeshProUGUI>("m_NameLabel");
        private static readonly AccessTools.FieldRef<InteractionUIController, LocalizedTextMeshProUGUI> DescriptionLabel =
            AccessTools.FieldRefAccess<InteractionUIController, LocalizedTextMeshProUGUI>("m_DescriptionLabel");
        private static readonly AccessTools.FieldRef<InteractionUIController, TextMeshProUGUI> ValueLabel =
            AccessTools.FieldRefAccess<InteractionUIController, TextMeshProUGUI>("m_ValueLabel");
        private static readonly AccessTools.FieldRef<InteractionUIController, InteractableObject> Current =
            AccessTools.FieldRefAccess<InteractionUIController, InteractableObject>("mCurrentInteractable");
        private static readonly System.Reflection.MethodInfo ColorByState = AccessTools.Method(typeof(InteractionUIController), "SetUIColorByState");

        private static InteractionUIController ui;
        private static float nextLook;
        private static int offeredFrame = -10;
        private static string offered = "";
        // The widget is filled in by the mod now, and what its group's alpha was before.
        private static bool shown;
        private static float gameAlpha = 1f;
        private static int logs;

        // From VrAiNode and VrRadio, each frame a hand holds that thing and its button would collect it.
        public static void Offer(string name)
        {
            offeredFrame = Time.frameCount;
            offered = name;
        }

        // From VrFrame, after the things that offer.
        public static void Update()
        {
            if (!VrXr.IsRunning || Time.frameCount - offeredFrame > 1)
            {
                Hide();
                return;
            }
            if (ui == null)
            {
                shown = false;
                if (Time.unscaledTime < nextLook) return;
                nextLook = Time.unscaledTime + 2f;
                ui = Object.FindObjectOfType<InteractionUIController>();
                if (ui == null) return;
            }
            // The game's own prompt is up: the widget is its.
            if (Current(ui) != null)
            {
                shown = false;
                return;
            }
            var group = Group(ui);
            if (group == null) return;
            if (!shown)
            {
                shown = true;
                gameAlpha = VrLeverPrompt.GameAlpha(group);
                group.gameObject.SetActive(true);
                ColorByState?.Invoke(ui, new object[] { true });
                ui.SetButtonState(false);
                ui.SetProgress(0f);
                ui.SetComplete(false);
                if (logs++ < 5) Plugin.Log.LogInfo($"Stow prompt: '{offered}' in a hand is collected with its button, told in the game's prompt");
            }
            group.alpha = 1f;
            // Each frame: the labels read their own text again as the widget comes on.
            Write(ActionLabel(ui), VrOptions.L("Collect", "Einsammeln"));
            Write(NameLabel(ui), offered);
            Write(DescriptionLabel(ui), "");
            var value = ValueLabel(ui);
            if (value != null && value.text != "") value.text = "";
        }

        private static void Write(LocalizedTextMeshProUGUI label, string text)
        {
            if (label == null) return;
            var writing = label.TMProText;
            if (writing == null || writing.text == text) return;
            label.ClearLocalizedID();
            writing.text = text;
        }

        // The widget goes off again, as the game leaves it after an interaction, unless the game has it by now.
        private static void Hide()
        {
            if (!shown) return;
            shown = false;
            if (ui == null || Current(ui) != null) return;
            var group = Group(ui);
            if (group == null) return;
            Write(ActionLabel(ui), "");
            Write(NameLabel(ui), "");
            group.alpha = gameAlpha;
            group.gameObject.SetActive(false);
        }

        private static void Release()
        {
            Hide();
            ui = null;
            offeredFrame = -10;
        }
    }
}
