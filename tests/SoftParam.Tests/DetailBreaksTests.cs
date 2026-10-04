using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D2b break lines on the detail garage: the 8 m south wall stays at 1:20
/// with one break in its plain middle, its values stay true and the ones
/// across the break are underlined; an end wall and a wall with no plain
/// stretch do not break.
/// </summary>
public class DetailBreaksTests
{
    static Details.Drawing SouthPlan(out Details.Facts facts)
    {
        facts = DetailFixtures.Facts(DetailFixtures.Garage(), wall: "w01");
        return Details.Frame(facts, Details.Plan);
    }

    [Fact]
    public void TheSouthWall_BreaksOnce_AndStaysAt1To20()
    {
        var plan = SouthPlan(out _);
        Assert.Equal(-300, plan.U0, 6);
        Assert.Equal(8300, plan.U1, 6);
        var b = Assert.Single(plan.Breaks.Breaks);
        Assert.Equal(3050, b.U0, 6);
        Assert.Equal(7200, b.U1, 6);
        Assert.Equal(4150, b.U1 - b.U0, 6);
        Assert.Equal(4650, plan.Width, 6);
        Assert.Equal(232.5, plan.Width / 20, 6);
        Assert.Equal(20, Details.Scale(new[] { new SheetScale.Span(plan.Width, plan.Height) }));
        Assert.Equal(3850, plan.Map(7800), 6);
        Assert.False(plan.Breaks.TooLong);
    }

    [Fact]
    public void TheKeepZones_AreTheBriefs_AndTheShortGapIsNoCandidate()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(), wall: "w01");
        var zones = DetailBreaks.Zones(new double[] { 0, 8000, 200, 7800, 1550, 2450 }, -300, 8300);
        Assert.Equal(new[] { (-900.0, 800.0), (950.0, 3050.0), (7200.0, 8900.0) }, zones.Select(z => (z.Key, z.Value)));
        // 800 … 950 is 150: it stays.
        Assert.DoesNotContain(Details.Frame(facts, Details.Plan).Breaks.Breaks, x => x.U0 < 950);
    }

    [Fact]
    public void TheValues_StayTrue_AndOnlyThoseAcrossTheBreakAreUnderlined()
    {
        var plan = SouthPlan(out var facts);
        var chains = DetailDims.PlanChains(facts, plan);
        PlanDims.FixedChain Chain(string kind) => chains.Single(c => c.Kind == kind);
        Assert.Equal(new[] { 1550, 900, 5550 }, DetailDims.Values(Chain("outer")));
        Assert.Equal(new[] { 8000 }, DetailDims.Values(Chain("outer_overall")));
        Assert.Equal(new[] { 1350, 900, 5350 }, DetailDims.Values(Chain("inner")));
        Assert.Equal(new[] { 7600 }, DetailDims.Values(Chain("inner_overall")));
        var underlined = chains.Where(c => c.Underline != null)
            .SelectMany(c => DetailDims.Values(c).Where((_, i) => c.Underline[i]))
            .OrderBy(v => v);
        Assert.Equal(new[] { 5350, 5550, 7600, 8000 }, underlined);
        Assert.Equal(new double[] { 0, 1550, 2450, 4050 }, Chain("outer").Stops);
        var thickness = Chain("thickness");
        Assert.Equal(new[] { 200 }, DetailDims.Values(thickness));
        Assert.Equal(plan.Map(7500), thickness.Origin.X, 6);
        Assert.True(thickness.Origin.X > plan.Map(7200), "the thickness sits at the east end, past the break");
    }

    [Fact]
    public void LayoutFixed_PrintsTheTrueValues_AndMarksTheUnderlined()
    {
        var plan = SouthPlan(out var facts);
        var laid = PlanDims.LayoutFixed(DetailDims.PlanChains(facts, plan), 20, new List<PlanDims.Obstacle>(),
            new List<List<List<RoomDetect.Pt>>>(), text => 0.6 * PlanDims.TextMm * text.Length);
        var outer = laid.Single(c => c.Kind == "outer");
        Assert.True(outer.Placed);
        Assert.Equal(new[] { "1550", "900", "5550" }, outer.Texts.Select(t => t.Text));
        Assert.Equal(new[] { false, false, true }, outer.Texts.Select(t => t.Underline));
        Assert.Equal(8000, laid.Single(c => c.Kind == "outer_overall").Total);
    }

    [Fact]
    public void TheSouthWall_SectionCut_IsBesideTheBreak()
    {
        var plan = SouthPlan(out var facts);
        Assert.Equal(3050, Details.CutU(facts, plan.Breaks), 6);
        Assert.Equal(3050, Assert.Single(plan.Marks).At.X, 6);
    }

    [Fact]
    public void AnEndWall_DoesNotBreak()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(), wall: "w03");
        var plan = Details.Frame(facts, Details.Plan);
        Assert.Null(plan.Breaks);
        Assert.Equal(4600, plan.Width, 6);
    }

    [Fact]
    public void AWallWithNoPlainStretch_DoesNotBreak_AndStepsTo25()
    {
        // Six 1000 windows in an 8400 wall, centres 1400 apart from 700 (the brief's 1500 centres do not fit 8400).
        var model = DetailFixtures.Garage();
        model.Walls[0].Rings = DetailFixtures.Box(0, 0, 8400, 200);
        model.Openings.Clear();
        for (var i = 0; i < 6; i++)
            model.Openings.Add(new IfcExport.Opening
            {
                Id = "o" + i, Host = "w01", Kind = "window", Mark = "W0" + (i + 1),
                Centre = new RoomDetect.Pt(700 + 1400 * i, 100), Along = new RoomDetect.Pt(1, 0), Width = 1000, Sill = 900, Head = 2100
            });
        model.Walls[1].Rings = DetailFixtures.Box(0, 3800, 8400, 4000);
        model.Walls[3].Rings = DetailFixtures.Box(8200, 200, 8400, 3800);
        var facts = DetailFixtures.Facts(model, wall: "w01");
        var plan = Details.Frame(facts, Details.Plan);
        Assert.Null(plan.Breaks);
        Assert.Equal(9000, plan.Width, 6);
        Assert.True(DetailBreaks.Make(new double[] { 0, 8400, 200, 1200, 1600, 2600, 3000, 4000, 4400, 5400, 5800, 6800, 7200, 8200 },
            -300, 8700, 7400).TooLong);
        Assert.Equal(25, Details.Scale(new[] { new SheetScale.Span(plan.Width, plan.Height) }));
    }
}
