using System;
using UnityEngine;

namespace ShipbreakerVR
{
    // A path for one device in a test script (VrTestScript): keyframes with times, played from a start time. Two keys
    // are a glide (move ... in T) with an easing; more keys are a smooth spline through all of them (track ... end),
    // for camera moves in trailer recordings. Plain math on the fields of Vector3 and Quaternion, no engine calls, so
    // it runs in the tests.
    internal class VrTestTrack
    {
        public enum Easing { Linear, In, Out, InOut }

        public readonly VrTestXr.Device Device;
        public readonly float Start;
        private readonly float[] times;
        private readonly Vector3[] positions;
        private readonly Quaternion[] rotations;
        private readonly Easing easing;

        // times rise from 0; with two keys the easing shapes the glide, with more the spline passes every key and
        // comes to rest at the first and the last one.
        public VrTestTrack(VrTestXr.Device device, float start, float[] times, VrTestXr.Pose[] keys, Easing easing = Easing.Linear)
        {
            if (times.Length != keys.Length || keys.Length < 2) throw new ArgumentException("a track needs at least two keys, each with a time");
            for (var i = 1; i < times.Length; i++)
                if (times[i] < times[i - 1]) throw new FormatException($"key times must rise ({times[i - 1]} before {times[i]})");
            Device = device;
            Start = start;
            this.times = times;
            this.easing = easing;
            positions = new Vector3[keys.Length];
            rotations = new Quaternion[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                positions[i] = keys[i].Position;
                // The shorter way round from one key to the next.
                var q = Normalize(keys[i].Rotation);
                if (i > 0 && Dot(rotations[i - 1], q) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                rotations[i] = q;
            }
        }

        public float Duration => times[times.Length - 1];

        public bool Done(float now) => now - Start >= Duration;

        public VrTestXr.Pose Evaluate(float now)
        {
            var t = now - Start;
            if (t <= times[0]) return new VrTestXr.Pose(positions[0], rotations[0]);
            var last = times.Length - 1;
            if (t >= times[last]) return new VrTestXr.Pose(positions[last], rotations[last]);
            var i = 0;
            while (t > times[i + 1]) i++;
            var span = times[i + 1] - times[i];
            var u = span <= 0f ? 1f : (t - times[i]) / span;
            if (times.Length == 2)
            {
                var e = Ease(u, easing);
                return new VrTestXr.Pose(Lerp(positions[0], positions[1], e), Slerp(rotations[0], rotations[1], e));
            }
            return new VrTestXr.Pose(Hermite(positions, i, u, span), Normalize(Hermite(rotations, i, u, span)));
        }

        public static Easing ParseEasing(string word)
        {
            switch (word.ToLowerInvariant())
            {
                case "linear": return Easing.Linear;
                case "ease":
                case "easeinout":
                case "ease-in-out": return Easing.InOut;
                case "easein":
                case "ease-in": return Easing.In;
                case "easeout":
                case "ease-out": return Easing.Out;
                default: throw new FormatException($"unknown easing '{word}' (linear, ease, ease-in, ease-out)");
            }
        }

        public static bool IsEasing(string word)
        {
            try { ParseEasing(word); return true; }
            catch (FormatException) { return false; }
        }

        // Smooth starts and stops without overshoot: quadratic in and out, smoothstep for both.
        public static float Ease(float u, Easing easing)
        {
            u = u < 0f ? 0f : u > 1f ? 1f : u;
            switch (easing)
            {
                case Easing.In: return u * u;
                case Easing.Out: return 1f - (1f - u) * (1f - u);
                case Easing.InOut: return u * u * (3f - 2f * u);
                default: return u;
            }
        }

        // Catmull-Rom tangents for keys at uneven times (the neighbors' difference over their time), zero at both ends,
        // so the device eases out of the first key and into the last; Hermite curve within the segment.
        private float Tangent(int i, Func<int, int, float> diff)
        {
            if (i == 0 || i == times.Length - 1) return 0f;
            var dt = times[i + 1] - times[i - 1];
            return dt <= 0f ? 0f : diff(i + 1, i - 1) / dt;
        }

        private Vector3 Hermite(Vector3[] p, int i, float u, float span)
        {
            var r = new float[3];
            for (var c = 0; c < 3; c++)
            {
                var axis = c;
                float At(int k) => axis == 0 ? p[k].x : axis == 1 ? p[k].y : p[k].z;
                r[c] = Hermite(At(i), At(i + 1), Tangent(i, (a, b) => At(a) - At(b)) * span, Tangent(i + 1, (a, b) => At(a) - At(b)) * span, u);
            }
            return new Vector3(r[0], r[1], r[2]);
        }

        // Component-wise on sign-aligned quaternions, normalized afterwards: smooth enough for the turns of a camera.
        private Quaternion Hermite(Quaternion[] q, int i, float u, float span)
        {
            var r = new float[4];
            for (var c = 0; c < 4; c++)
            {
                var axis = c;
                float At(int k) => axis == 0 ? q[k].x : axis == 1 ? q[k].y : axis == 2 ? q[k].z : q[k].w;
                r[c] = Hermite(At(i), At(i + 1), Tangent(i, (a, b) => At(a) - At(b)) * span, Tangent(i + 1, (a, b) => At(a) - At(b)) * span, u);
            }
            return new Quaternion(r[0], r[1], r[2], r[3]);
        }

        private static float Hermite(float p0, float p1, float m0, float m1, float u)
        {
            var u2 = u * u;
            var u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * m0 + (-2f * u3 + 3f * u2) * p1 + (u3 - u2) * m1;
        }

        private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);

        private static float Dot(Quaternion a, Quaternion b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

        private static Quaternion Normalize(Quaternion q)
        {
            var length = (float)Math.Sqrt(Dot(q, q));
            return length < 1e-6f ? new Quaternion(0f, 0f, 0f, 1f) : new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
        }

        // a and b already on the same side (see the constructor).
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            var dot = Dot(a, b);
            if (dot < 0f)
            {
                b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
                dot = -dot;
            }
            if (dot > 0.9995f)
                return Normalize(new Quaternion(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t));
            var theta = (float)Math.Acos(dot);
            var sin = (float)Math.Sin(theta);
            var wa = (float)Math.Sin((1f - t) * theta) / sin;
            var wb = (float)Math.Sin(t * theta) / sin;
            return new Quaternion(a.x * wa + b.x * wb, a.y * wa + b.y * wb, a.z * wa + b.z * wb, a.w * wa + b.w * wb);
        }
    }
}
