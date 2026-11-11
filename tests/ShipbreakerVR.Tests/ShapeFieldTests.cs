using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    public class ShapeFieldTests
    {
        // A closed cube of side 0.2 m around the origin, as triangles.
        private static List<Vector3> Cube()
        {
            var c = new List<Vector3>();
            var h = 0.1f;
            Vector3 P(int x, int y, int z) => new Vector3(x * h, y * h, z * h);
            void Quad(Vector3 a, Vector3 b, Vector3 d, Vector3 e) { c.Add(a); c.Add(b); c.Add(d); c.Add(a); c.Add(d); c.Add(e); }
            Quad(P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1));
            Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1));
            Quad(P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1));
            Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1));
            Quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1));
            Quad(P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1));
            return c;
        }

        [Fact]
        public void KnowsInsideAndOutside()
        {
            var field = new VrShapeField(Cube(), 0.01f, 0.08f, float.MinValue);
            Assert.InRange(field.SolidBounds.size.x, 0.19f, 0.23f);
            Assert.InRange(field.Distance(Vector3.zero), -0.11f, -0.085f);
            Assert.InRange(field.Distance(new Vector3(0.15f, 0f, 0f)), 0.04f, 0.06f);
            Assert.InRange(field.Distance(new Vector3(0f, 0f, 0.1f)), -0.012f, 0.012f);
            Assert.Equal(float.MaxValue, field.Distance(new Vector3(1f, 0f, 0f)));
        }

        [Fact]
        public void PointsOut()
        {
            var field = new VrShapeField(Cube(), 0.01f, 0.08f, float.MinValue);
            Assert.True(Vector3.Dot(field.Out(new Vector3(0.12f, 0.01f, 0f)), Vector3.right) > 0.9f);
            Assert.True(Vector3.Dot(field.Out(new Vector3(0f, 0.08f, 0.01f)), Vector3.up) > 0.9f);
        }

        [Fact]
        public void LeavesOutBelowTheFloor()
        {
            var field = new VrShapeField(Cube(), 0.01f, 0.08f, 0f);
            Assert.True(field.SolidBounds.min.y > -0.01f);
            Assert.True(field.Distance(new Vector3(0f, -0.05f, 0f)) > 0f);
        }
    }
}
