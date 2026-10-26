using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BBI.Unity.Game;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // F8, with Debug/Diagnose on: writes what is drawn around the player into the log, for things only seen in the headset. Directions are
    // relative to the head: "ahead 10, up 60" is 10 degrees to the right of the gaze's heading and 60 degrees above the
    // horizon. Also saves the UI texture (what the canvas in front shows) and the game window (the headset's mirror)
    // as PNG files next to the log. Also runs on its own a few seconds after the rig enters the Hab (Schedule), since
    // what shows up there right after waking up is gone by the time one thinks of pressing a key.
    internal static class VrDiagnostics
    {
        private const int MaxGraphicsPerCanvas = 6;
        private const int MaxTextsPerCanvas = 12;
        private const int MaxRenderers = 30;

        private static float dueAt = -1f;
        private static string dueReason;

        public static void Schedule(float delay, string reason)
        {
            dueAt = Time.unscaledTime + delay;
            dueReason = reason;
        }

        public static void Update(VrCamera vr)
        {
            if (Plugin.ToolExport.Value) VrToolExport.Update();
            if (!Plugin.Diagnostics.Value)
            {
                dueAt = -1f;
                return;
            }
            var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.F8) && !shift) Dump(vr, "F8");
            else if (dueAt >= 0f && Time.unscaledTime >= dueAt)
            {
                dueAt = -1f;
                Dump(vr, dueReason);
            }
        }

        public static void Dump(VrCamera vr, string reason)
        {
            var head = vr.transform;
            var text = new StringBuilder();
            text.AppendLine($"Dump ({reason}) at {Time.unscaledTime:0.0} s, game state {GameSession.CurrentGameState}, load screen {(SceneLoader.Instance != null && SceneLoader.Instance.LoadScreenVisible)}, VR {VrXr.IsRunning}, screen {Screen.width}x{Screen.height}, eye {UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight}");
            text.AppendLine($"  head at {head.position}, looking {Direction(head, head.position + head.forward)}, rig scale {head.parent.localScale.x:0.00}");
            var game = vr.GameCamera;
            if (game != null)
                text.AppendLine($"  following '{Path(game.transform)}' at {Direction(head, game.transform.position)} {Vector3.Distance(head.position, game.transform.position):0.00} m, its view {Direction(head, head.position + game.transform.forward)}, fov {game.fieldOfView:0}, pixels {game.pixelWidth}x{game.pixelHeight}, stereo {game.stereoEnabled}, mask {game.cullingMask}, far {game.farClipPlane:0}, Hab {vr.FollowsHab}, fade {VrHab.Fade:0.00}");
            var ui = VrUi.Instance;
            if (ui != null) text.AppendLine("  " + ui.Describe(head));
            text.AppendLine("  " + VrAim.Describe(head));

            text.AppendLine("  Canvases (active, enabled, root):");
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(c => c != null && c.gameObject.scene.IsValid() && c.isActiveAndEnabled && c.isRootCanvas)
                .OrderBy(c => c.renderMode).ThenBy(c => Path(c.transform));
            foreach (var canvas in canvases) DescribeCanvas(text, head, canvas, ui);

            text.AppendLine("  Renderers seen last frame within 4 m, above the horizon:");
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>()
                .Where(r => r.enabled && r.isVisible)
                .Select(r => (renderer: r, distance: Vector3.Distance(head.position, r.bounds.center)))
                .Where(e => e.distance < 4f && e.renderer.bounds.max.y > head.position.y)
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

            // Visible texts with where they sit on the canvas (0 0 lower left, 100 100 upper right; outside that range
            // they are off the canvas). Finds prompts that are missing in VR.
            var canvasRect = ((RectTransform)canvas.transform).rect;
            var texts = 0;
            var corners = new Vector3[4];
            foreach (var label in canvas.GetComponentsInChildren<TMP_Text>())
            {
                if (!label.enabled || label.canvasRenderer.cull || string.IsNullOrWhiteSpace(label.text)) continue;
                var alpha = label.color.a * label.canvasRenderer.GetAlpha() * GroupAlpha(label.transform);
                if (alpha < 0.05f) continue;
                if (++texts > MaxTextsPerCanvas)
                {
                    text.AppendLine("      ...");
                    break;
                }
                label.rectTransform.GetWorldCorners(corners);
                var center = canvas.transform.InverseTransformPoint((corners[0] + corners[2]) * 0.5f);
                var x = canvasRect.width > 0 ? (center.x - canvasRect.xMin) / canvasRect.width * 100f : 0f;
                var y = canvasRect.height > 0 ? (center.y - canvasRect.yMin) / canvasRect.height * 100f : 0f;
                var content = label.text.Replace("\n", " ");
                if (content.Length > 40) content = content.Substring(0, 40) + "...";
                text.AppendLine($"      text '{content}' ('{label.name}' under '{label.transform.parent?.parent?.name}'), at {x:0} {y:0}, alpha {alpha:0.00}");
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

        public static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
    }
}
