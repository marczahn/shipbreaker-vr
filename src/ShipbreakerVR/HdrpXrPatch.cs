using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
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
}
