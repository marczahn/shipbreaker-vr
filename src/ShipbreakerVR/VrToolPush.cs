using BBI.Unity.Game;
using UnityEngine;

namespace ShipbreakerVR
{
    // Steuerung/SchwereTeileSchieben (extended physics): with one hand holding on to something fixed (VrClimb.BracedFor),
    // the cutter or the grapple in the other hand pushes heavy parts, as Raik asked: only by thrusting the tool forward,
    // its end against the part, not by swinging it sideways into it. The force goes along the tool, onto the point it
    // touches, so a part pushed off its centre of mass starts to turn. Held more than SlipAngle off straight on, the tool
    // slides off instead (VrToolCollide turns it out along the surface) and pushes nothing. The force grows with how far
    // the controller is past the surface along the tool, limited as a hand's push on a heavy part (BracedPush), and the
    // hand feels it. Without a hold the body is not braced and nothing is pushed: the tool only stops at the part.
    internal static class VrToolPush
    {
        // More than this off straight on against the surface, the tool slides off, in degrees.
        public const float SlipAngle = 20f;
        // The tool's end is tested as a ball of this radius, from FromGrip ahead of the grip, in meters at world scale 1.
        private const float Radius = 0.02f;
        private const float FromGrip = 0.1f;
        // Parts lighter than this are left to the game; up to BracedPush.MaxMass they are pushed.
        private const float LightestMass = 30f;
        private const int StaticLayer = 27;
        // The hum in the hand: Least at the first touch up to Most at the full force, a pulse every BuzzEvery seconds.
        private const float BuzzLeast = 0.05f;
        private const float BuzzMost = 0.5f;
        private const float BuzzEvery = 0.05f;
        // A push lost for less than this, in seconds (the hand wavering past SlipAngle, the ray slipping off a seam), goes
        // on: in the first test every push broke off within 0.4 s, too short to move the 636 kg hoist truck.
        private const float Grace = 0.25f;

        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        private class State
        {
            public Rigidbody Pushing;
            public float LastGood;
            public float AngleSum;
            public float AngleMost;
            public float NextBuzz;
            public float Since;
            public float Sum;
            public float Most;
            public int Steps;
        }

        private static readonly State Left = new State();
        private static readonly State Right = new State();
        private static int logs;

        // From VrFrame, each physics step after the climbing.
        public static void FixedUpdate()
        {
            if (VrSettings.BracedPush == null || !VrSettings.BracedPush.Value || !VrXr.IsRunning)
            {
                Stop(VrHand.Left, Left, "extended physics off");
                Stop(VrHand.Right, Right, "extended physics off");
                return;
            }
            foreach (var hand in VrHand.Both) Step(hand, hand.IsLeft ? Left : Right);
        }

        private static void Step(VrHand hand, State state)
        {
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            if (rig == null || !hand.HoldsTool
                || (hand.Tool != EquipmentController.Equipment.CuttingTool && hand.Tool != EquipmentController.Equipment.GrappleHook)
                || !VrClimb.BracedFor(hand)
                || !hand.Input.TryGetPosition(out var gripLocal) || !hand.Input.TryGetAimRotation(out var aimLocal))
            {
                Lost(hand, state, !VrClimb.BracedFor(hand) ? "no hold on something fixed" : "no tool");
                return;
            }
            // Where the arm puts the tool, the controller's own place and turn: the tool itself is held back at the part.
            var pivot = rig.TransformPoint(gripLocal);
            var rotation = rig.rotation * aimLocal;
            var forward = rotation * Vector3.forward;
            var scale = VrCamera.WorldScale;
            var start = pivot + forward * FromGrip * scale;
            var reach = VrToolCollide.ToolLength(pivot, rotation) - FromGrip * scale;
            if (reach <= 0f || !Touching(start, forward, reach, out var hit, out var body))
            {
                Lost(hand, state, "nothing heavy at the tool's end");
                return;
            }
            // Thrust in straight, within SlipAngle of the surface's normal; at a slant the tool slides off.
            var angle = Vector3.Angle(forward, -hit.normal);
            if (angle > SlipAngle)
            {
                Lost(hand, state, $"{angle:0}° off straight on");
                return;
            }
            // How far the tool's end would be past the surface, along the tool.
            var depth = reach - hit.distance;
            var force = BracedPush.Force(forward * (depth / scale), body.mass);
            body.AddForceAtPosition(force, hit.point, ForceMode.Force);
            var size = force.magnitude;
            if (state.Pushing != body)
            {
                Stop(hand, state);
                state.Pushing = body;
                state.Since = Time.time;
                if (logs++ < 60)
                    Plugin.Log.LogInfo($"Tool push: {hand.Name} {hand.Tool} on '{body.name}' {body.mass:0} kg, {angle:0}° off straight on, "
                        + $"{Vector3.Distance(hit.point, body.worldCenterOfMass):0.00} m off its centre of mass");
            }
            state.LastGood = Time.time;
            state.AngleSum += angle;
            state.AngleMost = Mathf.Max(state.AngleMost, angle);
            state.Sum += size;
            state.Most = Mathf.Max(state.Most, size);
            state.Steps++;
            if (Time.unscaledTime >= state.NextBuzz)
            {
                state.NextBuzz = Time.unscaledTime + BuzzEvery;
                hand.Input.Buzz(Mathf.Lerp(BuzzLeast, BuzzMost, size / BracedPush.MaxForce), BuzzEvery * 1.6f);
            }
        }

        // The nearest heavy, movable part the tool's end line runs into, and where.
        private static bool Touching(Vector3 start, Vector3 forward, float reach, out RaycastHit found, out Rigidbody body)
        {
            found = default;
            body = null;
            var player = VrToolCollide.Player();
            // A plain ray first, for the face's own normal (a ball's normal turns towards an edge it touches); the ball
            // when the ray passes beside a thin part.
            if (!Nearest(Physics.RaycastNonAlloc(start, forward, Hits, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore), player, out found)
                && !Nearest(Physics.SphereCastNonAlloc(start, Radius * VrCamera.WorldScale, forward, Hits, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore), player, out found))
                return false;
            var part = found.rigidbody;
            if (part == null || part.isKinematic || part.gameObject.layer == StaticLayer || part.mass < LightestMass || part.mass > BracedPush.MaxMass) return false;
            body = part;
            return true;
        }

        private static bool Nearest(int count, Rigidbody player, out RaycastHit found)
        {
            found = default;
            var nearest = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var hit = Hits[i];
                // Starting inside something gives no surface to push on.
                if (hit.distance <= 0f || hit.distance >= nearest || VrToolCollide.Own(hit.collider, player)) continue;
                nearest = hit.distance;
                found = hit;
            }
            return nearest < float.MaxValue;
        }

        // No push this step; the push ends once it has been lost for Grace.
        private static void Lost(VrHand hand, State state, string why)
        {
            if (state.Pushing != null && Time.time - state.LastGood < Grace) return;
            Stop(hand, state, why);
        }

        private static void Stop(VrHand hand, State state, string why = "another part")
        {
            if (state.Pushing != null && state.Steps > 0 && logs++ < 60)
            {
                var speed = state.Pushing.velocity.magnitude;
                Plugin.Log.LogInfo($"Tool push: {hand.Name} stopped on '{state.Pushing.name}' after {Time.time - state.Since:0.0} s ({why}), "
                    + $"{state.Sum / state.Steps:0} N on average (most {state.Most:0}, limit {BracedPush.Limit(state.Pushing.mass):0}), "
                    + $"{state.AngleSum / state.Steps:0}° off straight on on average (most {state.AngleMost:0}), the part now at {speed:0.00} m/s");
            }
            state.Pushing = null;
            state.Sum = state.Most = state.AngleSum = state.AngleMost = 0f;
            state.Steps = 0;
        }
    }
}
