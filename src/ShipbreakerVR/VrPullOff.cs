using System.Collections.Generic;
using BBI.Unity.Game;
using BepInEx.Configuration;
using Carbon.Audio;
using Carbon.Core.Unity;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace ShipbreakerVR
{
    // Things to use fixed on a ship's wall (first aid kits, oxygen and fuel tanks, repair kits) are pulled off by hand
    // (Raik, 2026-11-11), as a cargo container is torn off with the grapple: the hand holds one (VrClimb holds it as a
    // surface, the hand flat on it) and pulls away from the wall. From Start on the controller buzzes, the harder the
    // further; from Enough on the thing tilts off the wall by up to Tilt degrees, and pulled that hard for Hold seconds (a poster PosterHold) it
    // comes loose and flies into the hand over Fly seconds, a quick move, not a jump. There it is a loose part like any
    // other: held, thrown, let go, and let go over the left shoulder it is used as with A (VrShoulderStow).
    // It comes off only while the other hand holds on to the ship's wall (Raik, 2026-11-11): then the hand on it holds free
    // (VrClimb.SetFree) and pulls neither the body nor the ship. Without that it is a hold like the wall, to climb along.
    // Let go before it came off, the thing goes back to where it was.
    // Also a thing to collect the game gives no control (a damaged fuel can, 'SM_Collectible_..._Damaged', Raik 2026-11-11):
    // held as a surface, it comes off the same way and is then a loose part, with nothing to use.
    // The game has no loose form of them: they are parts of the ship's body. Pulled off, the thing's own part (its
    // StructurePart, smaller than Largest) leaves the ship and gets a rigidbody of Mass. A poster is one too (its part
    // 'SM_Props_Signage_Portrait' is larger than 1 m). Coming off it plays what the game plays as its joint breaks (BreakableJointComponent.BreakJoints): the part's own
    // break effect and sound where it last touched its mount, and the grapple's sound of a joint torn off
    // (BreakableSettings). Without a break effect of its own, the game's sparks of metal hitting metal
    // (HierarchyCollisionListener; the light ones are only for parts of 10 kg and more, none matched, Raik 2026-11-11).
    // In space: no gravity on it (Raik: thrown, it fell down).
    // While the hand is on it, the game hears the player touch it (VrTouchSound): its sounds come through the suit.
    internal static class VrPullOff
    {
        private const float Start = 0.03f;
        private const float Enough = 0.08f;
        // A part held as a surface (no control, the damaged fuel can) is pulled from this much further out (Raik).
        private const float SurfaceExtra = 0.03f;
        private const float Tilt = 7f;
        private const float TiltTime = 0.3f;
        // Pulled this long it comes off; a poster, light and only stuck on, sooner (Raik, 2026-11-11: 2 s and 1 s, from 3 s).
        private const float Hold = 2f;
        private const float PosterHold = 1f;
        // A demo charge on a wall holds on harder, a little different each time (Raik's list of 2026-11-21: three to
        // four seconds, in place of the game's little game of timing; VrChargeDisarm).
        private const float ChargeHoldLeast = 3f;
        private const float ChargeHoldMost = 4f;
        // And it lifts off the wall only a little while pulled (Raik, 2026-11-22: the 7 degrees of the things on walls
        // were far too much for it; about 3, then it comes off).
        private const float ChargeTilt = 3f;
        // A demo charge stays on its sticky side while pulled (Raik, 2026-11-22: "pulling lifts the mine off until it
        // comes loose, that is a bit silly"): it tips about its edge at the wrist, which stays on the wall, and the edge
        // at the finger tips comes off by ChargeLift only (m at world scale 1, "a few millimetres"), until its time is
        // up. Before, it tipped about a point 1.7 m beside it and so came off the wall whole, by 9 cm at 3 degrees: the
        // size it was measured by (Size, all its renderers) is 4.95 m with what the charge draws around itself. Its
        // size is now that of its own mesh.
        private const float ChargeLift = 0.005f;
        private const string ChargeMesh = "SM_Props_Demo_Charge";

        // Taking things apart by hand (Raik's list of 2026-11-21, point 12: "pulling apart"; his test of 2026-11-22:
        // the computers do not come apart by hand). Many parts sit on the ship by a joint that gives way to pulling
        // (BreakableJointComponent: what the grapple yanks off, computers and their parts among them). The game adds
        // up the pull on such a joint, each physics step between 1 and 1.5 whatever the force (its assets clamp it),
        // until the joint's durability is used up (50 for what yanks off easily, 200 otherwise); on the way the part
        // tilts and creaks, and then its joints break and it is a loose part. A hand that holds such a part, with the
        // other hand on the ship, and pulls it away gives the joint the same pull, YankRate times a second (the
        // grapple: 50), so an easy part takes two to three seconds of hard pulling and a firm one about ten. The game
        // does the rest: which joints break, the tilt, the sounds. Off unless Experimente/HandZerlegen is switched on:
        // Raik meant something else by taking computers apart (the devices stripped for the truck's parts, Extract),
        // and this lets a hand take off everything the grapple can.
        private const float YankRate = 15f;
        private const float YankForce = 1.5f;
        // The heaviest part a hand takes apart, by the game's own mass of what breaks off (kg).
        private const float YankMost = 60f;
        // After its joints broke, the game makes the part a loose object within this long; then the hand holds that.
        private const float YankLooseWait = 1f;
        private static ConfigEntry<bool> yankOn;

        // Stripping a device for a part of the truck (Raik, 2026-11-22: "the computers, terminals and so on that one
        // strips for spare parts"). In the game these have a prompt: its button held for a second takes the part out
        // and leaves the device wrecked (their interaction's kind is 'SalvageShort': computer terminal, data bank,
        // comms array, power junction, tool arm, hoist and the like). In VR the hand does it instead of the button: one
        // hand on the ship, the other holds the device and pulls away from it, for ExtractHold seconds; then the
        // interaction completes as the game completes it, and the game hands the part out and wrecks the device. The
        // device neither tilts nor comes off. The button does nothing for a device a hand holds (VrToolInput).
        // A second way (Raik after his test, 2026-11-22: "not that one has to hold the ship, that stays as a variant"):
        // one hand holds the device, the other, its grip closed, is close to it (PairWithin between the two controllers
        // as the two come together; Raik: "at most 20 cm" between the hands), on the device as well or beside it, and
        // then the two go apart: one holds, the other pulls the part out, whichever of them.
        // And a third (VrDevicePunch): three blows of the fist.
        private const string ExtractKind = "SalvageShort_InteractionTypeAsset";
        private const float ExtractHold = 3f;
        // Between the controllers, which sit in the palms: about 20 cm between the hands themselves.
        private const float PairWithin = 0.25f;
        private static readonly AccessTools.FieldRef<InteractableObject, InteractableObjectAsset> AssetOf =
            AccessTools.FieldRefAccess<InteractableObject, InteractableObjectAsset>("m_Asset");
        // Whether an interaction's kind is the stripping one, looked up once for each kind (its name is a new string
        // every time it is asked for).
        private static readonly Dictionary<InteractionTypeAsset, bool> ExtractKinds = new Dictionary<InteractionTypeAsset, bool>();

        // A device that can still be stripped for its part.
        public static bool Extractable(InteractableObject device)
        {
            if (device == null || !device.isActiveAndEnabled || !device.IsInteractable || !device.HasUsesRemaining) return false;
            var asset = AssetOf(device);
            var kind = asset != null ? asset.Data.InteractionType : null;
            if (kind == null) return false;
            if (!ExtractKinds.TryGetValue(kind, out var strips))
            {
                strips = kind.name == ExtractKind;
                ExtractKinds[kind] = strips;
            }
            return strips;
        }

        // True while a hand holds a device to strip it: its prompt's button is left out then (VrToolInput).
        public static bool Extracting => Left.Extract || Right.Extract;

        private static bool YankOn
        {
            get
            {
                if (yankOn == null && VrSettings.File != null)
                    yankOn = VrSettings.Bind("Experimente", "HandZerlegen", false,
                        "Experiment: a part that the grapple can yank off (computers and the like) also comes off by hand: hold the ship with one hand, the part with the other, and pull it away.");
                return yankOn != null && yankOn.Value;
            }
        }
        private const float Fly = 0.18f;
        private const float Largest = 2.5f;
        private const float Mass = 4f;

        private class Pull
        {
            public VrInput.Hand Input;
            // The control held (InteractableObject), or the part held where the game gives it none.
            public Component Thing;
            public Transform Root;
            public Transform Parent;
            public Vector3 RestPosition;
            public Quaternion RestRotation;
            public Vector3 Normal;
            public Vector3 Surface;
            public float Pulled;
            // How long it has to be pulled to come off (HoldFor), chosen as the hand takes hold.
            public float Needed;
            // How far it tilts off the wall while pulled hard, in degrees.
            public float Tilt = VrPullOff.Tilt;
            // A part on a joint that gives way (Yank): the pull still owed to the joint, and when its joints broke.
            public float YankDue;
            public float BrokeAt = -1f;
            // A demo charge tips about one edge of its own (Edge): a point on that edge, the way to the edge that comes
            // off and the way out of the wall, all in the charge's own space as it sits.
            public bool Edge;
            public Vector3 Hinge;
            public Vector3 LiftWay;
            public Vector3 Out;
            // A device stripped for its part (Extract): pulled at, it stays as it is. Since when the hand holds it; how
            // far apart the two controllers were as the other hand came close with its grip closed (StripPull.Unpaired
            // without one, StripPull.TooFar with one too far away), in m at world scale 1; and how it is held, for the log.
            public bool Extract;
            public float Since;
            public float PairFrom = StripPull.Unpaired;
            public string Mode = "";
            public float Angle;
            public Vector3 Contact;
            public bool Braced;
            public Rigidbody Ship;
            // Flying into the hand: from where, which point of it goes to the hand, since when.
            public Rigidbody Body;
            public Vector3 FromPosition;
            public Quaternion FromRotation;
            public Vector3 HeldOn;
            public float FlyStart = -1f;
            // The other parts of its group, which come off with it (Member).
            public readonly List<Member> Members = new List<Member>();
        }

        // A part of the same group as the part pulled (the large coolant tank: can, cap and valve, 'GRP_Collectible_Coolant_XL';
        // Raik, 2026-11-19: each came off by itself, the others stayed on the wall). Hung on the part pulled while the
        // hand is on it, so it tilts and comes off with it as one body; where it was, to go back.
        private struct Member
        {
            public Transform Part;
            public Transform Parent;
            public Vector3 Position;
            public Quaternion Rotation;
            // Hung on the part pulled, Unity gives it another local scale wherever that part is scaled or turned against
            // it (it keeps its size in the world); back on its own parent it needs its own again, or cap and valve sit
            // there in another size than before.
            public Vector3 Scale;
        }

        // The bodies made here for things pulled off: a part on one of them is no longer on the ship.
        private static readonly HashSet<Rigidbody> Made = new HashSet<Rigidbody>();
        private static readonly Collider[] Around = new Collider[64];

        private static readonly Pull Left = new Pull { Input = VrInput.Left };
        private static readonly Pull Right = new Pull { Input = VrInput.Right };
        private static readonly List<Collider> Colliders = new List<Collider>();
        // Things found not to come off (no part of their own), told once.
        private static readonly HashSet<Component> Refused = new HashSet<Component>();
        private const string Collectible = "SM_Collectible_";
        private static int logs;
        private static int stripLogs;

        // From VrFrame once the rig is placed, after the hands took hold or let go.
        public static void Update()
        {
            if (!VrXr.IsRunning || VrSettings.Climbing == null || !VrSettings.Climbing.Value)
            {
                Reset(Left);
                Reset(Right);
                return;
            }
            Step(Left);
            Step(Right);
        }

        private static void Step(Pull pull)
        {
            if (pull.FlyStart >= 0f)
            {
                Flying(pull);
                return;
            }
            var thing = Held(pull.Input);
            // A part whose joints just broke is no longer one to pull off, but the hand's business with it is not over
            // (Broke): it stays the hand's thing while the grip is held.
            var breaking = pull.Thing is BreakableJointComponent was && was != null && pull.Root != null && pull.Input.Grip.Down
                && (pull.BrokeAt >= 0f || was.IsBroken);
            if (thing != pull.Thing && !breaking)
            {
                Reset(pull);
                if (thing == null || Refused.Contains(thing) || !Begin(pull, thing)) return;
            }
            if (pull.Thing == null) return;
            if (pull.Root == null || !pull.Thing.gameObject.activeInHierarchy)
            {
                Reset(pull);
                return;
            }
            if (!Controller(pull.Input, out var hand)) return;
            var joint = pull.Thing as BreakableJointComponent;
            if (joint != null && Broke(pull, joint)) return;
            if (pull.Extract)
            {
                Strip(pull, hand);
                return;
            }
            var braced = OnWall(pull);
            if (braced != pull.Braced)
            {
                pull.Braced = braced;
                VrClimb.SetFree(pull.Input, braced, false);
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{pull.Thing.name}' {(braced ? "can be pulled off, the other hand holds the ship" : "is a hold to climb on again")}");
            }
            if (joint != null)
            {
                // The game tilts the part as its joint gives way; the mod neither tilts it nor puts it back.
                if (braced) Yank(pull, joint, hand);
                return;
            }
            if (!braced)
            {
                pull.Pulled = 0f;
                pull.Angle = Mathf.MoveTowards(pull.Angle, 0f, pull.Tilt / TiltTime * Time.deltaTime);
                Pose(pull, hand);
                return;
            }
            // How far the controller is from where the hand holds, away from the wall.
            var away = Vector3.Dot(hand - pull.Root.TransformPoint(pull.Surface), pull.Normal);
            var scale = VrCamera.WorldScale;
            if (!(pull.Thing is InteractableObject)) away -= SurfaceExtra * scale;
            if (away > Start * scale)
                pull.Input.Buzz(Mathf.Lerp(0.1f, 0.7f, Mathf.InverseLerp(Start * scale, Enough * scale, away)), 0.05f);
            var hard = away >= Enough * scale;
            pull.Pulled = hard ? pull.Pulled + Time.deltaTime : 0f;
            // Tilted off the wall while pulled hard, back while not.
            var wanted = hard ? pull.Tilt : 0f;
            pull.Angle = Mathf.MoveTowards(pull.Angle, wanted, pull.Tilt / TiltTime * Time.deltaTime);
            Pose(pull, hand);
            if (pull.Pulled < HoldFor(pull)) return;
            if (pull.Thing is DemoChargeRetrievalController charge) TakeOff(pull, charge);
            else Loosen(pull, hand);
        }

        // The hand holds a device to strip it. It pulls at it in one of two ways: the other hand holds the ship, and this
        // one goes away from its hold on the device; or the other hand is close with its grip closed (PairWithin), and
        // the two go apart. From Enough on it counts, the buzz growing with how far it is along, and after ExtractHold
        // seconds the device is stripped. With the other hand on the ship this hand is held free of the body, as with
        // everything pulled off. With both hands on the device, the one that came later is: it pulls without pulling the
        // body about, and the first is the body's hold. (Pulled with the first instead, the body goes towards the
        // device; the two hands go apart all the same.)
        private static void Strip(Pull pull, Vector3 hand)
        {
            var other = pull.Input.Other;
            var twin = pull == Left ? Right : Left;
            var scale = VrCamera.WorldScale;
            var both = twin.Extract && twin.Thing == pull.Thing;
            var onShip = !both && OnWall(pull);
            var apart = 0f;
            if (other.Grip.Down && !VrHand.Of(other.Node).HoldsTool && Controller(other, out var second))
            {
                var between = Vector3.Distance(hand, second) / scale;
                if (pull.PairFrom == StripPull.Unpaired) pull.PairFrom = StripPull.Begin(between, PairWithin);
                if (pull.PairFrom >= 0f) apart = between - pull.PairFrom;
            }
            else pull.PairFrom = StripPull.Unpaired;
            var paired = pull.PairFrom >= 0f;
            var mode = onShip ? (paired ? "the other hand holds the ship, close by" : "the other hand holds the ship")
                : paired ? (both ? "both hands on it" : "the other hand closed beside it")
                : pull.PairFrom == StripPull.TooFar ? "the other hand is closed, but came further away than it counts" : "";
            if (mode != pull.Mode)
            {
                pull.Mode = mode;
                if (mode != "" && stripLogs++ < 60)
                    Plugin.Log.LogInfo($"Pull off: the device in the {pull.Input.Name} hand: {mode}"
                        + (paired ? $" ({pull.PairFrom * 100f:0} cm between the hands, {PairWithin * 100f:0} cm at most)" : ""));
            }
            var free = onShip || (both && paired && pull.Since > twin.Since);
            if (free != pull.Braced)
            {
                pull.Braced = free;
                VrClimb.SetFree(pull.Input, free, false);
            }
            var away = Vector3.Dot(hand - pull.Root.TransformPoint(pull.Surface), pull.Normal) / scale;
            var far = StripPull.Far(onShip, away, paired, apart);
            var hard = far >= Enough;
            pull.Pulled = hard ? pull.Pulled + Time.deltaTime : 0f;
            if (hard)
            {
                var buzz = Mathf.Lerp(0.25f, 0.9f, pull.Pulled / pull.Needed);
                pull.Input.Buzz(buzz, 0.05f);
                // The other hand feels it too where the two pull against each other.
                if (paired && apart >= Enough) other.Buzz(buzz, 0.05f);
            }
            else if (far > Start) pull.Input.Buzz(0.1f, 0.05f);
            if (pull.Pulled < pull.Needed) return;
            StripNow(pull.Thing as InteractableObject, pull.Input, $"after {pull.Pulled:0.0} s of pulling ({mode})");
        }

        // The device is stripped as the game does it at the end of its prompt: the interaction completes (the part comes
        // out, the device is wrecked) and the game's listeners hear of it. The hands that held it to strip it are plain
        // holds on it again. False if it is not one to strip (any more).
        internal static bool StripNow(InteractableObject device, VrInput.Hand input, string how)
        {
            EndStrip(Left, device);
            EndStrip(Right, device);
            if (!Extractable(device)) return false;
            var part = device.GetComponentInParent<StructurePart>();
            var name = part != null ? part.name : device.name;
            device.Complete();
            ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(InteractionEvent.GetEvent(InteractionEvent.InteractionType.ProgressComplete, device));
            input.Buzz(1f, 0.15f);
            if (stripLogs++ < 60) Plugin.Log.LogInfo($"Pull off: '{name}' stripped for its part by the {input.Name} hand {how}");
            return true;
        }

        private static void EndStrip(Pull pull, InteractableObject device)
        {
            if (!pull.Extract || pull.Thing != device) return;
            var input = pull.Input;
            Clear(pull);
            // The hand is a hold on the device again, not free of the body.
            VrClimb.SetFree(input, false, false);
        }

        // A blow on a device (VrDevicePunch): the game's sparks of metal on metal where it lands, and the sound the game
        // plays as the grapple breaks a part off, on the device's part (heard through the suit).
        internal static void Blow(InteractableObject device, Rigidbody ship, Vector3 at, Vector3 normal)
        {
            var effect = Sparks(ship);
            if (effect != null && GameSession.SpawnPoolManager != null)
                GameSession.SpawnPoolManager.SpawnObject(effect, at, Quaternion.LookRotation(normal), null, null);
            var settings = Main.Instance != null && Main.Instance.MainSettings != null ? Main.Instance.MainSettings.BreakableSettings : null;
            if (settings != null) Play(settings.GrappleBreakJointSuccessSFX, device.GetComponentInParent<EntityBlueprintComponent>(), at);
        }

        private static bool BeginStrip(Pull pull, InteractableObject device)
        {
            var held = VrClimb.HeldCollider(pull.Input);
            if (held == null || !VrClimb.TryGetSurface(pull.Input, out var surface) || !VrClimb.TryGetGrip(pull.Input, out _, out var normal)) return false;
            var root = held.transform;
            pull.Thing = device;
            pull.Root = root;
            pull.Parent = root.parent;
            pull.RestPosition = root.localPosition;
            pull.RestRotation = root.localRotation;
            pull.Normal = normal.normalized;
            pull.Surface = root.InverseTransformPoint(surface);
            pull.Pulled = 0f;
            pull.Needed = ExtractHold;
            pull.Tilt = 0f;
            pull.Angle = 0f;
            pull.Braced = false;
            pull.Ship = device.GetComponentInParent<Rigidbody>();
            pull.Extract = true;
            pull.Since = Time.time;
            pull.PairFrom = StripPull.Unpaired;
            pull.Mode = "";
            pull.Members.Clear();
            if (stripLogs++ < 60) Plugin.Log.LogInfo($"Pull off: {pull.Input.Name} hand holds '{device.name}', a device to strip for its part: pulled for {ExtractHold:0} s, with the other hand on the ship or the two hands going apart");
            return true;
        }

        // The hand's pull on a joint that gives way, as the grapple's rope gives it (LaserRope: ApplyForce each physics
        // step): from Enough away from the hold on, YankRate times a second.
        private static void Yank(Pull pull, BreakableJointComponent joint, Vector3 hand)
        {
            var from = pull.Root.TransformPoint(pull.Surface);
            var away = Vector3.Dot(hand - from, pull.Normal) - SurfaceExtra * VrCamera.WorldScale;
            var scale = VrCamera.WorldScale;
            if (away < Enough * scale)
            {
                pull.YankDue = 0f;
                if (away > Start * scale) pull.Input.Buzz(0.1f, 0.05f);
                return;
            }
            // The nearer to breaking, the harder the buzz.
            pull.Input.Buzz(Mathf.Lerp(0.25f, 0.9f, 1f - joint.GetJointDurability), 0.05f);
            pull.YankDue += YankRate * Time.deltaTime;
            var times = Mathf.Min((int)pull.YankDue, 3);
            if (times <= 0) return;
            pull.YankDue -= times;
            var part = pull.Root.GetComponent<StructurePart>();
            var blueprint = part != null ? part.EntityBlueprintComponent : null;
            if (!EntityBlueprintComponent.IsValid(blueprint)) return;
            var way = (hand - from).sqrMagnitude > 1e-6f ? (hand - from).normalized : pull.Normal;
            var commands = new EntityCommandBuffer(Allocator.TempJob);
            try
            {
                for (var i = 0; i < times; i++) joint.ApplyForce(YankForce, way, BreakableJointComponent.ForceAppliedBy.Grapple, commands);
                commands.Playback(blueprint.EntityManager);
            }
            catch (System.Exception e)
            {
                Refused.Add(joint);
                Plugin.Log.LogWarning($"Pull off: pulling '{pull.Root.name}' apart by hand failed ({e.Message}), it is left alone");
            }
            finally { commands.Dispose(); }
        }

        // The joints of the part broke: once the game has made it a loose object of its own, the hand that pulled holds
        // that. True while this is all there is to do for the hand.
        private static bool Broke(Pull pull, BreakableJointComponent joint)
        {
            if (pull.BrokeAt < 0f)
            {
                if (!joint.IsBroken) return false;
                pull.BrokeAt = Time.time;
                pull.Input.Buzz(0.9f, 0.12f);
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{pull.Root.name}' comes apart from the ship by the {pull.Input.Name} hand");
            }
            var body = pull.Root.GetComponentInParent<Rigidbody>();
            var loose = body != null && body != pull.Ship;
            if (!loose && Time.time < pull.BrokeAt + YankLooseWait) return true;
            var input = pull.Input;
            var held = loose ? Solid(pull.Root) : null;
            Clear(pull);
            if (held != null && input.Grip.Down)
            {
                var holds = VrClimb.TakeHold(input, held);
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: the part that came apart {(holds ? "is in" : "did not get into")} the {input.Name} hand");
            }
            return true;
        }

        // A demo charge pulled long enough: back as it sat on the wall, then off the game's way, into the hand that
        // pulled (VrChargeDisarm.PulledOff: the charge goes back to the stock and one from the stock comes into the hand).
        private static void TakeOff(Pull pull, DemoChargeRetrievalController charge)
        {
            var input = pull.Input;
            var pulled = pull.Pulled;
            if (pull.Root != null && pull.Parent != null)
            {
                pull.Root.localPosition = pull.RestPosition;
                pull.Root.localRotation = pull.RestRotation;
            }
            Clear(pull);
            if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: the demo charge comes off after {pulled:0.0} s of pulling by the {input.Name} hand");
            VrChargeDisarm.PulledOff(input, charge);
        }

        // How long it is pulled before it comes off: a poster (its part 'SM_Props_Signage_...', its use 'Pickup_Poster_...')
        // sooner.
        private static float HoldFor(Pull pull) => pull.Needed;

        private static float Needs(Transform root, Component thing) =>
            thing is DemoChargeRetrievalController ? Random.Range(ChargeHoldLeast, ChargeHoldMost)
            : root.name.Contains("Signage") || thing.name.Contains("Poster") ? PosterHold : Hold;

        // The other hand holds on to something fixed (the ship, VrClimb.Braced), not to the thing. Before, only the very
        // body the thing is on counted, and the other hand on the wall next to a first aid kit never did (Raik, 2026-11-11).
        private static bool OnWall(Pull pull)
        {
            var other = pull.Input == VrInput.Left ? VrInput.Right : VrInput.Left;
            if (!VrClimb.Braced(pull.Input)) return false;
            var held = VrClimb.HeldCollider(other);
            return held == null || !held.transform.IsChildOf(pull.Root);
        }

        // What that hand holds that may come off: a control to use, or a part to collect the game gives no control. Held
        // as a surface (VrClimb), the control is the one on the part held.
        private static Component Held(VrInput.Hand input)
        {
            // A demo charge on a wall, before anything else on it: it comes off its own way (TakeOff).
            var charge = VrChargeDisarm.OnWall(input);
            if (charge != null) return charge;
            var control = VrClimb.HeldControl(input);
            if (Extractable(control) || Usable(control)) return control;
            var collider = VrClimb.HeldCollider(input);
            control = VrClimb.PartInteractable(collider);
            if (Extractable(control) || Usable(control)) return control;
            var part = collider != null ? collider.GetComponentInParent<StructurePart>() : null;
            // Not an AI node (VrAiNode): in 0.32.0 it came off by hand as well, which made taking one too easy (Raik,
            // 2026-11-22); it takes the grapple now.
            if (part == null || VrAiNode.Is(part)) return null;
            // Not one already off: its own body is the part, or it hangs on one made here with its group.
            var body = collider.attachedRigidbody;
            if (body == null || body.transform == part.transform || Made.Contains(body)) return null;
            if (part.name.StartsWith(Collectible)) return part;
            // A part on a joint that gives way to pulling (Yank), if the game lets the grapple break it.
            if (!YankOn || !StructureHelpers.TryGetBreakableJointComponent(part, BreakableJointComponent.ForceAppliedBy.Grapple, out var joint)) return null;
            return joint != null && !joint.IsBroken ? joint : null;
        }

        // A thing to use, fixed on the ship: removed when used, not a lever or switch, on no loose part.
        public static bool Usable(InteractableObject control) =>
            control != null && control.isActiveAndEnabled && control.DestroyOnComplete && !VrLever.Turnable(control)
            && !VrNacelle.Is(control) && !VrClimb.Loose(control);

        private static bool Begin(Pull pull, Component control)
        {
            if (control is InteractableObject device && Extractable(device)) return BeginStrip(pull, device);
            var root = control is DemoChargeRetrievalController onWall && onWall.DemoCharge != null ? onWall.DemoCharge.transform : Root(control);
            if (root == null)
            {
                Refused.Add(control);
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{control.name}' has no part of its own smaller than {Largest:0.0} m, stays on the wall");
                return false;
            }
            // Heavy things (the large coolant and fuel tanks, Raik 2026-11-19) come off with the grapple only, as in the
            // game, with the game's own body and mass: the hand holds them as part of the wall.
            var yanked = control as BreakableJointComponent;
            var mass = yanked != null ? yanked.GetMassOfBreakable() : GameMass(root);
            var most = yanked != null ? YankMost : HandMost;
            if (mass > most)
            {
                Refused.Add(control);
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{root.name}' weighs {mass:0} kg, more than {most:0} kg: only the grapple takes it off");
                return false;
            }
            var ship = control.GetComponentInParent<Rigidbody>();
            if (ship != null && Made.Contains(ship)) return false;
            if (!VrClimb.TryGetSurface(pull.Input, out var surface) || !VrClimb.TryGetGrip(pull.Input, out _, out var normal)) return false;
            pull.Thing = control;
            pull.Root = root;
            pull.Parent = root.parent;
            pull.RestPosition = root.localPosition;
            pull.RestRotation = root.localRotation;
            pull.Normal = normal.normalized;
            pull.Surface = root.InverseTransformPoint(surface);
            pull.Pulled = 0f;
            pull.Needed = Needs(root, control);
            pull.Tilt = control is DemoChargeRetrievalController ? ChargeTilt : Tilt;
            pull.Edge = false;
            if (control is DemoChargeRetrievalController) SetEdge(pull);
            pull.Angle = 0f;
            pull.Braced = false;
            pull.Ship = ship;
            pull.YankDue = 0f;
            pull.BrokeAt = -1f;
            // The game moves a group's parts itself as their joint gives way.
            if (yanked == null) Gather(pull);
            else pull.Members.Clear();
            // The hand goes with it as it tilts; it holds free only while the other hand holds the ship.
            VrClimb.Reanchor(pull.Input, root);
            if (yanked != null && logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{root.name}' sits on a joint that gives way to pulling ({yanked.GetJointDurability * 100f:0} % of its durability left)");
            if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: {pull.Input.Name} hand holds '{control.name}', its part '{root.name}' ({Size(root):0.00} m, {mass:0.0} kg) on '{(root.parent != null ? root.parent.name : "-")}', to pull {pull.Needed:0.0} s"
                + (pull.Members.Count > 0 ? $", with {pull.Members.Count} more of its group: {Names(pull)}" : ""));
            return true;
        }

        // The other parts of its group near it, on the same body of the ship, hung on it.
        private static void Gather(Pull pull)
        {
            pull.Members.Clear();
            var part = pull.Root.GetComponent<StructurePart>();
            var group = part != null ? part.Group : null;
            if (group == null) return;
            var count = Physics.OverlapSphereNonAlloc(pull.Root.position, Mathf.Max(Size(pull.Root), 0.5f * VrCamera.WorldScale), Around,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var other = Around[i] != null ? Around[i].GetComponentInParent<StructurePart>() : null;
                if (other == null || other == part || other.transform.parent != pull.Parent || other.GetComponent<Rigidbody>() != null) continue;
                if (other.Group != group) continue;
                var known = false;
                foreach (var member in pull.Members) if (member.Part == other.transform) known = true;
                if (known) continue;
                pull.Members.Add(new Member { Part = other.transform, Parent = other.transform.parent, Position = other.transform.localPosition, Rotation = other.transform.localRotation, Scale = other.transform.localScale });
            }
            foreach (var member in pull.Members) member.Part.SetParent(pull.Root, true);
        }

        private static string Names(Pull pull)
        {
            var names = new List<string>();
            foreach (var member in pull.Members) if (member.Part != null) names.Add($"'{member.Part.name}'");
            return string.Join(", ", names);
        }

        // Let go before it came off: the group's other parts back where they were.
        private static void PutBack(Pull pull)
        {
            foreach (var member in pull.Members)
            {
                if (member.Part == null || member.Parent == null) continue;
                member.Part.SetParent(member.Parent, false);
                member.Part.localPosition = member.Position;
                member.Part.localRotation = member.Rotation;
                member.Part.localScale = member.Scale;
            }
            pull.Members.Clear();
        }

        // The heaviest thing a hand pulls off, in kg (the game's own mass of the part or its group, GameMass). The large
        // coolant tank weighs some 67 kg, the fuel tank more (Raik, 2026-11-19).
        private const float HandMost = 20f;

        // The game's mass of the part, or of its group if it is in one (StructurePartAsset is the group's then), as the
        // game gives it a body (StructurePart.GetMassFromRigidbodyAsset); 0 where it has none.
        private static float GameMass(Transform root)
        {
            var part = root.GetComponent<StructurePart>();
            var data = part != null && part.StructurePartAsset != null ? part.StructurePartAsset.Data : null;
            var body = data != null ? data.IRigidbodyAsset : null;
            try { return body != null && body.Data != null ? body.Data.GetMass(part.gameObject) : 0f; }
            catch (System.Exception) { return 0f; }
        }

        // The transform that is the thing: its part (StructurePart), within Largest, not the ship's body.
        private static Transform Root(Component thing)
        {
            var part = thing.GetComponentInParent<StructurePart>();
            if (part == null || part.GetComponent<Rigidbody>() != null) return null;
            var size = Size(part.transform);
            return size > 0f && size <= Largest * VrCamera.WorldScale ? part.transform : null;
        }

        private static float Size(Transform t)
        {
            var any = false;
            var bounds = new Bounds();
            foreach (var renderer in t.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)) continue;
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            return any ? bounds.size.magnitude : 0f;
        }

        // Tilted about its edge away from the hand, so the side the hand pulls comes off the wall.
        private static void Pose(Pull pull, Vector3 hand)
        {
            if (pull.Parent == null) return;
            pull.Root.localPosition = pull.RestPosition;
            pull.Root.localRotation = pull.RestRotation;
            if (pull.Angle <= 0.001f) return;
            if (pull.Edge)
            {
                var edge = pull.Root.TransformPoint(pull.Hinge);
                var up = pull.Root.TransformDirection(pull.Out);
                var lifted = pull.Root.TransformDirection(pull.LiftWay);
                pull.Root.RotateAround(edge, Vector3.Cross(up, lifted), -pull.Angle);
                pull.Contact = edge;
                return;
            }
            var center = pull.Root.position;
            var along = Vector3.ProjectOnPlane(hand - center, pull.Normal);
            if (along.sqrMagnitude < 1e-6f) along = Vector3.ProjectOnPlane(Vector3.up, pull.Normal);
            if (along.sqrMagnitude < 1e-6f) return;
            along.Normalize();
            var axis = Vector3.Cross(pull.Normal, along);
            var hinge = center - along * (Size(pull.Root) * 0.35f);
            pull.Root.RotateAround(hinge, axis, -pull.Angle);
            // Where it still touches its mount: that edge, on the wall behind it.
            pull.Contact = OnWallBehind(pull.Root, pull.Normal, hinge);
        }

        // The edge a demo charge tips about, from its own mesh as it sits: the edge at the wrist of the hand that holds
        // it (opposite the way from the wrist to the knuckles, along the wall), on its sticky side. The angle is the one
        // that lifts the opposite edge by ChargeLift. The charge's mesh looks out of the wall along its forward (its
        // origin lies 3 cm before its back, VrChargeByHand.BackOfMesh).
        private static void SetEdge(Pull pull)
        {
            var root = pull.Root;
            var filter = root.GetComponent<MeshFilter>();
            if (filter == null)
                foreach (var each in root.GetComponentsInChildren<MeshFilter>())
                    if (each.name == ChargeMesh) { filter = each; break; }
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
            {
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: the demo charge '{root.name}' has no mesh of its own to tip by, it tips as other things do");
                return;
            }
            var frame = filter.transform;
            var bounds = mesh.bounds;
            var outward = frame.forward;
            var agrees = Vector3.Dot(outward, pull.Normal);
            if (agrees < -0.3f) outward = -outward;
            var lift = Vector3.zero;
            var by = "the hand, from the wrist to the knuckles";
            if (VrHands.TryGetArm(pull.Input.IsLeft, out _, out _, out var wrist, out var knuckles))
                lift = Vector3.ProjectOnPlane(knuckles - wrist, outward);
            if (lift.sqrMagnitude < 1e-6f)
            {
                lift = Vector3.ProjectOnPlane(frame.up, outward);
                by = "its own up, the hand lies square to the wall or is not drawn";
            }
            if (lift.sqrMagnitude < 1e-6f) return;
            lift.Normalize();
            var size = Mathf.Abs(frame.lossyScale.x);
            var e = bounds.extents;
            var along = frame.InverseTransformDirection(lift);
            var deep = frame.InverseTransformDirection(outward);
            var half = (Mathf.Abs(along.x) * e.x + Mathf.Abs(along.y) * e.y + Mathf.Abs(along.z) * e.z) * size;
            var depth = (Mathf.Abs(deep.x) * e.x + Mathf.Abs(deep.y) * e.y + Mathf.Abs(deep.z) * e.z) * size;
            if (half < 1e-4f) return;
            var hinge = frame.TransformPoint(bounds.center) - lift * half - outward * depth;
            pull.Hinge = root.InverseTransformPoint(hinge);
            pull.LiftWay = root.InverseTransformDirection(lift).normalized;
            pull.Out = root.InverseTransformDirection(outward).normalized;
            pull.Tilt = Mathf.Min(ChargeTilt, Mathf.Atan2(ChargeLift * VrCamera.WorldScale, 2f * half) * Mathf.Rad2Deg);
            pull.Edge = true;
            if (logs++ < 40)
                Plugin.Log.LogInfo($"Pull off: the demo charge tips about its edge at the wrist, by {pull.Tilt:0.00} degrees: {2f * half * 100f:0} cm across that way, {2f * depth * 100f:0.0} cm deep, "
                    + $"the far edge comes off by {ChargeLift * 1000f:0} mm (the way by {by}; its forward and the hold's normal agree by {agrees:0.00})");
        }

        // Off the ship: its own body, then into the hand.
        private static void Loosen(Pull pull, Vector3 hand)
        {
            var root = pull.Root;
            var ship = pull.Ship;
            var shipSpeed = ship != null ? ship.GetPointVelocity(root.position) : Vector3.zero;
            pull.FromPosition = root.position;
            pull.FromRotation = root.rotation;
            pull.HeldOn = pull.Surface;
            var contact = pull.Angle > 0.01f ? pull.Contact : OnWallBehind(root, pull.Normal, root.position);
            root.SetParent(null, true);
            var body = root.gameObject.AddComponent<Rigidbody>();
            body.mass = Mass;
            body.useGravity = false;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.velocity = shipSpeed;
            Made.RemoveWhere(made => made == null);
            Made.Add(body);
            pull.Body = body;
            // The rest of its group comes along, as part of its body.
            if (pull.Members.Count > 0 && logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{root.name}' takes {Names(pull)} along");
            pull.Members.Clear();
            pull.FlyStart = Time.time;
            VrClimb.SetFree(pull.Input, true, false);
            pull.Input.Buzz(0.9f, 0.12f);
            // Off the ship it is a body of its own, which the game does not know as touched: told before its break sounds
            // play on it, so they come through the suit where there is no air (Raik, 2026-11-12: nothing to hear).
            VrTouchSound.Moved(root.GetComponent<StructurePart>(), root.gameObject);
            Break(root, ship, contact, pull.Normal);
            VrTouchSound.Refresh(root.GetComponent<StructurePart>());
            if (logs++ < 40)
            {
                root.GetComponentsInChildren(Colliders);
                var solid = 0;
                foreach (var c in Colliders) if (!c.isTrigger) solid++;
                var parts = new List<string>();
                foreach (var component in root.GetComponents<Component>()) parts.Add(component.GetType().Name);
                Plugin.Log.LogInfo($"Pull off: '{pull.Thing.name}' comes off after {HoldFor(pull):0.0} s, '{root.name}' with {solid} solid collider(s) of {Colliders.Count} flies into the {pull.Input.Name} hand; on it: {string.Join(", ", parts)}");
            }
        }

        // Into the hand over Fly, easing out; then the hand holds it as a loose part.
        private static void Flying(Pull pull)
        {
            var body = pull.Body;
            if (body == null || pull.Root == null)
            {
                Clear(pull);
                return;
            }
            // Let go while it flies: it floats on from where it is.
            if (!VrClimb.TryGetSurface(pull.Input, out _))
            {
                body.isKinematic = false;
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{pull.Root.name}' let go on its way into the {pull.Input.Name} hand, floats");
                Clear(pull);
                return;
            }
            if (!Controller(pull.Input, out var hand)) hand = pull.FromPosition;
            var t = Mathf.Clamp01((Time.time - pull.FlyStart) / Fly);
            var eased = 1f - (1f - t) * (1f - t);
            // The point the hand held goes to the hand.
            var toHand = hand - pull.FromRotation * Vector3.Scale(pull.HeldOn, pull.Root.lossyScale) - pull.FromPosition;
            body.MovePosition(pull.FromPosition + toHand * eased);
            body.MoveRotation(pull.FromRotation);
            pull.Root.position = pull.FromPosition + toHand * eased;
            if (t < 1f) return;
            body.isKinematic = false;
            var held = Solid(pull.Root);
            var holds = held != null && VrClimb.TakeHold(pull.Input, held);
            if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{pull.Root.name}' {(holds ? "is in" : "did not get into")} the {pull.Input.Name} hand");
            Clear(pull);
        }

        // The point on the plane of its back, the side towards the wall, nearest to at.
        private static Vector3 OnWallBehind(Transform root, Vector3 normal, Vector3 at)
        {
            var any = false;
            var bounds = new Bounds();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)) continue;
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            if (!any) return at;
            var e = bounds.extents;
            var back = Vector3.Dot(bounds.center, normal) - (Mathf.Abs(e.x * normal.x) + Mathf.Abs(e.y * normal.y) + Mathf.Abs(e.z * normal.z));
            return at - normal * (Vector3.Dot(at, normal) - back);
        }

        private static FXElement sparks;
        private static bool sparksSought;

        // What the game plays as the part's joint breaks: its own break sound and effect, and the grapple's sound.
        internal static void Break(Transform root, Rigidbody ship, Vector3 at, Vector3 normal)
        {
            var joint = root.GetComponent<BreakableJointComponent>();
            var asset = joint != null ? joint.BreakableJointAsset : null;
            var data = asset != null ? asset.Data : null;
            var effect = data != null ? data.BreakFX : null;
            var sound = "none";
            // Bound to the part, which the hand touches (VrTouchSound): heard through the suit, also with no air around
            // (Raik, 2026-11-11: at a place, they were not heard in a room without air).
            var part = root.GetComponent<EntityBlueprintComponent>();
            if (data != null)
            {
                Play(data.BreakSuccess, part, at);
                sound = $"its own ({data.BreakSuccess})";
            }
            var settings = Main.Instance != null && Main.Instance.MainSettings != null ? Main.Instance.MainSettings.BreakableSettings : null;
            if (settings != null)
            {
                Play(settings.GrappleBreakJointSuccessSFX, part, at);
                sound += $", the grapple's ({settings.GrappleBreakJointSuccessSFX})";
            }
            sound += part != null ? ", on the part" : ", at the place";
            if (effect == null) effect = Sparks(ship);
            if (effect != null && GameSession.SpawnPoolManager != null)
                GameSession.SpawnPoolManager.SpawnObject(effect, at, Quaternion.LookRotation(normal), null, null);
            if (logs++ < 40)
                Plugin.Log.LogInfo($"Pull off: '{root.name}' breaks off with the effect '{(effect != null ? effect.name : "-")}'"
                    + $"{(data != null && data.BreakFX == null ? " (it has none of its own)" : "")}, sounds: {sound}");
        }

        private static void Play(WwiseShortID sound, EntityBlueprintComponent part, Vector3 at)
        {
            if (part != null) AudioViewSystem.RequestBoundSFX(sound, part);
            else AudioViewSystem.RequestOneShotSFX(sound, at);
        }

        // The game's sparks of metal hitting metal (FXCollisionAsset): the effect for light metal on light metal.
        private static FXElement Sparks(Rigidbody ship)
        {
            if (sparksSought) return sparks;
            var listener = ship != null ? ship.GetComponentInChildren<HierarchyCollisionListener>() : null;
            if (listener == null) listener = Object.FindObjectOfType<HierarchyCollisionListener>();
            var asset = listener != null ? listener.FXCollisionAsset : null;
            if (asset == null || asset.CollisionData == null) return null;
            sparksSought = true;
            foreach (var data in asset.CollisionData)
            {
                if (data == null || data.CollisionFX == null) continue;
                var first = data.Material1 != null ? data.Material1.name : "";
                var second = data.Material2 != null ? data.Material2.name : "";
                if (sparks == null && data.CollisionFX.name.Contains("Metal")) sparks = data.CollisionFX;
                if (first.StartsWith("LightMetal") && second.StartsWith("LightMetal")) { sparks = data.CollisionFX; break; }
            }
            Plugin.Log.LogInfo($"Pull off: sparks are '{(sparks != null ? sparks.name : "-")}'");
            return sparks;
        }

        private static Collider Solid(Transform root)
        {
            root.GetComponentsInChildren(Colliders);
            Collider trigger = null;
            foreach (var c in Colliders)
            {
                if (!c.enabled) continue;
                if (!c.isTrigger) return c;
                if (trigger == null) trigger = c;
            }
            return trigger;
        }

        private static bool Controller(VrInput.Hand input, out Vector3 world)
        {
            world = Vector3.zero;
            var vr = VrCamera.Instance;
            if (vr == null || vr.transform.parent == null || !input.TryGetPosition(out var local)) return false;
            world = vr.transform.parent.TransformPoint(local);
            return true;
        }

        // Let go before it came off: back where it was.
        private static void Reset(Pull pull)
        {
            if (pull.FlyStart >= 0f) return;
            if (pull.Thing != null && pull.Root != null && pull.Parent != null && !(pull.Thing is BreakableJointComponent) && !pull.Extract)
            {
                pull.Root.localPosition = pull.RestPosition;
                pull.Root.localRotation = pull.RestRotation;
                PutBack(pull);
                if (pull.Angle > 0f && logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{pull.Thing.name}' let go after {pull.Pulled:0.0} s, back on the wall");
            }
            // A hand still on a device it held free is a plain hold on it again.
            if (pull.Extract && pull.Braced) VrClimb.SetFree(pull.Input, false, false);
            Clear(pull);
        }

        private static void Clear(Pull pull)
        {
            PutBack(pull);
            pull.Thing = null;
            pull.Root = null;
            pull.Parent = null;
            pull.Body = null;
            pull.Pulled = 0f;
            pull.Angle = 0f;
            pull.Braced = false;
            pull.Ship = null;
            pull.FlyStart = -1f;
            pull.YankDue = 0f;
            pull.BrokeAt = -1f;
            pull.Extract = false;
            pull.Edge = false;
            pull.PairFrom = StripPull.Unpaired;
            pull.Mode = "";
        }
    }

    // Stripping a device with two hands close together (VrPullOff.Strip), the part that is plain numbers.
    internal static class StripPull
    {
        // No second hand with its grip closed; one that came too far away to count.
        public const float Unpaired = -1f;
        public const float TooFar = -2f;

        // The other hand closes its grip, 'between' from this one: from then on the two count as a pair, their distance
        // then the measure, or not at all until that grip opens again.
        public static float Begin(float between, float within) => between <= within ? between : TooFar;

        // How far the device is pulled at: away from the hold on it with the other hand on the ship, or the two hands
        // apart from where they came together, whichever is more.
        public static float Far(bool onShip, float away, bool paired, float apart) =>
            Mathf.Max(onShip ? away : 0f, paired ? apart : 0f);
    }
}
