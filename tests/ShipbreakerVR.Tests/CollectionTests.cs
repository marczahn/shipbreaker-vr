using BepInEx.Configuration;
using Xunit;

namespace ShipbreakerVR.Tests
{
    // The collection kept for each save (VrCollection): the config section of a profile and the list of kinds.
    public class CollectionTests
    {
        [Theory]
        [InlineData("Raik", "Sammlung Raik")]
        [InlineData("  Marc  ", "Sammlung Marc")]
        [InlineData("FreeMode", "Sammlung FreeMode")]
        [InlineData("a=b[c]d", "Sammlung a_b_c_d")]
        [InlineData("quo\"te's\\", "Sammlung quo_te_s_")]
        [InlineData("tab\tline\nbreak", "Sammlung tab_line_break")]
        public void AProfilesSectionKeepsItsNameWithoutWhatTheConfigCannotTake(string profile, string section)
        {
            Assert.Equal(section, VrCollection.Section(profile));
        }

        [Theory]
        [InlineData("Raik")]
        [InlineData("a=b[c]d")]
        [InlineData("quo\"te's\\")]
        [InlineData("tab\tline\nbreak")]
        [InlineData("Ünïcödé 名前")]
        [InlineData("")]
        public void BepInExTakesEveryProfilesSection(string profile)
        {
            // Throws for a section or key it cannot write.
            var definition = new ConfigDefinition(VrCollection.Section(profile), "KiKnoten");
            Assert.StartsWith("Sammlung", definition.Section);
        }

        [Fact]
        public void TwoProfilesHaveSectionsOfTheirOwn()
        {
            Assert.NotEqual(VrCollection.Section("Raik"), VrCollection.Section("Raik 2"));
        }

        [Fact]
        public void KindsAreReadTrimmedAndOnceEach()
        {
            Assert.Equal(new[] { "A", "B" }, VrCollection.Kinds(" A, B ,A,, "));
            Assert.Empty(VrCollection.Kinds(""));
            Assert.Empty(VrCollection.Kinds(null));
        }

        [Fact]
        public void BookedKindsGoToTheEndAndOnlyOnce()
        {
            Assert.Equal("A,B,C", VrCollection.WithKinds("A,B", new[] { "B", "C", "C", "A" }));
            Assert.Equal("C", VrCollection.WithKinds("", new[] { "C" }));
            Assert.Equal("A", VrCollection.WithKinds("A", new string[0]));
        }

        [Fact]
        public void TheRadiosBuiltInPlaceIsAPlace()
        {
            var parts = VrRadio.BuiltInPlace.Split(';');
            Assert.Equal(4, parts.Length);
            foreach (var part in parts)
                Assert.True(float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
        }
    }
}
