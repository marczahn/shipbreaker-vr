using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // The grapple's model has holes that the flat game never shows, its camera stays behind the tool: on each of the
    // four jaws (GB_GrappleTool_Teeth_1 to _4) a strip of 11 by 1.4 cm is missing where the front meets the underside,
    // and the thin frames on the body (GB_GrappleTool_Main_1) have no inner walls round their windows. The game draws
    // one side of a face only, so in VR, seen from the front or from below, one looks through the jaws (Raik,
    // 2026-11-26). Here each such part draws a copy of its mesh with the holes closed, worked out at run time from the
    // game's own mesh: nothing of the game's files ships with the mod, and nothing in them changes.
    //   - An open loop of up to MaxCorners edges and MaxArea in size is a hole and is closed, when the faces along it
    //     agree with the side the patch faces (a single plate's outline has its own face on the other side: left).
    //   - Two open loops of the same corners close together (a frame of two thin plates) are the edges of a window:
    //     the window stays open and gets walls from one loop to the other.
    // The new faces have vertices of their own at the loop's corners, flat to the patch, all on one point of the
    // texture: the middle of the largest face beside the hole, so the patch takes its colour (the jaws' black).
    // The parts get their mesh back when VR stops.
    internal static class VrGrappleHoles
    {
        static VrGrappleHoles() => VrSession.OnUnload("grapple holes", Release);

        private const string PartPrefix = "GB_GrappleTool_";
        // Corners that are one place (split along UV seams), in metres of the mesh's space.
        private const float Weld = 0.00005f;
        internal const int MaxCorners = 8;
        internal const float MaxArea = 0.003f;
        // How close the middles of two loops are when they are the two sides of one thin frame.
        internal const float Twin = 0.01f;

        // By the instance id of the mesh a filter had: its copy, or null when it needs none (also the copies themselves).
        private static readonly Dictionary<int, Mesh> Copies = new Dictionary<int, Mesh>();
        private static readonly List<(MeshFilter Filter, Mesh Own, Mesh Copy)> Worn = new List<(MeshFilter, Mesh, Mesh)>();
        private static readonly List<MeshFilter> filters = new List<MeshFilter>();
        private static float nextLook;

        // A patch: new vertices, each a copy of a corner (Source) with its own normal and UV, and triangles over them
        // (indices counted on from the mesh's vertex count), each in the submesh of the face it borders.
        internal sealed class Patch
        {
            public readonly List<int> Source = new List<int>();
            public readonly List<Vector3> Normal = new List<Vector3>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly List<int> Triangles = new List<int>();
            public readonly List<int> Submesh = new List<int>();
            public int Holes;
            public int Windows;
        }

        public static void Update()
        {
            if (!VrXr.IsRunning)
            {
                GiveBack();
                return;
            }
            if (Time.unscaledTime < nextLook) return;
            nextLook = Time.unscaledTime + 1f;
            var equipment = GameRefs.Equipment;
            if (equipment == null) return;
            // The player's hierarchy holds the grapple in the hand and its copy on the hip.
            equipment.transform.root.GetComponentsInChildren(true, filters);
            foreach (var filter in filters)
            {
                var own = filter.sharedMesh;
                if (own == null || !own.name.StartsWith(PartPrefix)) continue;
                if (!Copies.TryGetValue(own.GetInstanceID(), out var copy))
                {
                    copy = Copy(own);
                    Copies[own.GetInstanceID()] = copy;
                    if (copy != null) Copies[copy.GetInstanceID()] = null;
                }
                if (copy == null) continue;
                filter.sharedMesh = copy;
                Worn.RemoveAll(w => w.Filter == null || w.Filter == filter);
                Worn.Add((filter, own, copy));
            }
            filters.Clear();
        }

        private static Mesh Copy(Mesh own)
        {
            if (!own.isReadable) return null;
            var vertices = own.vertices;
            var uv = own.uv;
            if (uv.Length != vertices.Length) return null;
            var triangles = new List<int>();
            var submeshOf = new List<int>();
            for (var s = 0; s < own.subMeshCount; s++)
            {
                if (own.GetTopology(s) != MeshTopology.Triangles) return null;
                var part = own.GetTriangles(s);
                triangles.AddRange(part);
                for (var i = 0; i < part.Length / 3; i++) submeshOf.Add(s);
            }
            var patch = Close(vertices, uv, triangles.ToArray(), submeshOf.ToArray());
            if (patch.Triangles.Count == 0) return null;

            var copy = Object.Instantiate(own);
            copy.name = own.name + " (ShipbreakerVR closed)";
            copy.hideFlags = HideFlags.HideAndDontSave;
            var count = vertices.Length + patch.Source.Count;
            if (count > 65535) copy.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            copy.vertices = Grow(vertices, patch.Source, i => vertices[i]);
            var normals = own.normals;
            if (normals.Length == vertices.Length) copy.normals = Grow(normals, patch.Source, null, patch.Normal);
            var tangents = own.tangents;
            if (tangents.Length == vertices.Length) copy.tangents = Grow(tangents, patch.Source, i => tangents[i]);
            var colors = own.colors32;
            if (colors.Length == vertices.Length) copy.colors32 = Grow(colors, patch.Source, i => colors[i]);
            var weights = own.boneWeights;
            if (weights.Length == vertices.Length) copy.boneWeights = Grow(weights, patch.Source, i => weights[i]);
            for (var c = 0; c < 8; c++)
            {
                var attribute = UnityEngine.Rendering.VertexAttribute.TexCoord0 + c;
                if (!own.HasVertexAttribute(attribute)) continue;
                var channel = new List<Vector4>();
                own.GetUVs(c, channel);
                if (channel.Count != vertices.Length) continue;
                for (var k = 0; k < patch.Source.Count; k++)
                    channel.Add(c == 0 ? new Vector4(patch.Uv[k].x, patch.Uv[k].y, 0f, 0f) : channel[patch.Source[k]]);
                // In the channel's own size, so the copy's vertex layout stays the game's.
                switch (own.GetVertexAttributeDimension(attribute))
                {
                    case 2: copy.SetUVs(c, channel.ConvertAll(u => (Vector2)u)); break;
                    case 3: copy.SetUVs(c, channel.ConvertAll(u => (Vector3)u)); break;
                    default: copy.SetUVs(c, channel); break;
                }
            }
            for (var s = 0; s < own.subMeshCount; s++)
            {
                var list = new List<int>(own.GetTriangles(s));
                for (var t = 0; t < patch.Submesh.Count; t++)
                {
                    if (patch.Submesh[t] != s) continue;
                    for (var k = 0; k < 3; k++) list.Add(vertices.Length + patch.Triangles[t * 3 + k]);
                }
                copy.SetTriangles(list, s, false);
            }
            Plugin.Log.LogInfo($"Grapple holes: '{own.name}': {patch.Holes} hole(s) closed, {patch.Windows} window(s) given walls, {patch.Triangles.Count / 3} triangles added");
            return copy;
        }

        private static T[] Grow<T>(T[] values, List<int> source, System.Func<int, T> copyOf, List<T> given = null)
        {
            var grown = new T[values.Length + source.Count];
            values.CopyTo(grown, 0);
            for (var k = 0; k < source.Count; k++) grown[values.Length + k] = given != null ? given[k] : copyOf(source[k]);
            return grown;
        }

        // The patch that closes the mesh's holes and walls its windows (see above). triangles as the mesh has them,
        // submeshOf the submesh of each triangle.
        internal static Patch Close(Vector3[] vertices, Vector2[] uv, int[] triangles, int[] submeshOf)
        {
            var patch = new Patch();
            // One id per place.
            var place = new int[vertices.Length];
            var ids = new Dictionary<(long, long, long), int>();
            for (var v = 0; v < vertices.Length; v++)
            {
                var p = vertices[v];
                var key = ((long)Mathf.Round(p.x / Weld), (long)Mathf.Round(p.y / Weld), (long)Mathf.Round(p.z / Weld));
                if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
                place[v] = id;
            }
            // Each edge as its face runs it; an edge that one face alone has is open, and the patch runs it back.
            var uses = new Dictionary<(int, int), int>();
            var faceOf = new Dictionary<(int, int), int>();
            var triangleCount = triangles.Length / 3;
            for (var t = 0; t < triangleCount; t++)
                for (var k = 0; k < 3; k++)
                {
                    int a = place[triangles[t * 3 + k]], b = place[triangles[t * 3 + (k + 1) % 3]];
                    if (a == b) continue;
                    var key = a < b ? (a, b) : (b, a);
                    uses.TryGetValue(key, out var n);
                    uses[key] = n + 1;
                    faceOf[(a, b)] = t;
                }
            var next = new Dictionary<int, int>();     // along the patch: from a place to the next
            var cornerAt = new Dictionary<int, int>(); // a vertex at each place on an open edge
            var forked = new HashSet<int>();
            foreach (var edge in faceOf)
            {
                var (a, b) = edge.Key;
                if (uses[a < b ? (a, b) : (b, a)] != 1) continue;
                if (next.ContainsKey(b)) forked.Add(b);
                next[b] = a;
                var t = edge.Value;
                for (var k = 0; k < 3; k++)
                    if (place[triangles[t * 3 + k]] == b) cornerAt[b] = triangles[t * 3 + k];
            }
            // The open loops.
            var loops = new List<List<int>>();
            var seen = new HashSet<int>();
            foreach (var start in next.Keys)
            {
                if (seen.Contains(start) || forked.Contains(start)) continue;
                var loop = new List<int> { start };
                seen.Add(start);
                var closed = false;
                while (loop.Count <= 64)
                {
                    if (!next.TryGetValue(loop[loop.Count - 1], out var n) || forked.Contains(n)) break;
                    if (n == start)
                    {
                        closed = true;
                        break;
                    }
                    if (!seen.Add(n)) break;
                    loop.Add(n);
                }
                if (closed && loop.Count >= 3) loops.Add(loop);
            }
            var middles = new List<Vector3>();
            foreach (var loop in loops)
            {
                var sum = Vector3.zero;
                foreach (var p in loop) sum += vertices[cornerAt[p]];
                middles.Add(sum / loop.Count);
            }
            var done = new bool[loops.Count];
            for (var i = 0; i < loops.Count; i++)
            {
                if (done[i]) continue;
                var twin = -1;
                for (var j = 0; j < loops.Count; j++)
                    if (j != i && loops[j].Count == loops[i].Count && (middles[j] - middles[i]).magnitude < Twin) twin = j;
                if (twin >= 0)
                {
                    done[i] = done[twin] = true;
                    if (Wall(loops[i], loops[twin], next, cornerAt, place, vertices, uv, triangles, submeshOf, faceOf, patch)) patch.Windows++;
                    continue;
                }
                done[i] = true;
                if (Fill(loops[i], cornerAt, vertices, uv, triangles, submeshOf, faceOf, patch)) patch.Holes++;
            }
            return patch;
        }

        private static Vector3 FaceNormal(Vector3[] vertices, int[] triangles, int t) =>
            Vector3.Cross(vertices[triangles[t * 3 + 1]] - vertices[triangles[t * 3]], vertices[triangles[t * 3 + 2]] - vertices[triangles[t * 3]]);

        // The face beside the open edge from place a to place b (as the patch runs it: the face runs b to a).
        private static int Beside(Dictionary<(int, int), int> faceOf, int a, int b) => faceOf.TryGetValue((b, a), out var t) ? t : -1;

        private static Vector2 Middle(Vector2[] uv, int[] triangles, int t) =>
            (uv[triangles[t * 3]] + uv[triangles[t * 3 + 1]] + uv[triangles[t * 3 + 2]]) / 3f;

        private static bool Fill(List<int> loop, Dictionary<int, int> cornerAt, Vector3[] vertices, Vector2[] uv, int[] triangles,
            int[] submeshOf, Dictionary<(int, int), int> faceOf, Patch patch)
        {
            if (loop.Count > MaxCorners) return false;
            var corners = new List<int>();
            foreach (var p in loop) corners.Add(cornerAt[p]);
            var normal = Vector3.zero;
            for (var i = 1; i + 1 < corners.Count; i++)
                normal += Vector3.Cross(vertices[corners[i]] - vertices[corners[0]], vertices[corners[i + 1]] - vertices[corners[0]]);
            var area = normal.magnitude / 2f;
            if (area > MaxArea || area < 1e-9f) return false;
            normal.Normalize();
            var beside = Vector3.zero;
            var largest = -1;
            var largestSize = 0f;
            for (var i = 0; i < loop.Count; i++)
            {
                var t = Beside(faceOf, loop[i], loop[(i + 1) % loop.Count]);
                if (t < 0) return false;
                var n = FaceNormal(vertices, triangles, t);
                beside += n.normalized;
                if (n.magnitude > largestSize)
                {
                    largestSize = n.magnitude;
                    largest = t;
                }
            }
            // A plate's outline: the faces along it face the other way. Round a hole they stand across the patch (a
            // box without a side) or lean its way.
            if (Vector3.Dot(beside.normalized, normal) < -0.5f) return false;
            var ears = Ears(corners, vertices, normal);
            if (ears == null) return false;
            var first = patch.Source.Count;
            var colour = Middle(uv, triangles, largest);
            foreach (var c in corners)
            {
                patch.Source.Add(c);
                patch.Normal.Add(normal);
                patch.Uv.Add(colour);
            }
            for (var e = 0; e < ears.Count; e += 3)
            {
                patch.Triangles.Add(first + ears[e]);
                patch.Triangles.Add(first + ears[e + 1]);
                patch.Triangles.Add(first + ears[e + 2]);
                patch.Submesh.Add(submeshOf[largest]);
            }
            return true;
        }

        // Walls between the two loops of a window: each corner of one finds the nearest of the other, and the wall from
        // a to b on the first runs on the second from b's to a's twin (each loop as its patch runs it).
        private static bool Wall(List<int> one, List<int> other, Dictionary<int, int> next, Dictionary<int, int> cornerAt, int[] place,
            Vector3[] vertices, Vector2[] uv, int[] triangles, int[] submeshOf, Dictionary<(int, int), int> faceOf, Patch patch)
        {
            var twinOf = new Dictionary<int, int>();
            var taken = new HashSet<int>();
            foreach (var a in one)
            {
                var best = -1;
                var nearest = float.MaxValue;
                foreach (var b in other)
                {
                    var d = (vertices[cornerAt[b]] - vertices[cornerAt[a]]).sqrMagnitude;
                    if (d >= nearest) continue;
                    nearest = d;
                    best = b;
                }
                if (!taken.Add(best)) return false;
                twinOf[a] = best;
            }
            var walls = new List<(int A, int B, int C, int D, Vector3 Normal, int Face)>();
            for (var i = 0; i < one.Count; i++)
            {
                int a = one[i], b = one[(i + 1) % one.Count];
                int ta = twinOf[a], tb = twinOf[b];
                if (!next.TryGetValue(tb, out var n) || n != ta) return false;
                var t = Beside(faceOf, a, b);
                if (t < 0) return false;
                Vector3 pa = vertices[cornerAt[a]], pb = vertices[cornerAt[b]], pc = vertices[cornerAt[tb]];
                var normal = Vector3.Cross(pb - pa, pc - pa);
                if (normal.sqrMagnitude < 1e-14f) return false;
                walls.Add((cornerAt[a], cornerAt[b], cornerAt[tb], cornerAt[ta], normal.normalized, t));
            }
            foreach (var w in walls)
            {
                var first = patch.Source.Count;
                var colour = Middle(uv, triangles, w.Face);
                foreach (var c in new[] { w.A, w.B, w.C, w.D })
                {
                    patch.Source.Add(c);
                    patch.Normal.Add(w.Normal);
                    patch.Uv.Add(colour);
                }
                patch.Triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
                patch.Submesh.Add(submeshOf[w.Face]);
                patch.Submesh.Add(submeshOf[w.Face]);
            }
            return true;
        }

        // Ear clipping of a loop of corners in the plane across 'normal', keeping its winding: triangles as positions in
        // the loop, null when it does not come apart.
        internal static List<int> Ears(List<int> corners, Vector3[] vertices, Vector3 normal)
        {
            var left = new List<int>();
            for (var i = 0; i < corners.Count; i++) left.Add(i);
            var result = new List<int>();
            var guard = 0;
            while (left.Count > 3 && guard++ < 256)
            {
                var cut = false;
                for (var i = 0; i < left.Count; i++)
                {
                    int a = left[(i + left.Count - 1) % left.Count], b = left[i], c = left[(i + 1) % left.Count];
                    Vector3 pa = vertices[corners[a]], pb = vertices[corners[b]], pc = vertices[corners[c]];
                    if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), normal) <= 0f) continue;
                    var inside = false;
                    foreach (var o in left)
                    {
                        if (o == a || o == b || o == c) continue;
                        if (Within(vertices[corners[o]], pa, pb, pc, normal)) inside = true;
                    }
                    if (inside) continue;
                    result.AddRange(new[] { a, b, c });
                    left.RemoveAt(i);
                    cut = true;
                    break;
                }
                if (!cut) return null;
            }
            if (left.Count == 3) result.AddRange(new[] { left[0], left[1], left[2] });
            return result;
        }

        private static bool Within(Vector3 p, Vector3 a, Vector3 b, Vector3 c, Vector3 normal) =>
            Vector3.Dot(Vector3.Cross(b - a, p - a), normal) >= 0f
            && Vector3.Dot(Vector3.Cross(c - b, p - b), normal) >= 0f
            && Vector3.Dot(Vector3.Cross(a - c, p - c), normal) >= 0f;

        private static void GiveBack()
        {
            for (var i = Worn.Count - 1; i >= 0; i--)
            {
                var (filter, own, copy) = Worn[i];
                if (filter == null || own == null)
                {
                    Worn.RemoveAt(i);
                    continue;
                }
                if (filter.sharedMesh == copy) filter.sharedMesh = own;
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
