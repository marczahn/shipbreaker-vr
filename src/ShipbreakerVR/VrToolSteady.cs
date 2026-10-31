using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // Cutter and grapple do not turn exactly with the controller: tremor within a small angle is filtered out, as in Raik's
    // VR aiming mod (the filter closes (angle / radius)^2 of the gap), and the tool follows with a short lag, so it feels
    // like something with weight in the hand. Only the turn, which is what shakes the laser at a distance; the tool stays
    // at the hand. The aim uses the same turn, so the laser shows where the tool really points.
    internal static class VrToolSteady
    {
        // In degrees: larger turns are movement, not tremor.
        private const float TremorAngle = 0.5f;
        // How long the tool takes to follow, in seconds.
        private const float Lag = 0.04f;

        private class Filter
        {
            public bool Running;
            public int Frame;
            public Quaternion Rotation;
        }

        private static readonly Filter Left = new Filter();
        private static readonly Filter Right = new Filter();

        // The turn the tool in that hand has this frame, for the controller's turn raw. Asked many times per frame.
        public static Quaternion Apply(VrInput.Hand hand, EquipmentController.Equipment tool, Quaternion raw)
        {
            var filter = hand.IsLeft ? Left : Right;
            if (tool != EquipmentController.Equipment.CuttingTool && tool != EquipmentController.Equipment.GrappleHook)
            {
                filter.Running = false;
                return raw;
            }
            if (!filter.Running)
            {
                filter.Running = true;
                filter.Rotation = raw;
                filter.Frame = Time.frameCount;
                return raw;
            }
            if (filter.Frame == Time.frameCount) return filter.Rotation;
            filter.Frame = Time.frameCount;
            var share = Share(Quaternion.Angle(filter.Rotation, raw), TremorAngle) * (1f - Mathf.Exp(-Time.unscaledDeltaTime / Lag));
            filter.Rotation = Quaternion.Slerp(filter.Rotation, raw, share);
            return filter.Rotation;
        }

        // How much of an angle to the controller counts: little of tremor, all of a real movement.
        internal static float Share(float angle, float tremor)
        {
            var part = Mathf.Clamp01(angle / tremor);
            return part * part;
        }
    }
}
