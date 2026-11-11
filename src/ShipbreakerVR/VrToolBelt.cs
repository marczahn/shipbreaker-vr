using UnityEngine;

namespace ShipbreakerVR
{
    // Picking the tools off the body instead of the flat game's wheel, as Marc wanted: reaching there with either hand and
    // pressing the grip draws the tool (VrToolInput), letting go puts it back. The zones are measured from the head in
    // tracking space (real meters), turned with the head's heading, so they stay at the body whichever way one looks or
    // the playspace turns; they are the same for both hands. Entering a zone gives that hand a short buzz.
    //   right hip            grapple
    //   left hip             demo charges (drawn across the body)
    //   over the right shoulder  cutter
    //   right side of the helmet scanner
    //   left side of the helmet  flashlight on or off, left hand only
    internal static class VrToolBelt
    {
        public enum Zone { None, Grapple, Cutter, DemoCharge, Scanner, Flashlight }

        // Head-relative bounds in meters: x right, y up, z forward.
        private const float HipBelow = -0.45f;
        private const float HipDeepest = -0.85f;
        private const float HipSide = 0.1f;
        private const float HipOutside = 0.35f;
        // With the grapple at the hip (VrBeltGrapple), the hand draws it only within GripReach of its grip, as Raik asked:
        // the zone around the hip reached far beyond the grapple. The grip hangs at GripAt, where Raik's hand reached for
        // it (12 grips, all within 9 cm); before, a copy hung at the body model's hip, 30 cm further back, and the hand took
        // it from here.
        public static readonly Vector3 GripAt = new Vector3(0.30f, -0.72f, -0.08f);
        private const float GripReach = 0.13f;
        private const float HipForward = 0.1f;
        private const float HipBehind = -0.2f;
        // The hip zones are turned this far around the head towards the back, in degrees, so a hand reaching out to the
        // side to hold on does not draw a tool.
        private const float HipTurn = 15f;
        private static readonly float HipTurnCos = Mathf.Cos(HipTurn * Mathf.Deg2Rad);
        private static readonly float HipTurnSin = Mathf.Sin(HipTurn * Mathf.Deg2Rad);
        private const float ShoulderAbove = -0.12f;
        private const float ShoulderBehind = -0.05f;
        private const float ShoulderInside = -0.1f;
        private const float HelmetReach = 0.22f;
        private const float HelmetSide = 0.06f;

        // The zone each hand last buzzed for (left, right).
        private static readonly Zone[] announced = new Zone[2];
        private static int logs;
        private static int missLogs;

        // The zone the hand is in this frame (None without tracking). Quiet (while its trigger is held) it does not buzz;
        // the buzz comes when the trigger is let go inside the zone.
        public static Zone Update(VrInput.Hand hand, bool quiet)
        {
            var zone = Find(hand, out var local);
            var index = hand.IsLeft ? 0 : 1;
            // A grip pressed near the hip grapple but outside its zone: where the hand was, to fit GripReach.
            if (zone == Zone.None && !hand.IsLeft && hand.Grip.Pressed && missLogs < 30)
            {
                var off = local - GripAt;
                if (off.magnitude < 0.4f)
                {
                    missLogs++;
                    Plugin.Log.LogInfo($"Tool belt: right grip pressed {off.magnitude:0.00} m from where the hip grapple is drawn (off {off.x:0.00}, {off.y:0.00}, {off.z:0.00}), outside its {GripReach:0.00} m");
                }
            }
            if (zone == Zone.None) announced[index] = Zone.None;
            else if (zone != announced[index] && !quiet)
            {
                announced[index] = zone;
                hand.Buzz(0.25f, 0.04f);
                if (logs++ < 40)
                    Plugin.Log.LogInfo($"Tool belt: {hand.Name} hand at {zone} ({local.x:0.00}, {local.y:0.00}, {local.z:0.00} m from the head)");
            }
            return zone;
        }

        private static Zone Find(VrInput.Hand controller, out Vector3 local)
        {
            if (!HeadLocal(controller, out local)) return Zone.None;
            var zone = ZoneAt(local);
            return zone == Zone.Flashlight && !controller.IsLeft ? Zone.None : zone;
        }

        // Where the controller is relative to the head, in meters along the head's heading (x right, y up, z forward), as
        // the zones are measured; also for stowing things over the left shoulder (VrShoulderStow).
        internal static bool HeadLocal(VrInput.Hand controller, out Vector3 local)
        {
            local = Vector3.zero;
            var vr = VrCamera.Instance;
            if (vr == null || !controller.TryGetPosition(out var hand)) return false;
            var head = vr.transform;
            var forward = head.localRotation * Vector3.forward;
            forward.y = 0f;
            // Looking straight down the top of the head points ahead.
            if (forward.sqrMagnitude < 0.01f) forward = head.localRotation * Vector3.up;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return false;
            local = Quaternion.Inverse(Quaternion.LookRotation(forward, Vector3.up)) * (hand - head.localPosition);
            return true;
        }

        // Over the left shoulder, the mirror of the cutter's zone over the right one, with its inner edge at the middle
        // of the head (the cutter's reaches 10 cm past it), for stowing what a hand holds (VrShoulderStow).
        internal static bool OverLeftShoulder(Vector3 local) => local.y > ShoulderAbove && local.z < ShoulderBehind && local.x < 0f;

        // The zone at a hand position relative to the head, in meters along the head's heading (x right, y up, z forward).
        // The grapple's zone is a ball of GripReach around its grip at the right hip, the demo charges' the box at the left.
        internal static Zone ZoneAt(Vector3 local)
        {
            if ((local - GripAt).magnitude < GripReach) return Zone.Grapple;
            if (local.y < HipBelow && local.y > HipDeepest)
            {
                // Measured as if the hand were turned forward by HipTurn on its side of the body, mirrored for the left hip.
                var side = Mathf.Abs(local.x);
                var turnedSide = side * HipTurnCos - local.z * HipTurnSin;
                var turnedForward = local.z * HipTurnCos + side * HipTurnSin;
                if (local.x <= 0f && turnedForward < HipForward && turnedForward > HipBehind && turnedSide > HipSide && turnedSide < HipOutside)
                    return Zone.DemoCharge;
            }
            if (local.magnitude < HelmetReach && local.x > HelmetSide && local.z > ShoulderBehind) return Zone.Scanner;
            if (local.magnitude < HelmetReach && local.x < -HelmetSide && local.z > ShoulderBehind) return Zone.Flashlight;
            if (local.y > ShoulderAbove && local.z < ShoulderBehind && local.x > ShoulderInside) return Zone.Cutter;
            return Zone.None;
        }

        public static void Reset() => announced[0] = announced[1] = Zone.None;
    }
}
