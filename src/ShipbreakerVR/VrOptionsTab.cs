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
    // The VR options: a tab for each group of VR settings (Comfort, Gameplay, Graphics, VrOptions.Groups) on the game's
    // options screen, shown instead of the game's tabs when the options are opened from "VR options" in the main or
    // pause menu (VrOptionsMenu.VrMode), as Raik asked: one long VR tab next to the game's had become hard to search.
    // UnifiedOptionsScreen keeps three parallel arrays (screen controllers, their Doozy UIViews, the tab buttons) and
    // switches tabs by index, so the VR tabs are built by copying the video tab's view and the last tab button, and on
    // opening the screen gets either the game's arrays or the VR ones, with the other set's buttons switched off:
    // - A copied view loses the video controller and the video rows; it gets VrOptionsScreen and its group's rows.
    // - A copied button gets its click redirected to ShowTab with its index among the VR tabs.
    // The game hides the previous tab's view by its own means, which do not know the new views, so after every tab switch
    // all other views are hidden here.
    internal static class VrOptionsTab
    {
        static VrOptionsTab() => VrSession.OnUnload("options tab", RemoveAll);

        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, OptionsScreenController[]> Screens =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, OptionsScreenController[]>("m_OptionScreens");
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, UIView[]> Views =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, UIView[]>("m_OptionScreenUIViews");
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, GenericTab[]> Tabs =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, GenericTab[]>("m_OptionTabs");
        private static readonly AccessTools.FieldRef<UnifiedOptionsScreen, int> Active =
            AccessTools.FieldRefAccess<UnifiedOptionsScreen, int>("mActiveOptionScreenIndex");

        private static readonly HashSet<UnifiedOptionsScreen> Done = new HashSet<UnifiedOptionsScreen>();

        // A set of tabs for the screen's arrays: the game's, or the VR ones added here.
        private class TabSet
        {
            public OptionsScreenController[] Screens;
            public UIView[] Views;
            public GenericTab[] Tabs;
        }

        // Both sets of each options screen, to switch between them and to take the VR one out when the plugin unloads.
        private class Sets
        {
            public TabSet Game;
            public TabSet Vr;
            public bool ShowsVr;
        }

        private static readonly Dictionary<UnifiedOptionsScreen, Sets> added = new Dictionary<UnifiedOptionsScreen, Sets>();

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

            var made = new List<GameObject>();
            try
            {
                var groups = VrOptions.Groups();
                var vr = new TabSet { Screens = new OptionsScreenController[groups.Length], Views = new UIView[groups.Length], Tabs = new GenericTab[groups.Length] };
                var after = tabs[tabs.Length - 1];
                for (var g = 0; g < groups.Length; g++)
                {
                    made.Add(CreateView(unified, views[index], g, out vr.Views[g], out vr.Screens[g]));
                    vr.Tabs[g] = CreateTab(unified, after, g, groups[g], tabs[Math.Min(g, tabs.Length - 1)]);
                    made.Add(vr.Tabs[g].gameObject);
                    after = vr.Tabs[g];
                }
                added[unified] = new Sets { Game = new TabSet { Screens = screens, Views = views, Tabs = tabs }, Vr = vr };
                // The game's own lists get the scroll and hover fixes too.
                foreach (var scroll in views.Where(v => v != null).SelectMany(v => v.GetComponentsInChildren<ScrollRect>(true)))
                    if (scroll.content != null && scroll.content.GetComponent<VrOptionsList>() == null) scroll.content.gameObject.AddComponent<VrOptionsList>();
                Plugin.Log.LogInfo($"Options '{unified.name}': VR tabs added: {string.Join(", ", groups)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Options '{unified.name}': VR tabs failed, VR rows go into the video tab instead: {e}");
                foreach (var each in made) if (each != null) UnityEngine.Object.Destroy(each);
                added.Remove(unified);
                VrOptions.AppendTo(screens[index]);
            }
        }

        // The game's tabs or the VR ones on that screen, from the first tab when the set changes.
        public static void Show(UnifiedOptionsScreen unified, bool vr)
        {
            if (!added.TryGetValue(unified, out var sets)) return;
            var set = vr ? sets.Vr : sets.Game;
            var other = vr ? sets.Game : sets.Vr;
            Screens(unified) = set.Screens;
            Views(unified) = set.Views;
            Tabs(unified) = set.Tabs;
            if (sets.ShowsVr != vr || Active(unified) >= set.Screens.Length) Active(unified) = 0;
            sets.ShowsVr = vr;
            foreach (var tab in other.Tabs) if (tab != null) tab.gameObject.SetActive(false);
            foreach (var tab in set.Tabs) if (tab != null) tab.gameObject.SetActive(true);
            HideViews(other);
            Plugin.Log.LogInfo($"Options '{unified.name}': {(vr ? "VR" : "game")} tabs shown");
        }

        private static void HideViews(TabSet set)
        {
            foreach (var view in set.Views)
                if (view != null && view.gameObject.activeInHierarchy && (view.IsVisible || view.IsShowing)) view.Hide();
        }

        // Unloading (ScriptEngine reload): takes the VR tab out of every options screen again, and removes the VR rows,
        // the navigation and the list markers, so the new instance builds them anew without doubling them. Best done with
        // the options closed; an options screen open on the VR tab goes back to the first tab.
        public static void RemoveAll()
        {
            foreach (var pair in added)
            {
                var unified = pair.Key;
                var sets = pair.Value;
                if (unified == null) continue;
                Screens(unified) = sets.Game.Screens;
                Views(unified) = sets.Game.Views;
                Tabs(unified) = sets.Game.Tabs;
                if (sets.ShowsVr || Active(unified) >= Screens(unified).Length) Active(unified) = 0;
                foreach (var tab in sets.Game.Tabs) if (tab != null) tab.gameObject.SetActive(true);
                foreach (var view in sets.Vr.Views) if (view != null) UnityEngine.Object.DestroyImmediate(view.gameObject);
                foreach (var tab in sets.Vr.Tabs) if (tab != null) UnityEngine.Object.DestroyImmediate(tab.gameObject);
            }
            added.Clear();
            Done.Clear();
            var rows = VrOptions.RemoveRows();
            var marks = 0;
            foreach (var component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (!(component is VrOptionsNavigation) && !(component is VrOptionsList)) continue;
                if (!component.gameObject.scene.IsValid()) continue;
                UnityEngine.Object.DestroyImmediate(component);
                marks++;
            }
            Plugin.Log.LogInfo($"Options: VR tab removed, {rows} row(s) and {marks} list helper(s) removed");
        }

        // A copy of the video tab's view with one group's VR rows instead of the video rows.
        private static GameObject CreateView(UnifiedOptionsScreen unified, UIView source, int group, out UIView view, out OptionsScreenController screen)
        {
            // Copy while deactivated, so nothing in the copy wakes up before it is rebuilt.
            var wasActive = source.gameObject.activeSelf;
            source.gameObject.SetActive(false);
            var copy = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
            source.gameObject.SetActive(wasActive);
            copy.name = $"ShipbreakerVR Options {group}";
            copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1 + group);
            view = copy.GetComponent<UIView>();
            // The video view has not been shown yet when it is copied, so its state still reads the default "visible".
            // UIView.Show returns early for a visible view and never switches its canvas on: the tab could be selected,
            // but its page stayed empty. Start the copy hidden, and keep its first Start from hiding it again.
            Plugin.Log.LogInfo($"Options: video view '{VrUtil.Path(source.transform)}' {source.Visibility}, at start {source.BehaviorAtStart}, hide disables object {source.DisableGameObjectWhenHidden}");
            view.Visibility = VisibilityState.NotVisible;
            view.BehaviorAtStart = UIViewStartBehavior.DoNothing;
            // Views are also found by category and name; the copy must not answer to the video tab's name. A view shows
            // and hides its child views by its own category and name, so children with the parent's name are renamed
            // with it (the video view has none today).
            var category = view.ViewCategory;
            var name = view.ViewName;
            foreach (var v in copy.GetComponentsInChildren<UIView>(true))
            {
                var old = v.ViewName;
                v.ViewName = v.ViewCategory == category && v.ViewName == name ? $"ShipbreakerVR {group}" : $"ShipbreakerVR {group} " + v.ViewName;
                if (v != view) Plugin.Log.LogInfo($"Options: child view '{v.name}' ({v.ViewCategory}/{old}) renamed to '{v.ViewName}'");
            }

            var video = copy.GetComponentInChildren<VideoOptionsScreenController>(true);
            if (video == null) throw new InvalidOperationException("the video view holds no VideoOptionsScreenController");
            var t = Traverse.Create(video);
            var container = t.Field("m_OptionsContainer").GetValue<GameObject>();
            if (container == null || !container.transform.IsChildOf(copy.transform)) throw new InvalidOperationException("the video options list is outside its view");
            var host = video.gameObject;
            UnityEngine.Object.DestroyImmediate(video);

            var gameRows = container.GetComponentsInChildren<OptionEntry>(true);
            var list = gameRows.Length > 0 ? gameRows[0].transform.parent : container.transform;
            // Everything in the list goes, including headings between the rows.
            foreach (Transform child in list.Cast<Transform>().ToList()) UnityEngine.Object.DestroyImmediate(child.gameObject);

            var (_, count) = VrOptions.AddRows(list, source.transform.parent != null ? source.transform.parent : unified.transform, true, group);
            if (count == 0) throw new InvalidOperationException("no option row to copy");
            FitContent(list as RectTransform);
            list.gameObject.AddComponent<VrOptionsNavigation>();

            var first = list.GetComponentInChildren<OptionEntry>(true);
            var button = Traverse.Create(first).Field("m_BackgroundButton").GetValue<Component>();
            if (button != null) view.SelectedButton = button.gameObject;

            var vr = host.AddComponent<VrOptionsScreen>();
            vr.Root = copy.transform;
            Traverse.Create(vr).Field("m_OptionsContainer").SetValue(container);
            vr.Hint = CreateHint(copy.transform, Traverse.Create(first).Field("m_OptionNameText").GetValue<TextMeshProUGUI>());
            screen = vr;
            copy.SetActive(wasActive);
            return copy;
        }

        private const float HintHeight = 90f;

        // A text strip at the bottom of the VR view for the selected row's hint; the list's viewport makes room for it.
        // (The video tab's description field is not assigned in the game, so there is nothing to reuse.)
        private static TextMeshProUGUI CreateHint(Transform view, TextMeshProUGUI style)
        {
            var viewport = view.GetComponentInChildren<ScrollRect>(true)?.viewport;
            if (viewport != null) viewport.offsetMin += new Vector2(0f, HintHeight);
            var hint = new GameObject("ShipbreakerVR Hint", typeof(RectTransform));
            hint.layer = view.gameObject.layer;
            var rect = (RectTransform)hint.transform;
            rect.SetParent(view, false);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = new Vector2(0f, HintHeight);
            var text = hint.AddComponent<TextMeshProUGUI>();
            if (style != null)
            {
                text.font = style.font;
                text.fontSharedMaterial = style.fontSharedMaterial;
                text.color = style.color;
                text.fontSize = style.fontSize * 0.75f;
            }
            text.enableWordWrapping = true;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.margin = new Vector4(20f, 10f, 20f, 0f);
            text.raycastTarget = false;
            Plugin.Log.LogInfo($"Options: hint strip below the VR list (viewport {(viewport != null ? viewport.name : "none")}, font size {text.fontSize:0})");
            return text;
        }


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

        // A copy of a tab button, placed after it, that opens the VR tab at index. Without a layout group it takes the
        // place of the game's tab at the same index (place), which is switched off while the VR tabs show.
        private static GenericTab CreateTab(UnifiedOptionsScreen unified, GenericTab source, int index, string label, GenericTab place)
        {
            var copy = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
            copy.name = $"ShipbreakerVR Tab {index}";
            copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            var tab = copy.GetComponent<GenericTab>();
            var t = Traverse.Create(tab);
            t.Field("m_TabIndex").SetValue(index);
            t.Field("m_ShouldSelectAtStart").SetValue(false);
            VrOptions.SetText(t.Field("m_TabText").GetValue<TextMeshProUGUI>(), label);
            tab.DeselectTab();
            var parent = source.transform.parent;
            if (parent != null && parent.GetComponent<LayoutGroup>() == null && place != null)
                ((RectTransform)copy.transform).anchoredPosition = ((RectTransform)place.transform).anchoredPosition;
            copy.SetActive(false);

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

        // After a tab switch only the active tab's view may stay visible, and none of the set not shown.
        public static void HideInactive(UnifiedOptionsScreen unified)
        {
            if (!Done.Contains(unified)) return;
            if (added.TryGetValue(unified, out var sets)) HideViews(sets.ShowsVr ? sets.Game : sets.Vr);
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

        // Opened on the VR tabs, the screen shows the first one only the frame after: in its own OnEnable the game shows
        // the tab it remembers, before the arrays hold the VR tabs, and the VR page stayed empty until a key switched tabs
        // (Raik). From VrFrame each frame.
        private static UnifiedOptionsScreen showNext;
        private static int showAfter;

        public static void ShowLater(UnifiedOptionsScreen unified)
        {
            showNext = unified;
            showAfter = Time.frameCount;
        }

        public static void Update()
        {
            if (showNext == null || Time.frameCount <= showAfter) return;
            var unified = showNext;
            showNext = null;
            if (!unified.isActiveAndEnabled || !added.TryGetValue(unified, out var sets) || !sets.ShowsVr) return;
            var active = Active(unified);
            var views = Views(unified);
            var tabs = Tabs(unified);
            if (active < 0 || active >= views.Length) return;
            var view = views[active];
            if (view != null && !view.IsVisible && !view.IsShowing) view.Show();
            for (var i = 0; i < tabs.Length; i++)
                if (tabs[i] != null)
                {
                    if (i == active) tabs[i].SelectTab();
                    else tabs[i].DeselectTab();
                }
            HideInactive(unified);
            Plugin.Log.LogInfo($"Options '{unified.name}': VR tab {active} shown on opening");
        }

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
        // The tab's view, which holds its rows.
        public Transform Root;
        private string shown;

        public override bool OptionsAreDirty => false;

        protected override void Update()
        {
            base.Update();
            if (Hint == null) return;

            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            var hint = VrOptions.HintFor(selected != null ? selected.GetComponentInParent<OptionEntry>() : null)
                ?? VrOptions.L("Select a setting to see what it is for.", "Wähle eine Einstellung, um zu sehen, wofür sie gut ist.");
            if (hint == shown) return;
            shown = hint;
            Hint.text = hint;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            RefreshAllOptionData();
        }

        // This tab's settings only.
        public override void ResetOptionsToDefaults() => VrOptions.ResetToDefaults(Root != null ? Root : transform);

        public override void CancelAndReloadOptions() { }

        public override void SaveAndApplyOptions() { }
    }

    [HarmonyPatch(typeof(UnifiedOptionsScreen), "OnEnable")]
    internal static class UnifiedOptionsEnablePatch
    {
        private static void Prefix(UnifiedOptionsScreen __instance)
        {
            VrOptionsTab.Create(__instance);
            VrOptionsTab.Show(__instance, VrOptionsMenu.VrMode);
        }

        private static void Postfix(UnifiedOptionsScreen __instance)
        {
            if (VrOptionsMenu.VrMode) VrOptionsTab.ShowLater(__instance);
        }
    }

    // Closed, the next opening shows the game's tabs, unless it comes from "VR options" again.
    [HarmonyPatch(typeof(UnifiedOptionsScreen), "OnDisable")]
    internal static class UnifiedOptionsDisablePatch
    {
        private static void Postfix() => VrOptionsMenu.VrMode = false;
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
