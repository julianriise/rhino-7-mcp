using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// UI/UX unification, pass 1 (forsk docs/UI_UNIFICATION.md): one home per
/// action, More actions grouped by the work, and one Options entry for the
/// four option actions.
/// </summary>
public class UnifiedUiTests
{
    static string[] Groups(FileFacts f) => ForskRegistry.Card(f).Groups.Select(g => g.Title).ToArray();
    static string[] Ids(FileFacts f) => ForskRegistry.Card(f).Actions.Select(a => a.Id).ToArray();

    [Fact]
    public void MoreActions_IsGroupedByTheWork()
    {
        Assert.Equal(new[] { "Model and edit", "Rooms", "Analyses", "Print and export" }, Groups(Docs.Facts("house")));
        Assert.Equal(new[] { "Start a project", "Model and edit" }, Groups(Docs.Facts("empty")));
    }

    [Theory]
    [InlineData("ink.set")]
    [InlineData("daylight.quality")]
    [InlineData("meta.title")]
    [InlineData("bridge.start")]
    public void ASettingLivesInSettings_NotInMoreActions(string id)
    {
        foreach (var name in new[] { "house", "bridge down, grey ink", "empty" })
            Assert.DoesNotContain(id, Ids(Docs.Facts(name)));
    }

    [Fact]
    public void Analyses_HoldsDaylightAreasTheSetAndOptions()
    {
        var facts = Docs.Facts("saved, two options");
        var analyses = ForskRegistry.Card(facts).Groups.Single(g => g.Title == "Analyses").Actions.Select(a => a.Id).ToArray();
        Assert.Equal(new[] { "analysis.menu", "daylight.run", "area.stats", "analysis.print", "options" }, analyses);
        Assert.DoesNotContain("area.stats", ForskRegistry.Card(facts).Groups.Single(g => g.Title == "Rooms").Actions.Select(a => a.Id));
    }

    [Fact]
    public void Options_IsOneEntry_ThatOpensTheFourOptionActions()
    {
        var facts = Docs.Facts("saved, two options");
        Assert.Equal("Options", ForskRegistry.Find("options")!.Label);
        var card = ForskCards.For("options", facts)!;
        Assert.Equal(new[] { "option.save", "option.compare", "option.restore", "option.delete", "done" }, card.Pills.Select(p => p.Id));
        Assert.False(ForskRegistry.Find("options")!.Shows(Docs.Facts("house")));
        var saved = Docs.Facts("house");
        saved.Saved = true;
        Assert.Equal(new[] { "option.save", "done" }, ForskCards.For("options", saved)!.Pills.Select(p => p.Id));
    }

    [Fact]
    public void TheAnalysesMenu_ShowsAtMostFourActions()
    {
        var pills = ForskCards.Analyser(Docs.Facts("saved, two options")).Pills.Select(p => p.Id).ToArray();
        Assert.Equal(new[] { "daylight.run", "area.stats", "analysis.print", "options", "done" }, pills);
    }

    /// <summary>Pass 3: the Analyses menu (Live switches, last results) opens from More actions in every role, not only from the Analyser's face.</summary>
    [Fact]
    public void TheAnalysesMenu_OpensFromMoreActions_InEveryRole()
    {
        var house = Docs.Facts("house");
        Assert.Equal("Analyses menu", ForskRegistry.Find("analysis.menu")!.Label);
        Assert.Equal("analysis.menu", ForskRegistry.Card(house).Groups.Single(g => g.Title == "Analyses").Actions[0].Id);
        var card = ForskCards.For("analysis.menu", house)!;
        Assert.Equal("analyser", card.Kind);
        Assert.Contains(card.Fields!, f => f.Key == "live.daylight");
        Assert.False(ForskRegistry.Find("analysis.menu")!.Shows(Docs.Facts("empty")));
    }
}
