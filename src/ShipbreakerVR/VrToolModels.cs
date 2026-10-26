using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The tool models in the right hand while aiming with it (VrAim). The game hangs the equipped tool on the right hand's
    // equipment point (HandGrab.EquipmentGeoTransform, via GrabController.GetHandPositionTransformAndSetEquipment), a fixed
    // point on the player's body to the lower right of the view, and moves the game's hand to the tool's grip
    // (m_EquipmentHandTargetTransforms). Right before rendering, that point goes to the controller: turned like the aim
    // pose (the barrels run along the point's forward), and shifted so that the tool's grip lies in the controller's grip.
    // The tools aim from their muzzle (Muzzle), so beam, cut and grapple line leave from the barrel. With hand aiming off
    // the point goes back. Demo charges have no model on the point: the game's hand holds them, animated, so for them the
    // hand itself goes to the controller (HandTargetPatch).
    internal static class VrToolModels
    {
        private static readonly AccessTools.FieldRef<HandGrab, List<Transform>> HandTargets =
            AccessTools.FieldRefAccess<HandGrab, List<Transform>>("m_EquipmentHandTargetTransforms");

        private static GrabController grab;
        private static float nextSearch;
        private static Transform point;
        // The point's own pose under its parent, to put it back, and relative to the game camera, for the angle.
        private static Vector3 restPosition;
        private static Quaternion restRotation;
        private static bool moved;
        // This frame's tool: its grip and muzzle relative to the point, for Muzzle.
        private static Vector3 gripOffset;
        private static Vector3 muzzleOffset;
        private static bool hasMuzzle;
        private static CuttingToolController cutter;
        private static CuttingFXController splitsaw;
        private static ScalpelFXController stinger;
        private static GrapplingHook grapple;
        private static readonly AccessTools.FieldRef<CuttingFXController, Transform> SplitsawOrigin =
            AccessTools.FieldRefAccess<CuttingFXController, Transform>("m_CutOriginTransform");
        private static readonly AccessTools.FieldRef<CuttingFXController, Vector3> SplitsawOffset =
            AccessTools.FieldRefAccess<CuttingFXController, Vector3>("m_CutOriginOffset");
        private static readonly AccessTools.FieldRef<ScalpelFXController, Transform> StingerOrigin =
            AccessTools.FieldRefAccess<ScalpelFXController, Transform>("m_CutOriginTransform");
        private static readonly AccessTools.FieldRef<ScalpelFXController, Vector3> StingerOffset =
            AccessTools.FieldRefAccess<ScalpelFXController, Vector3>("m_CutOriginOffset");
        private static readonly AccessTools.FieldRef<GrapplingHook, Transform> GrappleBarrel =
            AccessTools.FieldRefAccess<GrapplingHook, Transform>("m_GrapplingGunBarrel");
        private static readonly HashSet<int> logged = new HashSet<int>();
        private static bool loggedCharge;
        private static int logCandidate;
        private static float logCandidateSince;

        // From VrCamera.FollowGameCamera, after the rig has moved.
        public static void Place()
        {
            var hand = RightHand();
            var target = hand != null ? hand.EquipmentGeoTransform : null;
            if (target != point)
            {
                Restore();
                point = target;
                if (point != null)
                {
                    restPosition = point.localPosition;
                    restRotation = point.localRotation;
                }
            }
            if (point == null || point.parent == null || !VrAim.TryGetHand(out var grip, out var aim))
            {
                Restore();
                return;
            }

            // The tool's grip and muzzle relative to the point, in the point's axes but world units (the point at rest if
            // the grip does not move with it).
            var equipment = hand.CurrentEquipmentType;
            var gripTarget = GripTarget(hand, equipment);
            var toPoint = Quaternion.Inverse(point.rotation);
            gripOffset = Vector3.zero;
            if (gripTarget != null)
            {
                gripOffset = gripTarget.IsChildOf(point)
                    ? toPoint * (gripTarget.position - point.position)
                    : Quaternion.Inverse(point.parent.rotation * restRotation) * (gripTarget.position - point.parent.TransformPoint(restPosition));
            }
            var muzzle = MuzzleTransform(equipment, out var muzzleWorld);
            hasMuzzle = muzzle != null && muzzle.IsChildOf(point);
            if (hasMuzzle) muzzleOffset = toPoint * (muzzleWorld - point.position);
            Log(hand, equipment, gripTarget, muzzle);

            point.rotation = aim;
            point.position = grip - aim * gripOffset;
            moved = true;
        }

        // Where the tool in the hand aims from, for a hand at grip pointing along rotation (VrAim, also before Place has
        // run in a frame).
        public static bool Muzzle(Vector3 grip, Quaternion rotation, out Vector3 position)
        {
            position = grip + rotation * (muzzleOffset - gripOffset);
            return moved && hasMuzzle && point != null;
        }

        // The barrel's end of the equipped tool, and the transform it moves with.
        private static Transform MuzzleTransform(EquipmentController.Equipment equipment, out Vector3 position)
        {
            position = Vector3.zero;
            if (equipment == EquipmentController.Equipment.GrappleHook)
            {
                if (grapple == null) grapple = Object.FindObjectOfType<GrapplingHook>();
                var barrel = grapple != null ? GrappleBarrel(grapple) : null;
                if (barrel != null) position = barrel.position;
                return barrel;
            }
            if (equipment != EquipmentController.Equipment.CuttingTool) return null;
            if (cutter == null) cutter = Object.FindObjectOfType<CuttingToolController>();
            if (cutter != null && cutter.CurrentMode == CuttingToolController.CutterMode.Scalpel)
            {
                if (stinger == null) stinger = Object.FindObjectOfType<ScalpelFXController>();
                var origin = stinger != null ? StingerOrigin(stinger) : null;
                if (origin != null) position = origin.TransformPoint(StingerOffset(stinger));
                return origin;
            }
            if (splitsaw == null) splitsaw = Object.FindObjectOfType<CuttingFXController>();
            var cut = splitsaw != null ? SplitsawOrigin(splitsaw) : null;
            if (cut != null) position = cut.TransformPoint(SplitsawOffset(splitsaw));
            return cut;
        }

        private static HandGrab RightHand()
        {
            if (grab == null)
            {
                if (Time.unscaledTime < nextSearch) return null;
                nextSearch = Time.unscaledTime + 1f;
                grab = Object.FindObjectOfType<GrabController>();
                if (grab == null) return null;
            }
            return grab.RightHand;
        }

        private static Transform GripTarget(HandGrab hand, EquipmentController.Equipment equipment)
        {
            var targets = HandTargets(hand);
            var index = (int)equipment;
            return targets != null && index > 0 && index < targets.Count ? targets[index] : null;
        }

        private static void Restore()
        {
            hasMuzzle = false;
            if (!moved) return;
            moved = false;
            if (point == null) return;
            point.localPosition = restPosition;
            point.localRotation = restRotation;
        }

        // Once per tool, a second after it was drawn (the model first slides in from its rest point): where the game keeps
        // it, for judging the fit in the headset.
        private static void Log(HandGrab hand, EquipmentController.Equipment equipment, Transform gripTarget, Transform muzzle)
        {
            var key = equipment == EquipmentController.Equipment.CuttingTool && cutter != null ? (int)equipment * 10 + (int)cutter.CurrentMode : (int)equipment;
            if (key != logCandidate)
            {
                logCandidate = key;
                logCandidateSince = Time.unscaledTime;
            }
            if (equipment == EquipmentController.Equipment.None || Time.unscaledTime - logCandidateSince < 1f || !logged.Add(key)) return;
            var models = new List<string>();
            foreach (Transform child in point) models.Add($"'{child.name}'");
            Plugin.Log.LogInfo($"Tool in hand: {equipment}; equipment point {Chain(point)}, rest {restPosition} {restRotation.eulerAngles}; " +
                $"models on it: {string.Join(", ", models)}; grip {(gripTarget == null ? "none" : Chain(gripTarget))}, " +
                $"{(gripTarget != null && gripTarget.IsChildOf(point) ? "moves with the tool" : "stays")}, offset {gripOffset} m, scale {point.lossyScale}; " +
                $"muzzle {(muzzle == null ? "none" : $"{Chain(muzzle)} at {muzzleOffset} m, {Vector3.Angle(muzzle.forward, point.forward):0}° off the point's forward")}; hand target {Chain(hand.HandPositionTransform)}");
        }

        private static string Chain(Transform transform)
        {
            var names = new List<string>();
            for (var t = transform; t != null && names.Count < 8; t = t.parent) names.Add(t.name);
            return string.Join(" < ", names);
        }

        // The game's right hand holding a demo charge: its IK target (HandGrab.m_HandTarget) goes to the controller's grip,
        // keeping the angle to the eye that the game gave it. Called after HandGrab.UpdateHandTarget, before the animation
        // reads the target.
        public static void HoldCharge(HandGrab hand, Transform target)
        {
            if (target == null || hand.Hand != HandGrab.PlayerHand.Right || hand.CurrentHandState != HandGrab.HandState.Equipped
                || hand.CurrentEquipmentType != EquipmentController.Equipment.DemoCharge) return;
            var head = LynxCameraController.MainCameraTransform;
            if (head == null || !VrAim.TryGetHand(out var grip, out var aim)) return;
            if (!loggedCharge)
            {
                loggedCharge = true;
                Plugin.Log.LogInfo($"Tool in hand: demo charge held by the game's hand {Chain(target)}, at {head.InverseTransformPoint(target.position)} from the eye");
            }
            target.SetPositionAndRotation(grip, aim * (Quaternion.Inverse(head.rotation) * target.rotation));
        }

        // On unload (hot reload): the equipment point goes back to the game.
        public static void Release()
        {
            Restore();
            point = null;
            grab = null;
            cutter = null;
            splitsaw = null;
            stinger = null;
            grapple = null;
            logged.Clear();
            loggedCharge = false;
        }
    }

    // See VrToolModels.HoldCharge.
    [HarmonyPatch]
    internal static class HandTargetPatch
    {
        private static readonly AccessTools.FieldRef<HandGrab, Transform> Target = AccessTools.FieldRefAccess<HandGrab, Transform>("m_HandTarget");

        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("BBI.Unity.Game.HandGrab"), "UpdateHandTarget");

        private static void Postfix(HandGrab __instance) => VrToolModels.HoldCharge(__instance, Target(__instance));
    }
}
