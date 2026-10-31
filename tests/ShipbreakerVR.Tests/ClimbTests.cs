using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    public class ClimbTests
    {
        private const float Step = 0.02f;
        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

        [Theory]
        [InlineData(361.2f)]
        [InlineData(361.11f)]
        public void StandsAtTheSameHeightFromEverySpot(float height)
        {
            var placed = HabStanding.Place(new Vector3(3f, height, 4f), Up, 359.3f, 1f);
            Assert.Equal(361.2f, placed.y, 3);
            Assert.Equal(3f, placed.x, 3);
            Assert.Equal(4f, placed.z, 3);
        }

        [Fact]
        public void FrameSitsWhereTheLineToItsSpotCrossesTheCanvas()
        {
            Assert.True(HabStanding.Across(new Vector3(0f, 0f, -2f), new Vector3(1f, 2f, 2f), out var place));
            Assert.Equal(0.5f, place.x, 3);
            Assert.Equal(1f, place.y, 3);
        }

        [Fact]
        public void FrameBeyondTheCanvasStaysOnIt()
        {
            var seen = HabStanding.Inside(new Vector2(0.9f, -0.1f), 0.2f, 0.3f);
            Assert.Equal(0.4f, seen.x, 3);
            Assert.Equal(-0.1f, seen.y, 3);
        }

        [Fact]
        public void FrameForASpotBehindTheEyeGoesToItsSide()
        {
            Assert.True(HabStanding.Across(new Vector3(0f, 0f, -2f), new Vector3(-1f, 2f, -3f), out var place));
            Assert.Equal(-1f, place.x, 3);
        }

        [Fact]
        public void WalksFreelyInsideTheFloor()
        {
            var to = new Vector2(131f, 116f);
            Assert.Equal(to, HabFloor.Limit(new Vector2(131f, 115f), to));
        }

        [Fact]
        public void SlidesAlongAnEdge()
        {
            var limited = HabFloor.Limit(new Vector2(132.3f, 114.7f), new Vector2(133f, 115f));
            Assert.Equal(132.3f, limited.x, 3);
            Assert.Equal(115f, limited.y, 3);
        }

        [Fact]
        public void TheFloorsEdgeDoesNotCrossItself()
        {
            var edge = HabFloor.Outline;
            for (var i = 0; i < edge.Length; i++)
                for (var j = i + 2; j < edge.Length; j++)
                {
                    if (i == 0 && j == edge.Length - 1) continue;
                    Assert.False(Crosses(edge[i], edge[i + 1], edge[j], edge[(j + 1) % edge.Length]), $"edges {i} and {j}");
                }
        }

        private static bool Crosses(Vector2 a, Vector2 b, Vector2 c, Vector2 d) =>
            Side(a, b, c) * Side(a, b, d) < 0f && Side(c, d, a) * Side(c, d, b) < 0f;

        private static float Side(Vector2 a, Vector2 b, Vector2 point) =>
            (b.x - a.x) * (point.y - a.y) - (b.y - a.y) * (point.x - a.x);

        [Theory]
        [InlineData(130.6f, 113.7f)]
        [InlineData(131.11f, 114.61f)]
        [InlineData(131.07f, 116.67f)]
        [InlineData(131.86f, 119.17f)]
        [InlineData(131.63f, 120.17f)]
        [InlineData(131.36f, 120.85f)]
        public void CanWalkWhereTheGameStands(float x, float z)
        {
            Assert.True(HabFloor.Inside(new Vector2(x, z)));
        }

        [Theory]
        [InlineData(133.4f, 121.2f)]
        [InlineData(130f, 111f)]
        [InlineData(129f, 114f)]
        public void CannotWalkIntoFurniture(float x, float z)
        {
            Assert.False(HabFloor.Inside(new Vector2(x, z)));
        }

        [Fact]
        public void WalksFromTheAisleToTheWorkArea()
        {
            Assert.True(HabFloor.Inside(new Vector2(131f, 118.5f)));
            Assert.True(HabFloor.Inside(new Vector2(132.5f, 120f)));
        }

        [Fact]
        public void StopsInACorner()
        {
            var from = new Vector2(130.6f, 113.7f);
            Assert.Equal(from, HabFloor.Limit(from, new Vector2(120f, 100f)));
        }

        [Fact]
        public void SittingOnTheBedKeepsTheSpot()
        {
            var placed = HabStanding.Place(new Vector3(3f, 1.28f, 4f), Up, 0f, 1f);
            Assert.Equal(1.28f, placed.y, 3);
        }

        [Fact]
        public void StandsOnATiltedFloorAlongItsUp()
        {
            var up = new Vector3(0f, 0.99756f, 0.06976f);
            var placed = HabStanding.Place(new Vector3(1f, 2f, 3f), up, 0f, 1f);
            Assert.Equal(HabStanding.Height, Vector3.Dot(placed, up), 3);
        }

        [Fact]
        public void StopsOnTheSpotWithNoRoomLeft()
        {
            var limited = ClimbContact.LimitApproach(new Vector3(0f, -5f, 0f), Vector3.zero, Up, 0f, Step);
            Assert.Equal(0f, limited.y, 3);
        }

        [Fact]
        public void TreatsHavingPassedTheClearanceAsNoRoom()
        {
            var limited = ClimbContact.LimitApproach(new Vector3(0f, -5f, 0f), Vector3.zero, Up, -0.01f, Step);
            Assert.Equal(0f, limited.y, 3);
        }

        [Fact]
        public void SlowsToWhatFitsInTheStep()
        {
            var limited = ClimbContact.LimitApproach(new Vector3(0f, -5f, 0f), Vector3.zero, Up, 0.02f, Step);
            Assert.Equal(-1f, limited.y, 3);
        }

        [Fact]
        public void KeepsASpeedThatFits()
        {
            var velocity = new Vector3(0f, -0.5f, 0f);
            Assert.Equal(velocity, ClimbContact.LimitApproach(velocity, Vector3.zero, Up, 0.02f, Step));
        }

        [Fact]
        public void KeepsSlidingAlongTheSurface()
        {
            var limited = ClimbContact.LimitApproach(new Vector3(3f, -5f, 0f), Vector3.zero, Up, 0f, Step);
            Assert.Equal(3f, limited.x, 3);
            Assert.Equal(0f, limited.y, 3);
        }

        [Fact]
        public void KeepsMovingAway()
        {
            var velocity = new Vector3(0f, 2f, 0f);
            Assert.Equal(velocity, ClimbContact.LimitApproach(velocity, Vector3.zero, Up, 0f, Step));
        }

        [Fact]
        public void AHandFarFromTheHeadStaysWhereItIs()
        {
            var hand = new Vector3(0f, 0f, 0.5f);
            Assert.Equal(hand, ClimbContact.Closer(hand, Vector3.zero));
        }

        [Fact]
        public void AHandNearTheHeadCountsAsCloser()
        {
            Assert.Equal(0.15f, ClimbContact.Closer(new Vector3(0f, 0f, 0.3f), Vector3.zero).z, 3);
        }

        [Fact]
        public void APulledInHandCountsAsAtTheHead()
        {
            var head = new Vector3(0f, 1.6f, 0f);
            Assert.Equal(head, ClimbContact.Closer(new Vector3(0f, 1.6f, 0.2f), head));
        }

        [Fact]
        public void HeavierPartsLagBehindMore()
        {
            Assert.Equal(0.5f, ClimbContact.Follow(0f, 30f, 0.5f, 0.08f), 3);
            Assert.Equal(0.29f, ClimbContact.Follow(15f, 30f, 0.5f, 0.08f), 3);
            Assert.Equal(0.08f, ClimbContact.Follow(30f, 30f, 0.5f, 0.08f), 3);
        }

        [Fact]
        public void MovesAlongWithASurfaceComingCloser()
        {
            var limited = ClimbContact.LimitApproach(Vector3.zero, new Vector3(0f, 2f, 0f), Up, 0f, Step);
            Assert.Equal(2f, limited.y, 3);
        }
    }
}
