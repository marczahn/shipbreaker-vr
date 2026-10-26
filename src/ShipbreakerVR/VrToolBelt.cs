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
    internal static class VrToolBelt
    {
        public enum Zone { None, Grapple, Cutter, DemoCharge, Scanner }

        // Head-relative bounds in meters: x right, y up, z forward.
        private const float HipBelow = -0.45f;
        private const float HipSide = 0.08f;
        private const float HipForward = 0.25f;
        private const float ShoulderAbove = -0.12f;
        private const float ShoulderBehind = -0.05f;
        private const float ShoulderInside = -0.1f;
        private const float HelmetReach = 0.22f;
        private const float HelmetSide = 0.06f;

        // The zone each hand last buzzed for (left, right).
        private static readonly Zone[] announced = new Zone[2];
        private static int logs;

        // The zone the hand is in this frame (None without tracking). Quiet (while its trigger is held) it does not buzz;
        // the buzz comes when the trigger is let go inside the zone.
        public static Zone Update(VrInput.Hand hand, bool quiet)
        {
            var zone = Find(hand, out var local);
            var index = hand.IsLeft ? 0 : 1;
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
            local = Vector3.zero;
            var vr = VrCamera.Instance;
            if (vr == null || !controller.TryGetPosition(out var hand)) return Zone.None;
            var head = vr.transform;
            var forward = head.localRotation * Vector3.forward;
            forward.y = 0f;
            // Looking straight down the top of the head points ahead.
            if (forward.sqrMagnitude < 0.01f) forward = head.localRotation * Vector3.up;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return Zone.None;
            local = Quaternion.Inverse(Quaternion.LookRotation(forward, Vector3.up)) * (hand - head.localPosition);

            if (local.y < HipBelow && local.z < HipForward)
            {
                if (local.x > HipSide) return Zone.Grapple;
                if (local.x < -HipSide) return Zone.DemoCharge;
            }
            if (local.magnitude < HelmetReach && local.x > HelmetSide && local.z > ShoulderBehind) return Zone.Scanner;
            if (local.y > ShoulderAbove && local.z < ShoulderBehind && local.x > ShoulderInside) return Zone.Cutter;
            return Zone.None;
        }

        public static void Reset() => announced[0] = announced[1] = Zone.None;
    }
}
