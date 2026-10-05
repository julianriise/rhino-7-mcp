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
        Assert.Equal(new[] { "low", "medium", "high" }, card.Pills.Select(p => p.Id));
        Assert.Equal(new[] { "Low", "Medium", "High" }, card.Pills.Select(p => p.Label));
        Assert.Equal("Now: Low.", card.Note);

        facts.DaylightQuality = "medium";
        Assert.Equal("Now: Medium.", ForskCards.For("daylight.quality", facts)!.Note);
        Assert.Equal(ForskRole.Analyser, ForskRoles.OfAction("daylight.quality"));
    }
}
