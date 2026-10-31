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
        [InlineData(358f)]
        public void StandsAtTheSameHeightFromEverySpot(float height)
        {
            var placed = HabStanding.Place(new Vector3(3f, height, 4f), Up, 359.3f, 1f);
            Assert.Equal(361.2f, placed.y, 3);
            Assert.Equal(3f, placed.x, 3);
            Assert.Equal(4f, placed.z, 3);
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
