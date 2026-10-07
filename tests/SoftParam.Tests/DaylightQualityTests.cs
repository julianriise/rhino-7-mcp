using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Daylight quality: low is the 400 mm grid, and each step up splits every
/// tile into four (half the cell side). The ⋯ card names the saved one.
/// </summary>
public class DaylightQualityTests
{
    [Fact]
    public void EachStep_SplitsEveryTileIntoFour()
    {
        Assert.Equal(400.0, DaylightQuality.CellMm("low"));
        Assert.Equal(200.0, DaylightQuality.CellMm("medium"));
        Assert.Equal(100.0, DaylightQuality.CellMm("high"));
        // Each step quarters the tile area.
        Assert.Equal(4.0, System.Math.Pow(DaylightQuality.CellMm("low") / DaylightQuality.CellMm("medium"), 2));
        Assert.Equal(4.0, System.Math.Pow(DaylightQuality.CellMm("medium") / DaylightQuality.CellMm("high"), 2));
    }

    [Fact]
    public void UnknownOrEmpty_IsLow()
    {
        Assert.Equal("low", DaylightQuality.Normal(null));
        Assert.Equal("low", DaylightQuality.Normal(""));
        Assert.Equal("low", DaylightQuality.Normal("ultra"));
        Assert.Equal("high", DaylightQuality.Normal(" High "));
        Assert.Equal(400.0, DaylightQuality.CellMm("ultra"));
    }

    [Fact]
    public void TheCard_ListsLowMediumHigh_AndNamesTheSavedOne()
    {
        var facts = Docs.Facts("house");
        var card = ForskCards.For("daylight.quality", facts)!;
        Assert.Equal(new[] { "low", "medium", "high", "done" }, card.Pills.Select(p => p.Id));
        Assert.Equal(new[] { "Low", "Medium", "High", "Confirm" }, card.Pills.Select(p => p.Label));
        Assert.Equal("Now: Low.", card.Note);

        facts.DaylightQuality = "medium";
        Assert.Equal("Now: Medium.", ForskCards.For("daylight.quality", facts)!.Note);
        Assert.Equal(ForskRole.Analyser, ForskRoles.OfAction("daylight.quality"));
    }

    /// <summary>
    /// The card fills the saved quality's pill, not the first one: with Medium
    /// saved, a filled Low read as "it went back to Low" (Julian, 2026-10-07).
    /// </summary>
    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void TheCard_FillsTheSavedQuality(string saved)
    {
        var facts = Docs.Facts("house");
        facts.DaylightQuality = saved;
        var card = ForskCards.Quality(facts);
        Assert.Equal(new[] { saved }, card.Pills.Where(p => p.Primary).Select(p => p.Id));
        var json = new DocThread().AddCard(card, facts);
        Assert.Equal(saved, json["pills"]!.Single(p => p["primary"]?.Value<bool>() == true)["id"]!.ToString());
    }

    /// <summary>The page fills the pill the card names, and the first one only when it names none.</summary>
    [Fact]
    public void ThePage_FillsTheNamedPill()
    {
        var engine = PageScript.Load();
        Assert.Equal("false,true,false", engine.Evaluate("var ps = [{id:'low'},{id:'medium',primary:true},{id:'high'}]; ps.map(function (p, i) { return Forsk.isPrimaryPill(ps, i); }).join(',')").ToString());
        Assert.Equal("true,false", engine.Evaluate("var qs = [{id:'a'},{id:'b'}]; qs.map(function (p, i) { return Forsk.isPrimaryPill(qs, i); }).join(',')").ToString());
    }
}
