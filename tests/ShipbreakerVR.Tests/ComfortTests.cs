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

        [Theory]
        [InlineData(10f, false, false)]
        [InlineData(80f, false, true)]
        [InlineData(50f, false, false)]
        [InlineData(50f, true, true)]
        [InlineData(30f, true, false)]
        public void AChargeTurnsPastTheMiddleOnly(float tilt, bool wasOnEdge, bool onEdge)
        {
            Assert.Equal(onEdge, ChargeHold.OnEdge(tilt, wasOnEdge));
        }
    }
}
