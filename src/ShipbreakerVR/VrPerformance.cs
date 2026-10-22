using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // GPU cost that only hurts at VR resolution (two eyes at up to 3360x3600), and frame time numbers to find it.
    //
    // Ambient occlusion: the bay's global profile uses HDRP's Low level (half resolution, 4 steps). The Hab's own volume
    // (HAB_PostprocessProfile2, priority 15) overrides it with a custom level: full resolution, 16 steps, radius up to
    // 50 pixels. That is roughly 30 times the work per eye and made the Hab stutter while the bay ran smoothly. A
    // global volume on our pass layer (only the VR camera has it in its volume mask) puts the quality back to Low;
    // intensity and radius stay as each scene sets them.
    //
    // The Hab's space truck viewer (SpaceTruck3DHABController) renders its own camera into a texture every frame, a
    // third HDRP render next to the two eyes, even though only the truck screen at the terminal shows that image. In
    // VR it renders only while the image is on screen.
    internal static class VrPerformance
    {
        public enum Occlusion { Niedrig, Aus, Spiel }

        private static AmbientOcclusion ambientOcclusion;

        private const float StatsPeriod = 30f;
        private const float StatsSettle = 1f;
        private static readonly List<float> frames = new List<float>(4096);
        private static string statsLabel;
        private static float statsStart;
        // What the current label was made from, so it is only rebuilt when something changes.
        private static Camera labelCamera;
        private static Occlusion labelOcclusion;
        private static float labelScale;

        // Main thread time from Update to the end of rendering, and what Unity's frame timing reports if enabled.
        private static double mainSum, cpuSum, gpuSum;
        private static int mainCount, timingCount;
        private static float updateStart = -1f;
        private static readonly FrameTiming[] timings = new FrameTiming[1];

        private static Camera truckCamera;
        private static readonly List<RawImage> truckImages = new List<RawImage>();
        private static float nextTruckSearch;
        private static bool truckPaused;

        public static void Attach(VrCamera vr)
        {
            var go = new GameObject("ShipbreakerVR Quality") { layer = VrUi.PassLayer };
            go.SetActive(false);
            go.transform.SetParent(vr.transform.parent, false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            // Above every volume of the game (the Hab's is 15).
            volume.priority = 1000f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "ShipbreakerVR Quality";
            ambientOcclusion = profile.Add<AmbientOcclusion>();
            ambientOcclusion.quality.value = (int)ScalableSettingLevelParameter.Level.Low;
            ambientOcclusion.intensity.value = 0f;
            volume.sharedProfile = profile;
            go.SetActive(true);
            RenderPipelineManager.endFrameRendering += (context, cameras) => EndOfRendering();
        }

        // Called from the VR camera's Update.
        public static void FrameStart() => updateStart = Time.realtimeSinceStartup;

        private static void EndOfRendering()
        {
            if (updateStart < 0f || statsLabel == null) return;
            mainSum += Time.realtimeSinceStartup - updateStart;
            mainCount++;
            updateStart = -1f;
        }

        // Called every frame by the VR camera.
        public static void Update(bool running, Camera followed, bool inHab, bool worldHidden)
        {
            PauseTruckCamera(running && inHab);
            if (ambientOcclusion != null)
            {
                var mode = Plugin.AmbientOcclusion.Value;
                ambientOcclusion.quality.overrideState = running && mode == Occlusion.Niedrig;
                ambientOcclusion.intensity.overrideState = running && mode == Occlusion.Aus;
            }
            RecordFrame(running && !worldHidden ? Label(followed, inHab) : null);
        }

        private static string Label(Camera followed, bool inHab)
        {
            if (followed == null) return null;
            var occlusion = Plugin.AmbientOcclusion.Value;
            var scale = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
            if (statsLabel == null || followed != labelCamera || occlusion != labelOcclusion || !Mathf.Approximately(scale, labelScale))
            {
                labelCamera = followed;
                labelOcclusion = occlusion;
                labelScale = scale;
                return $"{(inHab ? "Hab" : $"'{followed.name}'")}, occlusion {occlusion}, {scale:0.00}x";
            }
            return statsLabel;
        }

        // Frame times per place and setting, logged every 30 s and whenever either changes. In VR the frame rate is tied
        // to the headset's: a frame the GPU doesn't finish in time takes two refresh intervals.
        private static void RecordFrame(string label)
        {
            if (label != statsLabel)
            {
                LogStats();
                statsLabel = label;
                statsStart = Time.unscaledTime;
            }
            // The switch itself (new camera, new render targets) is not representative.
            if (statsLabel == null || Time.unscaledTime < statsStart + StatsSettle) return;
            frames.Add(Time.unscaledDeltaTime);
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0.0)
            {
                cpuSum += timings[0].cpuFrameTime;
                gpuSum += timings[0].gpuFrameTime;
                timingCount++;
            }
            if (Time.unscaledTime >= statsStart + StatsSettle + StatsPeriod)
            {
                LogStats();
                statsStart = Time.unscaledTime - StatsSettle;
            }
        }

        private static void LogStats()
        {
            if (statsLabel != null && frames.Count >= 30)
            {
                var sum = 0f;
                foreach (var frame in frames) sum += frame;
                frames.Sort();
                var average = sum / frames.Count;
                var slow = frames[(int)(frames.Count * 0.95f)];
                var refresh = UnityEngine.XR.XRDevice.refreshRate;
                var text = new StringBuilder($"Frame time ({statsLabel}): average {average * 1000f:0.0} ms ({1f / average:0} fps), ");
                text.Append($"slowest 5 % from {slow * 1000f:0.0} ms, slowest {frames[frames.Count - 1] * 1000f:0} ms, {frames.Count} frames");
                if (refresh > 0f) text.Append($", headset {refresh:0} Hz ({1000f / refresh:0.0} ms)");
                // Whether the main thread or the GPU sets the pace (the frame waits for the headset either way).
                if (mainCount > 0) text.Append($", main thread {mainSum / mainCount * 1000.0:0.0} ms");
                text.Append(timingCount > 0 ? $", CPU {cpuSum / timingCount:0.0} ms, GPU {gpuSum / timingCount:0.0} ms" : ", GPU time unavailable");
                if (truckCamera != null) text.Append($", truck viewer {(truckPaused ? "paused" : "rendering")}");
                Plugin.Log.LogInfo(text.ToString());
            }
            frames.Clear();
            mainSum = cpuSum = gpuSum = 0.0;
            mainCount = timingCount = 0;
        }

        private static void PauseTruckCamera(bool pause)
        {
            if (pause && truckCamera == null && Time.unscaledTime >= nextTruckSearch)
            {
                nextTruckSearch = Time.unscaledTime + 1f;
                FindTruckCamera();
            }
            if (truckCamera == null)
            {
                truckPaused = false;
                return;
            }
            var paused = pause && !ShowsTruck();
            if (paused == truckPaused) return;
            truckPaused = paused;
            truckCamera.enabled = !paused;
            Plugin.Log.LogInfo($"Space truck viewer camera {(paused ? "paused, its image is not on screen" : "rendering")}");
        }

        private static void FindTruckCamera()
        {
            // The class is internal to the game.
            var type = AccessTools.TypeByName("BBI.Unity.Game.SpaceTruck3DHABController");
            var controller = type != null ? Object.FindObjectOfType(type) : null;
            var camera = controller != null ? Traverse.Create(controller).Field<Camera>("m_Camera").Value : null;
            if (camera == null || camera.targetTexture == null) return;
            truckImages.Clear();
            foreach (var image in Resources.FindObjectsOfTypeAll<RawImage>())
                if (image.gameObject.scene.IsValid() && image.texture == camera.targetTexture) truckImages.Add(image);
            // Without the image that shows it, it stays as the game has it.
            if (truckImages.Count == 0) return;
            truckCamera = camera;
            truckPaused = false;
            Plugin.Log.LogInfo($"Space truck viewer camera '{camera.name}' found, shown by {truckImages.Count} image(s)");
        }

        // Whether one of the images is drawn: active, on an enabled canvas, not faded out by a canvas group.
        private static bool ShowsTruck()
        {
            foreach (var image in truckImages)
            {
                if (image == null || !image.isActiveAndEnabled) continue;
                var shown = true;
                var ignoreGroups = false;
                for (var t = image.transform; t != null && shown; t = t.parent)
                {
                    var canvas = t.GetComponent<Canvas>();
                    if (canvas != null && !canvas.enabled) shown = false;
                    var group = ignoreGroups ? null : t.GetComponent<CanvasGroup>();
                    if (group == null || !group.enabled) continue;
                    if (group.alpha <= 0f) shown = false;
                    if (group.ignoreParentGroups) ignoreGroups = true;
                }
                if (shown) return true;
            }
            return false;
        }

        // Every other camera that renders each frame costs GPU time too, also when its image only ends up on a screen
        // in the room. Logged once per place, for the next look at performance.
        public static void LogOtherCameras(Camera followed)
        {
            var text = new StringBuilder();
            foreach (var camera in Camera.allCameras)
            {
                if (camera == followed || camera.name.StartsWith("ShipbreakerVR")) continue;
                var target = camera.targetTexture;
                text.Append(text.Length > 0 ? ", " : "").Append($"'{camera.name}' (depth {camera.depth}, {(target != null ? $"texture {target.width}x{target.height}" : "screen")})");
            }
            Plugin.Log.LogInfo($"Other cameras rendering next to '{followed.name}': {(text.Length > 0 ? text.ToString() : "none")}");
        }
    }
}
