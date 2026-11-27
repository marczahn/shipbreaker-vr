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
    // After a shift with a call waiting (Raik, 2026-11-27, after the tutorial): the game waits for one to go to the comms
    // speaker by the workbench, its light blinking, while it keeps the exit and the terminal shut. Within CallReach of
    // that light A answers the call (Hab3DController.TryStartAfterShiftSequence, as going there in the flat game does),
    // and '[X] Exit' shows only while the game lets the exit open.
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
        // How near the comms speaker's light the head answers a waiting call, in meters at world scale 1, wherever it looks.
        private const float CallReach = 2f;

        private static readonly AccessTools.FieldRef<Hab3DController, NotificationLightController> CommsLight =
            AccessTools.FieldRefAccess<Hab3DController, NotificationLightController>("m_CommsLightController");
        private static readonly AccessTools.FieldRef<Hab3DController, HabAfterShiftAsset> AfterShift =
            AccessTools.FieldRefAccess<Hab3DController, HabAfterShiftAsset>("mCurrentAfterShiftData");
        private static readonly AccessTools.FieldRef<Hab3DController, bool> AfterShiftDone =
            AccessTools.FieldRefAccess<Hab3DController, bool>("mHasFinsishedAfterShiftSequence");
        private static readonly AccessTools.FieldRef<Hab3DController, bool> InAfterShift =
            AccessTools.FieldRefAccess<Hab3DController, bool>("mIsInAfterShiftHab");
        private static bool nearCall;
        private static Hab3DController callLogged;
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
            HideCallMarker();
            if (answering)
            {
                Answering();
                Show("");
                ShowHint("");
                return;
            }
            var call = CallNear(head, scale);
            Station next = null;
            var nextAngle = float.MaxValue;
            if (!call) next = Nearest(head, scale, out nextAngle);
            var radio = VrRadio.InHab;
            var radioNow = !call && RadioInView(head, scale, radio, out var radioAngle) && (next == null || radioAngle < nextAngle);
            if (radioNow) next = null;
            if (next != near || radioNow != nearRadio || call != nearCall)
            {
                near = next;
                nearRadio = radioNow;
                nearCall = call;
                // In front of the head, as one may stand turned away from the canvas's place.
                if ((near != null || nearRadio || nearCall) && VrUi.Instance != null) VrUi.Instance.Recenter();
                promptText = null;
            }
            // Its text is built when what it says changes, not each frame.
            var radioOn = nearRadio && VrRadioMusic.On;
            if (near != promptFor || nearRadio != promptRadio || radioOn != promptRadioOn || promptText == null)
            {
                promptFor = near;
                promptRadio = nearRadio;
                promptRadioOn = radioOn;
                promptText = nearCall ? $"[A] {VrOptions.L("Answer the call", "Anruf annehmen")}"
                    : nearRadio ? RadioPrompt() : near != null ? $"[A] {near.Name}" : "";
            }
            Show(promptText);
            var exitOpen = ExitOpen();
            ShowHint(exitOpen ? hintText : "");
            if (Time.unscaledTime < lockedUntil) return;
            if (nearCall)
            {
                if (VrInput.Right.Primary.Pressed) AnswerCall();
                return;
            }
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

        // The exit as the game lets it open now: shut while a call after the shift waits (IsCameraBlocked).
        private static bool ExitOpen()
        {
            var controller = VrHab.Controller;
            return exit != null && controller != null && !controller.IsInputBlocked(exit.Way) && !controller.IsCameraBlocked(exit.Way);
        }

        // A call after the shift waiting to be answered, and the head within CallReach of the comms speaker's light.
        private static bool CallNear(Transform head, float scale)
        {
            var controller = VrHab.Controller;
            if (controller == null || AfterShift(controller) == null || AfterShiftDone(controller) || InAfterShift(controller)) return false;
            var light = CommsLight(controller);
            if (light == null) return false;
            var way = Vector3.ProjectOnPlane(light.transform.position - head.position, VrHab.RoomUp);
            if (callLogged != controller)
            {
                callLogged = controller;
                Plugin.Log.LogInfo($"Hab: a call waits after the shift, at '{VrUtil.Path(light.transform)}' {way.magnitude / scale:0.0} m from the head");
            }
            // Raik (2026-11-27): only once the station has said that a call waits (that line played or skipped).
            return way.magnitude <= CallReach * scale && VrSkipHabLine.Speaker() == null;
        }

        // The game's own way to the call ("Answer intercom" at the entry): its navigation sends the event the game's flow
        // waits on, and that flow ends the night after the call. Started straight away (TryStartAfterShiftSequence), the
        // call played but the flow stayed behind: after it the screen went black, input stayed blocked, no pause menu,
        // no bed (Raik, 2026-11-27). Only without that way is the call started straight away.
        // That way goes over several places (entry, living area, bed: Raik, 2026-11-27, A had to be pressed three times,
        // each time with the game's sound of moving on); one A now goes all of it, a step each time the game's camera has
        // arrived, and starts the call where no way goes on.
        private const int MostSteps = 5;
        private const float StepPause = 0.3f;
        private static bool answering;
        private static float nextStep;
        private static readonly HashSet<string> Passed = new HashSet<string>();

        private static void AnswerCall()
        {
            var controller = VrHab.Controller;
            Plugin.Log.LogInfo($"Hab: answering the call after the shift ('{AfterShift(controller).name}', {AfterShift(controller).SceneType})");
            answering = true;
            answered = controller;
            nextStep = 0f;
            Passed.Clear();
            nearCall = false;
            promptText = null;
            Show("");
        }

        private static void Answering()
        {
            var controller = VrHab.Controller;
            if (controller == null || AfterShift(controller) == null || AfterShiftDone(controller) || InAfterShift(controller))
            {
                answering = false;
                return;
            }
            if (Time.unscaledTime < nextStep || (controller.CinemachineBrain != null && controller.CinemachineBrain.IsBlending)) return;
            var location = Current();
            if (location != null) Passed.Add(location.LocationVirtualCamera.Name);
            // At the intercom's own place the way ends (it went on to the window, 2026-11-27).
            var there = location != null && location.LocationVirtualCamera.Name.StartsWith("Comms");
            var way = location != null && !there && Passed.Count <= MostSteps ? CallWay(controller, location) : null;
            if (way != null)
            {
                Plugin.Log.LogInfo($"Hab: the game's way to the call, from '{location.LocationVirtualCamera.Name}' to '{way.CameraToGoTo.Name}'");
                location.Navigate(way);
                nextStep = Time.unscaledTime + StepPause;
                return;
            }
            Plugin.Log.LogInfo($"Hab: the call starts at '{(location != null ? location.LocationVirtualCamera.Name : VrHab.ActiveSpot)}' after {Passed.Count - 1} step(s), "
                + $"ways there: {WaysText(location)}");
            controller.TryStartAfterShiftSequence();
            answering = false;
            answered = controller;
        }

        private static string WaysText(Hab3DLocationNavigation location)
        {
            var options = location != null ? Options(location) : null;
            if (options == null) return "none";
            var said = new System.Text.StringBuilder();
            foreach (var way in options)
                if (way != null && way.CameraToGoTo != null)
                    said.Append(said.Length > 0 ? ", " : "").Append(way.CameraToGoTo.Name).Append(" (").Append(way.ButtonOption).Append(')');
            return said.Length > 0 ? said.ToString() : "none";
        }

        // Raik (2026-11-27): after the call, the night and the morning's messages, the game's green waypoint over the
        // intercom (PRF_Waypoint_Intercom) still showed. It goes off as soon as the call is answered here, and is kept
        // off until the call is over.
        private static Hab3DController answered;
        private static GameObject callMarker;

        // Raik (2026-11-27): while anyone speaks in the station, the intercom's light is on as in a call (the game's
        // Conversation state, no waypoint), so one sees the talk comes in over it. Only from the light being off, and back
        // off when the talk ends, unless the game set the light to something else meanwhile (a call waiting, the call).
        private static readonly AccessTools.FieldRef<NotificationLightController, NotificationLightState> LightState =
            AccessTools.FieldRefAccess<NotificationLightController, NotificationLightState>("m_State");
        private const float SpeakerCheck = 0.2f;
        private static float nextSpeakerCheck;
        private static NotificationLightController litLight;

        // Once a frame (VrFrame), wherever one is in the station: in bed, sitting, at a spot or walking (Raik, 2026-11-27:
        // lying in bed the light stayed off, as this ran only while walking).
        public static void SpeakerLight()
        {
            if (!VrXr.IsRunning) return;
            if (Time.unscaledTime < nextSpeakerCheck) return;
            nextSpeakerCheck = Time.unscaledTime + SpeakerCheck;
            var controller = VrHab.Controller;
            var light = controller != null ? CommsLight(controller) : null;
            if (litLight != null && litLight != light) litLight = null;
            if (light == null) return;
            var speaking = VrSkipHabLine.Speaker() != null;
            if (litLight != null)
            {
                if (LightState(light) != NotificationLightState.Conversation) litLight = null;
                else if (!speaking)
                {
                    light.SetState(NotificationLightState.Off);
                    litLight = null;
                }
                return;
            }
            if (!speaking || LightState(light) != NotificationLightState.Off) return;
            light.SetState(NotificationLightState.Conversation);
            litLight = light;
            if (speakerLogs++ < 5) Plugin.Log.LogInfo("Hab: someone speaks, the intercom's light is on");
        }

        private static int speakerLogs;

        private static void HideCallMarker()
        {
            var controller = VrHab.Controller;
            if (answered == null) return;
            if (answered != controller)
            {
                answered = null;
                return;
            }
            if (callMarker == null)
            {
                foreach (var each in Resources.FindObjectsOfTypeAll<Transform>())
                    if (each.name == "PRF_Waypoint_Intercom" && each.gameObject.scene.IsValid()) { callMarker = each.gameObject; break; }
                if (callMarker == null)
                {
                    answered = null;
                    return;
                }
            }
            if (callMarker.activeSelf)
            {
                callMarker.SetActive(false);
                Plugin.Log.LogInfo("Hab: the call is answered, the intercom's waypoint goes off");
            }
            if (AfterShift(controller) == null) answered = null;
        }

        private static readonly AccessTools.FieldRef<Hab3DController, List<LocalizedTextMeshProUGUI>> CallLabels =
            AccessTools.FieldRefAccess<Hab3DController, List<LocalizedTextMeshProUGUI>>("m_PathToCommsTerminalLocs");

        // The entry's way whose button the game labelled for the call (its PathToCommsTerminal labels), else the one up
        // to the living area, as the flat game showed it ("Answer intercom", up).
        private static NavigationOption CallWay(Hab3DController controller, Hab3DLocationNavigation location)
        {
            var options = Options(location);
            if (options == null) return null;
            var labels = CallLabels(controller);
            NavigationOption up = null, comms = null;
            foreach (var way in options)
            {
                if (way == null || way.CameraToGoTo == null || controller.IsInputBlocked(way) || controller.IsCameraBlocked(way)) continue;
                if (Passed.Contains(way.CameraToGoTo.Name)) continue;
                if (labels != null && way.ClickableButton != null)
                    foreach (var label in labels)
                        if (label != null && label.transform.IsChildOf(way.ClickableButton.transform)) return way;
                // The place at the intercom itself (Comms_Virtual_Camera), where the flat game's way ends.
                if (way.CameraToGoTo.Name.StartsWith("Comms")) comms = way;
                if (way.ButtonOption == FrontEndActions.FrontEndActionSet.NavigateUp) up = way;
            }
            return comms ?? up;
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
