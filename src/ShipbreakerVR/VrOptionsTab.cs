using System;
using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using BBI.Unity.Game.UI.Config.Controllers;
using Doozy.Engine.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // Adds a "VR" tab to the options screen, next to Gameplay, Video and so on. UnifiedOptionsScreen keeps three parallel
    // arrays (screen controllers, their Doozy UIViews, the tab buttons) and switches tabs by index, so the tab is built by
    // copying the video tab's view and button and appending them to those arrays:
    // - The copied view loses the video controller and the video rows; it gets VrOptionsScreen and the VR rows instead.
    // - The copied button gets its click redirected to ShowTab with the new index.
    // The game hides the previous tab's view by its own means, which do not know the new view, so after every tab switch
    // all other views are hidden here.
    internal static class VrOptionsTab
    {
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, OptionsScreenController[]> Screens =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, OptionsScreenController[]>("m_OptionScreens");
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, UIView[]> Views =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, UIView[]>("m_OptionScreenUIViews");
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, GenericTab[]> Tabs =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, GenericTab[]>("m_OptionTabs");
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, int> Active =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, int>("mActiveOptionScreenIndex");

        private static readonly HashSet<UnifiedOptionsScreen> Done = new HashSet<UnifiedOptionsScreen>();

        public static void Create(UnifiedOptionsScreen unified)
        {
            if (!Done.Add(unified)) return;
            var screens = Screens(unified);
            var views = Views(unified);
            var tabs = Tabs(unified);
            var index = screens == null ? -1 : Array.FindIndex(screens, s => s is VideoOptionsScreenController);
            if (index < 0 || views == null || tabs == null || index >= views.Length || tabs.Length != screens.Length || views.Length != screens.Length)
            {
                Plugin.Log.LogWarning($"Options '{unified.name}': no video tab or unexpected layout (screens {screens?.Length}, views {views?.Length}, tabs {tabs?.Length}, video {index}), no VR tab");
                return;
            }

            GameObject viewObject = null;
            try
            {
                viewObject = CreateView(unified, views[index], out var view, out var screen);
                var tab = CreateTab(unified, tabs[tabs.Length - 1], screens.Length);
                Screens(unified) = screens.Append(screen).ToArray();
                Views(unified) = views.Append(view).ToArray();
                Tabs(unified) = tabs.Append(tab).ToArray();
                // The game's own lists get the scroll and hover fixes too.
                foreach (var scroll in views.Where(v => v != null).SelectMany(v => v.GetComponentsInChildren<ScrollRect>(true)))
                    if (scroll.content != null && scroll.content.GetComponent<VrOptionsList>() == null) scroll.content.gameObject.AddComponent<VrOptionsList>();
                Plugin.Log.LogInfo($"Options '{unified.name}': VR tab added as tab {screens.Length + 1} (view '{viewObject.name}', button '{tab.name}')");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Options '{unified.name}': VR tab failed, VR rows go into the video tab instead: {e}");
                if (viewObject != null) UnityEngine.Object.Destroy(viewObject);
                VrOptions.AppendTo(screens[index]);
            }
        }

        // A copy of the video tab's view with the VR rows instead of the video rows.
        private static GameObject CreateView(UnifiedOptionsScreen unified, UIView source, out UIView view, out OptionsScreenController screen)
        {
            // Copy while deactivated, so nothing in the copy wakes up before it is rebuilt.
            var wasActive = source.gameObject.activeSelf;
            source.gameObject.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
            source.gameObject.SetActive(wasActive);
            copy.name = "ShipbreakerVR Options";
            copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            // If the views' container lays out its children, one more child would squeeze the others (the video list
            // could no longer scroll to its last row) and put the copy elsewhere. Keep it out of the layout, on top of
            // the video view.
            var layout = source.transform.parent != null ? source.transform.parent.GetComponent<LayoutGroup>() : null;
            var element = copy.GetComponent<LayoutElement>() ?? copy.AddComponent<LayoutElement>();
            element.ignoreLayout = true;
            var from = (RectTransform)source.transform;
            var to = (RectTransform)copy.transform;
            to.anchorMin = from.anchorMin;
            to.anchorMax = from.anchorMax;
            to.pivot = from.pivot;
            to.sizeDelta = from.sizeDelta;
            to.anchoredPosition3D = from.anchoredPosition3D;
            Plugin.Log.LogInfo($"Options: video view '{Path(from)}' at {from.anchoredPosition} size {from.rect.size}, container layout {(layout != null ? layout.GetType().Name : "none")}");
            view = copy.GetComponent<UIView>();
            // Views are also found by category and name; the copy must not answer to the video tab's name. A view shows
            // and hides its child views by its own category and name, so children with the parent's name are renamed
            // with it (otherwise the VR list stayed hidden, and would have shown with the video tab).
            var category = view.ViewCategory;
            var name = view.ViewName;
            foreach (var v in copy.GetComponentsInChildren<UIView>(true))
            {
                var old = v.ViewName;
                v.ViewName = v.ViewCategory == category && v.ViewName == name ? "ShipbreakerVR" : "ShipbreakerVR " + v.ViewName;
                if (v != view) Plugin.Log.LogInfo($"Options: child view '{v.name}' ({v.ViewCategory}/{old}) renamed to '{v.ViewName}'");
            }

            var video = copy.GetComponentInChildren<VideoOptionsScreenController>(true);
            if (video == null) throw new InvalidOperationException("the video view holds no VideoOptionsScreenController");
            var t = Traverse.Create(video);
            var container = t.Field("m_OptionsContainer").GetValue<GameObject>();
            if (container == null || !container.transform.IsChildOf(copy.transform)) throw new InvalidOperationException("the video options list is outside its view");
            // The display quality explanation of the video tab. It lies outside the video view, so the copy still points
            // at the original.
            var description = t.Field("m_DisplayPerformanceDescription").GetValue<LocalizedTextMeshProUGUI>();
            var host = video.gameObject;
            UnityEngine.Object.DestroyImmediate(video);

            var gameRows = container.GetComponentsInChildren<OptionEntry>(true);
            var list = gameRows.Length > 0 ? gameRows[0].transform.parent : container.transform;
            // Everything in the list goes, including headings between the rows.
            foreach (Transform child in list.Cast<Transform>().ToList()) UnityEngine.Object.DestroyImmediate(child.gameObject);

            var (_, count) = VrOptions.AddRows(list, source.transform.parent != null ? source.transform.parent : unified.transform, true);
            if (count == 0) throw new InvalidOperationException("no option row to copy");
            FitContent(list as RectTransform);
            list.gameObject.AddComponent<VrOptionsNavigation>();

            var first = list.GetComponentInChildren<OptionEntry>(true);
            var button = Traverse.Create(first).Field("m_BackgroundButton").GetValue<Component>();
            if (button != null) view.SelectedButton = button.gameObject;

            var vr = host.AddComponent<VrOptionsScreen>();
            Traverse.Create(vr).Field("m_OptionsContainer").SetValue(container);
            vr.View = view;
            if (description != null) vr.Hint = CreateHint(description, copy.transform, vr);
            else Plugin.Log.LogInfo("Options: the video tab has no description text, no hints");
            screen = vr;
            copy.SetActive(wasActive);
            return copy;
        }

        // A copy of the description text for the hints, at the same place but inside the VR view, so it shows and hides
        // with it. The original is hidden while the VR tab shows, in case it stays visible across tabs.
        private static TextMeshProUGUI CreateHint(LocalizedTextMeshProUGUI description, Transform view, VrOptionsScreen screen)
        {
            var wasActive = description.gameObject.activeSelf;
            description.gameObject.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(description.gameObject, description.transform.parent, false);
            description.gameObject.SetActive(wasActive);
            copy.name = "ShipbreakerVR Hint";
            copy.transform.SetParent(view, true);
            copy.SetActive(true);
            screen.Original = description.gameObject;
            var text = copy.GetComponent<LocalizedTextMeshProUGUI>().TMProText;
            text.enableWordWrapping = true;
            Plugin.Log.LogInfo($"Options: hints in a copy of '{Path(description.transform)}' (active {description.gameObject.activeInHierarchy}), size {((RectTransform)description.transform).rect.size}");
            return text;
        }

        private static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

        // The list content has a fixed height for the video rows in the game's prefab; make it fit ours.
        private static void FitContent(RectTransform content)
        {
            if (content == null || content.GetComponent<ContentSizeFitter>() != null) return;
            var layout = content.GetComponent<VerticalLayoutGroup>();
            var rows = content.Cast<RectTransform>().ToList();
            var height = rows.Sum(r => r.rect.height);
            if (layout != null) height += layout.spacing * Mathf.Max(0, rows.Count - 1) + layout.padding.vertical;
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        }

        // A copy of the last tab button, placed after it, that opens the VR tab.
        private static GenericTab CreateTab(UnifiedOptionsScreen unified, GenericTab source, int index)
        {
            var copy = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
            copy.name = "ShipbreakerVR Tab";
            copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            var tab = copy.GetComponent<GenericTab>();
            var t = Traverse.Create(tab);
            t.Field("m_TabIndex").SetValue(index);
            t.Field("m_ShouldSelectAtStart").SetValue(false);
            VrOptions.SetText(t.Field("m_TabText").GetValue<TextMeshProUGUI>(), "VR");
            tab.DeselectTab();

            // Without a layout group the copy would sit on top of the source; move it by one tab spacing.
            var parent = source.transform.parent;
            if (parent != null && parent.GetComponent<LayoutGroup>() == null)
            {
                var sourceRect = (RectTransform)source.transform;
                var before = parent.Cast<Transform>().Select(c => c.GetComponent<GenericTab>()).Where(g => g != null && g != source && g != tab)
                    .Select(g => (RectTransform)g.transform).OrderBy(r => (r.anchoredPosition - sourceRect.anchoredPosition).sqrMagnitude).FirstOrDefault();
                var step = before != null ? sourceRect.anchoredPosition - before.anchoredPosition : new Vector2(sourceRect.rect.width, 0f);
                ((RectTransform)copy.transform).anchoredPosition = sourceRect.anchoredPosition + step;
            }

            // The copied click still opens the source tab; replace it.
            UnityAction open = () => unified.ShowTab(index);
            var uiButtons = copy.GetComponentsInChildren<UIButton>(true);
            if (uiButtons.Length > 0)
            {
                foreach (var b in uiButtons)
                {
                    var action = b.OnClick.OnTrigger;
                    action.GameEvents.Clear();
                    if (action.Event == null) action.Event = new UnityEvent();
                    Silence(action.Event);
                    action.Event.AddListener(open);
                }
            }
            else
            {
                foreach (var b in copy.GetComponentsInChildren<Button>(true))
                {
                    Silence(b.onClick);
                    b.onClick.AddListener(open);
                }
            }
            return tab;
        }

        private static void Silence(UnityEventBase e)
        {
            for (var i = 0; i < e.GetPersistentEventCount(); i++) e.SetPersistentListenerState(i, UnityEventCallState.Off);
        }

        // After a tab switch only the active tab's view may stay visible.
        public static void HideInactive(UnifiedOptionsScreen unified)
        {
            if (!Done.Contains(unified)) return;
            var views = Views(unified);
            var active = Active(unified);
            for (var i = 0; i < views.Length; i++)
                if (i != active && views[i] != null && views[i].gameObject.activeInHierarchy && (views[i].IsVisible || views[i].IsShowing)) views[i].Hide();
            if (tabLogs++ < 20)
            {
                var text = new System.Text.StringBuilder($"Options: tab {active} of {views.Length}");
                for (var i = 0; i < views.Length; i++) text.Append("\n  ").Append(Describe(views[i]));
                Plugin.Log.LogInfo(text.ToString());
            }
        }

        private static int tabLogs;

        // State of a view for the log: what decides whether it and its list can be seen.
        private static string Describe(UIView view)
        {
            if (view == null) return "-";
            var rect = (RectTransform)view.transform;
            var canvas = view.GetComponent<Canvas>();
            var alpha = view.GetComponentsInParent<CanvasGroup>(true).Aggregate(1f, (a, g) => a * g.alpha);
            var scroll = view.GetComponentInChildren<ScrollRect>(true);
            var content = scroll != null ? scroll.content : null;
            var rows = content != null ? content.Cast<Transform>().Count(c => c.gameObject.activeSelf) : -1;
            return $"'{view.name}' ({view.ViewCategory}/{view.ViewName}) active {view.gameObject.activeInHierarchy}, {view.Visibility}, canvas {(canvas != null ? canvas.enabled.ToString() : "-")}, alpha {alpha:0.00}, " +
                $"at {rect.anchoredPosition} size {rect.rect.size} scale {rect.localScale}, content {(content != null ? $"{content.anchoredPosition} size {content.rect.size}" : "-")}, {rows} active rows";
        }
    }

    // The VR tab's screen: values apply and save at once, so there is never anything to save or discard. Shows the hint
    // of the selected row (keyboard, gamepad or mouse pointer) in the description text below the list.
    internal class VrOptionsScreen : OptionsScreenController
    {
        public TextMeshProUGUI Hint;
        public UIView View;
        // The video tab's description text, hidden while this tab shows.
        public GameObject Original;
        private string shown;
        private bool originalHidden;
        private bool originalWasActive;

        public override bool OptionsAreDirty => false;

        // Gives the video tab its description back when this view is switched off while showing.
        private new void OnDisable()
        {
            if (Original != null && originalHidden) Original.SetActive(originalWasActive);
            originalHidden = false;
        }

        protected override void Update()
        {
            base.Update();
            if (Hint == null) return;
            var showing = View != null && (View.IsVisible || View.IsShowing);
            if (Original != null && showing != originalHidden)
            {
                if (showing) originalWasActive = Original.activeSelf;
                Original.SetActive(!showing && originalWasActive);
                originalHidden = showing;
            }
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            var hint = VrOptions.HintFor(selected != null ? selected.GetComponentInParent<OptionEntry>() : null)
                ?? VrOptions.L("Select a setting to see what it is for.", "Wähle eine Einstellung, um zu sehen, wofür sie gut ist.");
            if (hint == shown) return;
            shown = hint;
            Hint.enableWordWrapping = true;
            VrOptions.SetText(Hint, hint);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            RefreshAllOptionData();
        }

        public override void ResetOptionsToDefaults() => VrOptions.ResetToDefaults();

        public override void CancelAndReloadOptions() { }

        public override void SaveAndApplyOptions() { }
    }

    [HarmonyPatch(typeof(UnifiedOptionsScreen), "OnEnable")]
    internal static class UnifiedOptionsEnablePatch
    {
        private static void Prefix(UnifiedOptionsScreen __instance) => VrOptionsTab.Create(__instance);
    }

    [HarmonyPatch]
    internal static class UnifiedOptionsTabPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UnifiedOptionsScreen), nameof(UnifiedOptionsScreen.ShowTab));
            yield return AccessTools.Method(typeof(UnifiedOptionsScreen), nameof(UnifiedOptionsScreen.NextOptionTab));
            yield return AccessTools.Method(typeof(UnifiedOptionsScreen), nameof(UnifiedOptionsScreen.PreviousOptionTab));
        }

        private static void Postfix(UnifiedOptionsScreen __instance) => VrOptionsTab.HideInactive(__instance);
    }
}
