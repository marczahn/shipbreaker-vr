using Xunit;

namespace ShipbreakerVR.Tests
{
    public class TorsoTests
    {
        [Fact]
        public void BodyKeepsItsHeadingNearWhereItHeadsFor()
        {
            Assert.Equal(0f, VrTorso.Follow(0f, 0f, VrTorso.FollowFrom - 1f, 1f), 3);
            Assert.Equal(0f, VrTorso.Follow(0f, 0f, -(VrTorso.FollowFrom - 1f), 1f), 3);
        }

        [Fact]
        public void BodyStaysWithTheHandsWhileTheHeadLooksAround()
        {
            var body = 0f;
            for (var i = 0; i < 300; i++) body = VrTorso.Follow(body, VrTorso.DeadZone - 5f, 0f, 1f / 60f);
            Assert.Equal(0f, body, 3);
        }

        [Fact]
        public void BodySettlesOntoTheHeadWithinSeconds()
        {
            var body = 30f;
            for (var i = 0; i < 300; i++) body = VrTorso.SettleOnto(body, 0f, 1f / 60f);
            Assert.InRange(body, 0f, 30f * 0.1f);
            Assert.Equal(-175f, VrTorso.SettleOnto(175f, -175f, 1000f), 2);
        }

        [Fact]
        public void BodyStaysWithinTheDeadZoneOfTheHead()
        {
            Assert.Equal(90f - VrTorso.DeadZone, VrTorso.Follow(0f, 90f, 0f, 0f), 3);
            Assert.Equal(-90f + VrTorso.DeadZone, VrTorso.Follow(0f, -90f, 0f, 0f), 3);
        }

        [Fact]
        public void BodyTurnsAfterWhereItHeadsForOverTime()
        {
            var body = 0f;
            for (var i = 0; i < 300; i++) body = VrTorso.Follow(body, 30f, 30f, 1f / 60f);
            Assert.InRange(body, 30f - VrTorso.FollowFrom - 0.5f, 30f - VrTorso.FollowFrom + 0.01f);
        }

        [Fact]
        public void BodyTurnsTheShortWayAcrossTheBack()
        {
            Assert.Equal(170f, VrTorso.Follow(170f, -170f, -170f, 0f), 3);
            Assert.Equal(-100f - VrTorso.DeadZone, VrTorso.Follow(170f, -100f, -100f, 0f), 3);
        }

        [Theory]
        [InlineData(190f, -170f)]
        [InlineData(-190f, 170f)]
        [InlineData(180f, 180f)]
        [InlineData(-180f, 180f)]
        [InlineData(720f, 0f)]
        public void AnglesWrapIntoOneTurn(float angle, float wrapped) => Assert.Equal(wrapped, VrTorso.Wrap(angle), 3);
    }
}
