using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The Analysis set's daylight map sheet (2026-10-08): the plan with the
/// daylight map under its lines, right after the daylight table, and daylight
/// run first when the set needs it and it never ran.
/// </summary>
public class DaylightMapTests
{
    static Analysis.State Stored(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value);
        return Analysis.Read(k => map.TryGetValue(k, out var v) ? v : null);
    }

    [Fact]
    public void TheMap_FollowsTheDaylightTable_OnlyWithDaylightInTheSet()
    {
        var both = Stored(("set.daylight", "1"), ("set.areas", "1"));
        Assert.Equal(new[] { "analysis_daylight", "analysis_daylight_map", "analysis_areas" },
            Analysis.SetSheets(both).Where(s => s.On).Select(s => s.Id));
        var areas = Stored(("set.areas", "1"));
        Assert.Equal(new[] { "analysis_areas" }, Analysis.SetSheets(areas).Where(s => s.On).Select(s => s.Id));
        // Off, it is still a sheet of the set, so a Print takes its old page away.
        Assert.Contains(Analysis.SetSheets(areas), s => s.Id == "analysis_daylight_map" && !s.On);
    }

    [Fact]
    public void WithNoMapToDraw_TheMapSheetIsOff_AndTheTableStays()
    {
        var state = Stored(("set.daylight", "1"));
        Assert.Equal(new[] { "analysis_daylight" }, Analysis.SetSheets(state, false).Where(s => s.On).Select(s => s.Id));
    }

    [Fact]
    public void TheMap_IsNumberedAsAnAnalysisDrawing_AndTheTablesKeepTheirs()
    {
        Assert.Equal("A-80-001", SheetSet.Number("analysis_daylight", 0));
        Assert.Equal("A-80-002", SheetSet.Number("analysis_areas", 0));
        Assert.Equal("A-80-101", SheetSet.Number("analysis_daylight_map", 0));
        Assert.Equal("Daylight map", SheetSet.Title("analysis_daylight_map", 0));
        Assert.Equal("Dagslyskart", SheetSet.Title("analysis_daylight_map", 0, null, true));
    }

    [Fact]
    public void TheMap_IsADrawingOfThePlan_InTheAnalysisSet()
    {
        Assert.True(SheetSet.IsAnalysisSheet("analysis_daylight_map"));
        Assert.False(SheetSet.IsListSheet("analysis_daylight_map"));
        Assert.False(SheetSet.IsTablePage("analysis_daylight_map"));
        Assert.True(SheetSet.IsTablePage("analysis_daylight"));
        Assert.Equal("plan", SheetSet.DrawingOf("analysis_daylight_map"));
        Assert.Equal("north", SheetSet.DrawingOf("north"));
        Assert.True(TitleBlock.NorthArrow("analysis_daylight_map"));
        Assert.True(TitleBlock.ScaleBar("analysis_daylight_map", 100));
        Assert.False(TitleBlock.ScaleBar("analysis_daylight", 100));
        // Analysis.TryAnalysis names analyses, and the map is none.
        Assert.False(Analysis.TryAnalysis("analysis_daylight_map", out _));
        // The Sheets set never holds it.
        var facts = new SheetSet.SetFacts { Walls = true, Lists = { "room" } };
        Assert.DoesNotContain(SheetSet.Infer(facts), s => s.Id == "analysis_daylight_map");
    }

    [Fact]
    public void DaylightRunsBeforePrint_OnlyWhenTheSetHasItAndItNeverRan()
    {
        var never = Stored(("set.daylight", "1"));
        Assert.True(Analysis.NeedsDaylightRun(never, false));
        var ran = Stored(("set.daylight", "1"), ("daylight.rooms", "R01=1.8"));
        Assert.False(Analysis.NeedsDaylightRun(ran, true));
        // The map was cleared since: the run draws it again.
        Assert.True(Analysis.NeedsDaylightRun(ran, false));
        // A map with no room means stored (an older file): the table would be dashes.
        Assert.True(Analysis.NeedsDaylightRun(never, true));
        Assert.False(Analysis.NeedsDaylightRun(Stored(("set.areas", "1")), false));
        // A map the model has moved on from prints old colours: Print runs daylight again.
        Assert.True(Analysis.NeedsDaylightRun(ran, true, mapStale: true));
        Assert.False(Analysis.NeedsDaylightRun(null, false));
    }

    [Fact]
    public void ThePrintLine_SaysDaylightRanFirst_OrWhyItDidNot()
    {
        Assert.Equal("✓ Printed the analysis set: 3 sheets on A3 · a.pdf · daylight ran first",
            ForskReceipt.AnalysisPrintLine(3, "a.pdf", "A3", ForskReceipt.DaylightFirst(true, null)));
        Assert.Equal("✓ Printed the analysis set: 2 sheets on A3 · a.pdf · daylight did not run: No windows.",
            ForskReceipt.AnalysisPrintLine(2, "a.pdf", "A3", ForskReceipt.DaylightFirst(false, "No windows.")));
        Assert.Equal("daylight did not run", ForskReceipt.DaylightFirst(false, " "));
        Assert.Equal("✓ Printed the analysis set: 1 sheet on A3 · a.pdf", ForskReceipt.AnalysisPrintLine(1, "a.pdf", "A3", null));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.1, 0.0)]
    [InlineData(1.0, 0.5)]
    [InlineData(10.0, 1.0)]
    [InlineData(50.0, 1.0)]
    public void TheRamp_IsLogFromATenthToTenPercent_AsTheTracersIs(double df, double t)
    {
        Assert.Equal(t, DaylightPrint.Shade(df), 6);
    }

    [Fact]
    public void TheRampsStops_AreTheTracers_AndPrintLighter()
    {
        Assert.Equal(new byte[] { 0x0B, 0x25, 0x45 }, DaylightPrint.Rgb(0));
        Assert.Equal(new byte[] { 0xF2, 0xF8, 0xFD }, DaylightPrint.Rgb(1));
        Assert.Equal(new byte[] { 0x2F, 0x66, 0x90 }, DaylightPrint.Rgb(0.33));
        Assert.Equal(new byte[] { 0x8F, 0xBC, 0xE6 }, DaylightPrint.Rgb(0.66));
        // Black lines and tags must read over the darkest cell.
        Assert.Equal(new byte[] { 0x0B, 0x25, 0x45 }.Select(DaylightPrint.Tint).ToArray(), DaylightPrint.PrintRgb(0));
        Assert.True(DaylightPrint.Tint(0) >= 90);
        Assert.Equal(255, DaylightPrint.Tint(255));
    }

    [Fact]
    public void TheLegend_TicksEachLabelledDf_AlongTheBar_BottomLowest()
    {
        var legend = DaylightPrint.LegendAt(370, 287);
        Assert.Equal(new[] { "0.1 %", "0.5 %", "1 %", "2 %", "5 %", "10 %" }, legend.Ticks.Select(t => t.Label));
        Assert.Equal(legend.BarY0, legend.Ticks[0].Y, 6);
        Assert.Equal(legend.BarY1, legend.Ticks[5].Y, 6);
        Assert.Equal((legend.BarY0 + legend.BarY1) / 2, legend.Ticks[2].Y, 6);
        Assert.True(legend.BarY1 < legend.TitleY);
        Assert.True(legend.BarX0 >= 370 && legend.BarX1 <= 370 + DaylightPrint.ColumnMm);
        Assert.True(legend.TitleY <= 287);
    }

    [Fact]
    public void TheRampPicture_HasTenPercentOnItsTopRow()
    {
        var ramp = DaylightPrint.RampPicture(64);
        Assert.Equal(64, ramp.Height);
        Assert.Equal(1, ramp.Width);
        Assert.Equal(DaylightPrint.PrintRgb(1), ramp.Rgb.Take(3).ToArray());
        Assert.Equal(DaylightPrint.PrintRgb(0), ramp.Rgb.Skip(63 * 3).Take(3).ToArray());
        Assert.Null(ramp.Alpha);
    }

    [Fact]
    public void TheModelToPaperMap_IsTheDrawingMapThenTheDetails()
    {
        // The plan drawing moved the model by (-1000, -2000); the detail is 1:100 with its origin at (50, 60) mm.
        var drawing = SheetFlat.Affine.Translation(-1000, -2000);
        var page = new SheetFlat.Affine { A = 0.01, E = 0.01, C = 50, F = 60 };
        var map = DaylightPrint.Then(drawing, page);
        map.Apply(1000, 2000, out var x, out var y);
        Assert.Equal(50, x, 9);
        Assert.Equal(60, y, 9);
        map.Apply(6000, 2000, out x, out y);
        Assert.Equal(100, x, 9);
        Assert.True(DaylightPrint.TryAffine(DaylightPrint.FormatAffine(map), out var back));
        back.Apply(6000, 7000, out var bx, out var by);
        Assert.Equal(100, bx, 9);
        Assert.Equal(110, by, 9);
        Assert.False(DaylightPrint.TryAffine("1,2,3", out _));
        Assert.False(DaylightPrint.TryAffine(null, out _));
    }

    [Fact]
    public void ARoomsCell_FillsItsPaperBox_AndOutsideStaysClear()
    {
        // Two triangles: a 10 x 5 mm square at (100, 50) mm, one colour.
        var tris = new List<DaylightPrint.Tri>
        {
            new DaylightPrint.Tri { X0 = 100, Y0 = 50, X1 = 110, Y1 = 50, X2 = 110, Y2 = 55, R = 10, G = 20, B = 30 },
            new DaylightPrint.Tri { X0 = 100, Y0 = 50, X1 = 110, Y1 = 55, X2 = 100, Y2 = 55, R = 10, G = 20, B = 30 },
            // A triangle at the far corner, so the raster's box spans both.
            new DaylightPrint.Tri { X0 = 114, Y0 = 60, X1 = 120, Y1 = 54, X2 = 120, Y2 = 60, R = 200, G = 0, B = 0 }
        };
        var raster = DaylightPrint.Rasterise(tris, 2.0);
        Assert.Equal(100, raster.X0, 6);
        Assert.Equal(50, raster.Y0, 6);
        Assert.Equal(120, raster.X1, 6);
        Assert.Equal(60, raster.Y1, 6);
        var picture = raster.Picture;
        Assert.Equal(40, picture.Width);
        Assert.Equal(20, picture.Height);
        // Rows run top down: the square's bottom-left pixel is on the last row.
        var bottomLeft = 19 * 40;
        Assert.Equal(255, picture.Alpha[bottomLeft]);
        Assert.Equal(new byte[] { 10, 20, 30 }, picture.Rgb.Skip(bottomLeft * 3).Take(3).ToArray());
        // Above the square (y 56 mm) and left of the red corner: clear.
        var clear = 7 * 40 + 5;
        Assert.Equal(0, picture.Alpha[clear]);
        // The red corner's top-right pixel.
        Assert.Equal(200, picture.Rgb[(0 * 40 + 39) * 3]);
        Assert.Equal(255, picture.Alpha[39]);
    }

    [Fact]
    public void ABigMap_IsCappedInPixels_AndKeepsItsBox()
    {
        var tris = new List<DaylightPrint.Tri>
        {
            new DaylightPrint.Tri { X0 = 0, Y0 = 0, X1 = 400, Y1 = 0, X2 = 400, Y2 = 250, R = 1, G = 2, B = 3 }
        };
        var raster = DaylightPrint.Rasterise(tris, 10.0, 100_000);
        Assert.True(raster.Picture.Width * raster.Picture.Height <= 100_000);
        Assert.Equal(0, raster.X0, 6);
        Assert.InRange(raster.X1, 399.0, 402.0);
        Assert.InRange(raster.Y1, 249.0, 252.0);
        Assert.Null(DaylightPrint.Rasterise(new List<DaylightPrint.Tri>(), 6));
    }

    [Fact]
    public void ARoomsLabel_ReadsItsMean()
    {
        Assert.Equal("DF 2.1 %", DaylightPrint.RoomLabel(2.06));
        Assert.Equal("DF 0.0 %", DaylightPrint.RoomLabel(0));
    }

    [Fact]
    public void AnUnderImage_IsDrawnBeforeTheLines_AndAnOverOneAfter()
    {
        var picture = new OfficeLogo.Picture { Width = 1, Height = 1, Rgb = new byte[] { 1, 2, 3 } };
        var page = new SheetPdf.Page();
        page.Strokes.Add(new SheetPdf.Stroke { Points = { new RoomDetect.Pt(0, 0), new RoomDetect.Pt(10, 0) }, WidthMm = 0.25 });
        page.Images.Add(new SheetPdf.Image { Picture = picture, X0 = 0, Y0 = 0, X1 = 10, Y1 = 10 });
        page.Images.Add(new SheetPdf.Image { Picture = picture, X0 = 0, Y0 = 0, X1 = 10, Y1 = 10, Under = true });
        var unmapped = 0;
        var ops = System.Text.Encoding.ASCII.GetString(SheetPdf.Content(page, ref unmapped));
        var under = ops.IndexOf("/Im1 Do", StringComparison.Ordinal);
        var line = ops.IndexOf("\nS\n", StringComparison.Ordinal);
        var over = ops.IndexOf("/Im0 Do", StringComparison.Ordinal);
        Assert.True(under >= 0 && line > under && over > line, ops);
    }
}
