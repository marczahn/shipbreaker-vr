using System;
using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // The VR settings as option rows, shown in their own "VR" tab (VrOptionsTab) or, if that cannot be built, appended to
    // the video options. The rows are copies of real game option rows so that look, selection and mouse handling are right.
    // We intercept the copies' game logic (fixed enums for game options) via Harmony and read/write
    // the BepInEx config instead. Values take effect and are saved immediately.
    internal static class VrOptions
    {
        private abstract class Row
        {
            public string Label;
            public abstract string ValueText { get; }
        }

        private class BoolRow : Row
        {
            public ConfigEntry<bool> Entry;
            public override string ValueText => Entry.Value ? "On" : "Off";
        }

        private class SliderRow : Row
        {
            public ConfigEntry<float> Entry;
            public float Min, Max;
            public string Format;
            public override string ValueText => Entry.Value.ToString(Format, System.Globalization.CultureInfo.InvariantCulture);
        }

        private class ListRow : Row
        {
            public Func<int> Get;
            public Action<int> Set;
            public string[] Names;
            public Action Reset;
            public override string ValueText => Names[Mathf.Clamp(Get(), 0, Names.Length - 1)];
        }

        private static readonly Dictionary<OptionEntry, Row> Rows = new Dictionary<OptionEntry, Row>();

        private static Row[] CreateRows()
        {
            ListRow EnumRow<T>(string label, ConfigEntry<T> entry, string[] names) where T : Enum
            {
                var values = (T[])Enum.GetValues(typeof(T));
                return new ListRow
                {
                    Label = label,
                    Names = names,
                    Get = () => Array.IndexOf(values, entry.Value),
                    Set = i => entry.Value = values[(i % values.Length + values.Length) % values.Length],
                    Reset = () => entry.Value = (T)entry.DefaultValue,
                };
            }

            return new Row[]
            {
                new BoolRow { Label = "Body follows head", Entry = Plugin.BodyFollowsHead },
                EnumRow("Controller flying", Plugin.ControlScheme, new[] { "Off", "VTOL (trigger)" }),
                EnumRow("Anti-aliasing", Plugin.AntiAliasing, new[] { "TAA", "SMAA", "FXAA", "None", "Game default" }),
                new SliderRow { Label = "Render resolution", Entry = Plugin.ResolutionScale, Min = 0.5f, Max = VrCamera.ResolutionSliderMax, Format = "0.00" },
                new SliderRow { Label = "World scale", Entry = Plugin.WorldScale, Min = 0.5f, Max = 3f, Format = "0.00" },
                EnumRow("Helmet", Plugin.Helmet, new[] { "Follows head", "Hidden", "Game default" }),
                new BoolRow { Label = "HUD and menus in world", Entry = Plugin.UiInVr },
                EnumRow("Visor steadying", Plugin.HudSteadying, new[] { "Off", "Light", "Strong" }),
                new SliderRow { Label = "Visor size", Entry = Plugin.HudScale, Min = 0.3f, Max = 1f, Format = "0.00" },
                new SliderRow { Label = "Visor distance (m)", Entry = Plugin.HudDistance, Min = 0.5f, Max = 3f, Format = "0.0" },
                new SliderRow { Label = "Menu distance (m)", Entry = Plugin.MenuDistance, Min = 1f, Max = 4f, Format = "0.0" },
                new SliderRow { Label = "Menu width (m)", Entry = Plugin.MenuWidth, Min = 1f, Max = 4f, Format = "0.0" },
            };
        }

        // Fallback when the VR tab cannot be built: append the rows to the video options like before.
        public static void AppendTo(OptionsScreenController screen)
        {
            var entries = screen.GetComponentsInChildren<OptionEntry>(true);
            if (entries.Length == 0) return;
            var parent = entries.Last().transform.parent;
            var (height, added) = AddRows(parent, screen.transform.parent != null ? screen.transform.parent : screen.transform, false);
            GrowContent(parent as RectTransform, height, added);
            if (added > 0) parent.gameObject.AddComponent<VrOptionsNavigation>();
        }

        // Adds a copy of a game option row for each VR setting to parent. The templates come from all option rows under
        // templateRoot, so every row type is available. Returns the height of the added rows and their number.
        public static (float height, int count) AddRows(Transform parent, Transform templateRoot, bool firstIsTop)
        {
            var all = templateRoot.GetComponentsInChildren<OptionEntry>(true);
            var boolTemplate = PickTemplate<OptionEntryBool>(all);
            var sliderTemplate = PickTemplate<OptionEntrySlider>(all);
            var listTemplate = PickTemplate<OptionEntryList>(all);
            Plugin.Log.LogInfo($"Options '{Path(parent)}': templates from '{templateRoot.name}': toggle {Describe(boolTemplate)}, slider {Describe(sliderTemplate)}, list {Describe(listTemplate)}");

            var addedHeight = 0f;
            var added = 0;
            foreach (var row in CreateRows())
            {
                OptionEntry template = row switch
                {
                    BoolRow _ => boolTemplate,
                    SliderRow _ => sliderTemplate,
                    _ => listTemplate,
                };
                if (template == null) continue;
                // Copy while deactivated so the clone's Awake/OnEnable already find our row.
                var wasActive = template.gameObject.activeSelf;
                template.gameObject.SetActive(false);
                var clone = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
                template.gameObject.SetActive(wasActive);
                clone.name = "ShipbreakerVR " + row.Label;
                var entry = clone.GetComponent<OptionEntry>();
                // Only the list's first row resets the scroll position when selected.
                Traverse.Create(entry).Field("m_IsTopItem").SetValue(firstIsTop && added == 0);
                Rows[entry] = row;
                SetText(Traverse.Create(entry).Field("m_OptionNameText").GetValue<TextMeshProUGUI>(), row.Label);
                clone.SetActive(true);
                addedHeight += ((RectTransform)template.transform).rect.height;
                added++;
            }
            return (addedHeight, added);
        }

        // Puts every VR setting back to its default and shows it.
        public static void ResetToDefaults()
        {
            foreach (var pair in Rows)
            {
                switch (pair.Value)
                {
                    case BoolRow b: b.Entry.Value = (bool)b.Entry.DefaultValue; break;
                    case SliderRow s: s.Entry.Value = (float)s.Entry.DefaultValue; break;
                    case ListRow l: l.Reset(); break;
                }
                if (pair.Key != null) TryRefresh(pair.Key);
            }
        }

        public static bool IsOurs(OptionEntry entry) => entry != null && Rows.ContainsKey(entry);

        // The scroll content has a fixed height in the game's prefab, so the extra rows would hang outside the
        // viewport, invisible and not clickable. Grow it by the height of the added rows.
        private static void GrowContent(RectTransform content, float rowsHeight, int rowCount)
        {
            if (content == null || rowsHeight <= 0f) return;
            if (content.GetComponent<ContentSizeFitter>() != null)
            {
                Plugin.Log.LogInfo($"Options content '{content.name}' sizes itself, height {content.rect.height:0}");
                return;
            }
            var layout = content.GetComponent<VerticalLayoutGroup>();
            var extra = rowsHeight + (layout != null ? layout.spacing * rowCount : 0f);
            var before = content.rect.height;
            // With the usual top pivot the content grows downwards; the position keeps the top edge in place.
            content.sizeDelta += new Vector2(0f, extra);
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            Plugin.Log.LogInfo($"Options content '{content.name}' grown from {before:0} to {content.rect.height:0} (pivot {content.pivot}, layout {(layout != null ? "vertical" : "none")})");
        }

        // A row usable in-game (not title-screen only, no restart required).
        private static T PickTemplate<T>(IEnumerable<OptionEntry> entries) where T : OptionEntry
        {
            return entries.OfType<T>().FirstOrDefault(e =>
                !Traverse.Create(e).Field("m_TitleScreenOnly").GetValue<bool>() &&
                !Traverse.Create(e).Field("m_RequiresRestart").GetValue<bool>() &&
                !e.name.StartsWith("ShipbreakerVR"));
        }

        public static void SetText(TextMeshProUGUI text, string value)
        {
            if (text == null) return;
            // Otherwise localized text would overwrite ours again on activation.
            var localized = text.GetComponent<LocalizedTextMeshProUGUI>();
            if (localized != null) localized.ClearLocalizedID(value);
            text.text = value;
        }

        public static bool TryRefresh(OptionEntry entry)
        {
            if (!Rows.TryGetValue(entry, out var row)) return false;
            var t = Traverse.Create(entry);
            switch (row)
            {
                case BoolRow b:
                    SetText(t.Field("m_EnabledDisabledText").GetValue<TextMeshProUGUI>(), row.ValueText);
                    var fill = t.Field("m_ToggleFill").GetValue<Image>();
                    if (fill != null) fill.gameObject.SetActive(b.Entry.Value);
                    break;
                case SliderRow s:
                    var slider = t.Field("m_Slider").GetValue<Slider>();
                    slider.SetValueWithoutNotify(Mathf.Clamp(s.Entry.Value, s.Min, s.Max));
                    slider.minValue = s.Min;
                    slider.maxValue = s.Max;
                    slider.SetValueWithoutNotify(Mathf.Clamp(s.Entry.Value, s.Min, s.Max));
                    SetText(t.Field("m_ValueText").GetValue<TextMeshProUGUI>(), row.ValueText);
                    break;
                case ListRow _:
                    SetText(t.Field("m_ListText").GetValue<TextMeshProUGUI>(), row.ValueText);
                    break;
            }
            return true;
        }

        // Shows a value that was changed from outside the menu (e.g. clamped) in all rows bound to it.
        public static void Refresh(ConfigEntry<float> config)
        {
            foreach (var pair in Rows)
                if (pair.Key != null && pair.Value is SliderRow s && s.Entry == config) TryRefresh(pair.Key);
        }

        public static bool TryToggle(OptionEntry entry)
        {
            if (!Rows.TryGetValue(entry, out var row) || !(row is BoolRow b)) return false;
            b.Entry.Value = !b.Entry.Value;
            TryRefresh(entry);
            return true;
        }

        public static bool TrySlide(OptionEntry entry, float value)
        {
            if (!Rows.TryGetValue(entry, out var row) || !(row is SliderRow s)) return false;
            s.Entry.Value = Mathf.Round(value * 100f) / 100f;
            SetText(Traverse.Create(entry).Field("m_ValueText").GetValue<TextMeshProUGUI>(), row.ValueText);
            return true;
        }

        public static bool TryStep(OptionEntry entry, int step)
        {
            if (!Rows.TryGetValue(entry, out var row) || !(row is ListRow l)) return false;
            l.Set(l.Get() + step);
            TryRefresh(entry);
            return true;
        }

        private static string Describe(OptionEntry e) => e == null ? "none" : $"'{e.name}'";

        private static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
    }

    [HarmonyPatch]
    internal static class OptionRefreshPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(OptionEntryBool), nameof(OptionEntry.RefreshOptionData));
            yield return AccessTools.Method(typeof(OptionEntrySlider), nameof(OptionEntry.RefreshOptionData));
            yield return AccessTools.Method(typeof(OptionEntryList), nameof(OptionEntry.RefreshOptionData));
        }

        private static bool Prefix(OptionEntry __instance) => !VrOptions.TryRefresh(__instance);
    }

    [HarmonyPatch(typeof(OptionEntryBool), "ToggleOption")]
    internal static class OptionToggledPatch
    {
        private static bool Prefix(OptionEntry __instance) => !VrOptions.TryToggle(__instance);
    }

    [HarmonyPatch(typeof(OptionEntrySlider), "SliderValueChanged")]
    internal static class OptionSlidPatch
    {
        private static bool Prefix(OptionEntry __instance, float newValue) => !VrOptions.TrySlide(__instance, newValue);
    }

    [HarmonyPatch(typeof(OptionEntryList), "LeftButtonClicked")]
    internal static class OptionLeftPatch
    {
        private static bool Prefix(OptionEntry __instance) => !VrOptions.TryStep(__instance, -1);
    }

    [HarmonyPatch(typeof(OptionEntryList), "RightButtonClicked")]
    internal static class OptionRightPatch
    {
        private static bool Prefix(OptionEntry __instance) => !VrOptions.TryStep(__instance, 1);
    }
}
