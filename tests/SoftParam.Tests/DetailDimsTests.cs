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
        Assert.Equal(new double[] { 0, 1550, 2450, 8000 }, outer.Stops);
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
        Assert.Equal(7500, thickness.Origin.X, 3);
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
}
