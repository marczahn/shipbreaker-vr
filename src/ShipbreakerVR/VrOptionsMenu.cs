using System.Linq;
using BBI.Unity.Game;
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
    // button, so it looks and selects like it; its click sets VrMode and then does what the Options button does. The
    // options screen clears VrMode when it closes, so "Options" opens the game's tabs again.
    internal static class VrOptionsMenu
    {
        public static bool VrMode;

        private const string Name = "ShipbreakerVR Options Button";

        private static readonly AccessTools.FieldRef<PauseMenuController, PauseMenuButton[]> PauseButtons =
            AccessTools.FieldRefAccess<PauseMenuController, PauseMenuButton[]>("m_PauseMenuButtons");
        private static readonly AccessTools.FieldRef<PauseMenuButton, PauseMenuButton.ButtonType> PauseType =
            AccessTools.FieldRefAccess<PauseMenuButton, PauseMenuButton.ButtonType>("m_ButtonType");
        private static readonly System.Reflection.MethodInfo PauseClick =
            AccessTools.Method(typeof(PauseMenuController), "OnButtonClick", new[] { typeof(PauseMenuButton.ButtonType) });
        private static readonly AccessTools.FieldRef<MainMenuButton, LocalizedTextMeshProUGUI> MainText =
            AccessTools.FieldRefAccess<MainMenuButton, LocalizedTextMeshProUGUI>("m_LocButtonText");
        private static readonly AccessTools.FieldRef<LocalizedTextMeshProUGUI, string> LocId =
            AccessTools.FieldRefAccess<LocalizedTextMeshProUGUI, string>("m_LocalizationID");

        private static float nextSearch;
        private static int logs;

        private static string Label => VrOptions.L("VR options", "VR-Optionen");

        // From VrFrame, once per frame: the main menu's Options button gets its copy once it is there.
        public static void Update()
        {
            if (Time.unscaledTime < nextSearch) return;
            nextSearch = Time.unscaledTime + 1f;
            foreach (var button in Object.FindObjectsOfType<MainMenuButton>())
            {
                if (button.name == Name || !IsOptions(button) || HasCopy(button.transform)) continue;
                AddToMainMenu(button);
            }
        }

        // The main menu's Options button, by its text's localization ID.
        private static bool IsOptions(MainMenuButton button)
        {
            var text = MainText(button);
            var id = text != null ? LocId(text) : null;
            return id != null && id.IndexOf("option", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasCopy(Transform button) =>
            button.parent != null && button.parent.Cast<Transform>().Any(child => child.name == Name);

        private static void AddToMainMenu(MainMenuButton options)
        {
            var copy = Copy(options.gameObject);
            var source = options.GetComponentInChildren<UIButton>(true);
            UnityAction open = () =>
            {
                VrMode = true;
                if (source != null) source.ExecuteClick();
            };
            var uiButtons = copy.GetComponentsInChildren<UIButton>(true);
            foreach (var b in uiButtons)
            {
                var action = b.OnClick.OnTrigger;
                action.GameEvents.Clear();
                if (action.Event == null) action.Event = new UnityEvent();
                Silence(action.Event);
                action.Event.AddListener(open);
            }
            if (uiButtons.Length == 0) Redirect(copy, open);
            Log($"main menu: '{Label}' added above '{VrUtil.Path(options.transform)}' ({(source != null ? "Doozy button" : "no Doozy button")})");
        }

        // From PauseMenuController.Start: a copy of its Options button.
        public static void AddToPauseMenu(PauseMenuController controller)
        {
            var buttons = PauseButtons(controller);
            var options = buttons?.FirstOrDefault(b => b != null && PauseType(b) == PauseMenuButton.ButtonType.Options);
            if (options == null || HasCopy(options.transform))
            {
                if (options == null) Log("pause menu: no Options button found");
                return;
            }
            var copy = Copy(options.gameObject);
            Redirect(copy, () =>
            {
                VrMode = true;
                PauseClick?.Invoke(controller, new object[] { PauseMenuButton.ButtonType.Options });
            });
            Log($"pause menu: '{Label}' added above '{VrUtil.Path(options.transform)}'");
        }

        // A copy of the button just above it, with our label instead of its localized one. Without a layout group the
        // buttons below make room by the distance between two buttons.
        private static GameObject Copy(GameObject source)
        {
            var parent = source.transform.parent;
            var copy = Object.Instantiate(source, parent, false);
            copy.name = Name;
            var index = source.transform.GetSiblingIndex();
            copy.transform.SetSiblingIndex(index);
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

        // The plain buttons of a copy: their clicks (persistent ones off, runtime ones gone) replaced by open.
        private static void Redirect(GameObject copy, UnityAction open)
        {
            foreach (var b in copy.GetComponentsInChildren<Button>(true))
            {
                b.onClick.RemoveAllListeners();
                Silence(b.onClick);
                b.onClick.AddListener(open);
            }
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

    [HarmonyPatch(typeof(PauseMenuController), "Start")]
    internal static class PauseMenuOptionsPatch
    {
        private static void Postfix(PauseMenuController __instance) => VrOptionsMenu.AddToPauseMenu(__instance);
    }
}
