using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// AN.1: the Analyser's face opens a card with the analyses the file can run
/// now. Each pill is the existing action's id, so it runs as the bar's pill.
/// </summary>
public class AnalyserCardTests
{
    static string[] Pills(string fixture) => ForskCards.Analyser(Docs.Facts(fixture)).Pills.Select(p => p.Id).ToArray();

    [Theory]
    [InlineData("house", new[] { "daylight.run", "area.stats", "done" })]
    [InlineData("map shown", new[] { "daylight.hide", "area.stats", "done" })]
    [InlineData("map hidden", new[] { "daylight.show", "area.stats", "done" })]
    [InlineData("map stale", new[] { "daylight.again", "area.stats", "done" })]
    [InlineData("walls only", new[] { "daylight.rooms", "done" })]
    public void TheCard_ListsTheAnalysesTheFileCanRun(string fixture, string[] expected)
    {
        Assert.Equal(expected, Pills(fixture));
    }

    [Fact]
    public void AnEmptyFile_SaysWhatComesFirst()
    {
        var card = ForskCards.Analyser(Docs.Facts("empty"));
        Assert.Equal(new[] { "done" }, card.Pills.Select(p => p.Id));
        Assert.Equal("Daylight and areas need walls and rooms. Generate 3D first.", card.Note);
        Assert.Equal("analyser", card.Kind);
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void EveryPill_IsARegistryActionTheFileShows_OrDone(string fixture)
    {
        var facts = Docs.Facts(fixture);
        foreach (var pill in ForskCards.Analyser(facts).Pills.Where(p => p.Id != "done"))
            Assert.True(ForskRegistry.Find(pill.Id)!.Shows(facts), fixture + ": " + pill.Id);
    }

    [Fact]
    public void TheMenu_SitsUnderTheTopBar_WithALastResultAndALiveSwitchPerAnalysis()
    {
        var facts = Docs.Facts("house");
        facts.Analysis.Last["daylight"] = "DF mean 2.1 % in 8 rooms";
        var card = ForskCards.Analyser(facts);
        Assert.True(card.Pin);
        Assert.Equal(new[] { "Daylight: DF mean 2.1 % in 8 rooms", "Areas: not run yet" }, card.Rows);
        Assert.Equal(new[] { "live.daylight", "live.areas" }, card.Fields!.Select(f => f.Key));
        Assert.All(card.Fields!, f => Assert.True(f.Check));
        Assert.Equal(new[] { "0", "1" }, card.Fields!.Select(f => f.Value));
        Assert.Equal("Daylight live", card.Fields![0].Label);
    }

    [Fact]
    public void APinnedCard_DrawsInThePanel_LikeAForm()
    {
        var engine = PageScript.Load();
        Assert.True(engine.Evaluate("Forsk.isForm({ role: 'card', state: 'open', pin: true })").ToString() == "true");
        Assert.False(engine.Evaluate("Forsk.isForm({ role: 'card', state: 'done', pin: true })").ToString() == "true");
    }

    [Fact]
    public void ThePage_SendsAnalyser_OnlyFromTheAnalysersFace()
    {
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("var tap = shown === 'analyser';", js);
        Assert.Contains("sender.send({ kind: 'analyser' })", js);
    }
}
