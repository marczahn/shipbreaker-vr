using Xunit;

namespace ShipbreakerVR.Tests
{
    public class ComfortTests
    {
        [Fact]
        public void BrakesByTheStep()
        {
            Assert.Equal(1.5f, SimpleThrust.Slowed(2f, 1f, 0.5f), 3);
        }

        [Fact]
        public void BrakingStopsAtZero()
        {
            Assert.Equal(0f, SimpleThrust.Slowed(0.1f, 3f, 0.5f), 3);
        }
    }
}
