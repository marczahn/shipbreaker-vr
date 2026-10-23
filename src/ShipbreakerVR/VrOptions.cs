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
            // What the setting is for and when to change it, shown below the list in the VR tab.
            public string Hint;
            public abstract string ValueText { get; }
        }

        private class BoolRow : Row
        {
            public ConfigEntry<bool> Entry;
            public override string ValueText => Entry.Value ? L("On", "An") : L("Off", "Aus");
        }

        // A row that runs something when activated, shown as a toggle row without the toggle.
        private class ActionRow : Row
        {
            public Action Run;
            public override string ValueText => L("Go", "Los");
        }

        private class SliderRow : Row
        {
            public ConfigEntry<float> Entry;
            public float Min, Max;
            public string Format;
            // Values snap to multiples of this.
            public float Step = 0.01f;
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
            ListRow EnumRow<T>(string label, ConfigEntry<T> entry, string[] names, string hint) where T : Enum
            {
                var values = (T[])Enum.GetValues(typeof(T));
                return new ListRow
                {
                    Label = label,
                    Hint = hint,
                    Names = names,
                    Get = () => Array.IndexOf(values, entry.Value),
                    Set = i => entry.Value = values[(i % values.Length + values.Length) % values.Length],
                    Reset = () => entry.Value = (T)entry.DefaultValue,
                };
            }

            return new Row[]
            {
                new ActionRow
                {
                    Label = L("Center view", "Ansicht zentrieren"), Run = VrCamera.RecenterView,
                    Hint = L("Puts the view and the menus in front of you again, like F4 or B + Y. Use it after you turned around or moved away from your spot.",
                        "Richtet Blick und Menüs wieder vor dir aus, wie F4 oder B + Y. Nutzen, wenn du dich umgedreht oder von deinem Platz entfernt hast."),
                },
                new BoolRow
                {
                    Label = L("Body follows head", "Körper folgt Kopf"), Entry = Plugin.BodyFollowsHead,
                    Hint = L("The body and the tools' aim turn with your head; mouse and stick turn on top. Turn it off to look around without turning the body.",
                        "Körper und Zielrichtung der Werkzeuge drehen sich mit dem Kopf, Maus und Stick drehen zusätzlich. Aus, um dich umzusehen, ohne den Körper zu drehen."),
                },
                EnumRow(L("Controller flying", "Controllersteuerung"), Plugin.ControlScheme, new[] { L("Off", "Aus"), L("VTOL (trigger)", "VTOL (Trigger)") },
                    L("Hold the left trigger: moving the controller thrusts that way, tilting it sideways rolls. Right stick: turn and pitch. Y: brake. Off if you fly with mouse and keyboard or a gamepad.",
                        "Linken Trigger halten: Controller verschieben gibt Schub in diese Richtung, seitlich Kippen rollt. Rechter Stick: drehen und neigen. Y: Bremse. Aus, wenn du mit Maus und Tastatur oder Gamepad fliegst.")),
                EnumRow(L("Turning", "Drehen"), Plugin.TurnMode, new[] { L("Smooth", "Fließend"), "30°", "45°", "90°" },
                    L("How the right stick turns you when flying with the controllers: smoothly, or in steps of 30, 45 or 90 degrees per flick. Steps are easier on the stomach.",
                        "Wie der rechte Stick dich beim Fliegen mit den Controllern dreht: fließend oder in Schritten von 30, 45 oder 90 Grad pro Antippen. Schritte sind magenfreundlicher.")),
                EnumRow(L("Flight globe", "Flugkugel"), Plugin.FlightGlobe, new[] { L("Off", "Aus"), L("Always", "Immer"), L("While flying", "Beim Fliegen") },
                    L("A small globe in the visor shows where your left controller is while you hold the trigger: the center is where you pressed it, the surface full thrust. Helps to find the zero point again.",
                        "Eine kleine Kugel im Visier zeigt, wo dein linker Controller ist, während du den Trigger hältst: die Mitte ist, wo du gedrückt hast, die Oberfläche voller Schub. Hilft, den Nullpunkt wiederzufinden.")),
                EnumRow(L("Anti-aliasing", "Kantenglättung"), Plugin.AntiAliasing, new[] { "TAA", "SMAA", "FXAA", L("None", "Keine"), L("Game default", "Spielstandard") },
                    L("Smooths jagged edges. SMAA stays sharp and is the best choice in VR. TAA looks blurry in VR and makes objects jitter, FXAA is cheap but soft.",
                        "Glättet treppige Kanten. SMAA bleibt scharf und ist in VR die beste Wahl. TAA wirkt in VR unscharf und lässt Objekte zittern, FXAA ist günstig, aber weich.")),
                new SliderRow
                {
                    Label = L("Render resolution", "Renderauflösung"), Entry = Plugin.ResolutionScale, Min = 0.5f, Max = VrCamera.ResolutionSliderMax, Format = "0.00",
                    Hint = L("Resolution per eye relative to the headset's default. Higher is sharper but costs a lot of performance and video memory. Lower it if the game stutters.",
                        "Auflösung pro Auge im Verhältnis zum Standard des Headsets. Höher ist schärfer, kostet aber viel Leistung und Grafikspeicher. Senken, wenn das Spiel ruckelt."),
                },
                new SliderRow
                {
                    Label = L("Station resolution", "Auflösung Station"), Entry = Plugin.StationResolution, Min = 0.5f, Max = 1f, Format = "0.00",
                    Hint = L("Own resolution for the station between shifts, which is harder on the graphics card. If the view wobbles at the monitors when you move your head, lower it step by step until that stops.",
                        "Eigene Auflösung für die Station zwischen den Schichten, die die Grafikkarte stärker fordert. Wackelt das Bild an den Monitoren, wenn du den Kopf bewegst, schrittweise senken, bis es aufhört."),
                },
                new SliderRow
                {
                    Label = L("World scale", "Weltmaßstab"), Entry = Plugin.WorldScale, Min = 0.5f, Max = 3f, Format = "0.00",
                    Hint = L("Your size relative to the world. Above 1, distant objects keep more depth and are easier to judge, but the world looks smaller; below 1 the opposite. 1 is true to scale.",
                        "Deine Größe im Verhältnis zur Welt. Über 1 behalten ferne Objekte mehr Tiefe und lassen sich besser einschätzen, die Welt wirkt aber kleiner; unter 1 umgekehrt. 1 ist maßstabsgetreu."),
                },
                new SliderRow
                {
                    Label = L("Head height (cm)", "Kopfhöhe (cm)"), Entry = Plugin.HeadHeight, Min = -VrCamera.HeadOffsetRange, Max = VrCamera.HeadOffsetRange, Format = "0", Step = 1f,
                    Hint = L("Moves your eyes up or down against the game's head position. Raise it if your view seems to sit too low in the suit.",
                        "Verschiebt die Augen gegenüber der Kopfposition des Spiels nach oben oder unten. Erhöhen, wenn du zu tief im Anzug zu sitzen scheinst."),
                },
                new SliderRow
                {
                    Label = L("Head sideways (cm)", "Kopf seitlich (cm)"), Entry = Plugin.HeadSideways, Min = -VrCamera.HeadOffsetRange, Max = VrCamera.HeadOffsetRange, Format = "0", Step = 1f,
                    Hint = L("Moves your eyes right (positive) or left (negative) against the game's head position, relative to the direction you face.",
                        "Verschiebt die Augen gegenüber der Kopfposition des Spiels nach rechts (positiv) oder links (negativ), bezogen auf die Blickrichtung."),
                },
                EnumRow(L("Ambient occlusion", "Umgebungsverdeckung"), Plugin.AmbientOcclusion, new[] { L("Low", "Niedrig"), L("Off", "Aus"), L("Game default", "Spielstandard") },
                    L("Shading in corners and creases. Low looks nearly the same and saves a lot of performance; the game default can stutter on the station. Off for the most performance.",
                        "Schattierung in Ecken und Fugen. Niedrig sieht fast gleich aus und spart viel Leistung; der Spielstandard kann auf der Station ruckeln. Aus für die meiste Leistung.")),
                EnumRow(L("Helmet", "Helm"), Plugin.Helmet, new[] { L("Follows head", "Folgt dem Kopf"), L("Hidden", "Ausgeblendet"), L("Game default", "Spielstandard") },
                    L("Follows head: the helmet frame moves with the headset. Hidden: a clear view without the frame. Game default: the helmet stays on the body.",
                        "Folgt dem Kopf: der Helmrahmen bewegt sich mit dem Headset. Ausgeblendet: freie Sicht ohne Rahmen. Spielstandard: der Helm bleibt am Körper.")),
                new BoolRow
                {
                    Label = L("HUD and menus in world", "HUD und Menüs im Raum"), Entry = Plugin.UiInVr,
                    Hint = L("Shows the HUD and the menus on a surface in the room instead of flat over the image. Leave it on; off is only for troubleshooting.",
                        "Zeigt HUD und Menüs auf einer Fläche im Raum statt flach über dem Bild. An lassen; aus nur zur Fehlersuche."),
                },
                EnumRow(L("Visor steadying", "Visierberuhigung"), Plugin.HudSteadying, new[] { L("Off", "Aus"), L("Light", "Leicht"), L("Strong", "Stark") },
                    L("Keeps the visor display still instead of copying every small head movement. Light follows your gaze quickly, Strong stays put longer. Off: fixed to the head.",
                        "Hält die Visieranzeige ruhig, statt jede kleine Kopfbewegung mitzumachen. Leicht folgt dem Blick schnell, Stark bleibt länger stehen. Aus: fest am Kopf.")),
                new SliderRow
                {
                    Label = L("Visor size", "Visiergröße"), Entry = Plugin.HudScale, Min = 0.3f, Max = 1f, Format = "0.00",
                    Hint = L("Size of the visor display. At 1 markers sit exactly on their objects, but the corners are outside your view. Smaller: everything in view, markers slightly offset.",
                        "Größe der Visieranzeige. Bei 1 sitzen Markierungen genau auf den Objekten, die Ecken liegen aber außerhalb des Blickfelds. Kleiner: alles im Blick, Markierungen leicht versetzt."),
                },
                new SliderRow
                {
                    Label = L("Visor distance (m)", "Visierabstand (m)"), Entry = Plugin.HudDistance, Min = 0.5f, Max = 3f, Format = "0.0",
                    Hint = L("How far in front of your eyes the visor display floats. Change it if reading the display or switching focus between it and the world is uncomfortable.",
                        "Wie weit die Visieranzeige vor den Augen schwebt. Anpassen, wenn das Lesen der Anzeige oder der Blickwechsel zwischen ihr und der Welt unangenehm ist."),
                },
                new SliderRow
                {
                    Label = L("Menu distance (m)", "Menüabstand (m)"), Entry = Plugin.MenuDistance, Min = 1f, Max = 4f, Format = "0.0",
                    Hint = L("How far away the menus appear. Together with the width it sets how large the text looks; adjust until it reads comfortably.",
                        "Wie weit entfernt die Menüs erscheinen. Bestimmt zusammen mit der Breite, wie groß der Text wirkt; anpassen, bis er sich bequem lesen lässt."),
                },
                new SliderRow
                {
                    Label = L("Menu width (m)", "Menübreite (m)"), Entry = Plugin.MenuWidth, Min = 1f, Max = 4f, Format = "0.0",
                    Hint = L("Width of the menus. Wider makes the text larger but needs more head movement to see everything.",
                        "Breite der Menüs. Breiter macht den Text größer, braucht aber mehr Kopfbewegung, um alles zu sehen."),
                },
            };
        }

        // Texts follow the game's language: German, otherwise English.
        public static string L(string english, string german) =>
            (LynxGameUserOptions.Language ?? "").StartsWith("de", StringComparison.OrdinalIgnoreCase) ? german : english;

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

        // Shows a value that was changed from outside the menu (e.g. clamped) in all rows bound to it.
        public static void Refresh(ConfigEntry<float> config)
        {
            foreach (var pair in Rows)
                if (pair.Key != null && pair.Value is SliderRow s && s.Entry == config) TryRefresh(pair.Key);
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
            TryRefresh(entry);
            return true;
        }

        public static bool TrySlide(OptionEntry entry, float value)
        {
            if (!Rows.TryGetValue(entry, out var row) || !(row is SliderRow s)) return false;
            s.Entry.Value = Mathf.Round(value / s.Step) * s.Step;
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
