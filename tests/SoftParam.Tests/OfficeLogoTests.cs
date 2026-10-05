using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The office logo: a PNG read into RGB and alpha (every filter, a palette, a
/// grey image), a JPEG kept whole with its size, junk refused; the logo cell at
/// the title block's right end; and the PDF drawing one image object for every
/// sheet's logo, with a soft mask when the PNG has transparency.
/// </summary>
public class OfficeLogoTests
{
    /// <summary>A PNG file: 8-bit, not interlaced, each row with the filter given.</summary>
    internal static byte[] Png(int width, int height, int colour, byte[][] rows, byte[] palette = null, byte[] trns = null)
    {
        var output = new MemoryStream();
        output.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }, 0, 8);
        void Chunk(string type, byte[] body)
        {
            var len = new[] { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length };
            output.Write(len, 0, 4);
            output.Write(Encoding.ASCII.GetBytes(type), 0, 4);
            output.Write(body, 0, body.Length);
            output.Write(new byte[4], 0, 4);
        }
        Chunk("IHDR", new byte[] { 0, 0, 0, (byte)width, 0, 0, 0, (byte)height, 8, (byte)colour, 0, 0, 0 });
        if (palette != null) Chunk("PLTE", palette);
        if (trns != null) Chunk("tRNS", trns);
        Chunk("IDAT", SheetPdf.Deflate(rows.SelectMany(r => r).ToArray()));
        Chunk("IEND", new byte[0]);
        return output.ToArray();
    }

    [Fact]
    public void APng_ReadsAsRgb_EveryFilterUndone()
    {
        // 2 x 3 RGB, rows filtered None, Sub, Up: every pixel (10, 20, 30) after unfiltering.
        var rows = new[]
        {
            new byte[] { 0, 10, 20, 30, 10, 20, 30 },
            new byte[] { 1, 10, 20, 30, 0, 0, 0 },
            new byte[] { 2, 0, 0, 0, 0, 0, 0 }
        };
        var picture = OfficeLogo.Read(Png(2, 3, 2, rows), out var error);
        Assert.Null(error);
        Assert.Equal(2, picture.Width);
        Assert.Equal(3, picture.Height);
        Assert.Null(picture.Alpha);
        Assert.Equal(Enumerable.Repeat(new byte[] { 10, 20, 30 }, 6).SelectMany(b => b), picture.Rgb);
    }

    [Fact]
    public void AverageAndPaeth_AreUndoneToo()
    {
        // Grey 2 x 2: row 0 None [100, 50]; row 1 Average then row 1 Paeth of the same pixels.
        var avg = OfficeLogo.Read(Png(2, 2, 0, new[] { new byte[] { 0, 100, 50 }, new byte[] { 3, 0, 0 } }), out _);
        // Average: x0 = 0 + (0 + 100) / 2 = 50; x1 = 0 + (50 + 50) / 2 = 50.
        Assert.Equal(new byte[] { 100, 100, 100, 50, 50, 50, 50, 50, 50, 50, 50, 50 }, avg.Rgb);
        var paeth = OfficeLogo.Read(Png(2, 2, 0, new[] { new byte[] { 0, 100, 50 }, new byte[] { 4, 0, 0 } }), out _);
        // Paeth: x0 predicts up (100); x1 from left 100, up 50, corner 100 predicts 50.
        Assert.Equal(new byte[] { 100, 100, 100, 50, 50, 50, 100, 100, 100, 50, 50, 50 }, paeth.Rgb);
    }

    [Fact]
    public void APaletteWithTransparency_KeepsItsAlpha()
    {
        var palette = new byte[] { 255, 0, 0, 0, 0, 255 };
        var picture = OfficeLogo.Read(Png(2, 1, 3, new[] { new byte[] { 0, 0, 1 } }, palette, new byte[] { 0 }), out _);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255 }, picture.Rgb);
        Assert.Equal(new byte[] { 0, 255 }, picture.Alpha);
    }

    [Fact]
    public void AJpeg_IsKeptWhole_WithItsSize()
    {
        // SOI, an APP0 to skip, then SOF0: 8 bit, height 40, width 120, 3 components.
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 17, 8, 0, 40, 0, 120, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9 };
        var picture = OfficeLogo.Read(jpeg, out var error);
        Assert.Null(error);
        Assert.Equal(120, picture.Width);
        Assert.Equal(40, picture.Height);
        Assert.Equal(3, picture.JpegComponents);
        Assert.Same(jpeg, picture.Jpeg);
        Assert.Equal(3.0, picture.Aspect, 6);
    }

    [Fact]
    public void Junk_And_ABigFile_AreRefusedWithTheReason()
    {
        Assert.Null(OfficeLogo.Read(Encoding.ASCII.GetBytes("%PDF-1.4 not a picture"), out var junk));
        Assert.Equal(OfficeLogo.Unreadable, junk);
        Assert.Null(OfficeLogo.Read(new byte[OfficeLogo.MaxBytes + 1], out var big));
        Assert.Equal(OfficeLogo.TooLarge, big);
        Assert.Null(OfficeLogo.Decode("not base64!"));
        var png = Png(1, 1, 2, new[] { new byte[] { 0, 1, 2, 3 } });
        Assert.Equal(1, OfficeLogo.Decode(OfficeLogo.Encode(png)).Width);
    }

    [Fact]
    public void TheLogoCell_SitsAtTheRightEnd_AsTallAsTheBandAllows()
    {
        // An 18 mm band: the picture is 14 mm tall, a 3:1 mark 42 mm wide, its cell 46 mm.
        var wide = TitleBlock.Logo(3.0, 410, 10, 28);
        Assert.Equal(46, wide.CellMm, 6);
        Assert.Equal(366, wide.X0, 6);
        Assert.Equal(408, wide.X1, 6);
        Assert.Equal(12, wide.Y0, 6);
        Assert.Equal(26, wide.Y1, 6);
        // A long wordmark stops at LogoMaxMm and is centred in the band's height.
        var longMark = TitleBlock.Logo(10.0, 410, 10, 28);
        Assert.Equal(TitleBlock.LogoMaxMm + 2 * TitleBlock.PadMm, longMark.CellMm, 6);
        Assert.Equal(6, longMark.Y1 - longMark.Y0, 6);
        Assert.Equal(19, (longMark.Y0 + longMark.Y1) / 2, 6);
        // A narrow mark keeps the least cell width.
        Assert.Equal(TitleBlock.LogoMinMm + 2 * TitleBlock.PadMm, TitleBlock.Logo(0.2, 410, 10, 28).CellMm, 6);
        Assert.Null(TitleBlock.Logo(0, 410, 10, 28));
    }

    [Fact]
    public void ThePdf_DrawsOneImageForEverySheetsLogo_WithASoftMask()
    {
        var palette = new byte[] { 0, 0, 0, 255, 255, 255 };
        var logo = OfficeLogo.Read(Png(2, 1, 3, new[] { new byte[] { 0, 0, 1 } }, palette, new byte[] { 255, 0 }), out _);
        SheetPdf.Page Sheet() => new SheetPdf.Page
        {
            Images = { new SheetPdf.Image { Picture = logo, X0 = 366, Y0 = 12, X1 = 408, Y1 = 26 } }
        };
        var pdf = Encoding.Latin1.GetString(SheetPdf.Write(new List<SheetPdf.Page> { Sheet(), Sheet() }, new ProjectInfo.Pdf()).Bytes);
        // Two pages, one image object and its mask after them (objects 10 and 11).
        Assert.Equal(1, Count(pdf, "/Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB"));
        Assert.Equal(1, Count(pdf, "/ColorSpace /DeviceGray"));
        Assert.Contains("/SMask 11 0 R", pdf);
        Assert.Equal(2, Count(pdf, "/XObject << /Im0 10 0 R >>"));
        var unmapped = 0;
        var content = Encoding.ASCII.GetString(SheetPdf.Content(Sheet(), ref unmapped));
        Assert.Contains("q 119.055 0 0 39.685 1037.48 34.016 cm /Im0 Do Q", content);
    }

    [Fact]
    public void APageWithoutALogo_HasNoXObjects()
    {
        var pdf = Encoding.Latin1.GetString(SheetPdf.Write(new List<SheetPdf.Page> { new SheetPdf.Page() }, new ProjectInfo.Pdf()).Bytes);
        Assert.DoesNotContain("/XObject", pdf);
    }

    static int Count(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, System.StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, System.StringComparison.Ordinal)) n++;
        return n;
    }
}
