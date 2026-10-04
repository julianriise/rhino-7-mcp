using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D2 detail frames on the detail garage: the crop of each plan detail, the
/// cut and the scale ladder, pinned to the brief's numbers.
/// </summary>
public class DetailFrameTests
{
    [Fact]
    public void TheEndWall_PlanCrop_Is4600By800()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(), wall: "w03");
        var plan = Details.Frame(facts, Details.Plan);
        Assert.NotNull(plan);
        Assert.Equal(4600, plan.Width, 3);
        Assert.Equal(800, plan.Height, 3);
        Assert.Equal(20, Details.Scale(new[] { new SheetScale.Span(plan.Width, plan.Height) }));
    }

    [Fact]
    public void TheSouthWall_PlanCrop_Runs300PastEachEndAndFace()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(), wall: "w01");
        var plan = Details.Frame(facts, Details.Plan);
        Assert.Equal(-300, plan.U0, 3);
        Assert.Equal(8300, plan.U1, 3);
        Assert.Equal(-300, plan.V0, 3);
        Assert.Equal(500, plan.V1, 3);
        Assert.Equal(1200, plan.CutZ, 3);
        Assert.Equal("South wall — Plan", plan.Title);
        var mark = Assert.Single(plan.Marks);
        Assert.Equal(Details.Cut, mark.View);
        Assert.Equal(5225, mark.At.X, 3);
        Assert.True(mark.At.Y < 0, "the section mark sits outside the outer (south) face");
    }

    [Fact]
    public void TheDoor_PlanCrop_IsItsWidthPlus600EachSide_ByTheWallPlus400()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(), opening: "o-door");
        var plan = Details.Frame(facts, Details.Plan);
        Assert.Equal(950, plan.U0, 3);
        Assert.Equal(3050, plan.U1, 3);
        Assert.Equal(1000, plan.Height, 3);
        Assert.Equal("Door D01 — Plan", plan.Title);
        Assert.Equal(new[] { Details.Elevation, Details.Cut }, plan.Marks.Select(m => m.View));
        Assert.True(plan.Marks[0].At.Y < 0 && plan.Marks[0].Look.Y > 0, "the elevation arrow is outside, looking in");
        Assert.True(plan.Marks[1].At.Y > 200, "the section mark is inside");
    }

    [Fact]
    public void Scale_PinsTheBriefsSteps()
    {
        var door = Details.Frame(DetailFixtures.Facts(DetailFixtures.Garage(), opening: "o-door"), Details.Plan);
        // The door's section crop (thickness + 2 × 400 by slab − 200 to wall top + 200) is 1000 × 3800.
        Assert.Equal(20, Details.Scale(new[] { new SheetScale.Span(door.Width, door.Height), new SheetScale.Span(1000, 3800) }));
        Assert.Equal(25, Details.Scale(new[] { new SheetScale.Span(8600, 800) }));
        Assert.Equal(20, Details.Scale(new[] { new SheetScale.Span(4600, 800) }));
        Assert.Equal(20, Details.Scale(new[] { new SheetScale.Span(1000, 4240) }));
        Assert.Equal(25, Details.Scale(new[] { new SheetScale.Span(1000, 4300) }));
        Assert.Equal(370, Details.DrawWidthMm, 6);
        Assert.Equal(212, Details.DrawHeightMm, 6);
    }

    [Fact]
    public void ASheet_HasItsId_PageAndLayer_AndReadsBackFromThePage()
    {
        Assert.Equal("detail_20_1", Details.SheetId(20, 1));
        Assert.True(Details.TrySheetId("detail_20_1", out var scale, out var n));
        Assert.Equal((20, 1), (scale, n));
        Assert.False(Details.TrySheetId("detail_30_1", out _, out _));
        Assert.False(Details.TrySheetId("detail_20_0", out _, out _));
        Assert.False(Details.TrySheetId("section_a", out _, out _));
        Assert.Equal("Forsk — Details 1:20", Details.PageName(20, 1));
        Assert.Equal("Forsk — Details 1:20 (2)", Details.PageName(20, 2));
        Assert.Equal("Details 20-1", Details.LayerName(20, 1));
        Assert.Equal("Detaljer 1:20", Details.SheetTitle(20, true));
        Assert.True(Details.TryPage("Forsk — Details 1:20 (2)", out scale, out n));
        Assert.Equal((20, 2), (scale, n));
        Assert.True(Details.TryPage("Forsk — Details 1:25", out scale, out n));
        Assert.Equal((25, 1), (scale, n));
        Assert.False(Details.TryPage("Forsk — Plan", out _, out _));
        Assert.False(Details.TryPage("Forsk — Details 1:30", out _, out _));
    }

    [Fact]
    public void EachSheet_HasItsOwnModelRegion()
    {
        var a = Details.SheetOrigin(20, 1);
        var b = Details.SheetOrigin(20, 2);
        var c = Details.SheetOrigin(50, 1);
        Assert.True(b.X - a.X >= Details.AreaWidthMm * 50);
        Assert.True(Math.Abs(c.X - b.X) >= Details.AreaWidthMm * 50);
        Assert.Equal(-60000, a.Y, 6);
    }

    [Fact]
    public void TheDoorsPlan_InARow_StandsOnTheTitleBand()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(), opening: "o-door");
        var placed = Details.Row(Details.Drawings(facts), 20);
        var plan = Assert.Single(placed);
        Assert.Equal(1, plan.Number);
        Assert.Equal(Details.BandMm, plan.X, 6);
        Assert.Equal(Details.TitleBandMm + Details.BandMm, plan.Y, 6);
    }

    [Fact]
    public void Frame_IsNullForAViewTheDetailHasNot()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(), wall: "w01");
        Assert.Null(Details.Frame(facts, Details.Elevation));
    }
}
