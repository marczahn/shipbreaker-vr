using HarmonyLib;

namespace ShipbreakerVR
{
    // With XR active the game sets ScreenWidth/ScreenHeight to the eye texture size. But the tools aim through
    // the flat game camera (MainCamera.ScreenPointToRay) using ScreenCenter; its screen center is elsewhere, and the
    // cutter then fires upward. So we always use the game camera's pixel size.
    [HarmonyPatch(typeof(LynxCameraController), "UpdateScreenDimensions")]
    internal static class ScreenSizePatch
    {
        private static readonly System.Reflection.MethodInfo SetWidth = AccessTools.PropertySetter(typeof(LynxCameraController), nameof(LynxCameraController.ScreenWidth));
        private static readonly System.Reflection.MethodInfo SetHeight = AccessTools.PropertySetter(typeof(LynxCameraController), nameof(LynxCameraController.ScreenHeight));
        private static readonly System.Reflection.MethodInfo SetCenter = AccessTools.PropertySetter(typeof(LynxCameraController), nameof(LynxCameraController.ScreenCenter));

        private static void Postfix()
        {
            var cam = LynxCameraController.MainCamera;
            if (cam == null) return;
            int width = cam.pixelWidth, height = cam.pixelHeight;
            if (LynxCameraController.ScreenWidth == width && LynxCameraController.ScreenHeight == height) return;
            SetWidth.Invoke(null, new object[] { width });
            SetHeight.Invoke(null, new object[] { height });
            SetCenter.Invoke(null, new object[] { new UnityEngine.Vector2(width * 0.5f, height * 0.5f) });
            Plugin.Log.LogInfo($"Screen size for tools: {width}x{height}");
        }
    }
}
