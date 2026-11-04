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
    // ID.
    internal static class VrOptionsMenu
    {
        public static bool VrMode;

        private const string Name = "ShipbreakerVR Options Button";
        // The localization ID of "Options" on the main and the pause menu's buttons (seen in the game, 2026-11-03).
        private const string OptionsText = "LocID:69";

        private static readonly AccessTools.FieldRef<LocalizedTextMeshProUGUI, string> LocId =
            AccessTools.FieldRefAccess<LocalizedTextMeshProUGUI, string>("m_LocalizationID");

        private static float nextSearch;
        // How long after a change of scenes the menus are searched, in seconds.
        private const float SearchFor = 20f;
        private static int lastScenes = int.MinValue;
        private static float searchUntil;
        private static int logs;

        private static string Label => VrOptions.L("VR options", "VR-Optionen");

        // From VrFrame, once per frame: every two seconds each Options button without its copy gets one, also while its
        // menu is still hidden (the pause menu during a shift), so the entry is there when the menu opens. Before, it was
        // added only once the menu was open, a moment after it showed (Raik).
        public static void Update()
        {
            // Only for a while after a scene came or went, where new menus appear: looking through every localized text
            // costs a little, and every two seconds all through a shift it is not worth it.
            var scenes = UnityEngine.SceneManagement.SceneManager.sceneCount * 31 + UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scenes != lastScenes)
            {
                lastScenes = scenes;
                searchUntil = Time.unscaledTime + SearchFor;
            }
            if (Time.unscaledTime < nextSearch || Time.unscaledTime > searchUntil) return;
            nextSearch = Time.unscaledTime + 2f;
            foreach (var text in Resources.FindObjectsOfTypeAll<LocalizedTextMeshProUGUI>())
            {
                if (!text.gameObject.scene.IsValid() || LocId(text) != OptionsText) continue;
                var button = Up<UIButton>(text.transform) ?? (Component)Up<Button>(text.transform);
                if (button == null || button.name == Name || HasCopy(button.transform) || Up<UnifiedOptionsScreen>(text.transform) != null) continue;
                Add(button);
            }
        }

        // The nearest component of that type on the transform or a parent, active or not.
        private static T Up<T>(Transform from) where T : Component
        {
            for (var t = from; t != null; t = t.parent)
            {
                var found = t.GetComponent<T>();
                if (found != null) return found;
            }
            return null;
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
