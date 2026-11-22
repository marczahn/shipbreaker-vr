using Xunit;

namespace ShipbreakerVR.Tests
{
    // Demo charges: which side clings (ChargeCling) and how the limit warning fades (ChargeWarning).
    public class ChargeTests
    {
        [Fact]
        public void FlatOnItsBackItClings()
        {
            Assert.True(ChargeCling.BackOn(1f, -0.03f, 35f));
            // The game's normal may point either way.
            Assert.True(ChargeCling.BackOn(-1f, -0.03f, 35f));
        }

        [Fact]
        public void WithItsFrontItDoesNot()
        {
            Assert.False(ChargeCling.BackOn(1f, 0.03f, 35f));
        }

        [Fact]
        public void OnEdgeOrTiltedTooFarItDoesNot()
        {
            Assert.False(ChargeCling.BackOn(0f, -0.01f, 35f));
            // 45 degrees.
            Assert.False(ChargeCling.BackOn(0.7071f, -0.03f, 35f));
            // 30 degrees.
            Assert.True(ChargeCling.BackOn(0.8660f, -0.03f, 35f));
        }

        [Fact]
        public void TiltIsTheAngleAgainstTheWall()
        {
            Assert.Equal(0f, ChargeCling.Tilt(1f), 3);
            Assert.Equal(60f, ChargeCling.Tilt(-0.5f), 3);
            Assert.Equal(90f, ChargeCling.Tilt(0f), 3);
        }

        [Fact]
        public void TheWarningShowsWhollyThenFadesOverItsLastHalfSecond()
        {
            Assert.Equal(1f, ChargeWarning.FadeOut(3.5f, 0.5f));
            Assert.Equal(1f, ChargeWarning.FadeOut(0.5f, 0.5f));
            Assert.Equal(0.5f, ChargeWarning.FadeOut(0.25f, 0.5f));
            Assert.Equal(0f, ChargeWarning.FadeOut(0f, 0.5f));
            Assert.Equal(0f, ChargeWarning.FadeOut(-1f, 0.5f));
        }
    }
}
