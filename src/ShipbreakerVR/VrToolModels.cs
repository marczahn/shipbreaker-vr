using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The tool models in the right hand while aiming with it (VrAim). The game hangs the equipped tool on the right hand's
    // equipment point (HandGrab.EquipmentGeoTransform, via GrabController.GetHandPositionTransformAndSetEquipment), a fixed
    // point in front of the head, and moves the game's hand to the tool's grip (m_EquipmentHandTargetTransforms). Right
    // before rendering, that point goes to the controller: turned like the aim pose, the tool keeps the angle to the hand
    // that it had to the eye, and shifted so that the tool's grip lies in the controller's grip. The cutter's beam and the
    // grapple's line start at the model, so they now leave from the hand too. With hand aiming off the point goes back.
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
        private static readonly HashSet<EquipmentController.Equipment> logged = new HashSet<EquipmentController.Equipment>();

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
            var head = LynxCameraController.MainCameraTransform;
            if (point == null || head == null || point.parent == null || !VrAim.TryGetHand(out var grip, out var aim))
            {
                Restore();
                return;
            }

            // Where the point sits without us, relative to the eye.
            var parent = point.parent;
            var restWorld = parent.TransformPoint(restPosition);
            var restWorldRotation = parent.rotation * restRotation;
            var angle = Quaternion.Inverse(head.rotation) * restWorldRotation;

            // The tool's grip relative to the point (the point at rest if the grip does not move with it).
            var equipment = hand.CurrentEquipmentType;
            var gripTarget = GripTarget(hand, equipment);
            var gripOffset = Vector3.zero;
            if (gripTarget != null)
            {
                gripOffset = gripTarget.IsChildOf(point)
                    ? point.InverseTransformPoint(gripTarget.position)
                    : Quaternion.Inverse(restWorldRotation) * (gripTarget.position - restWorld);
            }
            Log(hand, equipment, gripTarget, gripOffset);

            var rotation = aim * angle;
            point.rotation = rotation;
            point.position = grip;
            point.position = grip - (point.TransformPoint(gripOffset) - point.position);
            moved = true;
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
            if (!moved) return;
            moved = false;
            if (point == null) return;
            point.localPosition = restPosition;
            point.localRotation = restRotation;
        }

        // Once per tool: where the game keeps it, for judging the fit in the headset.
        private static void Log(HandGrab hand, EquipmentController.Equipment equipment, Transform gripTarget, Vector3 gripOffset)
        {
            if (equipment == EquipmentController.Equipment.None || !logged.Add(equipment)) return;
            var models = new List<string>();
            foreach (Transform child in point) models.Add($"'{child.name}'");
            Plugin.Log.LogInfo($"Tool in hand: {equipment}; equipment point {Chain(point)}, rest {restPosition} {restRotation.eulerAngles}; " +
                $"models on it: {string.Join(", ", models)}; grip {(gripTarget == null ? "none" : Chain(gripTarget))}, " +
                $"{(gripTarget != null && gripTarget.IsChildOf(point) ? "moves with the tool" : "stays")}, offset {gripOffset}; hand target {Chain(hand.HandPositionTransform)}");
        }

        private static string Chain(Transform transform)
        {
            var names = new List<string>();
            for (var t = transform; t != null && names.Count < 8; t = t.parent) names.Add(t.name);
            return string.Join(" < ", names);
        }

        // On unload (hot reload): the equipment point goes back to the game.
        public static void Release()
        {
            Restore();
            point = null;
            grab = null;
            logged.Clear();
        }
    }
}
