using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Scripting;

namespace ShipbreakerVR
{
    // What HDRP actually does for the VR camera, logged a few seconds after the rig follows a new camera: the frame
    // settings and post effects in effect, the asset's render settings, the realtime shadow lights and planar probes,
    // and once per session the timing and memory settings. For finding work that costs without showing.
    internal static class VrRenderInfo
    {
        private const float Delay = 3f;
        private static float due = -1f;
        private static bool sessionLogged;

        private static readonly MethodInfo RenderGraphEnabled = AccessTools.Method(typeof(HDRenderPipeline), "IsRenderGraphEnabled");
        private static readonly PropertyInfo HdCameraXr = AccessTools.Property(typeof(HDCamera), "xr");

        public static void Schedule() => due = Time.unscaledTime + Delay;

        // Called every frame while VR runs.
        public static void Update(VrCamera vr)
        {
            if (due < 0f || Time.unscaledTime < due || vr.WorldHidden || vr.GameCamera == null) return;
            due = -1f;
            try
            {
                if (!sessionLogged)
                {
                    sessionLogged = true;
                    LogSession();
                }
                LogCamera(vr);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Render info failed: {e.Message}");
            }
        }

        private static void LogSession()
        {
            var pipeline = RenderPipelineManager.currentPipeline;
            var renderGraph = pipeline != null && RenderGraphEnabled != null ? RenderGraphEnabled.Invoke(pipeline, null) : null;
            Plugin.Log.LogInfo($"Render info: {SystemInfo.processorType} ({SystemInfo.processorCount} threads), {SystemInfo.systemMemorySize} MB RAM, " +
                $"{SystemInfo.graphicsDeviceType}, render graph {renderGraph ?? "unknown"}, texture array slices {TextureXR.slices}, " +
                $"multithreaded rendering {SystemInfo.graphicsMultiThreaded}");
            Plugin.Log.LogInfo($"Render info: target frame rate {Application.targetFrameRate}, vSync {QualitySettings.vSyncCount}, max queued frames {QualitySettings.maxQueuedFrames}, " +
                $"fixed step {Time.fixedDeltaTime * 1000f:0.0} ms, max step {Time.maximumDeltaTime * 1000f:0} ms, physics auto sync {Physics.autoSyncTransforms}, " +
                $"incremental GC {GarbageCollector.isIncremental}, GC mode {GarbageCollector.GCMode}, quality level {QualitySettings.GetQualityLevel()} ({QualitySettings.names[QualitySettings.GetQualityLevel()]}), " +
                $"LOD bias {QualitySettings.lodBias}, run in background {Application.runInBackground}");
            if (GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset)
                Plugin.Log.LogInfo($"Render info: HDRP asset '{asset.name}' settings {JsonUtility.ToJson(asset.currentPlatformRenderPipelineSettings)}");
        }

        private static void LogCamera(VrCamera vr)
        {
            var hdCamera = HDCamera.GetOrCreate(vr.Camera, 0);
            var settings = hdCamera.frameSettings;
            var enabled = new StringBuilder();
            foreach (FrameSettingsField field in Enum.GetValues(typeof(FrameSettingsField)))
                if (field != FrameSettingsField.None && settings.IsEnabled(field)) enabled.Append(enabled.Length > 0 ? ", " : "").Append(field);
            Plugin.Log.LogInfo($"Render info for '{vr.GameCamera.name}': frame settings on: {enabled}");

            var stack = hdCamera.volumeStack;
            var effects = new List<string>();
            void Effect(string name, bool active) { if (active) effects.Add(name); }
            Effect("bloom", stack.GetComponent<Bloom>().IsActive());
            Effect("motion blur", stack.GetComponent<MotionBlur>().IsActive());
            Effect("depth of field", stack.GetComponent<DepthOfField>().IsActive());
            Effect("lens distortion", stack.GetComponent<LensDistortion>().IsActive());
            Effect("chromatic aberration", stack.GetComponent<ChromaticAberration>().IsActive());
            Effect("panini", stack.GetComponent<PaniniProjection>().IsActive());
            Effect("vignette", stack.GetComponent<Vignette>().IsActive());
            Effect("film grain", stack.GetComponent<FilmGrain>().IsActive());
            Effect("SSR", stack.GetComponent<ScreenSpaceReflection>().enabled.value);
            Effect("volumetric fog", stack.GetComponent<Fog>().enabled.value && stack.GetComponent<Fog>().enableVolumetricFog.value);
            Effect("contact shadows", stack.GetComponent<ContactShadows>().enable.value);
            var ao = stack.GetComponent<AmbientOcclusion>();
            Effect($"ambient occlusion (intensity {ao.intensity.value:0.00}, quality {ao.quality.value})", ao.intensity.value > 0f);
            var exposure = stack.GetComponent<Exposure>();
            var bloom = stack.GetComponent<Bloom>();
            var xr = HdCameraXr?.GetValue(hdCamera);
            var occlusionMesh = xr != null ? Traverse.Create(xr).Property("hasValidOcclusionMesh").GetValue() : null;
            Plugin.Log.LogInfo($"Render info for '{vr.GameCamera.name}': effects {string.Join(", ", effects)}; exposure {exposure.mode.value}, bloom quality {bloom.quality.value}, " +
                $"anti-aliasing {vr.Camera.GetComponent<HDAdditionalCameraData>().antialiasing}, stop NaNs {vr.Camera.GetComponent<HDAdditionalCameraData>().stopNaNs}, " +
                $"occlusion mesh {occlusionMesh ?? "unknown"}, {hdCamera.actualWidth}x{hdCamera.actualHeight} per eye");

            var lights = new StringBuilder();
            var count = 0;
            foreach (var light in UnityEngine.Object.FindObjectsOfType<HDAdditionalLightData>())
            {
                var unity = light.GetComponent<Light>();
                if (unity == null || !unity.isActiveAndEnabled || unity.shadows == LightShadows.None || (unity.bakingOutput.isBaked && unity.bakingOutput.lightmapBakeType == LightmapBakeType.Baked)) continue;
                count++;
                if (count <= 12) lights.Append(lights.Length > 0 ? ", " : "").Append($"'{light.name}' {unity.type} {light.shadowUpdateMode}");
            }
            var planar = 0;
            foreach (var probe in UnityEngine.Object.FindObjectsOfType<PlanarReflectionProbe>())
                if (probe.isActiveAndEnabled && probe.mode == ProbeSettings.Mode.Realtime) planar++;
            Plugin.Log.LogInfo($"Render info for '{vr.GameCamera.name}': {count} realtime shadow lights ({lights}), {planar} realtime planar reflection probes, " +
                $"{Camera.allCamerasCount} cameras active");
        }
    }
}
