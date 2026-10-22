using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ShipbreakerVR
{
    // GPU cost that only hurts at VR resolution (two eyes at up to 3360x3600), and frame time numbers to find it.
    //
    // Ambient occlusion: the bay's global profile uses HDRP's Low level (half resolution, 4 steps). The Hab's own volume
    // (HAB_PostprocessProfile2, priority 15) overrides it with a custom level: full resolution, 16 steps, radius up to
    // 50 pixels. That is roughly 30 times the work per eye and made the Hab stutter while the bay ran smoothly. A
    // global volume on our pass layer (only the VR camera has it in its volume mask) puts the quality back to Low;
    // intensity and radius stay as each scene sets them.
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
        }

        // Called every frame by the VR camera.
        public static void Update(bool running, Camera followed, bool inHab, bool worldHidden)
        {
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
                Plugin.Log.LogInfo(text.ToString());
            }
            frames.Clear();
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
