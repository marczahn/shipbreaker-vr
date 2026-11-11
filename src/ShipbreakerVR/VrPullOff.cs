using System.Collections.Generic;
using BBI.Unity.Game;
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
    // The game has no loose form of them: they are parts of the ship's body. Pulled off, the thing's own transform (the
    // largest one around it still smaller than Largest) leaves the ship and gets a rigidbody of Mass.
    internal static class VrPullOff
    {
        private const float Start = 0.03f;
        private const float Enough = 0.08f;
        private const float Tilt = 7f;
        private const float TiltTime = 0.3f;
        private const float Hold = 3f;
        private const float Fly = 0.18f;
        private const float Largest = 1f;
        private const float Mass = 4f;

        private class Pull
        {
            public VrInput.Hand Input;
            public InteractableObject Thing;
            public Transform Root;
            public Transform Parent;
            public Vector3 RestPosition;
            public Quaternion RestRotation;
            public Vector3 Normal;
            public Vector3 Surface;
            public float Pulled;
            public float Angle;
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
            var control = VrClimb.HeldControl(pull.Input);
            if (control != pull.Thing)
            {
                Reset(pull);
                if (!Usable(control) || !Begin(pull, control)) return;
            }
            if (pull.Thing == null) return;
            if (pull.Root == null || !pull.Thing.isActiveAndEnabled)
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

        // The other hand holds on to the ship the thing is on, not to the thing.
        private static bool OnWall(Pull pull)
        {
            var other = pull.Input == VrInput.Left ? VrInput.Right : VrInput.Left;
            return pull.Ship != null && VrClimb.Braced(pull.Input) && VrClimb.HeldBody(other) == pull.Ship
                && VrClimb.HeldControl(other) != pull.Thing;
        }

        // A thing to use, fixed on the ship: removed when used, not a lever or switch, on no loose part.
        private static bool Usable(InteractableObject control) =>
            control != null && control.isActiveAndEnabled && control.DestroyOnComplete && !VrLever.Turnable(control)
            && !VrNacelle.Is(control) && !VrClimb.Loose(control);

        private static bool Begin(Pull pull, InteractableObject control)
        {
            var root = Root(control);
            if (root == null)
            {
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

        // The transform that is the thing: the highest one around the control whose renderers stay within Largest, not the
        // ship's body.
        private static Transform Root(InteractableObject control)
        {
            var body = control.GetComponentInParent<Rigidbody>();
            Transform found = null;
            for (var t = control.transform; t != null && (body == null || t != body.transform); t = t.parent)
            {
                var size = Size(t);
                if (size <= 0f) continue;
                if (size > Largest * VrCamera.WorldScale) break;
                found = t;
            }
            return found;
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
            if (VrClimb.HeldControl(pull.Input) != pull.Thing)
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
