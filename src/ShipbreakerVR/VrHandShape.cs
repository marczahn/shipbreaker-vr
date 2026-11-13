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

        private static readonly Collider[] Found = new Collider[32];
        private static readonly List<Collider> Near = new List<Collider>();
        private static CapsuleCollider capsule;
        private static BoxCollider box;

        // What may stop a hand within reach of that point, for the Depth queries after it.
        public static int Gather(Vector3 center, float reach, Transform body)
        {
            Near.Clear();
            var count = Physics.OverlapSphereNonAlloc(center, reach, Found, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
                if (Found[i] != null && Stops(Found[i], body)) Near.Add(Found[i]);
            return Near.Count;
        }

        // Whether this collider stops a hand (not the body, not a light loose part).
        public static bool Stops(Collider collider, Transform body)
        {
            if (collider.isTrigger || collider.transform.IsChildOf(body)) return false;
            var rigidbody = collider.attachedRigidbody;
            return rigidbody == null || rigidbody.isKinematic || rigidbody.mass >= LightestStops;
        }

        // How deep a capsule from a to b of that radius is in what Gather found (0 when free), and the way out of it.
        public static float Capsule(Vector3 a, Vector3 b, float radius, out Vector3 way)
        {
            way = Vector3.zero;
            if (Near.Count == 0) return 0f;
            Probes();
            var axis = b - a;
            var length = axis.magnitude;
            var turn = length > 1e-5f ? Quaternion.LookRotation(axis / length) : Quaternion.identity;
            capsule.radius = radius;
            capsule.height = length + 2f * radius;
            return Deepest(capsule, (a + b) * 0.5f, turn, out way);
        }

        // How deep a box (its middle, axes and half size) is in what Gather found (0 when free), and the way out of it.
        public static float Box(Vector3 center, Quaternion turn, Vector3 half, out Vector3 way)
        {
            way = Vector3.zero;
            if (Near.Count == 0) return 0f;
            Probes();
            box.size = half * 2f;
            return Deepest(box, center, turn, out way);
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
                if (Physics.ComputePenetration(probe, at, turn, thing, thing.transform.position, thing.transform.rotation, out var direction, out var distance)
                    && distance > deepest)
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
