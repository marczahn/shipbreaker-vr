using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // Keyboard/gamepad navigation for the VR option rows. The game's last row does not lead down into ours, and our
    // clones still point at rows of the tab they were copied from. Sits on the list content and keeps the rows chained
    // up/down in list order while the list is visible, because rows can appear or vanish and the game may reset them.
    internal class VrOptionsNavigation : MonoBehaviour
    {
        private readonly List<Selectable> rows = new List<Selectable>();
        private readonly Dictionary<Selectable, Navigation> original = new Dictionary<Selectable, Navigation>();
        private float nextLink;
        private bool logged;

        private void OnEnable() => nextLink = 0f;

        private void LateUpdate()
        {
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
                if (selectable != null) rows.Add(selectable);
            }
            var firstOurs = rows.FindIndex(r => VrOptions.IsOurs(r.GetComponentInParent<OptionEntry>()));
            if (firstOurs < 0) return;

            // From the last game row on: explicit up/down to the neighbours in the list, left/right as before.
            for (var i = Mathf.Max(0, firstOurs - 1); i < rows.Count; i++)
            {
                var row = rows[i];
                if (!original.TryGetValue(row, out var before))
                {
                    before = Capture(row);
                    original[row] = before;
                }
                var nav = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnLeft = before.selectOnLeft,
                    selectOnRight = before.selectOnRight,
                    // The game's row keeps its way up; ours chain to the previous row.
                    selectOnUp = i < firstOurs ? before.selectOnUp : rows[i - 1],
                    selectOnDown = i < rows.Count - 1 ? rows[i + 1] : null,
                };
                row.navigation = nav;
            }

            if (logged) return;
            logged = true;
            var last = firstOurs > 0 ? rows[firstOurs - 1] : null;
            Plugin.Log.LogInfo($"Options navigation: {rows.Count} rows, ours from {firstOurs}; last game row '{(last != null ? last.transform.parent.name : "-")}' " +
                $"was {(last != null ? original[last].mode.ToString() : "-")}, down to '{(last != null && original[last].selectOnDown != null ? original[last].selectOnDown.transform.parent.name : "-")}'");
        }

        // The row's navigation as explicit targets. Automatic modes are resolved now, before we switch to explicit;
        // targets outside the row on our clones belong to the template's tab and are dropped.
        private static Navigation Capture(Selectable row)
        {
            var nav = row.navigation;
            var explicitNav = nav.mode == Navigation.Mode.Explicit;
            var result = new Navigation
            {
                mode = nav.mode,
                selectOnLeft = explicitNav ? nav.selectOnLeft : row.FindSelectableOnLeft(),
                selectOnRight = explicitNav ? nav.selectOnRight : row.FindSelectableOnRight(),
                selectOnUp = explicitNav ? nav.selectOnUp : row.FindSelectableOnUp(),
                selectOnDown = explicitNav ? nav.selectOnDown : row.FindSelectableOnDown(),
            };
            var entry = row.GetComponentInParent<OptionEntry>();
            if (VrOptions.IsOurs(entry))
            {
                if (result.selectOnLeft != null && !result.selectOnLeft.transform.IsChildOf(entry.transform)) result.selectOnLeft = null;
                if (result.selectOnRight != null && !result.selectOnRight.transform.IsChildOf(entry.transform)) result.selectOnRight = null;
            }
            return result;
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
