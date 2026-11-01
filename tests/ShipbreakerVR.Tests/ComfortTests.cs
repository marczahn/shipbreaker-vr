using Xunit;

namespace ShipbreakerVR.Tests
{
    public class ComfortTests
    {
        [Theory]
        [InlineData(0f, 0f, 1f, 0f)]
        [InlineData(0f, 1f, 0f, 90f)]
        [InlineData(0f, -1f, 0f, -90f)]
        public void GazeIsMeasuredFromTheHorizon(float x, float y, float z, float pitch)
        {
            Assert.Equal(pitch, FlightGaze.Pitch(new UnityEngine.Vector3(x, y, z)), 2);
        }

        [Fact]
        public void APortraitKeepsItsColorsInsideTheCard()
        {
            var card = PortraitCard.Cut(new[] { new UnityEngine.Color32(10, 20, 30, 0) }, new[] { new UnityEngine.Color32(200, 5, 0, 255) });
            Assert.Equal(10, card[0].r);
            Assert.Equal(30, card[0].b);
            Assert.Equal(200, card[0].a);
        }

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
