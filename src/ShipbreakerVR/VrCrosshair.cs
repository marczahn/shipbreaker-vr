using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // While a tool aims from the hand (VrAim.Aiming), the game's crosshair in the middle of the view marks where the head
    // looks, which that tool does not use: the center dot (CentreDotUIController), the grapple's reticle with its thrust
    // indicators and distance (GrappleCrosshairController), and the cutter's reticle, cut lines and edge points
    // (CuttingUIController), and the scanner's dot (ScannerUIController). A center dot was still left over, from none of
    // these as far as the log tells, so the HUD is also searched now and then for small graphics in the middle of a canvas
    // (Scan), which go too. They are made invisible through their CanvasRenderer's alpha, which the game never sets, so
    // its own switching and coloring go on untouched. With an empty hand or hand aiming off they come back: then
    // interacting aims at the middle of the view, and the dot marks it.
    internal static class VrCrosshair
    {
        static VrCrosshair() => VrSession.OnUnload("crosshair", Release);

        private static readonly AccessTools.FieldRef<GrappleCrosshairController, Image> GrappleSnap =
            AccessTools.FieldRefAccess<GrappleCrosshairController, Image>("m_GrappleSnapImage");
        private static readonly AccessTools.FieldRef<CuttingUIController, Image> CutterReticle =
            AccessTools.FieldRefAccess<CuttingUIController, Image>("m_AimedCenterReticleIcon");
        private static readonly AccessTools.FieldRef<CuttingUIController, Image> CutterCooldown =
            AccessTools.FieldRefAccess<CuttingUIController, Image>("m_CuttingCooldownUI");
        private static readonly AccessTools.FieldRef<CuttingUIController, Image> ScalpelReticle =
            AccessTools.FieldRefAccess<CuttingUIController, Image>("m_ScalpelReticle");
        private static readonly AccessTools.FieldRef<CuttingUIController, GameObject> EdgePoints =
            AccessTools.FieldRefAccess<CuttingUIController, GameObject>("m_EdgeDetectionPointContainer");
        private static readonly AccessTools.FieldRef<CuttingUIController, List<CutLineUIObject>> CutLines =
            AccessTools.FieldRefAccess<CuttingUIController, List<CutLineUIObject>>("mCutLines");
        private static readonly AccessTools.FieldRef<ScannerUIController, Image> ScannerDot =
            AccessTools.FieldRefAccess<ScannerUIController, Image>("m_CenterDot");

        // A graphic counts as being in the middle when its center is within this fraction of the canvas height of the
        // canvas's center and it is at most this big; the searches come after these delays (seconds), then stop.
        private const float MiddleOffset = 0.02f;
        private const float MiddleSize = 0.06f;
        private static readonly float[] ScanDelays = { 2f, 5f, 10f, 30f, 60f, 120f };

        private static GrappleCrosshairController grapple;
        private static CuttingUIController cutting;
        private static CentreDotUIController centre;
        private static ScannerUIController scanner;
        private static float nextSearch;
        private static float nextCollect;
        private static float nextScan;
        private static int scans;
        private static readonly List<Graphic> graphics = new List<Graphic>();
        private static readonly HashSet<Graphic> middle = new HashSet<Graphic>();
        private static readonly Vector3[] corners = new Vector3[4];
        private static readonly HashSet<Graphic> hidden = new HashSet<Graphic>();
        private static bool logged;

        // From VrCamera.LateUpdate: after the game's UI has updated, before the canvases are drawn.
        public static void LateUpdate()
        {
            if (!VrAim.Aiming)
            {
                Show();
                return;
            }
            Find();
            if (nextScan > 0f && Time.unscaledTime >= nextScan) Scan();
            // Cut lines and edge points are created as the cutter needs them.
            if (Time.unscaledTime >= nextCollect) Collect();
            foreach (var graphic in graphics)
            {
                if (graphic == null || graphic.canvasRenderer.GetAlpha() <= 0f) continue;
                graphic.canvasRenderer.SetAlpha(0f);
                hidden.Add(graphic);
            }
        }

        private static void Find()
        {
            if (grapple != null && cutting != null && centre != null && scanner != null) return;
            if (Time.unscaledTime < nextSearch) return;
            var hadHud = grapple != null || cutting != null;
            if (grapple == null) grapple = Object.FindObjectOfType<GrappleCrosshairController>();
            if (cutting == null) cutting = Object.FindObjectOfType<CuttingUIController>();
            if (centre == null) centre = Object.FindObjectOfType<CentreDotUIController>(true);
            if (scanner == null) scanner = Object.FindObjectOfType<ScannerUIController>(true);
            // Searching the scene costs a moment; once the HUD is there, a part it lacks is looked for only now and then.
            var hud = grapple != null || cutting != null;
            nextSearch = Time.unscaledTime + (hud ? 15f : 2f);
            nextCollect = 0f;
            // The search for graphics in the middle starts over with each new HUD.
            if (hud && !hadHud)
            {
                scans = 0;
                nextScan = Time.unscaledTime + ScanDelays[0];
            }
        }

        private static void Collect()
        {
            nextCollect = Time.unscaledTime + 1f;
            graphics.Clear();
            if (centre != null) graphics.AddRange(centre.GetComponentsInChildren<Graphic>(true));
            if (scanner != null) graphics.Add(ScannerDot(scanner));
            middle.RemoveWhere(g => g == null);
            graphics.AddRange(middle);
            if (grapple != null)
            {
                graphics.AddRange(new Graphic[]
                {
                    grapple.CrosshairIcon, grapple.CrosshairGrips, grapple.ReticuleDeniedImage, grapple.ReticuleCenterDot,
                    GrappleSnap(grapple), grapple.ReticuleRightThrustIndicator, grapple.ReticuleLeftThrustIndicator,
                    grapple.ReticuleUpThrustIndicator, grapple.ReticuleDownThrustIndicator, grapple.ReticuleForwardThrustIndicator,
                    grapple.ReticuleBackwardThrustIndicator, grapple.DistanceText, grapple.MetersText,
                });
            }
            if (cutting != null)
            {
                graphics.AddRange(new Graphic[] { CutterReticle(cutting), CutterCooldown(cutting), ScalpelReticle(cutting) });
                var points = EdgePoints(cutting);
                if (points != null) graphics.AddRange(points.GetComponentsInChildren<Graphic>(true));
                var lines = CutLines(cutting);
                if (lines != null)
                    foreach (var line in lines)
                        if (line != null) graphics.AddRange(line.GetComponentsInChildren<Graphic>(true));
            }
            graphics.RemoveAll(g => g == null);
            if (logged || graphics.Count == 0) return;
            logged = true;
            Plugin.Log.LogInfo($"Crosshair hidden while aiming with the hand: {graphics.Count} graphic(s): {string.Join(", ", graphics.Select(g => g.name).Distinct())}" +
                $" (center dot {(centre != null ? "found" : "missing")}, scanner dot {(scanner != null ? "found" : "missing")}, grapple reticle {(grapple != null ? "found" : "missing")}, cutter reticle {(cutting != null ? "found" : "missing")})");
        }

        // Small graphics in the middle of a HUD canvas that are not hidden yet, whatever draws them: there the head's
        // center is marked, which nothing aims at while the hand aims.
        private static void Scan()
        {
            scans++;
            nextScan = scans < ScanDelays.Length ? Time.unscaledTime + ScanDelays[scans] - ScanDelays[scans - 1] : 0f;
            var known = new HashSet<Graphic>(graphics);
            var found = new List<Graphic>();
            foreach (var graphic in Object.FindObjectsOfType<Graphic>())
            {
                if (!graphic.enabled || known.Contains(graphic) || middle.Contains(graphic) || graphic.color.a <= 0.01f
                    || graphic.canvasRenderer.GetAlpha() <= 0f || !InMiddle(graphic)) continue;
                middle.Add(graphic);
                found.Add(graphic);
            }
            Plugin.Log.LogInfo($"Crosshair: search {scans} for graphics in the middle of the view: " +
                (found.Count == 0 ? "none" : string.Join(", ", found.Select(g => $"'{VrUtil.Chain(g.transform, 6)}' ({g.GetType().Name})"))));
            if (found.Count > 0) nextCollect = 0f;
        }

        private static bool InMiddle(Graphic graphic)
        {
            var canvas = graphic.canvas;
            if (canvas == null) return false;
            var root = canvas.rootCanvas;
            if (root.renderMode == RenderMode.WorldSpace) return false;
            var rect = ((RectTransform)root.transform).rect;
            if (rect.height <= 0f) return false;
            graphic.rectTransform.GetWorldCorners(corners);
            var low = root.transform.InverseTransformPoint(corners[0]);
            var high = root.transform.InverseTransformPoint(corners[2]);
            var center = (Vector2)((low + high) * 0.5f) - rect.center;
            var size = high - low;
            var height = rect.height;
            return Mathf.Abs(center.x) <= MiddleOffset * height && Mathf.Abs(center.y) <= MiddleOffset * height
                && Mathf.Abs(size.x) <= MiddleSize * height && Mathf.Abs(size.y) <= MiddleSize * height;
        }

        // The crosshair back as the game has it.
        private static void Show()
        {
            if (hidden.Count == 0) return;
            foreach (var graphic in hidden)
                if (graphic != null) graphic.canvasRenderer.SetAlpha(1f);
            hidden.Clear();
        }

        // On unload (hot reload).
        public static void Release()
        {
            Show();
            graphics.Clear();
            middle.Clear();
            scans = 0;
            nextScan = 0f;
            grapple = null;
            cutting = null;
            centre = null;
            scanner = null;
        }
    }
}
