using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // The free hand takes hold of the cutter or the grapple in the other hand, as Raik asked: anywhere on it, by pressing
    // its grip within Reach of the tool's shape (VrToolCollide.Distance). The hand then stays where it took hold, turning
    // and moving with the tool instead of its controller (VrHands.Pose), and its fingers close until they touch the tool
    // (VrHands.Wrap). The grapple is quicker with it: its own weight lags 80% less, a part on it 60% less
    // (VrToolWeight). The cutter keeps its weight and is steadied more instead, for precise cuts (VrToolSteady; Raik,
    // 2026-11-18). Letting go of the grip, or the tool leaving the other hand, lets go. The tool keeps aiming with the
    // hand that works it. While a hand holds the tool, or is about to, it does not hold on to anything else (VrHand.Free).
    internal static class VrToolSupport
    {
        // The hand's grip this near the tool's shape takes hold of it, in meters at world scale 1.
        private const float Reach = 0.07f;
        // The demo charges are taken hold of as well (Raik, 2026-11-18), whatever the tool physics: by the charge showing
        // in the hand, this near its middle. The hand that had them may then let go, and the other one has them
        // (VrToolInput, Supporter): from hand to hand as a thing afloat goes, instead of jumping over.
        private const float ChargeReach = 0.16f;
        private static bool physics;
        // How much of the tool's own weight, and of a grappled part's, is left with two hands.
        public const float ToolLeft = 0.2f;
        public const float LoadLeft = 0.4f;

        private static VrHand supporting;
        private static VrHand toolHand;
        private static EquipmentController.Equipment tool;
        // The supporting controller's grip and aim relative to the tool's grip and turn, in tracking space.
        private static Vector3 offset;
        private static Quaternion turn;
        private static bool claimLeft;
        private static bool claimRight;
        private static int logs;
        // VR running, as of this frame's Update (not VrXr itself in Claims: its type needs OpenXR, which the tests lack).
        private static bool running;

        public static bool Supports(VrInput.Hand hand) => supporting != null && supporting.Input == hand;

        // Whether the tool in that hand is held by the other one too.
        public static bool Supported(VrInput.Hand hand) => supporting != null && toolHand != null && toolHand.Input == hand;

        // A hand holding the tool, or empty and near enough to take hold of it: not free to hold on to anything else.
        public static bool Claims(VrHand hand)
        {
            if (supporting == hand) return true;
            // Nothing to take hold of: no engine calls (the hand tests run without the game).
            if (!running || hand.State != HandState.Empty || !Works(hand.Other)) return false;
            return hand.IsLeft ? claimLeft : claimRight;
        }

        // From VrFrame, once per frame after the tool input.
        public static void Update()
        {
            // Only with extended tool physics (VR options, Physik), like the tool's weight and collision.
            running = VrXr.IsRunning;
            physics = running && VrSettings.ToolPhysics != null && VrSettings.ToolPhysics.Value;
            // Which hands are near enough to take hold, for Claims this frame (worked out here: the hand tests call Claims
            // without the game, where no engine call may run).
            claimLeft = running && supporting == null && Near(VrHand.Left);
            claimRight = running && supporting == null && Near(VrHand.Right);
            if (supporting != null)
            {
                var why = !running ? "VR or tool physics off"
                    : !supporting.Input.Grip.Down ? "let go"
                    : !Works(toolHand) || toolHand.Tool != tool ? "the tool left the other hand"
                    : supporting.State != HandState.Empty ? "the hand took something else"
                    : null;
                if (why != null) Release(why);
                return;
            }
            if (!running) return;
            foreach (var hand in VrHand.Both)
            {
                // While the tools are fitted, a grip near the tool moves it instead (VrToolFit).
                if (!hand.Input.Grip.Pressed || !Near(hand) || VrToolFit.Blocks(hand.Node)) continue;
                var other = hand.Other;
                if (!ToolFrame(other, out var grip, out var aim) || !hand.Input.TryGetPosition(out var at) || !hand.Input.TryGetAimRotation(out var pointing)) continue;
                supporting = hand;
                toolHand = other;
                tool = other.Tool;
                var inverse = Quaternion.Inverse(aim);
                offset = inverse * (at - grip);
                turn = inverse * pointing;
                if (logs++ < 30) Plugin.Log.LogInfo($"Tool support: {hand.Name} hand takes hold of the {tool}, {offset.z:0.00} m along it from the grip");
                hand.Input.Buzz(0.3f, 0.05f);
                return;
            }
        }

        // Where the supporting controller should be, in tracking space: on the tool where it took hold.
        public static bool TryGetPose(VrInput.Hand hand, out Vector3 grip, out Quaternion aim)
        {
            grip = default;
            aim = default;
            if (!Supports(hand) || !ToolFrame(toolHand, out var toolGrip, out var toolAim)) return false;
            grip = toolGrip + toolAim * offset;
            aim = toolAim * turn;
            return true;
        }

        // How far a point in the world lies from the tool held by the other hand than this one, or MaxValue.
        public static float Distance(VrInput.Hand hand, Vector3 point)
        {
            if (supporting == null || supporting.Input != hand) return float.MaxValue;
            return VrToolCollide.Distance(toolHand.Input, tool, point);
        }

        private static void Release(string why)
        {
            if (logs++ < 30) Plugin.Log.LogInfo($"Tool support: {supporting.Name} hand lets go of the tool ({why})");
            supporting = null;
            toolHand = null;
        }

        private static bool Works(VrHand hand) => hand != null && hand.HoldsTool
            && (hand.Tool == EquipmentController.Equipment.DemoCharge
                || (physics && (hand.Tool == EquipmentController.Equipment.CuttingTool || hand.Tool == EquipmentController.Equipment.GrappleHook)));

        // The hand that holds the tool in that hand as well, or null.
        public static VrHand Supporter(VrInput.Hand hand) => Supported(hand) ? supporting : null;

        // An empty hand whose grip is near the tool in the other hand.
        private static bool Near(VrHand hand)
        {
            if (hand.State != HandState.Empty || hand.Zone != VrToolBelt.Zone.None || !Works(hand.Other)) return false;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (rig == null || !hand.Input.TryGetPosition(out var local)) return false;
            var at = rig.TransformPoint(local);
            if (hand.Other.Tool == EquipmentController.Equipment.DemoCharge) return VrChargeByHand.Distance(at) <= ChargeReach * VrCamera.WorldScale;
            return VrToolCollide.Distance(hand.Other.Input, hand.Other.Tool, at) <= Reach * VrCamera.WorldScale;
        }

        // The tool's grip and turn this frame in tracking space, as the hand working it holds it.
        // The frame of the tool held by the other hand than this one, as TryGetPose uses it, and that hand.
        public static bool TryGetToolFrame(VrInput.Hand hand, out Vector3 grip, out Quaternion aim, out VrHand holder)
        {
            grip = default;
            aim = default;
            holder = toolHand;
            return Supports(hand) && ToolFrame(toolHand, out grip, out aim);
        }

        private static bool ToolFrame(VrHand hand, out Vector3 grip, out Quaternion aim)
        {
            grip = default;
            aim = default;
            if (!Works(hand) || !hand.Input.TryGetPosition(out var local) || !hand.Input.TryGetAimRotation(out var pointing)) return false;
            aim = VrToolSteady.Apply(hand.Input, hand.Tool, pointing);
            grip = VrToolWeight.Grip(hand.Input, hand.Tool, local);
            return true;
        }
    }
}
