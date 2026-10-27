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
            if (VrSettings.SeparateEyeTextures.Value) display.textureLayout = XRDisplaySubsystem.TextureLayout.SeparateTexture2Ds;

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
            if (VrSettings.MultiPass.Value) __result = 1;
        }
    }

    // HDRP's XR settings in the render pipeline asset, which the game shipped without VR in mind.
    internal static class HdrpXrSettings
    {
        private static readonly FieldInfo Settings = AccessTools.Field(typeof(HDRenderPipelineAsset), "m_RenderPipelineSettings");
        private static bool slicesChecked;
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

        // XRSystem only ever raises the slices (Math.Max with the current value), so XrMaxViewsPatch keeps them at one
        // only if they are one when a pipeline is created. Lowering them while targets with two slices exist would pair
        // old and new targets of different array sizes in one render target bind, which D3D11 refuses; so this only
        // reports it.
        public static void CheckSlices()
        {
            if (slicesChecked || !VrSettings.MultiPass.Value || TextureXR.slices <= 1) return;
            slicesChecked = true;
            Plugin.Log.LogWarning($"HDRP render targets have {TextureXR.slices} texture array slices although MultiPass uses one: about twice the video memory they need");
        }
    }
}
