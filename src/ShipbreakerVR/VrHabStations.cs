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
    internal static class VrHabStations
    {
        private const float Reach = 1.6f;
        private const float ExitReach = 3f;
        private const float Cone = 50f;
        private const float OpenWait = 2f;
        private const float CloseWait = 3f;
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
        private static TextMeshProUGUI prompt;
        private static string target;
        private static bool opened;
        private static bool closing;
        private static float waitingSince;

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
        }

        public static void Update(Transform head, float scale)
        {
            if (home == null) return;
            var next = Nearest(head, scale);
            if (next != near)
            {
                near = next;
                // In front of the head, as one may stand turned away from the canvas's place.
                if (near != null && VrUi.Instance != null) VrUi.Instance.Recenter();
                Show(near != null ? $"[A] {near.Name}" : "");
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
                }
                else if (Time.unscaledTime - waitingSince > CloseWait) Done();
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
            Show($"[A] {VrOptions.L("Stand up", "Aufstehen")}");
            if (VrUi.Instance != null) VrUi.Instance.Recenter();
        }

        public static void Clear() => Show("");

        public static void Stop()
        {
            Done();
            near = null;
            Show("");
            HideNavigation(false);
        }

        // Everything else on the game's navigation canvas, such as the prompt to turn around.
        private static void HideNavigation(bool hide)
        {
            if (prompt == null) return;
            foreach (Transform part in prompt.transform.parent)
                if (part != prompt.transform) part.localScale = hide ? Vector3.zero : Vector3.one;
        }

        private static void Done()
        {
            Using = false;
            opened = false;
            closing = false;
        }

        private static Station Nearest(Transform head, float scale)
        {
            Station best = null;
            var bestDistance = float.MaxValue;
            var controller = VrHab.Controller;
            foreach (var station in stations)
            {
                if (station.Way.TransformToTrack == null || controller.IsInputBlocked(station.Way) || controller.IsCameraBlocked(station.Way)) continue;
                var way = station.Way.TransformToTrack.position - head.position;
                if (way.magnitude > station.Reach * scale || way.magnitude >= bestDistance || Vector3.Angle(head.forward, way) > Cone) continue;
                bestDistance = way.magnitude;
                best = station;
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
            Plugin.Log.LogInfo($"Hab: station prompt in the style of '{sample.name}', '{sample.font.name}', size {prompt.fontSize:0}, {sample.fontStyle}");
        }
    }
}
