using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // Cutter and grapple do not turn exactly with the controller: tremor within a small angle is filtered out, as in Raik's
    // VR aiming mod (the filter closes (angle / radius)^2 of the gap), and the tool follows with a short lag, so it feels
    // like something with weight in the hand. Only the turn, which is what shakes the laser at a distance. With extended
    // tool physics the turn then goes through the tool's weight (VrToolWeight, which also moves its place) and its
    // collision (VrToolCollide). The aim uses the same turn, so the laser shows where the tool really points.
    internal static class VrToolSteady
    {
        // In degrees: larger turns are movement, not tremor.
        private const float TremorAngle = 0.5f;
        // How long the tool takes to follow, in seconds.
        private const float Lag = 0.04f;
        // The cutter with the second hand on it (VrToolSupport) is steadied more (Raik, 2026-11-18: the other way round
        // from the grapple, which the second hand makes quicker; so a precise cut does not stray onto delicate parts):
        // tremor within a wider angle is filtered out, and it follows more slowly. Raik 2026-11-27: with 1.5° and 0.1 s
        // and the place not steadied at all it moved "just as without the hand"; now clearly slower and steadier, its
        // place too (Place): within 3° and 2 cm hardly at all, beyond with a lag of a third of a second.
        private const float TwoHandTremor = 3f;
        private const float TwoHandLag = 0.30f;
        private const float TwoHandPlaceTremor = 0.02f;
        private const float TwoHandPlaceLag = 0.25f;

        private class Filter
        {
            public bool Running;
            public int Frame;
            public Quaternion Rotation;
            public bool Placing;
            public int PlaceFrame;
            public Vector3 Position;
        }

        private static int placeLogs;

        // The cutter's place this frame, in tracking space, for the controller's grip raw: steadied while the second
        // hand holds it, else raw. Asked many times per frame.
        public static Vector3 Place(VrInput.Hand hand, EquipmentController.Equipment tool, Vector3 raw)
        {
            var filter = hand.IsLeft ? Left : Right;
            if (tool != EquipmentController.Equipment.CuttingTool || !VrToolSupport.Supported(hand))
            {
                filter.Placing = false;
                return raw;
            }
            if (filter.Placing && filter.PlaceFrame == Time.frameCount) return filter.Position;
            if (!filter.Placing || filter.PlaceFrame < Time.frameCount - 1)
            {
                filter.Placing = true;
                filter.PlaceFrame = Time.frameCount;
                filter.Position = raw;
                if (placeLogs++ < 20) Plugin.Log.LogInfo($"Tool steady: the cutter in the {hand.Name} hand steadied by the other hand (turn within {TwoHandTremor}°, place within {TwoHandPlaceTremor * 100f:0} cm, lag {TwoHandLag:0.00} s)");
                return raw;
            }
            filter.PlaceFrame = Time.frameCount;
            var scale = VrCamera.WorldScale;
            var share = Share((raw - filter.Position).magnitude, TwoHandPlaceTremor * scale) * (1f - Mathf.Exp(-Time.unscaledDeltaTime / TwoHandPlaceLag));
            filter.Position = Vector3.Lerp(filter.Position, raw, share);
            return filter.Position;
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
            // Not run last frame: the tool was put away, the filter starts afresh (as VrToolWeight).
            if (filter.Running && filter.Frame < Time.frameCount - 1) filter.Running = false;
            if (!filter.Running)
            {
                filter.Running = true;
                filter.Rotation = raw;
                filter.Frame = Time.frameCount;
                return VrToolCollide.Apply(hand, tool, VrToolWeight.Turn(hand, tool, raw));
            }
            if (filter.Frame == Time.frameCount) return VrToolCollide.Apply(hand, tool, VrToolWeight.Turn(hand, tool, filter.Rotation));
            filter.Frame = Time.frameCount;
            var steadier = tool == EquipmentController.Equipment.CuttingTool && VrToolSupport.Supported(hand);
            var share = Share(Quaternion.Angle(filter.Rotation, raw), steadier ? TwoHandTremor : TremorAngle)
                * (1f - Mathf.Exp(-Time.unscaledDeltaTime / (steadier ? TwoHandLag : Lag)));
            filter.Rotation = Quaternion.Slerp(filter.Rotation, raw, share);
            // Then given weight and kept out of what is in the tool's way (extended tool physics).
            return VrToolCollide.Apply(hand, tool, VrToolWeight.Turn(hand, tool, filter.Rotation));
        }

        // How much of an angle to the controller counts: little of tremor, all of a real movement.
        internal static float Share(float angle, float tremor)
        {
            var part = Mathf.Clamp01(angle / tremor);
            return part * part;
        }

        // The light smoothing of every hand (VrInput, Experimente/HandGlaettung): how much of the way to the controller
        // a pose that is that far behind it (a length or an angle) goes in a frame of dt seconds. Within the tremor it
        // follows slowly, the slower the less it is behind (as Share); beyond it the faster the further behind, so a
        // real movement is followed at once: moved at a steady speed the pose stays the root of speed x lag x tremor
        // behind (under a centimeter at 1 m/s with 2 mm and 0.04 s).
        // The angle between two rotations in degrees, also where Quaternion.Angle gives 0 (below about 0.16 degrees,
        // where its dot product rounds to 1): a smoothed pose would stop that far off its controller.
        internal static float Angle(Quaternion a, Quaternion b)
        {
            // The inverse of a rotation is its conjugate (plain arithmetic, so the tests run it without the engine).
            var turn = new Quaternion(-a.x, -a.y, -a.z, a.w) * b;
            var sine = Mathf.Sqrt(turn.x * turn.x + turn.y * turn.y + turn.z * turn.z);
            return 2f * Mathf.Atan2(sine, Mathf.Abs(turn.w)) * Mathf.Rad2Deg;
        }

        internal static float Follow(float behind, float tremor, float lag, float dt)
        {
            if (tremor <= 0f || lag <= 0f) return 1f;
            if (dt <= 0f || behind <= 0f) return 0f;
            var part = behind / tremor;
            var rate = (part < 1f ? part * part : part) / lag;
            return 1f - Mathf.Exp(-rate * dt);
        }
    }
}
