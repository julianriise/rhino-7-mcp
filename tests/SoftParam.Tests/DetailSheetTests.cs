using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D4 detail sheets on the detail garage: the drawings shelf-packed per
/// scale into the 400 × 254 area, a detail's drawings in one row, the sheet
/// numbers A-50-00n and the plan callouts that point at them.
/// </summary>
public class DetailSheetTests
{
    static List<DetailSheet.Sheet> Sheets(IfcExport.Model model, params (string Wall, string Opening)[] picks)
    {
        var facts = picks.Select((p, i) => Details.Resolve(
            new Details.Record { Id = "DET" + (i + 1).ToString("00"), Wall = p.Wall, Opening = p.Opening }, model, DetailFixtures.Tol)).ToList();
        return DetailSheet.Plan(DetailSheet.Items(facts));
    }

    static double Right(Details.Placed p, int scale) => p.X - Details.BandMm + DetailSheet.BoxWidth(p.Drawing, scale);

    [Fact]
    public void TheBrokenSouthWall_ThenTheDoor_TakeTwoSheetsAt1To20()
    {
        var sheets = Sheets(DetailFixtures.Garage(window: true), ("w01", null), (null, "o-door"));
        Assert.Equal(new[] { "detail_20_1", "detail_20_2" }, sheets.Select(s => s.Id));
        var ids = sheets.Select(s => s.Id).ToList();
        Assert.Equal(new[] { "A-50-001", "A-50-002" }, ids.Select(id => DetailSheet.Number(id, ids)));

        var wall = sheets[0].Drawings;
        Assert.Equal(new[] { 1, 2 }, wall.Select(p => p.Number));
        Assert.Equal(new[] { Details.Plan, Details.Cut }, wall.Select(p => p.Drawing.View));
        Assert.Equal(new[] { 262.5, 80.0 }, wall.Select(p => DetailSheet.BoxWidth(p.Drawing, 20)));

        var door = sheets[1].Drawings;
        Assert.Equal(new[] { 1, 2, 3 }, door.Select(p => p.Number));
        Assert.Equal(new[] { Details.Plan, Details.Elevation, Details.Cut }, door.Select(p => p.Drawing.View));
        Assert.Equal(new[] { 135.0, 135.0, 80.0 }, door.Select(p => DetailSheet.BoxWidth(p.Drawing, 20)));
        Assert.Equal(new[] { 15.0, 160.0, 305.0 }, door.Select(p => p.X));
        Assert.Equal(370, Right(door[2], 20), 6);
        Assert.Equal(232, door.Max(p => DetailSheet.BoxHeight(p.Drawing, 20)), 6);
        // One row, its boxes' tops on the area's top.
        Assert.All(door, p => Assert.Equal(Details.AreaHeightMm,
            p.Y - Details.TitleBandMm - Details.BandMm + DetailSheet.BoxHeight(p.Drawing, 20), 6));
    }

    [Fact]
    public void TheCallouts_ReadThePlanDetailsNumber_OverItsSheet()
    {
        var callouts = DetailCallout.Callouts(Sheets(DetailFixtures.Garage(window: true), ("w01", null), (null, "o-door")));
        Assert.Equal(new[] { (1, "A-50-001"), (1, "A-50-002") }, callouts.Select(c => (c.Number, c.Sheet)));
        Assert.Equal(new[] { "DET01", "DET02" }, callouts.Select(c => c.Detail));
        // The wall's main run's middle on its centreline; the door's centre there.
        Assert.Equal((4000.0, 100.0), (callouts[0].Target.X, callouts[0].Target.Y));
        Assert.Equal((2000.0, 100.0), (callouts[1].Target.X, callouts[1].Target.Y));
    }

    [Fact]
    public void EachOpening_TakesASheet_ADoorAndAWindowTwo()
    {
        var sheets = Sheets(DetailFixtures.Garage(window: true), (null, "o-door"), (null, "o-window"));
        Assert.Equal(new[] { "detail_20_1", "detail_20_2" }, sheets.Select(s => s.Id));
        Assert.All(sheets, s => Assert.Equal(3, s.Drawings.Count));
    }

    [Fact]
    public void TwoEndWalls_AndBothOpenings_TakeFourSheetsAt1To20()
    {
        var sheets = Sheets(DetailFixtures.Garage(window: true), ("w03", null), ("w04", null), (null, "o-door"), (null, "o-window"));
        Assert.Equal(new[] { "detail_20_1", "detail_20_2", "detail_20_3", "detail_20_4" }, sheets.Select(s => s.Id));
        Assert.Equal(new[] { 2, 2, 3, 3 }, sheets.Select(s => s.Drawings.Count));
    }

    [Fact]
    public void ThePlacement_IsTheSameEveryRun()
    {
        string Layout() => string.Join(";", Sheets(DetailFixtures.Garage(window: true), ("w01", null), (null, "o-door"), (null, "o-window"))
            .SelectMany(s => s.Drawings.Select(p => s.Id + " " + p.Number + " " + p.Drawing.View + " " + p.X + " " + p.Y)));
        Assert.Equal(Layout(), Layout());
    }

    [Fact]
    public void Order_IsScaleAscendingThenN_AndNumbersFollowIt()
    {
        var ids = new[] { "detail_25_1", "detail_20_2", "nonsense", "detail_20_1", "DETAIL_20_1" };
        Assert.Equal(new[] { "detail_20_1", "detail_20_2", "detail_25_1" }, DetailSheet.Order(ids));
        Assert.Equal("A-50-003", DetailSheet.Number("detail_25_1", ids));
        Assert.Equal("", DetailSheet.Number("plan", ids));
    }

    [Fact]
    public void TheViewTitle_SitsInTheTitleBand_UnderItsDrawing()
    {
        var door = Sheets(DetailFixtures.Garage(), (null, "o-door"))[0].Drawings[0];
        var title = DetailSheet.TitleOf(door, 20);
        Assert.Equal(DetailSheet.TitleCircleMm / 2, title.Radius, 6);
        Assert.Equal(door.X - Details.BandMm + title.Radius, title.Circle.X, 6);
        Assert.InRange(title.Circle.Y, door.Y - Details.BandMm - Details.TitleBandMm, door.Y - Details.BandMm);
        Assert.Equal(door.X + door.Drawing.Width / 20, title.RuleTo.X, 6);
    }
}
