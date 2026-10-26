using System.Collections.Generic;
using System.Text;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipbreakerVR
{
    // With Debug/Diagnose on: load screens in VR, frame by frame, for what is too short to see in the headset (a view of the station flashing
    // up, a blink of black). While the rig hides the world, and for a few seconds after, it logs each frame in which the
    // picture changes: which cameras rendered (the rig's as "VR"), whether our pass drew the UI canvas, the eye texture
    // size, the load screen and which camera the rig follows. Frames that took long get a line of their own.
    internal static class VrLoadTrace
    {
        private const float Tail = 3f;
        private const float Hitch = 0.05f;
        private const int MaxLines = 400;

        private static readonly List<string> cameras = new List<string>();
        private static readonly StringBuilder text = new StringBuilder();
        private static bool tracing;
        private static float until = -1f;
        private static int frame = -1;
        private static bool uiDrawn;
        private static string last;
        private static float lastTime;
        private static int lines;

        // From VrCamera.LateUpdate: hidden is true while only the UI shows on black.
        public static void Track(bool hidden)
        {
            if (!Plugin.Diagnostics.Value)
            {
                tracing = false;
                return;
            }
            var now = Time.unscaledTime;
            if (hidden)
            {
                if (!tracing)
                {
                    tracing = true;
                    lines = 0;
                    last = null;
                    lastTime = now;
                    Plugin.Log.LogInfo($"Load trace started at frame {Time.frameCount}");
                }
                until = now + Tail;
            }
            else if (tracing && now > until)
            {
                tracing = false;
                Plugin.Log.LogInfo($"Load trace ended at frame {Time.frameCount}");
            }
        }

        public static void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!tracing) return;
            Begin();
            var vr = VrCamera.Instance;
            var name = vr != null && camera == vr.Camera
                ? "VR"
                : $"'{VrDiagnostics.Path(camera.transform)}' depth {camera.depth}{(camera.targetTexture != null ? $" into {camera.targetTexture.width}x{camera.targetTexture.height}" : "")}";
            if (!cameras.Contains(name)) cameras.Add(name);
        }

        public static void UiDrawn()
        {
            if (!tracing) return;
            Begin();
            uiDrawn = true;
        }

        private static void Begin()
        {
            if (frame == Time.frameCount) return;
            frame = Time.frameCount;
            cameras.Clear();
            uiDrawn = false;
        }

        // From VrCamera.Update, so the frame rendered last is complete.
        public static void EndFrame(VrCamera vr)
        {
            if (!tracing || lines >= MaxLines) return;
            var now = Time.unscaledTime;
            var gap = now - lastTime;
            lastTime = now;
            if (gap > Hitch) Log($"Load trace: {gap * 1000f:0} ms since the previous frame (frame {Time.frameCount})");
            if (frame != Time.frameCount - 1) cameras.Clear();
            text.Clear();
            text.Append("cameras ").Append(cameras.Count > 0 ? string.Join(", ", cameras) : "none");
            text.Append(uiDrawn && frame == Time.frameCount - 1 ? ", UI drawn" : ", UI not drawn");
            text.Append($", eye {UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight}");
            text.Append($", load screen {(SceneLoader.Instance != null && SceneLoader.Instance.LoadScreenVisible ? "on" : "off")}");
            text.Append($", world {(vr.WorldHidden ? "hidden" : "shown")}");
            text.Append($", follows {(vr.GameCamera != null ? $"'{VrDiagnostics.Path(vr.GameCamera.transform)}'" : "-")}");
            var line = text.ToString();
            if (line == last) return;
            last = line;
            Log($"Load trace frame {Time.frameCount - 1}: {line}");
        }

        private static void Log(string line)
        {
            lines++;
            Plugin.Log.LogInfo(lines < MaxLines ? line : line + " (trace full, no further lines)");
        }
    }
}
