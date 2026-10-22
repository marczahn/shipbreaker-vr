using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BBI.Unity.Game;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // F8: writes what is drawn around the player into the log, for things only seen in the headset. Directions are
    // relative to the head: "ahead 10, up 60" is 10 degrees to the right of the gaze's heading and 60 degrees above the
    // horizon. Also saves the UI texture (what the canvas in front shows) and the game window (the headset's mirror)
    // as PNG files next to the log.
    internal static class VrDiagnostics
    {
        private const int MaxGraphicsPerCanvas = 6;
        private const int MaxRenderers = 20;

        public static void Dump(VrCamera vr)
        {
            var head = vr.transform;
            var text = new StringBuilder();
            text.AppendLine($"F8 dump at {Time.unscaledTime:0.0} s, game state {GameSession.CurrentGameState}, load screen {(SceneLoader.Instance != null && SceneLoader.Instance.LoadScreenVisible)}, VR {VrXr.IsRunning}");
            text.AppendLine($"  head at {head.position}, looking {Direction(head, head.position + head.forward)}, rig scale {head.parent.localScale.x:0.00}");
            var game = vr.GameCamera;
            if (game != null)
                text.AppendLine($"  following '{Path(game.transform)}' at {Direction(head, game.transform.position)} {Vector3.Distance(head.position, game.transform.position):0.00} m, its view {Direction(head, head.position + game.transform.forward)}, fov {game.fieldOfView:0}, mask {game.cullingMask}, Hab {vr.FollowsHab}, fade {VrHab.Fade:0.00}");
            var ui = VrUi.Instance;
            if (ui != null) text.AppendLine("  " + ui.Describe(head));

            text.AppendLine("  Canvases (active, enabled, root):");
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(c => c != null && c.gameObject.scene.IsValid() && c.isActiveAndEnabled && c.isRootCanvas)
                .OrderBy(c => c.renderMode).ThenBy(c => Path(c.transform));
            foreach (var canvas in canvases) DescribeCanvas(text, head, canvas, ui);

            text.AppendLine("  Renderers seen last frame within 3 m above the horizon:");
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>()
                .Where(r => r.enabled && r.isVisible)
                .Select(r => (renderer: r, distance: Vector3.Distance(head.position, r.bounds.center)))
                .Where(e => e.distance < 3f && e.renderer.bounds.center.y > head.position.y)
                .OrderBy(e => e.distance)
                .Take(MaxRenderers);
            foreach (var (renderer, distance) in renderers)
            {
                var material = renderer.sharedMaterial;
                text.AppendLine($"    '{Path(renderer.transform)}' {renderer.GetType().Name}, {Direction(head, renderer.bounds.center)} {distance:0.00} m, size {renderer.bounds.size}, layer {renderer.gameObject.layer}, material '{(material != null ? material.name : "-")}' ({(material != null && material.shader != null ? material.shader.name : "-")})");
            }

            Plugin.Log.LogInfo(text.ToString());
            SaveImages(ui);
        }

        private static void DescribeCanvas(StringBuilder text, Transform head, Canvas canvas, VrUi ui)
        {
            var camera = canvas.worldCamera;
            var line = $"    '{Path(canvas.transform)}' {canvas.renderMode}, camera '{(camera != null ? camera.name : "-")}'{(camera != null && !camera.enabled ? " (off)" : "")}, layer {canvas.gameObject.layer}, order {canvas.sortingOrder}, alpha {GroupAlpha(canvas.transform):0.00}";
            if (ui != null && ui.IsConverted(canvas)) line += ", on the VR canvas";
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                line += $", at {Direction(head, canvas.transform.position)} {Vector3.Distance(head.position, canvas.transform.position):0.00} m";
            text.AppendLine(line);

            var size = ((RectTransform)canvas.transform).rect.size;
            var shown = 0;
            foreach (var graphic in canvas.GetComponentsInChildren<Graphic>())
            {
                if (!graphic.enabled || graphic.canvasRenderer.cull) continue;
                var alpha = graphic.color.a * graphic.canvasRenderer.GetAlpha() * GroupAlpha(graphic.transform);
                if (alpha < 0.05f) continue;
                var c = graphic.color;
                var dark = Mathf.Max(c.r, Mathf.Max(c.g, c.b)) < 0.15f;
                var material = graphic.materialForRendering;
                var shader = material != null && material.shader != null ? material.shader.name : "-";
                var special = shader != "UI/Default" && !shader.StartsWith("TextMeshPro");
                var rect = graphic.rectTransform.rect.size;
                var scale = graphic.transform.lossyScale.x / Mathf.Max(1e-6f, canvas.transform.lossyScale.x);
                var share = new Vector2(size.x > 0 ? rect.x * scale / size.x : 0f, size.y > 0 ? rect.y * scale / size.y : 0f);
                var large = share.x > 0.25f && share.y > 0.25f;
                if (!dark && !special && !large) continue;
                if (++shown > MaxGraphicsPerCanvas)
                {
                    text.AppendLine("      ...");
                    break;
                }
                text.AppendLine($"      '{graphic.name}' {graphic.GetType().Name}, color {c.r:0.00}/{c.g:0.00}/{c.b:0.00}, alpha {alpha:0.00}, {share.x * 100f:0}% x {share.y * 100f:0}% of the canvas, shader {shader}");
            }
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

        // Heading relative to the head's gaze and elevation above the horizon, in degrees.
        public static string Direction(Transform head, Vector3 point)
        {
            var d = point - head.position;
            if (d.sqrMagnitude < 1e-8f) return "here";
            var flat = new Vector3(d.x, 0f, d.z);
            var gaze = new Vector3(head.forward.x, 0f, head.forward.z);
            var ahead = flat.sqrMagnitude > 1e-8f && gaze.sqrMagnitude > 1e-8f ? Vector3.SignedAngle(gaze, flat, Vector3.up) : 0f;
            var up = Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            return $"ahead {ahead:0}, up {up:0}";
        }

        private static void SaveImages(VrUi ui)
        {
            try
            {
                var folder = System.IO.Path.Combine(Paths.BepInExRootPath, "ShipbreakerVR-F8");
                Directory.CreateDirectory(folder);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var texture = ui != null ? ui.Texture : null;
                if (texture != null)
                {
                    var previous = RenderTexture.active;
                    RenderTexture.active = texture;
                    var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                    copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                    copy.Apply();
                    RenderTexture.active = previous;
                    var file = System.IO.Path.Combine(folder, $"{stamp}-ui.png");
                    File.WriteAllBytes(file, copy.EncodeToPNG());
                    UnityEngine.Object.Destroy(copy);
                    Plugin.Log.LogInfo($"UI texture saved: {file}");
                }
                var window = System.IO.Path.Combine(folder, $"{stamp}-window.png");
                ScreenCapture.CaptureScreenshot(window);
                Plugin.Log.LogInfo($"Game window saved: {window}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"F8 images not saved: {e.Message}");
            }
        }

        private static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
    }
}
