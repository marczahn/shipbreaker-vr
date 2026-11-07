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
        public void APushGrowsWithTheHandsWay()
        {
            Assert.Equal(125f, BracedPush.Force(new UnityEngine.Vector3(0.05f, 0f, 0f), 30f).x, 2);
        }

        [Fact]
        public void APushStopsAtItsLimit()
        {
            Assert.Equal(250f, BracedPush.Force(new UnityEngine.Vector3(0f, 0.5f, 0f), 30f).y, 2);
        }

        [Theory]
        [InlineData(200f, 0.35f)]
        [InlineData(600f, 0.26f)]
        [InlineData(1000f, 0.17f)]
        public void HeavyPartsGatherSpeedSlowly(float mass, float acceleration)
        {
            Assert.Equal(acceleration, BracedPush.Force(new UnityEngine.Vector3(0f, 0.5f, 0f), mass).y / mass, 3);
        }

        [Fact]
        public void TheHoistTruckGetsAQuarter()
        {
            Assert.Equal(0.25f, BracedPush.Limit(635.6f) / 635.6f, 2);
        }

        [Fact]
        public void TheVeryHeaviestGetTheLimit()
        {
            Assert.Equal(250f, BracedPush.Limit(5000f), 2);
        }

        [Fact]
        public void LighterPartsGetMoreForce()
        {
            Assert.True(BracedPush.Limit(100f) > BracedPush.Limit(200f));
            Assert.True(BracedPush.Limit(100f) < BracedPush.Limit(30f));
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

        [Fact]
        public void ShooterThrustNeedsTheStickOutOfItsDeadzone()
        {
            Assert.Equal(UnityEngine.Vector3.zero, VrControls.ShooterThrust(new UnityEngine.Vector2(0f, 0.1f), UnityEngine.Quaternion.identity));
        }

        [Fact]
        public void ShooterThrustGoesWhereTheControllerPoints()
        {
            var forward = VrControls.ShooterThrust(new UnityEngine.Vector2(0f, 1f), UnityEngine.Quaternion.identity);
            Assert.Equal(1f, forward.z, 3);
            var right = VrControls.ShooterThrust(new UnityEngine.Vector2(1f, 0f), UnityEngine.Quaternion.identity);
            Assert.Equal(1f, right.x, 3);
            var back = VrControls.ShooterThrust(new UnityEngine.Vector2(0f, -1f), UnityEngine.Quaternion.identity);
            Assert.Equal(-1f, back.z, 3);
        }

        [Fact]
        public void ShooterThrustFollowsTheControllersTilt()
        {
            // The controller pitched 90 degrees down (about its right axis): the stick's forward thrusts down.
            var half = (float)System.Math.Sqrt(0.5);
            var down = VrControls.ShooterThrust(new UnityEngine.Vector2(0f, 1f), new UnityEngine.Quaternion(half, 0f, 0f, half));
            Assert.Equal(-1f, down.y, 3);
            Assert.Equal(0f, down.z, 3);
        }

        [Fact]
        public void ShooterRollGoesTheSticksWay()
        {
            Assert.Equal(0f, VrControls.ShooterRoll(0.1f), 3);
            Assert.True(VrControls.ShooterRoll(-1f) > 0f);
            Assert.True(VrControls.ShooterRoll(1f) < 0f);
            Assert.Equal(100f, VrControls.ShooterRoll(-1f), 2);
        }

        [Fact]
        public void ShooterThrustGrowsWithTheStick()
        {
            var half = VrControls.ShooterThrust(new UnityEngine.Vector2(0f, 0.575f), UnityEngine.Quaternion.identity);
            Assert.Equal(0.5f, half.magnitude, 2);
        }
    }
}
