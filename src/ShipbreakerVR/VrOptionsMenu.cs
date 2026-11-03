using System.Linq;
using BBI.Unity.Game;
using BBI.Unity.Game.UI.Config.Controllers;
using Doozy.Engine.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // "VR options" above "Options" in the main menu and in the pause menu, as Raik asked: it opens the game's options
    // screen with the VR tabs instead of the game's (VrOptionsTab.Show). The entry is a copy of the game's own Options
    // button, so it looks and selects like it; its click sets VrMode and then clicks the original. The options screen
    // clears VrMode when it closes, so "Options" opens the game's tabs again.
    // Both Options buttons are Doozy buttons ('Button - OPTIONS' in the main menu, 'Options_Button' in the pause menu),
    // neither a MainMenuButton nor in PauseMenuController's button list, so they are found by their text's localization
    // ID, with the label as a fallback.
    internal static class VrOptionsMenu
    {
        public static bool VrMode;

        private const string Name = "ShipbreakerVR Options Button";
        // The localization ID of "Options" on the main and the pause menu's buttons (seen in the game, 2026-11-03).
        private const string OptionsText = "LocID:69";

        private static readonly AccessTools.FieldRef<LocalizedTextMeshProUGUI, string> LocId =
            AccessTools.FieldRefAccess<LocalizedTextMeshProUGUI, string>("m_LocalizationID");

        private static float nextSearch;
        private static int logs;

        private static string Label => VrOptions.L("VR options", "VR-Optionen");

        // From VrFrame, once per frame: once a second outside a shift's play (menus, the pause menu, the station), each
        // Options button without its copy gets one.
        public static void Update()
        {
            if (Time.unscaledTime < nextSearch) return;
            nextSearch = Time.unscaledTime + 1f;
            if (GameSession.CurrentGameState == GameSession.GameState.Gameplay) return;
            foreach (var text in Object.FindObjectsOfType<TextMeshProUGUI>())
            {
                var button = (Component)text.GetComponentInParent<UIButton>() ?? text.GetComponentInParent<Button>();
                if (button == null || button.name == Name || !IsOptions(text) || HasCopy(button.transform)) continue;
                if (text.GetComponentInParent<UnifiedOptionsScreen>() != null) continue;
                Add(button);
            }
        }

        private static bool IsOptions(TextMeshProUGUI text)
        {
            var localized = text.GetComponentInParent<LocalizedTextMeshProUGUI>();
            if (localized != null && LocId(localized) == OptionsText) return true;
            var label = text.text != null ? text.text.Trim() : "";
            return label.Equals("Options", System.StringComparison.OrdinalIgnoreCase) || label.Equals("Optionen", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasCopy(Transform button) =>
            button.parent != null && button.parent.Cast<Transform>().Any(child => child.name == Name);

        // A copy of an Options button: its click sets VrMode and then clicks the original.
        private static void Add(Component source)
        {
            var copy = Copy(source.gameObject);
            var doozy = source.GetComponent<UIButton>();
            var plain = source.GetComponent<Button>();
            UnityAction open = () =>
            {
                VrMode = true;
                if (doozy != null) doozy.ExecuteClick();
                else if (plain != null) plain.onClick.Invoke();
            };
            foreach (var b in copy.GetComponentsInChildren<UIButton>(true))
            {
                var action = b.OnClick.OnTrigger;
                action.GameEvents.Clear();
                if (action.Event == null) action.Event = new UnityEvent();
                Silence(action.Event);
                action.Event.AddListener(open);
            }
            foreach (var b in copy.GetComponentsInChildren<Button>(true))
            {
                b.onClick.RemoveAllListeners();
                Silence(b.onClick);
                b.onClick.AddListener(open);
            }
            Log($"'{Label}' added above '{VrUtil.Path(source.transform)}' ({(doozy != null ? "Doozy button" : "button")})");
        }

        // A copy of the button just above it, with our label instead of its localized one. Without a layout group the
        // buttons below make room by the distance between two buttons.
        private static GameObject Copy(GameObject source)
        {
            var parent = source.transform.parent;
            var copy = Object.Instantiate(source, parent, false);
            copy.name = Name;
            copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex());
            foreach (var localized in copy.GetComponentsInChildren<LocalizedTextMeshProUGUI>(true)) Object.DestroyImmediate(localized);
            foreach (var text in copy.GetComponentsInChildren<TextMeshProUGUI>(true)) VrOptions.SetText(text, Label);
            if (parent != null && parent.GetComponent<LayoutGroup>() == null)
            {
                var below = parent.Cast<Transform>().Where(child => child != copy.transform && child.GetSiblingIndex() > copy.transform.GetSiblingIndex()
                    && child.gameObject.activeSelf).OfType<RectTransform>().ToList();
                var next = below.FirstOrDefault(child => child != source.transform);
                var step = next != null ? ((RectTransform)source.transform).anchoredPosition - next.anchoredPosition
                    : new Vector2(0f, ((RectTransform)source.transform).rect.height);
                foreach (var child in below) child.anchoredPosition -= step;
                Log($"no layout group under '{parent.name}': {below.Count} button(s) moved by {step}");
            }
            return copy;
        }

        private static void Silence(UnityEventBase e)
        {
            for (var i = 0; i < e.GetPersistentEventCount(); i++) e.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        private static void Log(string text)
        {
            if (logs++ < 20) Plugin.Log.LogInfo($"VR options entry, {text}");
        }
    }
}
