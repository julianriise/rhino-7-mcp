using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Label text as the plugin reads it: Rhino's rich text (the raw DXF string,
/// bare or in Rhino's RTF wrapper) and its plain text, decoded once.
/// </summary>
public class LabelTextTests
{
    [Theory]
    // Rhino keeps a plain imported string as is, or wraps it the way
    // RtfComposer writes a style font (opennurbs_textiterator.cpp).
    [InlineData(@"B\U+00F8ttekott", "B00F8ttekott", "Bøttekott")]
    [InlineData(@"{\rtf1\deff0{\fonttbl{\f0 Arial;}}\f0 \fs20 B\U+00F8ttekott}", "B00F8ttekott", "Bøttekott")]
    [InlineData(@"M\U+00F8terom \U+00C6 \U+00E5", "M00F8terom 00C6 00E5", "Møterom Æ å")]
    // MTEXT formatting inside the string: font, height, colour, paragraph, braces.
    [InlineData(@"{\fArial|b0|i0|c0|p34;\H1.5x;\C1;Kontor\P2. etg}", "?", "Kontor 2. etg")]
    [InlineData(@"\LWC\l \~ 1", "?", "WC 1")]
    public void DxfCodes_AreDecodedFromTheRichText(string rich, string rhinoPlain, string expected)
    {
        Assert.Equal(expected, LabelText.Plain(rich, rhinoPlain));
    }

    [Theory]
    // No DXF codes: Rhino's own plain text stands, RTF accents and all.
    [InlineData(@"{\rtf1\deff0{\fonttbl{\f0 Arial;}}\f0 M\u248?terom}", "Møterom", "Møterom")]
    [InlineData("Kontorplasser", "Kontorplasser", "Kontorplasser")]
    [InlineData("", "  Hall \n", "Hall")]
    public void WithoutDxfCodes_RhinosPlainTextStands(string rich, string rhinoPlain, string expected)
    {
        Assert.Equal(expected, LabelText.Plain(rich, rhinoPlain));
    }

    [Fact]
    public void RtfEscapes_NextToADxfCode_DecodeToo()
    {
        Assert.Equal("Bøttekott æ {1}",
            LabelText.Plain(@"{\rtf1{\colortbl;\red0\green0\blue0;}B\U+00F8ttekott \u230? \{1\}\par}", "?"));
    }
}
