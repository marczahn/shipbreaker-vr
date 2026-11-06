using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/Werkzeuggewicht: cutter and grapple weigh something in the hand, as Raik asked (first 12 kg, now 5 kg by default). The tool hangs
    // on the controller by a damped spring, for its position and its turn: moved fast it lags behind and settles, a heavier
    // tool more. The hand stays on the tool's grip and goes with it (VrHands.Pose, VrAim), so it never leaves the tool:
    // tool and hand come after the controller together. In tracking space, so flying does not swing the tool. 0 kg is off.
    internal static class VrToolWeight
    {
        // How stiffly the arm holds a tool: N/m for its place, and its turn as fast relative to that.
        // Raised from 600 with the default down to 5 kg (Raik: a little less sluggish).
        private const float Stiffness = 800f;
        private const float TurnFaster = 1.3f;
        // Of critical damping: a little below, so a fast stop lets the tool settle with a slight swing.
        private const float Damping = 0.85f;
        // A tool never lags further than this behind the controller, in meters and degrees.
        private const float MostBehind = 0.2f;
        private const float MostTurn = 50f;
        // Steps of at most this long, in seconds, so the spring stays steady at low frame rates.
        private const float MostStep = 1f / 120f;

        private class State
        {
            public bool Running;
            public int Frame;
            public Vector3 Position;
            public Vector3 Velocity;
            public Quaternion Rotation;
            public Vector3 Spin;
        }

        private static readonly State Left = new State();
        private static readonly State Right = new State();
        private static int logs;

        // Only with extended tool physics (VR options, Physik).
        private static float Mass => VrSettings.ToolWeight != null && VrSettings.ToolPhysics != null && VrSettings.ToolPhysics.Value ? VrSettings.ToolWeight.Value : 0f;

        public static bool Weighs(EquipmentController.Equipment tool) =>
            Mass > 0f && (tool == EquipmentController.Equipment.CuttingTool || tool == EquipmentController.Equipment.GrappleHook);

        // The tool's turn this frame, in tracking space, for the controller's turn wanted; moves its place too. Once a frame.
        public static Quaternion Turn(VrInput.Hand hand, EquipmentController.Equipment tool, Quaternion wanted)
        {
            var state = hand.IsLeft ? Left : Right;
            if (!Weighs(tool) || !hand.TryGetPosition(out var grip))
            {
                state.Running = false;
                return wanted;
            }
            // Not run last frame: the tool was put away (nothing calls this without one), so it starts afresh at the
            // controller instead of swinging in from where it was (review).
            if (state.Running && state.Frame < Time.frameCount - 1) state.Running = false;
            if (state.Running && state.Frame == Time.frameCount) return state.Rotation;
            state.Frame = Time.frameCount;
            if (!state.Running)
            {
                state.Running = true;
                state.Position = grip;
                state.Velocity = Vector3.zero;
                state.Rotation = wanted;
                state.Spin = Vector3.zero;
                if (logs++ < 10) Plugin.Log.LogInfo($"Tool weight: {hand.Name} {tool} at {Mass:0} kg");
                return wanted;
            }
            var mass = Mathf.Max(Mass, 0.5f);
            var omega = Mathf.Sqrt(Stiffness / mass);
            var turnOmega = omega * TurnFaster;
            var time = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            var steps = Mathf.Max(1, Mathf.CeilToInt(time / MostStep));
            var dt = time / steps;
            for (var i = 0; i < steps; i++)
            {
                // Place: a = omega² (target - x) - 2 zeta omega v.
                state.Velocity += (omega * omega * (grip - state.Position) - 2f * Damping * omega * state.Velocity) * dt;
                state.Position += state.Velocity * dt;
                // Turn: the same on the angle between the tool and the controller, about its axis.
                (wanted * Quaternion.Inverse(state.Rotation)).ToAngleAxis(out var angle, out var axis);
                if (angle > 180f) angle -= 360f;
                var error = float.IsInfinity(axis.x) || float.IsNaN(axis.x) ? Vector3.zero : axis * (angle * Mathf.Deg2Rad);
                state.Spin += (turnOmega * turnOmega * error - 2f * Damping * turnOmega * state.Spin) * dt;
                var spin = state.Spin.magnitude;
                if (spin > 1e-6f) state.Rotation = Quaternion.AngleAxis(spin * dt * Mathf.Rad2Deg, state.Spin / spin) * state.Rotation;
            }
            // Never further behind than MostBehind and MostTurn.
            var behind = state.Position - grip;
            var most = MostBehind * VrCamera.WorldScale;
            if (behind.magnitude > most) state.Position = grip + behind.normalized * most;
            var off = Quaternion.Angle(state.Rotation, wanted);
            if (off > MostTurn) state.Rotation = Quaternion.Slerp(wanted, state.Rotation, MostTurn / off);
            return state.Rotation;
        }

        // Where the hand holding the tool is, in tracking space, for the controller's grip there, raw: the tool's place,
        // held back where the tool was thrust into something (VrToolCollide.Back).
        public static Vector3 Grip(VrInput.Hand hand, EquipmentController.Equipment tool, Vector3 raw)
        {
            if (tool != EquipmentController.Equipment.CuttingTool && tool != EquipmentController.Equipment.GrappleHook) return raw;
            // The turn moves the place: run it for this frame first (each step keeps its result for the frame).
            if (hand.TryGetAimRotation(out var aim)) VrToolSteady.Apply(hand, tool, aim);
            return Spring(hand, tool, raw) + VrToolCollide.Back(hand);
        }

        // The tool's place on its spring alone, without what holds it back (for VrToolCollide); after Turn this frame.
        public static Vector3 Spring(VrInput.Hand hand, EquipmentController.Equipment tool, Vector3 raw)
        {
            var state = hand.IsLeft ? Left : Right;
            return Weighs(tool) && state.Running ? state.Position : raw;
        }
    }
}
