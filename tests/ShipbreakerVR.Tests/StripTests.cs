using Xunit;

namespace ShipbreakerVR.Tests
{
    // Stripping a device for its part with two hands close together (StripPull) and by blows of the fist (Punch).
    public class StripTests
    {
        [Fact]
        public void HandsCloseTogetherAreAPairFromWhereTheyCameTogether()
        {
            Assert.Equal(0.18f, StripPull.Begin(0.18f, 0.25f));
            Assert.Equal(0.25f, StripPull.Begin(0.25f, 0.25f));
        }

        [Fact]
        public void HandsTooFarApartAreNoPair()
        {
            Assert.Equal(StripPull.TooFar, StripPull.Begin(0.4f, 0.25f));
            Assert.True(StripPull.TooFar < 0f && StripPull.Unpaired < 0f && StripPull.TooFar != StripPull.Unpaired);
        }

        [Fact]
        public void PullCountsOnlyTheWayThatApplies()
        {
            // The other hand on the ship: away from the hold counts, the hands' distance does not.
            Assert.Equal(0.1f, StripPull.Far(true, 0.1f, false, 0.3f));
            // A pair without the ship: only the hands going apart counts.
            Assert.Equal(0.12f, StripPull.Far(false, 0.5f, true, 0.12f));
            // Both: whichever is more.
            Assert.Equal(0.2f, StripPull.Far(true, 0.05f, true, 0.2f));
            // Neither: nothing, also with the hand far from its hold.
            Assert.Equal(0f, StripPull.Far(false, 0.5f, false, 0.5f));
        }

        [Fact]
        public void HandsComingTogetherDoNotCount()
        {
            Assert.Equal(0f, StripPull.Far(false, 0f, true, -0.1f));
        }

        [Fact]
        public void ABlowIsFastAndIntoTheSurface()
        {
            Assert.True(Punch.Lands(2f, 1.5f, true, 1f, 0.25f));
            Assert.False(Punch.Lands(1.2f, 1.5f, true, 1f, 0.25f));
            Assert.False(Punch.Lands(2f, 1.5f, false, 1f, 0.25f));
        }

        [Fact]
        public void TheSameHandDoesNotStrikeTwiceAtOnce()
        {
            Assert.False(Punch.Lands(3f, 1.5f, true, 0.1f, 0.25f));
        }

        [Fact]
        public void ThreeBlowsTakeAThirdEach()
        {
            Assert.Equal(1f, Punch.Left(0, VrDevicePunch.Blows));
            Assert.Equal(2f / 3f, Punch.Left(1, VrDevicePunch.Blows), 4);
            Assert.Equal(1f / 3f, Punch.Left(2, VrDevicePunch.Blows), 4);
            Assert.Equal(0f, Punch.Left(3, VrDevicePunch.Blows));
            Assert.Equal(0f, Punch.Left(4, VrDevicePunch.Blows));
        }
    }
}
