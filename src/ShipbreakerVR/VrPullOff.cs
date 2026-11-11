using System.Collections.Generic;
using BBI.Unity.Game;
using Carbon.Audio;
using Carbon.Core.Unity;
using UnityEngine;

namespace ShipbreakerVR
{
    // Things to use fixed on a ship's wall (first aid kits, oxygen and fuel tanks, repair kits) are pulled off by hand
    // (Raik, 2026-11-11), as a cargo container is torn off with the grapple: the hand holds one (VrClimb holds it as a
    // control, the hand closed on it) and pulls away from the wall. From Start on the controller buzzes, the harder the
    // further; from Enough on the thing tilts off the wall by up to Tilt degrees, and pulled that hard for Hold seconds it
    // comes loose and flies into the hand over Fly seconds, a quick move, not a jump. There it is a loose part like any
    // other: held, thrown, let go, and let go over the left shoulder it is used as with A (VrShoulderStow).
    // It comes off only while the other hand holds on to the ship's wall (Raik, 2026-11-11): then the hand on it holds free
    // (VrClimb.SetFree) and pulls neither the body nor the ship. Without that it is a hold like the wall, to climb along.
    // Let go before it came off, the thing goes back to where it was.
    // Also a thing to collect the game gives no control (a damaged fuel can, 'SM_Collectible_..._Damaged', Raik 2026-11-11):
    // held as a surface, it comes off the same way and is then a loose part, with nothing to use.
    // The game has no loose form of them: they are parts of the ship's body. Pulled off, the thing's own part (its
    // StructurePart, smaller than Largest) leaves the ship and gets a rigidbody of Mass. A poster is one too (its part
    // 'SM_Props_Signage_Portrait' is larger than 1 m). Coming off it throws
    // sparks and smoke where it last touched its mount, as the game does where a light part hits a wall fast
    // (HierarchyCollisionListener), and sounds as a part the grapple tears off (BreakableSettings).
    internal static class VrPullOff
    {
        private const float Start = 0.03f;
        private const float Enough = 0.08f;
        private const float Tilt = 7f;
        private const float TiltTime = 0.3f;
        private const float Hold = 3f;
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
                pull.Angle = Mathf.MoveTowards(pull.Angle, 0f, Tilt / TiltTime * Time.deltaTime);
                Pose(pull, hand);
                return;
            }
            // How far the controller is from where the hand holds, away from the wall.
            var away = Vector3.Dot(hand - pull.Root.TransformPoint(pull.Surface), pull.Normal);
            var scale = VrCamera.WorldScale;
            if (away > Start * scale)
                pull.Input.Buzz(Mathf.Lerp(0.1f, 0.7f, Mathf.InverseLerp(Start * scale, Enough * scale, away)), 0.05f);
            var hard = away >= Enough * scale;
            pull.Pulled = hard ? pull.Pulled + Time.deltaTime : 0f;
            // Tilted off the wall while pulled hard, back while not.
            var wanted = hard ? Tilt : 0f;
            pull.Angle = Mathf.MoveTowards(pull.Angle, wanted, Tilt / TiltTime * Time.deltaTime);
            Pose(pull, hand);
            if (pull.Pulled >= Hold) Loosen(pull, hand);
        }

        // The other hand holds on to something fixed (the ship, VrClimb.Braced), not to the thing. Before, only the very
        // body the thing is on counted, and the other hand on the wall next to a first aid kit never did (Raik, 2026-11-11).
        private static bool OnWall(Pull pull)
        {
            var other = pull.Input == VrInput.Left ? VrInput.Right : VrInput.Left;
            if (!VrClimb.Braced(pull.Input)) return false;
            var held = VrClimb.HeldCollider(other);
            return held == null || !held.transform.IsChildOf(pull.Root);
        }

        // What that hand holds that may come off: a control to use, or a part to collect the game gives no control.
        private static Component Held(VrInput.Hand input)
        {
            var control = VrClimb.HeldControl(input);
            if (Usable(control)) return control;
            var collider = VrClimb.HeldCollider(input);
            var part = collider != null ? collider.GetComponentInParent<StructurePart>() : null;
            // Not one already off: its own body is the part.
            if (part == null || !part.name.StartsWith(Collectible) || collider.attachedRigidbody == null
                || collider.attachedRigidbody.transform == part.transform) return null;
            return part;
        }

        // A thing to use, fixed on the ship: removed when used, not a lever or switch, on no loose part.
        private static bool Usable(InteractableObject control) =>
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
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.velocity = shipSpeed;
            pull.Body = body;
            pull.FlyStart = Time.time;
            VrClimb.SetFree(pull.Input, true, false);
            pull.Input.Buzz(0.9f, 0.12f);
            Sparks(ship, contact, pull.Normal);
            TearSound();
            if (logs++ < 40)
            {
                root.GetComponentsInChildren(Colliders);
                var solid = 0;
                foreach (var c in Colliders) if (!c.isTrigger) solid++;
                var parts = new List<string>();
                foreach (var component in root.GetComponents<Component>()) parts.Add(component.GetType().Name);
                Plugin.Log.LogInfo($"Pull off: '{pull.Thing.name}' comes off after {Hold:0} s, '{root.name}' with {solid} solid collider(s) of {Colliders.Count} flies into the {pull.Input.Name} hand; on it: {string.Join(", ", parts)}");
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

        // The game's sparks and smoke of a light part hitting a metal wall fast: of its collision effects (FXCollisionAsset)
        // the one for parts no heavier than Mass that needs the highest speed.
        private static void Sparks(Rigidbody ship, Vector3 at, Vector3 normal)
        {
            if (!sparksSought)
            {
                var listener = ship != null ? ship.GetComponentInChildren<HierarchyCollisionListener>() : null;
                if (listener == null) listener = Object.FindObjectOfType<HierarchyCollisionListener>();
                var asset = listener != null ? listener.FXCollisionAsset : null;
                if (asset == null || asset.CollisionData == null)
                {
                    if (logs++ < 40) Plugin.Log.LogInfo("Pull off: no collision effects found yet, no sparks");
                    return;
                }
                sparksSought = true;
                FXCollisionAsset.FXData best = null;
                foreach (var data in asset.CollisionData)
                {
                    if (data == null) continue;
                    Plugin.Log.LogInfo($"Pull off: collision effect '{(data.Material1 != null ? data.Material1.name : "-")}' on '{(data.Material2 != null ? data.Material2.name : "-")}', from {data.MinRequiredVelocity:0.0} m/s and {data.MinRequiredMass:0.0} kg: '{(data.CollisionFX != null ? data.CollisionFX.name : "-")}'");
                    if (data.CollisionFX == null || data.MinRequiredMass > Mass) continue;
                    if (best == null || data.MinRequiredVelocity > best.MinRequiredVelocity) best = data;
                }
                sparks = best != null ? best.CollisionFX : null;
                Plugin.Log.LogInfo($"Pull off: sparks are '{(sparks != null ? sparks.name : "-")}'");
            }
            if (sparks == null || GameSession.SpawnPoolManager == null) return;
            GameSession.SpawnPoolManager.SpawnObject(sparks, at, Quaternion.LookRotation(normal), null, null);
        }

        // The sound of a part the grapple tears off its joint.
        private static void TearSound()
        {
            var settings = Main.Instance != null && Main.Instance.MainSettings != null ? Main.Instance.MainSettings.BreakableSettings : null;
            if (settings == null) return;
            ((Carbon.Core.Events.IEventBroadcaster)Main.EventSystem).Post(MasterSFXEvent.GetEvent(settings.GrappleBreakJointSuccessSFX));
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
            pull.Angle = 0f;
            pull.Braced = false;
            pull.Ship = null;
            pull.FlyStart = -1f;
        }
    }
}
