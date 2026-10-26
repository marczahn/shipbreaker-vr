using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // What the VR camera renders at: anti-aliasing, the render resolution per eye (with its own setting in the Hab and a
    // lower one while loading), the limit from the video memory, and shrinking HDRP's shared render targets after the
    // resolution went down. Video memory is the tight resource here: a 12 GB card ran out of it at 1.25x and above.
    // VrCamera reports what happens (Retargeted, Loading, Started, Stopped) and calls Update once per frame.
    internal static class VrResolution
    {
        public enum AntiAliasing { TAA, SMAA, FXAA, Keine, Spiel }

        // Rough estimate: HDRP needs about 250 bytes of render targets per pixel and eye (G-buffer, depth, color,
        // history, post effects, and the old targets while the new ones are allocated). Half of the video memory stays
        // free for the game's own textures and meshes. Calibrated on an RTX 4070 Ti (12 GB, 2688x2880 per eye):
        // 1.55 and 2.0 ran out of memory, 1.0 works; the estimate gives 1.25.
        private const float BytesPerPixelAndEye = 250f;
        private const float VideoMemoryShare = 0.5f;
        public const float ResolutionSliderMax = 2f;

        // Render resolution while only the loading UI shows: the headset's native one, so the load screen stays sharp
        // while the level being loaded gets the video memory (at 1.25x the level start ran out of it and crashed).
        private const float LoadingResolution = 1f;

        // Highest render resolution in the Hab. The bay stays loaded next to it, and at 1.25x the 12 GB card ran out of
        // video memory each time the Hab appeared (Failed to create RenderTexture ... 0x8007000e). It also leaves the
        // Hab more room to reach the headset's frame rate.
        private const float HabResolution = 1f;

        // The Hab costs the GPU more than the bay: on an RTX 4070 Ti at 2688x2880 per eye it ran at 45 fps at 1.0x and at
        // the headset's 90 at 0.7x. Below the headset's rate Virtual Desktop halves it and synthesizes every second frame,
        // and even a few missed frames (3 % at 0.8x) wobble close to the monitors. So the Hab has its own resolution
        // setting ([Bild] AufloesungStation), independent of the one for everywhere else, at most HabResolution.
        private static float HabScale => Mathf.Clamp(Plugin.StationResolution.Value, 0.5f, HabResolution);

        private static float maxResolutionScale = -1f;
        private static float pendingResolution = -1f;
        private static float resolutionChangedAt;
        private static float shrinkAt = -1f;
        private static bool habScaleLogged;
        private static HDAdditionalCameraData.AntialiasingMode gameAntialiasing;

        // The VR camera took over another game camera's HDRP settings, with its anti-aliasing (AntiAliasing.Spiel).
        public static void Retargeted(HDAdditionalCameraData.AntialiasingMode game) => gameAntialiasing = game;

        // Only the loading UI shows: down to LoadingResolution, and the render targets shrink with it.
        public static void Loading()
        {
            var current = XRSettings.eyeTextureResolutionScale;
            if (!VrXr.IsRunning || current <= LoadingResolution + 0.01f) return;
            XRSettings.eyeTextureResolutionScale = LoadingResolution;
            shrinkAt = Time.unscaledTime + 0.5f;
            Plugin.Log.LogInfo($"Render resolution per eye while loading: {LoadingResolution:0.00}x (was {current:0.00}x)");
        }

        // When VR starts the eye textures are created at 1.0 and, a second later, again at the configured scale.
        // During a mission load that second allocation ran out of video memory. So lower the scale right away;
        // raising above the current value still waits and goes through the memory probe in Update.
        public static void Started()
        {
            shrinkAt = Time.unscaledTime + 2f;
            var max = MaxResolutionScale();
            var scale = Mathf.Clamp(Plugin.ResolutionScale.Value, 0.5f, max > 0f ? max : 1f);
            var current = XRSettings.eyeTextureResolutionScale;
            if (scale >= current || Mathf.Approximately(scale, current)) return;
            XRSettings.eyeTextureResolutionScale = scale;
            Plugin.Log.LogInfo($"Render resolution per eye at VR start: {scale:0.00}x");
        }

        // VR stopped: the render targets shrink to the flat view's size.
        public static void Stopped() => shrinkAt = Time.unscaledTime + 2f;

        // From VrCamera.LateUpdate, every frame.
        public static void Update(bool running, HDAdditionalCameraData vrHd, Camera gameCamera, bool followsHab, bool worldHidden)
        {
            ShrinkRenderTargets(running, gameCamera);
            if (running) Apply(vrHd, followsHab, worldHidden);
        }

        private static void Apply(HDAdditionalCameraData vrHd, bool followsHab, bool worldHidden)
        {
            var mode = Plugin.AntiAliasing.Value switch
            {
                AntiAliasing.TAA => HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing,
                AntiAliasing.SMAA => HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing,
                AntiAliasing.FXAA => HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing,
                AntiAliasing.Keine => HDAdditionalCameraData.AntialiasingMode.None,
                _ => gameAntialiasing,
            };
            if (vrHd.antialiasing != mode)
            {
                vrHd.antialiasing = mode;
                Plugin.Log.LogInfo($"VR camera anti-aliasing: {mode} (game: {gameAntialiasing})");
            }
            // While loading the resolution stays at LoadingResolution (Loading); once the world shows again it goes
            // back to the configured one, through the memory probe below.
            if (worldHidden) return;
            // Each change reallocates every render target. Wait until the slider has rested, otherwise dragging it
            // through all steps fragments video memory until allocations fail and the image smears.
            var max = MaxResolutionScale();
            if (max > 0f && Plugin.ResolutionScale.Value > max)
            {
                Plugin.ResolutionScale.Value = max;
                VrOptions.Refresh(Plugin.ResolutionScale);
            }
            var scale = Mathf.Clamp(followsHab ? HabScale : Plugin.ResolutionScale.Value, 0.5f, max > 0f ? max : 1f);
            if (followsHab != habScaleLogged)
            {
                habScaleLogged = followsHab;
                if (followsHab) Plugin.Log.LogInfo($"Render resolution in the Hab: {scale:0.00}x (station setting, at most {HabResolution:0.00}x since the bay stays in video memory next to it)");
            }
            if (!Mathf.Approximately(scale, pendingResolution))
            {
                pendingResolution = scale;
                resolutionChangedAt = Time.unscaledTime;
            }
            if (Time.unscaledTime - resolutionChangedAt < 1f) return;
            var current = XRSettings.eyeTextureResolutionScale;
            if (!Mathf.Approximately(current, scale))
            {
                // Going up: first check that the new targets can be allocated while the old ones still exist,
                // otherwise stay at the previous value instead of ending up with a black or smeared image.
                if (scale > current && !FitsIntoMemory(scale))
                {
                    Plugin.Log.LogWarning($"Render resolution {scale:0.00}x does not fit into video memory, staying at {current:0.00}x");
                    var entry = followsHab ? Plugin.StationResolution : Plugin.ResolutionScale;
                    entry.Value = current;
                    pendingResolution = current;
                    VrOptions.Refresh(entry);
                    return;
                }
                XRSettings.eyeTextureResolutionScale = scale;
                Plugin.Log.LogInfo($"Render resolution per eye: {scale:0.00}x");
                if (scale < current) shrinkAt = Time.unscaledTime + 1f;
            }
        }

        // HDRP's shared render targets only grow: they keep the largest width and height any camera rendered at
        // (RTHandleSystem), and the game never shrinks them. After VR starts or stops, or the render resolution goes
        // down, shrink them once to what is rendered now, so memory held for the flat view or a higher resolution is
        // freed. A camera that needs more simply grows them again.
        private static void ShrinkRenderTargets(bool running, Camera gameCamera)
        {
            if (shrinkAt < 0f || Time.unscaledTime < shrinkAt) return;
            shrinkAt = -1f;
            int width, height;
            if (running)
            {
                width = XRSettings.eyeTextureWidth;
                height = XRSettings.eyeTextureHeight;
            }
            else if (gameCamera != null)
            {
                width = gameCamera.pixelWidth;
                height = gameCamera.pixelHeight;
            }
            else
            {
                width = Screen.width;
                height = Screen.height;
            }
            int maxWidth = RTHandles.maxWidth, maxHeight = RTHandles.maxHeight;
            // Compare with what they grow to once this size renders, and skip resets that would free next to nothing.
            if (width <= 0 || height <= 0 || (float)Mathf.Max(maxWidth, width) * Mathf.Max(maxHeight, height) < 1.05f * width * height) return;
            // Only the shared targets: each camera's history goes away with the camera (HDCamera.CleanUnused), and
            // HDRenderPipeline.ResetRTHandleReferenceSize would also stretch other cameras' history to this size.
            RTHandles.ResetReferenceSize(width, height);
            Plugin.Log.LogInfo($"HDRP render targets reduced from {maxWidth}x{maxHeight} to {width}x{height} ({(running ? "eye" : "screen")} size)");
        }

        // Highest render resolution that should fit into video memory, rounded down to 0.05; -1 until the headset's
        // native eye size is known.
        private static float MaxResolutionScale()
        {
            if (maxResolutionScale > 0f) return maxResolutionScale;
            var current = XRSettings.eyeTextureResolutionScale;
            var width = XRSettings.eyeTextureWidth / current;
            var height = XRSettings.eyeTextureHeight / current;
            if (width <= 0f || height <= 0f || SystemInfo.graphicsMemorySize <= 0) return -1f;
            var budget = SystemInfo.graphicsMemorySize * 1024f * 1024f * VideoMemoryShare;
            var fit = Mathf.Sqrt(budget / (width * height * 2f * BytesPerPixelAndEye));
            maxResolutionScale = Mathf.Clamp(Mathf.Floor(fit * 20f) / 20f, 0.5f, ResolutionSliderMax);
            Plugin.Log.LogInfo($"{SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsMemorySize} MB video memory, {width:0}x{height:0} per eye: render resolution limited to {maxResolutionScale:0.00}x");
            return maxResolutionScale;
        }

        // Allocates test textures as large as the render targets at this scale would be (about 160 bytes per pixel
        // and eye) and frees them again. Fails if the graphics driver refuses one of them.
        private static bool FitsIntoMemory(float scale)
        {
            var current = XRSettings.eyeTextureResolutionScale;
            var width = Mathf.CeilToInt(XRSettings.eyeTextureWidth / current * scale);
            var height = Mathf.CeilToInt(XRSettings.eyeTextureHeight / current * scale);
            if (width <= 0 || height <= 0) return true;
            const int bytesPerTexel = 8;
            var count = Mathf.CeilToInt(160f * 2f / bytesPerTexel);
            var probes = new List<RenderTexture>();
            var ok = true;
            try
            {
                for (var i = 0; i < count && ok; i++)
                {
                    var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf) { name = "ShipbreakerVR memory probe" };
                    probes.Add(rt);
                    ok = rt.Create();
                }
            }
            finally
            {
                foreach (var rt in probes)
                {
                    rt.Release();
                    Object.Destroy(rt);
                }
            }
            Plugin.Log.LogInfo($"Video memory probe for {scale:0.00}x ({width}x{height}, {probes.Count} of {count} textures): {(ok ? "fits" : "too large")}");
            return ok;
        }
    }
}
