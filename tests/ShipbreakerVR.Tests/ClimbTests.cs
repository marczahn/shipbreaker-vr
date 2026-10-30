using UnityEngine;
using Xunit;

namespace ShipbreakerVR.Tests
{
    public class ClimbTests
    {
        private const float Step = 0.02f;
        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);

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
        public void MovesAlongWithASurfaceComingCloser()
        {
            var limited = ClimbContact.LimitApproach(Vector3.zero, new Vector3(0f, 2f, 0f), Up, 0f, Step);
            Assert.Equal(2f, limited.y, 3);
        }
    }
}
