using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    // Holes in a mesh closed at run time, windows given walls, plates left alone (VrGrappleHoles.Close).
    public class GrappleHolesTests
    {
        // A mesh built of quads, each with vertices of its own (as a mesh split along UV seams), turned to face 'facing'.
        private sealed class Builder
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly List<int> Triangles = new List<int>();

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing, Vector2 uv)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), facing) < 0f) (b, d) = (d, b);
                var first = Vertices.Count;
                Vertices.AddRange(new[] { a, b, c, d });
                Uv.AddRange(new[] { uv, uv, uv, uv });
                Triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }

            public VrGrappleHoles.Patch Close() =>
                VrGrappleHoles.Close(Vertices.ToArray(), Uv.ToArray(), Triangles.ToArray(), new int[Triangles.Count / 3]);
        }

        // A box from lo to hi, without the faces named in 'missing' (+x, -x, +y, -y, +z, -z).
        private static Builder Box(Vector3 lo, Vector3 hi, params string[] missing)
        {
            var box = new Builder();
            var skip = new HashSet<string>(missing);
            Vector3 P(float x, float y, float z) => new Vector3(x, y, z);
            if (!skip.Contains("+x")) box.Quad(P(hi.x, lo.y, lo.z), P(hi.x, hi.y, lo.z), P(hi.x, hi.y, hi.z), P(hi.x, lo.y, hi.z), Vector3.right, new Vector2(0.1f, 0.1f));
            if (!skip.Contains("-x")) box.Quad(P(lo.x, lo.y, lo.z), P(lo.x, hi.y, lo.z), P(lo.x, hi.y, hi.z), P(lo.x, lo.y, hi.z), Vector3.left, new Vector2(0.2f, 0.2f));
            if (!skip.Contains("+y")) box.Quad(P(lo.x, hi.y, lo.z), P(hi.x, hi.y, lo.z), P(hi.x, hi.y, hi.z), P(lo.x, hi.y, hi.z), Vector3.up, new Vector2(0.3f, 0.3f));
            if (!skip.Contains("-y")) box.Quad(P(lo.x, lo.y, lo.z), P(hi.x, lo.y, lo.z), P(hi.x, lo.y, hi.z), P(lo.x, lo.y, hi.z), Vector3.down, new Vector2(0.4f, 0.4f));
            if (!skip.Contains("+z")) box.Quad(P(lo.x, lo.y, hi.z), P(hi.x, lo.y, hi.z), P(hi.x, hi.y, hi.z), P(lo.x, hi.y, hi.z), Vector3.forward, new Vector2(0.5f, 0.5f));
            if (!skip.Contains("-z")) box.Quad(P(lo.x, lo.y, lo.z), P(hi.x, lo.y, lo.z), P(hi.x, hi.y, lo.z), P(lo.x, hi.y, lo.z), Vector3.back, new Vector2(0.6f, 0.6f));
            return box;
        }

        [Fact]
        public void AHoleIsClosedFacingOut()
        {
            var box = Box(Vector3.zero, new Vector3(0.04f, 0.04f, 0.04f), "+x");
            var patch = box.Close();
            Assert.Equal(1, patch.Holes);
            Assert.Equal(0, patch.Windows);
            Assert.Equal(4, patch.Source.Count);
            Assert.Equal(6, patch.Triangles.Count);
            foreach (var n in patch.Normal) Assert.True(Vector3.Dot(n, Vector3.right) > 0.99f);
            // Each new triangle faces the way its normal says, as the mesh's own faces do.
            for (var t = 0; t < patch.Triangles.Count; t += 3)
            {
                Vector3 a = box.Vertices[patch.Source[patch.Triangles[t]]], b = box.Vertices[patch.Source[patch.Triangles[t + 1]]], c = box.Vertices[patch.Source[patch.Triangles[t + 2]]];
                Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), Vector3.right) > 0f);
                foreach (var corner in new[] { a, b, c }) Assert.Equal(0.04f, corner.x, 5);
            }
            // The colour of the largest face beside it (all equal here: the first one found keeps it).
            foreach (var uv in patch.Uv) Assert.NotEqual(new Vector2(0.1f, 0.1f), uv);
        }

        [Fact]
        public void ALargeHoleStaysOpen()
        {
            var patch = Box(Vector3.zero, new Vector3(0.1f, 0.1f, 0.1f), "+x").Close();
            Assert.Equal(0, patch.Holes);
            Assert.Empty(patch.Triangles);
        }

        [Fact]
        public void APlateIsLeftAlone()
        {
            var plate = new Builder();
            plate.Quad(Vector3.zero, new Vector3(0.02f, 0f, 0f), new Vector3(0.02f, 0.02f, 0f), new Vector3(0f, 0.02f, 0f), Vector3.back, Vector2.zero);
            var patch = plate.Close();
            Assert.Empty(patch.Triangles);
        }

        [Fact]
        public void AClosedBoxGetsNothing()
        {
            Assert.Empty(Box(Vector3.zero, new Vector3(0.04f, 0.04f, 0.04f)).Close().Triangles);
        }

        // A frame of two thin plates round a window, without walls inside the window: the window stays open and gets
        // its four walls, each facing into the window.
        [Fact]
        public void AWindowGetsWalls()
        {
            var frame = new Builder();
            const float o = 0.03f, i = 0.01f, d = 0.002f;
            Vector3[] Ring(float r, float z) => new[] { new Vector3(-r, -r, z), new Vector3(r, -r, z), new Vector3(r, r, z), new Vector3(-r, r, z) };
            foreach (var (z, facing) in new[] { (0f, Vector3.back), (d, Vector3.forward) })
            {
                var outer = Ring(o, z);
                var inner = Ring(i, z);
                for (var k = 0; k < 4; k++) frame.Quad(outer[k], outer[(k + 1) % 4], inner[(k + 1) % 4], inner[k], facing, new Vector2(0.7f, 0.7f));
            }
            var front = Ring(o, 0f);
            var back = Ring(o, d);
            for (var k = 0; k < 4; k++)
            {
                var mid = (front[k] + front[(k + 1) % 4]) / 2f;
                frame.Quad(front[k], front[(k + 1) % 4], back[(k + 1) % 4], back[k], new Vector3(mid.x, mid.y, 0f), new Vector2(0.8f, 0.8f));
            }
            var patch = frame.Close();
            Assert.Equal(0, patch.Holes);
            Assert.Equal(1, patch.Windows);
            Assert.Equal(8 * 3, patch.Triangles.Count);
            for (var w = 0; w < patch.Normal.Count; w += 4)
            {
                var corner = frame.Vertices[patch.Source[w]];
                var toMiddle = new Vector3(-corner.x, -corner.y, 0f);
                Assert.True(Vector3.Dot(patch.Normal[w], toMiddle) > 0f);
            }
        }

        [Fact]
        public void EarsKeepAnLShapeInside()
        {
            var points = new[]
            {
                new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 2, 0), new Vector3(0, 2, 0),
            };
            var corners = new List<int> { 0, 1, 2, 3, 4, 5 };
            var ears = VrGrappleHoles.Ears(corners, points, Vector3.forward);
            Assert.NotNull(ears);
            Assert.Equal(4 * 3, ears.Count);
            var area = 0f;
            for (var t = 0; t < ears.Count; t += 3)
                area += Vector3.Cross(points[ears[t + 1]] - points[ears[t]], points[ears[t + 2]] - points[ears[t]]).z / 2f;
            Assert.Equal(3f, area, 4);
        }
    }
}
