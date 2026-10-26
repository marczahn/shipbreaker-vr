using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Picking the tools off the body instead of the flat game's wheel, as Marc wanted: reaching there with the right hand
    // and pressing the grip draws the tool, doing it again at the same place puts it away. The zones are measured from the
    // head in tracking space (real meters), turned with the head's heading, so they stay at the body whichever way one
    // looks or the playspace turns. Entering a zone gives a short buzz.
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

        private static Zone current;
        private static int logs;

        // The zone the right hand is in this frame (None without tracking).
        public static Zone Update(InputDevice right)
        {
            var zone = Find(right, out var local);
            if (zone != current)
            {
                current = zone;
                if (zone != Zone.None)
                {
                    right.SendHapticImpulse(0, 0.25f, 0.04f);
                    if (logs++ < 40) Plugin.Log.LogInfo($"Tool belt: hand at {zone} ({local.x:0.00}, {local.y:0.00}, {local.z:0.00} m from the head)");
                }
            }
            return zone;
        }

        private static Zone Find(InputDevice right, out Vector3 local)
        {
            local = Vector3.zero;
            var vr = VrCamera.Instance;
            if (vr == null || !right.TryGetFeatureValue(CommonUsages.devicePosition, out var hand)) return Zone.None;
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

        public static void Reset() => current = Zone.None;
    }
}
