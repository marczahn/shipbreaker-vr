using HarmonyLib;

namespace ShipbreakerVR
{
    // Mit aktivem XR setzt das Spiel ScreenWidth/ScreenHeight auf die Größe der Augentextur. Die Werkzeuge zielen aber über
    // die flache Spielkamera (MainCamera.ScreenPointToRay) mit ScreenCenter; deren Bildschirmmitte liegt woanders, und der
    // Cutter schießt dann nach oben. Wir nehmen deshalb immer die Pixelgröße der Spielkamera.
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
            Plugin.Log.LogInfo($"Bildschirmgröße für Werkzeuge: {width}x{height}");
        }
    }
}
