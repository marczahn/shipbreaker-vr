using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ShipbreakerVR
{
    // While the tools aim from the hand (VrAim), the game's crosshair in the middle of the view marks where the head
    // looks, which no tool uses any more: the center dot (CentreDotUIController), the grapple's reticle with its thrust
    // indicators and distance (GrappleCrosshairController), and the cutter's reticle, cut lines and edge points
    // (CuttingUIController). They are made invisible through their CanvasRenderer's alpha, which the game never sets, so
    // its own switching and coloring go on untouched; with hand aiming off they come back. The color the game gives the
    // reticle for what the tool aims at (in range, out of range, cooling down) goes to the laser instead (TryGetColor).
    internal static class VrCrosshair
    {
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
        private static readonly AccessTools.FieldRef<CuttingUIController, CuttingController> Cutter =
            AccessTools.FieldRefAccess<CuttingUIController, CuttingController>("m_CuttingController");
        private static readonly AccessTools.FieldRef<CuttingUIController, ScalpelController> Scalpel =
            AccessTools.FieldRefAccess<CuttingUIController, ScalpelController>("m_ScalpelController");

        private static GrappleCrosshairController grapple;
        private static CuttingUIController cutting;
        private static CentreDotUIController centre;
        private static EquipmentController equipment;
        private static float nextSearch;
        private static float nextCollect;
        private static readonly List<Graphic> graphics = new List<Graphic>();
        private static readonly HashSet<Graphic> hidden = new HashSet<Graphic>();
        private static bool logged;
        private static readonly HashSet<string> loggedColors = new HashSet<string>();

        // From VrCamera.LateUpdate: after the game's UI has updated, before the canvases are drawn.
        public static void LateUpdate()
        {
            if (!VrAim.Active)
            {
                Show();
                return;
            }
            Find();
            // Cut lines and edge points are created as the cutter needs them.
            if (Time.unscaledTime >= nextCollect) Collect();
            foreach (var graphic in graphics)
            {
                if (graphic == null || graphic.canvasRenderer.GetAlpha() <= 0f) continue;
                graphic.canvasRenderer.SetAlpha(0f);
                hidden.Add(graphic);
            }
        }

        // The color of the game's reticle for the equipped tool, for the laser: false without a tool that has one.
        public static bool TryGetColor(out Color color)
        {
            color = Color.white;
            if (equipment == null) return false;
            string what;
            switch (equipment.CurrentEquipment)
            {
                case EquipmentController.Equipment.GrappleHook:
                    if (grapple == null || grapple.CrosshairIcon == null) return false;
                    color = grapple.CrosshairIcon.color;
                    what = "grapple";
                    break;
                case EquipmentController.Equipment.CuttingTool:
                    if (cutting == null) return false;
                    var scalpel = Scalpel(cutting);
                    var reticle = scalpel != null && scalpel.IsCurrentEquipment ? ScalpelReticle(cutting) : CutterReticle(cutting);
                    if (reticle == null) return false;
                    color = reticle.color;
                    what = scalpel != null && scalpel.IsCurrentEquipment ? "scalpel" : "cutter";
                    break;
                default:
                    return false;
            }
            // Each color the reticles take, once, for judging them on the laser.
            if (loggedColors.Count < 20 && loggedColors.Add($"{what} {ColorUtility.ToHtmlStringRGB(color)}"))
                Plugin.Log.LogInfo($"Laser: {what} reticle color #{ColorUtility.ToHtmlStringRGBA(color)}");
            return true;
        }

        private static void Find()
        {
            if (grapple != null && cutting != null && centre != null && equipment != null) return;
            if (Time.unscaledTime < nextSearch) return;
            if (grapple == null) grapple = Object.FindObjectOfType<GrappleCrosshairController>();
            if (cutting == null) cutting = Object.FindObjectOfType<CuttingUIController>();
            if (centre == null) centre = Object.FindObjectOfType<CentreDotUIController>(true);
            if (equipment == null) equipment = Object.FindObjectOfType<EquipmentController>();
            // Searching the scene costs a moment; once the HUD is there, a part it lacks is looked for only now and then.
            nextSearch = Time.unscaledTime + (grapple != null || cutting != null ? 15f : 2f);
            nextCollect = 0f;
        }

        private static void Collect()
        {
            nextCollect = Time.unscaledTime + 1f;
            graphics.Clear();
            if (centre != null) graphics.AddRange(centre.GetComponentsInChildren<Graphic>(true));
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
                $" (center dot {(centre != null ? "found" : "missing")}, grapple reticle {(grapple != null ? "found" : "missing")}, cutter reticle {(cutting != null ? "found" : "missing")})");
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
            grapple = null;
            cutting = null;
            centre = null;
            equipment = null;
        }
    }

    // Where the grapple has no target of its own, its reticle judges what is aimed at (hookable, out of range) with a ray
    // from the player's camera, a field of its own (GrappleCrosshairController.PlayerCamera) that AimPatch does not
    // reach. With hand aiming that ray leaves from the hand, so the laser's color matches what the hand points at.
    [HarmonyPatch]
    internal static class GrappleCrosshairPatch
    {
        private static readonly MethodInfo Instead = AccessTools.Method(typeof(VrAim), nameof(VrAim.Instead));

        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("GrappleCrosshairController"), "Update");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(AccessTools.TypeByName("GrappleCrosshairController"), "PlayerCamera");
            var list = instructions.ToList();
            var replaced = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (!list[i].LoadsField(field)) continue;
                list.Insert(++i, new CodeInstruction(OpCodes.Call, Instead));
                replaced++;
            }
            Plugin.Log.LogInfo($"Aim: GrappleCrosshairController.Update: {replaced} player camera read(s) redirected to the hand");
            return list;
        }
    }
}
