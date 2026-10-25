using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Every frame HDRP sets the XR display to Texture2DArray (one image with two slices, one per eye).
    // Optionally (config SeparateEyeTextures) we switch to separate textures per eye instead.
    // Also logs once how the display actually renders.
    [HarmonyPatch]
    internal static class HdrpXrPatch
    {
        // XRSystem is internal, so look it up by name.
        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.XRSystem"), "RefreshXrSdk");

        private static readonly List<XRDisplaySubsystem> Displays = new List<XRDisplaySubsystem>();
        private static bool logged;

        private static void Postfix()
        {
            SubsystemManager.GetSubsystems(Displays);
            if (Displays.Count == 0) return;

            var display = Displays[0];
            if (Plugin.SeparateEyeTextures.Value) display.textureLayout = XRDisplaySubsystem.TextureLayout.SeparateTexture2Ds;

            if (logged || !display.running) return;
            logged = true;
            Plugin.Log.LogInfo($"XR display: supported layouts {display.supportedTextureLayouts}, set {display.textureLayout}, passes {display.GetRenderPassCount()}");
        }
    }

    // HDRP allocates every screen-sized render target (G-buffer, depth pyramid, color pyramid, history, post effects) as
    // a texture array with TextureXR.slices slices. XRSystem's constructor sets it to 2 whenever an XR display exists
    // when the pipeline is created, and nothing ever lowers it again. In MultiPass each eye is a render request of its
    // own that only uses slice 0, so a second slice would be video memory that is never read. The pipeline is normally
    // created before the plugin starts OpenXR; one recreated afterwards (the game applying quality settings with another
    // asset, for example) would have both slices. With MultiPass the pipeline gets one slice.
    [HarmonyPatch]
    internal static class XrMaxViewsPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Rendering.HighDefinition.XRSystem"), "GetMaxViews");

        private static void Postfix(ref int __result)
        {
            if (Plugin.MultiPass.Value) __result = 1;
        }
    }

    // HDRP's XR settings in the render pipeline asset, which the game shipped without VR in mind.
    internal static class HdrpXrSettings
    {
        private static readonly FieldInfo Settings = AccessTools.Field(typeof(HDRenderPipelineAsset), "m_RenderPipelineSettings");
        private static bool slicesFixed;
        private static bool loggedSettings;

        // Every frame while VR runs. The occlusion mesh (the parts of each eye the lenses never show) marks those pixels
        // as done before anything is drawn, if the runtime supplies one; HDRP only uses it if the asset says so. Camera
        // jitter is what TAA needs to gather detail over several frames: in XR, HDRP leaves it out unless the asset asks
        // for it, and TAA without jitter only blurs (the washed-out TAA of earlier versions).
        public static void Update(bool taa)
        {
            var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (asset == null || Settings == null) return;
            var settings = asset.currentPlatformRenderPipelineSettings;
            var xr = settings.xrSettings;
            if (!loggedSettings)
            {
                loggedSettings = true;
                Plugin.Log.LogInfo($"HDRP asset '{asset.name}' XR settings: single pass {xr.singlePass}, occlusion mesh {xr.occlusionMesh}, camera jitter {xr.cameraJitter}");
            }
            if (xr.occlusionMesh && xr.cameraJitter == taa) return;
            if (!xr.occlusionMesh) Plugin.Log.LogInfo("HDRP occlusion mesh switched on for VR");
            if (xr.cameraJitter != taa) Plugin.Log.LogInfo($"HDRP camera jitter in VR {(taa ? "on (TAA)" : "off")}");
            xr.occlusionMesh = true;
            xr.cameraJitter = taa;
            settings.xrSettings = xr;
            Settings.SetValue(asset, settings);
        }

        // While the world is hidden (loading), so the new targets are allocated while little else needs video memory.
        // Targets already allocated keep their two slices until HDRP frees them; the render graph's pooled ones go
        // within a few frames.
        public static void FixSlices()
        {
            if (slicesFixed || !Plugin.MultiPass.Value || TextureXR.slices <= 1) return;
            slicesFixed = true;
            Plugin.Log.LogInfo($"HDRP render targets had {TextureXR.slices} texture array slices; MultiPass uses one, set to 1");
            TextureXR.maxViews = 1;
        }
    }
}
