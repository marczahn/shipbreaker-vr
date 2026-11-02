using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ShipbreakerVR
{
    // Bild/Raumanzug: the player's body in a shift (geo_suit) has no textures. The suit hanging in the Hab
    // (PRF_Shipbreaker_BodysuitMesh_HabVersion) has, but no bones. So the suit is loaded from the game's files at runtime
    // and bound to the body's bones: each of its points takes the weights of the nearest point of geo_suit, so it bends
    // with the body without gaps, a little distorted where the shapes differ. Its sleeves go, the game's own arms
    // (geo_arms) stay; the helmet stays the player's. The legs are made LegsLonger longer, the suit looked stocky.
    // Nothing of the game is copied to disk or shipped: the suit lives in memory, built from the user's own game.
    internal static class VrSuitBody
    {
        static VrSuitBody() => VrSession.OnUnload("suit body", Release);

        private const string Key = "Assets/Content/Art/Structures/Kitbash/Hab/Models/PRF_Shipbreaker_BodysuitMesh_HabVersion.prefab";
        private static readonly string[] Parts =
            { "SM_Bodysuit_Mesh_Bodysuit", "SM_Bodysuit_Mesh_Accesories", "SM_Bodysuit_Mesh_Chestplate", "SM_Bodysuit_Mesh_Backpack" };
        private const string Backpack = "SM_Bodysuit_Mesh_Backpack";
        private const string Torso = "SM_Bodysuit_Mesh_Bodysuit";
        private const string BodyName = "geo_suit";
        private const string Pelvis = "rig_pelvis";

        // In meters at the suit's own size.
        private const float LegsLonger = 0.05f;
        // The sleeves: what lies further out than this to the side, above SleevesAbove of the height.
        private const float SleevesOut = 0.21f;
        private const float SleevesAbove = 0.47f;
        private const float Cell = 0.04f;

        private static AsyncOperationHandle<GameObject> handle;
        private static bool loading;
        private static bool failed;
        private static GameObject prefab;
        private static Mesh suit;
        private static Material[] materials;
        private static SkinnedMeshRenderer body;
        private static SkinnedMeshRenderer dress;
        private static float nextSearch;

        public static void Update()
        {
            if (!VrSettings.SuitBody.Value || !VrXr.IsRunning)
            {
                Undress();
                return;
            }
            if (failed) return;
            if (prefab == null && !Load()) return;
            if (dress != null && body != null) return;
            if (Time.unscaledTime < nextSearch) return;
            nextSearch = Time.unscaledTime + 1f;
            foreach (var found in Object.FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (found.name != BodyName || found.sharedMesh == null) continue;
                try
                {
                    if (suit == null) Build(found);
                    if (suit != null) Dress(found);
                }
                catch (System.Exception e)
                {
                    failed = true;
                    Plugin.Log.LogWarning($"Suit body: {e}");
                }
                return;
            }
        }

        private static bool Load()
        {
            if (!loading)
            {
                try
                {
                    handle = Addressables.LoadAssetAsync<GameObject>(Key);
                    loading = true;
                }
                catch (System.Exception e)
                {
                    failed = true;
                    Plugin.Log.LogWarning($"Suit body: cannot load the suit: {e.Message}");
                }
                return false;
            }
            if (!handle.IsDone) return false;
            loading = false;
            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                failed = true;
                Plugin.Log.LogWarning($"Suit body: the suit did not load ({handle.Status})");
                return false;
            }
            prefab = handle.Result;
            Plugin.Log.LogInfo($"Suit body: '{prefab.name}' loaded");
            return true;
        }

        private static void Dress(SkinnedMeshRenderer found)
        {
            Undress();
            var go = new GameObject("ShipbreakerVR Suit") { layer = found.gameObject.layer };
            go.transform.SetParent(found.transform.parent, false);
            go.transform.localPosition = found.transform.localPosition;
            go.transform.localRotation = found.transform.localRotation;
            go.transform.localScale = found.transform.localScale;
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = suit;
            renderer.bones = found.bones;
            renderer.rootBone = found.rootBone;
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = found.shadowCastingMode;
            renderer.updateWhenOffscreen = true;
            found.enabled = false;
            body = found;
            dress = renderer;
            Plugin.Log.LogInfo($"Suit body: dressed '{VrUtil.Path(found.transform)}'");
        }

        private static void Undress()
        {
            if (body != null) body.enabled = true;
            body = null;
            if (dress != null) Object.Destroy(dress.gameObject);
            dress = null;
        }

        private static void Build(SkinnedMeshRenderer target)
        {
            var source = target.sharedMesh;
            if (!source.isReadable) throw new System.InvalidOperationException($"'{source.name}' is not readable");
            var root = prefab.transform;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<List<int>>();
            var mats = new List<Material>();
            var parts = new List<(string Name, int From, int To)>();
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || System.Array.IndexOf(Parts, mesh.name) < 0) continue;
                if (!mesh.isReadable)
                {
                    Plugin.Log.LogWarning($"Suit body: '{mesh.name}' is not readable, left out");
                    continue;
                }
                var matrix = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                var start = positions.Count;
                var vertices = mesh.vertices;
                var meshNormals = mesh.normals;
                var meshUvs = mesh.uv;
                for (var i = 0; i < vertices.Length; i++)
                {
                    positions.Add(matrix.MultiplyPoint3x4(vertices[i]));
                    normals.Add(i < meshNormals.Length ? matrix.MultiplyVector(meshNormals[i]).normalized : Vector3.up);
                    uvs.Add(i < meshUvs.Length ? meshUvs[i] : Vector2.zero);
                }
                for (var s = 0; s < mesh.subMeshCount; s++)
                {
                    var list = new List<int>();
                    foreach (var index in mesh.GetTriangles(s)) list.Add(index + start);
                    triangles.Add(list);
                    var shared = renderer.sharedMaterials;
                    mats.Add(shared.Length > 0 ? shared[Mathf.Min(s, shared.Length - 1)] : null);
                }
                parts.Add((mesh.name, start, positions.Count));
            }
            if (positions.Count == 0) throw new System.InvalidOperationException("no suit parts found");

            // Turned to face +z, as the body does, from where the backpack sits.
            var torso = Centroid(positions, parts, Torso);
            var back = Centroid(positions, parts, Backpack) - torso;
            back.y = 0f;
            var turn = back.sqrMagnitude > 1e-6f ? Quaternion.FromToRotation(-back.normalized, Vector3.forward) : Quaternion.identity;
            var low = float.MaxValue;
            for (var i = 0; i < positions.Count; i++)
            {
                positions[i] = turn * positions[i];
                normals[i] = turn * normals[i];
                low = Mathf.Min(low, positions[i].y);
            }
            var high = float.MinValue;
            for (var i = 0; i < positions.Count; i++)
            {
                positions[i] -= new Vector3(0f, low, 0f);
                high = Mathf.Max(high, positions[i].y);
            }
            // Centered on the body's pelvis.
            var pelvis = BindPosition(target, Pelvis);
            var middle = Centroid(positions, parts, Torso);
            var shift = new Vector3(pelvis.x - middle.x, 0f, pelvis.z - middle.z);
            for (var i = 0; i < positions.Count; i++) positions[i] += shift;

            // Without the sleeves.
            var cut = 0;
            foreach (var list in triangles)
                for (var t = list.Count - 3; t >= 0; t -= 3)
                {
                    var center = (positions[list[t]] + positions[list[t + 1]] + positions[list[t + 2]]) / 3f;
                    if (Mathf.Abs(center.x - pelvis.x) <= SleevesOut || center.y <= SleevesAbove * high) continue;
                    list.RemoveRange(t, 3);
                    cut++;
                }

            var weights = Weights(target, source, positions);
            // Longer legs: below the pelvis stretched, above moved up, after the weights were taken where it was.
            var hip = Mathf.Max(pelvis.y, 0.1f);
            for (var i = 0; i < positions.Count; i++)
            {
                var p = positions[i];
                p.y = p.y < hip ? p.y * (hip + LegsLonger) / hip : p.y + LegsLonger;
                positions[i] = p;
            }

            suit = new Mesh { name = "ShipbreakerVR suit", hideFlags = HideFlags.HideAndDontSave };
            suit.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            suit.SetVertices(positions);
            suit.SetNormals(normals);
            suit.SetUVs(0, uvs);
            suit.boneWeights = weights;
            suit.bindposes = source.bindposes;
            suit.subMeshCount = triangles.Count;
            for (var s = 0; s < triangles.Count; s++) suit.SetTriangles(triangles[s], s);
            suit.RecalculateBounds();
            materials = mats.ToArray();
            Plugin.Log.LogInfo($"Suit body: {positions.Count} vertices from {parts.Count} part(s), height {high:0.00} m (+{LegsLonger:0.00} legs), pelvis of the body at {pelvis}, turned {turn.eulerAngles.y:0}°, {cut} sleeve triangles cut");
        }

        private static Vector3 Centroid(List<Vector3> positions, List<(string Name, int From, int To)> parts, string name)
        {
            var sum = Vector3.zero;
            var count = 0;
            foreach (var part in parts)
            {
                if (part.Name != name) continue;
                for (var i = part.From; i < part.To; i++) sum += positions[i];
                count += part.To - part.From;
            }
            return count > 0 ? sum / count : Vector3.zero;
        }

        // Where a bone sits in the body's mesh, from its bind pose.
        private static Vector3 BindPosition(SkinnedMeshRenderer target, string bone)
        {
            var bones = target.bones;
            var binds = target.sharedMesh.bindposes;
            for (var i = 0; i < bones.Length && i < binds.Length; i++)
                if (bones[i] != null && bones[i].name == bone) return binds[i].inverse.GetColumn(3);
            return new Vector3(0f, 0.92f, 0f);
        }

        // Each point gets the weights of the nearest visible point of the body (its first submesh; the others are hidden).
        private static BoneWeight[] Weights(SkinnedMeshRenderer target, Mesh source, List<Vector3> positions)
        {
            var vertices = source.vertices;
            var sourceWeights = source.boneWeights;
            var grid = new Dictionary<Vector3Int, List<int>>();
            foreach (var index in new HashSet<int>(source.GetTriangles(0)))
            {
                var cell = CellOf(vertices[index]);
                if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new List<int>();
                list.Add(index);
            }
            var result = new BoneWeight[positions.Count];
            for (var i = 0; i < positions.Count; i++)
            {
                var p = positions[i];
                var at = CellOf(p);
                var best = -1;
                var bestDistance = float.MaxValue;
                for (var ring = 1; ring <= 8 && best < 0; ring++)
                    for (var x = -ring; x <= ring; x++)
                        for (var y = -ring; y <= ring; y++)
                            for (var z = -ring; z <= ring; z++)
                            {
                                if (!grid.TryGetValue(new Vector3Int(at.x + x, at.y + y, at.z + z), out var list)) continue;
                                foreach (var index in list)
                                {
                                    var distance = (vertices[index] - p).sqrMagnitude;
                                    if (distance < bestDistance)
                                    {
                                        bestDistance = distance;
                                        best = index;
                                    }
                                }
                            }
                result[i] = best >= 0 ? sourceWeights[best] : new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            }
            return result;
        }

        private static Vector3Int CellOf(Vector3 p) =>
            new Vector3Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell), Mathf.FloorToInt(p.z / Cell));

        private static void Release()
        {
            Undress();
            if (suit != null) Object.Destroy(suit);
            suit = null;
            if (handle.IsValid()) Addressables.Release(handle);
            prefab = null;
            loading = false;
        }
    }
}
