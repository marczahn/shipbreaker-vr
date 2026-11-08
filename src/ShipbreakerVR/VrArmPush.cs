using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The arms knock light things aside (Raik, 2026-11-08): parts up to LightestPushed that the drawn arm runs into are
    // pushed softly, as by a light arm in a soft suit with give in its joints. Of the arm's own swing (its speed against
    // the body) a part gets only Swing; of the body's own flight it gets all, but no more: flying with the arm out, the
    // part is pushed along ahead, never faster than the body, and keeps that speed when the body brakes (Raik). Before,
    // the arm's whole speed and a bounce, which threw parts hard at the slightest touch. Always, whatever the hand does: the upper arm
    // and the forearm push at any speed; the hand only once it moves faster than HandFrom, so a hand reaching slowly for a
    // part can still take hold of it before knocking it away. What either hand holds is never pushed. The arm itself is
    // not stopped by them (an empty hand stops at walls, VrHands.OutOfWalls). Each piece is a capsule between its joints
    // as drawn after the arm is posed (VrHands); its speed is how far each end went since the frame before. All layers
    // but triggers: the collectable spare parts (circuit boards, cables) were not pushed with the default raycast layers.
    internal static class VrArmPush
    {
        // Heaviest pushed, in kg.
        private const float LightestPushed = 15f;
        // The arm's and hand's radius, how far the fingers reach past the knuckles, in meters at world scale 1.
        private const float ArmRadius = 0.05f;
        private const float HandRadius = 0.05f;
        private const float Fingers = 0.08f;
        // The hand pushes only this fast, in m/s at world scale 1.
        private const float HandFrom = 0.8f;
        // The share of the arm's own swing a part gets, and the most it gets from it, in m/s at world scale 1.
        private const float Swing = 0.3f;
        private const float MostSwing = 1f;
        // Moves faster than this, in m/s, are a jump of the tracking, not a swing.
        private const float Jump = 15f;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");
        private static readonly Collider[] Found = new Collider[32];
        private static readonly Rigidbody[] Pushed = new Rigidbody[32];
        private static int pushed;

        // The joints of an arm last frame: shoulder, elbow, wrist, finger tips.
        private class Joints
        {
            public readonly Vector3[] At = new Vector3[4];
            public bool Known;
        }

        private static readonly Joints[] Arms = { new Joints(), new Joints() };
        private static readonly Vector3[] Now = new Vector3[4];
        private static readonly Vector3[] Speed = new Vector3[4];
        private static int logs;

        // From VrFrame, after the arms are posed.
        public static void LateUpdate()
        {
            var grab = GameRefs.Grab;
            var player = grab != null ? PlayerBody(grab) : null;
            var dt = Time.deltaTime;
            var scale = VrCamera.WorldScale;
            var heldLeft = VrClimb.HeldBody(VrInput.Left);
            var heldRight = VrClimb.HeldBody(VrInput.Right);
            pushed = 0;
            for (var i = 0; i < 2; i++)
            {
                var arm = Arms[i];
                if (player == null || dt <= 0f || !VrXr.IsRunning
                    || !VrHands.TryGetArm(i == 0, out var shoulder, out var elbow, out var wrist, out var knuckles))
                {
                    arm.Known = false;
                    continue;
                }
                var along = knuckles - wrist;
                if (along.sqrMagnitude < 1e-6f) along = wrist - elbow;
                Now[0] = shoulder;
                Now[1] = elbow;
                Now[2] = wrist;
                Now[3] = knuckles + along.normalized * (Fingers * scale);
                if (arm.Known)
                {
                    var jumped = false;
                    for (var j = 0; j < 4; j++)
                    {
                        Speed[j] = (Now[j] - arm.At[j]) / dt;
                        if (Speed[j].magnitude > Jump * scale) jumped = true;
                    }
                    if (!jumped)
                    {
                        Push(player, heldLeft, heldRight, 0, ArmRadius * scale);
                        Push(player, heldLeft, heldRight, 1, ArmRadius * scale);
                        if ((Speed[3] - player.velocity).magnitude > HandFrom * scale) Push(player, heldLeft, heldRight, 2, HandRadius * scale);
                    }
                }
                System.Array.Copy(Now, arm.At, 4);
                arm.Known = true;
            }
            System.Array.Clear(Pushed, 0, pushed);
        }

        // The piece from joint j to joint j + 1.
        private static void Push(Rigidbody player, Rigidbody heldLeft, Rigidbody heldRight, int j, float radius)
        {
            var from = Now[j];
            var to = Now[j + 1];
            var count = Physics.OverlapCapsuleNonAlloc(from, to, radius, Found, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var body = Found[i].attachedRigidbody;
                if (body == null || body == player || body == heldLeft || body == heldRight || body.isKinematic || body.mass <= 0f || body.mass > LightestPushed) continue;
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
                // The speed it is to have along the push: the body's flight, and a share of the arm's own swing.
                var swing = Vector3.Lerp(Speed[j], Speed[j + 1], t) - player.velocity;
                var flight = Mathf.Max(Vector3.Dot(player.velocity, outward), 0f);
                var own = Mathf.Min(Mathf.Max(Vector3.Dot(swing, outward), 0f) * Swing, MostSwing * VrCamera.WorldScale);
                var into = flight + own - Vector3.Dot(body.GetPointVelocity(contact), outward);
                if (into <= 0f) continue;
                var change = outward * into;
                body.AddForceAtPosition(change * body.mass, contact, ForceMode.Impulse);
                if (pushed < Pushed.Length) Pushed[pushed++] = body;
                if (logs++ < 40)
                    Plugin.Log.LogInfo($"Arm push: {(j == 0 ? "upper arm" : j == 1 ? "forearm" : "hand")} knocked '{Found[i].name}' (layer {Found[i].gameObject.layer}, '{body.name}' {body.mass:0.0} kg), {change.magnitude:0.00} m/s more (flight {flight:0.00}, swing {own:0.00})");
            }
        }
    }
}
