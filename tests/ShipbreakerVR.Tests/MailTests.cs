using Xunit;

namespace ShipbreakerVR.Tests
{
    public class MailTests
    {
        [Theory]
        [InlineData("KiKnoten-Abmahnung-Deutsch.txt")]
        [InlineData("KiKnoten-Abmahnung-English.txt")]
        [InlineData("Moral-Mitteilung-Deutsch.txt")]
        [InlineData("Moral-Mitteilung-English.txt")]
        public void EveryMailTextIsInTheDllWithSenderSubjectAndBody(string file)
        {
            using (var stream = typeof(VrModMail).Assembly.GetManifestResourceStream("Mails/" + file))
            {
                Assert.NotNull(stream);
                var text = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd();
                var (sender, subject, body) = VrModMail.Parse(text);
                Assert.StartsWith("LYNX", sender);
                Assert.NotEmpty(subject);
                Assert.True(body.Length > 200);
            }
        }

        [Theory]
        [InlineData("Models/Radio.obj")]
        [InlineData("Models/Radio.png")]
        public void TheRadiosModelIsInTheDll(string name)
        {
            using (var stream = typeof(VrModMail).Assembly.GetManifestResourceStream(name))
            {
                Assert.NotNull(stream);
                Assert.True(stream.Length > 100000);
            }
        }

        [Fact]
        public void ReadsSenderSubjectAndBodyFromPlainText()
        {
            var (sender, subject, body) = VrModMail.Parse("Absender: LYNX: Abteilung\r\nBetreff: Hallo\r\n\r\n\r\nErste Zeile\r\n\r\nZweite\r\n\r\n");
            Assert.Equal("LYNX: Abteilung", sender);
            Assert.Equal("Hallo", subject);
            Assert.Equal("Erste Zeile\n\nZweite", body);
        }
    }
}
