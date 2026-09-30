using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F7.7: a vector PDF straight to plan_import. The PDF is written here, run
/// through forsk's extractor (tools/pdf_vector, with uv) exactly as the plugin
/// runs it, and the plan file and page image it writes are what plan_import
/// reads: counts, scale and swing come through the clean-up. A scan says why
/// nothing imports and what to do instead. Runs where the forsk checkout and
/// uv are on the machine, as plan1 runs where forsk-private is.
/// </summary>
public class PlanPdfTests : IDisposable
{
    // A3 landscape in points.
    const double Width = 1190.55;
    const double Height = 841.89;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "forsk-plan-pdf-tests", Guid.NewGuid().ToString("N"));

    public PlanPdfTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    static bool CanExtract => PlanPdf.ExtractorDir() != null && PlanPdf.Uv() != null;

    string Temp(string name) => Path.Combine(_dir, name);

    /// <summary>A one-page PDF of the content stream, Helvetica as F1, and the image XObject when given.</summary>
    static byte[] Pdf(string stream, byte[] image = null, double width = Width, double height = Height)
    {
        var resources = image == null
            ? "/Font << /F1 4 0 R >>"
            : "/XObject << /Im1 6 0 R >>";
        var content = Encoding.ASCII.GetBytes(stream);
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0:0.00} {1:0.00}] /Resources << {2} >> /Contents 5 0 R >>",
                width, height, resources)),
            Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Concat(Encoding.ASCII.GetBytes("<< /Length " + content.Length + " >>\nstream\n"), content, Encoding.ASCII.GetBytes("\nendstream"))
        };
        if (image != null)
            objects.Add(Concat(Encoding.ASCII.GetBytes(
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length "
                + image.Length + " >>\nstream\n"), image, Encoding.ASCII.GetBytes("\nendstream")));

        var output = new List<byte>(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Count);
            output.AddRange(Encoding.ASCII.GetBytes((i + 1) + " 0 obj\n"));
            output.AddRange(objects[i]);
            output.AddRange(Encoding.ASCII.GetBytes("\nendobj\n"));
        }
        var xref = output.Count;
        var table = new StringBuilder("xref\n0 " + (objects.Count + 1) + "\n0000000000 65535 f \n");
        foreach (var offset in offsets) table.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        table.Append("trailer << /Size " + (objects.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        output.AddRange(Encoding.ASCII.GetBytes(table.ToString()));
        return output.ToArray();
    }

    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A filled rectangle; y_down is its top edge on the page, y down.</summary>
    static string Rect(double x, double yDown, double w, double h)
    {
        double yb = Height - (yDown + h), yt = Height - yDown;
        return F(x) + " " + F(yb) + " m " + F(x + w) + " " + F(yb) + " l " + F(x + w) + " " + F(yt) + " l " + F(x) + " " + F(yt) + " l h f\n";
    }

    static string Line(double x0, double y0, double x1, double y1) =>
        F(x0) + " " + F(Height - y0) + " m " + F(x1) + " " + F(Height - y1) + " l S\n";

    static string Text(double x, double yDown, string literal) =>
        "BT /F1 11 Tf 1 0 0 1 " + F(x) + " " + F(Height - yDown) + " Tm (" + literal + ") Tj ET\n";

    /// <summary>A door swing: a quarter circle of short lines from the closed end, and the open leaf, y down.</summary>
    static string Swing(double hingeX, double hingeY, double radius = 24.0, int steps = 12)
    {
        var parts = new StringBuilder("0.24 w\n");
        var points = Enumerable.Range(0, steps + 1)
            .Select(step => -Math.PI / 2 * step / steps)
            .Select(a => (X: hingeX + radius * Math.Cos(a), Y: hingeY + radius * Math.Sin(a)))
            .ToList();
        for (var i = 0; i + 1 < points.Count; i++) parts.Append(Line(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y));
        parts.Append(Line(hingeX, hingeY, hingeX, hingeY - radius));
        return parts.ToString();
    }

    /// <summary>
    /// The extractor's own synthetic sheet (tools/pdf_vector/tests): one room
    /// of four grey walls, a door gap in the bottom wall with its swing, a
    /// two-line window gap in the top wall, a room name with its area, and a
    /// 1:100 on A3 title.
    /// </summary>
    static string Sheet()
    {
        var parts = new StringBuilder("0.753 g\n");
        parts.Append(Rect(400, 230, 10, 240));
        parts.Append(Rect(610, 230, 10, 240));
        parts.Append(Rect(400, 230, 50, 10));
        parts.Append(Rect(490, 230, 130, 10));
        parts.Append(Rect(400, 460, 90, 10));
        parts.Append(Rect(520, 460, 100, 10));
        parts.Append("0.12 w\n");
        parts.Append(Line(454, 234, 486, 234));
        parts.Append(Line(454, 236, 486, 236));
        parts.Append(Swing(490.0, 465.0));
        parts.Append(Text(470, 340, "Stue"));
        parts.Append(Text(470, 356, "12,4 m\\262"));
        parts.Append(Text(40, 30, "1:100"));
        parts.Append(Text(40, 48, "\\(p\\345 A3\\)"));
        return parts.ToString();
    }

    [Fact]
    public void VectorPdf_ThroughTheExtractor_ImportsItsCountsAndScale()
    {
        if (!CanExtract) return;
        var pdf = Temp("sheet.pdf");
        File.WriteAllBytes(pdf, Pdf(Sheet()));

        Assert.Equal(1, PlanPdf.Pages(pdf));
        var extracted = PlanPdf.Extract(pdf, 1, Path.GetDirectoryName(pdf));
        Assert.Equal("sheet-p1.json", Path.GetFileName(extracted.PlanPath));

        var plan = PlanImport.Parse(File.ReadAllText(extracted.PlanPath));
        Assert.Null(PlanImport.Refusal(plan, "sheet.pdf page 1"));
        Assert.Equal("detected", plan.ScaleStatus);
        Assert.Equal("1:100", plan.ScaleRatio);
        // The page image sits in the plan's frame: its pixels at 200 dpi and 1:100, 12.7 mm each.
        Assert.True(PlanImport.TryPngSize(File.ReadAllBytes(extracted.ImagePath), out var px, out _, out _));
        Assert.Equal(Math.Round(px * 12.7, 1), plan.ImageWidthMm, 6);
        Assert.InRange(plan.ImageWidthMm - 42000.0, 0.0, 12.7);

        var result = PlanImport.Clean(plan);
        Assert.Equal(4, result.Detected);
        Assert.Equal(1, result.Doors);
        Assert.Equal(1, result.Windows);
        Assert.Equal(0, result.Loose);
        Assert.Equal("Stue", Assert.Single(result.Rooms).Label);
        Assert.Equal(0, result.Outside);
        // One room of walls: one outline, the room its hole.
        Assert.Equal(1, result.Outlines);
        Assert.Single(result.Networks[0].Holes);
        Assert.Equal(0, result.Overlaps);
        Assert.All(result.Walls, wall => Assert.Equal(0.0, wall.Thickness % 10.0, 9));
        // The door's swing comes through: the hinge end along its wall, the leaf opening into the room.
        var door = result.Openings.Single(o => o.Kind == "door");
        Assert.True(door.Swings);
        Assert.Equal(1.0, Math.Abs(door.HingeDir.X), 9);
        Assert.Equal(1.0, door.OpensDir.Y, 9);
        Assert.StartsWith("Imported ", PlanImport.Message(result, PlanImport.ScaleLine("detected", plan.ScaleRatio)));
        Assert.Contains("Scale 1:100 read from the plan and applied: confirm it, or override it, with Set scale",
            PlanImport.Message(result, PlanImport.ScaleLine("detected", plan.ScaleRatio)));
    }

    [Fact]
    public void ScannedPdf_SaysItHasNoVectorWalls()
    {
        if (!CanExtract) return;
        var pdf = Temp("scan.pdf");
        File.WriteAllBytes(pdf, Pdf("q 180 0 0 180 40 400 cm /Im1 Do Q\n", new byte[] { 0xff, 0x00, 0x00 }, 595.28, 841.89));

        var extracted = PlanPdf.Extract(pdf, 1, Path.GetDirectoryName(pdf));
        var plan = PlanImport.Parse(File.ReadAllText(extracted.PlanPath));
        Assert.Empty(plan.Walls);
        Assert.Equal("scan.pdf page 1: no vector walls found; page looks like a raster scan. Nothing was imported.",
            PlanImport.Refusal(plan, "scan.pdf page 1"));
    }

    [Fact]
    public void AMissingPage_FailsWithTheExtractorsReason()
    {
        if (!CanExtract) return;
        var pdf = Temp("sheet.pdf");
        File.WriteAllBytes(pdf, Pdf(Sheet()));
        var error = Assert.Throws<InvalidOperationException>(() => PlanPdf.Extract(pdf, 2, Path.GetDirectoryName(pdf)));
        Assert.Contains("page 2 is not in", error.Message);
    }

    /// <summary>
    /// plan1 straight from its PDF, as the smoke's pdf run imports it. A client
    /// drawing in forsk-private: this runs only where that is checked out.
    /// </summary>
    [Fact]
    public void Plan1Pdf_ThroughTheExtractor_ClosesAroundAllTwelveRooms()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pdf = Path.Combine(home, "Documents", "hobby", "forsk-private", "import", "plan1", "plan1.pdf");
        if (!CanExtract || !File.Exists(pdf)) return;
        var extracted = PlanPdf.Extract(pdf, 1, _dir);
        var plan = PlanImport.Parse(File.ReadAllText(extracted.PlanPath));
        Assert.Equal("detected", plan.ScaleStatus);
        Assert.Equal("1:100", plan.ScaleRatio);
        Assert.Equal(42011.6, plan.ImageWidthMm, 6);

        var result = PlanImport.Clean(plan);
        // The counts the smoke's pdf run checks. Against the PyMuPDF reference
        // file (47 walls, nothing closed), this port's reading drops one wall
        // inside a thicker one and closes one 346 mm gap, both near 16, -22.7 m
        // and both lines in review.
        Assert.Equal(
            "detected 54 walls 46 doors 12 windows 10 loose 0 diagonal 4 rooms 12 unlabelled 0 outside 0 "
            + "outlines 1 holes 12 pieces 53 closed 1 free 0 uncut 0 swings 11",
            $"detected {result.Detected} walls {result.Walls.Count} doors {result.Doors} windows {result.Windows} "
            + $"loose {result.Loose} diagonal {result.Diagonal} rooms {result.Rooms.Count} unlabelled {result.Unlabelled} "
            + $"outside {result.Outside} outlines {result.Outlines} holes {result.Networks.Sum(n => n.Holes.Count)} "
            + $"pieces {result.Networks.Sum(n => n.Pieces)} closed {result.Closed} free {result.Blocks} uncut {result.Uncut} "
            + $"swings {result.Openings.Count(o => o.Swings)}");
        Assert.Single(result.Dropped);
        WallCleanupTests.AssertSound(result, 5.0);
    }

    [Fact]
    public void ADetectionWithWalls_OrWithoutADiagnostic_IsNotRefused()
    {
        var plan = new PlanImport.Plan { Diagnostic = "no vector walls found" };
        plan.Walls.Add(new PlanImport.Wall { A = new RoomDetect.Pt(0, 0), B = new RoomDetect.Pt(4000, 0), Thickness = 200 });
        Assert.Null(PlanImport.Refusal(plan, "plan.pdf page 1"));
        Assert.Null(PlanImport.Refusal(new PlanImport.Plan(), "plan.json"));
        Assert.StartsWith("plan.pdf page 1: no vector walls found. Nothing was imported.",
            PlanImport.Refusal(new PlanImport.Plan { Diagnostic = "no vector walls found" }, "plan.pdf page 1"));
    }
}
