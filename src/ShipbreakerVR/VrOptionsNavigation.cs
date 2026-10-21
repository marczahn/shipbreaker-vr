using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // Keyboard/gamepad navigation for the VR option rows. Sits on the list content and keeps the rows linked while the
    // list is visible, because rows become (non-)interactable in between and the game may reset them.
    //
    // How the game navigates (UIInControlBridge): Unity's own navigation events are off; the selected row's bridge reads
    // the arrow keys from one action set (FrontEnd in the main menu, Paused in the pause menu; only the set for the
    // current game state is loaded). With explicit navigation it moves only if the target is active and interactable,
    // and never falls back to automatic search. Title-screen-only rows are non-interactable in-game, so links to them
    // are dead ends. Hence: link only interactable rows, recompute whenever that changes, and give our rows' bridges
    // the same action set as the game's rows in this list.
    internal class VrOptionsNavigation : MonoBehaviour
    {
        private struct Row
        {
            public OptionEntry Entry;
            public Selectable Selectable;
            public bool Ours;
        }

        private readonly List<Row> rows = new List<Row>();

        // Navigation of game rows before we changed them, to restore them when they stop being the boundary.
        private readonly Dictionary<Selectable, Navigation> original = new Dictionary<Selectable, Navigation>();
        private Selectable boundary;
        private float nextLink;
        private string loggedState;
        private GameObject lastSelected;
        private static int selectionLogs;

        private void OnEnable() => nextLink = 0f;

        private void LateUpdate()
        {
            TrackSelection();
            if (Time.unscaledTime < nextLink) return;
            nextLink = Time.unscaledTime + 0.25f;
            Link();
        }

        private void Link()
        {
            rows.Clear();
            foreach (Transform child in transform)
            {
                var entry = child.GetComponent<OptionEntry>();
                if (entry == null || !child.gameObject.activeInHierarchy) continue;
                var button = Traverse.Create(entry).Field("m_BackgroundButton").GetValue<Component>();
                var selectable = button != null ? button.GetComponent<Selectable>() : null;
                if (selectable != null) rows.Add(new Row { Entry = entry, Selectable = selectable, Ours = VrOptions.IsOurs(entry) });
            }
            var firstOurs = rows.FindIndex(r => r.Ours);
            if (firstOurs < 0) return;

            var ours = rows.Skip(firstOurs).Where(r => r.Ours && r.Selectable.IsInteractable()).Select(r => r.Selectable).ToList();
            var gameAbove = rows.Take(firstOurs).Where(r => r.Selectable.IsInteractable()).Select(r => r.Selectable).ToList();
            var last = gameAbove.Count > 0 ? gameAbove[gameAbove.Count - 1] : null;

            if (boundary != null && boundary != last && original.TryGetValue(boundary, out var restore)) boundary.navigation = restore;
            boundary = last;

            if (last != null && ours.Count > 0)
            {
                if (!original.ContainsKey(last)) original[last] = last.navigation;
                var before = Resolve(last, original[last]);
                before.selectOnDown = ours[0];
                last.navigation = before;
            }
            for (var i = 0; i < ours.Count; i++)
            {
                // No left/right: the rows change their value with left/right themselves (OptionEntryList/-Slider).
                ours[i].navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = i > 0 ? ours[i - 1] : last,
                    selectOnDown = i < ours.Count - 1 ? ours[i + 1] : null,
                };
            }

            var actionSet = last != null ? ActionSetOf(last.gameObject) : null;
            if (actionSet != null)
                foreach (var row in rows.Skip(firstOurs).Where(r => r.Ours)) SetActionSet(row.Entry.gameObject, actionSet.Value);

            LogState(firstOurs);
        }

        // The game row's own navigation as explicit targets. For automatic modes Unity resolves them by position
        // (skipping non-interactable rows); do that with the row's original settings before we make it explicit.
        private static Navigation Resolve(Selectable row, Navigation nav)
        {
            if (nav.mode != Navigation.Mode.Explicit)
            {
                row.navigation = nav;
                nav = new Navigation
                {
                    selectOnLeft = row.FindSelectableOnLeft(),
                    selectOnRight = row.FindSelectableOnRight(),
                    selectOnUp = row.FindSelectableOnUp(),
                };
            }
            nav.mode = Navigation.Mode.Explicit;
            return nav;
        }

        // The game's input bridges (UIInControlBridge<T>) of a row with their action set field.
        private static IEnumerable<KeyValuePair<Component, FieldInfo>> Bridges(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                var field = AccessTools.Field(component.GetType(), "m_InControlActionSetType");
                if (field != null && field.FieldType.IsEnum) yield return new KeyValuePair<Component, FieldInfo>(component, field);
            }
        }

        private static int? ActionSetOf(GameObject row)
        {
            foreach (var bridge in Bridges(row)) return Convert.ToInt32(bridge.Value.GetValue(bridge.Key));
            return null;
        }

        // Each bridge type has its own nested enum, so copy the number, not the value.
        private static void SetActionSet(GameObject row, int value)
        {
            foreach (var pair in Bridges(row))
            {
                var bridge = pair.Key;
                var field = pair.Value;
                if (Convert.ToInt32(field.GetValue(bridge)) == value) continue;
                Plugin.Log.LogInfo($"Options navigation: '{row.name}' {bridge.GetType().Name} reads action set {field.GetValue(bridge)}, switching to {Enum.ToObject(field.FieldType, value)} like the game's rows");
                field.SetValue(bridge, Enum.ToObject(field.FieldType, value));
            }
        }

        // Logs the whole list whenever which rows are usable changes, with the game's original links.
        private void LogState(int firstOurs)
        {
            var state = string.Join(",", rows.Select(r => r.Selectable.IsInteractable() ? "1" : "0")) + (boundary != null ? boundary.name : "-");
            if (state == loggedState) return;
            loggedState = state;
            var text = new StringBuilder($"Options navigation in '{transform.parent?.parent?.name}' ({GameSession.CurrentGameState}): {rows.Count} rows, VR rows from {firstOurs}, boundary '{Name(boundary)}'");
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var nav = original.TryGetValue(row.Selectable, out var o) ? o : row.Selectable.navigation;
                var set = ActionSetOf(row.Entry.gameObject);
                text.Append($"\n  [{i}] '{row.Entry.name}'{(row.Ours ? " (VR)" : "")} interactable {row.Selectable.IsInteractable()}, " +
                    $"{(original.ContainsKey(row.Selectable) ? "original " : "")}{nav.mode}{(nav.wrapAround ? " wrap" : "")} " +
                    $"up '{Name(nav.selectOnUp)}' down '{Name(nav.selectOnDown)}', action set {(set.HasValue ? set.Value.ToString() : "-")}");
            }
            Plugin.Log.LogInfo(text.ToString());
        }

        // Logs where the selection goes while this list is visible, to see jumps.
        private void TrackSelection()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == lastSelected) return;
            var previous = lastSelected;
            lastSelected = selected;
            var inside = (selected != null && selected.transform.IsChildOf(transform)) || (previous != null && previous.transform.IsChildOf(transform));
            if (inside && selectionLogs++ < 300) Plugin.Log.LogInfo($"Options selection: '{Name(previous)}' -> '{Name(selected)}'");
        }

        private static string Name(Component c) => c == null ? "-" : Name(c.gameObject);

        private static string Name(GameObject go)
        {
            if (go == null) return "-";
            var entry = go.GetComponentInParent<OptionEntry>();
            return entry != null ? entry.name : go.transform.parent != null ? go.transform.parent.name + "/" + go.name : go.name;
        }
    }

    // When the list scrolls, the game reselects a visible row if the selected one counts as out of view. Its check
    // ignores the list's padding, so a row that was just scrolled to by keyboard counts as out of view and the selection
    // jumps back up. In lists with our rows, keep a selection that sits inside the list.
    [HarmonyPatch(typeof(ScrollController), "ScrollBarUpdated")]
    internal static class ScrollReselectPatch
    {
        private static readonly AccessTools.FieldRef<ScrollController, ScrollRect> ScrollRectRef =
            AccessTools.FieldRefAccess<ScrollController, ScrollRect>("m_ScrollRect");

        private static bool logged;

        private static bool Prefix(ScrollController __instance)
        {
            var scroll = ScrollRectRef(__instance);
            var content = scroll != null ? scroll.content : null;
            if (content == null || content.GetComponent<VrOptionsNavigation>() == null) return true;
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null || !selected.transform.IsChildOf(content)) return true;
            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"Options scroll: keeping selection on '{selected.transform.parent.name}'");
            }
            return false;
        }
    }
}
