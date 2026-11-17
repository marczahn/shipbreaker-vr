using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // Experiment (Raik, 2026-11-17): the collision shape of the cutter and the grapple as their models show them
    // (ToolPieces), built once per mesh and size and used twice: by the tool in the hand, which tests its pieces against
    // what the boxes around its parts touch (VrToolCollide), and by a tool let go, whose floating body carries them as
    // its colliders (VrLooseTools).
    // The pieces are in the part's own axes, in meters (the part's size already in them), so every collider made from
    // them sits at the part's place and turn with no scale of its own.
    internal static class VrToolShape
    {
        static VrToolShape() => VrSession.OnUnload("tool shapes", Release);

        internal sealed class Part
        {
            public ToolPieces.Piece[] Pieces;
            // The hull of each piece as a mesh for a convex collider; null for a plate (a box).
            public Mesh[] Hulls;
            // Colliders that never meet anything (triggers parked far off), only for Physics.ComputePenetration with a
            // place and turn given: one per piece, with where it sits in the part's axes.
            public Collider[] Probes;
            public Vector3[] At;
            public Quaternion[] Turn;
            // A ball around each piece (its middle in the part's axes and how far it reaches from there), to leave out
            // the pieces that are nowhere near a thing before the physics engine is asked.
            public Vector3[] Mid;
            public float[] Reach;
        }

        private static readonly Dictionary<long, Part> Parts = new Dictionary<long, Part>();
        private static Transform park;
        private static readonly Vector3 ParkedAt = new Vector3(0f, -90000f, 0f);
        private static int logs;

        // The model's parts that count for its shape: shown meshes that are not tiny or paper-thin (stickers).
        public static void PartsOf(Transform model, List<MeshFilter> into)
        {
            into.Clear();
            var smallest = 0.01f * VrCamera.WorldScale;
            var thinnest = 0.002f * VrCamera.WorldScale;
            foreach (var renderer in model.GetComponentsInChildren<MeshRenderer>())
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (!renderer.enabled || filter == null || filter.sharedMesh == null) continue;
                var size = Vector3.Scale(filter.sharedMesh.bounds.size, renderer.transform.lossyScale);
                size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                if (Mathf.Max(size.x, size.y, size.z) < smallest * 2f || Mathf.Min(size.x, size.y, size.z) < thinnest) continue;
                into.Add(filter);
            }
        }

        // The pieces of that mesh at that size; built on first use (a few milliseconds for a large mesh).
        public static Part For(Mesh mesh, Vector3 scale)
        {
            if (mesh == null) return null;
            // By mesh and size to the millimeter per meter: the cutter's mirrored copy (VrToolMirror) is a mesh of its own.
            var key = ((long)mesh.GetInstanceID() << 32) ^ (uint)(Mathf.RoundToInt(scale.x * 1000f) * 73856093 ^ Mathf.RoundToInt(scale.y * 1000f) * 19349663 ^ Mathf.RoundToInt(scale.z * 1000f) * 83492791);
            if (Parts.TryGetValue(key, out var part)) return part;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var world = VrCamera.WorldScale;
            List<ToolPieces.Piece> pieces;
            var read = mesh.isReadable;
            if (read)
            {
                var vertices = mesh.vertices;
                for (var i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], scale);
                pieces = ToolPieces.Split(vertices, mesh.triangles, world);
            }
            else pieces = new List<ToolPieces.Piece>();
            if (pieces.Count == 0)
            {
                // Not readable, or nothing came of it: the box around it.
                var bounds = mesh.bounds;
                var half = Vector3.Scale(bounds.extents, scale);
                pieces.Add(new ToolPieces.Piece
                {
                    Flat = true,
                    Center = Vector3.Scale(bounds.center, scale),
                    U = Vector3.right,
                    V = Vector3.up,
                    N = Vector3.forward,
                    Half = new Vector3(Mathf.Abs(half.x), Mathf.Abs(half.y), Mathf.Abs(half.z)),
                    Middle = Vector3.Scale(bounds.center, scale),
                });
            }
            part = new Part
            {
                Pieces = pieces.ToArray(),
                Hulls = new Mesh[pieces.Count],
                Probes = new Collider[pieces.Count],
                At = new Vector3[pieces.Count],
                Turn = new Quaternion[pieces.Count],
                Mid = new Vector3[pieces.Count],
                Reach = new float[pieces.Count],
            };
            if (park == null)
            {
                var holder = new GameObject("ShipbreakerVR tool shape probes") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(holder);
                holder.transform.position = ParkedAt;
                park = holder.transform;
            }
            var plates = 0;
            for (var i = 0; i < part.Pieces.Length; i++)
            {
                var piece = part.Pieces[i];
                if (piece.Flat)
                {
                    plates++;
                    part.At[i] = piece.Center;
                    part.Turn[i] = Quaternion.LookRotation(piece.N, piece.V);
                    part.Mid[i] = piece.Center;
                    part.Reach[i] = piece.Half.magnitude;
                }
                else
                {
                    part.Hulls[i] = Hull(piece.Points);
                    part.At[i] = Vector3.zero;
                    part.Turn[i] = Quaternion.identity;
                    part.Mid[i] = piece.Middle;
                    foreach (var point in piece.Points) part.Reach[i] = Mathf.Max(part.Reach[i], (point - piece.Middle).magnitude);
                }
                part.Probes[i] = Add(park, part, i, park.position, Quaternion.identity, 0, true);
            }
            Parts[key] = part;
            if (logs++ < 40)
                Plugin.Log.LogInfo($"Tool shape: '{mesh.name}' at {scale.x:0.00} of its size, {Vector3.Scale(mesh.bounds.size, scale) * 100f:F0} cm, "
                    + (read ? $"{mesh.triangles.Length / 3} triangles cut into {part.Pieces.Length} piece(s), {plates} of them plates" : "not readable, the box around it")
                    + $", in {watch.ElapsedMilliseconds} ms");
            return part;
        }

        // A mesh of those points with any triangles over them: the physics engine builds the hull from the points alone.
        private static Mesh Hull(Vector3[] points)
        {
            var triangles = new int[(points.Length - 2) * 3];
            for (var i = 0; i < points.Length - 2; i++)
            {
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = i + 2;
            }
            return new Mesh { name = "ShipbreakerVR tool piece", hideFlags = HideFlags.HideAndDontSave, vertices = points, triangles = triangles };
        }

        // A collider for one piece on an object of its own under parent, for a part at that place and turn (world).
        public static Collider Add(Transform parent, Part part, int index, Vector3 partAt, Quaternion partTurn, int layer, bool trigger)
        {
            var holder = new GameObject("piece") { layer = layer };
            holder.transform.SetParent(parent, false);
            holder.transform.SetPositionAndRotation(partAt + partTurn * part.At[index], partTurn * part.Turn[index]);
            if (part.Hulls[index] != null)
            {
                var hull = holder.AddComponent<MeshCollider>();
                hull.sharedMesh = part.Hulls[index];
                hull.convex = true;
                hull.isTrigger = trigger;
                return hull;
            }
            var box = holder.AddComponent<BoxCollider>();
            box.size = part.Pieces[index].Half * 2f;
            box.isTrigger = trigger;
            return box;
        }

        // The deepest place where a part at that place and turn lies in a thing: the way out of it, how deep, and the
        // middle of the piece that is in it. Deeper than graze only, so a tool does not catch on what it merely touches.
        public static bool Touches(Part part, Vector3 partAt, Quaternion partTurn, Collider thing, float graze, out Vector3 way, out float depth, out Vector3 at)
        {
            way = at = Vector3.zero;
            depth = 0f;
            var frame = thing.transform;
            var thingAt = frame.position;
            var thingTurn = frame.rotation;
            // The thing's own box, in its axes, where it has one: a wall is a thin plate there, however it is turned,
            // while its box along the world's axes may take in the whole tool.
            var boxed = true;
            Bounds own = default;
            if (thing is BoxCollider box) own = new Bounds(box.center, box.size);
            else if (thing is MeshCollider mesh && mesh.sharedMesh != null) own = mesh.sharedMesh.bounds;
            else boxed = false;
            var around = thing.bounds;
            for (var i = 0; i < part.Probes.Length; i++)
            {
                var probe = part.Probes[i];
                if (probe == null) continue;
                var middle = partAt + partTurn * part.Mid[i];
                var reach = part.Reach[i] + graze;
                if (boxed)
                {
                    var inside = frame.InverseTransformPoint(middle);
                    var nearest = frame.TransformPoint(Vector3.Max(own.min, Vector3.Min(own.max, inside)));
                    if ((nearest - middle).sqrMagnitude > reach * reach) continue;
                }
                else if (around.SqrDistance(middle) > reach * reach) continue;
                if (!Physics.ComputePenetration(probe, partAt + partTurn * part.At[i], partTurn * part.Turn[i], thing, thingAt, thingTurn, out var direction, out var distance)) continue;
                if (distance <= graze || distance <= depth || direction.sqrMagnitude < 1e-8f) continue;
                depth = distance;
                way = direction.normalized;
                at = partAt + partTurn * part.Pieces[i].Middle;
            }
            return depth > 0f;
        }

        // On unload (hot reload): the probes and the hull meshes go with the old instance.
        public static void Release()
        {
            foreach (var part in Parts.Values)
                foreach (var hull in part.Hulls)
                    if (hull != null) Object.Destroy(hull);
            Parts.Clear();
            if (park != null) Object.Destroy(park.gameObject);
            park = null;
        }
    }
}
