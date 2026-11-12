using System.Collections.Generic;
using BBI.Unity.Game;
using Carbon.Audio;
using Carbon.Core.Unity;
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
        // Or jerked off (Raik, 2026-11-12): a quick, short pull away from the wall, faster than JerkSpeed (m/s at world
        // scale 1) as the controller passes Enough, counts as a third of Hold; the third one breaks it off. The next one
        // counts once the controller came back within Start. Not posters, which come off soon enough.
        private const float JerkSpeed = 0.7f;
        private const int Jerks = 3;
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
            // Quick pulls so far, whether the next one may count, and how far out the controller was last frame.
            public int Jerked;
            public bool JerkArmed;
            public float LastAway;
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
        }

        private static readonly Pull Left = new Pull { Input = VrInput.Left };
        private static readonly Pull Right = new Pull { Input = VrInput.Right };
        private static readonly List<Collider> Colliders = new List<Collider>();
        // Things found not to come off (no part of their own), told once.
        private static readonly HashSet<Component> Refused = new HashSet<Component>();
        private const string Collectible = "SM_Collectible_";
        private static int logs;

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
            if (thing != pull.Thing)
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
            var braced = OnWall(pull);
            if (braced != pull.Braced)
            {
                pull.Braced = braced;
                VrClimb.SetFree(pull.Input, braced, false);
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{pull.Thing.name}' {(braced ? "can be pulled off, the other hand holds the ship" : "is a hold to climb on again")}");
            }
            if (!braced)
            {
                pull.Pulled = 0f;
                // A quick pull counts only from within Start, with the other hand on the ship.
                pull.JerkArmed = false;
                pull.Angle = Mathf.MoveTowards(pull.Angle, 0f, Tilt / TiltTime * Time.deltaTime);
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
            var speed = Time.deltaTime > 0f ? (away - pull.LastAway) / Time.deltaTime : 0f;
            if (away < Start * scale) pull.JerkArmed = true;
            else if (pull.JerkArmed && hard && pull.LastAway < Enough * scale && speed > JerkSpeed * scale && HoldFor(pull) >= Hold)
            {
                pull.JerkArmed = false;
                pull.Jerked++;
                pull.Input.Buzz(0.9f, 0.08f);
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{pull.Thing.name}' jerked ({pull.Jerked} of {Jerks}) at {speed / scale:0.00} m/s");
            }
            pull.LastAway = away;
            // Tilted off the wall while pulled hard, back while not.
            var wanted = hard ? Tilt : 0f;
            pull.Angle = Mathf.MoveTowards(pull.Angle, wanted, Tilt / TiltTime * Time.deltaTime);
            Pose(pull, hand);
            if (pull.Pulled + pull.Jerked * Hold / Jerks >= HoldFor(pull) - 0.001f) Loosen(pull, hand);
        }

        // How long it is pulled before it comes off: a poster (its part 'SM_Props_Signage_...', its use 'Pickup_Poster_...') sooner.
        private static float HoldFor(Pull pull) =>
            pull.Root.name.Contains("Signage") || pull.Thing.name.Contains("Poster") ? PosterHold : Hold;

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
            var control = VrClimb.HeldControl(input);
            if (Usable(control)) return control;
            var collider = VrClimb.HeldCollider(input);
            control = VrClimb.PartInteractable(collider);
            if (Usable(control)) return control;
            var part = collider != null ? collider.GetComponentInParent<StructurePart>() : null;
            // Not one already off: its own body is the part.
            if (part == null || !part.name.StartsWith(Collectible) || collider.attachedRigidbody == null
                || collider.attachedRigidbody.transform == part.transform) return null;
            return part;
        }

        // A thing to use, fixed on the ship: removed when used, not a lever or switch, on no loose part.
        public static bool Usable(InteractableObject control) =>
            control != null && control.isActiveAndEnabled && control.DestroyOnComplete && !VrLever.Turnable(control)
            && !VrNacelle.Is(control) && !VrClimb.Loose(control);

        private static bool Begin(Pull pull, Component control)
        {
            var root = Root(control);
            if (root == null)
            {
                Refused.Add(control);
                if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{control.name}' has no part of its own smaller than {Largest:0.0} m, stays on the wall");
                return false;
            }
            if (!VrClimb.TryGetSurface(pull.Input, out var surface) || !VrClimb.TryGetGrip(pull.Input, out _, out var normal)) return false;
            pull.Thing = control;
            pull.Root = root;
            pull.Parent = root.parent;
            pull.RestPosition = root.localPosition;
            pull.RestRotation = root.localRotation;
            pull.Normal = normal.normalized;
            pull.Surface = root.InverseTransformPoint(surface);
            pull.Pulled = 0f;
            pull.Jerked = 0;
            pull.JerkArmed = false;
            pull.LastAway = 0f;
            pull.Angle = 0f;
            pull.Braced = false;
            pull.Ship = control.GetComponentInParent<Rigidbody>();
            // The hand goes with it as it tilts; it holds free only while the other hand holds the ship.
            VrClimb.Reanchor(pull.Input, root);
            if (logs++ < 40) Plugin.Log.LogInfo($"Pull off: {pull.Input.Name} hand holds '{control.name}', its part '{root.name}' ({Size(root):0.00} m) on '{(root.parent != null ? root.parent.name : "-")}'");
            return true;
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
            if (pull.Angle <= 0.01f) return;
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
            pull.Body = body;
            pull.FlyStart = Time.time;
            VrClimb.SetFree(pull.Input, true, false);
            pull.Input.Buzz(0.9f, 0.12f);
            // Off the ship it is a body of its own, which the game does not know as touched: told before its break sounds
            // play on it, so they come through the suit where there is no air (Raik, 2026-11-12: nothing to hear).
            VrTouchSound.Moved(root.GetComponent<StructurePart>(), root.gameObject);
            Break(root, ship, contact, pull.Normal);
            if (logs++ < 40)
            {
                root.GetComponentsInChildren(Colliders);
                var solid = 0;
                foreach (var c in Colliders) if (!c.isTrigger) solid++;
                var parts = new List<string>();
                foreach (var component in root.GetComponents<Component>()) parts.Add(component.GetType().Name);
                Plugin.Log.LogInfo($"Pull off: '{pull.Thing.name}' comes off after {HoldFor(pull):0} s, '{root.name}' with {solid} solid collider(s) of {Colliders.Count} flies into the {pull.Input.Name} hand; on it: {string.Join(", ", parts)}");
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
        private static void Break(Transform root, Rigidbody ship, Vector3 at, Vector3 normal)
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
            if (pull.Thing != null && pull.Root != null && pull.Parent != null)
            {
                pull.Root.localPosition = pull.RestPosition;
                pull.Root.localRotation = pull.RestRotation;
                if (pull.Angle > 0f && logs++ < 40) Plugin.Log.LogInfo($"Pull off: '{pull.Thing.name}' let go after {pull.Pulled:0.0} s, back on the wall");
            }
            Clear(pull);
        }

        private static void Clear(Pull pull)
        {
            pull.Thing = null;
            pull.Root = null;
            pull.Parent = null;
            pull.Body = null;
            pull.Pulled = 0f;
            pull.Jerked = 0;
            pull.JerkArmed = false;
            pull.LastAway = 0f;
            pull.Angle = 0f;
            pull.Braced = false;
            pull.Ship = null;
            pull.FlyStart = -1f;
        }
    }
}
