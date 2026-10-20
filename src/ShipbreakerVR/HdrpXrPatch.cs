using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // HDRP stellt das XR-Display bei jedem Frame auf Texture2DArray (ein Bild mit zwei Ebenen, eine pro Auge).
    // Optional (Config SeparateEyeTextures) stellen wir stattdessen getrennte Texturen pro Auge ein.
    // Protokolliert außerdem einmal, wie das Display tatsächlich rendert.
    [HarmonyPatch]
    internal static class HdrpXrPatch
    {
        // XRSystem ist internal, deshalb per Name suchen.
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
            Plugin.Log.LogInfo($"XR-Display: unterstützte Layouts {display.supportedTextureLayouts}, gesetzt {display.textureLayout}, Durchgänge {display.GetRenderPassCount()}");
        }
    }
}
