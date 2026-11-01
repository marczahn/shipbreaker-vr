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
            // The setting the row shows (none for an action).
            public abstract ConfigEntryBase Setting { get; }
            // What the setting is for and when to change it, shown below the list in the VR tab; it comes with the
            // setting (VrSettings).
            public virtual string Hint => VrSettings.Hint(Setting);
            public abstract string ValueText { get; }
        }

        private class BoolRow : Row
        {
            public ConfigEntry<bool> Entry;
            public override ConfigEntryBase Setting => Entry;
            public override string ValueText => Entry.Value ? L("On", "An") : L("Off", "Aus");
        }

        // A row that runs something when activated, shown as a toggle row without the toggle.
        private class ActionRow : Row
        {
            public Action Run;
            public Func<string> Explained;
            public override ConfigEntryBase Setting => null;
            public override string Hint => Explained();
            public override string ValueText => L("Go", "Los");
        }

        // A number setting; the slider's ends are the setting's range.
        private class SliderRow : Row
        {
            public ConfigEntry<float> Entry;
            public string Format;
            // Values snap to multiples of this.
            public float Step = 0.01f;
            public override ConfigEntryBase Setting => Entry;
            public float Min => VrSettings.Range(Entry).Min;
            public float Max => VrSettings.Range(Entry).Max;
            public override string ValueText => Entry.Value.ToString(Format, System.Globalization.CultureInfo.InvariantCulture);
        }

        private class ListRow : Row
        {
            public ConfigEntryBase Entry;
            public Func<int> Get;
            public Action<int> Set;
            public override ConfigEntryBase Setting => Entry;
            public override string ValueText
            {
                get
                {
                    var names = VrSettings.Names(Entry);
                    return names.Length > 0 ? names[Mathf.Clamp(Get(), 0, names.Length - 1)] : Entry.BoxedValue.ToString();
                }
            }
        }

        private static readonly Dictionary<OptionEntry, Row> Rows = new Dictionary<OptionEntry, Row>();

        // The rows show every change of their setting, from the menu or not (a value clamped or pushed along by another
        // one, as with the thrust range and the render resolution). Before, the code that changed a value had to
        // refresh the options itself. The config file goes with the plugin instance on a reload, and the handler with it.
        static VrOptions() => VrSettings.File.SettingChanged += (_, e) => Refresh(e.ChangedSetting);

        // The slider row being dragged: its value only shows as text, so rounding it does not fight the slider.
        private static OptionEntry sliding;

        private static Row[] CreateRows()
        {
            ListRow EnumRow<T>(string label, ConfigEntry<T> entry) where T : Enum
            {
                var values = (T[])Enum.GetValues(typeof(T));
                return new ListRow
                {
                    Label = label,
                    Entry = entry,
                    Get = () => Array.IndexOf(values, entry.Value),
                    Set = i => entry.Value = values[(i % values.Length + values.Length) % values.Length],
                };
            }

            return new Row[]
            {
                new ActionRow
                {
                    Label = L("Center view", "Ansicht zentrieren"), Run = VrCamera.RecenterView,
                    Explained = () => L($"Puts the view and the menus in front of you again, like {VrSettings.CenterKey.Value} or pressing the left stick. Use it after you turned around or moved away from your spot.",
                        $"Richtet Blick und Menüs wieder vor dir aus, wie {VrSettings.CenterKey.Value} oder ein Druck auf den linken Stick. Nutzen, wenn du dich umgedreht oder von deinem Platz entfernt hast."),
                },
                new BoolRow { Label = L("Aim with the hand", "Zielen mit der Hand"), Entry = VrSettings.AimWithHand },
                new BoolRow { Label = L("Two tools at once", "Zwei Werkzeuge gleichzeitig"), Entry = VrSettings.TwoTools },
                new BoolRow { Label = L("Hold on and climb", "Festhalten und Hangeln"), Entry = VrSettings.Climbing },
                new BoolRow { Label = L("Upright body", "Oberkörper aufrecht"), Entry = VrSettings.UprightBody },
                new BoolRow { Label = L("Walk in the station", "Frei gehen in der Station"), Entry = VrSettings.FreeWalking },
                new BoolRow { Label = L("Fit tools in the hand", "Werkzeuge justieren"), Entry = VrToolFit.Adjusting },
                EnumRow(L("Controller flying", "Controllersteuerung"), VrSettings.ControlScheme),
                EnumRow(L("Turning", "Drehen"), VrSettings.TurnMode),
                EnumRow(L("Thrust mode", "Schubmodus"), VrSettings.ThrustMode),
                new SliderRow { Label = L("Thrust deadzone (cm)", "Schub-Totzone (cm)"), Entry = VrSettings.ThrustDeadzone, Format = "0", Step = 1f },
                new SliderRow { Label = L("Full thrust at (cm)", "Voller Schub ab (cm)"), Entry = VrSettings.ThrustFull, Format = "0", Step = 1f },
                EnumRow(L("Flight globe", "Flugkugel"), VrSettings.FlightGlobe),
                EnumRow(L("Anti-aliasing", "Kantenglättung"), VrSettings.AntiAliasing),
                new SliderRow { Label = L("Render resolution", "Renderauflösung"), Entry = VrSettings.ResolutionScale, Format = "0.00" },
                new SliderRow { Label = L("Station resolution", "Auflösung Station"), Entry = VrSettings.StationResolution, Format = "0.00" },
                new SliderRow { Label = L("World scale", "Weltmaßstab"), Entry = VrSettings.WorldScale, Format = "0.00" },
                new SliderRow { Label = L("Head height (cm)", "Kopfhöhe (cm)"), Entry = VrSettings.HeadHeight, Format = "0", Step = 1f },
                new SliderRow { Label = L("Head sideways (cm)", "Kopf seitlich (cm)"), Entry = VrSettings.HeadSideways, Format = "0", Step = 1f },
                EnumRow(L("Ambient occlusion", "Umgebungsverdeckung"), VrSettings.AmbientOcclusion),
                new BoolRow { Label = L("Share work between eyes", "Augen teilen"), Entry = VrSettings.EyeSharing },
                new BoolRow { Label = L("Alternate eyes", "Augen abwechselnd"), Entry = VrSettings.AlternateEyes },
                EnumRow(L("Helmet", "Helm"), VrSettings.Helmet),
                new BoolRow { Label = L("HUD and menus in world", "HUD und Menüs im Raum"), Entry = VrSettings.UiInVr },
                EnumRow(L("Visor steadying", "Visierberuhigung"), VrSettings.HudSteadying),
                new SliderRow { Label = L("Visor size", "Visiergröße"), Entry = VrSettings.HudScale, Format = "0.00" },
                new SliderRow { Label = L("Visor distance (m)", "Visierabstand (m)"), Entry = VrSettings.HudDistance, Format = "0.0" },
                new SliderRow { Label = L("Visor height (cm)", "Visierhöhe (cm)"), Entry = VrSettings.HudHeight, Format = "0", Step = 1f },
                new SliderRow { Label = L("Menu distance (m)", "Menüabstand (m)"), Entry = VrSettings.MenuDistance, Format = "0.0" },
                new SliderRow { Label = L("Menu width (m)", "Menübreite (m)"), Entry = VrSettings.MenuWidth, Format = "0.0" },
                new BoolRow { Label = L("Performance display", "Leistungsanzeige"), Entry = VrSettings.PerformanceOverlay },
            };
        }

        // Texts follow the game's language: German, otherwise English.
        public static string L(string english, string german) => German ? german : english;

        public static string[] L(string[] english, string[] german) => German ? german : english;

        private static bool German => (LynxGameUserOptions.Language ?? "").StartsWith("de", StringComparison.OrdinalIgnoreCase);

        public static string HintFor(OptionEntry entry) => entry != null && Rows.TryGetValue(entry, out var row) ? row.Hint : null;

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
            Prune();
            var all = templateRoot.GetComponentsInChildren<OptionEntry>(true);
            var boolTemplate = PickTemplate<OptionEntryBool>(all);
            var sliderTemplate = PickTemplate<OptionEntrySlider>(all);
            var listTemplate = PickTemplate<OptionEntryList>(all);
            Plugin.Log.LogInfo($"Options '{VrUtil.Path(parent)}': templates from '{templateRoot.name}': toggle {Describe(boolTemplate)}, slider {Describe(sliderTemplate)}, list {Describe(listTemplate)}");

            var addedHeight = 0f;
            var added = 0;
            foreach (var row in CreateRows())
            {
                OptionEntry template = row switch
                {
                    BoolRow _ => boolTemplate,
                    ActionRow _ => boolTemplate,
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
            foreach (var setting in Rows.Values.Select(row => row.Setting).Where(setting => setting != null).Distinct().ToList())
                setting.BoxedValue = setting.DefaultValue;
        }

        // Rows whose options screen was destroyed (a scene change) leave the table, so Refresh and ResetToDefaults
        // only go through the rows that exist. Before, the table grew with every options screen built in a session.
        private static void Prune()
        {
            var gone = Rows.Keys.Where(entry => entry == null).ToList();
            foreach (var entry in gone) Rows.Remove(entry);
        }

        public static bool IsOurs(OptionEntry entry) => entry != null && Rows.ContainsKey(entry);

        // Unloading: destroys the VR rows still left (those appended to the video options when the tab could not be
        // built; the tab's rows went with its view). Returns how many.
        public static int RemoveRows()
        {
            var count = 0;
            foreach (var entry in Rows.Keys)
            {
                if (entry == null) continue;
                UnityEngine.Object.DestroyImmediate(entry.gameObject);
                count++;
            }
            Rows.Clear();
            return count;
        }

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
                    // Whole steps (centimeters): the stick moves by exactly one step, without the game's acceleration,
                    // and dragging snaps to whole values.
                    if (s.Step >= 1f)
                    {
                        slider.wholeNumbers = true;
                        t.Field("m_ControllerSliderStep").SetValue(s.Step);
                        t.Field("mSpeed").SetValue(s.Step);
                        t.Field("m_AccelerateWhenHeld").SetValue(false);
                    }
                    slider.SetValueWithoutNotify(Mathf.Clamp(s.Entry.Value, s.Min, s.Max));
                    SetText(t.Field("m_ValueText").GetValue<TextMeshProUGUI>(), row.ValueText);
                    break;
                case ActionRow _:
                    SetText(t.Field("m_EnabledDisabledText").GetValue<TextMeshProUGUI>(), row.ValueText);
                    var box = t.Field("m_ToggleFill").GetValue<Image>();
                    if (box != null) box.gameObject.SetActive(false);
                    break;
                case ListRow _:
                    SetText(t.Field("m_ListText").GetValue<TextMeshProUGUI>(), row.ValueText);
                    break;
            }
            return true;
        }

        // Shows a changed setting in all rows bound to it.
        private static void Refresh(ConfigEntryBase setting)
        {
            foreach (var pair in Rows)
            {
                if (pair.Key == null || pair.Value.Setting != setting) continue;
                if (pair.Key == sliding) SetText(Traverse.Create(pair.Key).Field("m_ValueText").GetValue<TextMeshProUGUI>(), pair.Value.ValueText);
                else TryRefresh(pair.Key);
            }
        }

        public static bool TryToggle(OptionEntry entry)
        {
            if (!Rows.TryGetValue(entry, out var row)) return false;
            if (row is ActionRow action)
            {
                action.Run();
                return true;
            }
            if (!(row is BoolRow b)) return false;
            b.Entry.Value = !b.Entry.Value;
            return true;
        }

        public static bool TrySlide(OptionEntry entry, float value)
        {
            if (!Rows.TryGetValue(entry, out var row) || !(row is SliderRow s)) return false;
            sliding = entry;
            try
            {
                s.Entry.Value = Mathf.Round(value / s.Step) * s.Step;
            }
            finally
            {
                sliding = null;
            }
            return true;
        }

        public static bool TryStep(OptionEntry entry, int step)
        {
            if (!Rows.TryGetValue(entry, out var row) || !(row is ListRow l)) return false;
            l.Set(l.Get() + step);
            return true;
        }

        private static string Describe(OptionEntry e) => e == null ? "none" : $"'{e.name}'";

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
