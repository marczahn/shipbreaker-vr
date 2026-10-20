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
    // Appends the VR settings as extra rows to the video options (that list already scrolls; the Gameplay one does not).
    // The rows are copies of real game option rows so that look, selection and mouse handling are right.
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
                };
            }

            return new Row[]
            {
                new BoolRow { Label = "VR: Body follows head", Entry = Plugin.BodyFollowsHead },
                EnumRow("VR: Anti-aliasing", Plugin.AntiAliasing, new[] { "TAA", "SMAA", "FXAA", "None", "Game default" }),
                new SliderRow { Label = "VR: Render resolution", Entry = Plugin.ResolutionScale, Min = 0.5f, Max = VrCamera.ResolutionSliderMax, Format = "0.00" },
                new SliderRow { Label = "VR: World scale", Entry = Plugin.WorldScale, Min = 0.5f, Max = 3f, Format = "0.00" },
                EnumRow("VR: Helmet", Plugin.Helmet, new[] { "Follows head", "Hidden", "Game default" }),
                new BoolRow { Label = "VR: HUD and menus in world", Entry = Plugin.UiInVr },
                new SliderRow { Label = "VR: Visor size", Entry = Plugin.HudScale, Min = 0.3f, Max = 1f, Format = "0.00" },
                new SliderRow { Label = "VR: Visor distance (m)", Entry = Plugin.HudDistance, Min = 0.5f, Max = 3f, Format = "0.0" },
                new SliderRow { Label = "VR: Menu distance (m)", Entry = Plugin.MenuDistance, Min = 1f, Max = 4f, Format = "0.0" },
                new SliderRow { Label = "VR: Menu width (m)", Entry = Plugin.MenuWidth, Min = 1f, Max = 4f, Format = "0.0" },
            };
        }

        public static void Inject(OptionsScreenController screen)
        {
            var entries = screen.GetComponentsInChildren<OptionEntry>(true);
            if (entries.Length == 0) return;
            // Take templates from all tabs of the options menu, so every row type is available.
            var root = screen.transform.parent != null ? screen.transform.parent : screen.transform;
            var all = root.GetComponentsInChildren<OptionEntry>(true);
            var boolTemplate = PickTemplate<OptionEntryBool>(all);
            var sliderTemplate = PickTemplate<OptionEntrySlider>(all);
            var listTemplate = PickTemplate<OptionEntryList>(all);
            Plugin.Log.LogInfo($"Options '{Path(screen.transform)}': {entries.Length} rows, templates from '{root.name}': toggle {Describe(boolTemplate)}, slider {Describe(sliderTemplate)}, list {Describe(listTemplate)}");

            var parent = entries.Last().transform.parent;
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
                Traverse.Create(entry).Field("m_IsTopItem").SetValue(false);
                Rows[entry] = row;
                SetText(Traverse.Create(entry).Field("m_OptionNameText").GetValue<TextMeshProUGUI>(), row.Label);
                clone.SetActive(true);
                addedHeight += ((RectTransform)template.transform).rect.height;
                added++;
            }
            GrowContent(parent as RectTransform, addedHeight, added);
            if (added > 0) parent.gameObject.AddComponent<VrOptionsNavigation>();
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

        private static void SetText(TextMeshProUGUI text, string value)
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

    [HarmonyPatch(typeof(OptionsScreenController), "Awake")]
    internal static class OptionsScreenAwakePatch
    {
        private static void Postfix(OptionsScreenController __instance)
        {
            if (__instance is VideoOptionsScreenController) VrOptions.Inject(__instance);
        }
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
