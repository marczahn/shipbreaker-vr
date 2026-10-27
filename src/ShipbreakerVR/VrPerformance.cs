using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using UnityEngine.XR;

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
    //
    // Effects that are wrong in a headset, whatever they cost: motion blur (the eyes and the head make their own),
    // depth of field (focused where the flat camera looks, not where the eyes do), lens distortion, chromatic
    // aberration and Panini projection (they bend or split the picture against the lenses' own correction). The same
    // volume switches them off for the VR camera ([Bild] VrUnpassendeEffekteAus); depth of field and motion blur at
    // full resolution per eye are also among the most expensive post effects.
    internal static class VrPerformance
    {
        public enum Occlusion { Niedrig, Aus, Spiel }

        private static AmbientOcclusion ambientOcclusion;
        private static MotionBlur motionBlur;
        private static DepthOfField depthOfField;
        private static LensDistortion lensDistortion;
        private static ChromaticAberration chromaticAberration;
        private static PaniniProjection panini;
        private static VolumeProfile profile;

        private const float StatsPeriod = 30f;
        private const float StatsSettle = 1f;
        private static readonly List<float> frames = new List<float>(4096);
        private static string statsLabel;
        private static float statsStart;
        private static float bestRefresh;
        // What the current label was made from, so it is only rebuilt when something changes.
        private static Camera labelCamera;
        private static Occlusion labelOcclusion;
        private static float labelScale;
        private static bool labelSharing;
        private static int labelWorkers;

        // Where a frame's time goes: the main thread from Update until rendering starts (game logic), rendering on the
        // main thread (which also waits for the headset's next frame, so it fills up to the frame time), and the GPU
        // time the headset runtime reports for the app, if it does.
        private static double logicSum, renderSum, gpuSum;
        private static int logicCount, renderCount, gpuCount;
        private static float updateStart = -1f;
        private static float renderStart = -1f;
        private static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();

        // Physics steps (FixedUpdate runs 0 to several times per frame) and garbage collections during a period.
        private static int fixedSteps;
        private static int gcStart;
        private static int cullFrames;

        // GPU time of the VR camera's render (both eyes, from our BeforeRendering pass to the end of post-processing),
        // if Unity's GPU recorder works in this player. Release players may not support it; logged once.
        private static CustomSampler gpuSampler;
        private static Recorder gpuRecorder;
        private static bool gpuLogged;
        private static double gpuEyeSum;
        private static int gpuEyeCount;

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
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "ShipbreakerVR Quality";
            ambientOcclusion = profile.Add<AmbientOcclusion>();
            ambientOcclusion.quality.value = (int)ScalableSettingLevelParameter.Level.Low;
            ambientOcclusion.intensity.value = 0f;
            motionBlur = profile.Add<MotionBlur>();
            motionBlur.intensity.value = 0f;
            depthOfField = profile.Add<DepthOfField>();
            depthOfField.focusMode.value = DepthOfFieldMode.Off;
            lensDistortion = profile.Add<LensDistortion>();
            lensDistortion.intensity.value = 0f;
            chromaticAberration = profile.Add<ChromaticAberration>();
            chromaticAberration.intensity.value = 0f;
            panini = profile.Add<PaniniProjection>();
            panini.distance.value = 0f;
            volume.sharedProfile = profile;
            go.SetActive(true);
            AttachGpuTimer(vr);
            RenderPipelineManager.beginFrameRendering += OnBeginFrame;
            RenderPipelineManager.endFrameRendering += OnEndFrame;
        }

        // Unity's events would otherwise keep calling into an unloaded instance after a reload.
        public static void Detach()
        {
            RenderPipelineManager.beginFrameRendering -= OnBeginFrame;
            RenderPipelineManager.endFrameRendering -= OnEndFrame;
            // The volume went with the rig; its profile is not part of it.
            if (profile == null) return;
            foreach (var component in profile.components) Object.Destroy(component);
            Object.Destroy(profile);
        }

        private static void OnBeginFrame(ScriptableRenderContext context, Camera[] cameras) => StartOfRendering();

        private static void OnEndFrame(ScriptableRenderContext context, Camera[] cameras) => EndOfRendering();

        // Called from the VR camera's Update.
        public static void FrameStart() => updateStart = Time.realtimeSinceStartup;

        // Called from the VR camera's FixedUpdate.
        public static void FixedStep() => fixedSteps++;

        // A pass right at the start of the VR camera's render opens the GPU sample, our UI pass after post-processing
        // closes it (VrUiPass).
        private static void AttachGpuTimer(VrCamera vr)
        {
            gpuSampler = CustomSampler.Create("ShipbreakerVR eye", true);
            gpuRecorder = gpuSampler != null && gpuSampler.isValid ? gpuSampler.GetRecorder() : null;
            if (gpuRecorder != null && gpuRecorder.isValid) gpuRecorder.enabled = true;
            var go = new GameObject("ShipbreakerVR GPU Timer") { layer = VrUi.PassLayer };
            go.SetActive(false);
            go.transform.SetParent(vr.transform.parent, false);
            var volume = go.AddComponent<CustomPassVolume>();
            volume.isGlobal = true;
            volume.injectionPoint = CustomPassInjectionPoint.BeforeRendering;
            volume.AddPassOfType<VrGpuTimerPass>();
            go.SetActive(true);
        }

        public static void BeginGpuSample(CustomPassContext context)
        {
            if (gpuRecorder == null || !gpuRecorder.isValid || !IsVrCamera(context)) return;
            context.cmd.BeginSample(gpuSampler);
        }

        public static void EndGpuSample(CustomPassContext context)
        {
            if (gpuRecorder == null || !gpuRecorder.isValid || !IsVrCamera(context)) return;
            context.cmd.EndSample(gpuSampler);
        }

        private static bool IsVrCamera(CustomPassContext context)
        {
            var vr = VrCamera.Instance;
            return vr != null && VrXr.IsRunning && context.hdCamera.camera == vr.Camera;
        }

        // Once, whether Unity's GPU recorder gives numbers in this player.
        private static void LogGpuTimer()
        {
            if (gpuLogged) return;
            gpuLogged = true;
            var supports = typeof(SystemInfo).GetProperty("supportsGpuRecorder", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            Plugin.Log.LogInfo($"GPU timer: sampler {(gpuSampler != null && gpuSampler.isValid ? "valid" : "invalid")}, recorder {(gpuRecorder != null && gpuRecorder.isValid ? "valid" : "invalid")}, GPU recorder supported: {supports ?? "unknown"}");
        }

        // Whether the frame time line is recording; the render profile (VrRenderProfile) only times then.
        public static bool Recording => statsLabel != null;

        // The last frame's two parts, for the overlay (VrPerformanceOverlay).
        public static float FrameLogic { get; private set; }
        public static float FrameRender { get; private set; }

        private static void StartOfRendering()
        {
            renderStart = Time.realtimeSinceStartup;
            if (updateStart < 0f) return;
            FrameLogic = renderStart - updateStart;
            updateStart = -1f;
            if (statsLabel == null) return;
            logicSum += FrameLogic;
            logicCount++;
        }

        private static void EndOfRendering()
        {
            if (renderStart < 0f) return;
            FrameRender = Time.realtimeSinceStartup - renderStart;
            renderStart = -1f;
            if (statsLabel == null) return;
            renderSum += FrameRender;
            renderCount++;
        }

        // Called every frame by the VR camera.
        public static void Update(bool running, Camera followed, bool inHab, bool worldHidden)
        {
            PauseTruckCamera(running && inHab);
            if (ambientOcclusion != null)
            {
                var mode = VrSettings.AmbientOcclusion.Value;
                ambientOcclusion.quality.overrideState = running && mode == Occlusion.Niedrig;
                ambientOcclusion.intensity.overrideState = running && mode == Occlusion.Aus;
                var off = running && VrSettings.NoVrUnfitEffects.Value;
                motionBlur.intensity.overrideState = off;
                depthOfField.focusMode.overrideState = off;
                lensDistortion.intensity.overrideState = off;
                chromaticAberration.intensity.overrideState = off;
                panini.distance.overrideState = off;
            }
            // Logged with the diagnostics, and while the overlay shows, so a test with it on leaves numbers in the log.
            var logged = VrSettings.Diagnostics.Value || VrSettings.PerformanceOverlay.Value;
            RecordFrame(logged && running && !worldHidden ? Label(followed, inHab) : null);
            VrPerformanceOverlay.Update(running, inHab);
        }

        private static string Label(Camera followed, bool inHab)
        {
            if (followed == null) return null;
            var occlusion = VrSettings.AmbientOcclusion.Value;
            var scale = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
            var sharing = VrSettings.EyeSharing.Value;
            if (statsLabel == null || followed != labelCamera || occlusion != labelOcclusion || !Mathf.Approximately(scale, labelScale) || sharing != labelSharing || VrJobWorkers.Count != labelWorkers)
            {
                labelCamera = followed;
                labelOcclusion = occlusion;
                labelScale = scale;
                labelSharing = sharing;
                labelWorkers = VrJobWorkers.Count;
                return $"{(inHab ? "Hab" : $"'{followed.name}'")}, occlusion {occlusion}, {scale:0.00}x, eye sharing {(sharing ? "on" : "off")}, {labelWorkers} job workers";
            }
            return statsLabel;
        }

        // With Debug/Diagnose on: frame times per place and setting, logged every 30 s and whenever either changes. In VR the frame rate is tied
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
            if (statsLabel == null || Time.unscaledTime < statsStart + StatsSettle)
            {
                ResetCounters();
                return;
            }
            frames.Add(Time.unscaledDeltaTime);
            cullFrames++;
            VrRenderProfile.SampleCounters();
            if (gpuRecorder != null && gpuRecorder.isValid)
            {
                LogGpuTimer();
                // The total of the frame's samples, i.e. both eyes.
                var nanoseconds = gpuRecorder.gpuElapsedNanoseconds;
                if (nanoseconds > 0)
                {
                    gpuEyeSum += nanoseconds / 1e9;
                    gpuEyeCount++;
                }
            }
            else LogGpuTimer();
            SubsystemManager.GetInstances(displays);
            foreach (var display in displays)
                if (display.running && display.TryGetAppGPUTimeLastFrame(out var gpu) && gpu > 0f)
                {
                    gpuSum += gpu;
                    gpuCount++;
                    break;
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
                bestRefresh = Mathf.Max(bestRefresh, refresh);
                // Missed frames are counted against the best rate seen, the headset's own: at half rate it reports half.
                var missed = bestRefresh > 0f ? MissedShare(bestRefresh) : 0f;
                var text = new StringBuilder($"Frame time ({statsLabel}): average {average * 1000f:0.0} ms ({1f / average:0} fps), ");
                text.Append($"slowest 5 % from {slow * 1000f:0.0} ms, slowest {frames[frames.Count - 1] * 1000f:0} ms, {frames.Count} frames");
                if (refresh > 0f) text.Append($", headset {refresh:0} Hz ({1000f / refresh:0.0} ms), {missed * 100f:0.0} % missed at {bestRefresh:0} Hz");
                // Whether the main thread or the GPU sets the pace.
                if (logicCount > 0) text.Append($", game logic {logicSum / logicCount * 1000.0:0.0} ms");
                if (renderCount > 0) text.Append($", rendering with waiting {renderSum / renderCount * 1000.0:0.0} ms");
                // Unity doesn't document the unit; runtimes report seconds or milliseconds.
                var gpu = gpuCount > 0 ? gpuSum / gpuCount : 0.0;
                text.Append(gpuCount > 0 ? $", app GPU {(gpu < 1.0 ? gpu * 1000.0 : gpu):0.0} ms" : ", GPU time unavailable");
                if (gpuEyeCount > 0) text.Append($", GPU both eyes {gpuEyeSum / gpuEyeCount * 1000.0:0.0} ms");
                if (truckCamera != null) text.Append($", truck viewer {(truckPaused ? "paused" : "rendering")}");
                // CPU side: culling (twice per frame unless the eyes share it), physics steps and garbage collections.
                if (cullFrames > 0)
                {
                    text.Append($", culling {(double)VrEyeSharing.CullCalls / cullFrames:0.0}x per frame, {VrEyeSharing.CullSeconds / cullFrames * 1000.0:0.00} ms");
                    text.Append($", physics steps {(double)fixedSteps / cullFrames:0.00} per frame");
                }
                text.Append($", {System.GC.CollectionCount(0) - gcStart} garbage collections, {System.GC.GetTotalMemory(false) / (1024 * 1024)} MB managed");
                if (VrEyeSharing.SharedThisFrame && VrSettings.EyeSharing.Value) text.Append($", {VrEyeSharing.ReusedAtlases} shadow atlases reused");
                Plugin.Log.LogInfo(text.ToString());
                if (cullFrames > 0)
                {
                    var steps = VrFrameProfile.Describe(cullFrames);
                    if (steps != null) Plugin.Log.LogInfo($"Frame steps ({statsLabel}), main thread per frame: {steps}");
                    var render = VrRenderProfile.Describe(cullFrames);
                    if (render != null) Plugin.Log.LogInfo($"Render steps ({statsLabel}), main thread per frame: {render}");
                    var systems = VrSystemProfile.Describe(cullFrames);
                    if (systems != null) Plugin.Log.LogInfo($"ECS systems ({statsLabel}), main thread per frame: {systems}");
                }
            }
            frames.Clear();
            logicSum = renderSum = gpuSum = 0.0;
            logicCount = renderCount = gpuCount = 0;
            ResetCounters();
        }

        private static void ResetCounters()
        {
            fixedSteps = 0;
            cullFrames = 0;
            VrEyeSharing.CullCalls = 0;
            VrEyeSharing.CullSeconds = 0.0;
            gcStart = System.GC.CollectionCount(0);
            gpuEyeSum = 0.0;
            gpuEyeCount = 0;
            VrFrameProfile.Reset();
            VrRenderProfile.Reset();
            VrSystemProfile.Reset();
        }

        // Share of frames that took 1.4 to 4 intervals of the given refresh rate: missed by the GPU, which the runtime
        // fills with a synthesized frame. Longer ones are hitches (loading, a camera cut).
        private static float MissedShare(float rate)
        {
            var interval = 1f / rate;
            var missed = 0;
            foreach (var frame in frames)
                if (frame >= 1.4f * interval && frame < 4f * interval) missed++;
            return (float)missed / frames.Count;
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

    // Opens the GPU sample of the VR camera's render (VrPerformance).
    internal class VrGpuTimerPass : CustomPass
    {
        protected override void Execute(CustomPassContext ctx) => VrPerformance.BeginGpuSample(ctx);
    }
}
