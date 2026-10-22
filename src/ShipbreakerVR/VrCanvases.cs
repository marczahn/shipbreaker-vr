using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Many of the game's menus and prompts are "Screen Space - Overlay" canvases. Unity draws them at the end of the frame
    // straight into the output image, so in VR they end up half in one eye. While VR is running we attach them to the
    // game's UI camera; they then end up with the HUD in our UI texture and on the canvas in the world.
    internal class VrCanvases
    {
        // The game's full-screen fades to black (loading into the Hab, waking up, going to sleep, leaving the Hab). On a
        // menu canvas fixed in the room they were a black rectangle with the room around it, as if a piece of the world
        // were missing. So there they go on a layer no camera draws, and VrUi darkens the whole view by FadeAlpha
        // instead. On the visor in gameplay they cover the view as meant (FadeScreen also flashes it when cutting).
        private static readonly HashSet<string> FadeNames = new HashSet<string> { "FadeScreen", "HAB_General_Fade", "WakeUpFade", "QuitFromHabFade" };
        private const int HiddenLayer = VrUi.PassLayer;

        private struct Original
        {
            public RenderMode Mode;
            public Camera Camera;
            public float PlaneDistance;
            public int Layer;
        }

        private readonly Dictionary<Canvas, Original> converted = new Dictionary<Canvas, Original>();
        private readonly List<Canvas> reverted = new List<Canvas>();
        private readonly List<UnityEngine.UI.Graphic> graphics = new List<UnityEngine.UI.Graphic>();
        private float nextScan;
        private bool logged;
        private Camera convertedFor;
        private int uiLayer = 5;
        private bool hideFades;

        // Whether the fades stay out of the UI texture (see FadeNames).
        public bool HideFades
        {
            set
            {
                if (value == hideFades) return;
                hideFades = value;
                var count = 0;
                foreach (var canvas in converted.Keys)
                    if (canvas != null && IsFade(canvas))
                    {
                        SetLayer(canvas.transform, LayerFor(canvas));
                        count++;
                    }
                if (count > 0) Plugin.Log.LogInfo($"Screen fades {(value ? "hidden on the menu canvas" : "shown on the visor")} ({count})");
            }
        }

        public void Update(Camera uiCamera)
        {
            // New UI camera (scene change, loading screen): the canvases still point to the old, possibly destroyed one.
            if (uiCamera != convertedFor)
            {
                Restore();
                convertedFor = uiCamera;
                nextScan = 0f;
            }
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.5f;

            var layer = uiLayer = LowestLayer(uiCamera.cullingMask);
            // Inactive ones too: a canvas the game switches on later (the Hab's fades) would otherwise show for up to
            // half a second where the game put it, which for one on the Hab camera is in the room right in front of
            // the VR camera.
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
            // Slightly in front of the canvases already on the UI camera. Not too close: TextMeshPro text turns into
            // solid blocks when a canvas ends up extremely small (planeDistance of a few centimetres).
            var nearest = float.MaxValue;
            foreach (var canvas in canvases)
                if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera == uiCamera && !converted.ContainsKey(canvas) && InScene(canvas) && IsRoot(canvas))
                    nearest = Mathf.Min(nearest, canvas.planeDistance);
            var distance = nearest < float.MaxValue ? nearest * 0.9f : 1f;
            distance = Mathf.Clamp(distance, Mathf.Max(uiCamera.nearClipPlane * 1.5f, 0.3f), uiCamera.farClipPlane * 0.5f);
            // A canvas the game put back on its own camera would hang in the world for the VR camera to see (e.g. a
            // fade 25 cm in front of the Hab camera). Attach it again, and give it back what the game set last.
            reverted.Clear();
            foreach (var entry in converted)
                if (entry.Key != null && IsRoot(entry.Key) && (entry.Key.renderMode != RenderMode.ScreenSpaceCamera || entry.Key.worldCamera != uiCamera))
                    reverted.Add(entry.Key);
            foreach (var canvas in reverted)
            {
                Plugin.Log.LogWarning($"Canvas '{canvas.name}' left the UI camera ({canvas.renderMode}, camera {(canvas.worldCamera != null ? canvas.worldCamera.name : "-")}, layer {canvas.gameObject.layer}), attached again");
                var original = converted[canvas];
                original.Mode = canvas.renderMode;
                original.Camera = canvas.worldCamera;
                original.PlaneDistance = canvas.planeDistance;
                converted[canvas] = original;
                SetLayer(canvas.transform, LayerFor(canvas));
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = uiCamera;
                canvas.planeDistance = distance;
            }
            var added = 0;
            foreach (var canvas in canvases)
            {
                if (converted.ContainsKey(canvas) || !InScene(canvas) || !IsRoot(canvas)) continue;
                // Overlay canvases and those attached to another camera (e.g. in the main menu).
                var foreign = canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera != uiCamera;
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay && !foreign) continue;
                converted[canvas] = new Original { Mode = canvas.renderMode, Camera = canvas.worldCamera, PlaneDistance = canvas.planeDistance, Layer = canvas.gameObject.layer };
                SetLayer(canvas.transform, LayerFor(canvas));
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = uiCamera;
                canvas.planeDistance = distance;
                added++;
                if (!logged) Plugin.Log.LogInfo($"Canvas attached to UI camera: '{canvas.name}' ({converted[canvas].Mode}, camera {(converted[canvas].Camera != null ? converted[canvas].Camera.name : "-")}, distance {converted[canvas].PlaneDistance:0.###}, order {canvas.sortingOrder}, shaders {Shaders(canvas)})");
            }
            if (added > 0 && !logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"{converted.Count} overlay canvases on layer {layer}, distance {distance:0.000}");
            }
        }

        public bool IsConverted(Canvas canvas) => converted.ContainsKey(canvas);

        // How dark the hidden fades would make the screen, 0 to 1.
        public float FadeAlpha()
        {
            if (!hideFades) return 0f;
            var alpha = 0f;
            foreach (var canvas in converted.Keys)
            {
                if (canvas == null || !canvas.isActiveAndEnabled || !IsFade(canvas)) continue;
                canvas.GetComponentsInChildren(graphics);
                foreach (var graphic in graphics)
                {
                    if (!graphic.enabled) continue;
                    var c = graphic.color;
                    var darkness = 1f - Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    alpha = Mathf.Max(alpha, c.a * darkness * graphic.canvasRenderer.GetAlpha() * GroupAlpha(graphic.transform));
                }
            }
            return Mathf.Clamp01(alpha);
        }

        private static float GroupAlpha(Transform t)
        {
            var alpha = 1f;
            for (; t != null; t = t.parent)
            {
                var group = t.GetComponent<CanvasGroup>();
                if (group == null || !group.enabled) continue;
                alpha *= group.alpha;
                if (group.ignoreParentGroups) break;
            }
            return alpha;
        }

        private static bool IsFade(Canvas canvas) => FadeNames.Contains(canvas.name);

        private int LayerFor(Canvas canvas) => hideFades && IsFade(canvas) ? HiddenLayer : uiLayer;

        // Scene objects only; FindObjectsOfTypeAll also returns prefabs.
        private static bool InScene(Canvas canvas) => canvas.gameObject.scene.IsValid();

        // isRootCanvas is only kept up to date for active canvases.
        private static bool IsRoot(Canvas canvas) =>
            canvas.isActiveAndEnabled ? canvas.isRootCanvas : canvas.GetComponentsInParent<Canvas>(true).Length == 1;

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

        private static string Shaders(Canvas canvas)
        {
            var names = new HashSet<string>();
            foreach (var graphic in canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                if (graphic.material != null && graphic.material.shader != null) names.Add(graphic.material.shader.name);
            return string.Join(", ", names);
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

    // UITrackTransform keeps a prompt on the screen point of a spot in the world by setting the prompt's world position
    // to that screen point. That only works on an overlay canvas, where a unit is a pixel. On a canvas we attached to
    // the UI camera the prompt landed far off the canvas: the prompt to answer a call at the Hab's terminal never showed
    // in VR. There the screen point becomes the matching point on the canvas.
    [HarmonyPatch(typeof(UITrackTransform), "UpdateLocation")]
    internal static class UITrackTransformPatch
    {
        private static void Postfix(RectTransform ___mTargetTransform)
        {
            var ui = VrUi.Instance;
            if (ui == null || !ui.Active || ___mTargetTransform == null) return;
            var canvas = ___mTargetTransform.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            canvas = canvas.rootCanvas;
            if (!ui.IsConverted(canvas) || canvas.worldCamera == null) return;
            if (RectTransformUtility.ScreenPointToWorldPointInRectangle((RectTransform)canvas.transform, ___mTargetTransform.position, canvas.worldCamera, out var point))
                ___mTargetTransform.position = point;
        }
    }
}
