using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // The game's two hands follow the controllers while the tools aim from the hand (VrAim), as Marc asked: the player
    // model has both arms, and the game has a hand of its own for each (GrabController.LeftHand/RightHand, with their own
    // targets, grab animations and grab actions). The game pulls each arm to its hand's target with an IK component of
    // its own (BBI.IKLimb, in LateUpdate); right after that the arm is bent until the wrist sits at its controller, and
    // the hand is turned so that its knuckles point where the controller points, thumb side up.
    // A hand that is busy with something of the game's (grabbing, a gesture, damage) is left to the game, and so is the
    // right hand with a tool in it: the game's IK already puts it on the tool's grip, which sits in the controller.
    // With the tool in the left hand, the left hand holds it and the right one follows its controller empty. Demo charges
    // hang on the right hand's bone, so that arm is posed for them by VrToolModels.PoseArm, also with the charge in the
    // left hand; the left arm stays with the game then.
    internal static class VrHands
    {
        // How long an arm takes to move between the game's pose and the controller.
        private const float Blend = 0.2f;

        private static readonly InputFeatureUsage<Quaternion> PointerRotation = new InputFeatureUsage<Quaternion>("PointerRotation");

        private class Arm
        {
            public readonly XRNode Node;
            public readonly string Name;
            public InputDevice Device;
            public HandGrab Hand;
            public Transform Upper;
            public Transform Lower;
            public Transform Wrist;
            public Transform Index;
            public Transform Middle;
            public Transform Little;
            public BBI.IKLimb Limb;
            public float Weight;
            public bool Driven;
            public int PosedFrame = -1;

            public Arm(XRNode node, string name)
            {
                Node = node;
                Name = name;
            }
        }

        private static readonly Arm LeftArm = new Arm(XRNode.LeftHand, "left");
        private static readonly Arm RightArm = new Arm(XRNode.RightHand, "right");

        private static GrabController grab;
        private static float nextSearch;
        private static int preparedFrame = -1;
        private static int logs;
        private static EquipmentController.Equipment loggedEquipment = (EquipmentController.Equipment)(-1);

        // Called after each IKLimb.LateUpdate (IkLimbPatch).
        public static void AfterLimb(BBI.IKLimb limb)
        {
            if (!Prepare()) return;
            if (limb == LeftArm.Limb) Pose(LeftArm);
            else if (limb == RightArm.Limb) Pose(RightArm);
        }

        // From VrCamera.LateUpdate, after VrToolModels.PoseArm: the arms without an IK component of the game's.
        public static void LateUpdate()
        {
            if (!Prepare()) return;
            if (LeftArm.Limb == null || !LeftArm.Limb.isActiveAndEnabled) Pose(LeftArm);
            if (RightArm.Limb == null || !RightArm.Limb.isActiveAndEnabled) Pose(RightArm);
        }

        // Once per frame: which arms follow their controllers, blended in and out over Blend.
        private static bool Prepare()
        {
            if (!Find()) return false;
            if (preparedFrame == Time.frameCount) return true;
            preparedFrame = Time.frameCount;
            var active = VrAim.Active;
            var equipment = grab.RightHand.CurrentEquipmentType;
            var leftHolds = VrToolInput.LeftHoldsTool;
            Drive(LeftArm, active && Idle(grab.LeftHand) && !(leftHolds && equipment == EquipmentController.Equipment.DemoCharge));
            var rightBusy = VrToolModels.PosesRightArm;
            Drive(RightArm, active && !rightBusy && (Idle(grab.RightHand) || (grab.RightHand.CurrentHandState == HandGrab.HandState.Equipped
                && (equipment == EquipmentController.Equipment.Scanner || (leftHolds && equipment != EquipmentController.Equipment.DemoCharge)))));
            // The demo charge's arm is bent there; another bend on top would fight it.
            if (rightBusy) RightArm.Weight = 0f;
            LogLimbs(equipment);
            return true;
        }

        // Not grabbing, gesturing, hurt or holding a tool.
        private static bool Idle(HandGrab hand)
        {
            var state = hand.CurrentHandState;
            return state == HandGrab.HandState.None || state == HandGrab.HandState.Resting || state == HandGrab.HandState.Leading;
        }

        private static void Drive(Arm arm, bool driven)
        {
            arm.Weight = Mathf.MoveTowards(arm.Weight, driven ? 1f : 0f, Time.unscaledDeltaTime / Blend);
            if (driven == arm.Driven) return;
            arm.Driven = driven;
            if (logs++ < 40)
                Plugin.Log.LogInfo($"Hands: {arm.Name} arm {(driven ? "follows the controller" : "back to the game")} (hand {arm.Hand.CurrentHandState}, {grab.RightHand.CurrentEquipmentType} on the right hand)");
        }

        // After the animation and the game's IK, before skinning: the wrist to the controller, the hand along it.
        private static void Pose(Arm arm)
        {
            if (arm.PosedFrame == Time.frameCount) return;
            arm.PosedFrame = Time.frameCount;
            var weight = arm.Weight;
            if (weight <= 0f || arm.Wrist == null || arm.Upper == null || arm.Lower == null) return;
            if (!arm.Device.isValid) arm.Device = InputDevices.GetDeviceAtXRNode(arm.Node);
            if (!arm.Device.TryGetFeatureValue(CommonUsages.devicePosition, out var gripLocal)) return;
            if (!arm.Device.TryGetFeatureValue(PointerRotation, out var aimLocal)
                && !arm.Device.TryGetFeatureValue(CommonUsages.deviceRotation, out aimLocal)) return;
            if (!VrToolModels.TrackingToWorld(gripLocal + aimLocal * VrToolModels.WristBehindGrip, aimLocal, out var wrist, out var aim)) return;

            var animated = arm.Wrist.rotation;
            VrToolModels.Bend(arm.Upper, arm.Lower, arm.Wrist, Vector3.Lerp(arm.Wrist.position, wrist, weight));
            arm.Wrist.rotation = Quaternion.Slerp(animated, HandRotation(arm, aim, animated), weight);
        }

        // The hand's knuckles (wrist to the middle finger) along the controller's aim, the thumb side (little finger to
        // index finger) up, as when holding a Touch controller: the palm then faces inwards on either hand. Without
        // finger bones the hand keeps the angle to the eye that the animation gave it.
        private static Quaternion HandRotation(Arm arm, Quaternion aim, Quaternion animated)
        {
            if (arm.Index != null && arm.Middle != null && arm.Little != null)
            {
                var knuckles = arm.Middle.position - arm.Wrist.position;
                var thumbSide = arm.Index.position - arm.Little.position;
                if (knuckles.sqrMagnitude > 1e-8f && thumbSide.sqrMagnitude > 1e-8f)
                {
                    var current = Quaternion.LookRotation(knuckles, thumbSide);
                    var wanted = Quaternion.LookRotation(aim * Vector3.forward, aim * Vector3.up);
                    return wanted * Quaternion.Inverse(current) * arm.Wrist.rotation;
                }
            }
            var camera = VrCamera.Instance != null ? VrCamera.Instance.GameCamera : null;
            return camera == null ? animated : aim * (Quaternion.Inverse(camera.transform.rotation) * animated);
        }

        private static bool Find()
        {
            if (grab != null) return true;
            if (Time.unscaledTime < nextSearch) return false;
            nextSearch = Time.unscaledTime + 1f;
            grab = Object.FindObjectOfType<GrabController>();
            if (grab == null) return false;
            var animator = grab.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a != null && a.isHuman);
            var limbs = grab.transform.root.GetComponentsInChildren<BBI.IKLimb>(true);
            Setup(LeftArm, grab.LeftHand, animator, limbs, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftLittleProximal);
            Setup(RightArm, grab.RightHand, animator, limbs, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLittleProximal);

            foreach (var limb in limbs)
                Plugin.Log.LogInfo($"Hands: IKLimb on '{limb.name}': {Name(limb.UpperArm)} > {Name(limb.Forearm)} > {Name(limb.Hand)}, target {Chain(limb.Target)}, " +
                    $"elbow {Name(limb.ElbowTarget)}, {(limb.enabled ? "" : "component off, ")}{(limb.IsEnabled ? "on" : "off")}, transition {limb.Transition}, hand {limb.HandRotationPolicy}");
            if (limbs.Length == 0) Plugin.Log.LogInfo("Hands: no IKLimb on the player");
            foreach (var arm in new[] { LeftArm, RightArm })
                Plugin.Log.LogInfo($"Hands: {arm.Name} arm {Name(arm.Upper)} > {Name(arm.Lower)} > {Name(arm.Wrist)}, fingers {(arm.Index != null && arm.Middle != null && arm.Little != null ? "found" : "missing")}, " +
                    $"IK {(arm.Limb != null ? $"'{arm.Limb.name}'" : "none")}; game hand target {Chain(arm.Hand.HandPositionTransform)}, equipment point {Chain(arm.Hand.EquipmentGeoTransform)}, state {arm.Hand.CurrentHandState}");
            if (animator != null)
                Plugin.Log.LogInfo($"Hands: animator '{animator.name}' parameters: {string.Join(", ", animator.parameters.Select(p => p.name))}");
            return true;
        }

        private static void Setup(Arm arm, HandGrab hand, Animator animator, BBI.IKLimb[] limbs,
            HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones wrist, HumanBodyBones index, HumanBodyBones middle, HumanBodyBones little)
        {
            arm.Hand = hand;
            arm.Upper = animator != null ? animator.GetBoneTransform(upper) : null;
            arm.Lower = animator != null ? animator.GetBoneTransform(lower) : null;
            arm.Wrist = animator != null ? animator.GetBoneTransform(wrist) : null;
            arm.Index = animator != null ? animator.GetBoneTransform(index) : null;
            arm.Middle = animator != null ? animator.GetBoneTransform(middle) : null;
            arm.Little = animator != null ? animator.GetBoneTransform(little) : null;
            arm.Limb = limbs.FirstOrDefault(l => l != null && arm.Wrist != null && l.Hand == arm.Wrist)
                ?? limbs.FirstOrDefault(l => l != null && hand.HandPositionTransform != null && l.Target == hand.HandPositionTransform);
            if (arm.Limb != null && arm.Wrist == null)
            {
                arm.Upper = arm.Limb.UpperArm;
                arm.Lower = arm.Limb.Forearm;
                arm.Wrist = arm.Limb.Hand;
            }
            arm.Weight = 0f;
            arm.Driven = false;
        }

        // Whenever the right hand's tool changes, a few times: whether the game's IK pulls the arms, for the demo charges.
        private static void LogLimbs(EquipmentController.Equipment equipment)
        {
            if (equipment == loggedEquipment || logs >= 40) return;
            loggedEquipment = equipment;
            logs++;
            Plugin.Log.LogInfo($"Hands: with {equipment} the game's arm IK is left {Limb(LeftArm.Limb)}, right {Limb(RightArm.Limb)}");
        }

        private static string Limb(BBI.IKLimb limb) =>
            limb == null ? "none" : $"{(limb.IsEnabled && limb.enabled ? "on" : "off")} at transition {limb.Transition:0.00}";

        private static string Name(Transform transform) => transform == null ? "none" : $"'{transform.name}'";

        private static string Chain(Transform transform)
        {
            if (transform == null) return "none";
            var names = new List<string>();
            for (var t = transform; t != null && names.Count < 6; t = t.parent) names.Add(t.name);
            return string.Join(" < ", names);
        }

        // On unload (hot reload).
        public static void Release()
        {
            grab = null;
            LeftArm.Limb = RightArm.Limb = null;
            LeftArm.Weight = RightArm.Weight = 0f;
            LeftArm.Driven = RightArm.Driven = false;
            loggedEquipment = (EquipmentController.Equipment)(-1);
        }
    }

    // Bends the arms after the game's own arm IK (VrHands): BBI.IKLimb solves in LateUpdate, and anything posed before it
    // would be overwritten.
    [HarmonyPatch]
    internal static class IkLimbPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("BBI.IKLimb"), "LateUpdate");

        private static void Postfix(BBI.IKLimb __instance) => VrHands.AfterLimb(__instance);
    }
}
