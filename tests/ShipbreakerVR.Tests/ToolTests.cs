using System.Reflection;
using UnityEngine;
using Xunit;
using Zone = ShipbreakerVR.VrToolBelt.Zone;

namespace ShipbreakerVR.Tests
{
    public class ToolFitTests
    {
        [Fact]
        public void ReadsOffsetAndTurn()
        {
            Assert.True(VrToolFit.TryParse("1.5 -2 3 / 10 20 -30", out var offset, out var angles));
            Assert.Equal(new Vector3(1.5f, -2f, 3f), offset);
            Assert.Equal(new Vector3(10f, 20f, -30f), angles);
        }

        [Fact]
        public void AcceptsDecimalCommas()
        {
            Assert.True(VrToolFit.TryParse("1,5 2 3 / 0 0 0,5", out var offset, out var angles));
            Assert.Equal(1.5f, offset.x);
            Assert.Equal(0.5f, angles.z);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("1 2 3")]
        [InlineData("1 2 / 3 4 5")]
        [InlineData("a b c / 1 2 3")]
        [InlineData("1 2 3 / 4 5 6 / 7 8 9")]
        public void RejectsMalformedText(string text) => Assert.False(VrToolFit.TryParse(text, out _, out _));

        [Fact]
        public void DefaultsParse()
        {
            foreach (var field in typeof(VrToolFit).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (field.FieldType.Name != "Fit") continue;
                var fit = field.GetValue(null);
                if (fit == null) continue;
                var text = (string)fit.GetType().GetField("Default").GetValue(fit);
                Assert.True(VrToolFit.TryParse(text, out _, out _), field.Name);
            }
        }
    }

    public class ToolBeltTests
    {
        [Theory]
        [InlineData(0.2f, -0.6f, 0f, (int)Zone.Grapple)]
        [InlineData(-0.2f, -0.6f, 0f, (int)Zone.DemoCharge)]
        [InlineData(0f, -0.6f, 0f, (int)Zone.None)]
        [InlineData(0.2f, -0.6f, 0.4f, (int)Zone.None)]
        [InlineData(0.1f, 0.1f, -0.3f, (int)Zone.Cutter)]
        [InlineData(0.15f, 0f, 0.05f, (int)Zone.Scanner)]
        [InlineData(0f, 0f, 0.5f, (int)Zone.None)]
        [InlineData(-0.3f, 0f, -0.3f, (int)Zone.None)]
        // The zone as a number: the test method is public, the enum internal.
        public void ZoneAroundTheBody(float x, float y, float z, int zone) =>
            Assert.Equal((Zone)zone, VrToolBelt.ZoneAt(new Vector3(x, y, z)));
    }

    public class ResolutionTests
    {
        // The calibration in VrResolution: 12 GB at 2688x2880 per eye gives 1.25.
        [Fact]
        public void FitsTheCalibratedCard() => Assert.Equal(1.25f, VrResolution.FitScale(12282, 2688f, 2880f), 3);

        [Fact]
        public void StaysWithinTheSettingsRange()
        {
            Assert.Equal(VrResolution.ResolutionScaleMin, VrResolution.FitScale(1024, 2688f, 2880f));
            Assert.Equal(VrResolution.ResolutionScaleMax, VrResolution.FitScale(1 << 20, 2688f, 2880f));
        }
    }
}
