using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // The right thumb looked different from the left one under the same pose (Raik, since 2026-11-13; found 2026-11-19).
    // The game's arms (geo_arms) are one mesh, the right arm the mirror image of the left one (within half a
    // millimetre), and the skeletons match too. What differs is the skin: on the left hand the ball of the thumb hangs
    // on the thumb's base bone (rig_left_hand_thumb_base, which sits on the wrist), on the right hand that bone carries
    // no weight at all and the ball hangs on the hand. So when the thumb turns from its base, the left one turns as a
    // whole and the right one only from its first joint out, with a kink in its middle (35° at the base: up to 37 mm
    // between the right thumb and the mirrored left one). The flat game never shows it, its right hand holds a tool.
    // Here the arms draw a copy of their mesh in which each vertex of the right hand takes, for the right thumb's base
    // bone, the share its mirror image on the left has for the left one, out of what it had on the hand bone (then 2 mm
    // remain, the joints themselves are not quite mirrored). Nothing of the game's files changes, the weights come
    // from the game's own left hand at run time, and the arms get their mesh back when VR stops.
    // The right hand's other base bones (index to little) and rig_right_elbow_stretchtracker are bare the same way and
    // stay so: whether the game moves them on the right is not known.
    internal static class VrThumbSkin
    {
        static VrThumbSkin() => VrSession.OnUnload("thumb skin", Release);

        private const string ArmsName = "geo_arms";
        private const string LeftBase = "rig_left_hand_thumb_base";
        private const string RightBase = "rig_right_hand_thumb_base";
        private const string RightHand = "rig_right_hand";
        // How far a vertex may lie from the mirror image of its twin, and how much weight counts as some.
        private const float Twin = 0.0005f;
        private const float Some = 1e-4f;

        // By the instance id of the mesh a renderer had: its copy, or null when it needs none (also the copies themselves).
        private static readonly Dictionary<int, Mesh> Copies = new Dictionary<int, Mesh>();
        // Renderers that draw a copy, with the mesh they had.
        private static readonly List<(SkinnedMeshRenderer Renderer, Mesh Own, Mesh Copy)> Worn = new List<(SkinnedMeshRenderer, Mesh, Mesh)>();
        private static float nextLook;

        public static void Update()
        {
            if (!VrXr.IsRunning)
            {
                // Every frame while VR is off: another part of the mod may hand a copy back to the renderer only now.
                GiveBack();
                return;
            }
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 1f;
            foreach (var renderer in Object.FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (renderer.name != ArmsName) continue;
                var own = renderer.sharedMesh;
                if (own == null) continue;
                if (!Copies.TryGetValue(own.GetInstanceID(), out var copy))
                {
                    copy = Copy(renderer, own);
                    Copies[own.GetInstanceID()] = copy;
                    if (copy != null) Copies[copy.GetInstanceID()] = null;
                }
                if (copy == null) continue;
                renderer.sharedMesh = copy;
                Worn.RemoveAll(w => w.Renderer == null || w.Renderer == renderer);
                Worn.Add((renderer, own, copy));
            }
        }

        private static Mesh Copy(SkinnedMeshRenderer renderer, Mesh own)
        {
            if (!own.isReadable)
            {
                Plugin.Log.LogInfo($"Thumb skin: '{own.name}' cannot be read, the right thumb stays as the game binds it");
                return null;
            }
            var bones = renderer.bones;
            int leftBase = -1, rightBase = -1, rightHand = -1;
            for (var i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                if (bones[i].name == LeftBase) leftBase = i;
                else if (bones[i].name == RightBase) rightBase = i;
                else if (bones[i].name == RightHand) rightHand = i;
            }
            if (leftBase < 0 || rightBase < 0 || rightHand < 0) return null;
            var weights = own.boneWeights;
            var changed = Bind(own.vertices, weights, leftBase, rightBase, rightHand, out var share);
            if (changed == 0) return null;
            var copy = Object.Instantiate(own);
            copy.name = own.name + " (ShipbreakerVR thumb)";
            copy.hideFlags = HideFlags.HideAndDontSave;
            copy.boneWeights = weights;
            Plugin.Log.LogInfo($"Thumb skin: '{own.name}': {changed} vertices of the right hand now hang on {RightBase} as their mirror images on {LeftBase}, {share:0.0} of weight in all");
            return copy;
        }

        // Gives each vertex of the right side, for the right thumb's base bone, the share its mirror image (x to -x in
        // the mesh's space) has for the left one, taken from its weight on the right hand bone. How many vertices
        // changed; none when the right base bone carries weight already. Vertices at one place (split along a UV seam)
        // each find the same twin, so they stay together.
        internal static int Bind(Vector3[] vertices, BoneWeight[] weights, int leftBase, int rightBase, int rightHand, out float moved)
        {
            moved = 0f;
            var left = new List<int>();
            for (var v = 0; v < weights.Length; v++)
            {
                if (On(weights[v], rightBase) > Some) return 0;
                if (On(weights[v], leftBase) > Some) left.Add(v);
            }
            if (left.Count == 0) return 0;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            foreach (var l in left)
            {
                var mirrored = new Vector3(-vertices[l].x, vertices[l].y, vertices[l].z);
                min = Vector3.Min(min, mirrored);
                max = Vector3.Max(max, mirrored);
            }
            min -= Vector3.one * Twin;
            max += Vector3.one * Twin;
            var changed = 0;
            for (var v = 0; v < vertices.Length; v++)
            {
                var p = vertices[v];
                if (p.x < min.x || p.y < min.y || p.z < min.z || p.x > max.x || p.y > max.y || p.z > max.z) continue;
                var onHand = On(weights[v], rightHand);
                if (onHand <= Some) continue;
                var twin = -1;
                var nearest = Twin * Twin;
                foreach (var l in left)
                {
                    var d = new Vector3(-vertices[l].x - p.x, vertices[l].y - p.y, vertices[l].z - p.z).sqrMagnitude;
                    if (d > nearest) continue;
                    nearest = d;
                    twin = l;
                }
                if (twin < 0) continue;
                var take = Mathf.Min(On(weights[twin], leftBase), onHand);
                weights[v] = Move(weights[v], rightHand, rightBase, take);
                moved += take;
                changed++;
            }
            return changed;
        }

        private static float On(BoneWeight w, int bone) =>
            (w.boneIndex0 == bone ? w.weight0 : 0f) + (w.boneIndex1 == bone ? w.weight1 : 0f)
            + (w.boneIndex2 == bone ? w.weight2 : 0f) + (w.boneIndex3 == bone ? w.weight3 : 0f);

        // The vertex's weights with 'amount' moved from one bone to another: the four largest, summing to one.
        private static BoneWeight Move(BoneWeight w, int from, int to, float amount)
        {
            var bones = new List<int> { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3, to };
            var shares = new List<float> { w.weight0, w.weight1, w.weight2, w.weight3, amount };
            for (var i = 0; i < 4; i++)
            {
                if (bones[i] != from || amount <= 0f) continue;
                var taken = Mathf.Min(shares[i], amount);
                shares[i] -= taken;
                amount -= taken;
            }
            // Slots of one bone together, then the smallest out until four are left.
            for (var i = 0; i < bones.Count; i++)
                for (var j = bones.Count - 1; j > i; j--)
                    if (bones[j] == bones[i])
                    {
                        shares[i] += shares[j];
                        bones.RemoveAt(j);
                        shares.RemoveAt(j);
                    }
            while (bones.Count > 4)
            {
                var least = 0;
                for (var i = 1; i < bones.Count; i++)
                    if (shares[i] < shares[least]) least = i;
                bones.RemoveAt(least);
                shares.RemoveAt(least);
            }
            while (bones.Count < 4)
            {
                bones.Add(0);
                shares.Add(0f);
            }
            // The largest first, as Unity keeps them.
            for (var i = 0; i < 4; i++)
                for (var j = i + 1; j < 4; j++)
                    if (shares[j] > shares[i])
                    {
                        (shares[i], shares[j]) = (shares[j], shares[i]);
                        (bones[i], bones[j]) = (bones[j], bones[i]);
                    }
            var sum = shares[0] + shares[1] + shares[2] + shares[3];
            if (sum <= 0f) return w;
            return new BoneWeight
            {
                boneIndex0 = bones[0], weight0 = shares[0] / sum,
                boneIndex1 = bones[1], weight1 = shares[1] / sum,
                boneIndex2 = bones[2], weight2 = shares[2] / sum,
                boneIndex3 = bones[3], weight3 = shares[3] / sum,
            };
        }

        // Each renderer that still draws its copy gets the mesh it had. One that draws something else for now (a copy
        // of the copy another part of the mod made) stays listed until it draws the copy again or is gone.
        private static void GiveBack()
        {
            for (var i = Worn.Count - 1; i >= 0; i--)
            {
                var (renderer, own, copy) = Worn[i];
                if (renderer == null || own == null)
                {
                    Worn.RemoveAt(i);
                    continue;
                }
                if (renderer.sharedMesh != copy) continue;
                renderer.sharedMesh = own;
                Worn.RemoveAt(i);
            }
        }

        private static void Release()
        {
            GiveBack();
            Worn.Clear();
            foreach (var copy in Copies.Values)
                if (copy != null) Object.Destroy(copy);
            Copies.Clear();
        }
    }
}
