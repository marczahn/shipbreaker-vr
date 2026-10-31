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
    internal static class VrClimb
    {
        static VrClimb() => VrSession.OnUnload("climbing", Release);

        // How far from the controller a surface may be to hold on, in meters at world scale 1.
        private const float Reach = 0.15f;
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
        private const float MaxPushOff = 5f;
        // The speed a push off keeps is the hands' speed, smoothed over about this time in seconds.
        private const float PushOffSmoothing = 0.08f;
        // How far the body stays from what is ahead of it while holding on, in meters at world scale 1.
        private const float Clearance = 0.02f;
        // Up to this speed, in m/s at world scale 1, touching the thing held is no impact.
        private const float SoftContact = 3f;
        // Parts up to this mass, in kg, sit firmly in the hand: they turn with it. Heavier ones turn freely on the hold.
        private const float FirmMass = 100f;
        // How fast a firmly held part may turn, in radians per second.
        private const float FirmSpin = 30f;
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
            // The hold: a point fixed on Anchor (the body, or the collider's transform without one).
            public Transform Anchor;
            public Vector3 Point;
            // How far the pull point (PullPoint) sat from the controller at taking hold, in tracking space.
            public Vector3 PullOffset;
            // Where the grip was pressed last, for CatchTime.
            public float PressedAt = -1f;
            // The surface under the hand when it took hold, on Anchor: the game's hand goes there, the body keeps to Point.
            public Vector3 Surface;
            // A part that turns with the hand: its turn relative to the controller, and its speed limit before.
            public bool Firm;
            public Quaternion FirmTurn;
            public float SpinBefore;
            public float HeldAt;

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
        private static readonly Collider[] Found = new Collider[32];
        private static readonly Vector3[] Around = BuildAround();

        private static GrabController grab;
        private static Rigidbody player;
        private static PlayerMotion motion;
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
        // Whether KeepClear held the body back in the last step.
        private static bool blocked;

        // From VrCamera.FollowGameCamera, right after the rig was placed for the frame. The hands are measured on the rig,
        // which moves once per frame; in the physics steps between they move along by as far as the body has come since.
        public static void RigPlaced()
        {
            if (player == null) return;
            bodyAtRig = player.transform.position;
            rigPlaced = true;
        }

        // From VrCamera.FixedUpdate, before the physics step.
        public static void FixedUpdate()
        {
            if (!Usable())
            {
                LetGoAll();
                return;
            }
            foreach (var hand in Hands) Read(hand);
            Pull();
        }

        public static void Release()
        {
            LetGoAll();
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
                    Plugin.Log.LogInfo($"Climbing: player body '{player.name}', {player.mass:0.#} kg, interpolation {player.interpolation}");
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

        // Holds on to the surface nearest to the hand, if one is within reach, right where the hand is.
        private static void TryHold(Hand hand, bool firstTry)
        {
            var at = HandWorld(hand);
            var reach = Reach * VrCamera.WorldScale;
            var count = Physics.OverlapSphereNonAlloc(at, reach, Found, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Collider best = null;
            var bestDistance = float.MaxValue;
            var surface = at;
            for (var i = 0; i < count; i++)
            {
                var collider = Found[i];
                if (Own(collider)) continue;
                var closest = Closest(collider, at, reach);
                var distance = (closest - at).sqrMagnitude;
                if (distance >= bestDistance) continue;
                best = collider;
                bestDistance = distance;
                surface = closest;
            }
            if (best == null)
            {
                if (firstTry && logs++ < 80) Plugin.Log.LogInfo($"Climbing: {hand.Name} hand grips, nothing within {reach:0.00} m");
                return;
            }

            var first = !LeftHand.Holding && !RightHand.Holding;
            if (!VrHand.StartHolding(hand.Vr)) return;
            var body = best.attachedRigidbody;
            hand.Collider = best;
            hand.Body = body;
            hand.Movable = body != null && !body.isKinematic && body.gameObject.layer != StaticLayer && body.mass > 0f
                && !TooHeavy(body.mass);
            hand.Anchor = body != null ? body.transform : best.transform;
            hand.Point = hand.Anchor.InverseTransformPoint(at);
            hand.Surface = hand.Anchor.InverseTransformPoint(surface);
            hand.HeldAt = Time.time;
            hand.Firm = false;
            if (hand.Movable && body.mass <= FirmMass && !HeldByOther(hand) && hand.Input.TryGetAimRotation(out var aim))
            {
                hand.Firm = true;
                hand.FirmTurn = Quaternion.Inverse(VrCamera.Instance.transform.parent.rotation * aim) * body.rotation;
                hand.SpinBefore = body.maxAngularVelocity;
                body.maxAngularVelocity = FirmSpin;
            }
            hand.PullOffset = hand.Local - ClimbContact.Closer(hand.Local, VrCamera.Instance.transform.localPosition);
            if (first)
            {
                pushOff = Vector3.zero;
                SquishyOff();
            }
            hand.Input.Buzz(0.5f, 0.06f);
            if (logs++ < 80)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand holds '{best.name}' (layer {best.gameObject.layer}, "
                    + (body == null ? "no rigidbody" : $"rigidbody '{body.name}' {body.mass:0.#} kg{(body.isKinematic ? " kinematic" : "")}")
                    + $", {(hand.Movable ? "moves" : "fixed")}), {Mathf.Sqrt(bestDistance):0.00} m from the surface");
        }

        // Each holding hand: the impulse between player and hold that brings the hand to its hold in this step. Both hands
        // share the player's mass; two hands on the same light part share its mass too.
        private static void Pull()
        {
            var holding = 0;
            foreach (var hand in Hands)
            {
                if (!hand.Holding) continue;
                var error = Hold(hand) - PullPoint(hand);
                if (!blocked && error.magnitude > TearOff * VrCamera.WorldScale) LetGo(hand, false, $"held back {error.magnitude:0.00} m from the hold");
                else holding++;
            }
            if (holding == 0)
            {
                blocked = false;
                return;
            }

            var dt = Time.fixedDeltaTime;
            var velocity = player.velocity;
            var change = Vector3.zero;
            var holdsVelocity = Vector3.zero;
            foreach (var hand in Hands)
            {
                if (!hand.Holding) continue;
                var hold = Hold(hand);
                var error = hold - PullPoint(hand);
                var holdVelocityHere = hand.Body != null ? hand.Body.GetPointVelocity(hold) : Vector3.zero;
                holdsVelocity += holdVelocityHere;
                // The player's velocity relative to the hold that closes the gap within this step.
                var wanted = Vector3.ClampMagnitude(error / dt, MaxPull * VrCamera.WorldScale);
                var needed = wanted - (velocity - holdVelocityHere);
                if (needed.sqrMagnitude < 1e-10f) continue;
                var inverse = holding / player.mass;
                if (hand.Movable) inverse += Sharing(hand) * InverseMass(hand.Body, hold, needed.normalized);
                var impulse = needed / inverse;
                change += impulse / player.mass;
                if (hand.Movable) hand.Body.AddForceAtPosition(-impulse, hold, ForceMode.Impulse);
            }
            holdVelocity = holdsVelocity / holding;
            player.velocity = KeepClear(velocity + change, dt);
            foreach (var hand in Hands)
                if (hand.Holding && hand.Firm) TurnWithHand(hand, dt);
            pushOff = Vector3.Lerp(pushOff, player.velocity - holdVelocity, 1f - Mathf.Exp(-dt / PushOffSmoothing));
        }

        // The velocity without what would carry the body's colliders closer than Clearance to anything ahead within this
        // step. Moving along a surface or away from it stays.
        private static Vector3 KeepClear(Vector3 velocity, float dt)
        {
            blocked = false;
            var relative = velocity - holdVelocity;
            var speed = relative.magnitude;
            if (speed < 1e-4f) return velocity;
            var clearance = Clearance * VrCamera.WorldScale;
            var hits = player.SweepTestAll(relative / speed, speed * dt + clearance, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                if (Own(hit.collider)) continue;
                var body = hit.collider.attachedRigidbody;
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
            if (contactLogs++ < 20)
                Plugin.Log.LogInfo($"Climbing: {(soft ? "touches" : "impact with")} '{collision.collider.name}' at {speed:0.00} m/s while holding on");
            return soft;
        }

        // Spins a firmly held part so that it reaches its turn in the hand by the end of the step.
        private static void TurnWithHand(Hand hand, float dt)
        {
            if (hand.Body == null || !hand.Input.TryGetAimRotation(out var aim)) return;
            var wanted = VrCamera.Instance.transform.parent.rotation * aim * hand.FirmTurn;
            (wanted * Quaternion.Inverse(hand.Body.rotation)).ToAngleAxis(out var angle, out var axis);
            if (angle > 180f) angle -= 360f;
            if (Mathf.Abs(angle) < 0.01f || float.IsInfinity(axis.x)) hand.Body.angularVelocity = Vector3.zero;
            else hand.Body.angularVelocity = Vector3.ClampMagnitude(axis * (angle * Mathf.Deg2Rad / dt), FirmSpin);
        }

        private static bool HeldByOther(Hand hand)
        {
            foreach (var each in Hands)
                if (each != hand && each.Holding && each.Body == hand.Body) return true;
            return false;
        }

        // Above the game's grab mass range (GrabController.RawGrabbedMassRange.AboveRange), through reflection: the range's
        // type lives in BBI.Unity.Core, which the plugin does not reference.
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
        private static float InverseMass(Rigidbody body, Vector3 point, Vector3 direction)
        {
            var arm = point - body.worldCenterOfMass;
            var frame = body.rotation * body.inertiaTensorRotation;
            var torque = Quaternion.Inverse(frame) * Vector3.Cross(arm, direction);
            var tensor = body.inertiaTensor;
            var spin = frame * new Vector3(
                tensor.x > 0f ? torque.x / tensor.x : 0f,
                tensor.y > 0f ? torque.y / tensor.y : 0f,
                tensor.z > 0f ? torque.z / tensor.z : 0f);
            return 1f / body.mass + Vector3.Dot(Vector3.Cross(spin, arm), direction);
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

        private static Vector3 Hold(Hand hand) => hand.Anchor.TransformPoint(hand.Point);

        // Where that controller's hand holds on, if it does, for the game's hand (VrHands.Pose).
        public static bool TryGetHold(VrInput.Hand input, out Vector3 point)
        {
            point = Vector3.zero;
            foreach (var hand in Hands)
            {
                if (hand.Input != input || !hand.Holding || hand.Anchor == null) continue;
                point = Vector3.Lerp(Hold(hand), hand.Anchor.TransformPoint(hand.Surface), (Time.time - hand.HeldAt) / SnapTime);
                return true;
            }
            return false;
        }

        // The controller in the world at the body's current physics position.
        private static Vector3 HandWorld(Hand hand) =>
            VrCamera.Instance.transform.parent.TransformPoint(hand.Local) + (player.position - bodyAtRig);

        // The point the body is pulled by: the controller, closer to the head than it really is once it is near (see
        // ClimbContact.Closer).
        private static Vector3 PullPoint(Hand hand) => hand.Movable ? HandWorld(hand) :
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
            VrHand.StopHolding(hand.Vr);
            if (hand.Firm && hand.Body != null) hand.Body.maxAngularVelocity = hand.SpinBefore;
            hand.Firm = false;
            hand.Anchor = null;
            hand.Collider = null;
            hand.Body = null;
            var last = !LeftHand.Holding && !RightHand.Holding;
            var speed = Vector3.ClampMagnitude(pushOff, MaxPushOff * VrCamera.WorldScale);
            if (push && last && player != null) player.velocity = holdVelocity + speed;
            if (logs++ < 80)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand lets go ({reason}){(push && last ? $", flies on at {speed.magnitude:0.00} m/s" : "")}");
            if (last)
            {
                pushOff = Vector3.zero;
                SquishyBack();
            }
        }

        // PlayerMotion.HandleSquishyCollide: moving towards a surface within its range, it pushes the body away and plays
        // a crackle, an effect and a shake. A hand pulling up to the hull would fight it all the time.
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
        // From this far from the head, in real meters, a hand counts as closer than it is, by Gain for each meter nearer.
        private const float CloseFrom = 0.4f;
        private const float Gain = 1.5f;

        // An arm cannot bring its hand to the head: pulled in, it stops about 30 cm ahead, and the body with it, far from a
        // wall it reached for at arm's length. So the last part of a pull counts for more, up to the hand counting as at
        // the head. The game's hand stays on its hold either way (VrHands.Pose).
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
}
