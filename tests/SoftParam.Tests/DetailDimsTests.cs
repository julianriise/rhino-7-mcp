using RhinoMCPPlugin.Functions;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D2 plan detail dimensions on the detail garage: both faces of the south
/// wall with stops at the door jambs, the thickness at its plain end, and
/// the door's width on both faces.
/// </summary>
public class DetailDimsTests
{
    static PlanDims.FixedChain Chain(List<PlanDims.FixedChain> chains, string kind) => chains.Single(c => c.Kind == kind);

    static List<PlanDims.FixedChain> Plan(string wall = null, string opening = null)
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(), wall, opening);
        return DetailDims.PlanChains(facts, Details.Frame(facts, Details.Plan));
    }

    [Fact]
    public void TheSouthWall_OuterRow1_Sums8000_WithStopsAtTheDoorJambs()
    {
        var chains = Plan(wall: "w01");
        var outer = Chain(chains, "outer");
        Assert.Equal(new[] { 1550, 900, 5550 }, DetailDims.Values(outer));
        Assert.Equal(8000, DetailDims.Values(outer).Sum());
        // Drawn across the plan's break (D2b): 8000 sits at 4050.
        Assert.Equal(new double[] { 0, 1550, 2450, 4050 }, outer.Stops);
        Assert.Equal(0, outer.Origin.Y, 3);
        Assert.Equal(-1, outer.Out.Y, 3);
        Assert.Equal(new[] { 8000 }, DetailDims.Values(Chain(chains, "outer_overall")));
        Assert.Equal(2, Chain(chains, "outer_overall").Row);
    }

    [Fact]
    public void TheSouthWall_InnerRow1_Sums7600()
    {
        var chains = Plan(wall: "w01");
        var inner = Chain(chains, "inner");
        Assert.Equal(new[] { 1350, 900, 5350 }, DetailDims.Values(inner));
        Assert.Equal(7600, DetailDims.Values(inner).Sum());
        Assert.Equal(200, inner.Origin.Y, 3);
        Assert.Equal(1, inner.Out.Y, 3);
        Assert.Equal(new[] { 7600 }, DetailDims.Values(Chain(chains, "inner_overall")));
    }

    [Fact]
    public void TheSouthWall_Thickness_Is200_AtTheEastEnd()
    {
        var thickness = Chain(Plan(wall: "w01"), "thickness");
        Assert.Equal(new[] { 200 }, DetailDims.Values(thickness));
        // 7500 along, drawn past the break at 3550.
        Assert.Equal(3550, thickness.Origin.X, 3);
    }

    [Fact]
    public void AnEndWall_WithoutOpenings_HasOneRowPerFace()
    {
        var chains = Plan(wall: "w03");
        Assert.Equal(new[] { 4000 }, DetailDims.Values(Chain(chains, "outer")));
        Assert.Equal(new[] { 3600 }, DetailDims.Values(Chain(chains, "inner")));
        Assert.DoesNotContain(chains, c => c.Row == 2);
    }

    [Fact]
    public void LayoutFixed_PlacesEveryChain_Row2PastRow1_WithoutWitnessLines()
    {
        var chains = PlanDims.LayoutFixed(Plan(wall: "w01"), 20, new List<PlanDims.Obstacle>(),
            new List<List<List<Pt>>> { DetailFixtures.Box(0, 0, 8000, 200) }, text => 0.6 * PlanDims.TextMm * text.Length);
        Assert.All(chains, c => Assert.True(c.Placed, c.Kind));
        Assert.All(chains, c => Assert.Single(c.Lines));
        var outer = chains.Single(c => c.Kind == "outer");
        var overall = chains.Single(c => c.Kind == "outer_overall");
        Assert.Equal(PlanDims.FirstMm * 20, outer.Offset, 6);
        Assert.True(overall.Offset >= outer.Offset + PlanDims.FirstMm * 20 - 1e-6);
        Assert.Equal(new[] { "1550", "900", "5550" }, outer.Texts.Select(t => t.Text));
        Assert.All(outer.Lines.Concat(overall.Lines), l => Assert.True(l.A.Y < 0 && l.B.Y < 0, "outer rows lie outside the south face"));
        Assert.Equal(new[] { "8000" }, overall.Texts.Select(t => t.Text));
        var thickness = chains.Single(c => c.Kind == "thickness");
        Assert.Equal("200", Assert.Single(thickness.Texts).Text);
    }

    [Fact]
    public void TheDoor_Reads900OnBothFaces_And200Across()
    {
        var chains = Plan(opening: "o-door");
        Assert.Equal(new[] { 900 }, DetailDims.Values(Chain(chains, "outer")));
        Assert.Equal(new[] { 900 }, DetailDims.Values(Chain(chains, "inner")));
        Assert.Equal(new[] { 200 }, DetailDims.Values(Chain(chains, "thickness")));
        Assert.DoesNotContain(chains, c => c.Row == 2);
    }

    static List<PlanDims.FixedChain> Of(string view, string wall = null, string opening = null, bool roof = false)
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(window: true, roof: roof), wall, opening);
        return DetailDims.Chains(facts, Details.Frame(facts, view));
    }

    static int[] V(List<PlanDims.FixedChain> chains, string kind) => DetailDims.Values(Chain(chains, kind)).ToArray();

    [Fact]
    public void TheWallSection_Reads200Across_400And3000_Then3400()
    {
        var chains = Of(Details.Cut, wall: "w01");
        Assert.Equal(new[] { 200 }, V(chains, "thickness"));
        Assert.Equal(new[] { 400, 3000 }, V(chains, "height"));
        Assert.Equal(new[] { 3400 }, V(chains, "height_overall"));
    }

    [Fact]
    public void TheDoorElevation_Reads900_And2100_WithNoSillStop()
    {
        var chains = Of(Details.Elevation, opening: "o-door");
        Assert.Equal(new[] { 900 }, V(chains, "width"));
        Assert.Equal(new[] { 2100 }, V(chains, "height"));
        Assert.DoesNotContain(chains, c => c.Kind == "height_overall");
    }

    [Fact]
    public void TheWindowElevation_Reads900And1200_Then2100()
    {
        var chains = Of(Details.Elevation, opening: "o-window");
        Assert.Equal(new[] { 1200 }, V(chains, "width"));
        Assert.Equal(new[] { 900, 1200 }, V(chains, "height"));
        Assert.Equal(new[] { 2100 }, V(chains, "height_overall"));
    }

    [Fact]
    public void TheWindowSection_Reads200Across_WithNoRevealStop_AndItsHeights()
    {
        var chains = Of(Details.Cut, opening: "o-window");
        Assert.Equal(new[] { 200 }, V(chains, "thickness"));
        Assert.Equal(new[] { 400, 900, 1200, 900 }, V(chains, "height"));
        Assert.Equal(new[] { 3400 }, V(chains, "height_overall"));
    }

    [Fact]
    public void TheDoorSection_HasNoSillStop()
    {
        var chains = Of(Details.Cut, opening: "o-door");
        Assert.Equal(new[] { 400, 2100, 900 }, V(chains, "height"));
        Assert.Equal(new[] { 3400 }, V(chains, "height_overall"));
    }

    [Fact]
    public void WithARoof_TheWindowSectionRow1_EndsWithTheRoof()
    {
        var chains = Of(Details.Cut, opening: "o-window", roof: true);
        Assert.Equal(new[] { 400, 900, 1200, 900, 300 }, V(chains, "height"));
        Assert.Equal(new[] { 3400 }, V(chains, "height_overall"));
    }

    [Fact]
    public void TheLevelMarks_StandAwayFromTheHeights()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(window: true), opening: "o-window");
        var cut = Details.Frame(facts, Details.Cut);
        var levels = DetailDims.Levels(facts, cut);
        Assert.Equal(new[] { "±0", "+900", "+2100" }, levels.Select(l => l.Text));
        var heights = Chain(DetailDims.Chains(facts, cut), "height");
        Assert.All(levels, l => Assert.Equal(heights.Out.X > 0 ? cut.U0 : cut.U1, l.U, 6));
        Assert.All(levels, l => Assert.Equal(heights.Out.X > 0 ? -1 : 1, l.Side));
        var wall = DetailFixtures.Facts(DetailFixtures.Garage(), wall: "w01");
        Assert.Equal(new[] { "±0", "+3000" }, DetailDims.Levels(wall, Details.Frame(wall, Details.Cut)).Select(l => l.Text));
        var door = DetailFixtures.Facts(DetailFixtures.Garage(), opening: "o-door");
        Assert.Equal(new[] { "±0", "+2100" }, DetailDims.Levels(door, Details.Frame(door, Details.Cut)).Select(l => l.Text));
        Assert.Equal("-400", DetailDims.LevelText(-400));
    }
}
