using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Empty arms knock light things aside (Raik, 2026-11-08): parts up to LightestPushed that the drawn forearm or hand
    // runs into get the arm's speed into them, as a plain collision would give, not more. The arm itself is not stopped
    // by them (an empty hand stops at walls, VrHands.OutOfWalls). The forearm is a capsule from the elbow to past the
    // knuckles, as drawn after the arm is posed (VrHands); its speed is how far each end went since the frame before.
    internal static class VrArmPush
    {
        // Heaviest pushed, in kg.
        private const float LightestPushed = 15f;
        // The forearm's and hand's radius, how far the fingers reach past the knuckles, in meters at world scale 1.
        private const float Radius = 0.05f;
        private const float Fingers = 0.08f;
        // A part leaves a little faster than the arm met it (0 for none), at most this fast, in m/s.
        private const float Bounce = 0.2f;
        private const float Fastest = 4f;
        // Moves faster than this, in m/s, are a jump of the tracking, not a swing.
        private const float Jump = 15f;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");
        private static readonly Collider[] Found = new Collider[32];
        private static readonly Rigidbody[] Pushed = new Rigidbody[32];

        private class Segment
        {
            public Vector3 From;
            public Vector3 To;
            public bool Known;
        }

        private static readonly Segment[] Arms = { new Segment(), new Segment() };
        private static int logs;

        // From VrFrame, after the arms are posed.
        public static void LateUpdate()
        {
            var grab = GameRefs.Grab;
            var player = grab != null ? PlayerBody(grab) : null;
            var dt = Time.deltaTime;
            for (var i = 0; i < 2; i++)
            {
                var arm = Arms[i];
                if (player == null || dt <= 0f || !VrXr.IsRunning || !VrHands.TryGetEmptyArm(i == 0, out var elbow, out var wrist, out var knuckles))
                {
                    arm.Known = false;
                    continue;
                }
                var along = knuckles - wrist;
                var tip = knuckles + (along.sqrMagnitude > 1e-6f ? along.normalized : (wrist - elbow).normalized) * (Fingers * VrCamera.WorldScale);
                if (arm.Known) Push(player, elbow, tip, (elbow - arm.From) / dt, (tip - arm.To) / dt);
                arm.From = elbow;
                arm.To = tip;
                arm.Known = true;
            }
        }

        private static void Push(Rigidbody player, Vector3 from, Vector3 to, Vector3 fromSpeed, Vector3 toSpeed)
        {
            if (fromSpeed.magnitude > Jump || toSpeed.magnitude > Jump) return;
            var radius = Radius * VrCamera.WorldScale;
            var count = Physics.OverlapCapsuleNonAlloc(from, to, radius, Found, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var pushed = 0;
            for (var i = 0; i < count; i++)
            {
                var body = Found[i].attachedRigidbody;
                if (body == null || body == player || body.isKinematic || body.mass <= 0f || body.mass > LightestPushed) continue;
                if (Found[i].transform.IsChildOf(player.transform) || System.Array.IndexOf(Pushed, body, 0, pushed) >= 0) continue;
                // Where the arm meets it, and how fast the arm goes there.
                var line = to - from;
                var t = Mathf.Clamp01(Vector3.Dot(body.worldCenterOfMass - from, line) / Mathf.Max(line.sqrMagnitude, 1e-6f));
                var axis = from + line * t;
                var contact = Found[i].ClosestPoint(axis);
                var outward = contact - axis;
                if (outward.sqrMagnitude < 1e-8f) outward = body.worldCenterOfMass - axis;
                if (outward.sqrMagnitude < 1e-8f) continue;
                outward.Normalize();
                var speed = Vector3.Lerp(fromSpeed, toSpeed, t);
                var into = Vector3.Dot(speed - body.GetPointVelocity(contact), outward);
                if (into <= 0f) continue;
                var change = outward * Mathf.Min(into * (1f + Bounce), Fastest);
                body.AddForceAtPosition(change * body.mass, contact, ForceMode.Impulse);
                if (pushed < Pushed.Length) Pushed[pushed++] = body;
                if (logs++ < 30) Plugin.Log.LogInfo($"Arm push: '{body.name}' {body.mass:0.0} kg knocked at {change.magnitude:0.00} m/s");
            }
            System.Array.Clear(Pushed, 0, pushed);
        }
    }
}
