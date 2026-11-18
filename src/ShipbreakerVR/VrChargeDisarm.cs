using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace ShipbreakerVR
{
    // Taking a demo charge off a wall. The game does it through its own interaction: the prompt's button sends the game's
    // right hand to the charge and starts a little game of timing (DemoChargeRetrievalController.OnInteractStart, a
    // GolfSwingController: a ring fills, and the input has to come inside its safe zone), and then the charge is gone,
    // back in the stock. In VR that input never came (Raik, 2026-11-18), and the hand that came was not his.
    // As he asked: the hand that holds on to the charge (either one, VrClimb) takes it off itself. The interact button
    // (A or X) of that hand starts the game's ring for that charge, without the game's hand; the button once more is the
    // ring's input, and the ring decides as in the game (in its safe zone the charge comes off, outside it it goes off).
    // Coming off, the charge on the wall goes back to the stock the game's way and one from the stock is let go in its
    // place, afloat and in that hand, which holds it as any loose part. For NoStickTime it does not cling to the wall
    // again, near as it is. The game's own interaction is left out while a hand holds a charge on a wall (VrToolInput),
    // so its hand does not come. A ring that the game's prompt started gets its input from the button as well.
    // The charge that came off glides into the hand's own hold of a charge (VrChargeByHand.HoldPose; Raik, 2026-11-18:
    // "the hold one has automatically with disarmed mines") within GlideTime, and the hand takes hold of it there.
    // With the charges placed the game's way (Sprengsatzplatzierung = Automatisch) there is no charge of its own: the
    // one that came off stays in the stock, and the hand gets the charges as the game's tool, with the preview and the
    // trigger (VrToolInput.TakeCharges).
    internal static class VrChargeDisarm
    {
        // The ring: one that starts within this of a charge's interaction is the charge's, and the button counts only
        // this long after its start, so the press that started it does not end it.
        private const float StartsWithin = 1f;
        private const float NotBefore = 0.25f;
        // By hand: the second press within this of the first, the new charge is taken hold of within TakeWithin, and it
        // does not cling to a wall for NoStickTime (seconds).
        private const float TakeWithin = 0.8f;
        private const float NoStickTime = 1.5f;
        // The charge that came off is in the hand's hold after GlideTime (seconds), at MostGlide (m/s) and MostGlideTurn
        // (rad/s) at most; within GlideNear (m) and GlideTurnNear (degrees) of it, it is there.
        private const float GlideTime = 0.18f;
        private const float MostGlide = 4f;
        private const float MostGlideTurn = 20f;
        private const float GlideNear = 0.01f;
        private const float GlideTurnNear = 3f;

        private static readonly AccessTools.FieldRef<GolfSwingController, bool> InSafeZone =
            AccessTools.FieldRefAccess<GolfSwingController, bool>("mInSafeZone");
        private static readonly AccessTools.FieldRef<GolfSwingController, float> DelayTime =
            AccessTools.FieldRefAccess<GolfSwingController, float>("mCurrentDelayTime");
        private static readonly AccessTools.FieldRef<GolfSwingController, float> DelayAmount =
            AccessTools.FieldRefAccess<GolfSwingController, float>("mStartDelayAmount");
        private static readonly AccessTools.FieldRef<DemoChargeRetrievalController, bool> Deployed =
            AccessTools.FieldRefAccess<DemoChargeRetrievalController, bool>("mIsDeployed");

        private static float chargeAt = -10f;
        private static float ringAt;
        private static bool mine;
        private static int logs;

        private sealed class Held
        {
            public Collider Collider;
            public DemoChargeRetrievalController Charge;
        }

        // The charge whose ring a hand started, that hand, and where the charge was.
        private static DemoChargeRetrievalController ringCharge;
        private static VrInput.Hand ringHand;
        private static Vector3 ringPlace;
        private static Quaternion ringTurn;

        private static readonly Held[] held = { new Held(), new Held() };

        // The charge that came off: taken hold of by that hand once its body is there, and kept from clinging.
        private static StructurePart loose;
        private static VrInput.Hand looseHand;
        private static float looseAt;
        private static bool looseTaken;
        private static bool looseGlides;
        private static bool looseSets;
        private static float glideUntil;
        private static Entity stickyOn;
        private static LayerMask stickyMask;
        private static bool stickyOff;

        // True while a hand holds on to a charge on a wall: the game's interaction stays out (VrToolInput).
        public static bool Holds { get; private set; }

        // From VrFrame, once a frame after the hands held on or let go (VrClimb).
        public static void Track()
        {
            Holds = false;
            if (VrXr.IsRunning)
            {
                Check(VrInput.Left, held[0]);
                Check(VrInput.Right, held[1]);
            }
            Loose();
        }

        private static void Check(VrInput.Hand hand, Held state)
        {
            var collider = VrClimb.HeldCollider(hand);
            if (collider != state.Collider)
            {
                state.Collider = collider;
                state.Charge = null;
                var part = collider != null ? collider.GetComponentInParent<StructurePart>() : null;
                var charge = part != null ? part.GetComponentInChildren<DemoChargeRetrievalController>(true) : null;
                if (charge != null && charge.DemoCharge == part) state.Charge = charge;
            }
            var on = state.Charge;
            // Only one on a wall that the game lets be taken off (its own object is on then, the removable grip unlocked).
            if (on == null || !Deployed(on)) return;
            // Its ring is up (the game switches the charge's own object off meanwhile): the hand stays the one that works it.
            if (on == ringCharge)
            {
                Holds = true;
                return;
            }
            // Only one on a wall that the game lets be taken off (its own object is on then, the removable grip unlocked).
            if (!on.gameObject.activeInHierarchy) return;
            Holds = true;
            if (!hand.Primary.Pressed || ringCharge != null || on.DemoCharge == null) return;
            ringCharge = on;
            ringHand = hand;
            // Made at its mesh's place, the new charge's back will be a mesh's depth (BackOfMesh) off the wall.
            ringPlace = on.DemoCharge.transform.position;
            ringTurn = on.DemoCharge.transform.rotation;
            hand.Buzz(0.3f, 0.05f);
            if (logs++ < 30) Plugin.Log.LogInfo($"Charge disarm: {hand.Name} hand on a charge on the wall starts its ring");
            on.OnInteractStart();
        }

        // From ChargeDisarmSuccessPatch: the game has taken that charge back to the stock. If a hand's ring did it, one
        // from the stock is let go in its place, into that hand.
        public static void Succeeded(DemoChargeRetrievalController charge)
        {
            if (charge == null || charge != ringCharge) return;
            ringCharge = null;
            var hand = ringHand;
            hand.Buzz(0.8f, 0.12f);
            if (!VrChargeByHand.Manual)
            {
                VrClimb.Drop(hand, "the demo charge came off");
                VrToolInput.TakeCharges(hand);
                if (logs++ < 30) Plugin.Log.LogInfo($"Charge disarm: taken off by the {hand.Name} hand, back in the stock; the hand gets the charges as the game's tool");
                return;
            }
            var fresh = VrChargeByHand.LetGoFromStock(ringPlace, ringTurn);
            if (fresh == null)
            {
                if (logs++ < 30) Plugin.Log.LogInfo($"Charge disarm: taken off by the {hand.Name} hand, back in the stock (no charge to hold could be made)");
                return;
            }
            loose = fresh;
            looseHand = hand;
            looseAt = Time.unscaledTime;
            looseTaken = false;
            looseGlides = true;
            looseSets = false;
            glideUntil = -1f;
            stickyOff = false;
            if (logs++ < 30) Plugin.Log.LogInfo($"Charge disarm: taken off by the {hand.Name} hand, which holds it now");
        }

        // For VrChargeByHand: that charge, just made, goes into that hand the same way. intoHold: as the hand takes hold
        // of it, it is set into the hand's own hold of a charge (a charge taken out from behind the back: made in that
        // hold, the hand had moved on by the time the charge had its body, some frames later, and held it askew). Not a
        // charge handed over: that one stays as the hand took hold of it.
        public static void Give(StructurePart part, VrInput.Hand hand, bool intoHold = false)
        {
            loose = part;
            looseHand = hand;
            looseAt = Time.unscaledTime;
            looseTaken = false;
            looseGlides = false;
            looseSets = intoHold;
            stickyOff = false;
        }

        // The charge just made is set where that hand holds a charge now, at rest against the player's body.
        private static void SetIntoHold(Rigidbody body)
        {
            if (!looseSets || !VrChargeByHand.HoldPose(looseHand, out var origin, out var turned)) return;
            looseSets = false;
            var part = loose.transform;
            var turn = turned * Quaternion.Inverse(part.rotation);
            var rotation = turn * body.rotation;
            var position = origin + turn * (body.position - part.position);
            body.transform.SetPositionAndRotation(position, rotation);
            body.position = position;
            body.rotation = rotation;
            Physics.SyncTransforms();
            var player = VrToolCollide.Player();
            body.velocity = player != null ? player.velocity : Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        // The charge that came off, on its way into the hand's hold: true while it still glides there, by its speed, so
        // it meets what is in its way. Then the hand takes hold of it.
        private static bool Glides(Rigidbody body)
        {
            if (!looseGlides) return false;
            if (glideUntil < 0f) glideUntil = Time.unscaledTime + GlideTime;
            var left = glideUntil - Time.unscaledTime;
            var player = VrToolCollide.Player();
            var along = player != null ? player.velocity : Vector3.zero;
            var origin = Vector3.zero;
            var turned = Quaternion.identity;
            var gliding = left > 0f && VrChargeByHand.HoldPose(looseHand, out origin, out turned);
            if (gliding)
            {
                var part = loose.transform;
                var scale = VrCamera.WorldScale;
                var way = origin - part.position;
                (turned * Quaternion.Inverse(part.rotation)).ToAngleAxis(out var angle, out var axis);
                if (angle > 180f) angle -= 360f;
                gliding = way.magnitude > GlideNear * scale || Mathf.Abs(angle) > GlideTurnNear;
                if (gliding)
                {
                    var step = Mathf.Max(left, Time.fixedDeltaTime);
                    body.velocity = along + Vector3.ClampMagnitude(way / step, MostGlide * scale);
                    body.angularVelocity = float.IsInfinity(axis.x) || float.IsNaN(axis.x)
                        ? Vector3.zero
                        : Vector3.ClampMagnitude(axis * (angle * Mathf.Deg2Rad / step), MostGlideTurn);
                    return true;
                }
            }
            looseGlides = false;
            body.velocity = along;
            body.angularVelocity = Vector3.zero;
            return false;
        }

        // The charge that came off, each frame: into the hand, and no clinging for a while.
        private static void Loose()
        {
            if (loose == null) return;
            var since = Time.unscaledTime - looseAt;
            if (!looseTaken && since <= TakeWithin && looseHand.Grip.Down)
            {
                var body = loose.GetComponentInParent<Rigidbody>();
                var collider = loose.GetComponent<Collider>();
                if (body != null && !body.isKinematic && collider != null && !Glides(body))
                {
                    SetIntoHold(body);
                    looseTaken = VrClimb.HoldOn(looseHand, collider);
                }
            }
            // The game makes a charge afloat cling to what it touches once its automatic deploying is unlocked (Sticky on
            // what the charge hangs on): taken away for the time, then given back.
            if (EntityBlueprintComponent.IsValid(loose.EntityBlueprintComponent))
            {
                var manager = loose.EntityBlueprintComponent.EntityManager;
                var entity = loose.EntityBlueprintComponent.Entity;
                if (since < NoStickTime)
                {
                    if (manager.Exists(entity) && manager.HasComponent<HierarchyMember>(entity))
                    {
                        var parent = manager.GetComponentData<HierarchyMember>(entity).Parent;
                        if (manager.Exists(parent) && manager.HasComponent<Sticky>(parent))
                        {
                            stickyMask = manager.GetComponentData<Sticky>(parent).LayerMask;
                            stickyOn = parent;
                            stickyOff = true;
                            manager.RemoveComponent<Sticky>(parent);
                        }
                    }
                    return;
                }
                if (stickyOff && manager.Exists(stickyOn) && !manager.HasComponent<Sticky>(stickyOn))
                    manager.AddComponentData(stickyOn, new Sticky { LayerMask = stickyMask });
            }
            else if (since < NoStickTime) return;
            loose = null;
            stickyOff = false;
        }

        // ---- The game's ring, should it come up ----

        // A demo charge is being interacted with: a ring that starts right after is its own.
        public static void ChargeStarts() => chargeAt = Time.unscaledTime;

        public static void RingStarts()
        {
            mine = VrXr.IsRunning && Time.unscaledTime - chargeAt < StartsWithin;
            ringAt = Time.unscaledTime;
            if (mine && logs++ < 30) Plugin.Log.LogInfo("Charge disarm: the ring is up, the interact button once more is its input");
        }

        public static void RingEnds()
        {
            mine = false;
            ringCharge = null;
        }

        // Each frame while the ring runs.
        public static void Tick(GolfSwingController ring)
        {
            if (!mine || !ring.enabled || Time.unscaledTime - ringAt < NotBefore) return;
            if (!VrInput.Left.Primary.Pressed && !VrInput.Right.Primary.Pressed) return;
            // The game's own input: the ring decides (Raik, 2026-11-18: the little game stays). Not during its starting
            // delay, when the game does not take it either.
            if (DelayTime(ring) < DelayAmount(ring)) return;
            if (logs++ < 30) Plugin.Log.LogInfo($"Charge disarm: the interact button with the ring {(InSafeZone(ring) ? "in" : "outside")} its safe zone");
            ring.OnInput();
        }
    }

    [HarmonyPatch(typeof(DemoChargeRetrievalController), nameof(DemoChargeRetrievalController.OnInteractStart))]
    internal static class ChargeDisarmStartPatch
    {
        private static void Prefix() => VrChargeDisarm.ChargeStarts();
    }

    [HarmonyPatch(typeof(DemoChargeRetrievalController), nameof(DemoChargeRetrievalController.OnSuccess))]
    internal static class ChargeDisarmSuccessPatch
    {
        private static void Postfix(DemoChargeRetrievalController __instance) => VrChargeDisarm.Succeeded(__instance);
    }

    [HarmonyPatch(typeof(GolfSwingController), "OnListenerRegistered")]
    internal static class ChargeDisarmRingPatch
    {
        private static void Postfix() => VrChargeDisarm.RingStarts();
    }

    [HarmonyPatch(typeof(GolfSwingController), "Update")]
    internal static class ChargeDisarmTickPatch
    {
        private static void Postfix(GolfSwingController __instance) => VrChargeDisarm.Tick(__instance);
    }

    [HarmonyPatch(typeof(GolfSwingController), "ExitGolfSwingScreen")]
    internal static class ChargeDisarmEndPatch
    {
        private static void Postfix() => VrChargeDisarm.RingEnds();
    }
}
