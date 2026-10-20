using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // Many of the game's menus and prompts are "Screen Space - Overlay" canvases. Unity draws them at the end of the frame
    // straight into the output image, so in VR they end up half in one eye. While VR is running we attach them to the
    // game's UI camera; they then end up with the HUD in our UI texture and on the canvas in the world.
    internal class VrCanvases
    {
        private struct Original
        {
            public RenderMode Mode;
            public Camera Camera;
            public float PlaneDistance;
            public int Layer;
        }

        private readonly Dictionary<Canvas, Original> converted = new Dictionary<Canvas, Original>();
        private float nextScan;
        private bool logged;

        public void Update(Camera uiCamera)
        {
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.5f;

            var layer = LowestLayer(uiCamera.cullingMask);
            // In front of all canvases already on the UI camera, but still within its view range.
            var distance = Mathf.Max(uiCamera.nearClipPlane * 1.5f, 0.02f);
            var added = 0;
            foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            {
                if (!canvas.isRootCanvas || converted.ContainsKey(canvas)) continue;
                // Overlay canvases and those attached to another camera (e.g. in the main menu).
                var foreign = canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera != uiCamera;
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay && !foreign) continue;
                converted[canvas] = new Original { Mode = canvas.renderMode, Camera = canvas.worldCamera, PlaneDistance = canvas.planeDistance, Layer = canvas.gameObject.layer };
                SetLayer(canvas.transform, layer);
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = uiCamera;
                canvas.planeDistance = distance;
                added++;
                if (!logged) Plugin.Log.LogInfo($"Canvas attached to UI camera: '{canvas.name}' ({converted[canvas].Mode}, camera {(converted[canvas].Camera != null ? converted[canvas].Camera.name : "-")}, order {canvas.sortingOrder})");
            }
            if (added > 0 && !logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"{converted.Count} overlay canvases on layer {layer}, distance {distance:0.000}");
            }
        }

        public void Restore()
        {
            if (converted.Count == 0) return;
            foreach (var entry in converted)
            {
                var canvas = entry.Key;
                if (canvas == null) continue;
                canvas.renderMode = entry.Value.Mode;
                canvas.worldCamera = entry.Value.Camera;
                canvas.planeDistance = entry.Value.PlaneDistance;
                SetLayer(canvas.transform, entry.Value.Layer);
            }
            converted.Clear();
        }

        private static int LowestLayer(int mask)
        {
            for (var i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) return i;
            return 5;
        }

        private static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (var i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }
    }
}
