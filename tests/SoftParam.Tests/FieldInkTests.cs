using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests
{
    public class FieldInkTests
    {
        [Fact]
        public void ComposerTextReadsOnItsBackground()
        {
            Assert.True(FieldInk.Contrast(FieldInk.Text, FieldInk.Background) >= 7.0);
        }

        [Fact]
        public void ComposerIsLightWithDarkText()
        {
            Assert.True(FieldInk.Background[0] > 200 && FieldInk.Text[0] < 80);
        }
    }
}
