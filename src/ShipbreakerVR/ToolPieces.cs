using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // Experiment (Raik, 2026-11-17): a tool's shape for collisions as its model shows it. A box around each mesh was far
    // off: the cutter is one mesh, so it was one box of 16 x 34 x 93 cm, and the grapple's housing with the ring under it
    // a box of 19 x 42 x 50 cm. A dynamic body may only carry convex shapes, so each mesh is cut into convex pieces: a
    // grid of Cell along its two longer sides (its shortest side is kept whole below KeepWhole, so the pieces are solid
    // from one side of the tool to the other), each triangle clipped to the cells it crosses. Seen from the tool's side
    // the pieces follow its outline, hollows and holes included, to about a cell.
    // A piece is the convex hull of its points, which the physics engine builds from them, cut down to the points that
    // lie furthest out in Directions ways (a hull of a few dozen corners). A piece with all its points in one plane has
    // no volume for a hull and becomes a thin box.
    // Engine-free, so the tests run it (ToolPieceTests).
    internal static class ToolPieces
    {
        internal struct Piece
        {
            // The hull's corner points; for a plate (Flat) the box instead: its middle, its axes (unit, N across the
            // plate) and its half sizes along U, V and N.
            public Vector3[] Points;
            public bool Flat;
            public Vector3 Center, U, V, N, Half;
            // The middle of the piece, for where it met something.
            public Vector3 Middle;
        }

        // In meters at world scale 1.
        public const float Cell = 0.08f;
        public const float KeepWhole = 0.2f;
        public const int MostCells = 12;
        // A plate's thickness at least, and pieces smaller than Smallest in every direction are left out.
        public const float Thinnest = 0.006f;
        public const float Smallest = 0.004f;
        private const int Directions = 48;

        private static readonly Vector3[] Ways = BuildWays();

        // Ways spread evenly over the sphere (a Fibonacci spiral), with the six axes.
        private static Vector3[] BuildWays()
        {
            var ways = new List<Vector3> { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            var golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (var i = 0; i < Directions; i++)
            {
                var y = 1f - 2f * (i + 0.5f) / Directions;
                var r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                ways.Add(new Vector3(Mathf.Cos(golden * i) * r, y, Mathf.Sin(golden * i) * r));
            }
            return ways.ToArray();
        }

        // How many cells each side of a shape of that size is cut into.
        public static Vector3Int Cells(Vector3 size, float scale = 1f)
        {
            var cells = Vector3Int.one;
            var shortest = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            for (var axis = 0; axis < 3; axis++)
            {
                if (axis == shortest && size[axis] < KeepWhole * scale) continue;
                cells[axis] = Mathf.Clamp(Mathf.RoundToInt(size[axis] / (Cell * scale)), 1, MostCells);
            }
            return cells;
        }

        // The convex pieces of a mesh: its corner points (already in meters, in the axes the pieces are wanted in) and
        // its triangles as indices into them.
        public static List<Piece> Split(Vector3[] vertices, int[] triangles, float scale = 1f)
        {
            var pieces = new List<Piece>();
            if (vertices == null || triangles == null || vertices.Length < 3 || triangles.Length < 3) return pieces;
            var min = vertices[0];
            var max = vertices[0];
            foreach (var v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            var size = max - min;
            var cells = Cells(size, scale);
            var step = new Vector3(size.x / cells.x, size.y / cells.y, size.z / cells.z);
            var points = new List<Vector3>[cells.x * cells.y * cells.z];
            var polygon = new List<Vector3>(12);
            var clipped = new List<Vector3>(12);
            var from = new int[3];
            var to = new int[3];
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                var a = vertices[triangles[t]];
                var b = vertices[triangles[t + 1]];
                var c = vertices[triangles[t + 2]];
                for (var axis = 0; axis < 3; axis++)
                {
                    var low = Mathf.Min(a[axis], Mathf.Min(b[axis], c[axis]));
                    var high = Mathf.Max(a[axis], Mathf.Max(b[axis], c[axis]));
                    from[axis] = CellAt(low, min[axis], step[axis], cells[axis]);
                    to[axis] = CellAt(high, min[axis], step[axis], cells[axis]);
                }
                for (var x = from[0]; x <= to[0]; x++)
                    for (var y = from[1]; y <= to[1]; y++)
                        for (var z = from[2]; z <= to[2]; z++)
                        {
                            polygon.Clear();
                            polygon.Add(a);
                            polygon.Add(b);
                            polygon.Add(c);
                            // A triangle inside one cell needs no clipping.
                            if (from[0] != to[0] || from[1] != to[1] || from[2] != to[2])
                            {
                                var cell = new Vector3Int(x, y, z);
                                for (var axis = 0; axis < 3 && polygon.Count > 0; axis++)
                                {
                                    if (cells[axis] == 1) continue;
                                    var low = min[axis] + step[axis] * cell[axis];
                                    Clip(polygon, clipped, axis, low, true);
                                    Clip(clipped, polygon, axis, low + step[axis], false);
                                }
                            }
                            if (polygon.Count == 0) continue;
                            var index = (x * cells.y + y) * cells.z + z;
                            if (points[index] == null) points[index] = new List<Vector3>();
                            points[index].AddRange(polygon);
                        }
            }
            foreach (var each in points)
                if (each != null && TryPiece(each, scale, out var piece)) pieces.Add(piece);
            return pieces;
        }

        private static int CellAt(float value, float min, float step, int cells) =>
            cells == 1 || step <= 0f ? 0 : Mathf.Clamp((int)((value - min) / step), 0, cells - 1);

        // The part of a convex polygon on one side of a plane across an axis (Sutherland-Hodgman).
        private static void Clip(List<Vector3> polygon, List<Vector3> result, int axis, float bound, bool above)
        {
            result.Clear();
            var count = polygon.Count;
            for (var i = 0; i < count; i++)
            {
                var current = polygon[i];
                var next = polygon[(i + 1) % count];
                var currentIn = above ? current[axis] >= bound : current[axis] <= bound;
                var nextIn = above ? next[axis] >= bound : next[axis] <= bound;
                if (currentIn) result.Add(current);
                if (currentIn == nextIn) continue;
                var span = next[axis] - current[axis];
                if (Mathf.Abs(span) < 1e-9f) continue;
                result.Add(current + (next - current) * ((bound - current[axis]) / span));
            }
        }

        // One piece from the points of a cell: the points furthest out, as a hull, or as a thin box if they lie flat.
        internal static bool TryPiece(List<Vector3> points, float scale, out Piece piece)
        {
            piece = default;
            if (points.Count < 3) return false;
            var chosen = new List<Vector3>(Ways.Length);
            foreach (var way in Ways)
            {
                var best = points[0];
                var reach = Vector3.Dot(best, way);
                for (var i = 1; i < points.Count; i++)
                {
                    var d = Vector3.Dot(points[i], way);
                    if (d <= reach) continue;
                    reach = d;
                    best = points[i];
                }
                var known = false;
                foreach (var c in chosen)
                    if ((c - best).sqrMagnitude < 1e-10f) { known = true; break; }
                if (!known) chosen.Add(best);
            }
            var middle = Vector3.zero;
            foreach (var c in chosen) middle += c;
            middle /= chosen.Count;

            // The longest way through the points, the furthest point from that line, and how thick they are across both.
            var a = chosen[0];
            var b = a;
            foreach (var c in chosen)
                if ((c - a).sqrMagnitude > (b - a).sqrMagnitude) b = c;
            foreach (var c in chosen)
                if ((c - b).sqrMagnitude > (a - b).sqrMagnitude) a = c;
            var along = b - a;
            if (along.magnitude < Smallest * scale) return false;
            var u = along.normalized;
            var far = Vector3.zero;
            foreach (var c in chosen)
            {
                var off = c - a - u * Vector3.Dot(c - a, u);
                if (off.sqrMagnitude > far.sqrMagnitude) far = off;
            }
            var thinnest = Thinnest * scale;
            // A sliver: any way across it does.
            var v = far.magnitude >= thinnest * 0.5f ? far.normalized
                : Vector3.Cross(u, Mathf.Abs(u.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var n = Vector3.Cross(u, v);
            var low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var high = -low;
            foreach (var c in chosen)
            {
                var p = new Vector3(Vector3.Dot(c - a, u), Vector3.Dot(c - a, v), Vector3.Dot(c - a, n));
                low = Vector3.Min(low, p);
                high = Vector3.Max(high, p);
            }
            piece.Middle = middle;
            if (high.z - low.z >= thinnest && high.y - low.y >= thinnest && chosen.Count >= 4)
            {
                piece.Points = chosen.ToArray();
                return true;
            }
            var center = (low + high) * 0.5f;
            var half = (high - low) * 0.5f;
            half.y = Mathf.Max(half.y, thinnest * 0.5f);
            half.z = Mathf.Max(half.z, thinnest * 0.5f);
            piece.Flat = true;
            piece.Center = a + u * center.x + v * center.y + n * center.z;
            piece.U = u;
            piece.V = v;
            piece.N = n;
            piece.Half = half;
            return true;
        }
    }
}
