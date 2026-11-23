using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // Test (Raik, 2026-11-13): the hand's collision shape is the hand as drawn. The palm is a box from the wrist to the
    // knuckles, as wide as the knuckles and as thick as the palm; each finger and the thumb is a chain of capsules, one
    // per bone, from joint to joint along the skeleton as posed, each as thick as that finger. Only for queries
    // (Physics.ComputePenetration): the probes are never in the scene's physics, so nothing else bumps into them. What
    // stops a hand is anything solid but the player's own body and the light loose parts the arms push aside
    // (VrArmPush), which a hand would only judder against.
    internal static class VrHandShape
    {
        // The fingers' radii, index to little, and the thumb's, in meters at world scale 1.
        public static readonly float[] FingerRadius = { 0.0095f, 0.0095f, 0.009f, 0.008f };
        public const float ThumbRadius = 0.011f;
        // Loose parts lighter than this (kg) are pushed by the arms, not in the hand's way (VrArmPush.LightestPushed).
        private const float LightestStops = 15f;

        // What a query finds near the hand. It held 32 and no more: in a dense cloud of debris the query filled it with
        // any 32 of what was there, the wall could be left out, and the hand went through it (bug list B5). Now a query
        // that fills it is asked again with twice the room, up to MostFound; the room stays that large.
        private static Collider[] Found = new Collider[32];
        private const int MostFound = 512;
        private static readonly List<Collider> Near = new List<Collider>();
        // Of those, the ones met by their field too (VrKiosk: a shape not convex, which is only a skin to physics).
        private static readonly List<Collider> NearField = new List<Collider>();
        private static CapsuleCollider capsule;
        private static BoxCollider box;

        // Test (Raik, 2026-11-14): the tool in the other hand stops a hand too, by its own shape
        // (VrToolCollide.WorldDistance); not for the hand that holds it.
        private static bool toolNear;
        private static int toolLogs;
        // Whether the deepest place the last Box or Capsule found was in the tool (VrHands.PalmOut: the tool gives way).
        public static bool FromTool { get; private set; }

        // Test (Raik, 2026-11-14: a hand should slide over the other arm as over a hull, its fingers following it):
        // the other arm stops a hand too, as a capsule per bone, the upper arm from the shoulder to the
        // elbow and the forearm from the elbow to the wrist, thinner towards the wrist, as thick as the suit's sleeves.
        // Only for queries, as the tool: neither arm is pushed.
        public struct Limb
        {
            public bool Known;
            public Vector3 Shoulder;
            public Vector3 Elbow;
            public Vector3 Wrist;
            // The forearm's turn, to keep a side in (VrHands.StayOutOfArm).
            public Quaternion Turn;
        }

        public const float UpperArmRadius = 0.06f;
        public const float ElbowRadius = 0.055f;
        public const float WristRadius = 0.045f;
        private static Limb limb;
        private static bool armNear;
        // Whether the deepest place the last Box or Capsule found was in the other arm.
        public static bool FromArm { get; private set; }

        // How far a point lies outside that arm (less than 0 inside it), and the way out of it; MaxValue without one.
        public static float ArmDistance(Limb arm, Vector3 point, out Vector3 way)
        {
            way = Vector3.zero;
            if (!arm.Known) return float.MaxValue;
            var scale = VrCamera.WorldScale;
            var upper = Segment(point, arm.Shoulder, arm.Elbow, UpperArmRadius * scale, ElbowRadius * scale, out var upperWay);
            var lower = Segment(point, arm.Elbow, arm.Wrist, ElbowRadius * scale, WristRadius * scale, out var lowerWay);
            if (upper <= lower)
            {
                way = upperWay;
                return upper;
            }
            way = lowerWay;
            return lower;
        }

        private static float Along(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            var length = ab.sqrMagnitude;
            return length > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / length) : 0f;
        }

        private static float Segment(Vector3 p, Vector3 a, Vector3 b, float ra, float rb, out Vector3 way)
        {
            var t = Along(p, a, b);
            var nearest = Vector3.Lerp(a, b, t);
            var off = p - nearest;
            var distance = off.magnitude;
            if (distance > 1e-6f) way = off / distance;
            else
            {
                var axis = (b - a).sqrMagnitude > 1e-8f ? (b - a).normalized : Vector3.forward;
                way = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            }
            return distance - Mathf.Lerp(ra, rb, t);
        }

        // What may stop a hand within reach of that point, for the Depth queries after it; with the other hand's tool
        // when withTool.
        public static int Gather(Vector3 center, float reach, Transform body, bool withTool = false, Limb other = default)
        {
            limb = other;
            armNear = other.Known && ArmDistance(other, center, out _) < reach;
            Near.Clear();
            NearField.Clear();
            toolNear = withTool && VrToolCollide.ToolNear(center, reach);
            var count = Physics.OverlapSphereNonAlloc(center, reach, Found, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            while (count == Found.Length && Found.Length < MostFound)
            {
                Found = new Collider[Found.Length * 2];
                count = Physics.OverlapSphereNonAlloc(center, reach, Found, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                Plugin.Log.LogInfo($"Hand shape: more than {Found.Length / 2} things within reach of a hand, room for {Found.Length} now");
            }
            for (var i = 0; i < count; i++)
            {
                if (Found[i] != null) VrKiosk.Fit(Found[i]);
                if (Found[i] != null && Stops(Found[i], body))
                {
                    Near.Add(Found[i]);
                    if (VrKiosk.HasField(Found[i])) NearField.Add(Found[i]);
                    Survey(Found[i]);
                }
            }
            return Near.Count + (toolNear ? 1 : 0) + (armNear ? 1 : 0);
        }

        // Test (Raik, 2026-11-14: the fingers seem to feel something other than what is drawn): for each collider a hand
        // comes near, once, what kind it is and how it compares with the model drawn on the same object: whether a mesh
        // collider uses the drawn mesh itself or a mesh of its own, convex or not, and how far their bounds differ.
        private static readonly HashSet<int> Surveyed = new HashSet<int>();
        private const int MostSurveyed = 60;

        private static void Survey(Collider collider)
        {
            if (Surveyed.Count >= MostSurveyed || !Surveyed.Add(collider.GetInstanceID())) return;
            var filter = collider.GetComponent<MeshFilter>();
            var drawn = filter != null ? filter.sharedMesh : null;
            string kind;
            if (collider is MeshCollider mesh)
            {
                var own = mesh.sharedMesh;
                kind = $"mesh collider{(mesh.convex ? ", convex" : ", not convex")}, "
                    + (own == null ? "no mesh" : own == drawn ? $"the drawn mesh itself ({own.vertexCount} vertices)"
                        : $"its own mesh '{own.name}' ({own.vertexCount} vertices)");
            }
            else kind = collider.GetType().Name;
            string note;
            if (drawn != null)
            {
                var renderer = collider.GetComponent<Renderer>();
                var seen = renderer != null ? renderer.bounds : collider.bounds;
                var felt = collider.bounds;
                var off = Vector3.Max(Abs(felt.min - seen.min), Abs(felt.max - seen.max)) * 100f;
                note = $"; drawn mesh '{drawn.name}' {drawn.vertexCount} vertices, {(drawn.isReadable ? "readable" : "not readable")}; bounds differ by up to ({off.x:0.0}, {off.y:0.0}, {off.z:0.0}) cm";
            }
            else note = "; nothing drawn on the same object";
            Plugin.Log.LogInfo($"Hand shape: near '{collider.name}' on '{collider.transform.root.name}': {kind}{note}");
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        // Whether this collider stops a hand (not the body, not a light loose part).
        public static bool Stops(Collider collider, Transform body)
        {
            // A tool afloat is no wall to the hand: the hand is in it when it lets go, and reaches into it to take it.
            if (collider.isTrigger || collider.transform.IsChildOf(body) || VrLooseTools.HandsPass(collider)) return false;
            var rigidbody = collider.attachedRigidbody;
            return rigidbody == null || rigidbody.isKinematic || rigidbody.mass >= LightestStops;
        }

        // How deep a capsule from a to b of that radius is in what Gather found (0 when free), and the way out of it.
        public static float Capsule(Vector3 a, Vector3 b, float radius, out Vector3 way)
        {
            way = Vector3.zero;
            var deepest = 0f;
            FromTool = false;
            if (Near.Count > 0)
            {
                Probes();
                var axis = b - a;
                var length = axis.magnitude;
                var turn = length > 1e-5f ? Quaternion.LookRotation(axis / length) : Quaternion.identity;
                capsule.radius = radius;
                capsule.height = length + 2f * radius;
                deepest = Deepest(capsule, (a + b) * 0.5f, turn, out way);
                foreach (var thing in NearField)
                    for (var k = 0; k <= CapsulePoints; k++)
                    {
                        if (thing == null || !VrKiosk.Distance(thing, Vector3.Lerp(a, b, (float)k / CapsulePoints), out var off, out var outward)) continue;
                        var depth = radius - off;
                        if (depth <= deepest) continue;
                        deepest = depth;
                        way = outward;
                    }
            }
            // The tool: points along the bone, each as deep as it lies within its radius of a box.
            for (var k = 0; k <= CapsulePoints && toolNear; k++)
            {
                var depth = radius - ToolDistance(Vector3.Lerp(a, b, (float)k / CapsulePoints), out var outward);
                if (depth <= deepest) continue;
                deepest = depth;
                way = outward;
                FromTool = true;
            }
            FromArm = false;
            for (var k = 0; k <= CapsulePoints && armNear; k++)
            {
                var depth = radius - ArmDistance(limb, Vector3.Lerp(a, b, (float)k / CapsulePoints), out var outward);
                if (depth <= deepest) continue;
                deepest = depth;
                way = outward;
                FromTool = false;
                FromArm = true;
            }
            return deepest;
        }

        private const int CapsulePoints = 4;

        // How far a point lies outside the tool found by Gather (less than 0 inside it), and the way out of it.
        private static float ToolDistance(Vector3 point, out Vector3 way)
        {
            var nearest = VrToolCollide.WorldDistance(point, out way);
            if (nearest < 0f && toolLogs++ < 20) Plugin.Log.LogInfo($"Hand shape: a hand reaches {-nearest * 1000f:0.0} mm into the tool in the other hand");
            return nearest;
        }

        // How deep a box (its middle, axes and half size) is in what Gather found (0 when free), and the way out of it.
        public static float Box(Vector3 center, Quaternion turn, Vector3 half, out Vector3 way)
        {
            way = Vector3.zero;
            var deepest = 0f;
            FromTool = false;
            if (Near.Count > 0)
            {
                Probes();
                box.size = half * 2f;
                deepest = Deepest(box, center, turn, out way);
                foreach (var thing in NearField)
                    for (var x = -1; x <= 1; x++)
                        for (var y = -1; y <= 1; y++)
                            for (var z = -1; z <= 1; z++)
                            {
                                if (thing == null || !VrKiosk.Distance(thing, center + turn * Vector3.Scale(half, new Vector3(x, y, z)), out var off, out var outward)) continue;
                                if (-off <= deepest) continue;
                                deepest = -off;
                                way = outward;
                            }
            }
            // The tool and the other arm: the box's middle, corners, and the middles of its edges and faces, each as deep
            // as it lies in them.
            FromArm = false;
            if (!toolNear && !armNear) return deepest;
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                    {
                        var point = center + turn * Vector3.Scale(half, new Vector3(x, y, z));
                        if (toolNear)
                        {
                            var depth = -ToolDistance(point, out var outward);
                            if (depth > deepest)
                            {
                                deepest = depth;
                                way = outward;
                                FromTool = true;
                                FromArm = false;
                            }
                        }
                        if (armNear)
                        {
                            var depth = -ArmDistance(limb, point, out var outward);
                            if (depth > deepest)
                            {
                                deepest = depth;
                                way = outward;
                                FromTool = false;
                                FromArm = true;
                            }
                        }
                    }
            return deepest;
        }

        private static float Deepest(Collider probe, Vector3 at, Quaternion turn, out Vector3 way)
        {
            way = Vector3.zero;
            var deepest = 0f;
            // Physics.ComputePenetration finds nothing for a disabled collider (VrToolCollide): on just for the calls, as
            // a trigger, with no physics step in between.
            probe.enabled = true;
            foreach (var thing in Near)
            {
                if (thing == null) continue;
                // Against a skin (VrKiosk), only out of it, never further in.
                if (Physics.ComputePenetration(probe, at, turn, thing, thing.transform.position, thing.transform.rotation, out var direction, out var distance)
                    && distance > deepest
                    && (!VrKiosk.HasField(thing) || !VrKiosk.Distance(thing, at, out _, out var outward) || Vector3.Dot(direction, outward) > 0f))
                {
                    deepest = distance;
                    way = direction;
                }
            }
            probe.enabled = false;
            return deepest;
        }

        private static void Probes()
        {
            if (capsule != null && box != null) return;
            var holder = new GameObject("ShipbreakerVR hand probe") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(holder);
            capsule = holder.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;
            capsule.direction = 2;
            capsule.enabled = false;
            box = holder.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.enabled = false;
        }
    }
}
