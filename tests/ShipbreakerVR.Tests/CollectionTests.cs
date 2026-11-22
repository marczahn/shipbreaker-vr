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
        [InlineData("a=b[c]d", "Sammlung a%3Db%5Bc%5Dd")]
        [InlineData("quo\"te's\\", "Sammlung quo%22te%27s%5C")]
        [InlineData("tab\tline\nbreak", "Sammlung tab%09line%0Abreak")]
        [InlineData("100%", "Sammlung 100%25")]
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

        [Theory]
        [InlineData("Raik", "Raik 2")]
        [InlineData("a=b", "a_b")]
        [InlineData("x[1]", "x_1_")]
        [InlineData("a=b", "a%3Db")]
        public void TwoProfilesHaveSectionsOfTheirOwn(string one, string other)
        {
            Assert.NotEqual(VrCollection.Section(one), VrCollection.Section(other));
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

        [Theory]
        [InlineData(VrRadio.BuiltInPlace, 4)]
        [InlineData(VrAiNode.LargeBuiltIn, 3)]
        public void TheBuiltInPlacesArePlaces(string place, int numbers)
        {
            var parts = place.Split(';');
            Assert.Equal(numbers, parts.Length);
            foreach (var part in parts)
                Assert.True(float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
        }
    }
}
