using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    // The line from the hip to a tool (ToolLine) and the pieces a tool's model is cut into (ToolPieces).
    public class ToolLineTests
    {
        private static ToolLine Stepped(Vector3 from, Vector3 to, float length, int steps)
        {
            var line = new ToolLine(26);
            line.Lay(from, to, Vector3.down * 0.02f);
            for (var i = 0; i < steps; i++) line.Step(from, to, length, Vector3.zero, Vector3.zero, 0.98f, 0.1f, 14);
            return line;
        }

        [Fact]
        public void LineHeldInAHandKeepsItsLengthToTheTool()
        {
            // 2 m from the hip to the tool, 3 m of line out, a hand takes it 0.5 m beside the hip.
            var line = new ToolLine(21);
            var hip = Vector3.zero;
            var tool = new Vector3(2f, 0f, 0f);
            line.Lay(hip, tool, new Vector3(0f, -0.02f, 0f));
            for (var i = 0; i < 200; i++) line.Step(hip, tool, 3f, Vector3.zero, Vector3.zero, 0.98f, 0.1f, 14);
            var hand = line.At[5] + new Vector3(0f, 0.03f, 0f);
            Assert.True(line.Nearest(hand, out var along) < 0.05f);
            Assert.InRange(along, 0.7f, 0.8f);
            var held = line.PointAt(along);
            Assert.Equal(5, held);
            var rest = line.Length() - along;
            // The hand goes 0.3 m away from the tool; the tool's end follows it by as much, as the pull would bring it.
            var pulledHand = hand + new Vector3(-0.3f, 0f, 0f);
            var straight = (tool - pulledHand).magnitude;
            var far = System.Math.Max(rest, straight);
            // As the hip reels it: a quarter more than the way to the hand, and a little spare.
            var near = (pulledHand - hip).magnitude * 1.25f + 0.12f;
            for (var i = 0; i < 200; i++) line.Step(hip, tool, far, held, pulledHand, near, Vector3.zero, Vector3.zero, 0.98f, 0.1f, 14);
            Assert.True((line.At[held] - pulledHand).magnitude < 1e-4f, "the held point is not in the hand");
            var beyond = 0f;
            for (var i = held; i + 1 < line.At.Length; i++) beyond += (line.At[i + 1] - line.At[i]).magnitude;
            Assert.InRange(beyond, far - 0.03f, far + 0.03f);
            var before = 0f;
            for (var i = 0; i < held; i++) before += (line.At[i + 1] - line.At[i]).magnitude;
            Assert.InRange(before, near - 0.03f, near + 0.03f);
        }

        [Fact]
        public void HandOverHandTheChainDoesNotRunOutOfPoints()
        {
            // 4 m to the tool, the line straight. The hands take it in turn, each beyond the other, six times over.
            var line = new ToolLine(26);
            var hip = Vector3.zero;
            var tool = new Vector3(4f, 0f, 0f);
            line.Lay(hip, tool, Vector3.zero);
            var innerAlong = 0.4f;
            var innerPoint = line.PointAt(innerAlong);
            line.Spread(innerPoint, innerAlong, -1, 0f);
            for (var turn = 0; turn < 6; turn++)
            {
                var outerAlong = innerAlong + 0.5f;
                var first = System.Math.Min(System.Math.Max(line.PointAt(innerAlong), 1), 23);
                var second = System.Math.Min(System.Math.Max(line.PointAt(outerAlong), first + 1), 24);
                line.Spread(first, innerAlong, second, outerAlong);
                Assert.InRange(line.Along(first), innerAlong - 0.01f, innerAlong + 0.01f);
                Assert.InRange(line.Along(second), outerAlong - 0.01f, outerAlong + 0.01f);
                Assert.InRange(line.Length(), 3.99f, 4.01f);
                Assert.True(second < 24, $"turn {turn}: the far hand is at the chain's last point");
                // The near hand lets go; the far one is the near one of the next turn.
                innerAlong = outerAlong;
            }
        }

        [Fact]
        public void TwoHandsHoldTheLineEachStretchKeepsItsLength()
        {
            var line = new ToolLine(26);
            var hip = Vector3.zero;
            var tool = new Vector3(3f, 0f, 0f);
            line.Lay(hip, tool, new Vector3(0f, -0.02f, 0f));
            line.Spread(4, 0.5f, 9, 1.1f);
            var left = new ToolLine.Hold { Point = 4, At = new Vector3(0.4f, 0.2f, 0f), Length = 0.6f };
            var right = new ToolLine.Hold { Point = 9, At = new Vector3(0.9f, 0.2f, 0f), Length = 0.7f };
            var far = 2.3f;
            for (var i = 0; i < 300; i++) line.Step(hip, tool, far, left, right, Vector3.zero, Vector3.zero, 0.95f, 0.1f, 14);
            Assert.True((line.At[4] - left.At).magnitude < 1e-4f);
            Assert.True((line.At[9] - right.At).magnitude < 1e-4f);
            Assert.InRange(line.Along(4), 0.57f, 0.63f);
            Assert.InRange(line.Along(9) - line.Along(4), 0.67f, 0.73f);
            Assert.InRange(line.Length() - line.Along(9), far - 0.04f, far + 0.04f);
        }

        [Fact]
        public void TautLineIsStraight()
        {
            var to = new Vector3(3f, 0f, 0f);
            var line = Stepped(Vector3.zero, to, 3f, 120);
            Assert.InRange(line.Length(), 2.99f, 3.02f);
            foreach (var point in line.At) Assert.InRange(Mathf.Abs(point.y) + Mathf.Abs(point.z), 0f, 0.02f);
        }

        [Fact]
        public void SlackLineKeepsItsLengthAndBows()
        {
            var to = new Vector3(1f, 0f, 0f);
            var line = Stepped(Vector3.zero, to, 2f, 200);
            Assert.InRange(line.Length(), 1.9f, 2.1f);
            var furthest = 0f;
            foreach (var point in line.At) furthest = Mathf.Max(furthest, Mathf.Abs(point.y) + Mathf.Abs(point.z));
            Assert.True(furthest > 0.3f, $"a line twice as long as the way bows out, {furthest:0.00} m here");
            Assert.Equal(Vector3.zero, line.At[0]);
            Assert.Equal(to, line.At[line.At.Length - 1]);
        }

        [Fact]
        public void LineGoesAlongWithThePlayer()
        {
            // The ends and the surroundings move 5 cm a step: the line must not trail behind.
            var line = new ToolLine(26);
            var from = Vector3.zero;
            var to = new Vector3(1f, 0f, 0f);
            line.Lay(from, to, Vector3.down * 0.02f);
            var way = new Vector3(0f, 0f, 0.05f);
            for (var i = 0; i < 100; i++)
            {
                from += way;
                to += way;
                line.Step(from, to, 1.3f, i == 0 ? Vector3.zero : way, way, 0.98f, 0.1f, 14);
            }
            var middle = line.At[13];
            Assert.InRange(middle.z - from.z, -0.05f, 0.05f);
        }

        [Fact]
        public void HipReelsWithTheToolInTheHand()
        {
            // Paid out at once to the way plus slack, wound in slowly, never past the line's length.
            Assert.Equal(0.5f * 1.25f + 0.12f, ToolLine.PaidOut(0.2f, 0.5f, 5f, true, 0.25f, 0.12f, 1.2f, 0.011f), 4);
            Assert.Equal(3f - 0.012f, ToolLine.PaidOut(3f, 0.5f, 5f, true, 0.25f, 0.12f, 1.2f, 0.01f), 4);
            Assert.Equal(5f, ToolLine.PaidOut(1f, 4.8f, 5f, true, 0.25f, 0.12f, 1.2f, 0.011f), 4);
        }

        [Fact]
        public void LineOfAToolAfloatStaysOut()
        {
            Assert.Equal(3f, ToolLine.PaidOut(3f, 0.5f, 5f, false, 0.25f, 0.12f, 1.2f, 0.011f), 4);
            Assert.Equal(4f, ToolLine.PaidOut(3f, 4f, 5f, false, 0.25f, 0.12f, 1.2f, 0.011f), 4);
            Assert.Equal(5f, ToolLine.PaidOut(3f, 6f, 5f, false, 0.25f, 0.12f, 1.2f, 0.011f), 4);
        }

        [Fact]
        public void TautLineStopsToolAndBodyMovingApart()
        {
            // Slack, or already coming back: nothing.
            Assert.Equal(0f, ToolLine.Taut(1f, -0.1f, 4f, 1f, 0.2f, 0.01f));
            Assert.Equal(0f, ToolLine.Taut(-2f, 0.05f, 4f, 1f, 0.2f, 0.01f));
            // A 5 kg tool leaving a 100 kg body at 2 m/s, the line just at its end: the impulse takes all of the
            // 2 m/s between them, and the tool nearly all of the change.
            var impulse = ToolLine.Taut(2f, 0.0001f, 4f, 1f, 1f / 5f, 1f / 100f);
            var tool = impulse / 5f;
            var body = impulse / 100f;
            Assert.InRange(tool + body, 1.99f, 2.01f);
            Assert.InRange(tool, 1.89f, 1.92f);
            // A body that holds on does not give.
            Assert.InRange(ToolLine.Taut(2f, 0.0001f, 4f, 1f, 1f / 5f, 0f) / 5f, 1.99f, 2.01f);
        }

        // A box of that size around the origin, as corner points and triangles.
        private static void Box(Vector3 half, List<Vector3> vertices, List<int> triangles, Vector3 at)
        {
            var start = vertices.Count;
            for (var i = 0; i < 8; i++)
                vertices.Add(at + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z));
            int[] quads = { 0, 1, 3, 2, 4, 6, 7, 5, 0, 4, 5, 1, 2, 3, 7, 6, 0, 2, 6, 4, 1, 5, 7, 3 };
            for (var q = 0; q < quads.Length; q += 4)
            {
                triangles.Add(start + quads[q]); triangles.Add(start + quads[q + 1]); triangles.Add(start + quads[q + 2]);
                triangles.Add(start + quads[q]); triangles.Add(start + quads[q + 2]); triangles.Add(start + quads[q + 3]);
            }
        }

        [Fact]
        public void LongToolIsCutAlongItsLongSides()
        {
            // The cutter's size: 16 x 34 x 93 cm. Its short side stays whole.
            var cells = ToolPieces.Cells(new Vector3(0.16f, 0.34f, 0.93f));
            Assert.Equal(1, cells.x);
            Assert.Equal(4, cells.y);
            Assert.Equal(12, cells.z);
        }

        [Fact]
        public void PiecesFollowAnLShape()
        {
            // An L of two bars, each 10 cm thick: 40 cm along x at the bottom, 40 cm up at the left. The box around it
            // is 40 x 40 cm; the pieces must leave its empty upper right corner empty.
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            Box(new Vector3(0.2f, 0.05f, 0.05f), vertices, triangles, new Vector3(0.2f, 0.05f, 0f));
            Box(new Vector3(0.05f, 0.2f, 0.05f), vertices, triangles, new Vector3(0.05f, 0.2f, 0f));
            var pieces = ToolPieces.Split(vertices.ToArray(), triangles.ToArray());
            Assert.True(pieces.Count >= 5, $"{pieces.Count} piece(s)");
            foreach (var piece in pieces)
            {
                var points = piece.Flat ? new[] { piece.Center + piece.U * piece.Half.x + piece.V * piece.Half.y, piece.Center - piece.U * piece.Half.x - piece.V * piece.Half.y } : piece.Points;
                foreach (var p in points)
                    Assert.False(p.x > 0.11f && p.y > 0.11f, $"a piece reaches into the empty corner at {p}");
                // No piece is larger than a cell and a bit.
                Assert.InRange((piece.Flat ? piece.Half.magnitude * 2f : Extent(piece.Points)), 0f, 0.2f);
            }
        }

        private static float Extent(Vector3[] points)
        {
            var min = points[0];
            var max = points[0];
            foreach (var p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return (max - min).magnitude;
        }

        [Fact]
        public void FlatPatchBecomesAPlate()
        {
            // A square of two triangles has no volume for a hull.
            var points = new List<Vector3> { new Vector3(0f, 0f, 0f), new Vector3(0.05f, 0f, 0f), new Vector3(0.05f, 0.05f, 0f), new Vector3(0f, 0.05f, 0f) };
            Assert.True(ToolPieces.TryPiece(points, 1f, out var piece));
            Assert.True(piece.Flat);
            Assert.InRange(piece.Half.z, 0.0029f, 0.0031f);
            Assert.InRange(Mathf.Abs(piece.N.z), 0.99f, 1.01f);
            Assert.InRange((piece.Center - new Vector3(0.025f, 0.025f, 0f)).magnitude, 0f, 0.001f);
        }

        [Fact]
        public void SolidPatchBecomesAHull()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            Box(new Vector3(0.03f, 0.03f, 0.03f), vertices, triangles, Vector3.zero);
            var pieces = ToolPieces.Split(vertices.ToArray(), triangles.ToArray());
            Assert.Single(pieces);
            Assert.False(pieces[0].Flat);
            Assert.Equal(8, pieces[0].Points.Length);
        }
    }
}
