using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ShipbreakerVR.Tests
{
    public class SettingsTests
    {
        private static readonly object Lock = new object();
        private static bool bound;

        // Binds the settings once, from a file with values a player might have typed in by hand.
        internal static void Bind()
        {
            lock (Lock)
            {
                if (bound) return;
                var path = Path.Combine(Path.GetTempPath(), $"shipbreakervr-tests-{Guid.NewGuid():N}.cfg");
                File.WriteAllText(path, "[Bild]\nWeltmassstab = 7\nKopfHoehe = -40\n[Anzeige]\nVisierGroesse = 0.1\n" +
                    "[Steuerung]\nSchubTotzone = 9\nSchubVoll = 6\n");
                VrSettings.Bind(new ConfigFile(path, false));
                bound = true;
            }
        }

        public SettingsTests() => Bind();

        [Fact]
        public void OutOfRangeValuesFromTheFileAreClamped()
        {
            Assert.Equal(3f, VrSettings.WorldScale.Value);
            Assert.Equal(-VrCamera.HeadOffsetRange, VrSettings.HeadHeight.Value);
            Assert.Equal(0.3f, VrSettings.HudScale.Value);
        }

        [Fact]
        public void SlidersTakeTheirEndsFromTheSetting()
        {
            Assert.Equal((0.5f, 3f), VrSettings.Range(VrSettings.HudDistance));
            Assert.Equal((VrResolution.ResolutionScaleMin, VrResolution.HabResolution), VrSettings.Range(VrSettings.StationResolution));
            Assert.Equal((VrControls.MinThrustRange, VrControls.FullMax), VrSettings.Range(VrSettings.ThrustFull));
        }

        [Fact]
        public void ThrustRangeKeepsItsGap()
        {
            VrSettings.ThrustFull.Value = 10f;
            VrSettings.ThrustDeadzone.Value = 2f;
            Assert.Equal(0.1f, VrControls.FullOffset, 4);
            Assert.Equal(0.2f, VrControls.DeadzoneShare, 4);

            // The value just changed wins and pushes the other one along.
            VrSettings.ThrustDeadzone.Value = 8f;
            Assert.Equal(13f, VrSettings.ThrustFull.Value);
            VrSettings.ThrustFull.Value = 6f;
            Assert.Equal(1f, VrSettings.ThrustDeadzone.Value);

            VrSettings.ThrustFull.Value = 10f;
            VrSettings.ThrustDeadzone.Value = 2f;
        }

        [Fact]
        public void ListSettingsNameTheirValuesInTheFile()
        {
            var description = VrSettings.AmbientOcclusion.Description.Description;
            Assert.Contains("Values: Niedrig = Low, Aus = Off, Spiel = Game default.", description);
        }

        // Every setting with an option row has both hints, and a list setting a name in each language for each value of
        // its enum, so a value added to an enum without a name shows up here.
        [Fact]
        public void OptionTextsAreComplete()
        {
            var shown = (IDictionary)typeof(VrSettings).GetField("Shown", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert.NotEmpty(shown);
            foreach (DictionaryEntry pair in shown)
            {
                var entry = (ConfigEntryBase)pair.Key;
                string Text(string field) => (string)pair.Value.GetType().GetField(field).GetValue(pair.Value);
                string[] Names(string field) => (string[])pair.Value.GetType().GetField(field).GetValue(pair.Value);
                Assert.False(string.IsNullOrWhiteSpace(Text("HintEn")), entry.Definition.ToString());
                Assert.False(string.IsNullOrWhiteSpace(Text("HintDe")), entry.Definition.ToString());
                if (!entry.SettingType.IsEnum) continue;
                var count = Enum.GetValues(entry.SettingType).Length;
                Assert.Equal(count, Names("NamesEn")?.Length ?? 0);
                Assert.Equal(count, Names("NamesDe")?.Length ?? 0);
            }
        }
    }
}
