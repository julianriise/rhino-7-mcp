using System.Globalization;
using System.IO;
using System.Text;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F2.6: dxf_import reads the DXF's units from $INSUNITS, guesses them from
/// the drawing's size when the file does not say, and measures what Rhino's
/// own import scaled by, so a plan lands at true size in the mm model.
/// </summary>
public class DxfUnitsTests
{
    static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Rect(double w, double h) =>
        "0\nLWPOLYLINE\n8\nwall\n90\n4\n70\n1\n10\n0\n20\n0\n10\n" + N(w) + "\n20\n0\n10\n" + N(w) + "\n20\n" + N(h) + "\n10\n0\n20\n" + N(h) + "\n";

    /// <summary>A DXF with this $INSUNITS (null: no such header variable) and these entities.</summary>
    static byte[] Dxf(int? insunits, string entities) =>
        Encoding.ASCII.GetBytes(
            "0\nSECTION\n2\nHEADER\n9\n$ACADVER\n1\nAC1032\n"
            + (insunits.HasValue ? "9\n$INSUNITS\n70\n" + insunits.Value + "\n" : "")
            + "0\nENDSEC\n0\nSECTION\n2\nENTITIES\n" + entities + "0\nENDSEC\n0\nEOF\n");

    [Theory]
    [InlineData(1, "in", 25.4, "Units in ($INSUNITS 1), scale ×25.4.")]
    [InlineData(2, "ft", 304.8, "Units ft ($INSUNITS 2), scale ×304.8.")]
    [InlineData(4, "mm", 1.0, "Units mm ($INSUNITS 4), scale ×1.")]
    [InlineData(5, "cm", 10.0, "Units cm ($INSUNITS 5), scale ×10.")]
    [InlineData(6, "m", 1000.0, "Units m ($INSUNITS 6), scale ×1000.")]
    public void InsUnits_GivesTheUnitAndTheScaleIntoMillimetres(int code, string unit, double mm, string line)
    {
        // The size is not asked: a 12 x 8 drawing in mm is still mm when the file says so.
        var reading = DxfUnits.Read(Dxf(code, Rect(12, 8)));
        Assert.Equal(code, reading.InsUnits);
        Assert.Equal(unit, reading.Unit);
        Assert.Equal(mm, reading.Mm);
        Assert.False(reading.Guessed);
        Assert.Equal(line, DxfUnits.Line(reading));
    }

    [Theory]
    [InlineData(null, 12000, 8000, "mm", 1.0, "Units not stated ($INSUNITS missing): guessed mm from its size, 12000 across, scale ×1. Check a known length.")]
    [InlineData(null, 12, 8, "m", 1000.0, "Units not stated ($INSUNITS missing): guessed m from its size, 12 across, scale ×1000. Check a known length.")]
    [InlineData(0, 12000, 8000, "mm", 1.0, "Units not stated ($INSUNITS 0): guessed mm from its size, 12000 across, scale ×1. Check a known length.")]
    [InlineData(0, 12.5, 8, "m", 1000.0, "Units not stated ($INSUNITS 0): guessed m from its size, 12.5 across, scale ×1000. Check a known length.")]
    // 7 is kilometres: not a unit plans are drawn in.
    [InlineData(7, 12000, 8000, "mm", 1.0, "Units not stated ($INSUNITS 7): guessed mm from its size, 12000 across, scale ×1. Check a known length.")]
    public void MissingOrZero_IsGuessedFromTheSize_AndSaysSo(int? code, double w, double h, string unit, double mm, string line)
    {
        var reading = DxfUnits.Read(Dxf(code, Rect(w, h)));
        Assert.Equal(code, reading.InsUnits);
        Assert.True(reading.Guessed);
        Assert.Equal(unit, reading.Unit);
        Assert.Equal(mm, reading.Mm);
        Assert.Equal(line, DxfUnits.Line(reading));
    }

    [Fact]
    public void NothingToMeasure_IsMillimetres_AsAGuess()
    {
        var reading = DxfUnits.Read(Dxf(null, "0\nTEXT\n8\nlabel\n10\n3\n20\n4\n1\nStue\n"));
        Assert.True(reading.Guessed);
        Assert.Equal(0, reading.Span);
        Assert.Equal("mm", reading.Unit);
    }

    [Fact]
    public void ThePlanInMetres_ImportsAtTheSizeOfThePlanInMillimetres()
    {
        var mm = DxfUnits.Read(Dxf(4, Rect(12000, 8000)));
        var m = DxfUnits.Read(Dxf(6, Rect(12, 8)));
        Assert.Equal(12000.0, mm.Span * mm.Mm);
        Assert.Equal(12000.0, m.Span * m.Mm);
        // The same without the header: the size tells them apart.
        Assert.Equal(12000.0, DxfUnits.Read(Dxf(null, Rect(12, 8))).Mm * 12);
    }

    [Fact]
    public void Span_IsTheModelSpaceCurves_LinesPolylinesAndCircles()
    {
        var entities =
            "0\nLINE\n8\nwall\n10\n-1000\n20\n0\n11\n5000\n21\n250\n"
            + "0\nCIRCLE\n8\nwall\n10\n5000\n20\n0\n40\n1500\n"
            // A text and a block insert far out: Rhino's objects for them are wider than their points.
            + "0\nTEXT\n8\nlabel\n10\n90000\n20\n0\n1\nStue\n"
            + "0\nINSERT\n8\nwindow\n2\nV1\n10\n-90000\n20\n0\n"
            // A title block on a layout.
            + "0\nLINE\n8\nframe\n67\n1\n10\n0\n20\n0\n11\n420000\n21\n297000\n";
        Assert.Equal(7500.0, DxfUnits.Read(Dxf(4, entities)).Span);
    }

    [Theory]
    // Rhino brought it in as drawn.
    [InlineData(12000, 12000, 1.0)]
    // Text and hatches widen what Rhino made a little.
    [InlineData(12900, 12000, 1.0)]
    // Rhino's AutoCAD import setting on cm: a mm DXF comes in 10x (ROADMAP, found 2026-09-25).
    [InlineData(120000, 12000, 10.0)]
    [InlineData(12000000, 12000, 1000.0)]
    [InlineData(304800, 12000, 25.4)]
    // No unit step near: not told.
    [InlineData(36000, 12000, 0.0)]
    [InlineData(0, 12000, 0.0)]
    [InlineData(12000, 0, 0.0)]
    public void Applied_IsWhatRhinosImportScaledBy_ReadAsAUnitStep(double imported, double source, double expected)
    {
        Assert.Equal(expected, DxfUnits.Applied(imported, source, 1.0));
    }

    [Fact]
    public void Office_IsMillimetres_AsItsHeaderSays()
    {
        var reading = DxfUnits.Read(File.ReadAllBytes(OfficeRoomsTests.OfficePath));
        Assert.Equal(4, reading.InsUnits);
        Assert.False(reading.Guessed);
        Assert.Equal(1.0, reading.Mm);
        // The wall extent the office smoke pins: 30243 mm.
        Assert.Equal(30243, reading.Span, 0);
    }
}
