using System;
using System.IO;
using System.Linq;
using System.Text;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// DXF text as dxf_import reads it from the source file: TEXT escapes, MTEXT
/// formatting, the pre-2007 code page, and which imported text each belongs to.
/// </summary>
public class DxfTextTests
{
    static DxfTextTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    static readonly string OfficePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "office_2D.dxf");

    [Fact]
    public void Office_SixteenLabels_TheEscapedOneDecoded()
    {
        var texts = DxfText.Read(OfficePath);
        Assert.Equal(16, texts.Count(t => t.Layer == "label"));
        var bottekott = Assert.Single(texts, t => t.Raw.Contains("\\U+"));
        Assert.Equal("B\\U+00F8ttekott", bottekott.Raw);
        Assert.Equal("Bøttekott", bottekott.Text);
        Assert.False(string.IsNullOrEmpty(bottekott.Handle));
        // The other fifteen have no codes: decoded is written.
        Assert.All(texts.Where(t => t != bottekott), t => Assert.Equal(t.Raw.Trim(), t.Text));
    }

    [Theory]
    [InlineData(@"B\U+00F8ttekott", "Bøttekott")]
    [InlineData(@"\U+00C6RE \U+00E5", "ÆRE å")]
    [InlineData("%%c100 %%d %%p5 50%%%", "⌀100 ° ±5 50%")]
    [InlineData(@"%%uWC%%u", "WC")]
    [InlineData(@"Rom\P1", @"Rom\P1")]  // TEXT has no MTEXT codes: the backslash is text
    public void Text_EscapesAndPercentCodes(string raw, string expected)
    {
        Assert.Equal(expected, DxfText.DecodeText(raw, 1252));
    }

    [Theory]
    [InlineData(@"{\fArial|b0|i0|c0|p34;\H1.5x;\C1;Kontor\P2. etg}", "Kontor 2. etg")]
    [InlineData(@"\LWC\l\~1", "WC 1")]
    [InlineData(@"Trapp 1\S1^2;", "Trapp 11/2")]
    [InlineData(@"M\U+00F8te\{rom\}", "Møte{rom}")]
    [InlineData(@"\pxqc;Lager", "Lager")]
    public void MText_FormattingDropped(string raw, string expected)
    {
        Assert.Equal(expected, DxfText.DecodeMText(raw, 1252));
    }

    [Fact]
    public void Text_MbcsEscape_UsesItsCodePage()
    {
        // \M+1 is Shift-JIS: 0x82A0 is あ.
        Assert.Equal("あ", DxfText.DecodeText(@"\M+182A0", 1252));
    }

    static byte[] Dxf(string version, string codepage, string entities, Encoding encoding) =>
        encoding.GetBytes(
            "0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\n" + version + "\n9\n$DWGCODEPAGE\n3\n" + codepage
            + "\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n" + entities + "0\nENDSEC\n0\nEOF\n");

    [Fact]
    public void Before2007_StringsAreInTheDwgCodePage()
    {
        var bytes = Dxf("AC1015", "ANSI_1252",
            "0\nTEXT\n5\n2A\n8\nlabel\n10\n100.0\n20\n200.0\n1\nMøterom\n"
            + "0\nMTEXT\n5\n2B\n8\nlabel\n10\n0.0\n20\n0.0\n3\n{\\fArial|b0;Kjø\n1\nkken}\n",
            Encoding.GetEncoding(1252));
        var texts = DxfText.Read(bytes);
        Assert.Equal(new[] { "Møterom", "Kjøkken" }, texts.Select(t => t.Text).ToArray());
        Assert.Equal(new[] { "2A", "2B" }, texts.Select(t => t.Handle).ToArray());
        Assert.Equal(100.0, texts[0].X);
        Assert.Equal(200.0, texts[0].Y);
    }

    [Fact]
    public void From2007_StringsAreUtf8()
    {
        var bytes = Dxf("AC1032", "ANSI_1252", "0\nTEXT\n8\nlabel\n10\n0\n20\n0\n1\nMøterom\n", Encoding.UTF8);
        Assert.Equal("Møterom", Assert.Single(DxfText.Read(bytes)).Text);
    }

    static DxfText.Entity Source(string layer, double x, double y, string text) =>
        new DxfText.Entity { Kind = "TEXT", Layer = layer, X = x, Y = y, Text = text, Raw = text };

    [Fact]
    public void Rewrite_ByLayerAndPosition_ElseRhinosText()
    {
        var source = new[]
        {
            Source("label", 0, 0, "Bøttekott"),
            Source("label", 5000, 0, "WC"),
            Source("furniture", 0, 0, "Bord"),
        };
        var placed = new[]
        {
            new DxfText.Placed("furniture", 0.4, 0, "Bord"),
            new DxfText.Placed("LABEL", 0.2, 0.1, "B00F8ttekott"),
            new DxfText.Placed("label", 9000, 0, "Rom"),          // no source near: unchanged
        };
        var rewritten = DxfText.Rewrite(source, placed, 1.0);
        Assert.Equal(new[] { 2, 0, -1 }, rewritten.Select(r => r.Source).ToArray());
        Assert.Equal(new[] { "Bord", "Bøttekott", "Rom" }, rewritten.Select(r => r.Text).ToArray());
        // The unmatched one says how far off it is.
        Assert.Equal(4000, DxfText.Nearest(source, placed[2]), 6);
        Assert.Equal(-1, DxfText.Nearest(source, new DxfText.Placed("door", 0, 0, "D1")));
    }

    [Fact]
    public void Rewrite_TwoEquallyNearWithDifferentText_IsNotGuessed()
    {
        var source = new[] { Source("label", -0.5, 0, "WC"), Source("label", 0.5, 0, "Bad") };
        var rewritten = DxfText.Rewrite(source, new[] { new DxfText.Placed("label", 0, 0, "W0043") }, 1.0);
        Assert.Equal(-1, rewritten[0].Source);
        Assert.Equal("W0043", rewritten[0].Text);
    }

    [Fact]
    public void Rewrite_JustifiedTextMatchesItsAlignmentPoint()
    {
        var e = Source("label", 0, 0, "Hall");
        e.Aligned = true;
        e.AlignX = 400;
        e.AlignY = 50;
        var rewritten = DxfText.Rewrite(new[] { e }, new[] { new DxfText.Placed("label", 400, 50, "Hall") }, 1.0);
        Assert.Equal(0, rewritten[0].Source);
    }

    [Theory]
    [InlineData("B00F8ttekott", true)]
    [InlineData("M00F8terom", true)]
    [InlineData("Bøttekott", false)]
    [InlineData("Rom 0101", false)]
    [InlineData("Kontor 2", false)]
    public void LooksMangled_OnlyAHexLetterRemnantNextToALetter(string text, bool expected)
    {
        Assert.Equal(expected, DxfText.LooksMangled(text));
    }

    [Theory]
    [InlineData("B00F8ttekott", "Bøttekott")]
    [InlineData("M00F8terom", "Møterom")]
    [InlineData("Bøttekott", "Bøttekott")]
    [InlineData("Rom 0101", "Rom 0101")]
    [InlineData("Kontor", "Kontor")]
    [InlineData(@"B\U+00F8ttekott", @"B\U+00F8ttekott")]
    public void RepairRemnant_PutsTheLetterBack(string text, string expected)
    {
        Assert.Equal(expected, DxfText.RepairRemnant(text));
    }
}
