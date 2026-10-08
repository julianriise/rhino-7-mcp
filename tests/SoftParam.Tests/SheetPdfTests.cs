using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// v3 N3: the vector PDF from a synthetic flat A3 sheet: the title block's
/// cells, a 1:100 plan's strokes at 0.50/0.35/0.18/0.13, a solid poché ring
/// with a hole, a patterned hatch's segments, centred and right-aligned
/// text and "Bad 12,4 m² · æøå". The bytes are read back with this test's
/// own minimal reader. FORSK_WRITE_PDF_FIXTURE=1 writes the golden file
/// server/tests/fixtures/sheet_vector_sample.pdf that pdf_check reads.
/// </summary>
public class SheetPdfTests
{
    static readonly double[] Pens = { 0.50, 0.35, 0.18, 0.13 };

    static SheetPdf.Stroke Line(double x0, double y0, double x1, double y1, double pen) =>
        new() { Points = { new Pt(x0, y0), new Pt(x1, y1) }, WidthMm = pen };

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    /// <summary>The garage's 8 × 4 m walls at scale, from paper (40, 60), with a 200 mm poché band.</summary>
    public static SheetPdf.Page Sheet(int scale = 100)
    {
        var page = new SheetPdf.Page { WidthMm = 420, HeightMm = 297 };
        double P(double model) => model / scale;
        double X(double model) => 40 + P(model);
        double Y(double model) => 60 + P(model);
        // Title block: frame, dividers, captions and values.
        page.Strokes.Add(new SheetPdf.Stroke { Points = Rect(130, 10, 410, 28), Closed = true, WidthMm = 0.35 });
        var cells = new[] { ("Drawing", "Ground floor plan"), ("Drawing no.", "A-20-001"), ("Scale", "1:" + scale.ToString(CultureInfo.InvariantCulture)),
            ("Project", "Garage"), ("Client", "Ola Nordmann"), ("Architect", "Riise Arkitekter") };
        for (var i = 0; i < cells.Length; i++)
        {
            var x = 130 + i * 280.0 / cells.Length;
            if (i > 0) page.Strokes.Add(Line(x, 10, x, 28, 0.18));
            page.Texts.Add(new SheetPdf.Text { Value = cells[i].Item1, X = x + 2, Y = 22.2, Mm = 1.8 });
            page.Texts.Add(new SheetPdf.Text { Value = cells[i].Item2, X = x + 2, Y = 14, Mm = 2.5, Bold = i < 2 });
        }
        // Plan: the outer and inner faces as cut lines, a beyond line, a thin dimension line.
        page.Strokes.Add(new SheetPdf.Stroke { Points = Rect(X(0), Y(0), X(8000), Y(4000)), Closed = true, WidthMm = Pens[0] });
        page.Strokes.Add(new SheetPdf.Stroke { Points = Rect(X(200), Y(200), X(7800), Y(3800)), Closed = true, WidthMm = Pens[0] });
        page.Strokes.Add(Line(X(0), Y(4500), X(8000), Y(4500), Pens[1]));
        page.Strokes.Add(Line(X(1000), Y(1000), X(7000), Y(1000), Pens[2]));
        for (var k = 0; k <= 8; k++)
            page.Strokes.Add(Line(X(1000 * k), Y(-600), X(1000 * k), Y(-400), Pens[3]));
        page.Strokes.Add(Line(X(0), Y(-500), X(8000), Y(-500), Pens[3]));
        // Solid poché: the wall band, the outer ring with the room as its hole.
        page.Fills.Add(new SheetPdf.Fill { Rings = { Rect(X(0), Y(0), X(8000), Y(4000)), Rect(X(200), Y(200), X(7800), Y(3800)) } });
        // A patterned hatch exploded into its segments at the piece's weight.
        for (var k = 1; k < 10; k++)
            page.Strokes.Add(Line(X(8000 + 100 * k), Y(0), X(8000 + 100 * k + 200), Y(200), 0.13));
        // Centred room tag, right-aligned dimension value, and the æøå line.
        page.Texts.Add(new SheetPdf.Text { Value = "Bad 12,4 m² · æøå", X = X(4000), Y = Y(2000), Mm = 2.5, Align = SheetPdf.Align.Centre });
        page.Texts.Add(new SheetPdf.Text { Value = "8000", X = X(8000), Y = Y(-450), Mm = 1.8, Align = SheetPdf.Align.Right });
        page.Texts.Add(new SheetPdf.Text { Value = "N", X = 20, Y = 20, Mm = 2.5, AngleDeg = 90, Align = SheetPdf.Align.Centre, Vertical = SheetPdf.Vertical.Middle });
        return page;
    }

    static ProjectInfo.Pdf Info() => ProjectInfo.PdfInfo(ProjectInfoTests.Read(ProjectInfoTests.Smoke), "A-20-001", "A-20-001");

    static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>The objects by number, read through the xref; asserts every offset points at "n 0 obj".</summary>
    static Dictionary<int, string> Objects(byte[] pdf)
    {
        var text = Latin1(pdf);
        var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n0 ", text.Substring(startxref));
        var head = Regex.Match(text.Substring(startxref), @"^xref\n0 (\d+)\n");
        var count = int.Parse(head.Groups[1].Value, CultureInfo.InvariantCulture);
        var rows = text.Substring(startxref + head.Length).Split('\n');
        var objects = new Dictionary<int, string>();
        for (var n = 1; n < count; n++)
        {
            var row = rows[n];
            Assert.Matches(@"^\d{10} 00000 n $", row);
            var offset = int.Parse(row.Substring(0, 10), CultureInfo.InvariantCulture);
            var prefix = n.ToString(CultureInfo.InvariantCulture) + " 0 obj\n";
            Assert.Equal(prefix, text.Substring(offset, prefix.Length));
            var end = text.IndexOf("\nendobj\n", offset, StringComparison.Ordinal);
            objects[n] = text.Substring(offset + prefix.Length, end - offset - prefix.Length);
        }
        return objects;
    }

    static byte[] Inflate(string streamObject)
    {
        var start = streamObject.IndexOf("stream\n", StringComparison.Ordinal) + "stream\n".Length;
        var end = streamObject.LastIndexOf("\nendstream", StringComparison.Ordinal);
        var raw = Encoding.Latin1.GetBytes(streamObject.Substring(start, end - start));
        var length = int.Parse(Regex.Match(streamObject, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(length, raw.Length);
        using var input = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    static string PageContent(byte[] pdf, int page = 0)
    {
        var objects = Objects(pdf);
        return Latin1(Inflate(objects[7 + 2 * page]));
    }

    [Fact]
    public void TheFile_IsAPdf14_WithAGoodXref_NoImage_HelveticaWinAnsi_AndItsInfo()
    {
        var result = SheetPdf.Write(new[] { Sheet() }, Info());
        var text = Latin1(result.Bytes);
        Assert.StartsWith("%PDF-1.4\n", text);
        var objects = Objects(result.Bytes);
        Assert.DoesNotContain("/Image", text);
        Assert.Contains("/BaseFont /Helvetica /Encoding /WinAnsiEncoding", objects[3]);
        Assert.Contains("/BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding", objects[4]);
        Assert.Contains("/Title " + SheetPdf.PdfString("Garage — A-20-001"), objects[5]);
        Assert.StartsWith("<FEFF", SheetPdf.PdfString("Garage — A-20-001"));
        Assert.Contains("/Author (Riise Arkitekter)", objects[5]);
        Assert.Contains("/Creator (Forsk)", objects[5]);
        Assert.Contains("/Subject (Storgata 1, 0150 Oslo)", objects[5]);
        Assert.Contains("/Keywords (2026-07; Ola Nordmann; rev. B)", objects[5]);
        Assert.Contains("/MediaBox [0 0 1190.551 841.89]", objects[6]);
        Assert.Contains("/Filter /FlateDecode", objects[7]);
        Assert.Equal(1, result.Pages);
    }

    [Fact]
    public void TheTitle_IsUtf16WithItsBom_WhenNotAscii()
    {
        Assert.Equal("<FEFF00470061007200610067006520140020>", SheetPdf.PdfString("Garage— "));
        Assert.Equal("(A \\(b\\) \\\\)", SheetPdf.PdfString("A (b) \\"));
    }

    [Fact]
    public void TheContent_HasTheTextAsTj_TheWeightsInPoints_AndAnEvenOddFill()
    {
        var content = PageContent(SheetPdf.Write(new[] { Sheet() }, Info()).Bytes);
        Assert.Contains("(A-20-001) Tj", content);
        Assert.Contains("(Garage) Tj", content);
        // æ ø å as WinAnsi E6 F8 E5, ² B2, · B7.
        Assert.Contains("(Bad 12,4 m² · æøå) Tj", content);
        Assert.Contains("\n1.417 w\n", content);
        Assert.Contains("\n0.992 w\n", content);
        Assert.Contains("\n0.51 w\n", content);
        Assert.Contains("\n0.369 w\n", content);
        Assert.Contains("\nf*\n", content);
        Assert.Contains("0 J 0 j", content);
        Assert.Contains("/F2 ", content);
        Assert.True(Regex.Matches(content, @" [ml]\n").Count >= 20);
    }

    [Fact]
    public void CentredAndRightAlignedText_AreMeasuredWithHelveticasWidths()
    {
        var unmapped = 0;
        Assert.Equal(4 * 0.556, SheetPdf.Width(SheetPdf.WinAnsi("8000", ref unmapped)), 9);
        Assert.Equal(0.667 + 0.556 + 0.556, SheetPdf.Width(SheetPdf.WinAnsi("Bad", ref unmapped)), 9);
        var page = new SheetPdf.Page();
        page.Texts.Add(new SheetPdf.Text { Value = "8000", X = 100, Y = 50, Mm = 1.8, Align = SheetPdf.Align.Right });
        page.Texts.Add(new SheetPdf.Text { Value = "8000", X = 100, Y = 60, Mm = 1.8, Align = SheetPdf.Align.Centre });
        var content = Encoding.Latin1.GetString(SheetPdf.Content(page, ref unmapped));
        var size = 1.8 / SheetPdf.CapHeight;
        var right = (100 - 4 * 0.556 * size) * SheetPdf.PtPerMm;
        var centre = (100 - 2 * 0.556 * size) * SheetPdf.PtPerMm;
        Assert.Contains("1 0 0 1 " + SheetPdf.Num(right) + " " + SheetPdf.Num(50 * SheetPdf.PtPerMm) + " Tm", content);
        Assert.Contains(SheetPdf.Num(centre) + " " + SheetPdf.Num(60 * SheetPdf.PtPerMm) + " Tm", content);
        Assert.Equal(0, unmapped);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    public void ThePenWidths_InPoints_AreTheSameAtEveryScale(int scale)
    {
        static string[] Widths(string content) => Regex.Matches(content, @"\n([\d.]+) w\n").Select(m => m.Groups[1].Value).Distinct().OrderBy(w => w).ToArray();
        var reference = Widths(PageContent(SheetPdf.Write(new[] { Sheet(100) }, Info()).Bytes));
        Assert.Equal(reference, Widths(PageContent(SheetPdf.Write(new[] { Sheet(scale) }, Info()).Bytes)));
        Assert.Equal(new[] { "0.369", "0.51", "0.992", "1.417" }, reference);
    }

    [Fact]
    public void ACharacterOutsideWinAnsi_IsAQuestionMark_CountedOnce()
    {
        var page = new SheetPdf.Page();
        page.Texts.Add(new SheetPdf.Text { Value = "Høyde ≥ 2400 – OK", X = 10, Y = 10, Mm = 2.5 });
        var result = SheetPdf.Write(new[] { page }, Info());
        Assert.Equal(1, result.Unmapped);
        Assert.Contains("(Høyde ? 2400 \u0096 OK) Tj", PageContent(result.Bytes));
        Assert.Equal(0, SheetPdf.Write(new[] { Sheet() }, Info()).Unmapped);
    }

    [Fact]
    public void TheSameInput_GivesTheSameBytes_AndCreationDateOnlyWhenAsked()
    {
        var a = SheetPdf.Write(new[] { Sheet(), Sheet(50) }, Info()).Bytes;
        var b = SheetPdf.Write(new[] { Sheet(), Sheet(50) }, Info()).Bytes;
        Assert.Equal(a, b);
        Assert.DoesNotContain("/CreationDate", Latin1(a));
        Assert.Contains("/CreationDate (D:20261004213000)", Latin1(SheetPdf.Write(new[] { Sheet() }, Info(), new DateTime(2026, 10, 4, 21, 30, 0)).Bytes));
        Assert.Contains("/Count 2", Objects(a)[2]);
        Assert.Contains("/Kids [6 0 R 8 0 R]", Objects(a)[2]);
    }

    [Fact]
    public void TheGoldenFile_IsWhatTheWriterWritesNow()
    {
        var bytes = SheetPdf.Write(new[] { Sheet() }, Info()).Bytes;
        var path = Path.Combine(ProjectInfoTests.PluginDir(), "..", "server", "tests", "fixtures", "sheet_vector_sample.pdf");
        if (Environment.GetEnvironmentVariable("FORSK_WRITE_PDF_FIXTURE") == "1")
            File.WriteAllBytes(path, bytes);
        Assert.True(File.Exists(path), "Write it with FORSK_WRITE_PDF_FIXTURE=1: " + path);
        // Deflate's bytes may differ between zlib builds, so the drawing and the Info are compared, not the file.
        var golden = File.ReadAllBytes(path);
        Assert.Equal(PageContent(bytes), PageContent(golden));
        Assert.Equal(Objects(bytes)[5], Objects(golden)[5]);
        Assert.Equal(Objects(bytes)[6], Objects(golden)[6]);
    }

    /// <summary>
    /// export_pdf writes the vector file first on every OS: no bitmap on its
    /// main path, the Windows ViewCapture branch gone, and the Mac preview
    /// capture only after the vector write threw.
    /// </summary>
    [Fact]
    public void ExportPdf_IsVectorFirst_TheBitmapOnlyInTheFallback()
    {
        var functions = Path.Combine(ProjectInfoTests.PluginDir(), "Functions");
        var pack = File.ReadAllText(Path.Combine(functions, "LayoutPack.cs"));
        var body = pack.Substring(pack.IndexOf("[McpCommand(\"export_pdf\")]", StringComparison.Ordinal));
        Assert.DoesNotContain("DrawBitmap", body);
        Assert.DoesNotContain("ViewCaptureSettings", pack);
        Assert.DoesNotContain("RunningOnOSX", body);
        var vector = body.IndexOf("WriteVectorPdf(", StringComparison.Ordinal);
        var fallback = body.IndexOf("ExportMacPreviewPdf(", StringComparison.Ordinal);
        Assert.True(vector > 0 && fallback > vector, "vector first, then the fallback");
        Assert.Contains("catch (Exception e)", body.Substring(vector, fallback - vector));
        Assert.DoesNotContain("DrawBitmap", File.ReadAllText(Path.Combine(functions, "SheetExportPdf.cs")));
        Assert.Contains("DrawBitmap", File.ReadAllText(Path.Combine(functions, "LayoutPackMac.cs")));
    }

    /// <summary>
    /// FU.2 made each furniture symbol one block insert on the flat sheet (no
    /// geometry, no text), and the PDF page skipped every piece without
    /// either: the furniture never printed. The page draws the block's
    /// curves through the insert's transform, before that skip, and the piece
    /// carries the plan's pen. RhinoCommon does not run headless, so this
    /// reads the source.
    /// </summary>
    [Fact]
    public void AFurnitureInsert_PrintsItsBlockCurves_AtThePlansPen()
    {
        var functions = Path.Combine(ProjectInfoTests.PluginDir(), "Functions");
        var pdf = File.ReadAllText(Path.Combine(functions, "SheetExportPdf.cs"));
        var start = pdf.IndexOf("SheetPdf.Page PdfPage(", StringComparison.Ordinal);
        var end = pdf.IndexOf("private static void AddStroke(", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var page = pdf.Substring(start, end - start);
        var block = page.IndexOf("piece.BlockCurves", StringComparison.Ordinal);
        var skip = page.IndexOf("if (piece.Geometry == null)", StringComparison.Ordinal);
        Assert.True(block > 0 && skip > block, "the PDF page draws a block piece before it skips pieces with no geometry");
        Assert.Contains(".Transform(piece.BlockXform)", page);
        var furniture = File.ReadAllText(Path.Combine(functions, "SheetExportFurniture.cs"));
        Assert.Contains("ForskTechnical.PenFor(Furniture.RoleFor(piece)", furniture);
        Assert.Contains("Weight = pen.Mm", furniture);
    }

    /// <summary>Each stroke is marked content named by its DWG layer, so a check can find the furniture in a page.</summary>
    [Fact]
    public void Strokes_AreMarkedContentByLayer_OneRunPerLayerChange()
    {
        var page = new SheetPdf.Page();
        page.Strokes.Add(new SheetPdf.Stroke { Points = { new Pt(10, 10), new Pt(20, 10) }, WidthMm = 0.5, Layer = "A-WALL" });
        page.Strokes.Add(new SheetPdf.Stroke { Points = { new Pt(30, 10), new Pt(40, 10) }, WidthMm = 0.13, Layer = "A-FURN" });
        page.Strokes.Add(new SheetPdf.Stroke { Points = { new Pt(30, 20), new Pt(40, 20), new Pt(40, 30) }, WidthMm = 0.13, Layer = "A-FURN" });
        page.Strokes.Add(new SheetPdf.Stroke { Points = { new Pt(50, 10), new Pt(60, 10) }, WidthMm = 0.13 });
        page.Strokes.Add(new SheetPdf.Stroke { Points = { new Pt(70, 10), new Pt(80, 10) }, WidthMm = 0.13, Layer = "bad name" });
        var unmapped = 0;
        var content = Encoding.Latin1.GetString(SheetPdf.Content(page, ref unmapped));
        var furn = content.IndexOf("/A-FURN BMC\n", StringComparison.Ordinal);
        Assert.True(content.IndexOf("/A-WALL BMC\n", StringComparison.Ordinal) is var wall && wall >= 0 && wall < furn);
        var run = content.Substring(furn, content.IndexOf("EMC\n", furn, StringComparison.Ordinal) - furn);
        Assert.Equal(2, Regex.Matches(run, @" m\n").Count);
        Assert.Equal(3, Regex.Matches(run, @" l\n").Count);
        Assert.Equal(2, Regex.Matches(content, "BMC\n").Count);
        Assert.Equal(2, Regex.Matches(content, "EMC\n").Count);
        Assert.Null(SheetPdf.MarkName("bad name"));
        Assert.Null(SheetPdf.MarkName(null));
    }
}
