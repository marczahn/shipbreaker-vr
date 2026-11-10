using System.Collections.Generic;
using UnityEngine;

namespace ShipbreakerVR
{
    // A solid shape as a signed distance field on a grid (Raik, 2026-11-11: the box around the grapple's housing was larger
    // than the grapple, and the tool turned away from an arm that did not touch it). Built once from the shape's triangles:
    // its surface marked cell by cell, the inside filled (what the outside cannot reach), then for each cell how far it is
    // from the surface, negative inside. Distance and way out at a point are read from the grid between its cells.
    internal sealed class VrShapeField
    {
        private readonly Vector3 origin;
        private readonly float cell;
        private readonly int nx, ny, nz;
        private readonly float[] field;
        public readonly int SolidCells;
        public readonly Bounds SolidBounds;

        // The grid, for a local view of the shape.
        public Vector3 Origin => origin;
        public float Cell => cell;
        public Vector3Int Size => new Vector3Int(nx, ny, nz);
        public bool Solid(int x, int y, int z) =>
            x >= 0 && y >= 0 && z >= 0 && x < nx && y < ny && z < nz && field[(z * ny + y) * nx + x] < 0f;

        // Triangles as three points each, in one frame; cells below Floor (in that frame's y) are left out; Margin around
        // the shape is in the grid, so distances up to it are known.
        public VrShapeField(List<Vector3> corners, float cell, float margin, float floor)
        {
            this.cell = cell;
            var bounds = new Bounds(corners[0], Vector3.zero);
            foreach (var p in corners) bounds.Encapsulate(p);
            origin = bounds.min - Vector3.one * margin;
            var size = bounds.size + Vector3.one * (2f * margin);
            nx = Mathf.Clamp(Mathf.CeilToInt(size.x / cell), 2, 120);
            ny = Mathf.Clamp(Mathf.CeilToInt(size.y / cell), 2, 120);
            nz = Mathf.Clamp(Mathf.CeilToInt(size.z / cell), 2, 120);
            var count = nx * ny * nz;
            // 0 empty, 1 surface, 2 outside (reached from the grid's edge).
            var mark = new byte[count];
            var step = cell * 0.5f;
            for (var t = 0; t + 2 < corners.Count; t += 3)
            {
                var a = corners[t];
                var ab = corners[t + 1] - a;
                var ac = corners[t + 2] - a;
                var longest = Mathf.Max(ab.magnitude, ac.magnitude, (corners[t + 2] - corners[t + 1]).magnitude);
                var n = Mathf.Clamp(Mathf.CeilToInt(longest / step), 1, 400);
                for (var i = 0; i <= n; i++)
                    for (var j = 0; i + j <= n; j++)
                    {
                        var index = Index(a + ab * (i / (float)n) + ac * (j / (float)n));
                        if (index >= 0) mark[index] = 1;
                    }
            }
            // The outside: everything the grid's edge reaches without crossing the surface.
            var queue = new Queue<int>();
            for (var x = 0; x < nx; x++)
                for (var y = 0; y < ny; y++)
                    for (var z = 0; z < nz; z++)
                    {
                        if (x != 0 && y != 0 && z != 0 && x != nx - 1 && y != ny - 1 && z != nz - 1) continue;
                        var i = (z * ny + y) * nx + x;
                        if (mark[i] != 0) continue;
                        mark[i] = 2;
                        queue.Enqueue(i);
                    }
            while (queue.Count > 0)
            {
                var i = queue.Dequeue();
                var x = i % nx;
                var y = i / nx % ny;
                var z = i / (nx * ny);
                Reach(mark, queue, x - 1, y, z);
                Reach(mark, queue, x + 1, y, z);
                Reach(mark, queue, x, y - 1, z);
                Reach(mark, queue, x, y + 1, z);
                Reach(mark, queue, x, y, z - 1);
                Reach(mark, queue, x, y, z + 1);
            }
            var solid = new bool[count];
            var solidMin = Vector3.one * float.MaxValue;
            var solidMax = Vector3.one * float.MinValue;
            for (var i = 0; i < count; i++)
            {
                if (mark[i] == 2) continue;
                var center = Center(i);
                if (center.y < floor) continue;
                solid[i] = true;
                SolidCells++;
                solidMin = Vector3.Min(solidMin, center);
                solidMax = Vector3.Max(solidMax, center);
            }
            if (SolidCells > 0) SolidBounds.SetMinMax(solidMin - Vector3.one * (cell * 0.5f), solidMax + Vector3.one * (cell * 0.5f));
            // Distance to the nearest solid cell (outside) and to the nearest empty one (inside), in cells squared.
            var toSolid = Distances(solid, true);
            var toEmpty = Distances(solid, false);
            field = new float[count];
            for (var i = 0; i < count; i++)
                field[i] = solid[i] ? -(Mathf.Sqrt(toEmpty[i]) - 0.5f) * cell : (Mathf.Sqrt(toSolid[i]) - 0.5f) * cell;
        }

        private void Reach(byte[] mark, Queue<int> queue, int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz) return;
            var i = (z * ny + y) * nx + x;
            if (mark[i] != 0) return;
            mark[i] = 2;
            queue.Enqueue(i);
        }

        private int Index(Vector3 p)
        {
            var g = (p - origin) / cell;
            int x = Mathf.FloorToInt(g.x), y = Mathf.FloorToInt(g.y), z = Mathf.FloorToInt(g.z);
            if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz) return -1;
            return (z * ny + y) * nx + x;
        }

        private Vector3 Center(int i) => origin + new Vector3(i % nx + 0.5f, i / nx % ny + 0.5f, i / (nx * ny) + 0.5f) * cell;

        // Exact squared distances to the nearest cell where solid equals to, in cells (Felzenszwalb's lines, axis by axis).
        private float[] Distances(bool[] solid, bool to)
        {
            const float Far = 1e10f;
            var d = new float[solid.Length];
            for (var i = 0; i < d.Length; i++) d[i] = solid[i] == to ? 0f : Far;
            var longest = Mathf.Max(nx, Mathf.Max(ny, nz));
            var line = new float[longest];
            var result = new float[longest];
            var v = new int[longest];
            var zs = new float[longest + 1];
            for (var axis = 0; axis < 3; axis++)
            {
                var n = axis == 0 ? nx : axis == 1 ? ny : nz;
                var stride = axis == 0 ? 1 : axis == 1 ? nx : nx * ny;
                int outerA = axis == 0 ? ny : nx, outerB = axis == 2 ? ny : nz;
                for (var a = 0; a < outerA; a++)
                    for (var b = 0; b < outerB; b++)
                    {
                        var start = axis == 0 ? (b * ny + a) * nx : axis == 1 ? b * nx * ny + a : b * nx + a;
                        for (var k = 0; k < n; k++) line[k] = d[start + k * stride];
                        Line(line, result, v, zs, n);
                        for (var k = 0; k < n; k++) d[start + k * stride] = result[k];
                    }
            }
            return d;
        }

        private static void Line(float[] f, float[] d, int[] v, float[] z, int n)
        {
            var k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;
            for (var q = 1; q < n; q++)
            {
                float s;
                while (true)
                {
                    var p = v[k];
                    s = (f[q] + q * q - (f[p] + p * p)) / (2f * q - 2f * p);
                    if (s > z[k] || k == 0) break;
                    k--;
                }
                if (s <= z[k] && k == 0)
                {
                    v[0] = q;
                    z[1] = float.PositiveInfinity;
                    continue;
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }
            k = 0;
            for (var q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                var p = v[k];
                d[q] = (q - p) * (q - p) + f[p];
            }
        }

        // How far a point lies from the shape (negative inside); MaxValue outside the grid.
        public float Distance(Vector3 p)
        {
            var g = (p - origin) / cell - Vector3.one * 0.5f;
            if (g.x < 0f || g.y < 0f || g.z < 0f || g.x > nx - 1 || g.y > ny - 1 || g.z > nz - 1) return float.MaxValue;
            int x = Mathf.Min((int)g.x, nx - 2), y = Mathf.Min((int)g.y, ny - 2), z = Mathf.Min((int)g.z, nz - 2);
            float fx = g.x - x, fy = g.y - y, fz = g.z - z;
            var i = (z * ny + y) * nx + x;
            var row = nx;
            var layer = nx * ny;
            var c00 = Mathf.Lerp(field[i], field[i + 1], fx);
            var c10 = Mathf.Lerp(field[i + row], field[i + row + 1], fx);
            var c01 = Mathf.Lerp(field[i + layer], field[i + layer + 1], fx);
            var c11 = Mathf.Lerp(field[i + layer + row], field[i + layer + row + 1], fx);
            return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
        }

        // The way out of the shape at a point (unit; zero where the field is flat or unknown).
        public Vector3 Out(Vector3 p)
        {
            var h = cell;
            var way = new Vector3(Get(p + Vector3.right * h) - Get(p - Vector3.right * h),
                Get(p + Vector3.up * h) - Get(p - Vector3.up * h),
                Get(p + Vector3.forward * h) - Get(p - Vector3.forward * h));
            return way.sqrMagnitude > 1e-12f ? way.normalized : Vector3.zero;
        }

        // Outside the grid as far as its margin reaches, so the way out still points off the shape at the edge.
        private float Get(Vector3 p)
        {
            var d = Distance(p);
            return d == float.MaxValue ? Vector3.Distance(p, Vector3.Max(SolidBounds.min, Vector3.Min(SolidBounds.max, p))) : d;
        }
    }
}
