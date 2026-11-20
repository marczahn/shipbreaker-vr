using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace ShipbreakerVR
{
    // Holding on to anything and climbing along it with the hands, as Marc asked: with one hand, with both, or with one
    // while the other works a tool. The grip of a hand that holds no tool and is not at the tool belt (VrHand.Free),
    // pressed with a surface within reach of the controller, holds on right where the hand is: that point stays fixed on
    // the thing held and moves with it, and the hand does not drift from it. Moving the hand then moves the player and the
    // thing held against each other, shared by their masses, as two bodies pushing each other would: the station, a fixed
    // hull or anything the game counts as too heavy to move (the static layer, kinematic bodies, masses above
    // GrabController.RawGrabbedMassRange, as the game's own grab decides) does not move at all, so the hand pulls the
    // player along it; a light part comes to the hand instead. With both hands holding, each pulls with half the player's
    // mass, so the body settles between them and hand over hand climbs. Letting go keeps the speed the hands gave the
    // body, so a push off flies on. While the left hand holds on, controller flying rests (VrControls).
    // The grip always holds on, also at the game's controls (airlock consoles, panels); working them is A or X, as Marc
    // asked (VrToolInput).
    // Each physics step (VrCamera.FixedUpdate) the player's velocity, and the thing's at the hold, is set so the hand
    // reaches its hold by the end of the step: an impulse between the two, as a joint would apply, without joints.
    // Pulling up close must not hit what is in the way: that velocity ignores the body's colliders, and each new contact
    // is an impact to the game (PlayerMotion.OnCollisionEnter: sound, sparks, shake and damage). So a sweep along the
    // velocity stops the body just short of anything ahead (KeepClear), and what still touches the thing held at low
    // speed is no impact (Soft, ImpactPatch). The colliders stay on, so the body does not go through it.
    // Steuerung/SchwereTeileSchieben: with one hand braced on something fixed, the other works a heavy part with a limited
    // force (Pushes, BracedPush), less the heavier it is: it gathers speed slowly, keeps it, and is out of reach when the
    // hold tears off. With both hands on two heavy parts they push each other apart, the body between them, shared by the
    // masses.
    internal static class VrClimb
    {
        static VrClimb() => VrSession.OnUnload("climbing", Release);

        // How far from the controller a surface may be to hold on, in meters at world scale 1.
        private const float Reach = 0.15f;
        // A control's collider wins over the nearest surface only up to this much farther away, in meters at world scale
        // 1: a hand climbing past a switch keeps the wall.
        private const float ControlPreference = 0.05f;
        // A grip pressed just before the hand arrives still catches, for this many seconds: a moving hand misses less.
        private const float CatchTime = 0.25f;
        // How long the game's hand takes to come to the surface it caught, in seconds.
        private const float SnapTime = 0.1f;
        // A hand held back this far from its hold (the part torn away) lets go. Not while the body rests against what is
        // ahead of it: there the arm may reach as far as it wants, the game's hand stays on the hold (VrHands.Pose) and
        // nothing pushes the body until the real hand has come back past the hold.
        private const float TearOff = 0.5f;
        // How fast the hold may pull the body, and how fast a push off may fly, in m/s at world scale 1.
        private const float MaxPull = 10f;
        // A part that moves is pulled more softly than a fixed hold: its gap to the hand closes over PartPullTime, not
        // within one physics step, and each step's correction goes PartRelax of the way. Closed in one step, each
        // centimeter of a trembling hand became half a meter per second between body and part, the two hands on one
        // crate worked against each other through its turning, and a 56 kg crate swung and threw the body around: some
        // 1800 kg m/s went back and forth between them in a second for 90 of real change (Raik). The suit also calms a
        // held part's turning (PartSpinCalm, seconds), so a turning crate does not drag the body over it: the suit's
        // control wins over the momentum, which the pulls still share as before.
        private const float PartPullTime = 0.15f;
        private const float PartRelax = 0.5f;
        private const float PartSpinCalm = 0.3f;
        // The suit calms a turning part with at most this torque, in N m: a crate within a fraction of a second, a piece of
        // several tonnes hardly (it stopped those as fast before).
        private const float CalmTorque = 150f;
        // The pulls are worked out this many times a step, each from the velocities the one before left.
        private const int PullPasses = 2;
        private const float MaxPushOff = 5f;
        // The speed a push off keeps is the hands' speed, smoothed over about this time in seconds.
        private const float PushOffSmoothing = 0.08f;
        // How far the body stays from what is ahead of it while holding on, in meters at world scale 1.
        private const float Clearance = 0.04f;
        // The head's size for that, in meters at world scale 1.
        private const float HeadRadius = 0.1f;
        // Up to this speed, in m/s at world scale 1, touching the thing held is no impact.
        private const float SoftContact = 3f;
        // Parts up to this mass, in kg, sit firmly in the hand: they turn with it. Heavier ones turn freely on the hold.
        private const float FirmMass = 30f;
        // How fast a firmly held part may turn, in radians per second.
        private const float FirmSpin = 30f;
        // How much of the way to the hand a firmly held part goes each step, the lightest (FirmFollow) to the heaviest
        // (FirmFollowHeavy): heavier parts lag behind the hand more and more. All of it, as the body does, made them shake.
        private const float FirmFollow = 0.5f;
        private const float FirmFollowHeavy = 0.08f;
        // A part let go collides with the body again once its bounds are this far from the body, or after that many seconds.
        private const float FirmClear = 0.45f;
        // At most this long, in seconds: the next touch of a part let go can come soon (Raik; 10 s was too long).
        private const float FirmClearTime = 3f;
        // The body's mass in the pulls and pushes with extended world physics (Steuerung/SchwereTeileSchieben), in kg: the game's
        // player weighs 50 kg, light for a person in a suit; Raik wants 100 kg (modern materials). Only in these impulses:
        // the rigidbody keeps the game's mass, so thrust, braking and the speed of a push off stay as they were. Without
        // extended world physics the game's mass, as before.
        private const float ExtendedBodyMass = 100f;
        private static float BodyMass => VrSettings.BracedPush.Value ? ExtendedBodyMass : player.mass;
        // Raik, 0.30.1: with realistic climbing the body (ClimbContact.BrakeMass) has inertia (Pull). On a fixed hold each
        // arm speeds the body up with VrSettings.ArmPull and slows it down with VrSettings.CatchForce, both only as far as
        // the hand asks for it: a hand that takes hold and keeps moving back with the body (hand over hand) keeps the
        // body's speed and may push it on; a hand held still against the body stops it, gliding on a little; past what
        // the arm can stop (TearOff) the hand is torn off and the body flies on. No Swing or Stretch then.
        private static bool Realistic => VrSettings.ClimbTurning != null && VrSettings.ClimbTurning.Value == Turning.Realistisch;
        // What each hand changed the body's velocity by this step (Pull), within ArmPull.
        private static readonly Vector3[] PullApplied = new Vector3[2];
        // The hands' motion against the body is smoothed over about this time, in seconds: the controllers are read once a
        // frame, the pulls worked out once a physics step, so the raw motion jumps between steps.
        private const float MotionSmoothing = 0.04f;
        private static int inertiaLogs;
        // LynxUnityEngineExtensions.InStaticLayer: the station and other things that never move.
        private const int StaticLayer = 27;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");

        private static readonly AccessTools.FieldRef<PlayerMotion, bool> SquishyCollide =
            AccessTools.FieldRefAccess<PlayerMotion, bool>("m_SquishyCollideEnabled");

        private static readonly System.Reflection.MethodInfo RawGrabbedMassRange =
            AccessTools.PropertyGetter(typeof(GrabController), "RawGrabbedMassRange");

        private class Hand
        {
            public readonly VrHand Vr;
            public readonly VrInput.Hand Input;
            // The grip as this hand last saw it: the physics may step several times in a frame, or not at all.
            public bool GripDown;
            public bool Tracked;
            // The controller's position in tracking space this physics step.
            public Vector3 Local;
            public Collider Collider;
            // The rigidbody of what is held, if it has one; Movable when the hand may move it.
            public Rigidbody Body;
            public bool Movable;
            // Above what the game lets one grab (TooHeavy): moved by the hands only with extended world physics.
            public bool Heavy;
            // The hold: a point fixed on Anchor (the body, or the collider's transform without one).
            public Transform Anchor;
            public Vector3 Point;
            // How far the pull point (PullPoint) sat from the controller at taking hold, in tracking space.
            public Vector3 PullOffset;
            // Where the grip was pressed last, for CatchTime.
            public float PressedAt = -1f;
            // The surface under the hand when it took hold, on Anchor: the game's hand goes there, the body keeps to Point.
            public Vector3 Surface;
            // The surface's outward direction there, on Anchor: a flat hand lies on it (VrHands).
            public Vector3 Normal;
            // On a control (NearestControl), where the hand took hold of it: the hand makes a fist there.
            public bool Control;
            // Pinned (VrLever): the body keeps to this point on PinAnchor, which does not move, while the lever turns the
            // hand on Anchor (the game turning it back with the hand still on it).
            public Transform PinAnchor;
            public Vector3 Pin;
            // Free (VrNacelle): the hand stays on its hold, which a switch moves, but neither pulls at the body nor is torn
            // off the hold however far its controller goes; it turns freely on it (VrHands).
            public bool Free;
            // Held free and turning with the controller on the hold (the nacelle switch), not with the hold (a lever).
            public bool FreeTurn;
            // On a lever's grip (VrLever.OnGrip): the grip's direction on Anchor, the fist closes around it (VrHands).
            public Vector3 Bar;
            // A part that turns with the hand: its turn relative to the controller, and its speed limit before.
            public bool Firm;
            // A small part (Small): held as a fist round it; anything larger the hand lies on (HoldsFlat).
            public bool Small;
            // The second hand on a firmly held part: it stays on the part, the first hand moves it.
            public bool Follower;
            public Quaternion FirmTurn;
            public float SpinBefore;
            public RigidbodyInterpolation InterpolationBefore;
            public float HeldAt;
            // Sliding along a large surface (Slide): the body's turn the hold counts from, the way from and to, and how
            // often it slid (VrHands takes the hand's turn anew then).
            public Quaternion TurnAtHold;
            public bool Sliding;
            public float SlideAt;
            public Vector3 SlideFromPoint, SlideToPoint, SlideFromSurface, SlideToSurface;
            public int Slides;
            // Gliding along a fixed surface (Glide): how fast the hold moves along it, in world space.
            public Vector3 GlideVelocity;
            // Realistic climbing (Pull): where the pull point was off the body the step before, and how fast it moves
            // against the body, smoothed (the hand going back along the body, hand over hand).
            public Vector3 OffsetBefore;
            public bool OffsetKnown;
            public Vector3 Motion;
            public bool MotionKnown;
            // Held on an edge or a bar (a handle, a pillar's corner): its line on Anchor, along which the hold glides;
            // zero on a face.
            public Vector3 EdgeLine;
            // Taken hold of an edge too fast to stop at once (VrClimb.Stretch): the hand stays on the edge while the body
            // goes on, slowed by the arm, the way across it it goes (world); drawn back softly until SoftUntil once caught.
            public bool Stretch;
            public Vector3 StretchWay;
            // How hard the arm stops the body meanwhile (m/s²), and whether it is only the swing after taking hold (Swing),
            // which never lets go.
            public float StretchBrake;
            public bool StretchSwing;
            public float SoftUntil;
            // The way the hold glided in the last physics step (in the anchor's axes) and that step's time: the hand is
            // shown that far behind, less how far the frame is into the next step (TryGetHold).
            public Vector3 GlideStep;
            public float GlideStepAt;

            public Hand(VrHand vr)
            {
                Vr = vr;
                Input = vr.Input;
            }

            public string Name => Input.Name;
            // Holding on is the hand's state (VrHand), so no tool goes into it meanwhile.
            public bool Holding => Vr.State == HandState.Holding;
        }

        private static readonly Hand LeftHand = new Hand(VrHand.Left);
        private static readonly Hand RightHand = new Hand(VrHand.Right);
        private static readonly Hand[] Hands = { LeftHand, RightHand };

        // A part let go that still sits in the body: it collides with it again once it is clear.
        private struct Clearing
        {
            public Rigidbody Body;
            public Collider[] Parts;
            public float Until;
        }

        private static readonly System.Collections.Generic.List<Clearing> clearing = new System.Collections.Generic.List<Clearing>();
        private static readonly Collider[] Found = new Collider[32];
        private static readonly Vector3[] Around = BuildAround();

        private static GrabController grab;
        private static Rigidbody player;
        private static PlayerMotion motion;
        private static SphereCollider bodySphere;
        // The game's squishy collide as it was before the first hand took hold, while it is off for holding on.
        private static bool squishyWas;
        private static bool squishyOff;
        private static float nextSearch;
        // Where the body was when the rig was placed last (RigPlaced).
        private static Vector3 bodyAtRig;
        private static bool rigPlaced;
        // The body's speed relative to its holds, smoothed, and the holds' own speed, for the push off.
        private static Vector3 pushOff;
        private static Vector3 holdVelocity;
        private static int logs;
        private static int contactLogs;
        private static int impactLogs;
        private static int switchLogs;
        // Whether KeepClear held the body back in the last step.
        private static bool blocked;

        // From VrCamera.FollowGameCamera, right after the rig was placed for the frame. The hands are measured on the rig,
        // which moves once per frame; in the physics steps between they move along by as far as the body has come since.
        public static void RigPlaced()
        {
            if (player == null) return;
            bodyAtRig = player.transform.position;
            rigPlaced = true;
            foreach (var hand in Hands) Track(hand);
        }

        // Throwing (Raik, 2026-11-08): a part let go flew off with the speed of the last physics step, so a wrist that
        // tips at the moment of letting go sent it anywhere. The controller's path over the last ThrowWindow seconds
        // gives its speed and way instead, an average over a few frames; tiny debris gets more of it (ClimbContact.ThrowGain).
        private const float ThrowWindow = 0.06f;
        private const int Tracked = 16;

        private class Path
        {
            public readonly Vector3[] At = new Vector3[Tracked];
            public readonly Quaternion[] Turn = new Quaternion[Tracked];
            public readonly float[] Time = new float[Tracked];
            public int Count, Next;
        }

        private static readonly System.Collections.Generic.Dictionary<VrInput.Hand, Path> Paths = new System.Collections.Generic.Dictionary<VrInput.Hand, Path>();

        private static void Track(Hand hand)
        {
            if (!Paths.TryGetValue(hand.Input, out var path)) Paths[hand.Input] = path = new Path();
            if (!hand.Input.TryGetPosition(out var local) || !hand.Input.TryGetAimRotation(out var turn)) { path.Count = 0; return; }
            path.At[path.Next] = local;
            path.Turn[path.Next] = turn;
            path.Time[path.Next] = Time.unscaledTime;
            path.Next = (path.Next + 1) % Tracked;
            path.Count = Mathf.Min(path.Count + 1, Tracked);
        }

        // The controller's average speed and spin over the window, in world space relative to the body; false with too few
        // frames.
        private static bool HandSpeed(Hand hand, out Vector3 speed, out Vector3 spin)
        {
            speed = spin = Vector3.zero;
            if (!Paths.TryGetValue(hand.Input, out var path) || path.Count < 2) return false;
            var newest = (path.Next + Tracked - 1) % Tracked;
            var oldest = newest;
            for (var i = 1; i < path.Count; i++)
            {
                var each = (newest + Tracked - i) % Tracked;
                oldest = each;
                if (path.Time[newest] - path.Time[each] >= ThrowWindow) break;
            }
            var span = path.Time[newest] - path.Time[oldest];
            if (span <= 1e-4f) return false;
            var rig = VrCamera.Instance.transform.parent;
            speed = rig.TransformVector((path.At[newest] - path.At[oldest]) / span);
            (path.Turn[newest] * Quaternion.Inverse(path.Turn[oldest])).ToAngleAxis(out var angle, out var axis);
            if (angle > 180f) angle -= 360f;
            if (!float.IsInfinity(axis.x) && Mathf.Abs(angle) > 0.01f) spin = rig.rotation * axis * (angle * Mathf.Deg2Rad / span);
            return true;
        }

        // A firmly held part let go by its last hand flies off as the hand moved it, averaged over the window: it spins as
        // the hand turned, and its middle goes as a point of the hand there would, so the part where the hand held it goes
        // at the hand's speed (Raik: a crate held at its edge flew as if thrown from its middle, the held edge slower).
        private static void Throw(Hand hand, Rigidbody body)
        {
            if (body == null || player == null || body.isKinematic || !HandSpeed(hand, out var speed, out var spin)) return;
            var gain = ClimbContact.ThrowGain(body.mass);
            var before = body.velocity - player.velocity;
            var lever = body.worldCenterOfMass - HandWorld(hand);
            var middle = (speed + Vector3.Cross(spin, lever)) * gain;
            // Test (Raik, 2026-11-14): no faster than the arm's force over a throw can make it (ClimbContact.MostThrowSpeed).
            var allowed = ClimbContact.MostThrowSpeed(body.mass) * gain * VrCamera.WorldScale;
            var wanted = middle.magnitude;
            middle = Vector3.ClampMagnitude(middle, allowed);
            body.velocity = player.velocity + middle;
            body.angularVelocity = spin;
            if (logs++ < 80)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand throws '{body.name}' ({body.mass:0.0} kg) at {middle.magnitude:0.00} m/s, spinning {spin.magnitude * Mathf.Rad2Deg:0}°/s "
                    + $"(hand {speed.magnitude:0.00} m/s over {ThrowWindow * 1000f:0} ms, held {lever.magnitude:0.00} m from its middle, x{gain:0.00}; "
                    + $"the last step had {before.magnitude:0.00} m/s, {Vector3.Angle(before, middle):0}° off"
                    + (wanted > allowed + 0.01f ? $"; the hand's {wanted:0.00} m/s held to {allowed:0.00} by the arm's force)" : ")"));
        }

        // From VrCamera.FixedUpdate, before the physics step.
        public static void FixedUpdate()
        {
            ClearParts(false);
            if (!Usable())
            {
                LetGoAll();
                return;
            }
            foreach (var hand in Hands) Read(hand);
            foreach (var hand in Hands) Rethink(hand);
            foreach (var hand in Hands) Slide(hand);
            Glide();
            foreach (var hand in Hands) Stretch(hand);
            var start = Momentum.Begin();
            Pull();
            Momentum.End(start);
        }

        // Diagnostics for a light part pulled with the hands (Raik: rowing a 60 kg barrel with both arms still gathers
        // speed, which two bodies pushing each other cannot do): the momentum of body and part together, how much of its
        // change comes from the pulls here (should be none but what KeepClear takes away), and how much from elsewhere
        // between two steps (the game, collisions). Logged each second while it lasts, the first few times.
        private static class Momentum
        {
            private static Rigidbody part;
            private static bool predicted;
            private static Vector3 expectedBody;
            private static Vector3 expectedPart;
            private static Vector3 ours;
            private static Vector3 onBody;
            private static Vector3 onPart;
            private static Vector3 since;
            private static int steps;
            private static int touching;
            private static float nextLog;
            private static int logs;
            private static readonly Collider[] Near = new Collider[16];

            // The part and the momentum now, or null without one.
            public static Vector3? Begin()
            {
                part = null;
                foreach (var hand in Hands)
                    if (hand.Holding && hand.Movable && !hand.Firm && hand.Body != null) part = hand.Body;
                if (part == null || logs >= 80)
                {
                    predicted = false;
                    return null;
                }
                var body = BodyMass * player.velocity;
                var held = part.mass * part.velocity;
                if (predicted)
                {
                    // Between two steps: what changed the body's momentum and the part's, apart from the pulls.
                    onBody += body - expectedBody;
                    onPart += held - expectedPart;
                }
                else
                {
                    ours = onBody = onPart = Vector3.zero;
                    steps = touching = 0;
                    since = body + held;
                    nextLog = Time.time + 1f;
                }
                steps++;
                if (Touches()) touching++;
                return body + held;
            }

            public static void End(Vector3? start)
            {
                if (start == null || part == null) return;
                expectedBody = BodyMass * player.velocity;
                expectedPart = part.mass * part.velocity;
                predicted = true;
                var total = expectedBody + expectedPart;
                ours += total - start.Value;
                if (Time.time < nextLog) return;
                nextLog = Time.time + 1f;
                logs++;
                var way = total.sqrMagnitude > 1e-6f ? total.normalized : Vector3.zero;
                Plugin.Log.LogInfo($"Climbing momentum: body {BodyMass:0} kg at {player.velocity.magnitude:0.00} m/s, '{part.name}' {part.mass:0} kg at {part.velocity.magnitude:0.00} m/s; "
                    + $"together {total.magnitude:0.0} kg m/s ({(total - since).magnitude:0.0} changed in a second), from the pulls {ours.magnitude:0.0}; "
                    + $"from elsewhere on the body {onBody.magnitude:0.0} ({Vector3.Dot(onBody, way):0.0} along the drift), on the part {onPart.magnitude:0.0} ({Vector3.Dot(onPart, way):0.0} along); "
                    + $"body touching the part in {touching} of {steps} steps");
                ours = onBody = onPart = Vector3.zero;
                steps = touching = 0;
                since = total;
            }

            // The part within 2 cm of the body's solid sphere.
            private static bool Touches()
            {
                BodyBall(out var center, out var radius);
                var count = Physics.OverlapSphereNonAlloc(center, radius + 0.02f, Near, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < count; i++)
                    if (Near[i].attachedRigidbody == part) return true;
                return false;
            }
        }


        // A hand flat on a large fixed surface (a hull) while the body turns on with the controls: past SlideAfter degrees
        // from where it took hold, the hold slides along the surface to under the hand, over SlideTime, so the hand comes
        // after the body instead of staying behind with the arm wound round (Raik: holding a wall and turning all the way).
        private const float SlideAfter = 100f;
        private const float SlideTime = 0.35f;
        // Not for less than this, in meters at world scale 1.
        private const float SlideLeast = 0.05f;
        private static int slideLogs;

        private static void Slide(Hand hand)
        {
            if (!hand.Holding || hand.Movable || hand.Firm || hand.Follower || hand.Control || hand.Anchor == null || hand.Collider == null) return;
            if (hand.Sliding)
            {
                var t = Mathf.Clamp01((Time.time - hand.SlideAt) / SlideTime);
                var eased = t * t * (3f - 2f * t);
                hand.Point = Vector3.Lerp(hand.SlideFromPoint, hand.SlideToPoint, eased);
                hand.Surface = Vector3.Lerp(hand.SlideFromSurface, hand.SlideToSurface, eased);
                if (t >= 1f) hand.Sliding = false;
                return;
            }
            var playspace = VrCamera.Instance.Playspace;
            // Turning, not rolling: the way the body faces, which a roll about it keeps (VrHands turns the hand along on
            // a roll, RollAlong).
            if (Vector3.Angle(playspace * Vector3.forward, hand.TurnAtHold * Vector3.forward) < SlideAfter) return;
            hand.TurnAtHold = playspace;
            // Where the hand is now, taken onto the surface's plane and found on the surface there.
            var surface = hand.Anchor.TransformPoint(hand.Surface);
            var normal = hand.Anchor.TransformDirection(hand.Normal).normalized;
            var along = Vector3.ProjectOnPlane(HandWorld(hand) - surface, normal);
            var scale = VrCamera.WorldScale;
            if (along.magnitude < SlideLeast * scale) return;
            var probe = Reach * scale;
            if (!hand.Collider.Raycast(new Ray(surface + along + normal * probe, -normal), out var hit, 2f * probe)) return;
            var moved = hand.Anchor.InverseTransformPoint(hit.point) - hand.Surface;
            hand.SlideFromPoint = hand.Point;
            hand.SlideFromSurface = hand.Surface;
            hand.SlideToPoint = hand.Point + moved;
            hand.SlideToSurface = hand.Surface + moved;
            hand.Sliding = true;
            hand.SlideAt = Time.time;
            hand.Slides++;
            if (slideLogs++ < 20)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand slides {along.magnitude:0.00} m along '{hand.Collider.name}' after the body turned {SlideAfter:0}°");
        }

        // Holding a fixed surface, the hold glides along it (Raik: gliding along a ship's hull with a hand on it): taking
        // hold at speed, the body slides on and brakes to a stop within a short way (ClimbContact.Brake); thrust or the
        // brake along the surface speeds it up or slows it, without friction while thrusting. The pull still holds the body
        // at the hand's distance from the surface, also against thrust pointing away from it. Each step the hold moves by
        // the glide and is found on the surface again by a ray; past an edge it stops. Only while every pulling hand can
        // glide: one on a part that moves, or turning it, holds the body.
        private static int glideLogs;
        // The body's velocity as Pull left it, valid while pulling: what the physics step added since is the thrust.
        private static Vector3 afterPull;
        private static bool pulled;

        private static bool Glides(Hand hand) =>
            hand.Holding && !hand.Sliding && !hand.Movable && !hand.Firm && !hand.Follower && !hand.Control && hand.Anchor != null && hand.Collider != null;

        // Raik, 0.30.1: a glide brakes evenly with the gloves' force (ClimbContact.GloveBrake), so the faster one takes hold,
        // the further one glides. It goes on over the seams between hull parts onto the next fixed part, which takes a
        // little speed (SeamLoss); a step up or a wall ahead catches the hand like a handle, only the speed across it
        // counting (LipForce): within that the hand stays at it and glides along it, faster it goes over, slowed. Where the
        // surface ends or falls away by more than DropAngle the hand cannot hold on: it lets go and the body flies on
        // with what speed is left, unless it has nearly stopped. On an edge or a bar (EdgeLine) the hold glides along its
        // line and lets go at its end.
        private const float DropAngle = 30f;
        // In meters at world scale 1: a step up from StepLeast, a step down up to StepDownMost glided over as a seam; a
        // step or wall is looked for this high over the surface; an edge's line followed while it stays within EdgeKeep.
        private const float StepLeast = 0.01f;
        private const float StepDownMost = 0.03f;
        private const float StepProbe = 0.006f;
        private const float EdgeKeep = 0.02f;
        // A face rising ahead more steeply than DropAngle is a step or a wall (the sine of it).
        private static readonly float Rising = Mathf.Sin(DropAngle * Mathf.Deg2Rad);
        private static readonly RaycastHit[] GlideHits = new RaycastHit[16];
        private static int edgeLogs;

        private static void Glide()
        {
            var any = false;
            var all = true;
            var gliding = 0;
            foreach (var hand in Hands)
            {
                if (!hand.Holding || hand.Firm || hand.Follower || Pushes(hand)) continue;
                if (Glides(hand))
                {
                    any = true;
                    gliding++;
                }
                else all = false;
            }
            var steered = VrControls.Thrusting || VrControls.IsBraking();
            var added = pulled && steered ? player.velocity - afterPull : Vector3.zero;
            pulled = false;
            var scale = VrCamera.WorldScale;
            var brake = ClimbContact.GloveBrake(VrSettings.GloveBrake.Value, gliding, scale);
            foreach (var hand in Hands)
            {
                if (!any || !all || !Glides(hand)) { hand.GlideVelocity = Vector3.zero; continue; }
                var surface = hand.Anchor.TransformPoint(hand.Surface);
                var normal = hand.Anchor.TransformDirection(hand.Normal).normalized;
                var edge = hand.EdgeLine != Vector3.zero ? hand.Anchor.TransformDirection(hand.EdgeLine).normalized : Vector3.zero;
                var before = hand.GlideVelocity;
                var velocity = edge != Vector3.zero ? Vector3.Project(before + added, edge) : Vector3.ProjectOnPlane(before + added, normal);
                if (VrControls.Thrusting) velocity = ClimbContact.ThrustGlide(velocity, before.magnitude, brake, Time.fixedDeltaTime, scale);
                else velocity = Vector3.Dot(velocity, before) < 0f ? Vector3.zero : ClimbContact.Brake(velocity, brake, Time.fixedDeltaTime);
                hand.GlideVelocity = velocity;
                if (velocity == Vector3.zero) continue;
                // The glove rubs on the hull: the faster, the more.
                hand.Input.Buzz(Mathf.Lerp(0.05f, 0.35f, Mathf.Clamp01(velocity.magnitude / (4f * scale))), 0.03f);
                if (edge != Vector3.zero) GlideEdge(hand, surface, normal, velocity);
                else GlideFace(hand, surface, normal, velocity);
                if (hand.Holding && glideLogs++ < 10)
                    Plugin.Log.LogInfo($"Climbing: {hand.Name} hand glides along '{hand.Collider.name}'{(edge != Vector3.zero ? " on its edge" : "")} at {hand.GlideVelocity.magnitude:0.00} m/s{(VrControls.Thrusting ? " with the thrust" : "")}");
            }
        }

        private static void GlideFace(Hand hand, Vector3 surface, Vector3 normal, Vector3 velocity)
        {
            var scale = VrCamera.WorldScale;
            var dt = Time.fixedDeltaTime;
            var way = velocity.normalized;
            var probe = Reach * scale;
            var along = velocity * dt;
            // A step or a wall ahead, its face against the way.
            if (SurfaceRay(surface + normal * StepProbe * scale, way, along.magnitude + 0.01f * scale, out var front) && Vector3.Dot(front.normal, way) < -Rising)
            {
                Step(hand, surface, normal, velocity, front.normal, front.collider);
                return;
            }
            if (!SurfaceRay(surface + along + normal * probe, -normal, 2f * probe, out var hit))
            {
                Over(hand, velocity, "past the edge of the surface");
                return;
            }
            var height = Vector3.Dot(hit.point - (surface + along), normal);
            if (height < -StepDownMost * scale || (Vector3.Angle(hit.normal, normal) > DropAngle && Vector3.Dot(hit.normal, way) > 0f))
            {
                Over(hand, velocity, $"the surface falls away ({height * 100f:0} cm, {Vector3.Angle(hit.normal, normal):0}°)");
                return;
            }
            if (height > StepLeast * scale)
            {
                Step(hand, surface, normal, velocity, -way, hit.collider);
                return;
            }
            if (hit.collider != hand.Collider)
            {
                velocity = ClimbContact.Lose(velocity, ClimbContact.SeamLoss * scale);
                hand.GlideVelocity = velocity;
                hand.Input.Buzz(0.3f, 0.04f);
                if (edgeLogs++ < 30) Plugin.Log.LogInfo($"Climbing: {hand.Name} hand glides over a seam onto '{hit.collider.name}', {velocity.magnitude:0.00} m/s left");
                if (velocity == Vector3.zero) return;
            }
            MoveHold(hand, surface, hit.point, hit.normal, hit.collider);
        }

        // A step up or a wall ahead (its face facing face): caught as on a handle by the speed across it, then gliding
        // along it; faster, over it, slowed, onto what lies on top, or letting go where there is nothing to glide onto.
        private static void Step(Hand hand, Vector3 surface, Vector3 normal, Vector3 velocity, Vector3 face, Collider thing)
        {
            var scale = VrCamera.WorldScale;
            var across = Mathf.Max(-Vector3.Dot(velocity, face), 0f);
            var limit = ClimbContact.CatchLimit(ClimbContact.LipForce, ClimbContact.LipStroke, scale);
            if (ClimbContact.Catch(across, limit, out var left))
            {
                var on = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(velocity, face), normal);
                hand.GlideVelocity = on.magnitude < ClimbContact.GlideLeast * scale ? Vector3.zero : on;
                hand.Input.Buzz(0.6f, 0.06f);
                if (edgeLogs++ < 30)
                    Plugin.Log.LogInfo($"Climbing: {hand.Name} hand caught at a step on '{thing.name}', {across:0.00} m/s across (holds {limit:0.00}), glides on along it at {hand.GlideVelocity.magnitude:0.00} m/s");
                return;
            }
            velocity += face * (across - left);
            hand.GlideVelocity = velocity;
            hand.Input.Buzz(0.8f, 0.08f);
            var probe = Reach * scale;
            if (!SurfaceRay(surface + velocity * Time.fixedDeltaTime + normal * probe, -normal, 2f * probe, out var top)
                || Vector3.Angle(top.normal, normal) > DropAngle || Vector3.Dot(top.point - surface, normal) < StepLeast * scale)
            {
                Over(hand, velocity, $"over a step on '{thing.name}' at {across:0.00} m/s across, too high to glide onto");
                return;
            }
            if (edgeLogs++ < 30)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand goes over a step onto '{top.collider.name}' at {across:0.00} m/s across, {left:0.00} left");
            MoveHold(hand, surface, top.point, top.normal, top.collider);
        }

        private static void GlideEdge(Hand hand, Vector3 surface, Vector3 normal, Vector3 velocity)
        {
            var scale = VrCamera.WorldScale;
            var next = surface + velocity * Time.fixedDeltaTime;
            var on = Closest(hand.Collider, next + normal * 0.05f * scale, Reach * scale);
            if ((on - next).magnitude > EdgeKeep * scale)
            {
                Over(hand, velocity, "the edge ends");
                return;
            }
            MoveHold(hand, surface, on, normal, hand.Collider);
        }

        // Where the surface ends: past it the hand cannot hold on and the body flies on, unless it has all but stopped.
        private static void Over(Hand hand, Vector3 velocity, string why)
        {
            if (velocity.magnitude < ClimbContact.GlideLeast * VrCamera.WorldScale)
            {
                hand.GlideVelocity = Vector3.zero;
                return;
            }
            hand.PressedAt = -1f;
            hand.Input.Buzz(0.5f, 0.05f);
            LetGo(hand, true, $"{why} at {velocity.magnitude:0.00} m/s");
        }

        // The hold moved on to that point of the surface, on the same part or onto the next fixed one.
        private static void MoveHold(Hand hand, Vector3 surface, Vector3 point, Vector3 normal, Collider collider)
        {
            if (collider != hand.Collider)
            {
                var body = collider.attachedRigidbody;
                if (body != null && !body.isKinematic && body.gameObject.layer != StaticLayer && body.mass > 0f)
                {
                    // A part that moves is no hull to glide on: the hand stops at the seam.
                    hand.GlideVelocity = Vector3.zero;
                    return;
                }
                var hold = Hold(hand);
                var shift = point - surface;
                hand.Collider = collider;
                hand.Body = body;
                hand.Anchor = body != null ? body.transform : collider.transform;
                hand.Point = hand.Anchor.InverseTransformPoint(hold + shift);
                hand.Surface = hand.Anchor.InverseTransformPoint(point);
                if (hand.EdgeLine == Vector3.zero) hand.Normal = hand.Anchor.InverseTransformDirection(normal);
                hand.GlideStep = hand.Anchor.InverseTransformVector(shift);
                hand.GlideStepAt = Time.fixedTime;
                hand.Slides++;
                return;
            }
            var moved = hand.Anchor.InverseTransformPoint(point) - hand.Surface;
            hand.GlideStep = moved;
            hand.GlideStepAt = Time.fixedTime;
            hand.Point += moved;
            hand.Surface += moved;
            if (hand.EdgeLine == Vector3.zero) hand.Normal = hand.Anchor.InverseTransformDirection(normal);
        }

        // Raik, 0.30.1 (a 90° edge taken hold of too fast was lost at once, the hand still ahead): from StretchFrom m/s
        // across an edge the hand stays on it and the body goes on past, the arm braking the speed across with
        // HandleForce on BrakeMass (9 m/s²), the hand leaving its controller meanwhile. Stopped before the hold is behind
        // the head, the hand holds and the body is drawn back to the controller over PullBack seconds; else, once the hold
        // has passed the head, the hand lets go and the body flies on slower. Along the edge it glides as ever (Glide).
        private const float StretchFrom = 1f;
        private const float PullBack = 0.25f;
        private const float SoftFor = 1f;
        // Raik, 0.30.1 (first 2, then 3.2, after testing the body's inertia 2 m/s again: above it a little glide leaves time
        // to push off once more with the hand): taking hold at up to HoldsUpTo m/s along a surface the hand holds at once instead of gliding; the
        // body swings on past it, the arm stopping it with SwingBrake m/s² (15 cm from 3.2 m/s, 6 cm from 2, 1.4 from 1), and is
        // drawn back as after a catch on an edge. It never lets go for passing the head.
        private const float HoldsUpTo = 2f;
        private const float SwingBrake = 35f;

        private static void Swing(Hand hand, Vector3 velocity)
        {
            hand.Stretch = true;
            hand.StretchWay = velocity.normalized;
            hand.StretchBrake = SwingBrake * VrCamera.WorldScale;
            hand.StretchSwing = true;
            if (edgeLogs++ < 30) Plugin.Log.LogInfo($"Climbing: {hand.Name} hand holds at once at {velocity.magnitude:0.00} m/s along the surface; the body swings on");
        }

        private static void Stretch(Hand hand)
        {
            if (!hand.Stretch) return;
            if (!hand.Holding)
            {
                hand.Stretch = false;
                return;
            }
            var scale = VrCamera.WorldScale;
            var hold = Hold(hand);
            var holdVelocity = hand.Body != null ? hand.Body.GetPointVelocity(hold) : Vector3.zero;
            var relative = player.velocity - holdVelocity;
            var away = Vector3.Dot(relative, hand.StretchWay);
            if (away <= 0.05f * scale)
            {
                hand.Stretch = false;
                hand.SoftUntil = Time.time + SoftFor;
                player.velocity -= hand.StretchWay * away;
                hand.Input.Buzz(0.5f, 0.06f);
                if (edgeLogs++ < 30) Plugin.Log.LogInfo($"Climbing: {hand.Name} hand stopped the body {(hand.StretchSwing ? "on the surface" : "at the edge")}, {(hold - VrCamera.Instance.transform.position).magnitude:0.00} m from the head; drawn back");
                hand.StretchSwing = false;
                return;
            }
            var slower = Mathf.Max(away - hand.StretchBrake * Time.fixedDeltaTime, 0f);
            player.velocity += hand.StretchWay * (slower - away);
            hand.Input.Buzz(0.4f, 0.03f);
            var head = VrCamera.Instance.transform.position;
            if (hand.StretchSwing || Vector3.Dot(hold - head, hand.StretchWay) >= 0f) return;
            hand.Stretch = false;
            hand.PressedAt = -1f;
            hand.Input.Buzz(1f, 0.12f);
            LetGo(hand, false, $"the edge passed the head, flies on at {slower:0.00} m/s across it");
        }

        // The nearest surface along a ray that is not the player's own.
        private static bool SurfaceRay(Vector3 from, Vector3 way, float length, out RaycastHit found)
        {
            found = default;
            var count = Physics.RaycastNonAlloc(from, way, GlideHits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var best = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var hit = GlideHits[i];
                if (hit.collider == null || Own(hit.collider) || hit.distance >= best) continue;
                // Started inside it: no face found.
                if (hit.distance <= 0f && hit.point == Vector3.zero) continue;
                best = hit.distance;
                found = hit;
            }
            return best < float.MaxValue;
        }

        // Raik, 0.30.1: a hand taking hold of a bar or a handle (thin both ways, HandleThick, and long), or of an edge (the
        // nearest point on a corner between two faces, so the way from it to the hand stands off the face by more than
        // EdgeAngle), holds on to its line.
        private const float HandleThick = 0.12f;
        private const float EdgeAngle = 20f;

        private static bool EdgeAt(Collider collider, Vector3 at, Vector3 surface, out Vector3 line)
        {
            line = Vector3.zero;
            var scale = VrCamera.WorldScale;
            if (Extents(collider, out var size, out var axes))
            {
                var order = new[] { 0, 1, 2 };
                System.Array.Sort(order, (a, b) => size[a].CompareTo(size[b]));
                if (size[order[1]] <= HandleThick * scale && size[order[2]] >= 2f * size[order[1]])
                {
                    line = axes[order[2]];
                    return true;
                }
            }
            var away = at - surface;
            var distance = away.magnitude;
            if (distance < 0.01f * scale) return false;
            var off = away / distance;
            if (!collider.Raycast(new Ray(at, -off), out var face, distance + 0.02f * scale)) return false;
            if (Vector3.Angle(face.normal, off) < EdgeAngle) return false;
            var cross = Vector3.Cross(face.normal, off);
            if (cross.sqrMagnitude < 1e-6f) return false;
            line = cross.normalized;
            return true;
        }

        // A collider's size along its own three axes, in world meters, where it can be told.
        private static bool Extents(Collider collider, out Vector3 size, out Vector3[] axes)
        {
            var t = collider.transform;
            axes = new[] { t.right, t.up, t.forward };
            var scale = new Vector3(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.y), Mathf.Abs(t.lossyScale.z));
            switch (collider)
            {
                case BoxCollider box:
                    size = Vector3.Scale(box.size, scale);
                    return true;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    size = Vector3.Scale(mesh.sharedMesh.bounds.size, scale);
                    return true;
                case CapsuleCollider capsule:
                    var thick = capsule.radius * 2f;
                    size = new Vector3(thick, thick, thick);
                    size[capsule.direction] = Mathf.Max(capsule.height, thick);
                    size = Vector3.Scale(size, scale);
                    return true;
                default:
                    size = Vector3.zero;
                    return false;
            }
        }

        // Taking hold of a fixed surface at speed: the speed along it to glide on with (Glide), none below GlideLeast.
        private static Vector3 GlideAtHold(Hand hand)
        {
            if (!Glides(hand)) return Vector3.zero;
            var normal = hand.Anchor.TransformDirection(hand.Normal).normalized;
            var relative = player.velocity - (hand.Body != null ? hand.Body.GetPointVelocity(Hold(hand)) : Vector3.zero);
            var along = Vector3.ProjectOnPlane(relative, normal);
            return along.magnitude < ClimbContact.GlideLeast * VrCamera.WorldScale ? Vector3.zero : along;
        }

        // How often that hand's hold slid along its surface (VrHands turns the hand anew each time).
        public static int Slides(VrInput.Hand input)
        {
            foreach (var hand in Hands)
                if (hand.Input == input && hand.Holding) return hand.Slides;
            return 0;
        }

        // Whether a part above the game's grab range moves with the hands: with extended world physics, up to
        // BracedPush.MaxMass (see TryHold).
        private static bool HeavyMoves(Rigidbody body) => VrSettings.BracedPush.Value && body.mass <= BracedPush.MaxMass;

        // A heavy part held while extended world physics is switched: it moves, or holds like a wall, from now on, and
        // collides with the body again once apart (review: before, the setting only counted from the next grip).
        private static void Rethink(Hand hand)
        {
            if (!hand.Holding || !hand.Heavy || hand.Body == null) return;
            var moves = HeavyMoves(hand.Body);
            if (moves == hand.Movable) return;
            hand.Movable = moves;
            if (moves) Separate(hand.Body);
            else if (!HeldMoving(hand.Body)) Release(hand.Body);
        }

        public static void Release()
        {
            LetGoAll();
            ClearParts(true);
            grab = null;
            player = null;
            rigPlaced = false;
        }

        // VR in gameplay with the option on, and the player's body found.
        private static bool Usable()
        {
            var vr = VrCamera.Instance;
            if (!VrXr.IsRunning || !VrSettings.Climbing.Value || vr == null || vr.FollowsHab
                || GameSession.CurrentGameState != GameSession.GameState.Gameplay) return false;
            if ((grab == null || player == null) && Time.unscaledTime >= nextSearch)
            {
                nextSearch = Time.unscaledTime + 1f;
                grab = GameRefs.Grab;
                player = grab != null ? PlayerBody(grab) : null;
                motion = player != null ? player.GetComponent<PlayerMotion>() : null;
                rigPlaced = false;
                if (player != null && logs++ < 80)
                    Plugin.Log.LogInfo($"Climbing: player body '{player.name}', {player.mass:0.#} kg ({ExtendedBodyMass:0} kg in the pulls and pushes with extended world physics), interpolation {player.interpolation}");
                if (player != null) LogColliders();
            }
            return player != null && rigPlaced;
        }

        // The controller's grip (VrInput's for the frame) and position this step; a grip pressed starts holding on, one
        // let go stops.
        private static void Read(Hand hand)
        {
            var input = hand.Input;
            hand.Tracked = input.Tracked && input.TryGetPosition(out hand.Local);
            var down = input.Grip.Down;
            var pressed = down && !hand.GripDown;
            if (pressed) hand.PressedAt = Time.time;
            hand.GripDown = down;
            if (hand.Holding)
            {
                if (!down) LetGo(hand, true, "let go");
                else if (!hand.Tracked) LetGo(hand, false, "tracking lost");
                else if (hand.Anchor == null || !hand.Anchor.gameObject.activeInHierarchy || hand.Collider == null || !hand.Collider.enabled)
                    LetGo(hand, false, "the hold is gone");
            }
            else if (hand.Tracked && hand.Vr.Free && down && (pressed || Time.time - hand.PressedAt < CatchTime)) TryHold(hand, pressed);
        }

        // The small floating debris (nuts and bolts, 'PRF_MeshParticles_...') are loose 1 kg parts on the Ignore Raycast
        // layer, which the hands never found (Raik, 2026-11-08): of that layer, loose parts are held too, nothing else.
        private const int IgnoreRaycastLayer = 1 << 2;

        private static bool Holdable(Collider collider)
        {
            if (collider.gameObject.layer != 2) return true;
            var body = collider.attachedRigidbody;
            return body != null && !body.isKinematic;
        }

        // Holds on to the surface nearest to the hand, if one is within reach, right where the hand is.
        private static void TryHold(Hand hand, bool firstTry)
        {
            var at = HandWorld(hand);
            var reach = Reach * VrCamera.WorldScale;
            var count = Physics.OverlapSphereNonAlloc(at, reach, Found, Physics.DefaultRaycastLayers | IgnoreRaycastLayer, QueryTriggerInteraction.Ignore);
            Collider best = null;
            var bestDistance = float.MaxValue;
            var surface = at;
            for (var i = 0; i < count; i++)
            {
                var collider = Found[i];
                if (Own(collider) || !Holdable(collider)) continue;
                var closest = Closest(collider, at, reach);
                var distance = (closest - at).sqrMagnitude;
                if (distance >= bestDistance) continue;
                best = collider;
                bestDistance = distance;
                surface = closest;
            }
            // A control within reach (a door switch, a console) comes first, unless the nearest surface is more than
            // ControlPreference nearer: the hand holds it as a fist (HoldsControl) where it took hold, on the control's own
            // collider. Not on its HandTarget, where the game puts its own hand: on the airlock console's lever that lay
            // 17 cm off the hand, and the hand sat above the lever (Raik). Before that, the hand held the hull under the
            // switch.
            // Controls are found from just above the palm, not from inside the hand: a hand takes hold of what is in front
            // of its palm (Raik).
            var palm = PalmPoint(hand.Input, at);
            var control = NearestControl(palm, reach, out var controlCollider, out var controlPoint);
            // A lever turned by hand is held on its yellow grip, the hand pulled onto its middle, only right at it; else the
            // hand holds the surface there as anywhere (Raik: no fist touching the grip with the backs of the fingers).
            // A thing to collect on the wall (a first aid kit, a tank, a poster, VrPullOff) is held as a surface, the hand
            // on it as on a wall, not as a fist (Raik, 2026-11-11: as on the damaged fuel can, which looked right).
            if (control != null && VrPullOff.Usable(control)) control = null;
            // A device used by its button pushed by hand (an air conditioner, VrButtons) is held as a surface, the hand flat on
            // it as on a wall, not as a fist (Raik, 2026-11-11).
            if (control != null && VrButtons.PressedByButton(control))
            {
                if (firstTry && logs++ < 80) Plugin.Log.LogInfo($"Climbing: {hand.Name} hand at '{control.name}', used by its button: held as a surface");
                control = null;
            }
            var bar = Vector3.zero;
            if (control != null && VrLever.Turnable(control))
            {
                if (VrLever.OnGrip(control, palm, out var onGrip, out bar)) controlPoint = onGrip;
                else if (VrLever.OverHandle(control, palm))
                {
                    if (firstTry && logs++ < 80) Plugin.Log.LogInfo($"Climbing: {hand.Name} hand grips over the lever's turning part, off its grip: holds nothing");
                    return;
                }
                else control = null;
            }
            else if (control != null && VrNacelle.Is(control))
            {
                if (VrNacelle.OnGrip(control, palm, out var onHandle, out bar)) controlPoint = onHandle;
                else control = null;
            }
            if (control != null && best != null
                && (controlPoint - at).magnitude > Mathf.Sqrt(bestDistance) + ControlPreference * VrCamera.WorldScale)
                control = null;
            // Raik (0.30.1): a control without a grip to close round (a door switch, a console, the hoist truck) is held with
            // the flat hand, as on a wall (HoldsFlat); one with only a trigger (the hoist truck's 'PRF_SpaceTruck_ExtractionVolume')
            // is held for its use, the hand lying on the solid surface under it.
            Collider shape = null;
            var shapeSurface = Vector3.zero;
            if (control != null)
            {
                if (bar == Vector3.zero && controlCollider.isTrigger && best != null)
                {
                    shape = best;
                    shapeSurface = surface;
                }
                best = controlCollider;
                surface = controlPoint;
                bestDistance = (surface - at).sqrMagnitude;
            }
            // Some switches have no solid collider, only the trigger the game's interaction uses. It is no surface: the hand
            // stays where it is.
            if (best == null)
            {
                count = Physics.OverlapSphereNonAlloc(at, reach, Found, ~0, QueryTriggerInteraction.Collide);
                for (var i = 0; i < count; i++)
                {
                    var collider = Found[i];
                    if (!collider.isTrigger || Own(collider)) continue;
                    // A lever turned by hand is held on its grip only (Raik: not in the air over it, as on a wall).
                    var volume = collider.GetComponentInParent<InteractableObject>();
                    // Nor a device used by its button (an air conditioner, VrButtons): it has a body of its own to hold, and its
                    // trigger reaches past it (Raik, 2026-11-14: held in the open air near the air conditioner's left edge).
                    if (volume == null || VrLever.Turnable(volume) || VrNacelle.Is(volume) || VrButtons.PressedByButton(volume)) continue;
                    var distance = (VrUtil.ClosestPoint(collider, at) - at).sqrMagnitude;
                    if (distance >= bestDistance) continue;
                    best = collider;
                    bestDistance = distance;
                }
            }
            if (best == null)
            {
                if (firstTry) LogTriggers(at, reach, hand);
                if (firstTry && logs++ < 80) Plugin.Log.LogInfo($"Climbing: {hand.Name} hand grips, nothing within {reach:0.00} m");
                return;
            }

            Take(hand, best, at, surface, Mathf.Sqrt(bestDistance), control, bar, shape, shapeSurface);
        }

        // That controller's hand lets go of what it holds and takes hold of that collider where the hand is: a thing pulled
        // off a wall comes into the hand (VrPullOff).
        public static bool TakeHold(VrInput.Hand input, Collider collider)
        {
            foreach (var hand in Hands)
            {
                if (hand.Input != input) continue;
                if (hand.Holding) LetGo(hand, false, "takes hold of what it pulled off");
                var at = HandWorld(hand);
                var surface = VrUtil.ClosestPoint(collider, at);
                Take(hand, collider, at, surface, (surface - at).magnitude, null, Vector3.zero);
                return hand.Holding;
            }
            return false;
        }

        private static void Take(Hand hand, Collider best, Vector3 at, Vector3 surface, float bestDistance, InteractableObject control, Vector3 bar,
            Collider shape = null, Vector3 shapeSurface = default)
        {
            var first = !Pulling();
            if (!VrHand.StartHolding(hand.Vr)) return;
            var body = best.attachedRigidbody;
            hand.Collider = best;
            hand.Body = body;
            // With extended world physics a part above the game's grab range moves too, up to BracedPush.MaxMass: held
            // without a brace (both hands on a 100 kg fuel barrel), the arms' push or throw goes to it and the body takes
            // the same back, as two bodies pushing apart (Raik). Before it held like a wall: the body flew off and the
            // barrel stayed. Braced on something fixed, the other hand still pushes it with a limited force (Pushes).
            var moving = body != null && !body.isKinematic && body.gameObject.layer != StaticLayer && body.mass > 0f;
            hand.Heavy = moving && TooHeavy(body.mass);
            hand.Movable = moving && (!hand.Heavy || HeavyMoves(body));
            if (hand.Movable) Separate(body);
            hand.Anchor = body != null ? body.transform : best.transform;
            hand.Point = hand.Anchor.InverseTransformPoint(at);
            hand.Surface = hand.Anchor.InverseTransformPoint(shape != null ? shapeSurface : surface);
            hand.Control = control != null;
            hand.Normal = hand.Anchor.InverseTransformDirection(shape != null
                ? SurfaceNormal(shape, at, shapeSurface, hand)
                : SurfaceNormal(best, at, surface, hand));
            hand.Small = body != null && !body.isKinematic && Small(body);
            hand.HeldAt = Time.time;
            hand.TurnAtHold = VrCamera.Instance.Playspace;
            hand.Sliding = false;
            hand.Firm = false;
            hand.Follower = false;
            hand.PinAnchor = null;
            hand.Free = false;
            hand.FreeTurn = false;
            hand.Bar = control != null ? hand.Anchor.InverseTransformDirection(bar) : Vector3.zero;
            foreach (var other in Hands)
                if (other != hand && other.Firm && other.Body == body) hand.Follower = true;
            if (!hand.Follower && hand.Movable && body.mass <= FirmMass && !HeldByOther(hand) && hand.Input.TryGetAimRotation(out var aim))
                Firm(hand, aim);
            // On an edge or a bar (Raik, 0.30.1): only the speed across its line pulls at the arm. Within what the arm can
            // take (HandleForce over HandleStroke) the hand holds and the speed along it glides on, so coming in at a slant
            // one holds on at speeds that tear the hand off straight on; faster, the arm takes what it can and the hand is
            // torn off, the body flying on slower.
            hand.EdgeLine = Vector3.zero;
            hand.Stretch = false;
            hand.SoftUntil = 0f;
            hand.OffsetKnown = false;
            hand.Motion = Vector3.zero;
            hand.StretchSwing = false;
            if (Glides(hand) && EdgeAt(best, at, surface, out var line))
            {
                hand.EdgeLine = hand.Anchor.InverseTransformDirection(line);
                var holdVelocity = hand.Body != null ? hand.Body.GetPointVelocity(Hold(hand)) : Vector3.zero;
                var relative = player.velocity - holdVelocity;
                var alongEdge = Vector3.Project(relative, line);
                var acrossEdge = relative - alongEdge;
                var limit = ClimbContact.CatchLimit(ClimbContact.HandleForce, ClimbContact.HandleStroke, VrCamera.WorldScale);
                var slant = Vector3.Angle(relative, line);
                if (slant > 90f) slant = 180f - slant;
                if (edgeLogs++ < 30)
                    Plugin.Log.LogInfo($"Climbing: {hand.Name} hand takes hold of an edge of '{best.name}' at {acrossEdge.magnitude:0.00} m/s across, {alongEdge.magnitude:0.00} along "
                        + $"({slant:0}° to it, holds {limit:0.00} m/s across)");
                // Raik, 0.30.1: fast across it, the hand stays on the edge and the body goes on, the arm braking it, until
                // the hold passes the head (Stretch): caught by then, the body is drawn back; else the hand lets go.
                // Up to HoldsUpTo along it the hand stays put too, and that speed is taken by the arm as well (Swing).
                var holdsAlong = alongEdge.magnitude <= HoldsUpTo * VrCamera.WorldScale;
                var stopped = acrossEdge + (holdsAlong ? alongEdge : Vector3.zero);
                // Realistic climbing: neither, the arm itself stops the body as far as the hand stays put (Pull, CatchForce).
                var realistic = Realistic;
                if (!realistic && acrossEdge.magnitude > StretchFrom * VrCamera.WorldScale)
                {
                    hand.Stretch = true;
                    hand.StretchWay = stopped.normalized;
                    hand.StretchBrake = ClimbContact.HandleForce / ClimbContact.BrakeMass * VrCamera.WorldScale;
                    hand.StretchSwing = false;
                    hand.Input.Buzz(0.8f, 0.08f);
                }
                else if (!realistic && holdsAlong && stopped.magnitude >= ClimbContact.GlideLeast * VrCamera.WorldScale) Swing(hand, stopped);
                hand.GlideVelocity = holdsAlong || alongEdge.magnitude < ClimbContact.GlideLeast * VrCamera.WorldScale ? Vector3.zero : alongEdge;
            }
            else
            {
                // The other hand already gliding: this one glides on with the same speed, else with the body's own.
                hand.GlideVelocity = GlideAtHold(hand);
                var othersGlide = false;
                foreach (var other in Hands)
                    if (other != hand && other.GlideVelocity != Vector3.zero && Glides(hand))
                    {
                        hand.GlideVelocity = other.GlideVelocity;
                        othersGlide = true;
                    }
                // Raik, 0.30.1: up to HoldsUpTo the hand holds at once where it took hold, and the body swings on a few
                // centimeters, the arm taking it (Swing), instead of gliding.
                if (!othersGlide && hand.GlideVelocity != Vector3.zero && hand.GlideVelocity.magnitude <= HoldsUpTo * VrCamera.WorldScale)
                {
                    if (!Realistic) Swing(hand, hand.GlideVelocity);
                    hand.GlideVelocity = Vector3.zero;
                }
            }
            if (hand.GlideVelocity != Vector3.zero && logs++ < 80)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand takes hold at {hand.GlideVelocity.magnitude:0.00} m/s along the surface and glides to a stop");
            hand.PullOffset = hand.Local - ClimbContact.Closer(hand.Local, VrCamera.Instance.transform.localPosition);
            LogControl(best, hand);
            if (first && !hand.Firm)
            {
                pushOff = Vector3.zero;
                SquishyOff();
            }
            hand.Input.Buzz(0.5f, 0.06f);
            if (logs++ < 80)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand holds '{best.name}' (layer {best.gameObject.layer}, "
                    + (body == null ? "no rigidbody" : $"rigidbody '{body.name}' {body.mass:0.#} kg{(body.isKinematic ? " kinematic" : "")}")
                    + $", {(hand.Movable ? hand.Heavy ? "moves, above the grab range" : "moves" : "fixed")}), {bestDistance:0.00} m from the surface");
        }

        // Each holding hand: the impulse between player and hold that brings the hand to its hold in this step. Both hands
        // share the player's mass; two hands on the same light part share its mass too.
        private static void Pull()
        {
            var holding = 0;
            foreach (var hand in Hands)
            {
                if (!hand.Holding) continue;
                if (hand.Free || hand.Stretch)
                {
                    holding++;
                    continue;
                }
                var error = Hold(hand) - (Pushes(hand) ? HandWorld(hand) : PullPoint(hand));
                // A part that moves follows the hand softly (PartPullTime), so it lags further in a quick swing: torn off
                // only at twice the distance, as a firmly held one. Not while the body is drawn back after a catch on an edge.
                if ((!blocked || Pushes(hand)) && Time.time >= hand.SoftUntil && error.magnitude > (hand.Firm || hand.Follower || hand.Movable ? 2f : 1f) * TearOff * VrCamera.WorldScale) LetGo(hand, false, $"held back {error.magnitude:0.00} m from the hold");
                else holding++;
            }
            // A firmly held part goes with the hand and pulls at nothing.
            var pulling = 0;
            foreach (var hand in Hands)
                if (hand.Holding && !hand.Firm && !hand.Follower && !hand.Free && !hand.Stretch && !Pushes(hand)) pulling++;
            if (pulling == 0)
            {
                bodySpin = Vector3.zero;
                foreach (var hand in Hands)
                    if (Pushes(hand)) Push(hand);
                blocked = false;
                HoldOff(Time.fixedDeltaTime);
                MoveFirm(Time.fixedDeltaTime);
                return;
            }

            var dt = Time.fixedDeltaTime;
            foreach (var hand in Hands)
                if (Pushes(hand)) Push(hand);
            Twist(dt, out var twistLeft, out var twistRight);
            var velocity = player.velocity;
            var holdsVelocity = Vector3.zero;
            // Realistic climbing: on a fixed hold each arm speeds the body up or slows it down by at most armAccel, and the
            // body closes a gap no faster than it can still stop at it with all arms (no overshoot).
            var realistic = Realistic;
            var armAccel = realistic ? ClimbContact.GloveBrake(VrSettings.ArmPull.Value, 1, VrCamera.WorldScale) : 0f;
            var catchAccel = realistic ? ClimbContact.GloveBrake(VrSettings.CatchForce.Value, 1, VrCamera.WorldScale) : 0f;
            PullApplied[0] = PullApplied[1] = Vector3.zero;
            var limited = false;
            var braking = false;
            // How fast each hand moves against the body: the body keeps to the hold by going the other way as fast
            // (hand over hand keeps the speed), the rest of the gap closed no faster than it can still stop at it.
            foreach (var hand in Hands)
            {
                if (!realistic || !hand.Holding || hand.Movable || hand.Firm || hand.Follower || hand.Free || hand.Stretch || Pushes(hand))
                {
                    hand.OffsetKnown = false;
                    hand.Motion = Vector3.zero;
                    continue;
                }
                var offset = PullPoint(hand) - player.position;
                if (!hand.OffsetKnown)
                {
                    // Just taken hold: the hand is taken to stay on the hold, the body going on as it went, until the
                    // next step shows how the hand really moves (else each hold taken hand over hand braked for a step).
                    var holdVelocityNow = (hand.Body != null ? hand.Body.GetPointVelocity(Hold(hand)) : Vector3.zero) + hand.GlideVelocity;
                    hand.Motion = -(player.velocity - holdVelocityNow);
                    hand.MotionKnown = false;
                }
                else
                {
                    var raw = Vector3.ClampMagnitude((offset - hand.OffsetBefore) / dt, MaxPull * VrCamera.WorldScale);
                    hand.Motion = hand.MotionKnown ? Vector3.Lerp(hand.Motion, raw, 1f - Mathf.Exp(-dt / MotionSmoothing)) : raw;
                    hand.MotionKnown = true;
                }
                hand.OffsetBefore = offset;
                hand.OffsetKnown = true;
            }
            // Worked out PullPasses times, each from the velocities the pass before left; a part's change at once (Kick),
            // so the other hand on it sees it. Worked out once from the same velocities, two hands on a turning crate
            // overcorrected each other and it swung (review of the soft pull). The hands share the body as before:
            // with fixed holds a second pass changes nothing.
            for (var pass = 0; pass < PullPasses; pass++)
            {
                var change = Vector3.zero;
                foreach (var hand in Hands)
                {
                    if (!hand.Holding || hand.Firm || hand.Follower || hand.Free || hand.Stretch || Pushes(hand)) continue;
                    var hold = Hold(hand);
                    // Without the twist the body takes (Twist): that turns the body, it does not pull at the part.
                    var error = hold - PullPoint(hand) - (hand == LeftHand ? twistLeft : hand == RightHand ? twistRight : Vector3.zero);
                    var holdVelocityHere = hand.Body != null ? hand.Body.GetPointVelocity(hold) : Vector3.zero;
                    // Realistic climbing (Raik, 0.30.1): a hand gliding along the wall (faster than HoldsUpTo, Glide) moves with
                    // its hold, which goes along at the glide's speed: the gloves brake that glide (GloveBrake), not the arm.
                    if (realistic && !hand.Movable) holdVelocityHere += hand.GlideVelocity;
                    if (pass == 0) holdsVelocity += holdVelocityHere;
                    // The player's velocity relative to the hold that closes the gap within this step, or softly on a part
                    // that moves.
                    // After a catch on an edge (Stretch) the body is drawn back to where hand and controller meet again
                    // over PullBack, not at once.
                    var over = hand.Movable ? Mathf.Max(dt, PartPullTime) : Time.time < hand.SoftUntil ? Mathf.Max(dt, PullBack) : dt;
                    var wanted = Vector3.ClampMagnitude(error / over, MaxPull * VrCamera.WorldScale);
                    var inert = realistic && !hand.Movable;
                    if (inert) wanted = ClimbContact.KeepToHold(error, hand.Motion, over, armAccel * pulling, MaxPull * VrCamera.WorldScale);
                    var needed = wanted - (velocity - holdVelocityHere);
                    if (needed.sqrMagnitude < 1e-10f) continue;
                    var inverse = pulling / BodyMass;
                    if (hand.Movable) inverse += Sharing(hand) * InverseMass(hand.Body, hold, needed.normalized);
                    var impulse = needed / inverse * (hand.Movable ? PartRelax : 1f);
                    var step = impulse / BodyMass;
                    if (inert)
                    {
                        var index = hand == LeftHand ? 0 : 1;
                        // Against the body's way relative to the hold it brakes (the catch), else it pushes or pulls.
                        var brakes = Vector3.Dot(PullApplied[index] + step, velocity - holdVelocityHere) < 0f;
                        var allowed = Vector3.ClampMagnitude(PullApplied[index] + step, (brakes ? catchAccel : armAccel) * dt) - PullApplied[index];
                        if ((allowed - step).sqrMagnitude > 1e-8f)
                        {
                            limited = true;
                            if (brakes)
                            {
                                braking = true;
                                if (pass == 0) hand.Input.Buzz(0.35f, 0.03f);
                            }
                        }
                        step = allowed;
                        PullApplied[index] += step;
                    }
                    change += step;
                    if (hand.Movable) Kick(hand.Body, -impulse, hold);
                }
                velocity += change;
            }
            holdVelocity = holdsVelocity / pulling;
            if (limited && inertiaLogs++ < 30)
                Plugin.Log.LogInfo(braking
                    ? $"Climbing: the arms stop the body at most {catchAccel:0.0} m/s² each ({VrSettings.CatchForce.Value:0} N), body at {(velocity - holdVelocity).magnitude:0.00} m/s"
                    : $"Climbing: the arms speed the body up at most {armAccel:0.0} m/s² each ({VrSettings.ArmPull.Value:0} N), body at {(velocity - holdVelocity).magnitude:0.00} m/s");
            player.velocity = velocity;
            CalmSpin(dt);
            // Held off a part before stopping short of walls, so what HoldOff gives the body is kept clear of them too
            // (review: its knock back could carry the body into a wall behind).
            HoldOff(dt);
            player.velocity = KeepClear(player.velocity, dt);
            afterPull = player.velocity;
            pulled = true;
            MoveFirm(dt);
            pushOff = Vector3.Lerp(pushOff, player.velocity - holdVelocity, 1f - Mathf.Exp(-dt / PushOffSmoothing));
        }

        // Test (Raik, 2026-11-14: with both hands on a 4.8 t part he could turn it by twisting his arms, though he is far
        // lighter and braced on nothing; the body should turn instead): the hands' pulls turned the part at the holds but
        // the body, a point, never turned back. Now the twist of the two holds against the two hands (the hands moved
        // apart crosswise, about their middle) is shared by how hard each turns: the body, BodyTurnInertia about its
        // middle and its mass about the hands' middle, and the part, its inertia about the hands' middle. The body's
        // share turns the playspace about the head (TakeSpin, VrBody), closing it within PartPullTime, at most TwistMost;
        // the pulls leave it out and only turn the part by its own share. A heavy part hardly turns, the body turns
        // round it; a light one turns as before. Only with both hands pulling at the same part that moves; none of it
        // stays once they let go (the suit holds the body steady).
        // Test (Raik, 2026-11-14: holding on and moving to and fro should stay one to one; turning that starts at once is
        // tiring for many): the twist turns the body only past VrSettings.ClimbTurnDeadzone (crosswise, per hand), and
        // only with realistic climbing (VrSettings.ClimbTurning). Simple climbing never turns the body. Either way the
        // pulls leave the body's share of the twist out, so a heavy part is not turned by it.
        public enum Turning { Einfach, Realistisch }

        private const float BodyTurnInertia = 12f;
        private const float TwistMost = 2f;
        private const float TwistLeast = 0.1f;
        private static Vector3 bodySpin;
        private static float spinAt;
        private static float twistLogAt;
        private static int twistLogs;

        private static bool Twists(Hand hand) =>
            hand.Holding && hand.Movable && !hand.Firm && !hand.Follower && !hand.Free && hand.Body != null && !Pushes(hand);

        // Raik, 0.30.1: the same on fixed holds (the hull, a pillar, two handles; they may be two different things): what
        // is held does not turn, so the body takes all of the twist past the dead zone, with realistic climbing only.
        // Within the dead zone, and with simple climbing, the hands pull as before.
        private static bool TwistsFixed(Hand hand) =>
            hand.Holding && !hand.Movable && !hand.Firm && !hand.Follower && !hand.Free && !hand.Stretch && hand.PinAnchor == null
            && hand.Anchor != null && !Pushes(hand);

        private static void Twist(float dt, out Vector3 left, out Vector3 right)
        {
            left = right = Vector3.zero;
            bodySpin = Vector3.zero;
            var onFixed = TwistsFixed(LeftHand) && TwistsFixed(RightHand);
            if (!onFixed && (!Twists(LeftHand) || !Twists(RightHand) || LeftHand.Body != RightHand.Body)) return;
            if (onFixed && (VrSettings.ClimbTurning == null || VrSettings.ClimbTurning.Value != Turning.Realistisch)) return;
            var scale = VrCamera.WorldScale;
            var a = PullPoint(LeftHand);
            var b = PullPoint(RightHand);
            var half = (a - b) * 0.5f;
            if (half.sqrMagnitude < TwistLeast * TwistLeast * scale * scale) return;
            var apart = ((Hold(LeftHand) - a) - (Hold(RightHand) - b)) * 0.5f;
            var turn = Vector3.Cross(half, apart) / half.sqrMagnitude;
            var angle = turn.magnitude;
            if (angle < 1e-4f) return;
            if (onFixed)
            {
                var twisted = Vector3.Cross(turn, half);
                var overDead = twisted.magnitude - VrSettings.ClimbTurnDeadzone.Value * 0.01f * scale;
                if (overDead <= 0f) return;
                var cut = overDead / twisted.magnitude;
                // The pulls leave out what the body turns away; the rest they pull as ever.
                left = twisted * cut;
                right = -left;
                bodySpin = Vector3.ClampMagnitude(turn * cut / Mathf.Max(dt, PartPullTime), TwistMost);
                spinAt = Time.time;
                if (twistLogs < 40 && Time.time >= twistLogAt && bodySpin.magnitude > 0.05f)
                {
                    twistLogs++;
                    twistLogAt = Time.time + 1f;
                    Plugin.Log.LogInfo($"Climbing: both hands twist against fixed holds ('{LeftHand.Collider?.name}', '{RightHand.Collider?.name}'): the body turns {bodySpin.magnitude * Mathf.Rad2Deg:0} °/s");
                }
                return;
            }
            var axis = turn / angle;
            var middle = (a + b) * 0.5f;
            var part = LeftHand.Body;
            var frame = part.rotation * part.inertiaTensorRotation;
            var inPart = Quaternion.Inverse(frame) * axis;
            var partInertia = Vector3.Dot(Vector3.Scale(part.inertiaTensor, inPart), inPart)
                + part.mass * Vector3.Cross(part.worldCenterOfMass - middle, axis).sqrMagnitude;
            var bodyInertia = BodyTurnInertia * scale * scale + BodyMass * Vector3.Cross(player.worldCenterOfMass - middle, axis).sqrMagnitude;
            var share = partInertia / Mathf.Max(partInertia + bodyInertia, 1e-6f);
            var crosswise = Vector3.Cross(turn, half);
            var across = crosswise * share;
            left = across;
            right = -across;
            if (VrSettings.ClimbTurning == null || VrSettings.ClimbTurning.Value != Turning.Realistisch) return;
            var dead = VrSettings.ClimbTurnDeadzone.Value * 0.01f * scale;
            var past = crosswise.magnitude - dead;
            if (past <= 0f) return;
            var beyond = turn * (past / crosswise.magnitude);
            bodySpin = Vector3.ClampMagnitude(beyond * share / Mathf.Max(dt, PartPullTime), TwistMost);
            spinAt = Time.time;
            if (twistLogs < 40 && Time.time >= twistLogAt && bodySpin.magnitude > 0.05f)
            {
                twistLogs++;
                twistLogAt = Time.time + 1f;
                Plugin.Log.LogInfo($"Climbing: both hands twist '{part.name}' {part.mass:0} kg: the body turns {bodySpin.magnitude * Mathf.Rad2Deg:0} °/s, "
                    + $"taking {share * 100f:0}% of it (inertia part {partInertia:0}, body {bodyInertia:0} kg m²)");
            }
        }

        // The body's turn from twisting at a held part for this frame (VrBody), about the head; none once it stopped.
        public static Quaternion TakeSpin(float dt)
        {
            if (bodySpin == Vector3.zero || Time.time - spinAt > 0.1f || !VrXr.IsRunning) return Quaternion.identity;
            return Quaternion.AngleAxis(bodySpin.magnitude * dt * Mathf.Rad2Deg, bodySpin.normalized);
        }

        // The velocity without what would carry the body's colliders closer than Clearance to anything ahead within this
        // step. Moving along a surface or away from it stays.
        private static Vector3 KeepClear(Vector3 velocity, float dt)
        {
            blocked = false;
            // The body's solid sphere only: a Rigidbody sweep could also take its trigger sphere of 50 cm.
            BodyBall(out var center, out var radius);
            velocity = KeepClear(center, radius, velocity, dt);
            // The hitbox sits low and back, and the real head leans out of it: the face went through walls it looked at.
            var head = VrCamera.Instance.transform.position + (player.position - bodyAtRig);
            return KeepClear(head, HeadRadius * VrCamera.WorldScale, velocity, dt);
        }

        private static Vector3 KeepClear(Vector3 center, float radius, Vector3 velocity, float dt)
        {
            var relative = velocity - holdVelocity;
            var speed = relative.magnitude;
            if (speed < 1e-4f) return velocity;
            var clearance = Clearance * VrCamera.WorldScale;
            var hits = Physics.SphereCastAll(center, radius, relative / speed, speed * dt + clearance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                // What the sphere already touches has no place ahead of it.
                if (hit.distance <= 0f) continue;
                var body = hit.collider.attachedRigidbody;
                // A light part held that moves takes the pull's push back itself: stopping the body short of it as well
                // took the body's share away, and each stroke of the arms gave the two more speed (Raik, a 60 kg barrel).
                if (Own(hit.collider) || HeldFirmly(body) || PulledLight(body)) continue;
                var other = body != null ? body.GetPointVelocity(hit.point) : Vector3.zero;
                var limited = ClimbContact.LimitApproach(velocity, other, hit.normal, hit.distance - clearance, dt);
                if (limited != velocity)
                {
                    blocked = true;
                    if (contactLogs++ < 20)
                        Plugin.Log.LogInfo($"Climbing: body stops short of '{hit.collider.name}', {hit.distance:0.00} m ahead, "
                            + $"from {velocity.magnitude:0.00} to {limited.magnitude:0.00} m/s");
                }
                velocity = limited;
            }
            return velocity;
        }

        // The turning of each held part that moves dies down over PartSpinCalm, with at most CalmTorque: the suit holds it
        // steady, a light one at once, a heavy one hardly.
        private static void CalmSpin(float dt)
        {
            var calm = Mathf.Exp(-dt / PartSpinCalm);
            foreach (var hand in Hands)
            {
                if (!Pulls(hand) || !FirstOnPart(hand)) continue;
                var part = hand.Body;
                var frame = part.rotation * part.inertiaTensorRotation;
                // In the part's principal axes: the turning to take away, and the angular momentum that is.
                var lost = (Quaternion.Inverse(frame) * part.angularVelocity) * (1f - calm);
                var momentum = Vector3.Scale(part.inertiaTensor, lost).magnitude;
                var most = CalmTorque * dt;
                if (momentum > most) lost *= most / momentum;
                part.angularVelocity -= frame * lost;
            }
        }

        // A hand that pulls at a part that moves (not one held firmly, following another hand, or pushing it).
        private static bool Pulls(Hand hand) =>
            hand.Holding && hand.Movable && !hand.Firm && !hand.Follower && hand.Body != null && !Pushes(hand);

        // Not the right hand on a part the left one also holds: things done once per part.
        private static bool FirstOnPart(Hand hand) => !(hand == RightHand && LeftHand.Holding && LeftHand.Body == hand.Body);

        // The body's solid sphere, its middle and radius in the world.
        private static void BodyBall(out Vector3 center, out float radius)
        {
            var solid = BodySphere();
            center = solid != null ? solid.transform.TransformPoint(solid.center) : player.position;
            radius = solid != null ? solid.radius * solid.transform.lossyScale.x : 0.3f;
        }

        // An impulse on a part at a point, as a change of its velocities right away (AddForce waits for the physics
        // step, so the other hand on the part would not see it).
        private static void Kick(Rigidbody body, Vector3 impulse, Vector3 point)
        {
            body.velocity += impulse / body.mass;
            body.angularVelocity += Spin(body, point, impulse);
        }

        // The change of turning an impulse at a point gives a body.
        private static Vector3 Spin(Rigidbody body, Vector3 point, Vector3 impulse)
        {
            var frame = body.rotation * body.inertiaTensorRotation;
            var torque = Quaternion.Inverse(frame) * Vector3.Cross(point - body.worldCenterOfMass, impulse);
            var tensor = body.inertiaTensor;
            return frame * new Vector3(
                tensor.x > 0f ? torque.x / tensor.x : 0f,
                tensor.y > 0f ? torque.y / tensor.y : 0f,
                tensor.z > 0f ? torque.z / tensor.z : 0f);
        }

        // The point of a part nearest to the body's middle within reach of the body's surface, the way from it to the body,
        // and the room left before Clearance; false when none is near or the middle is inside it.
        private static bool NearBody(Rigidbody part, float reach, out Vector3 point, out Vector3 normal, out float room)
        {
            BodyBall(out var center, out var radius);
            var clearance = Clearance * VrCamera.WorldScale;
            point = normal = Vector3.zero;
            room = 0f;
            var count = Physics.OverlapSphereNonAlloc(center, radius + 2f * clearance + reach, Touching, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                if (Touching[i].attachedRigidbody != part) continue;
                var on = VrUtil.ClosestPoint(Touching[i], center);
                var distance = (on - center).sqrMagnitude;
                if (distance >= nearest) continue;
                nearest = distance;
                point = on;
            }
            if (nearest == float.MaxValue || nearest < 1e-8f) return false;
            var gap = Mathf.Sqrt(nearest);
            normal = (center - point) / gap;
            room = gap - radius - clearance;
            return true;
        }

        // A held part that moves does not collide with the body (Separate), so pulled in, it would go into it: once the two
        // are within Clearance, their closing is stopped, by equal and opposite impulses shared by their masses, as two
        // bodies meeting without bouncing. Up, down and apart stay free, and there is no push off: the pair keeps its
        // momentum (Raik: drawing a fuel barrel in, the pull should just stop going closer). KeepClear leaves such parts
        // out, as stopping the body alone gave a barrel rowed with both arms more speed each stroke.
        private static readonly Collider[] Touching = new Collider[16];
        private const float Bounce = 0.15f;
        private const float BounceFrom = 0.4f;
        private static int holdOffLogs;

        private static void HoldOff(float dt)
        {
            foreach (var hand in Hands)
            {
                var part = hand.Body;
                // Also a part pushed while braced (Pushes): it is kept off the body as well, pulled in by the push (review:
                // a hoist truck drawn in this way went through the body).
                if (!hand.Holding || !hand.Movable || hand.Firm || hand.Follower || part == null || !FirstOnPart(hand)) continue;
                // As far as the two could close within this step, so a quick pull does not pass the check.
                if (!NearBody(part, (player.velocity - part.velocity).magnitude * dt, out var point, out var normal, out var room)) continue;
                var closing = -Vector3.Dot(player.velocity - part.GetPointVelocity(point), normal);
                var allowed = Mathf.Max(room, 0f) / dt;
                if (closing <= allowed) continue;
                // Met fast (a part stopped by a wall, the body drifting on), the two part again a little (Bounce of the
                // closing speed): a small knock back, as Raik asked, not the hard throw the physics engine gave.
                var bounce = closing > BounceFrom * VrCamera.WorldScale && room <= 0f ? Bounce : 0f;
                // A body braced on something fixed does not give way: the part takes it all.
                var bodyInverse = Pushes(hand) && Braced(hand) ? 0f : 1f / BodyMass;
                var impulse = (closing - allowed + bounce * closing) / (bodyInverse + InverseMass(part, point, normal));
                player.velocity += normal * (impulse * bodyInverse);
                Kick(part, -normal * impulse, point);
                if (holdOffLogs++ < 20)
                    Plugin.Log.LogInfo($"Climbing: '{part.name}' {part.mass:0} kg held off the body {Mathf.Max(room, 0f) * 100f:0} cm short, closing at {closing:0.00} m/s stopped");
            }
        }

        // Whether a collision of the player's body is a touch of the thing held instead of an impact. Ship parts share one
        // rigidbody, so the whole hull counts as the thing held.
        public static bool Soft(Collision collision)
        {
            if (!LeftHand.Holding && !RightHand.Holding) return false;
            var speed = collision.relativeVelocity.magnitude;
            var soft = false;
            foreach (var hand in Hands)
                if (hand.Holding && (hand.Collider == collision.collider || (hand.Body != null && hand.Body == collision.rigidbody))) soft = true;
            soft = soft && speed <= SoftContact * VrCamera.WorldScale;
            if (impactLogs++ < 40)
                Plugin.Log.LogInfo($"Climbing: {(soft ? "touches" : "impact with")} '{collision.collider.name}' at {speed:0.00} m/s while holding on");
            return soft;
        }

        // The part goes where the hand has it, by velocity, and not through the body: it would knock into the body each
        // step and bounce, which at this size is a deadly impact. Interpolated, since the headset shows more frames than
        // physics runs steps.
        private static void Firm(Hand hand, Quaternion aim)
        {
            var body = hand.Body;
            hand.Firm = true;
            hand.Follower = false;
            hand.FirmTurn = Quaternion.Inverse(VrCamera.Instance.transform.parent.rotation * aim) * body.rotation;
            hand.SpinBefore = body.maxAngularVelocity;
            hand.InterpolationBefore = body.interpolation;
            body.maxAngularVelocity = FirmSpin;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            Separate(body);
        }

        // While a hand holds a part that moves, the body and that part do not collide; let go, they collide again once
        // apart (ClearParts). A firmly held light part would knock into the body each step. A heavier one, stopped by a
        // wall, was run into by the body drifting on, and the physics engine threw the two apart: some 900 kg m/s on the
        // body in a second from that contact while the pulls gave nothing, a strong push off the wall through a 56 kg
        // crate (Raik). The arm keeps the part off the body instead.
        private static int separateLogs;

        private static void Separate(Rigidbody body)
        {
            var known = clearing.RemoveAll(each => each.Body == body) > 0;
            Collide(body.GetComponentsInChildren<Collider>(true), false);
            if (!known && separateLogs++ < 20) Plugin.Log.LogInfo($"Climbing: body and '{body.name}' {body.mass:0} kg do not collide while held");
        }

        // Let go of a part that moves: it collides with the body again once apart or after FirmClearTime.
        private static void Release(Rigidbody body) =>
            clearing.Add(new Clearing { Body = body, Parts = body.GetComponentsInChildren<Collider>(true), Until = Time.time + FirmClearTime });

        private static void Unfirm(Hand hand)
        {
            hand.Firm = false;
            var body = hand.Body;
            if (body == null) return;
            body.maxAngularVelocity = hand.SpinBefore;
            body.interpolation = hand.InterpolationBefore;
            Release(body);
        }

        private static void Collide(Collider[] parts, bool collide)
        {
            if (player == null) return;
            foreach (var own in player.GetComponentsInChildren<Collider>(true))
                foreach (var part in parts)
                    if (part != null && !own.isTrigger && !part.isTrigger) Physics.IgnoreCollision(own, part, !collide);
        }

        private static void ClearParts(bool all)
        {
            for (var i = clearing.Count - 1; i >= 0; i--)
            {
                var each = clearing[i];
                // Kept apart while held, and near the body until the time is up; also after that while the part still
                // sits in the body, where colliding again would throw the two apart (review, with 3 s instead of 10).
                if (each.Body != null && !all && player != null && (HeldMoving(each.Body)
                    || ((each.Body.ClosestPointOnBounds(player.position) - player.position).magnitude < FirmClear
                        && (Time.time < each.Until || InBody(each.Parts))))) continue;
                Collide(each.Parts, true);
                clearing.RemoveAt(i);
            }
        }

        // Whether any of these colliders reaches into the body's sphere.
        private static bool InBody(Collider[] parts)
        {
            BodyBall(out var center, out var radius);
            foreach (var part in parts)
                if (part != null && part.enabled && (VrUtil.ClosestPoint(part, center) - center).sqrMagnitude < radius * radius) return true;
            return false;
        }

        // How many hands hold that part.
        private static int HandsOn(Rigidbody body)
        {
            var count = 0;
            foreach (var hand in Hands)
                if (hand.Holding && hand.Body == body) count++;
            return Mathf.Max(1, count);
        }

        // Each firmly held part goes part of the way to where the hand has it, with the body's speed on top.
        private static void MoveFirm(float dt)
        {
            foreach (var hand in Hands)
            {
                if (!hand.Holding || !hand.Firm || hand.Body == null || !hand.Input.TryGetAimRotation(out var aim)) continue;
                var body = hand.Body;
                var follow = ClimbContact.Follow(body.mass, FirmMass, FirmFollow, FirmFollowHeavy);
                var wanted = VrCamera.Instance.transform.parent.rotation * aim * hand.FirmTurn;
                var turn = wanted * Quaternion.Inverse(body.rotation);
                turn.ToAngleAxis(out var angle, out var axis);
                if (angle > 180f) angle -= 360f;
                var center = body.worldCenterOfMass;
                var wantedCenter = HandWorld(hand) - turn * (Hold(hand) - center);
                var toHand = Vector3.ClampMagnitude((wantedCenter - center) * follow / dt, MaxPull * VrCamera.WorldScale);
                // Test (Raik, 2026-11-14: a 10 kg crate flew as fast as the arm moved): the arms speed it up and slow it down
                // with ClimbContact.ArmForce each at most, so a heavy part lags a quick swing and goes as fast as that force
                // can make it.
                var hands = HandsOn(body);
                var now = body.velocity - player.velocity;
                var most = ClimbContact.MostAcceleration(body.mass, hands) * VrCamera.WorldScale * dt;
                body.velocity = player.velocity + now + Vector3.ClampMagnitude(toHand - now, most);
                body.angularVelocity = Mathf.Abs(angle) < 0.01f || float.IsInfinity(axis.x)
                    ? Vector3.zero
                    : Vector3.ClampMagnitude(axis * (angle * Mathf.Deg2Rad * follow / dt), FirmSpin);
            }
        }

        private static SphereCollider BodySphere()
        {
            if (bodySphere == null)
                foreach (var collider in player.GetComponentsInChildren<SphereCollider>(true))
                    if (!collider.isTrigger) bodySphere = collider;
            return bodySphere;
        }

        private static bool HeldFirmly(Rigidbody body)
        {
            foreach (var hand in Hands)
                if (hand.Firm && body != null && hand.Body == body) return true;
            return false;
        }

        // Outward from the surface at the hold: the face a ray from the hand to its nearest point meets. With the hand on
        // the surface or already in it (grabbed out of a movement), or that face turned well away from the palm, the face
        // the palm turns to instead: of the faces rays along the palm, the controller's aim and its down meet, the one most
        // facing the palm. The way from the nearest point to the hand alone tipped the hand at edges and on uneven tops
        // (Raik, the terminal at the start); a ray along the aim alone, which runs along the surface under a flat hand,
        // met the side of a step or an edge, and the hand held on edgewise with its fingers curled (Raik, 2026-11-06).
        private static Vector3 SurfaceNormal(Collider collider, Vector3 at, Vector3 surface, Hand hand)
        {
            var scale = VrCamera.WorldScale;
            var rig = VrCamera.Instance != null ? VrCamera.Instance.transform.parent : null;
            var turn = Quaternion.identity;
            var aimed = false;
            if (rig != null && hand.Input.TryGetAimRotation(out var aim))
            {
                aimed = true;
                turn = rig.rotation * aim;
            }
            // Where the palm faces: towards the body's middle, to the controller's right on the left hand.
            var palm = turn * (hand.Input.IsLeft ? Vector3.right : Vector3.left);
            var away = at - surface;
            var distance = away.magnitude;
            if (distance > 0.01f * scale)
            {
                var toward = -away / distance;
                var nearest = collider.Raycast(new Ray(at, toward), out var face, distance + 0.02f * scale) && Vector3.Dot(face.normal, away) > 0f
                    ? face.normal : away / distance;
                if (!aimed || Vector3.Angle(nearest, -palm) <= PalmTurnedAway) return nearest;
                if (!FacingPalm(collider, at, turn, palm, out var turned) || Vector3.Angle(turned, -palm) >= Vector3.Angle(nearest, -palm)) return nearest;
                LogFace(hand, "the nearest face turned away from the palm", Vector3.Angle(nearest, -palm), Vector3.Angle(turned, -palm));
                return turned;
            }
            if (aimed && FacingPalm(collider, at, turn, palm, out var normal))
            {
                LogFace(hand, "on or in the surface", -1f, Vector3.Angle(normal, -palm));
                return normal;
            }
            return -(aimed ? palm : Vector3.down);
        }

        private static int faceLogs;

        private static void LogFace(Hand hand, string why, float nearest, float palm)
        {
            if (faceLogs++ < 30)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand takes the face its palm turns to ({why}{(nearest >= 0f ? $", nearest face {nearest:0}° off the palm" : "")}), {palm:0}° off the palm");
        }

        // A face the nearest point's own is taken over the palm's only this far off facing the palm, in degrees.
        private const float PalmTurnedAway = 60f;

        // Of the faces rays from outside the hand along the palm, the aim and the controller's down meet, the one whose
        // outside faces the palm most; false without one.
        private static bool FacingPalm(Collider collider, Vector3 at, Quaternion turn, Vector3 palm, out Vector3 normal)
        {
            normal = Vector3.zero;
            var reach = Reach * VrCamera.WorldScale;
            var best = -2f;
            foreach (var way in new[] { palm, turn * Vector3.forward, turn * Vector3.down })
            {
                if (!collider.Raycast(new Ray(at - way * reach, way), out var hit, 2f * reach)) continue;
                var facing = Vector3.Dot(hit.normal, -palm);
                if (facing <= best) continue;
                best = facing;
                normal = hit.normal;
            }
            return best > -2f;
        }

        // A part the game lets one move, held by a hand that pulls it (not one pushing a heavy part, BracedPush).
        private static bool PulledLight(Rigidbody body)
        {
            foreach (var hand in Hands)
                if (body != null && hand.Body == body && Pulls(hand)) return true;
            return false;
        }

        // Held by a hand that may move it, so kept apart from the body (Separate).
        private static bool HeldMoving(Rigidbody body)
        {
            foreach (var hand in Hands)
                if (hand.Holding && hand.Movable && hand.Body == body) return true;
            return false;
        }

        private static bool Held(Rigidbody body)
        {
            foreach (var hand in Hands)
                if (hand.Holding && hand.Body == body) return true;
            return false;
        }

        private static bool HeldByOther(Hand hand)
        {
            foreach (var each in Hands)
                if (each != hand && each.Holding && each.Body == hand.Body) return true;
            return false;
        }

        // Above the game's grab mass range (GrabController.RawGrabbedMassRange.AboveRange), through reflection: the range's
        // type lives in BBI.Unity.Core, which the plugin does not reference.
        // A control on something that floats loose, as a cassette or a cable to collect: light and moving. A ship's controls
        // are on its body too, which the game also lets move (not kinematic), at tens of tonnes: taken for loose, a door
        // switch was held as a plain surface above the switch, and used, the hand was bent towards the ship's middle (Raik).
        private const float LooseMass = 200f;

        public static bool Loose(Component control)
        {
            var body = control != null ? control.GetComponentInParent<Rigidbody>() : null;
            return body != null && !body.isKinematic && body.mass <= LooseMass;
        }

        private static bool TooHeavy(float mass)
        {
            var range = RawGrabbedMassRange?.Invoke(grab, null);
            var above = range != null ? AccessTools.Method(range.GetType(), "AboveRange", new[] { typeof(float) }) : null;
            return above != null && (bool)above.Invoke(range, new object[] { mass });
        }

        // How many holding hands hold that hand's body.
        private static int Sharing(Hand hand)
        {
            var count = 0;
            foreach (var each in Hands)
                if (each.Holding && each.Body == hand.Body) count++;
            return count;
        }

        // The inverse of the mass a push at that point in that direction meets: lower than the whole mass off the center,
        // where the push also turns the body. Without it a part held at its end swung past the hand each step.
        private static float InverseMass(Rigidbody body, Vector3 point, Vector3 direction) =>
            1f / body.mass + Vector3.Dot(Vector3.Cross(Spin(body, point, direction), point - body.worldCenterOfMass), direction);

        // For finding out why door switches sometimes cannot be held: what the hand was near instead.
        private static void LogTriggers(Vector3 at, float reach, Hand hand)
        {
            var count = Physics.OverlapSphereNonAlloc(at, reach, Found, ~0, QueryTriggerInteraction.Collide);
            for (var i = 0; i < count && switchLogs < 40; i++)
            {
                if (Own(Found[i])) continue;
                switchLogs++;
                var control = Found[i].GetComponentInParent<InteractableObject>();
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand near '{Found[i].name}' (layer {Found[i].gameObject.layer}{(Found[i].isTrigger ? ", trigger" : "")}"
                    + $"{(control != null ? $", control '{control.name}'" : "")}), not held");
            }
        }

        // And why a held switch sometimes shows no prompt: the game offers what the view centers on, not what the hand holds.
        private static void LogControl(Collider held, Hand hand)
        {
            var control = held.GetComponentInParent<InteractableObject>();
            if (control == null || switchLogs >= 40) return;
            switchLogs++;
            Plugin.Log.LogInfo($"Climbing: {hand.Name} hand holds the control '{control.name}'{(hand.Control ? hand.Bar != Vector3.zero ? ", as a fist round its grip" : ", the hand flat on it where it took hold" : ", as a plain surface")}");
        }

        // The directions of a cube's faces and corners.
        private static Vector3[] BuildAround()
        {
            var directions = new System.Collections.Generic.List<Vector3>();
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                        if (x != 0 || y != 0 || z != 0) directions.Add(new Vector3(x, y, z).normalized);
            return directions.ToArray();
        }

        private static Vector3 Hold(Hand hand) =>
            hand.PinAnchor != null ? hand.PinAnchor.TransformPoint(hand.Pin) : hand.Anchor.TransformPoint(hand.Point);

        // That hand works a heavy part while the other is braced on something that does not move.
        // With the other hand on a second part too heavy to grab instead, the two push each other apart through the body
        // (Braced false): what one hand gives its part, the body takes back, so the masses share it.
        private static bool Pushes(Hand hand)
        {
            if (!Pushable(hand)) return false;
            var other = Other(hand);
            if (!other.Holding || other.Body == hand.Body) return false;
            // Two parts the game lets one grab and move keep sharing the pull as before (Pull).
            return Fixed(other) || (Pushable(other) && (!hand.Movable || hand.Heavy) && (!other.Movable || other.Heavy));
        }

        private static bool Pushable(Hand hand)
        {
            var body = hand.Body;
            if (!VrSettings.BracedPush.Value || !hand.Holding || hand.Firm || hand.Follower || body == null || body.isKinematic) return false;
            return body.gameObject.layer != StaticLayer && body.mass > FirmMass && body.mass <= BracedPush.MaxMass;
        }

        // A hold that does not move at all: the station, a hull, anything kinematic, or too heavy to push.
        private static bool Fixed(Hand hand) =>
            hand.Body == null || hand.Body.isKinematic || hand.Body.gameObject.layer == StaticLayer || hand.Body.mass > BracedPush.MaxMass;

        private static bool Braced(Hand hand) => Fixed(Other(hand));

        private static Hand Other(Hand hand) => hand == LeftHand ? RightHand : LeftHand;

        // The hand's force on its part; between two parts the body pushes back.
        private static void Push(Hand hand)
        {
            var hold = Hold(hand);
            var force = BracedPush.Force((HandWorld(hand) - hold) / VrCamera.WorldScale, hand.Body.mass);
            // At the body, a part that moves is pushed no closer to it (HoldOff stops what speed it still has).
            if (hand.Movable && NearBody(hand.Body, 0f, out _, out var normal, out var room) && room <= 0f)
                force -= normal * Mathf.Max(Vector3.Dot(force, normal), 0f);
            hand.Body.AddForceAtPosition(force, hold, ForceMode.Force);
            if (!Braced(hand)) player.AddForce(-force / BodyMass, ForceMode.Acceleration);
            PushLog.Step(hand, force);
        }

        // Diagnostics only, not for the mod: what a push gives a heavy part each second (Raik: the hoist truck seemed to
        // move more easily after the barrel fix). Force applied, the acceleration that should give, and the part's real
        // change of speed.
        private static class PushLog
        {
            private static Rigidbody part;
            private static Vector3 startVelocity;
            private static float since;
            private static float forceSum;
            private static float forceMost;
            private static int steps;
            private static int logs;

            public static void Step(Hand hand, Vector3 force)
            {
                if (logs >= 60) return;
                var body = hand.Body;
                if (body != part)
                {
                    part = body;
                    startVelocity = body.velocity;
                    since = Time.time;
                    forceSum = forceMost = 0f;
                    steps = 0;
                }
                steps++;
                forceSum += force.magnitude;
                forceMost = Mathf.Max(forceMost, force.magnitude);
                if (Time.time - since < 1f) return;
                var seconds = Time.time - since;
                var mean = forceSum / steps;
                logs++;
                Plugin.Log.LogInfo($"Climbing push: {hand.Name} hand on '{body.name}' {body.mass:0} kg, {(Braced(hand) ? "braced" : "against the other part")}, "
                    + $"force {mean:0} N on average (most {forceMost:0}, limit {BracedPush.Limit(body.mass):0}), so at most {mean / body.mass:0.00} m/s²; "
                    + $"its speed {startVelocity.magnitude:0.00} -> {body.velocity.magnitude:0.00} m/s, {(body.velocity - startVelocity).magnitude / seconds:0.00} m/s² measured, drag {body.drag:0.00}");
                startVelocity = body.velocity;
                since = Time.time;
                forceSum = forceMost = 0f;
                steps = 0;
            }
        }

        // The rigidbody that hand holds on to, if any (VrArmPush leaves it alone).
        public static Rigidbody HeldBody(VrInput.Hand input)
        {
            foreach (var hand in Hands)
                if (hand.Input == input && hand.Holding) return hand.Body;
            return null;
        }

        // True while that controller's hand holds on to something large: a wall, the hull, a console, a heavy part. The hand
        // lies flat on it there (VrHands.Curl); around a small part that turns with the hand (Firm) it closes.
        // Raik (0.30.1): only really small things (nuts and bolts, debris, Small) and a lever's or nacelle's grip (Bar) close
        // the hand round them; a light but large part turning with the hand, a door switch, a console and the hoist truck
        // are held flat, the fingers laid on as on a wall.
        public static bool HoldsFlat(VrInput.Hand input)
        {
            foreach (var hand in Hands)
                if (hand.Input == input && hand.Holding)
                    return (!(hand.Firm || hand.Follower) || !hand.Small) && (!hand.Control || hand.Bar == Vector3.zero);
            return false;
        }

        // A part no longer than SmallMost each way, by its solid colliders' bounds.
        private const float SmallMost = 0.35f;
        private static readonly System.Collections.Generic.List<Collider> SmallParts = new System.Collections.Generic.List<Collider>();

        private static bool Small(Rigidbody body)
        {
            body.GetComponentsInChildren(true, SmallParts);
            var known = false;
            var bounds = new Bounds();
            foreach (var part in SmallParts)
            {
                if (part == null || part.isTrigger || !part.enabled) continue;
                if (known) bounds.Encapsulate(part.bounds);
                else bounds = part.bounds;
                known = true;
            }
            SmallParts.Clear();
            if (!known) return true;
            var size = bounds.size;
            return Mathf.Max(size.x, Mathf.Max(size.y, size.z)) <= SmallMost * VrCamera.WorldScale;
        }

        // True while that controller's hand holds a control (a door switch, a console's button) as a fist.
        // What can be used of what the hands hold (VrInteractHeld): the interactable on the thing the hand that took hold
        // last holds, a switch, a console or a thing to collect; null when no hand holds one.
        public static InteractableObject HeldInteractable()
        {
            InteractableObject found = null;
            var at = float.MinValue;
            foreach (var hand in Hands)
            {
                if (!hand.Holding || hand.Collider == null || hand.HeldAt < at) continue;
                var interactable = PartInteractable(hand.Collider);
                if (interactable == null || !interactable.isActiveAndEnabled) continue;
                found = interactable;
                at = hand.HeldAt;
            }
            return found;
        }

        // The loose part that controller's hand holds, with what can be used of it (VrShoulderStow), or false.
        private static readonly System.Collections.Generic.HashSet<string> NothingToUse = new System.Collections.Generic.HashSet<string>();

        public static bool HeldLoose(VrInput.Hand input, out Rigidbody body, out InteractableObject interactable)
        {
            body = null;
            interactable = null;
            foreach (var hand in Hands)
            {
                if (hand.Input != input || !hand.Holding || hand.Body == null || hand.Body.isKinematic || hand.Collider == null) continue;
                body = hand.Body;
                interactable = hand.Collider.GetComponentInParent<InteractableObject>();
                // Else one below the part's body, but only of the same structure part: not a collectable mounted somewhere
                // inside a larger part, which is a structure part of its own.
                // The part is the held collider's, not the body's: parts the game joined under one body (a spare part
                // on a 'HierarchyParent', Raik 2026-11-12: the orange spare parts did not go into the backpack) have no
                // part above the body.
                if (interactable == null)
                {
                    var own = hand.Collider.GetComponentInParent<StructurePart>();
                    if (own == null) own = hand.Body.GetComponentInParent<StructurePart>();
                    var under = own != null ? own.transform : hand.Body.transform;
                    foreach (var each in under.GetComponentsInChildren<InteractableObject>())
                        if (each.GetComponentInParent<StructurePart>() == own) { interactable = each; break; }
                }
                if (interactable == null && NothingToUse.Add(hand.Collider.name) && NothingToUse.Count < 40)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var component in hand.Collider.GetComponents<Component>()) parts.Add(component.GetType().Name);
                    Plugin.Log.LogInfo($"Climbing: {input.Name} hand holds '{hand.Collider.name}' on '{hand.Body.name}', nothing to use found; on it: {string.Join(", ", parts)}");
                }
                return true;
            }
            return false;
        }

        // What can be used of the part a collider belongs to: the interactable above it, else a thing to collect below its
        // structure part that belongs to that part (a first aid kit's 'VOL_Interaction' under its 'SM_Collectible_MedKit', held as a
        // surface on the kit's own mesh).
        public static InteractableObject PartInteractable(Collider collider)
        {
            if (collider == null) return null;
            var interactable = collider.GetComponentInParent<InteractableObject>();
            if (interactable != null) return interactable;
            var own = collider.GetComponentInParent<StructurePart>();
            if (own == null) return null;
            foreach (var each in own.GetComponentsInChildren<InteractableObject>())
                if (each.GetComponentInParent<StructurePart>() == own && VrPullOff.Usable(each)) return each;
            return null;
        }

        // The control that controller's hand holds (VrLever), or null.
        public static InteractableObject HeldControl(VrInput.Hand input)
        {
            foreach (var hand in Hands)
                if (hand.Input == input && hand.Holding && hand.Control && hand.Collider != null)
                    return hand.Collider.GetComponentInParent<InteractableObject>();
            return null;
        }

        // The collider that controller's hand holds, or null.
        public static Collider HeldCollider(VrInput.Hand input)
        {
            foreach (var hand in Hands)
                if (hand.Input == input && hand.Holding) return hand.Collider;
            return null;
        }

        // That controller's hand held free (VrNacelle, Hand.Free), or not.
        public static void SetFree(VrInput.Hand input, bool free, bool turns = true)
        {
            foreach (var hand in Hands)
                if (hand.Input == input && hand.Holding)
                {
                    hand.Free = free;
                    hand.FreeTurn = free && turns;
                }
        }

        // How far ahead of the controller's grip the palm takes hold, along the palm's outward direction, in m at world
        // scale 1: just above the palm of the hand (Raik: 1 to 2 cm over the palm, not inside the hand), then 2 cm further
        // out, so a closed hand has it between fingers and palm and an open one just above the palm (Raik, 2026-11-08).
        private const float PalmAhead = 0.055f;
        // And this far towards the finger tips, in m at world scale 1 (Raik, 2026-11-08: 1 to 1.5 cm).
        private const float PalmToFingers = 0.015f;

        // The way that hand's palm faces, in the world (as PalmPoint).
        public static bool TryGetPalm(VrInput.Hand input, out Vector3 palm)
        {
            palm = Vector3.zero;
            if (!input.TryGetGripPose(out _, out var grip) || VrCamera.Instance == null) return false;
            palm = VrCamera.Instance.transform.parent.rotation * grip * (input == VrInput.Left ? Vector3.right : Vector3.left);
            return true;
        }

        // The point a hand takes hold of controls from: just above its palm. The palm faces to the right on the left hand
        // and to the left on the right hand, along the controller's grip pose.
        public static Vector3 PalmPoint(VrInput.Hand input, Vector3 at)
        {
            if (!input.TryGetGripPose(out _, out var grip) || VrCamera.Instance == null) return at;
            var turn = VrCamera.Instance.transform.parent.rotation * grip;
            var side = input == VrInput.Left ? Vector3.right : Vector3.left;
            var palm = turn * side;
            var point = at + palm * (PalmAhead * VrCamera.WorldScale);
            // Towards the finger tips, along the drawn hand from the wrist to the knuckles, at the same height over the palm.
            if (VrHands.TryGetArm(input == VrInput.Left, out _, out _, out var wrist, out var knuckles))
            {
                var fingers = Vector3.ProjectOnPlane(knuckles - wrist, palm);
                if (fingers.sqrMagnitude > 1e-6f) point += fingers.normalized * (PalmToFingers * VrCamera.WorldScale);
            }
            return point;
        }

        // Where that controller's hand sits on its hold, in the world.
        public static bool TryGetSurface(VrInput.Hand input, out Vector3 surface)
        {
            surface = Vector3.zero;
            foreach (var hand in Hands)
            {
                if (hand.Input != input || !hand.Holding || hand.Anchor == null) continue;
                surface = hand.Anchor.TransformPoint(hand.Surface);
                return true;
            }
            return false;
        }

        // Whether the other hand than that controller's holds on to something fixed (a counter hold, VrNacelle).
        public static bool Braced(VrInput.Hand input)
        {
            foreach (var hand in Hands)
                if (hand.Input != input && hand.Holding && !hand.Movable && !hand.Free && hand.Anchor != null) return true;
            return false;
        }

        public static bool HeldFree(VrInput.Hand input)
        {
            foreach (var hand in Hands)
                if (hand.Input == input && hand.Holding) return hand.FreeTurn;
            return false;
        }

        // The body of that controller's hand keeps to where its hold is now, on still, which does not move, while the hand
        // goes with its anchor (a lever the game turns back, VrLever); or, still null, it follows the hold again from where
        // it is, the hold's point on the anchor moved there, so the body does not move.
        public static void Pin(VrInput.Hand input, Transform still)
        {
            foreach (var hand in Hands)
            {
                if (hand.Input != input || !hand.Holding || hand.Anchor == null) continue;
                var point = Hold(hand);
                if (still != null)
                {
                    hand.PinAnchor = still;
                    hand.Pin = still.InverseTransformPoint(point);
                }
                else if (hand.PinAnchor != null)
                {
                    hand.PinAnchor = null;
                    hand.Point = hand.Anchor.InverseTransformPoint(point);
                }
            }
        }

        // The hold of that controller's hand moves with this transform from now on (a lever's handle it turns, VrLever):
        // the hand stays where it is on it.
        public static void Reanchor(VrInput.Hand input, Transform anchor)
        {
            foreach (var hand in Hands)
            {
                if (hand.Input != input || !hand.Holding || hand.Anchor == null || hand.Anchor == anchor) continue;
                var point = hand.Anchor.TransformPoint(hand.Point);
                var surface = hand.Anchor.TransformPoint(hand.Surface);
                var normal = hand.Anchor.TransformDirection(hand.Normal);
                var bar = hand.Anchor.TransformDirection(hand.Bar);
                hand.Anchor = anchor;
                hand.Point = anchor.InverseTransformPoint(point);
                hand.Surface = anchor.InverseTransformPoint(surface);
                hand.Normal = anchor.InverseTransformDirection(normal);
                hand.Bar = anchor.InverseTransformDirection(bar);
            }
        }

        public static bool HoldsControl(VrInput.Hand input)
        {
            foreach (var hand in Hands)
                if (hand.Input == input && hand.Holding) return hand.Control;
            return false;
        }

        // The fixed control with a collider nearest to the hand within reach, that collider and its point nearest the hand,
        // or null.
        private static InteractableObject NearestControl(Vector3 at, float reach, out Collider collider, out Vector3 point)
        {
            collider = null;
            point = at;
            InteractableObject best = null;
            var bestDistance = reach * reach;
            var count = Physics.OverlapSphereNonAlloc(at, reach, Found, ~0, QueryTriggerInteraction.Collide);
            for (var i = 0; i < count; i++)
            {
                if (Own(Found[i])) continue;
                var control = Found[i].GetComponentInParent<InteractableObject>();
                if (control == null || control.HandTarget == null || !control.isActiveAndEnabled) continue;
                // Things that float loose, as cassettes to collect, are held as parts.
                if (Loose(control)) continue;
                var nearest = VrUtil.ClosestPoint(Found[i], at);
                var distance = (nearest - at).sqrMagnitude;
                if (distance >= bestDistance) continue;
                best = control;
                bestDistance = distance;
                collider = Found[i];
                point = nearest;
            }
            return best;
        }

        // Where that controller's hand holds on, if it does, for the game's hand (VrHands.Pose).
        // The thing that hand holds on to, and the surface's outward direction there.
        // The direction of the lever grip that controller's hand holds (VrLever.OnGrip), in the world.
        public static bool TryGetBar(VrInput.Hand input, out Vector3 axis)
        {
            axis = Vector3.zero;
            foreach (var hand in Hands)
            {
                if (hand.Input != input || !hand.Holding || hand.Anchor == null || hand.Bar == Vector3.zero) continue;
                axis = hand.Anchor.TransformDirection(hand.Bar).normalized;
                return true;
            }
            return false;
        }

        public static bool TryGetGrip(VrInput.Hand input, out Transform anchor, out Vector3 normal)
        {
            anchor = null;
            normal = Vector3.up;
            foreach (var hand in Hands)
            {
                if (hand.Input != input || !hand.Holding || hand.Anchor == null) continue;
                anchor = hand.Anchor;
                normal = hand.Anchor.TransformDirection(hand.Normal).normalized;
                return true;
            }
            return false;
        }

        public static bool TryGetHold(VrInput.Hand input, out Vector3 point)
        {
            point = Vector3.zero;
            foreach (var hand in Hands)
            {
                if (hand.Input != input || !hand.Holding || hand.Anchor == null) continue;
                point = Vector3.Lerp(Hold(hand), hand.Anchor.TransformPoint(hand.Surface), (Time.time - hand.HeldAt) / SnapTime);
                // Raik (2026-11-14: a hand that glides on after taking hold at speed stutters): the glide moves the hold
                // once per physics step (20 ms), the body is drawn in between (interpolated), the headset shows 90 frames a
                // second. The hand is drawn in between the same way: the last step's way less how far into the next.
                if (hand.PinAnchor == null && hand.GlideStepAt == Time.fixedTime && hand.GlideStep != Vector3.zero)
                {
                    var into = Time.fixedDeltaTime > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 1f;
                    point -= hand.Anchor.TransformVector(hand.GlideStep) * (1f - into);
                }
                return true;
            }
            return false;
        }

        // The controller in the world at the body's current physics position.
        private static Vector3 HandWorld(Hand hand) =>
            VrCamera.Instance.transform.parent.TransformPoint(hand.Local) + (player.position - bodyAtRig);

        // The point the body is pulled by: the controller, closer to the head than it really is once it is near (see
        // ClimbContact.Closer).
        // A heavy part that moves (above the grab range) is climbed along like a fixed hold, with the gain near the head
        // (review: it lost it when such parts began to move).
        private static Vector3 PullPoint(Hand hand) => hand.Movable && !hand.Heavy ? HandWorld(hand) :
            VrCamera.Instance.transform.parent.TransformPoint(ClimbContact.Closer(hand.Local, VrCamera.Instance.transform.localPosition) + hand.PullOffset)
            + (player.position - bodyAtRig);

        // The player's own colliders (body, tools) are nothing to hold on to.
        private static bool Own(Collider collider) =>
            collider.attachedRigidbody == player || collider.transform.IsChildOf(player.transform);

        // The point of the collider nearest to the hand. Non-convex meshes have no such query, most of a ship's are: rays
        // all around the hand find it; a hand with no surface around counts as on them.
        private static Vector3 Closest(Collider collider, Vector3 point, float reach)
        {
            if (!(collider is MeshCollider mesh) || mesh.convex) return collider.ClosestPoint(point);
            var nearest = reach;
            var found = point;
            foreach (var direction in Around)
            {
                if (!collider.Raycast(new Ray(point, direction), out var hit, nearest)) continue;
                nearest = hit.distance;
                found = hit.point;
            }
            return found;
        }

        // Letting go keeps the speed the hands gave the body once the last hand is off: a push off flies on.
        private static void LetGo(Hand hand, bool push, string reason)
        {
            // Off a light part that moves the body keeps the speed the pulls gave it, as the part does: set from the
            // hands, the two did not add up. Asked while the hand still holds, as Pushes needs.
            var light = hand.Movable && hand.Body != null && !Pushes(hand);
            var movable = hand.Movable;
            VrHand.StopHolding(hand.Vr);
            var firm = hand.Firm;
            hand.Follower = false;
            var body = hand.Body;
            if (firm) Unfirm(hand);
            if (firm && push && body != null && !Held(body)) Throw(hand, body);
            hand.Anchor = null;
            hand.Collider = null;
            hand.Body = null;
            // A firmly held part is released by Unfirm already.
            if (movable && !firm && body != null && !Held(body)) Release(body);
            foreach (var other in Hands)
                if (other != hand && other.Holding && other.Movable && other.Body == body && body != null && body.mass <= FirmMass
                    && other.Input.TryGetAimRotation(out var aim)) Firm(other, aim);
            // A firmly held part does not count: it pulls at nothing.
            var last = !Pulling();
            var speed = Vector3.ClampMagnitude(pushOff, MaxPushOff * VrCamera.WorldScale);
            var flies = push && last && !firm && !light;
            if (flies && player != null) player.velocity = holdVelocity + speed;
            if (logs++ < 80)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand lets go ({reason}){(flies ? $", flies on at {speed.magnitude:0.00} m/s" : "")}");
            if (last)
            {
                pushOff = Vector3.zero;
                SquishyBack();
            }
        }

        // PlayerMotion.HandleSquishyCollide: moving towards a surface within its range, it pushes the body away and plays
        // a crackle, an effect and a shake. A hand pulling up to the hull would fight it all the time.
        private static bool Pulling()
        {
            foreach (var hand in Hands)
                if (hand.Holding && !hand.Firm && !hand.Follower) return true;
            return false;
        }

        private static void SquishyOff()
        {
            if (motion == null || squishyOff) return;
            squishyWas = SquishyCollide(motion);
            motion.SetSquishyCollide(false);
            squishyOff = true;
        }

        private static void SquishyBack()
        {
            if (!squishyOff) return;
            squishyOff = false;
            if (motion != null) motion.SetSquishyCollide(squishyWas);
        }

        // What the body collides with, for the log.
        private static void LogColliders()
        {
            foreach (var collider in player.GetComponentsInChildren<Collider>(true))
            {
                var shape = collider is SphereCollider sphere ? $"sphere r {sphere.radius:0.00} at {sphere.center}"
                    : collider is CapsuleCollider capsule ? $"capsule r {capsule.radius:0.00} h {capsule.height:0.00} axis {capsule.direction} at {capsule.center}"
                    : collider is BoxCollider box ? $"box {box.size} at {box.center}"
                    : collider.GetType().Name;
                Plugin.Log.LogInfo($"Climbing: body collider {VrUtil.Chain(collider.transform)}: {shape}, local {collider.transform.position - player.position}, "
                    + $"bounds {collider.bounds.size}{(collider.isTrigger ? ", trigger" : "")}{(collider.enabled ? "" : ", off")}");
            }
        }

        private static void LetGoAll()
        {
            foreach (var hand in Hands)
                if (hand.Holding) LetGo(hand, false, "climbing paused");
        }
    }

    // Outside VrClimb, whose static fields need the game: tests run this without it.
    internal static class ClimbContact
    {
        // Thrown parts get the hand's speed; tiny debris up to TinyMass more of it (Raik: nuts and bolts flew too slowly),
        // less and less up to NormalMass, from which they get just the hand's speed.
        private const float TinyMass = 2f;
        private const float NormalMass = 5f;
        private const float TinyGain = 1.5f;

        public static float ThrowGain(float mass) => Mathf.Lerp(TinyGain, 1f, Mathf.InverseLerp(TinyMass, NormalMass, mass));

        // Test (Raik, 2026-11-14: a 10 kg crate could be thrown far too hard; an arm has some 150 to 180 N): each arm
        // pushes a part it holds with at most ArmForce newtons, and moves its own ArmMass (forearm, hand and the suit's
        // glove) along with it. So a firmly held part speeds up or slows down by at most ArmForce / (mass + ArmMass) per
        // arm, and a throw, that force over a stroke of ThrowStroke meters, gives it at most
        // sqrt(2 * ArmForce * ThrowStroke / (mass + ArmMass)): about 4.5 m/s for 10 kg, 8.9 m/s for 1 kg.
        public const float ArmForce = 170f;
        public const float ArmMass = 2f;
        public const float ThrowStroke = 0.7f;

        // Meters per second squared, at world scale 1.
        public static float MostAcceleration(float mass, int hands) =>
            ArmForce * hands / (Mathf.Max(mass, 0f) + ArmMass * hands);

        // Meters per second, at world scale 1.
        public static float MostThrowSpeed(float mass) =>
            Mathf.Sqrt(2f * ArmForce * ThrowStroke / (Mathf.Max(mass, 0f) + ArmMass));

        // Gliding along a surface held (VrClimb.Glide): from this speed on, in m/s at world scale 1.
        public const float GlideLeast = 0.3f;

        // Raik, 0.30.1: the gloves brake a glide evenly with a force of their own, the same at any speed (a magnetic grip
        // on the hull, stronger than five fingers' friction), instead of always stopping within 0.8 m: the faster one comes
        // in, the further one glides. Each hand gliding brakes with that force (VrSettings.GloveBrake, newtons) on a body
        // of BrakeMass kg. Raik chose 180 N after testing, then about 20 % more with the body's inertia (2026-11-15): 215 N, 2.15 m/s² a hand,
        // 23 cm from 1 m/s, 2.1 m from 3 m/s.
        public const float BrakeMass = 100f;

        // How hard a glide brakes, in m/s² at that world scale.
        public static float GloveBrake(float newtons, int hands, float scale) =>
            Mathf.Max(newtons, 0f) * Mathf.Max(hands, 1) / BrakeMass * scale;

        // Raik, 0.30.1 (realistic climbing): the fastest the body may close a gap of that many meters and still stop at it
        // with that acceleration, in m/s.
        public static float MostApproach(float gap, float acceleration) =>
            Mathf.Sqrt(2f * Mathf.Max(acceleration, 0f) * Mathf.Max(gap, 0f));

        // Raik, 0.30.1 (realistic climbing): the body's velocity relative to a fixed hold that keeps the hand on it: against
        // the hand's motion along the body (hand over hand, the body keeps going), and the gap left (error, from the pull
        // point to the hold) closed within over, but no faster than the body can still stop at it with acceleration.
        public static Vector3 KeepToHold(Vector3 error, Vector3 motion, float over, float acceleration, float most)
        {
            var gap = error.magnitude;
            var close = gap > 1e-6f ? error / gap * Mathf.Min(gap / Mathf.Max(over, 1e-4f), MostApproach(gap, acceleration)) : Vector3.zero;
            return Vector3.ClampMagnitude(close - motion, most);
        }

        // One step of the body towards a velocity, changing by at most acceleration over dt.
        public static Vector3 Accelerate(Vector3 velocity, Vector3 toward, float acceleration, float dt) =>
            velocity + Vector3.ClampMagnitude(toward - velocity, acceleration * dt);

        // What a hand catches on, the arm giving way over a stroke as it takes the blow: a handle, a bar or the edge of a
        // pillar (HandleForce over HandleStroke, up to 3 m/s for 100 kg) and a step up between two hull parts (LipForce
        // over LipStroke, up to 1.7 m/s). Only the speed across it counts: what goes along it glides on.
        public const float HandleForce = 900f;
        public const float HandleStroke = 0.5f;
        public const float LipForce = 500f;
        public const float LipStroke = 0.3f;
        // A flush seam between two parts takes this much speed, in m/s at world scale 1.
        public const float SeamLoss = 0.3f;

        // The fastest speed across that force over that stroke can stop, in m/s at that world scale.
        public static float CatchLimit(float force, float stroke, float scale) =>
            Mathf.Sqrt(2f * force * stroke / BrakeMass) * scale;

        // Comes in at across (speed across, m/s): caught when within limit, with left 0; else the arm takes limit's energy
        // and the body goes on with what is left.
        public static bool Catch(float across, float limit, out float left)
        {
            if (across <= limit)
            {
                left = 0f;
                return true;
            }
            left = Mathf.Sqrt(across * across - limit * limit);
            return false;
        }

        // A speed taken off along its own way, never past standing still.
        public static Vector3 Lose(Vector3 velocity, float loss)
        {
            var speed = velocity.magnitude;
            return speed <= loss ? Vector3.zero : velocity * ((speed - loss) / speed);
        }

        // Thrust along a surface held glides at most this fast, in m/s at world scale 1 (Raik: 3 m/s was too fast);
        // a faster glide from taking hold brakes down to it. Before is the glide's speed before this step's thrust.
        private const float ThrustGlideMost = 1.5f;

        public static Vector3 ThrustGlide(Vector3 velocity, float before, float brake, float dt, float scale)
        {
            var most = ThrustGlideMost * scale;
            var speed = velocity.magnitude;
            if (speed <= most) return velocity;
            var allowed = Mathf.Max(most, Mathf.Min(speed, before) - brake * dt);
            return velocity * (allowed / speed);
        }

        // The glide velocity one step later.
        public static Vector3 Brake(Vector3 velocity, float brake, float dt)
        {
            var speed = velocity.magnitude;
            var slower = speed - brake * dt;
            return slower <= 0f ? Vector3.zero : velocity * (slower / speed);
        }

        // From this far from the head, in real meters, a hand counts as closer than it is, by Gain for each meter nearer.
        private const float CloseFrom = 0.4f;
        private const float Gain = 1.5f;

        // An arm cannot bring its hand to the head: pulled in, it stops about 30 cm ahead, and the body with it, far from a
        // wall it reached for at arm's length. So the last part of a pull counts for more, up to the hand counting as at
        // the head. The game's hand stays on its hold either way (VrHands.Pose).
        // How much of the way to the hand a part of that mass goes each step.
        public static float Follow(float mass, float heaviest, float lightFollow, float heavyFollow) =>
            Mathf.Lerp(lightFollow, heavyFollow, mass / heaviest);

        public static Vector3 Closer(Vector3 hand, Vector3 head)
        {
            var offset = hand - head;
            var distance = offset.magnitude;
            if (distance >= CloseFrom || distance < 1e-4f) return hand;
            return head + offset / distance * Mathf.Max(distance - Gain * (CloseFrom - distance), 0f);
        }

        // The velocity without the part that closes more than room meters on a surface (normal pointing at the body, moving
        // at other) within dt; no room left means no closing in at all.
        public static Vector3 LimitApproach(Vector3 velocity, Vector3 other, Vector3 normal, float room, float dt)
        {
            var closing = -Vector3.Dot(velocity - other, normal);
            var allowed = Mathf.Max(room, 0f) / dt;
            return closing > allowed ? velocity + normal * (closing - allowed) : velocity;
        }
    }

    // A touch of the thing held (VrClimb.Soft) makes no impact: no sound, sparks, shake or damage.
    [HarmonyPatch]
    internal static class ImpactPatch
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("BBI.Unity.Game.PlayerMotion"), "OnCollisionEnter", new[] { typeof(Collision) });

        private static bool Prefix(Collision collision) => !VrClimb.Soft(collision);
    }

    internal static class BracedPush
    {
        // Somewhat more than a person could, not enough to replace the grapple.
        public const float MaxForce = 250f;
        public const float MaxMass = 5000f;
        // Newtons per meter the hand is away from its hold: the full force comes at 10 cm.
        private const float Stiffness = 2500f;
        // Big parts gather speed slowly, the heavier the slower: at most 0.35 m/s² at 200 kg, down to 0.17 at 1000 kg, so
        // the 636 kg hoist truck gets 0.25 (Raik; first 0.4 to 0.2, which gave it 0.29). Lighter parts get more force on
        // the way down to 30 kg, where MaxForce is.
        private const float LightMass = 30f;
        private const float BigMass = 200f;
        private const float HeavyMass = 1000f;
        private const float BigAcceleration = 0.35f;
        private const float HeavyAcceleration = 0.17f;

        // The force on the hold, towards the hand.
        public static Vector3 Force(Vector3 toHand, float mass)
        {
            var force = toHand * Stiffness;
            var limit = Limit(mass);
            return force.magnitude > limit ? force.normalized * limit : force;
        }

        // The most force for a part of that mass.
        public static float Limit(float mass)
        {
            if (mass < BigMass)
                return Mathf.Lerp(MaxForce, BigMass * BigAcceleration, Mathf.InverseLerp(LightMass, BigMass, mass));
            var acceleration = Mathf.Lerp(BigAcceleration, HeavyAcceleration, Mathf.InverseLerp(BigMass, HeavyMass, mass));
            return Mathf.Min(MaxForce, mass * acceleration);
        }
    }
}
