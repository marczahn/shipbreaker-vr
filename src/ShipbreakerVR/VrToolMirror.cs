using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // The cutter's model (SM_CuttingTool_*) was only ever seen from its left in the flat game: that side has the modeled
    // detail and the painted texture, the right side is coarse and mostly plain yellow. The model is mirror-symmetric
    // across its own X axis, so its left half is copied onto the right half here: the triangles of the plain half (the
    // one with fewer triangles facing sideways) are dropped and the other half's triangles are added again, mirrored,
    // with the same UVs. Nothing is loaded from outside the game, and the game's files stay as they are; the copy lives
    // in memory and goes onto the model's MeshFilter (shared by every cutter that uses the mesh). Small print in the
    // texture (the WARNING label) reads backwards on the mirrored side.
    internal static class VrToolMirror
    {
        private const string MeshPrefix = "SM_CuttingTool_";

        // Original mesh (by instance id) -> its mirrored copy; and the filters that got a copy, to give them back.
        private static readonly Dictionary<int, Mesh> mirrored = new Dictionary<int, Mesh>();
        private static readonly Dictionary<MeshFilter, Mesh> replaced = new Dictionary<MeshFilter, Mesh>();
        private static readonly HashSet<int> failed = new HashSet<int>();

        // From VrToolModels.Place with the equipment point of the tool in the hand.
        public static void Apply(Transform point)
        {
            if (point == null) return;
            if (!Plugin.MirrorCutter.Value)
            {
                if (replaced.Count > 0) Restore();
                return;
            }
            foreach (var filter in point.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || replaced.ContainsKey(filter) || !mesh.name.StartsWith(MeshPrefix)) continue;
                var id = mesh.GetInstanceID();
                if (failed.Contains(id)) continue;
                if (!mirrored.TryGetValue(id, out var copy))
                {
                    copy = Mirror(mesh);
                    if (copy == null)
                    {
                        failed.Add(id);
                        continue;
                    }
                    mirrored[id] = copy;
                }
                replaced[filter] = mesh;
                filter.sharedMesh = copy;
            }
        }

        private static Mesh Mirror(Mesh mesh)
        {
            if (!mesh.isReadable)
            {
                Plugin.Log.LogInfo($"Cutter mirror: mesh '{mesh.name}' is not readable, left as it is");
                return null;
            }
            var vertices = mesh.vertices;
            var count = vertices.Length;

            // The plain half: the one with fewer triangles facing sideways.
            int towardPlus = 0, towardMinus = 0;
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var indices = mesh.GetTriangles(s);
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    var a = vertices[indices[i]];
                    var normal = Vector3.Cross(vertices[indices[i + 1]] - a, vertices[indices[i + 2]] - a).normalized;
                    if (normal.x > 0.7f) towardPlus++;
                    else if (normal.x < -0.7f) towardMinus++;
                }
            }
            // Triangles on the kept half (centroid on its side or on the middle) stay; those strictly on it are also
            // added mirrored, in place of the plain half's.
            var keep = towardPlus < towardMinus ? -1f : 1f;

            var copy = new Mesh { name = mesh.name + " (mirrored)" };
            if (count * 2 > 65535) copy.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            var newVertices = new Vector3[count * 2];
            for (var i = 0; i < count; i++)
            {
                newVertices[i] = vertices[i];
                newVertices[count + i] = new Vector3(-vertices[i].x, vertices[i].y, vertices[i].z);
            }
            copy.vertices = newVertices;
            var normals = mesh.normals;
            if (normals.Length == count)
            {
                var n = new Vector3[count * 2];
                for (var i = 0; i < count; i++)
                {
                    n[i] = normals[i];
                    n[count + i] = new Vector3(-normals[i].x, normals[i].y, normals[i].z);
                }
                copy.normals = n;
            }
            var tangents = mesh.tangents;
            if (tangents.Length == count)
            {
                // Mirroring flips handedness: the bitangent sign (w) turns over.
                var t = new Vector4[count * 2];
                for (var i = 0; i < count; i++)
                {
                    t[i] = tangents[i];
                    t[count + i] = new Vector4(-tangents[i].x, tangents[i].y, tangents[i].z, -tangents[i].w);
                }
                copy.tangents = t;
            }
            var colors = mesh.colors32;
            if (colors.Length == count) copy.colors32 = Twice(colors);
            var uvs = new List<Vector4>();
            for (var channel = 0; channel < 8; channel++)
            {
                mesh.GetUVs(channel, uvs);
                if (uvs.Count == count) copy.SetUVs(channel, Twice(uvs.ToArray()));
            }

            copy.subMeshCount = mesh.subMeshCount;
            int dropped = 0, added = 0;
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var indices = mesh.GetTriangles(s);
                var result = new List<int>(indices.Length + indices.Length / 8);
                var extra = new List<int>(indices.Length / 2);
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    var side = (vertices[indices[i]].x + vertices[indices[i + 1]].x + vertices[indices[i + 2]].x) * keep;
                    if (side < 0f)
                    {
                        dropped++;
                        continue;
                    }
                    result.Add(indices[i]);
                    result.Add(indices[i + 1]);
                    result.Add(indices[i + 2]);
                    if (side <= 0f) continue;
                    // Mirrored, so the winding is reversed to keep the front face outside.
                    extra.Add(count + indices[i + 2]);
                    extra.Add(count + indices[i + 1]);
                    extra.Add(count + indices[i]);
                    added++;
                }
                result.AddRange(extra);
                copy.SetTriangles(result, s, false);
            }
            copy.RecalculateBounds();
            Plugin.Log.LogInfo($"Cutter mirror: '{mesh.name}' ({count} vertices) mirrored from its {(keep > 0 ? "+X" : "-X")} half " +
                $"({towardPlus} triangles facing +X, {towardMinus} facing -X): {dropped} triangles dropped, {added} added");
            return copy;
        }

        private static T[] Twice<T>(T[] values)
        {
            var result = new T[values.Length * 2];
            values.CopyTo(result, 0);
            values.CopyTo(result, values.Length);
            return result;
        }

        // The game's meshes go back onto the filters (switched off in the config, or on unload).
        private static void Restore()
        {
            foreach (var entry in replaced)
                if (entry.Key != null) entry.Key.sharedMesh = entry.Value;
            replaced.Clear();
        }

        public static void Release()
        {
            Restore();
            foreach (var copy in mirrored.Values)
                if (copy != null) Object.Destroy(copy);
            mirrored.Clear();
            failed.Clear();
        }
    }
}
