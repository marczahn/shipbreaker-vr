using System.Collections.Generic;
using BBI.Unity.Game;
using Cinemachine;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The Hab (the station between shifts) is an extra scene loaded next to the bay. It has its own camera
    // (Hab3DController's CinemachineBrain on "Unity_Camera", depth 2) that Cinemachine moves between bed, kitchen,
    // terminal and so on, while LynxCameraController switches the bay's main camera off. While the VR rig stayed on
    // the main camera, the headset got the whole bay rendered by our camera and then the Hab on top by the Hab camera:
    // twice the work and video memory, no head tracking, and our UI canvas painted over.
    // So in the Hab the rig follows the Hab camera, and the camera sway of its virtual cameras is turned off in VR.
    internal static class VrHab
    {
        private static Hab3DController controller;
        private static Transform interior;
        private static Transform floorOf;
        private static bool floorFound;
        private static Vector3 floorLocal;
        private static float loggedSpot = float.MinValue;
        private static float nextControllerSearch;

        // Moving between bed, kitchen, terminal and so on, the Hab camera swings through the room for a second. In VR
        // the room then rushes past without the player moving, and at the Hab's 45 fps the headset's frame synthesis
        // smeared it sideways. While VR runs the brain cuts instead (original blends restored afterwards), and the VR
        // view fades to black on the old spot, jumps while black and fades in on the new one.
        private const float FadeOut = 0.15f;
        private const float Black = 0.05f;
        private const float FadeIn = 0.25f;
        private static CinemachineBrain cutBrain;
        private static CinemachineBlendDefinition originalBlend;
        private static CinemachineBlenderSettings originalCustomBlends;
        private static ICinemachineCamera lastVirtualCamera;
        private static float transitionStart = -1f;

        // The navigable screens with a monitor (above the bed, terminal, workbench): their virtual cameras sit 35 to 45 cm
        // in front of it, and what the screen shows comes as an overlay over the whole picture. In VR that overlay is on
        // the menu canvas 2.4 m away, which was drawn over the monitor 35 cm away: two distances at once for the eyes.
        // So in VR the canvas lies on the monitor itself (VrUi), and the head is ViewDistance in front of it. The monitor
        // above the bed is looked at from where one lies after waking up; there the rig stays where it was (Staying).
        // The screensaver's canvas floats about a centimetre in front of the glass; the canvas goes onto the glass.
        private const float ViewDistance = 0.7f;
        private const float ScreenInset = 0.01f;
        private static readonly Dictionary<ICinemachineCamera, RectTransform> monitors = new Dictionary<ICinemachineCamera, RectTransform>();
        private static RectTransform placedMonitor;
        private static ICinemachineCamera placeVirtualCamera;
        private static readonly Vector3[] corners = new Vector3[4];

        // The Hab's 3D waypoint markers (the green arrows pointing at the terminal and so on). In the flat game the
        // screen's overlay covers them; in VR, 0.7 m in front of the monitor, they stuck into it. Hidden at a monitor.
        private static readonly List<Renderer> markers = new List<Renderer>();
        private static bool markersHidden;

        // The farthest any of the Hab's virtual cameras sees (the window view, 5000 m). Each view's far clip plane only
        // fits what the game camera looks at there; in VR one can look out of the window from anywhere.
        public static float FarClip { get; private set; }

        // How far the VR view is faded to black, 0 to 1.
        public static float Fade { get; private set; }

        // True while the view fades out: the rig stays where it was until it is black.
        public static bool Holding => transitionStart >= 0f && Time.unscaledTime - transitionStart < FadeOut;

        // The monitor the rig stands at, or null. Changes only once the view is black, like the rig's spot.
        public static RectTransform Monitor => cutBrain != null ? placedMonitor : null;

        // True while the rig stays on the spot it had before the current camera (the monitor above the bed).
        public static bool Staying { get; private set; }

        // Remembers the virtual cameras of a navigation option that opens a screen on a monitor; true if the monitor is known.
        public static bool AddScreen(NavigationOption navOpt)
        {
            if (navOpt == null || !navOpt.GoesToNavigableScreen || navOpt.ScreenAnimator == null) return false;
            var monitor = ScreenCanvas(navOpt.ScreenAnimator.transform.parent);
            if (monitor == null) return false;
            if (navOpt.CameraToGoTo != null) monitors[navOpt.CameraToGoTo] = monitor;
            if (navOpt.CameraToSwapToAfterBlend != null) monitors[navOpt.CameraToSwapToAfterBlend] = monitor;
            return true;
        }

        // The monitor's own world space canvas (for its "locked" screensaver), which covers exactly its screen.
        private static RectTransform ScreenCanvas(Transform terminal)
        {
            if (terminal == null) return null;
            foreach (var canvas in terminal.GetComponentsInChildren<Canvas>(true))
                if (canvas.renderMode == RenderMode.WorldSpace) return (RectTransform)canvas.transform;
            return null;
        }

        private static void FindMonitors()
        {
            monitors.Clear();
            markers.Clear();
            markersHidden = false;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t.name == "PRF_3DHAB_WaypointMarkers" && t.gameObject.scene.IsValid()) t.GetComponentsInChildren(true, markers);
            FarClip = 0f;
            foreach (var camera in controller.GetComponentsInChildren<CinemachineVirtualCamera>(true))
                FarClip = Mathf.Max(FarClip, camera.m_Lens.FarClipPlane);
            var options = AccessTools.FieldRefAccess<Hab3DLocationNavigation, List<NavigationOption>>("m_NavigationOptions");
            foreach (var location in Resources.FindObjectsOfTypeAll<Hab3DLocationNavigation>())
            {
                if (!location.gameObject.scene.IsValid() || options(location) == null) continue;
                foreach (var navOpt in options(location))
                {
                    AddScreen(navOpt);
                    var goesTo = navOpt.CameraToGoTo != null ? navOpt.CameraToGoTo.Name : "-";
                    Plugin.Log.LogInfo($"Hab spot '{(location.LocationVirtualCamera != null ? location.LocationVirtualCamera.Name : "-")}': {navOpt.ButtonOption} to '{goesTo}'{(navOpt.GoesToNavigableScreen ? " (screen)" : "")}, frame on '{(navOpt.TransformToTrack != null ? navOpt.TransformToTrack.name : "-")}'");
                }
            }
            AddTerminalTurn();
            Plugin.Log.LogInfo($"Hab monitors: {monitors.Count} virtual camera(s), {markers.Count} waypoint marker renderer(s), farthest view {FarClip:0} m");
        }

        // Looking from the living area at the work area, the terminal is a turn to the left away, but the game goes there
        // only by way of the turned view. So that spot gets a copy of the bed's way to the terminal: the same left, with
        // a prompt of its own, among the spot's other options.
        private static void AddTerminalTurn()
        {
            if (!VrXr.IsRunning) return;
            var options = AccessTools.FieldRefAccess<Hab3DLocationNavigation, List<NavigationOption>>("m_NavigationOptions");
            List<NavigationOption> living = null, bed = null;
            foreach (var location in Resources.FindObjectsOfTypeAll<Hab3DLocationNavigation>())
            {
                if (!location.gameObject.scene.IsValid() || location.LocationVirtualCamera == null) continue;
                if (location.LocationVirtualCamera.Name == "Habitation_Virtual_Camera") living = options(location);
                else if (location.LocationVirtualCamera.Name == "BedSitting_Virtual_Camera") bed = options(location);
            }
            if (living == null || bed == null || living.Exists(o => o.ButtonOption == FrontEndActions.FrontEndActionSet.NavigateLeft)) return;
            var source = bed.Find(o => o.ButtonOption == FrontEndActions.FrontEndActionSet.NavigateLeft && o.ClickableButton != null && o.CameraToGoTo != null && o.CameraToGoTo.Name == "Comms_Virtual_Camera");
            var sibling = living.Find(o => o.ClickableButton != null);
            if (source == null || sibling == null)
            {
                Plugin.Log.LogWarning("Hab: no way to the terminal to copy");
                return;
            }
            var prompt = Object.Instantiate(source.ClickableButton.gameObject, sibling.ClickableButton.RectTransform.parent);
            prompt.SetActive(sibling.ClickableButton.gameObject.activeSelf);
            var turn = (NavigationOption)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(source, null);
            var fields = Traverse.Create(turn);
            fields.Field("m_ClickableButton").SetValue(prompt.GetComponent<Doozy.Engine.UI.UIButton>());
            fields.Field("m_GameEventListener").SetValue(null);
            fields.Field("m_ButtonStateChanger").SetValue(source.ButtonStateChanger != null ? prompt.GetComponentInChildren(source.ButtonStateChanger.GetType(), true) : null);
            living.Add(turn);
            Plugin.Log.LogInfo("Hab: left from the living area goes to the terminal");
        }

        // Center, orientation (forward into the screen) and size of a monitor's screen.
        public static void MonitorScreen(RectTransform monitor, out Vector3 center, out Quaternion rotation, out Vector2 size)
        {
            monitor.GetWorldCorners(corners);
            rotation = monitor.rotation;
            center = (corners[0] + corners[2]) * 0.5f + rotation * Vector3.forward * ScreenInset;
            size = new Vector2(Vector3.Distance(corners[0], corners[3]), Vector3.Distance(corners[0], corners[1]));
        }

        // Where the head goes at a monitor. One overhead (above the bed) is looked at from the spot before (Staying), or
        // where the game's camera is if the Hab opened right at it; further back the head would be in the mattress.
        public static Vector3 MonitorViewpoint(RectTransform monitor, Vector3 cameraPosition)
        {
            MonitorScreen(monitor, out var center, out var rotation, out _);
            var forward = rotation * Vector3.forward;
            return Overhead(monitor) ? cameraPosition : center - forward * ViewDistance;
        }

        private static bool Overhead(RectTransform monitor) => Mathf.Abs((monitor.rotation * Vector3.forward).y) > 0.7f;

        private static void HideMarkers(bool hide)
        {
            if (hide == markersHidden) return;
            markersHidden = hide;
            foreach (var marker in markers)
                if (marker != null) marker.forceRenderingOff = hide;
        }

        // Original amplitude of each sway (Perlin noise) we silenced; the game itself toggles their enabled flag.
        private static readonly Dictionary<CinemachineBasicMultiChannelPerlin, float> calmed = new Dictionary<CinemachineBasicMultiChannelPerlin, float>();
        private static float nextNoiseScan;

        // Mirrors what LynxCameraController did with the main camera: off in the Hab, on in gameplay. The camera's own
        // enabled flag doesn't tell, because the VR camera keeps the camera it follows switched off.
        public static bool MainCameraOff { get; set; }

        // After a reload the game state events that set MainCameraOff are long past. The previous instance gave the main
        // camera back to the game when it unloaded, so its enabled flag tells again.
        public static void Adopt()
        {
            var main = LynxCameraController.MainCamera;
            MainCameraOff = main != null && !main.enabled;
            if (MainCameraOff) Plugin.Log.LogInfo("Main camera is off: in the Hab");
        }

        // The camera the game shows the Hab with, or null outside the Hab.
        public static Camera Camera
        {
            get
            {
                // The Hab is only shown next to a loaded bay; without its main camera there is nothing to look for.
                if (!MainCameraOff || LynxCameraController.MainCamera == null) return null;
                // Asked every frame; searching all objects is only worth it now and then (e.g. while the Hab unloads).
                if (controller == null && Time.unscaledTime >= nextControllerSearch)
                {
                    nextControllerSearch = Time.unscaledTime + 0.25f;
                    controller = Object.FindObjectOfType<Hab3DController>();
                    if (controller != null) FindMonitors();
                }
                if (controller == null || !controller.isActiveAndEnabled || controller.CinemachineBrain == null) return null;
                var camera = controller.CinemachineBrain.OutputCamera;
                return camera != null && camera.gameObject.activeInHierarchy ? camera : null;
            }
        }

        public static bool IsHabCamera(Camera camera) => camera != null && camera.GetComponentInParent<Hab3DController>() != null;

        // The Hab stands tilted in the world: floor and walls (PRF_Hab_Interior) lie 4 degrees off, the container around
        // them 19. Their up is the room's, without the Hab the world's.
        public static Vector3 RoomUp
        {
            get
            {
                if (controller == null) return Vector3.up;
                if (interior == null || interior.root != controller.transform.root) interior = controller.transform.root.Find("PRF_Hab_Interior");
                return interior != null ? interior.up : Vector3.up;
            }
        }

        public static Hab3DController Controller => controller;

        // The room the Hab's floor plan is measured in.
        public static Transform Room
        {
            get
            {
                _ = RoomUp;
                return interior;
            }
        }

        // The floor's height in the room's axes.
        public static bool TryRoomFloor(out float height)
        {
            var found = Room != null && FindFloor();
            height = found ? floorLocal.y : 0f;
            return found;
        }

        // Set by a move between spots: the stick has to come back before the next one, or holding it spins through the room.
        public static bool WaitForStick { get; set; }

        // How far the head stands from the game's camera. The game places its prompts as seen from that camera.
        public static Vector3 StandShift { get; set; }

        // Where a spot shows on the game's screen once the camera stands at the head instead.
        public static Vector3 SeenFromHead(Camera camera, Vector3 spot) => camera.WorldToScreenPoint(spot - StandShift);

        // How far a prompt on an area of the given size moves for that.
        public static Vector3 PromptShift(Camera camera, Vector3 spot, Rect area, float screenWidth, float screenHeight)
        {
            var moved = SeenFromHead(camera, spot) - camera.WorldToScreenPoint(spot);
            return new Vector3(moved.x / screenWidth * area.width, moved.y / screenHeight * area.height, 0f);
        }

        // The camera moved to standing height over the room's floor. The floor has no collider, and the floor mesh is
        // static batched, so its bounds say nothing: the chair's foot stands for it.
        public static Vector3 Standing(Vector3 camera, float scale)
        {
            var up = RoomUp;
            if (!FindFloor()) return camera;
            var floor = Vector3.Dot(interior.TransformPoint(floorLocal), up);
            var spot = (Vector3.Dot(camera, up) - floor) / scale;
            if (Mathf.Abs(spot - loggedSpot) > 0.01f)
            {
                loggedSpot = spot;
                NavPromptPatch.LogAgain();
                var name = cutBrain != null && cutBrain.ActiveVirtualCamera != null ? cutBrain.ActiveVirtualCamera.Name : "?";
                Plugin.Log.LogInfo($"Hab: camera spot '{name}' {spot:0.00} m above the floor, {(HabStanding.Sits(spot) ? "sitting, stays" : $"standing at {HabStanding.Height:0.00} m")}");
            }
            return HabStanding.Place(camera, up, floor, scale);
        }

        private static bool FindFloor()
        {
            if (interior == null) return false;
            if (floorOf == interior) return floorFound;
            floorOf = interior;
            var chair = interior.Find("PRF_Props_Hab_Chair/SM_Props_Hab_Chair");
            var filter = chair != null ? chair.GetComponent<MeshFilter>() : null;
            floorFound = filter != null && filter.sharedMesh != null;
            if (!floorFound)
            {
                Plugin.Log.LogWarning("Hab: no chair to find the floor by, the camera spots keep their height");
                return false;
            }
            var bounds = filter.sharedMesh.bounds;
            var lowest = float.MaxValue;
            for (var x = -1; x <= 1; x += 2)
                for (var y = -1; y <= 1; y += 2)
                    for (var z = -1; z <= 1; z += 2)
                    {
                        var corner = chair.TransformPoint(bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z)));
                        var height = Vector3.Dot(corner, interior.up);
                        if (height >= lowest) continue;
                        lowest = height;
                        floorLocal = interior.InverseTransformPoint(corner);
                    }
            return true;
        }

        // In VR the sway moves the whole room around the player. Silenced while the rig follows the Hab camera,
        // restored afterwards. Rescanned now and then, since locations may be activated later.
        public static void Calm(bool calm)
        {
            if (!calm)
            {
                if (calmed.Count == 0) return;
                foreach (var entry in calmed)
                    if (entry.Key != null) entry.Key.m_AmplitudeGain = entry.Value;
                calmed.Clear();
                return;
            }
            if (controller == null || Time.unscaledTime < nextNoiseScan) return;
            nextNoiseScan = Time.unscaledTime + 1f;
            var added = 0;
            foreach (var noise in controller.GetComponentsInChildren<CinemachineBasicMultiChannelPerlin>(true))
            {
                if (calmed.ContainsKey(noise)) continue;
                calmed[noise] = noise.m_AmplitudeGain;
                noise.m_AmplitudeGain = 0f;
                added++;
            }
            if (added > 0) Plugin.Log.LogInfo($"Hab camera sway off in VR ({added} virtual camera(s))");
        }

        // Cuts instead of blends while the rig follows the Hab camera in VR; the game's blends again otherwise.
        public static void CutBlends(bool cut)
        {
            var brain = cut && controller != null ? controller.CinemachineBrain : null;
            if (brain == cutBrain) return;
            if (cutBrain != null)
            {
                cutBrain.m_DefaultBlend = originalBlend;
                cutBrain.m_CustomBlends = originalCustomBlends;
            }
            cutBrain = brain;
            lastVirtualCamera = null;
            placeVirtualCamera = null;
            placedMonitor = null;
            Staying = false;
            HideMarkers(false);
            transitionStart = -1f;
            Fade = 0f;
            if (brain == null) return;
            originalBlend = brain.m_DefaultBlend;
            originalCustomBlends = brain.m_CustomBlends;
            brain.m_DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.Cut, 0f);
            brain.m_CustomBlends = null;
            Plugin.Log.LogInfo("Hab camera moves replaced by fades in VR");
        }

        // Called before the rig follows the camera, also right before rendering, once the brain has picked its camera.
        public static void UpdateTransition()
        {
            if (cutBrain == null) return;
            var current = cutBrain.ActiveVirtualCamera;
            var now = Time.unscaledTime;
            var monitor = current != null && monitors.TryGetValue(current, out var found) && found != null ? found : null;
            // Into the monitor above the bed the rig stays, and back out to where it stayed it doesn't move either.
            var stay = monitor != null && placeVirtualCamera != null && Overhead(monitor);
            if (lastVirtualCamera != null && current != lastVirtualCamera && !stay && current != placeVirtualCamera)
            {
                var elapsed = transitionStart >= 0f ? now - transitionStart : float.MaxValue;
                // Fading out or black already: the rig jumps to wherever the camera is once it is black.
                // Fading in: fade out again from where the fade is now.
                if (elapsed >= FadeOut + Black) transitionStart = now - Fade * FadeOut;
            }
            lastVirtualCamera = current;
            Staying = stay;
            if (!stay) placeVirtualCamera = current;
            if (!Holding) placedMonitor = monitor;
            HideMarkers(placedMonitor != null);

            if (transitionStart < 0f)
            {
                Fade = 0f;
                return;
            }
            var t = now - transitionStart;
            if (t < FadeOut) Fade = t / FadeOut;
            else if (t < FadeOut + Black) Fade = 1f;
            else if (t < FadeOut + Black + FadeIn) Fade = 1f - (t - FadeOut - Black) / FadeIn;
            else
            {
                Fade = 0f;
                transitionStart = -1f;
            }
        }
    }

    // Going to one of the Hab's screens (the monitor above the bed after waking up, the terminal, the workbench), the
    // game fades out the monitor in the room: what it shows comes as an overlay over the whole picture instead. In VR the
    // overlay lies on the monitor (VrHab.Monitor), as in the flat game over the dark monitor. Should a monitor's screen
    // not be found, the overlay is on our canvas in front of the player and the switched-off monitor would stay in the
    // room as a black rectangle; then it keeps its screensaver while the UI is in VR.
    [HarmonyPatch(typeof(Hab3DLocationNavigation), nameof(Hab3DLocationNavigation.Navigate))]
    internal static class HabScreenNavigatePatch
    {
        private static void Postfix(NavigationOption navOpt)
        {
            VrHab.WaitForStick = true;
            if (VrHab.AddScreen(navOpt)) return;
            if (navOpt == null || !navOpt.GoesToNavigableScreen || navOpt.ScreenAnimator == null) return;
            if (VrUi.Instance == null || !VrUi.Instance.Active) return;
            navOpt.ScreenAnimator.ResetTrigger("FadeOut");
        }
    }

    // The frames of the Hab's spots are placed through the game's camera, with its field of view and height, so on the
    // menu canvas they sat beside what they frame, differently from each side. They go where the line from the resting
    // head to their spot crosses the canvas.
    [HarmonyPatch(typeof(Hab3DLocationNavigation), "UpdateNavLocation")]
    internal static class NavPromptPatch
    {
        private static readonly HashSet<NavigationOption> logged = new HashSet<NavigationOption>();

        private static void Postfix(NavigationOption nav)
        {
            if (nav.ClickableButton == null || nav.TransformToTrack == null) return;
            var prompt = nav.ClickableButton.RectTransform;
            var area = prompt.parent != null ? prompt.parent.parent as RectTransform : null;
            var ui = VrUi.Instance;
            if (area == null || ui == null || !ui.OnMenuCanvas(nav.TransformToTrack.position, out var place)) return;
            // A spot beyond the canvas's edge would lose its frame, and with it the only way to see that the spot can be reached.
            var seen = HabStanding.Inside(place, prompt.rect.width / area.rect.width, prompt.rect.height / area.rect.height);
            prompt.localPosition = new Vector3(seen.x * area.rect.width, seen.y * area.rect.height, prompt.localPosition.z);
            if (logged.Add(nav)) Plugin.Log.LogInfo($"Hab frame on '{nav.TransformToTrack.name}': {prompt.rect.width:0} x {prompt.rect.height:0}, at {place.x:0.00} {place.y:0.00} of the canvas{(seen != place ? ", held inside it" : "")}");
        }

        public static void LogAgain() => logged.Clear();
    }

    [HarmonyPatch(typeof(BBI.Unity.Game.UI.Hab.Hab3DPosterLocation), "UpdateButtonLocations")]
    internal static class PosterPromptPatch
    {
        private static void Postfix(Camera ___m_Camera, Doozy.Engine.UI.UIView ___m_TabButtons, RectTransform ___mTabButtonParentTransform, BBI.Unity.Game.UI.Hab.Hab3DPoster ___mCurrentlySelectedPoster)
        {
            if (!VrXr.IsRunning || VrHab.StandShift == Vector3.zero || ___m_TabButtons == null || ___m_Camera == null || ___mTabButtonParentTransform == null || ___mCurrentlySelectedPoster == null) return;
            ___m_TabButtons.RectTransform.localPosition += VrHab.PromptShift(___m_Camera, ___mCurrentlySelectedPoster.RectTransform.position, ___mTabButtonParentTransform.rect, ___m_Camera.pixelWidth, ___m_Camera.pixelHeight);
        }
    }

    internal static class HabStanding
    {
        // Eye height over the Hab's floor while standing, in meters at world scale 1.
        public const float Height = 1.9f;

        // A spot below this is one where one sits: the view of the room from the bed is at 1.28 m, the lowest one standing 1.81 m.
        private const float SitBelow = 1.5f;

        public static bool Sits(float spot) => spot < SitBelow;

        // A place on the canvas, kept so far in that a frame of the given shares of its size stays on it.
        public static Vector2 Inside(Vector2 place, float width, float height)
        {
            var x = 0.5f - width * 0.5f;
            var y = 0.5f - height * 0.5f;
            return new Vector2(Mathf.Clamp(place.x, -x, x), Mathf.Clamp(place.y, -y, y));
        }

        // Where the line from an eye to a spot crosses the plane z = 0. A spot behind the eye goes to the side it lies on,
        // far out, so a frame for it ends up at that edge.
        public static bool Across(Vector3 eye, Vector3 spot, out Vector2 place)
        {
            place = Vector2.zero;
            var along = eye.z - spot.z;
            if (Mathf.Abs(along) < 1e-5f) return false;
            var share = eye.z / along;
            if (share <= 0f)
            {
                place = new Vector2(spot.x < eye.x ? -1f : 1f, 0f);
                return true;
            }
            place = new Vector2(eye.x + (spot.x - eye.x) * share, eye.y + (spot.y - eye.y) * share);
            return true;
        }

        // Moves a point along up to standing height, keeping where it is in the room. A sitting spot stays.
        public static Vector3 Place(Vector3 point, Vector3 up, float floor, float scale)
        {
            var height = Vector3.Dot(point, up);
            return Sits((height - floor) / scale) ? point : point + up * (floor + Height * scale - height);
        }
    }

    [HarmonyPatch(typeof(LynxCameraController), "OnGameStateChangedEvent")]
    internal static class LynxCameraControllerGameStatePatch
    {
        private static void Postfix(GameStateChangedEvent ev)
        {
            if (ev.GameState == GameSession.GameState.Hab) VrHab.MainCameraOff = true;
            else if (ev.GameState == GameSession.GameState.Gameplay) VrHab.MainCameraOff = false;
        }
    }
}
