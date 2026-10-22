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

        // The navigable screens (monitor above the bed, terminal, workbench): their virtual cameras sit 35 cm in front
        // of a monitor, and the screen comes as an overlay over the whole picture. In VR the overlay is on the menu
        // canvas 2.4 m away, drawn in front of a monitor at 35 cm: two distances at once for the eyes. So in VR the rig
        // stays where it was (placeVirtualCamera) and the room is dimmed behind the canvas instead, like a full screen.
        private const float ScreenDimTarget = 0.75f;
        private const float ScreenDimTime = 0.3f;
        private static readonly HashSet<ICinemachineCamera> screenCameras = new HashSet<ICinemachineCamera>();
        private static ICinemachineCamera placeVirtualCamera;
        private static float lastDimTime;

        // How far the VR view is faded to black, 0 to 1.
        public static float Fade { get; private set; }

        // True while the view fades out: the rig stays where it was until it is black.
        public static bool Holding => transitionStart >= 0f && Time.unscaledTime - transitionStart < FadeOut;

        // True while one of the navigable screens is open: the rig stays where it was. Unless the Hab opened right at a
        // screen, with no spot to stay at yet.
        public static bool AtScreen => cutBrain != null && placeVirtualCamera != null && IsScreen(cutBrain.ActiveVirtualCamera);

        // How far the room behind the menu canvas is dimmed for a navigable screen, 0 to ScreenDimTarget.
        public static float ScreenDim { get; private set; }

        private static bool IsScreen(ICinemachineCamera camera) => camera != null && screenCameras.Contains(camera);

        // Remembers the virtual cameras of a navigation option that opens a screen.
        public static void AddScreen(NavigationOption navOpt)
        {
            if (navOpt == null || !navOpt.GoesToNavigableScreen) return;
            if (navOpt.CameraToGoTo != null) screenCameras.Add(navOpt.CameraToGoTo);
            if (navOpt.CameraToSwapToAfterBlend != null) screenCameras.Add(navOpt.CameraToSwapToAfterBlend);
        }

        private static void FindScreens()
        {
            var options = AccessTools.FieldRefAccess<Hab3DLocationNavigation, List<NavigationOption>>("m_NavigationOptions");
            foreach (var location in Resources.FindObjectsOfTypeAll<Hab3DLocationNavigation>())
            {
                if (!location.gameObject.scene.IsValid() || options(location) == null) continue;
                foreach (var navOpt in options(location)) AddScreen(navOpt);
            }
            Plugin.Log.LogInfo($"Hab screens: {screenCameras.Count} virtual camera(s)");
        }

        // Original amplitude of each sway (Perlin noise) we silenced; the game itself toggles their enabled flag.
        private static readonly Dictionary<CinemachineBasicMultiChannelPerlin, float> calmed = new Dictionary<CinemachineBasicMultiChannelPerlin, float>();
        private static float nextNoiseScan;

        // Mirrors what LynxCameraController did with the main camera: off in the Hab, on in gameplay. The camera's own
        // enabled flag doesn't tell, because the VR camera keeps the camera it follows switched off.
        public static bool MainCameraOff { get; set; }

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
                    if (controller != null)
                    {
                        screenCameras.Clear();
                        FindScreens();
                    }
                }
                if (controller == null || !controller.isActiveAndEnabled || controller.CinemachineBrain == null) return null;
                var camera = controller.CinemachineBrain.OutputCamera;
                return camera != null && camera.gameObject.activeInHierarchy ? camera : null;
            }
        }

        public static bool IsHabCamera(Camera camera) => camera != null && camera.GetComponentInParent<Hab3DController>() != null;

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
            transitionStart = -1f;
            Fade = 0f;
            ScreenDim = 0f;
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
            // Into a screen the rig stays, and back out to where it stayed it doesn't move either.
            if (lastVirtualCamera != null && current != lastVirtualCamera && !IsScreen(current) && current != placeVirtualCamera)
            {
                var elapsed = transitionStart >= 0f ? now - transitionStart : float.MaxValue;
                // Fading out or black already: the rig jumps to wherever the camera is once it is black.
                // Fading in: fade out again from where the fade is now.
                if (elapsed >= FadeOut + Black) transitionStart = now - Fade * FadeOut;
            }
            lastVirtualCamera = current;
            if (!IsScreen(current)) placeVirtualCamera = current;

            // Called more than once per frame, so the step follows the time since the last call.
            var dimTarget = IsScreen(current) ? ScreenDimTarget : 0f;
            ScreenDim = Mathf.MoveTowards(ScreenDim, dimTarget, (now - lastDimTime) * ScreenDimTarget / ScreenDimTime);
            lastDimTime = now;

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
    // game fades out the monitor in the room: what it shows comes as an overlay over the whole picture instead. In VR that
    // overlay is on our canvas in front of the player, and the switched-off monitor stayed in the room as a black
    // rectangle, above the player when lying in bed. So while the UI is in VR the monitor keeps its screensaver.
    [HarmonyPatch(typeof(Hab3DLocationNavigation), nameof(Hab3DLocationNavigation.Navigate))]
    internal static class HabScreenNavigatePatch
    {
        private static void Postfix(NavigationOption navOpt)
        {
            VrHab.AddScreen(navOpt);
            if (navOpt == null || !navOpt.GoesToNavigableScreen || navOpt.ScreenAnimator == null) return;
            if (VrUi.Instance == null || !VrUi.Instance.Active) return;
            navOpt.ScreenAnimator.ResetTrigger("FadeOut");
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
