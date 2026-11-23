using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ShipbreakerVR
{
    // The Hab's screens and exit while walking (VrHabWalk). Near one that one looks at, a prompt shows and A opens it with
    // the game's own way to it, from wherever the game is; X opens the exit from anywhere. The rig stays where it stands.
    // When the screen closes the game goes back to the bed, where it waits while one walks.
    // While one walks, a smaller line near the top of the view says so the whole time ('[X] Exit'; Raik, 2026-11-22:
    // nothing told that X does that). For it to be in view wherever one walks and looks, the canvas stays in front of
    // the head while walking (VrUi), as the visor does in a shift.
    internal static class VrHabStations
    {
        private const float Reach = 1.6f;
        private const float ExitReach = 3f;
        private const float Cone = 50f;
        private const float OpenWait = 2f;
        private const float CloseWait = 3f;
        // After a screen closed, A and X wait this long before they open anything: the game is still on its way back to
        // the bed, and the exit opened from there went nowhere, no walking, no screens (Raik).
        private const float InputLock = 1.5f;
        // The collected radio (VrRadioMusic): this near, and looked at more closely than the terminal beside it.
        private const float RadioReach = 1.5f;
        private const string HomeSpot = "BedSitting_Virtual_Camera";
        private const string ExitSpot = "Exit_Virtual_Camera";

        private class Station
        {
            public NavigationOption Way;
            public string Name;
            public float Reach = VrHabStations.Reach;
        }

        private static readonly AccessTools.FieldRef<Hab3DLocationNavigation, List<NavigationOption>> Options =
            AccessTools.FieldRefAccess<Hab3DLocationNavigation, List<NavigationOption>>("m_NavigationOptions");

        private static readonly List<Station> stations = new List<Station>();
        private static Station exit;
        private static NavigationOption home;
        private static Station near;
        private static bool nearRadio;
        private static TextMeshProUGUI prompt;
        // The standing line for X, above the prompt, and its words (built with the stations, Find).
        private static TextMeshProUGUI hint;
        private static string hintText = "";
        private const float HintHeight = 300f;
        private const float HintSize = 0.7f;
        private const float HintAlpha = 0.75f;
        private static string target;
        private static bool opened;
        private static bool closing;
        private static float waitingSince;
        private static float lockedUntil;

        public static bool AtExit => Using && target == ExitSpot;

        // True while the game has a screen open or is on its way there or back.
        public static bool Using { get; private set; }

        public static void Find()
        {
            stations.Clear();
            exit = null;
            home = null;
            TMP_Text sample = null;
            foreach (var location in Resources.FindObjectsOfTypeAll<Hab3DLocationNavigation>())
            {
                if (!location.gameObject.scene.IsValid() || location.LocationVirtualCamera == null || Options(location) == null) continue;
                var from = location.LocationVirtualCamera.Name;
                foreach (var way in Options(location))
                {
                    var to = way.CameraToGoTo != null ? way.CameraToGoTo.Name : null;
                    if (way.ClickableButton != null && sample == null) sample = Label(way.ClickableButton.transform);
                    if (to == HomeSpot && from == "Bed_Virtual_Camera") home = way;
                    if (way.TransformToTrack == null) continue;
                    if (to == "HabScreen_Virtual_Camera") stations.Add(new Station { Way = way, Name = VrOptions.L("Terminal", "Terminal") });
                    else if (to == "Workstation_Screen_Virtual_Camera") stations.Add(new Station { Way = way, Name = VrOptions.L("Workbench", "Werkbank") });
                    else if (to == ExitSpot)
                    {
                        exit = new Station { Way = way, Name = VrOptions.L("Exit", "Ausgang"), Reach = ExitReach };
                        stations.Add(exit);
                    }
                }
            }
            Plugin.Log.LogInfo($"Hab: {stations.Count} station(s), way home {(home != null ? "found" : "missing")}");
            MakePrompt(sample);
            hintText = exit != null ? $"[X] {exit.Name}" : "";
        }

        private static Station promptFor;
        private static bool promptRadio;
        private static bool promptRadioOn;
        private static string promptText;

        public static void Update(Transform head, float scale)
        {
            if (home == null) return;
            var next = Nearest(head, scale, out var nextAngle);
            var radio = VrRadio.InHab;
            var radioNow = RadioInView(head, scale, radio, out var radioAngle) && (next == null || radioAngle < nextAngle);
            if (radioNow) next = null;
            if (next != near || radioNow != nearRadio)
            {
                near = next;
                nearRadio = radioNow;
                // In front of the head, as one may stand turned away from the canvas's place.
                if ((near != null || nearRadio) && VrUi.Instance != null) VrUi.Instance.Recenter();
            }
            // Its text is built when what it says changes, not each frame.
            var radioOn = nearRadio && VrRadioMusic.On;
            if (near != promptFor || nearRadio != promptRadio || radioOn != promptRadioOn || promptText == null)
            {
                promptFor = near;
                promptRadio = nearRadio;
                promptRadioOn = radioOn;
                promptText = nearRadio ? RadioPrompt() : near != null ? $"[A] {near.Name}" : "";
            }
            Show(promptText);
            ShowHint(hintText);
            if (Time.unscaledTime < lockedUntil) return;
            if (nearRadio)
            {
                if (VrInput.Right.Primary.Pressed) VrRadioMusic.Next(radio);
                else if (VrInput.Right.Secondary.Pressed) VrRadioMusic.Off();
                else if (exit != null && VrInput.Left.Primary.Pressed) Open(exit);
                return;
            }
            if (near != null && VrInput.Right.Primary.Pressed) Open(near);
            else if (exit != null && VrInput.Left.Primary.Pressed) Open(exit);
        }

        public static void Track()
        {
            var spot = VrHab.ActiveSpot;
            if (closing)
            {
                var location = Current();
                if (location != null && location.isActiveAndEnabled)
                {
                    location.Navigate(home);
                    Done();
                    lockedUntil = Time.unscaledTime + InputLock;
                }
                else if (Time.unscaledTime - waitingSince > CloseWait)
                {
                    Done();
                    lockedUntil = Time.unscaledTime + InputLock;
                }
                return;
            }
            if (!opened)
            {
                if (spot == target)
                {
                    opened = true;
                    if (VrUi.Instance != null) VrUi.Instance.Recenter();
                }
                else if (Time.unscaledTime - waitingSince > OpenWait) Done();
                return;
            }
            if (spot == target) return;
            closing = true;
            waitingSince = Time.unscaledTime;
        }

        // Sitting on the bed before standing up.
        public static void ShowStandUp()
        {
            HideNavigation(true);
            ShowHint("");
            Show($"[A] {VrOptions.L("Stand up", "Aufstehen")}");
            if (VrUi.Instance != null) VrUi.Instance.Recenter();
        }

        public static void Clear()
        {
            Show("");
            ShowHint("");
        }

        public static void Stop()
        {
            Done();
            near = null;
            nearRadio = false;
            promptText = null;
            Show("");
            ShowHint("");
            HideNavigation(false);
        }

        // Everything else on the game's navigation canvas, such as the prompt to turn around. Not the mod's own two
        // lines: the standing hint for X is a sibling of the prompt and went with the rest, so it never showed while
        // walking (review, 2026-11-23).
        private static void HideNavigation(bool hide)
        {
            if (prompt == null) return;
            foreach (Transform part in prompt.transform.parent)
                if (part != prompt.transform && (hint == null || part != hint.transform)) part.localScale = hide ? Vector3.zero : Vector3.one;
        }

        private static void Done()
        {
            Using = false;
            opened = false;
            closing = false;
        }

        private static string RadioPrompt() => VrRadioMusic.On
            ? $"[A] {VrOptions.L("Next song", "Nächstes Lied")}    [B] {VrOptions.L("Radio off", "Radio aus")}"
            : $"[A] {VrOptions.L("Radio on", "Radio einschalten")}";

        private static bool RadioInView(Transform head, float scale, Transform radio, out float angle)
        {
            angle = float.MaxValue;
            if (radio == null) return false;
            var way = radio.TransformPoint(VrRadioModel.Body.center) - head.position;
            angle = Vector3.Angle(head.forward, way);
            return way.magnitude <= RadioReach * scale && angle <= Cone;
        }

        private static Station Nearest(Transform head, float scale, out float angle)
        {
            Station best = null;
            angle = float.MaxValue;
            var bestDistance = float.MaxValue;
            var controller = VrHab.Controller;
            foreach (var station in stations)
            {
                if (station.Way.TransformToTrack == null || controller.IsInputBlocked(station.Way) || controller.IsCameraBlocked(station.Way)) continue;
                var way = station.Way.TransformToTrack.position - head.position;
                if (way.magnitude > station.Reach * scale || way.magnitude >= bestDistance || Vector3.Angle(head.forward, way) > Cone) continue;
                bestDistance = way.magnitude;
                best = station;
                angle = Vector3.Angle(head.forward, way);
            }
            return best;
        }

        private static void Open(Station station)
        {
            var location = Current();
            var controller = VrHab.Controller;
            if (location == null || controller.IsInputBlocked(station.Way) || controller.IsCameraBlocked(station.Way)) return;
            Plugin.Log.LogInfo($"Hab: opening {station.Name}");
            if (station == exit) VrHabWalk.StepBackFromExit();
            target = station.Way.CameraToGoTo.Name;
            Using = true;
            opened = closing = false;
            waitingSince = Time.unscaledTime;
            near = null;
            Show("");
            ShowHint("");
            location.Navigate(station.Way);
        }

        private static Hab3DLocationNavigation Current()
        {
            var spot = VrHab.ActiveSpot;
            foreach (var location in Resources.FindObjectsOfTypeAll<Hab3DLocationNavigation>())
                if (location.gameObject.scene.IsValid() && location.LocationVirtualCamera != null && location.LocationVirtualCamera.Name == spot) return location;
            return null;
        }

        private static void Show(string text)
        {
            if (prompt != null && prompt.text != text) prompt.text = text;
        }

        private static void ShowHint(string text)
        {
            if (hint != null && hint.text != text) hint.text = text;
        }

        // The prompt's words, not its button symbol, which is text too.
        private static TMP_Text Label(Transform button)
        {
            foreach (var text in button.GetComponentsInChildren<TMP_Text>(true))
                if (text.GetComponent<LocalizedTextMeshProUGUI>() != null) return text;
            return button.GetComponentInChildren<TMP_Text>(true);
        }

        // A line in the style of the game's prompts at the lower middle of its navigation canvas.
        private static void MakePrompt(TMP_Text sample)
        {
            if (prompt != null) return;
            var canvas = sample != null ? sample.transform : null;
            while (canvas != null && canvas.GetComponent<Canvas>() == null) canvas = canvas.parent;
            if (canvas == null) return;
            var line = new GameObject("VR station prompt", typeof(RectTransform));
            line.layer = canvas.gameObject.layer;
            var rect = (RectTransform)line.transform;
            rect.SetParent(canvas.GetComponent<Canvas>().rootCanvas.transform, false);
            rect.sizeDelta = new Vector2(1200f, 120f);
            rect.anchoredPosition = new Vector2(0f, -300f);
            prompt = line.AddComponent<TextMeshProUGUI>();
            prompt.font = sample.font;
            prompt.fontSharedMaterial = sample.fontSharedMaterial;
            prompt.fontSize = (sample.enableAutoSizing ? sample.fontSizeMax : sample.fontSize) * sample.transform.lossyScale.y / rect.lossyScale.y;
            prompt.fontStyle = sample.fontStyle;
            prompt.characterSpacing = sample.characterSpacing;
            prompt.color = new Color(sample.color.r, sample.color.g, sample.color.b, 1f);
            prompt.alignment = TextAlignmentOptions.Center;
            prompt.raycastTarget = false;
            prompt.text = "";
            // The standing line for X: the same writing, smaller and a little see-through, as far above the middle as the
            // prompt is below it.
            var above = new GameObject("VR station hint", typeof(RectTransform));
            above.layer = line.layer;
            var aboveRect = (RectTransform)above.transform;
            aboveRect.SetParent(rect.parent, false);
            aboveRect.sizeDelta = rect.sizeDelta;
            aboveRect.anchoredPosition = new Vector2(0f, HintHeight);
            hint = above.AddComponent<TextMeshProUGUI>();
            hint.font = prompt.font;
            hint.fontSharedMaterial = prompt.fontSharedMaterial;
            hint.fontSize = prompt.fontSize * HintSize;
            hint.fontStyle = prompt.fontStyle;
            hint.characterSpacing = prompt.characterSpacing;
            hint.color = new Color(prompt.color.r, prompt.color.g, prompt.color.b, HintAlpha);
            hint.alignment = TextAlignmentOptions.Center;
            hint.raycastTarget = false;
            hint.text = "";
            Plugin.Log.LogInfo($"Hab: station prompt in the style of '{sample.name}', '{sample.font.name}', size {prompt.fontSize:0}, {sample.fontStyle}");
        }
    }
}
