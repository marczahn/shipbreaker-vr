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

    public class ToolBeltTests : System.IDisposable
    {
        // The zones around the mod's own places of the tools: Raik's places, the standard since 2026-11-27, are left
        // empty while these run (the settings bound once for all tests, SettingsTests).
        private readonly System.Collections.Generic.List<(BepInEx.Configuration.ConfigEntry<string> Entry, string Value)> stored =
            new System.Collections.Generic.List<(BepInEx.Configuration.ConfigEntry<string>, string)>();

        public ToolBeltTests()
        {
            SettingsTests.Bind();
            foreach (var entry in VrToolPlace.PlaceEntries)
                if (entry != null)
                {
                    stored.Add((entry, entry.Value));
                    entry.Value = "";
                }
        }

        public void Dispose()
        {
            foreach (var (entry, value) in stored) entry.Value = value;
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.25f, 0.25f)]
        [InlineData(0.5f, 1f)]
        [InlineData(10f, 1f)]
        public void TremorCountsLittle(float angle, float share) =>
            Assert.Equal(share, VrToolSteady.Share(angle, 0.5f), 3);

        // The light smoothing of the hands: 2 mm of tremor, 0.04 s, at 90 frames a second.
        private const float Tremor = 0.002f;
        private const float Lag = 0.04f;
        private const float Frame = 1f / 90f;

        [Fact]
        public void SmoothingFollowsTremorSlowlyAndARealMovementAtOnce()
        {
            var tremor = VrToolSteady.Follow(0.0005f, Tremor, Lag, Frame);
            var edge = VrToolSteady.Follow(Tremor, Tremor, Lag, Frame);
            var moved = VrToolSteady.Follow(0.05f, Tremor, Lag, Frame);
            Assert.InRange(tremor, 0.001f, 0.03f);
            Assert.InRange(edge, 0.2f, 0.3f);
            Assert.True(moved > 0.99f);
            Assert.True(tremor < edge && edge < moved);
        }

        [Theory]
        [InlineData(0.05f)]
        [InlineData(1f)]
        [InlineData(90f)]
        public void TheAngleBetweenTwoRotationsIsToldAlsoWhenTiny(float degrees)
        {
            var a = About(0f, 1f, 30f);
            var b = a * About(1f, 0f, degrees);
            Assert.Equal(degrees, VrToolSteady.Angle(a, b), 2);
        }

        // A turn about the x or the y axis, without the engine.
        private static UnityEngine.Quaternion About(float x, float y, float degrees)
        {
            var half = degrees * UnityEngine.Mathf.Deg2Rad * 0.5f;
            var sine = UnityEngine.Mathf.Sin(half);
            return new UnityEngine.Quaternion(x * sine, y * sine, 0f, UnityEngine.Mathf.Cos(half));
        }

        [Fact]
        public void SmoothingOffFollowsAtOnce()
        {
            Assert.Equal(1f, VrToolSteady.Follow(0.001f, 0f, Lag, Frame));
            Assert.Equal(0f, VrToolSteady.Follow(0f, Tremor, Lag, Frame));
        }

        // A hand moved at a steady 1 m/s stays under a centimeter behind its controller.
        [Fact]
        public void SmoothedHandStaysCloseBehindAMovingController()
        {
            float controller = 0f, hand = 0f;
            for (var i = 0; i < 180; i++)
            {
                controller += 1f * Frame;
                hand += (controller - hand) * VrToolSteady.Follow(controller - hand, Tremor, Lag, Frame);
            }
            Assert.InRange(controller - hand, 0.002f, 0.01f);
        }

        // A hand trembling 1 mm to either side ten times a second shows less than half of it.
        [Fact]
        public void SmoothingTakesOutMostOfATremor()
        {
            float hand = 0f, most = 0f;
            for (var i = 0; i < 270; i++)
            {
                var controller = 0.001f * UnityEngine.Mathf.Sin(2f * UnityEngine.Mathf.PI * 10f * i * Frame);
                hand += (controller - hand) * VrToolSteady.Follow(UnityEngine.Mathf.Abs(controller - hand), Tremor, Lag, Frame);
                if (i >= 90) most = UnityEngine.Mathf.Max(most, UnityEngine.Mathf.Abs(hand));
            }
            Assert.True(most < 0.0005f, $"{most * 1000f:0.00} mm");
        }

        [Theory]
        [InlineData(0.1f, 0f, 0.1f)]
        [InlineData(0.03f, 40f, 0f)]
        [InlineData(0.1f, 40f, 0.06f)]
        [InlineData(0.3f, 90f, 0.25f)]
        public void TiltDoesNotCountAsThrust(float offset, float tilt, float thrust) =>
            Assert.Equal(thrust, VrControls.ThrustOffset(offset, tilt), 3);

        [Theory]
        [InlineData(0.2f, -0.6f, 0f, (int)Zone.None)]
        [InlineData(-0.2f, -0.6f, 0f, (int)Zone.Detonator)]
        [InlineData(0f, -0.6f, 0f, (int)Zone.None)]
        [InlineData(0.2f, -0.6f, 0.4f, (int)Zone.None)]
        [InlineData(0.3f, -0.6f, 0.2f, (int)Zone.None)]
        [InlineData(-0.3f, -0.6f, 0.2f, (int)Zone.None)]
        [InlineData(0.25f, -0.6f, -0.1f, (int)Zone.None)]
        [InlineData(-0.25f, -0.6f, -0.1f, (int)Zone.Detonator)]
        [InlineData(0.45f, -0.6f, -0.1f, (int)Zone.None)]
        // Behind the lower back: the demo charges, for either hand; not where a hand hangs at the side.
        [InlineData(0.2f, -0.6f, -0.55f, (int)Zone.DemoCharge)]
        [InlineData(0f, -0.6f, -0.3f, (int)Zone.DemoCharge)]
        [InlineData(-0.2f, -0.8f, -0.3f, (int)Zone.DemoCharge)]
        [InlineData(0.3f, -0.45f, -0.05f, (int)Zone.None)]
        [InlineData(0.4f, -0.6f, -0.3f, (int)Zone.None)]
        [InlineData(0.2f, -0.95f, 0f, (int)Zone.None)]
        [InlineData(0.1f, 0.1f, -0.3f, (int)Zone.Cutter)]
        [InlineData(0.15f, 0f, 0.05f, (int)Zone.Scanner)]
        [InlineData(-0.15f, 0f, 0.05f, (int)Zone.Flashlight)]
        [InlineData(0f, 0f, 0.5f, (int)Zone.None)]
        [InlineData(-0.3f, 0f, -0.3f, (int)Zone.None)]
        // The cutter's zone reaches a little lower and further in than the shoulder's bounds.
        [InlineData(0.2f, -0.18f, -0.2f, (int)Zone.Cutter)]
        [InlineData(-0.13f, 0.1f, -0.2f, (int)Zone.Cutter)]
        [InlineData(0.2f, -0.25f, -0.2f, (int)Zone.None)]
        // The zone as a number: the test method is public, the enum internal.
        public void ZoneAroundTheBody(float x, float y, float z, int zone) =>
            Assert.Equal((Zone)zone, VrToolBelt.ZoneAt(new Vector3(x, y, z)));

        [Theory]
        [InlineData(0.30f, -0.72f, -0.08f, (int)Zone.Grapple)]
        [InlineData(0.33f, -0.67f, -0.01f, (int)Zone.Grapple)]
        [InlineData(0.20f, -0.62f, -0.37f, (int)Zone.DemoCharge)]
        [InlineData(0.20f, -0.50f, 0.10f, (int)Zone.None)]
        [InlineData(-0.25f, -0.6f, -0.1f, (int)Zone.Detonator)]
        public void WithTheGrappleAtTheHipOnlyItsGripDrawsIt(float x, float y, float z, int zone) =>
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

namespace ShipbreakerVR.Tests
{
    public class ButtonDeviceTests
    {
        [Fact]
        public void TellsTheAirConditionerFromTheWallTerminal()
        {
            var device = VrButtons.Device("SM_AtmosphereRegulator_Mini_1Bx1xA_Button");
            Assert.Equal("SM_AtmosphereRegulator", device);
            Assert.False(VrButtons.OtherDevice(device, "SM_AtmosphereRegulator_Mini_1Bx1xA1_Box"));
            Assert.True(VrButtons.OtherDevice(device, "SM_Computer_WallTerminal_B0xBxA"));
        }

        [Fact]
        public void TakesOtherNamesAsBefore()
        {
            Assert.False(VrButtons.OtherDevice(VrButtons.Device("Button_Panel"), "SM_Computer_WallTerminal_B0xBxA"));
            Assert.False(VrButtons.OtherDevice("SM_AtmosphereRegulator", "PRF_Console"));
        }
    }
}
