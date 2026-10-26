using System.Linq;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // The game's two hands follow the controllers while the tools aim from the hand (VrAim), as Marc asked: the player
    // model has both arms, and the game has a hand of its own for each (GrabController.LeftHand/RightHand, with their own
    // targets, grab animations and grab actions). The arms come from the animation alone (the game has an IK component,
    // BBI.IKLimb, but not on the player). After the animation, before skinning (VrCamera.LateUpdate), each arm is bent
    // until the wrist sits at its controller, and the hand is turned so that its knuckles point where the controller
    // points, thumb side up. The tools sit in the controller (VrToolModels), so the left hand holding one holds it there.
    // A hand that is busy with something of the game's (grabbing, a gesture, damage) is left to the game, and so is the
    // right hand with a tool in it; with the tool in the left hand the right one follows its controller empty. Demo charges
    // hang on the right hand's bone, so that arm is posed for them by VrToolModels.PoseArm, also with the charge in the
    // left hand; the left arm stays with the game then.
    // The fingers come from the animation too: a hand that follows its controller closes while its grip is held (around
    // the tool in the left hand, or into a fist), through the animator's own grab pose (Fingers).
    internal static class VrHands
    {
        // How long an arm takes to move between the game's pose and the controller.
        private const float Blend = 0.2f;
        private const float GripPress = 0.5f;
        private const float GripRelease = 0.4f;

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
            public float Weight;
            public bool Driven;
            public bool Gripping;
            // The animator's grab parameters of this hand, as HandGrab sets them.
            public readonly string Prep;
            public readonly string Grab;
            public readonly string Cancel;

            public Arm(XRNode node, string name, string side)
            {
                Node = node;
                Name = name;
                Prep = side + "PrepGrab";
                Grab = side + "Grab";
                Cancel = side + "CancelGrab";
            }
        }

        private static readonly Arm LeftArm = new Arm(XRNode.LeftHand, "left", "L");
        private static readonly Arm RightArm = new Arm(XRNode.RightHand, "right", "R");

        private static GrabController grab;
        private static Animator animator;
        private static int fingerLogs;
        private static float nextSearch;
        private static int logs;

        // From VrCamera.LateUpdate, after VrToolModels.PoseArm: which arms follow their controllers, blended in and out
        // over Blend, and those posed.
        public static void LateUpdate()
        {
            if (!Find()) return;
            var leftWasDriven = LeftArm.Driven;
            var rightWasDriven = RightArm.Driven;
            var active = VrAim.Active;
            var equipment = grab.RightHand.CurrentEquipmentType;
            var leftHolds = VrToolInput.LeftHoldsTool;
            Drive(LeftArm, active && Idle(grab.LeftHand) && !(leftHolds && equipment == EquipmentController.Equipment.DemoCharge));
            var rightBusy = VrToolModels.PosesRightArm;
            // The right hand with a tool in it stays with the game, as before.
            Drive(RightArm, active && !rightBusy && (Idle(grab.RightHand) || (grab.RightHand.CurrentHandState == HandGrab.HandState.Equipped
                && (equipment == EquipmentController.Equipment.Scanner || (leftHolds && equipment != EquipmentController.Equipment.DemoCharge)))));
            // The demo charge's arm is bent there; another bend on top would fight it.
            if (rightBusy) RightArm.Weight = 0f;
            Fingers(LeftArm, leftWasDriven);
            Fingers(RightArm, rightWasDriven);
            Pose(LeftArm);
            Pose(RightArm);
        }

        // Closes the hand while its grip is held, as long as it follows its controller: the same parameters HandGrab sets
        // for grabbing and for holding a tool (PrepGrab trigger and Grab on; Grab off and CancelGrab trigger). Handed back
        // to the game, the hand gets the pose the game had set for it: closed on a tool or a grab, open otherwise. The game
        // sets these only when its hand changes state, so a hand taken over keeps what is set here until then.
        private static void Fingers(Arm arm, bool wasDriven)
        {
            bool closed;
            if (arm.Driven)
            {
                if (!arm.Device.isValid) arm.Device = InputDevices.GetDeviceAtXRNode(arm.Node);
                arm.Device.TryGetFeatureValue(CommonUsages.grip, out var grip);
                arm.Gripping = grip >= (arm.Gripping ? GripRelease : GripPress);
                closed = arm.Gripping;
            }
            else if (wasDriven)
            {
                var state = arm.Hand.CurrentHandState;
                closed = state == HandGrab.HandState.Success
                    || (state == HandGrab.HandState.Equipped && arm.Hand.CurrentEquipmentType != EquipmentController.Equipment.DemoCharge);
            }
            else return;
            if (animator == null || animator.GetBool(arm.Grab) == closed) return;
            animator.ResetTrigger(closed ? arm.Cancel : arm.Prep);
            animator.SetTrigger(closed ? arm.Prep : arm.Cancel);
            animator.SetBool(arm.Grab, closed);
            if (fingerLogs++ < 20) Plugin.Log.LogInfo($"Hands: {arm.Name} hand {(closed ? "closes" : "opens")}{(arm.Driven ? "" : " (back to the game)")}");
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

        // The wrist to the controller, the hand along it.
        private static void Pose(Arm arm)
        {
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
            animator = grab.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a != null && a.isHuman);
            Setup(LeftArm, grab.LeftHand, animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftLittleProximal);
            Setup(RightArm, grab.RightHand, animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLittleProximal);
            foreach (var arm in new[] { LeftArm, RightArm })
                Plugin.Log.LogInfo($"Hands: {arm.Name} arm {Name(arm.Upper)} > {Name(arm.Lower)} > {Name(arm.Wrist)}, fingers {(arm.Index != null && arm.Middle != null && arm.Little != null ? "found" : "missing")}");
            return true;
        }

        private static void Setup(Arm arm, HandGrab hand, Animator animator,
            HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones wrist, HumanBodyBones index, HumanBodyBones middle, HumanBodyBones little)
        {
            arm.Hand = hand;
            arm.Upper = animator != null ? animator.GetBoneTransform(upper) : null;
            arm.Lower = animator != null ? animator.GetBoneTransform(lower) : null;
            arm.Wrist = animator != null ? animator.GetBoneTransform(wrist) : null;
            arm.Index = animator != null ? animator.GetBoneTransform(index) : null;
            arm.Middle = animator != null ? animator.GetBoneTransform(middle) : null;
            arm.Little = animator != null ? animator.GetBoneTransform(little) : null;
            arm.Weight = 0f;
            arm.Driven = false;
        }

        private static string Name(Transform transform) => transform == null ? "none" : $"'{transform.name}'";

        // On unload (hot reload).
        public static void Release()
        {
            foreach (var arm in new[] { LeftArm, RightArm })
                if (animator != null && arm.Driven && arm.Hand != null && Idle(arm.Hand)) animator.SetBool(arm.Grab, false);
            animator = null;
            grab = null;
            LeftArm.Weight = RightArm.Weight = 0f;
            LeftArm.Driven = RightArm.Driven = false;
            LeftArm.Gripping = RightArm.Gripping = false;
        }
    }
}
