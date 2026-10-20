using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // Viele Menüs und Hinweise des Spiels sind "Screen Space - Overlay"-Canvases. Die zeichnet Unity am Ende des Frames
    // direkt ins Ausgabebild, in VR landen sie dadurch halb in einem Auge. Solange VR läuft, hängen wir sie an die
    // UI-Kamera des Spiels; dann landen sie mit dem HUD in unserer UI-Textur und auf der Leinwand im Raum.
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
            // Vor alle Canvases, die schon an der UI-Kamera hängen, aber noch innerhalb ihres Sichtbereichs.
            var distance = Mathf.Max(uiCamera.nearClipPlane * 1.5f, 0.02f);
            var added = 0;
            foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            {
                if (!canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay || converted.ContainsKey(canvas)) continue;
                converted[canvas] = new Original { Mode = canvas.renderMode, Camera = canvas.worldCamera, PlaneDistance = canvas.planeDistance, Layer = canvas.gameObject.layer };
                SetLayer(canvas.transform, layer);
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = uiCamera;
                canvas.planeDistance = distance;
                added++;
                if (!logged) Plugin.Log.LogInfo($"Overlay-Canvas an die UI-Kamera gehängt: '{canvas.name}' (Reihenfolge {canvas.sortingOrder})");
            }
            if (added > 0 && !logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"{converted.Count} Overlay-Canvases auf Ebene {layer}, Abstand {distance:0.000}");
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
