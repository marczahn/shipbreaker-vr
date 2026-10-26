using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace ShipbreakerVR
{
    // Holding on to anything and climbing along it with the hands, as Marc asked: with one hand, with both, or with one
    // while the other works a tool. The grip of a hand that holds no tool and is not at the tool belt (VrToolInput.Free),
    // pressed with a surface within reach of the controller, holds on right where the hand is: that point stays fixed on
    // the thing held and moves with it, and the hand does not drift from it. Moving the hand then moves the player and the
    // thing held against each other, shared by their masses, as two bodies pushing each other would: the station, a fixed
    // hull or anything the game counts as too heavy to move (the static layer, kinematic bodies, masses above
    // GrabController.RawGrabbedMassRange, as the game's own grab decides) does not move at all, so the hand pulls the
    // player along it; a light part comes to the hand instead. With both hands holding, each pulls with half the player's
    // mass, so the body settles between them and hand over hand climbs. Letting go keeps the speed the hands gave the
    // body, so a push off flies on. While the left hand holds on, controller flying rests (VrControls).
    // A grip on one of the game's controls (an InteractableObject the game works by looking at it and pressing interact,
    // like the airlock console's lock panel) works that control instead of holding on: the game's interaction starts on
    // it, and the grip presses interact for as long as it is held (VrToolInput), wherever one looks meanwhile.
    // Each physics step (VrCamera.FixedUpdate) the player's velocity, and the thing's at the hold, is set so the hand
    // reaches its hold by the end of the step: an impulse between the two, as a joint would apply, without joints.
    internal static class VrClimb
    {
        // How far from the controller a surface may be to hold on, and a control to work it, in meters at world scale 1.
        private const float Reach = 0.1f;
        private const float ControlReach = 0.15f;
        // A hand held back this far from its hold (the body stuck behind something, the part torn away) lets go.
        private const float TearOff = 0.5f;
        // How fast the hold may pull the body, and how fast a push off may fly, in m/s at world scale 1.
        private const float MaxPull = 10f;
        private const float MaxPushOff = 5f;
        // The speed a push off keeps is the hands' speed, smoothed over about this time in seconds.
        private const float PushOffSmoothing = 0.08f;
        // LynxUnityEngineExtensions.InStaticLayer: the station and other things that never move.
        private const int StaticLayer = 27;

        private static readonly AccessTools.FieldRef<GrabController, Rigidbody> PlayerBody =
            AccessTools.FieldRefAccess<GrabController, Rigidbody>("m_PlayerRigidbodyReference");

        private static readonly System.Reflection.MethodInfo RawGrabbedMassRange =
            AccessTools.PropertyGetter(typeof(GrabController), "RawGrabbedMassRange");
        private static readonly System.Reflection.MethodInfo StartInteraction =
            AccessTools.Method(typeof(InteractionController), "StartInteraction");

        private class Hand
        {
            public readonly VrInput.Hand Input;
            // The grip as this hand last saw it: the physics may step several times in a frame, or not at all.
            public bool GripDown;
            public bool Tracked;
            // The controller's position in tracking space this physics step.
            public Vector3 Local;
            public bool Holding;
            public Collider Collider;
            // The rigidbody of what is held, if it has one; Movable when the hand may move it.
            public Rigidbody Body;
            public bool Movable;
            // The hold: a point fixed on Anchor (the body, or the collider's transform without one).
            public Transform Anchor;
            public Vector3 Point;
            // The control this hand's grip works, instead of holding on.
            public InteractableObject Control;

            public Hand(VrInput.Hand input) => Input = input;

            public XRNode Node => Input.Node;
            public string Name => Input.Name;
        }

        private static readonly Hand LeftHand = new Hand(VrInput.Left);
        private static readonly Hand RightHand = new Hand(VrInput.Right);
        private static readonly Hand[] Hands = { LeftHand, RightHand };
        private static readonly Collider[] Found = new Collider[32];

        private static GrabController grab;
        private static InteractionController interaction;
        private static Rigidbody player;
        private static float nextSearch;
        // Where the body was when the rig was placed last (RigPlaced).
        private static Vector3 bodyAtRig;
        private static bool rigPlaced;
        // The body's speed relative to its holds, smoothed, and the holds' own speed, for the push off.
        private static Vector3 pushOff;
        private static Vector3 holdVelocity;
        private static int logs;

        // Whether that hand holds on (VrControls: the left one does not fly meanwhile).
        public static bool Holds(XRNode node) => (node == XRNode.LeftHand ? LeftHand : RightHand).Holding;

        // Whether a grip works a control: VrToolInput presses interact, and the game keeps the interaction on it
        // (LineOfSightActivationPatch).
        public static bool WorksControl => LeftHand.Control != null || RightHand.Control != null;

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
            interaction = null;
            player = null;
            rigPlaced = false;
        }

        // VR in gameplay with the option on, and the player's body found.
        private static bool Usable()
        {
            var vr = VrCamera.Instance;
            if (!VrXr.IsRunning || !Plugin.Climbing.Value || vr == null || vr.FollowsHab
                || GameSession.CurrentGameState != GameSession.GameState.Gameplay) return false;
            if ((grab == null || player == null) && Time.unscaledTime >= nextSearch)
            {
                nextSearch = Time.unscaledTime + 1f;
                grab = Object.FindObjectOfType<GrabController>();
                interaction = Object.FindObjectOfType<InteractionController>();
                player = grab != null ? PlayerBody(grab) : null;
                rigPlaced = false;
                if (player != null && logs++ < 80)
                    Plugin.Log.LogInfo($"Climbing: player body '{player.name}', {player.mass:0.#} kg, interpolation {player.interpolation}");
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
            hand.GripDown = down;
            if (hand.Control != null && (!down || !hand.Tracked))
            {
                if (logs++ < 80) Plugin.Log.LogInfo($"Climbing: {hand.Name} hand lets go of the control '{hand.Control.name}'");
                hand.Control = null;
            }
            if (hand.Holding)
            {
                if (!down) LetGo(hand, true, "let go");
                else if (!hand.Tracked) LetGo(hand, false, "tracking lost");
                else if (hand.Anchor == null || !hand.Anchor.gameObject.activeInHierarchy || hand.Collider == null || !hand.Collider.enabled)
                    LetGo(hand, false, "the hold is gone");
            }
            else if (pressed && hand.Tracked && VrToolInput.Free(hand.Node) && !TryWork(hand)) TryHold(hand);
        }

        // Works the control nearest to the hand, if one is within reach: through InteractionController.StartInteraction,
        // as when it comes into view, so the game does the rest (its hand reaching for the control, the progress, the
        // result). Their interaction volumes are triggers, so those count too.
        private static bool TryWork(Hand hand)
        {
            if (interaction == null || StartInteraction == null) return false;
            var at = HandWorld(hand);
            var count = Physics.OverlapSphereNonAlloc(at, ControlReach * VrCamera.WorldScale, Found, Physics.AllLayers, QueryTriggerInteraction.Collide);
            InteractableObject best = null;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var control = Found[i].GetComponentInParent<InteractableObject>();
                if (control == null || control.Asset == null || control.Asset.Data.ActivationType != InteractionActivationType.LineOfSight
                    || !control.IsInteractable || control.IsComplete || !control.CheckConditions()) continue;
                var distance = (Closest(Found[i], at) - at).sqrMagnitude;
                if (distance >= bestDistance) continue;
                best = control;
                bestDistance = distance;
            }
            if (best == null) return false;
            StartInteraction.Invoke(interaction, new object[] { best, null });
            hand.Control = best;
            hand.Input.Buzz(0.5f, 0.06f);
            if (logs++ < 80)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand works the control '{best.name}' < '{(best.transform.parent != null ? best.transform.parent.name : "-")}' ({best.Asset.Data.ProgressType}), {Mathf.Sqrt(bestDistance):0.00} m away");
            return true;
        }

        // Holds on to the surface nearest to the hand, if one is within reach, right where the hand is.
        private static void TryHold(Hand hand)
        {
            var at = HandWorld(hand);
            var reach = Reach * VrCamera.WorldScale;
            var count = Physics.OverlapSphereNonAlloc(at, reach, Found, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Collider best = null;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var collider = Found[i];
                if (Own(collider)) continue;
                var distance = (Closest(collider, at) - at).sqrMagnitude;
                if (distance >= bestDistance) continue;
                best = collider;
                bestDistance = distance;
            }
            if (best == null)
            {
                if (logs++ < 80) Plugin.Log.LogInfo($"Climbing: {hand.Name} hand grips, nothing within {reach:0.00} m");
                return;
            }

            var body = best.attachedRigidbody;
            hand.Collider = best;
            hand.Body = body;
            hand.Movable = body != null && !body.isKinematic && body.gameObject.layer != StaticLayer && body.mass > 0f
                && !TooHeavy(body.mass);
            hand.Anchor = body != null ? body.transform : best.transform;
            hand.Point = hand.Anchor.InverseTransformPoint(at);
            if (!LeftHand.Holding && !RightHand.Holding) pushOff = Vector3.zero;
            hand.Holding = true;
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
                var error = Hold(hand) - HandWorld(hand);
                if (error.magnitude > TearOff * VrCamera.WorldScale) LetGo(hand, false, $"held back {error.magnitude:0.00} m from the hold");
                else holding++;
            }
            if (holding == 0) return;

            var dt = Time.fixedDeltaTime;
            var velocity = player.velocity;
            var change = Vector3.zero;
            var holdsVelocity = Vector3.zero;
            foreach (var hand in Hands)
            {
                if (!hand.Holding) continue;
                var hold = Hold(hand);
                var error = hold - HandWorld(hand);
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
            player.velocity = velocity + change;
            holdVelocity = holdsVelocity / holding;
            pushOff = Vector3.Lerp(pushOff, player.velocity - holdVelocity, 1f - Mathf.Exp(-dt / PushOffSmoothing));
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

        private static Vector3 Hold(Hand hand) => hand.Anchor.TransformPoint(hand.Point);

        // The controller in the world at the body's current physics position.
        private static Vector3 HandWorld(Hand hand) =>
            VrCamera.Instance.transform.parent.TransformPoint(hand.Local) + (player.position - bodyAtRig);

        // The player's own colliders (body, tools) are nothing to hold on to.
        private static bool Own(Collider collider) =>
            collider.attachedRigidbody == player || collider.transform.IsChildOf(player.transform);

        // The point of the collider nearest to the hand; non-convex meshes have none, the hand counts as on them.
        private static Vector3 Closest(Collider collider, Vector3 point)
        {
            if (collider is MeshCollider mesh && !mesh.convex) return point;
            return collider.ClosestPoint(point);
        }

        // Letting go keeps the speed the hands gave the body once the last hand is off: a push off flies on.
        private static void LetGo(Hand hand, bool push, string reason)
        {
            hand.Holding = false;
            hand.Anchor = null;
            hand.Collider = null;
            hand.Body = null;
            var last = !LeftHand.Holding && !RightHand.Holding;
            var speed = Vector3.ClampMagnitude(pushOff, MaxPushOff * VrCamera.WorldScale);
            if (push && last && player != null) player.velocity = holdVelocity + speed;
            if (logs++ < 80)
                Plugin.Log.LogInfo($"Climbing: {hand.Name} hand lets go ({reason}){(push && last ? $", flies on at {speed.magnitude:0.00} m/s" : "")}");
            if (last) pushOff = Vector3.zero;
        }

        private static void LetGoAll()
        {
            LeftHand.Control = RightHand.Control = null;
            foreach (var hand in Hands)
                if (hand.Holding) LetGo(hand, false, "climbing paused");
        }
    }

    // While a grip works a control (VrClimb), the game keeps its interaction on that control instead of switching to
    // whatever is in the middle of the view, or ending it when nothing is.
    [HarmonyPatch(typeof(InteractionController), "HandleLineOfSightActivation")]
    internal static class LineOfSightActivationPatch
    {
        private static bool Prefix() => !VrClimb.WorksControl;
    }
}
