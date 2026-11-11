using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // The arms knock light things aside (Raik, 2026-11-08): parts up to LightestPushed that the drawn arm runs into are
    // pushed softly, as by a light arm in a soft suit with give in its joints: a part gets only Share of the arm's speed
    // into it, its own swing (against the body, at most MostSwing) and the body's flight alike, and never more than the
    // body flies: flown into, it drifts off ahead slower than the body and keeps its speed when the body brakes (Raik).
    // Before, the arm's whole speed and a bounce, which threw parts hard at the slightest touch; then the flight's whole
    // speed, still too much when flying into parts. Always, whatever the hand does: the upper arm
    // and the forearm push at any speed; the hand only once it moves faster than HandFrom, so a hand reaching slowly for a
    // part can still take hold of it before knocking it away. What either hand holds is never pushed. The arm itself is
    // not stopped by them (an empty hand stops at walls, VrHands.OutOfWalls). Each piece is a capsule between its joints
    // as drawn after the arm is posed (VrHands); its speed is how far each end went since the frame before. All layers
    // but triggers: the collectable spare parts (circuit boards, cables) were not pushed with the default raycast layers.
    // Heavier parts (the small padded cargo crates) the hand strikes (Raik, 2026-11-11): a hand that comes into one faster
    // than StrikeFrom gives it a push as of a hand of HandMass at that speed, once as it comes in, the more the more the
    // palm faces it: the full push with the palm, BackShare of it with the back of the hand. A buzz to feel it.
    // The hand itself is a flat plate from the wrist to the finger tips (Raik, 2026-11-11), not a capsule: what it meets
    // goes off straight away from the face that met it, not at an angle from where its middle touched the round capsule.
    // Met edge-on, or sliding along, it goes the way the hand goes. And the plate is tested along its way since the frame
    // before in steps of its thickness, so a fast hand that passes through a small part in one frame still meets it and
    // pushes it the way it struck, not back against the blow.
    internal static class VrArmPush
    {
        // Heaviest pushed, in kg.
        private const float LightestPushed = 15f;
        // The arm's radius, how far the fingers reach past the knuckles, in meters at world scale 1.
        private const float ArmRadius = 0.05f;
        private const float Fingers = 0.08f;
        // The hand pushes only this fast, in m/s at world scale 1.
        private const float HandFrom = 0.8f;
        // The share of the arm's speed a part gets, and the most it gets from the arm's own swing, in m/s at world scale 1.
        private const float Share = 0.33f;
        private const float MostSwing = 1f;
        // Moves faster than this, in m/s, are a jump of the tracking, not a swing.
        private const float Jump = 15f;
        // Striking heavier parts: up to HeaviestStruck kg, from StrikeFrom m/s at world scale 1, as a hand of HandMass kg;
        // the back of the hand gives BackShare of it. Never faster than the hand's own Share of its speed.
        private const float HeaviestStruck = 2000f;
        private const float StrikeFrom = 1f;
        private const float HandMass = 6f;
        private const float BackShare = 0.15f;
        // The hand's plate: half its width and thickness, in meters at world scale 1; most steps along its way per frame.
        // Met within FaceWithin (the cosine) of its face, a part goes off along the face's normal.
        private const float HalfWidth = 0.045f;
        private const float HalfThick = 0.015f;
        private const int MostSteps = 10;
        private const float FaceWithin = 0.4f;
        private static readonly System.Collections.Generic.HashSet<Rigidbody> Met = new System.Collections.Generic.HashSet<Rigidbody>();
        // The bodies each hand touched last frame: struck only as the hand comes into one.
        private static readonly System.Collections.Generic.HashSet<Rigidbody>[] Touched =
            { new System.Collections.Generic.HashSet<Rigidbody>(), new System.Collections.Generic.HashSet<Rigidbody>() };
        private static readonly System.Collections.Generic.HashSet<Rigidbody>[] TouchedNow =
            { new System.Collections.Generic.HashSet<Rigidbody>(), new System.Collections.Generic.HashSet<Rigidbody>() };
        private static int side;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");
        private static readonly Collider[] Found = new Collider[32];
        private static readonly Rigidbody[] Pushed = new Rigidbody[32];
        private static int pushed;

        // The joints of an arm last frame: shoulder, elbow, wrist, finger tips.
        private class Joints
        {
            public readonly Vector3[] At = new Vector3[4];
            // The way the palm faced last frame.
            public Vector3 Palm;
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
                    side = i;
                    TouchedNow[i].Clear();
                    if (!jumped)
                    {
                        Push(player, heldLeft, heldRight, 0, ArmRadius * scale);
                        Push(player, heldLeft, heldRight, 1, ArmRadius * scale);
                        Hand(player, heldLeft, heldRight, arm);
                    }
                    Touched[i].Clear();
                    Touched[i].UnionWith(TouchedNow[i]);
                }
                else Touched[i].Clear();
                System.Array.Copy(Now, arm.At, 4);
                arm.Palm = Palm(i == 0 ? VrInput.Left : VrInput.Right, arm.Palm);
                arm.Known = true;
            }
            System.Array.Clear(Pushed, 0, pushed);
        }

        // The piece from joint j to joint j + 1 (upper arm, forearm).
        private static void Push(Rigidbody player, Rigidbody heldLeft, Rigidbody heldRight, int j, float radius)
        {
            var from = Now[j];
            var to = Now[j + 1];
            var count = Physics.OverlapCapsuleNonAlloc(from, to, radius, Found, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var body = Found[i].attachedRigidbody;
                if (!Pushable(body, player, heldLeft, heldRight) || Found[i].transform.IsChildOf(player.transform)) continue;
                if (body.mass > LightestPushed) continue;
                // Where the arm meets it, and how fast the arm goes there.
                var line = to - from;
                var t = Mathf.Clamp01(Vector3.Dot(body.worldCenterOfMass - from, line) / Mathf.Max(line.sqrMagnitude, 1e-6f));
                var axis = from + line * t;
                var contact = Found[i].ClosestPoint(axis);
                var outward = contact - axis;
                if (outward.sqrMagnitude < 1e-8f) outward = body.worldCenterOfMass - axis;
                if (outward.sqrMagnitude < 1e-8f) continue;
                Knock(player, Found[i], body, contact, outward.normalized, Vector3.Lerp(Speed[j], Speed[j + 1], t) - player.velocity,
                    j == 0 ? "upper arm" : "forearm");
            }
        }

        private static bool Pushable(Rigidbody body, Rigidbody player, Rigidbody heldLeft, Rigidbody heldRight) =>
            body != null && body != player && body != heldLeft && body != heldRight && !body.isKinematic && body.mass > 0f;

        // A light part gets the speed it is to have along the push: the body's flight, and a share of the arm's own swing.
        private static void Knock(Rigidbody player, Collider collider, Rigidbody body, Vector3 contact, Vector3 outward, Vector3 swing, string piece)
        {
            if (System.Array.IndexOf(Pushed, body, 0, pushed) >= 0) return;
            var flying = Mathf.Max(Vector3.Dot(player.velocity, outward), 0f);
            var flight = flying * Share;
            var own = Mathf.Min(Mathf.Max(Vector3.Dot(swing, outward), 0f) * Share, MostSwing * VrCamera.WorldScale);
            var wanted = flying > 0f ? Mathf.Min(flight + own, flying) : own;
            var into = wanted - Vector3.Dot(body.GetPointVelocity(contact), outward);
            if (into <= 0f) return;
            var change = outward * into;
            body.AddForceAtPosition(change * body.mass, contact, ForceMode.Impulse);
            if (pushed < Pushed.Length) Pushed[pushed++] = body;
            if (logs++ < 40)
                Plugin.Log.LogInfo($"Arm push: {piece} knocked '{collider.name}' (layer {collider.gameObject.layer}, '{body.name}' {body.mass:0.0} kg), {change.magnitude:0.00} m/s more (flight {flight:0.00}, swing {own:0.00})");
        }

        // The way that hand's palm faces, square to the hand from the wrist to the finger tips; else as before.
        private static Vector3 Palm(VrInput.Hand input, Vector3 before)
        {
            if (!VrClimb.TryGetPalm(input, out var palm)) return before;
            var flat = Vector3.ProjectOnPlane(palm, Now[3] - Now[2]);
            return flat.sqrMagnitude > 1e-6f ? flat.normalized : before;
        }

        // The hand as a flat plate, along its way since the frame before: what it meets is pushed off its face.
        private static void Hand(Rigidbody player, Rigidbody heldLeft, Rigidbody heldRight, Joints arm)
        {
            var input = side == 0 ? VrInput.Left : VrInput.Right;
            var palm = Palm(input, arm.Palm);
            if (palm.sqrMagnitude < 0.5f) return;
            var before = arm.Palm.sqrMagnitude > 0.5f ? arm.Palm : palm;
            var scale = VrCamera.WorldScale;
            var speed = (Speed[2] + Speed[3]) * 0.5f - player.velocity;
            var fast = (Speed[3] - player.velocity).magnitude > HandFrom * scale;
            var way = (Now[2] + Now[3] - arm.At[2] - arm.At[3]) * 0.5f;
            var steps = Mathf.Clamp(Mathf.CeilToInt(way.magnitude / (2f * HalfThick * scale)), 1, MostSteps);
            Met.Clear();
            for (var s = 1; s <= steps; s++)
            {
                var f = (float)s / steps;
                var wrist = Vector3.Lerp(arm.At[2], Now[2], f);
                var tips = Vector3.Lerp(arm.At[3], Now[3], f);
                var along = tips - wrist;
                if (along.sqrMagnitude < 1e-6f) continue;
                var face = Vector3.ProjectOnPlane(Vector3.Slerp(before, palm, f), along);
                if (face.sqrMagnitude < 1e-6f) continue;
                face.Normalize();
                var center = (wrist + tips) * 0.5f;
                var turn = Quaternion.LookRotation(along, face);
                var half = new Vector3(HalfWidth * scale, HalfThick * scale, along.magnitude * 0.5f);
                var count = Physics.OverlapBoxNonAlloc(center, half, Found, turn, ~0, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < count; i++)
                {
                    var body = Found[i].attachedRigidbody;
                    if (!Pushable(body, player, heldLeft, heldRight) || Found[i].transform.IsChildOf(player.transform) || !Met.Add(body)) continue;
                    // Off the face that meets it; edge-on or sliding along, the way the hand goes.
                    var going = speed.sqrMagnitude > 1e-6f ? speed.normalized : face;
                    var facing = Vector3.Dot(going, face);
                    var outward = Mathf.Abs(facing) >= FaceWithin ? face * Mathf.Sign(facing) : going;
                    var contact = Found[i].ClosestPoint(center);
                    if (body.mass > LightestPushed) StrikeOne(Found[i], body, contact, outward, Vector3.Dot(palm, outward), speed + player.velocity);
                    else if (fast) Knock(player, Found[i], body, contact, outward, speed, Vector3.Dot(palm, outward) > 0.3f ? "palm" : Vector3.Dot(palm, outward) < -0.3f ? "back of the hand" : "side of the hand");
                }
            }
        }

        // The hand comes into a heavier part: a push as of the hand's mass at its speed into it, by how the palm faces it.
        private static void StrikeOne(Collider collider, Rigidbody body, Vector3 contact, Vector3 outward, float facing, Vector3 handVelocity)
        {
            var first = TouchedNow[side].Add(body);
            if (!first || Touched[side].Contains(body) || body.mass > HeaviestStruck) return;
            var input = side == 0 ? VrInput.Left : VrInput.Right;
            var scale = VrCamera.WorldScale;
            var speed = Vector3.Dot(handVelocity - body.GetPointVelocity(contact), outward);
            if (speed < StrikeFrom * scale) return;
            var share = Mathf.Lerp(BackShare, 1f, Mathf.InverseLerp(-0.3f, 0.7f, facing));
            var change = Mathf.Min(HandMass * speed * share / body.mass, speed * Share);
            body.AddForceAtPosition(outward * (change * body.mass), contact, ForceMode.Impulse);
            input.Buzz(Mathf.Lerp(0.3f, 0.9f, share * Mathf.InverseLerp(StrikeFrom * scale, 4f * scale, speed)), 0.06f);
            if (logs++ < 40)
                Plugin.Log.LogInfo($"Arm push: {input.Name} hand strikes '{collider.name}' ('{body.name}' {body.mass:0.0} kg) at {speed:0.00} m/s with the "
                    + $"{(facing > 0.3f ? "palm" : facing < -0.3f ? "back of the hand" : "side of the hand")} ({share:0.00} of a strike), {change:0.000} m/s more");
        }
    }
}
