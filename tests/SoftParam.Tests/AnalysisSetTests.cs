using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// AN.3 and AN.4: the Analysis set beside the Sheets set. Each analysis has
/// its own numbered table sheet with the same title block; an analysis is in
/// the set once the user adds it or ticks it, and the choice is on the file.
/// </summary>
public class AnalysisSetTests
{
    static Analysis.Room R(string id, string name) => new Analysis.Room { Id = id, Name = name };

    static Analysis.State Stored(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value);
        return Analysis.Read(k => map.TryGetValue(k, out var v) ? v : null);
    }

    [Fact]
    public void EachAnalysis_HasItsOwnNumberedSheet_WithATitle()
    {
        Assert.Equal("analysis_daylight", Analysis.SheetId(Analysis.Daylight));
        Assert.Equal("analysis_areas", Analysis.SheetId(Analysis.Areas));
        Assert.Equal("A-80-001", SheetSet.Number("analysis_daylight", 0));
        Assert.Equal("A-80-002", SheetSet.Number("analysis_areas", 3));
        Assert.Equal("Daylight", SheetSet.Title("analysis_daylight", 0));
        Assert.Equal("Dagslys", SheetSet.Title("analysis_daylight", 0, null, true));
        Assert.Equal("Areas", SheetSet.Title("analysis_areas", 0));
        Assert.Equal("Arealer", SheetSet.Title("analysis_areas", 0, null, true));
    }

    [Fact]
    public void AnAnalysisSheet_IsATableSheet_OutsideTheSheetsSet()
    {
        Assert.True(SheetSet.IsListSheet("analysis_daylight"));
        Assert.True(SheetSet.IsAnalysisSheet("analysis_areas"));
        Assert.False(SheetSet.IsAnalysisSheet("front"));
        Assert.False(SheetSet.IsAnalysisSheet("plan"));
        Assert.True(Analysis.TryAnalysis("analysis_daylight", out var id));
        Assert.Equal(Analysis.Daylight, id);
        Assert.False(Analysis.TryAnalysis("analysis_wind", out _));
        var facts = new SheetSet.SetFacts { Walls = true, Lists = { "room" } };
        Assert.DoesNotContain(SheetSet.Infer(facts), s => SheetSet.IsAnalysisSheet(s.Id));
    }

    [Fact]
    public void AFreshFile_HasAnEmptyAnalysisSet()
    {
        var state = Analysis.Read(_ => null);
        Assert.False(state.InSet(Analysis.Daylight));
        Assert.False(state.InSet(Analysis.Areas));
        Assert.DoesNotContain(Analysis.SetSheets(state), s => s.On);
        Assert.Equal(new[] { "analysis_daylight", "analysis_areas" }, Analysis.SetSheets(state).Select(s => s.Id));
    }

    [Fact]
    public void TheSetChoice_ReadsBackFromTheFile_InTheMenusOrder()
    {
        var state = Stored(("set.areas", "1"), ("set.daylight", "0"));
        Assert.True(state.InSet(Analysis.Areas));
        Assert.False(state.InSet(Analysis.Daylight));
        Assert.Equal("set.daylight", Analysis.SetKey(Analysis.Daylight));
        Assert.Equal(new[] { "analysis_areas" }, Analysis.SetSheets(state).Where(s => s.On).Select(s => s.Id));
    }

    [Theory]
    [InlineData("daylight.run", "daylight")]
    [InlineData("daylight.again", "daylight")]
    [InlineData("daylight.room", "daylight")]
    [InlineData("area.stats", "areas")]
    [InlineData("daylight.hide", null)]
    [InlineData("file.print", null)]
    [InlineData(null, null)]
    public void TheAnalysisAJobRan_IsKnownByItsActionId(string kind, string expected)
    {
        Assert.Equal(expected, Analysis.JustRan(kind));
    }

    [Fact]
    public void TheDaylightSheet_ListsEachRoomsMean_AndTheLastRunUnderIt()
    {
        var state = Stored(("last.daylight", Analysis.DaylightLast(2.05, 2)), (Analysis.RoomsKey, "R01=1.8;R02=2.3"));
        var table = Analysis.DaylightTable(new[] { R("R01", "Bedroom"), R("R02", "Living"), R("R03", "Bath") }, state);
        Assert.Equal("daylight", table.Kind);
        Assert.Equal("Daylight factor", table.Title);
        Assert.Equal(new[] { "Room", "DF mean" }, table.Heads);
        Assert.Equal(new[] { "R01", "R02", "R03" }, table.Ids);
        Assert.Equal(new[] { "Bedroom", "1.8 %" }, table.Rows[0]);
        Assert.Equal(new[] { "Living", "2.3 %" }, table.Rows[1]);
        Assert.Equal(new[] { "Bath", "–" }, table.Rows[2]);
        Assert.Equal("DF mean 2.1 % in 2 rooms. A room with – has not been traced.", table.Note);
    }

    [Fact]
    public void TheDaylightSheet_SaysWhenDaylightHasNotRun()
    {
        var table = Analysis.DaylightTable(new[] { R("R01", "Bedroom") }, Analysis.Read(_ => null), true);
        Assert.Equal("Dagslysfaktor", table.Title);
        Assert.Equal(new[] { "Rom", "DF snitt" }, table.Heads);
        Assert.Equal(new[] { "Bedroom", "–" }, table.Rows[0]);
        Assert.Equal("Dagslys er ikke kjørt. Kjør Dagslys i Analyser-menyen.", table.Note);
    }

    [Fact]
    public void AfterDaylight_TheBarOffers_AddToAnalysisSet()
    {
        var ran = FileClassifier.Read(Docs.Of(Docs.House()).With(d => d.Analysed = "daylight"));
        Assert.Equal("daylight", ran.Analysed);
        Assert.Equal("analysis.add", ForskRegistry.Bar(ran).Context[0].Id);
        Assert.Equal("Add to analysis set", ForskRegistry.Find("analysis.add")!.Label);
        Assert.Equal(ForskRole.Analyser, ForskRoles.OfAction("analysis.add"));
        Assert.DoesNotContain(ForskRegistry.Bar(Docs.Facts("house")).Context, a => a.Id == "analysis.add");
    }

    [Fact]
    public void AnAnalysisAlreadyInTheSet_IsNotOfferedAgain()
    {
        var ran = FileClassifier.Read(Docs.Of(Docs.House()).With(d =>
        {
            d.Analysed = "areas";
            d.Analysis = Stored(("set.areas", "1"));
        }));
        Assert.False(ForskRegistry.Find("analysis.add")!.Shows(ran));
    }

    [Fact]
    public void TheAnalysesMenu_PrintsTheAnalysisSet()
    {
        var house = Docs.Facts("house");
        Assert.Contains(ForskCards.Analyser(house).Pills, p => p.Id == "analysis.print");
        Assert.True(ForskRegistry.Find("analysis.print")!.Shows(house));
        Assert.False(ForskRegistry.Find("analysis.print")!.Shows(Docs.Facts("empty")));
        Assert.Equal("Print analysis set", ForskRegistry.Find("analysis.print")!.Label);
    }

    [Fact]
    public void ChooseAnalyses_HasATickAndALinePerAnalysis_KeptOnTheFile()
    {
        var facts = Docs.Facts("house");
        facts.Analysis = Stored(("set.daylight", "1"));
        var card = ForskCards.AnalysisSet(facts);
        Assert.Equal("analysis.print", card.Kind);
        Assert.Equal(new[] { "set.daylight", "set.areas" }, card.Fields!.Select(f => f.Key));
        Assert.All(card.Fields!, f => Assert.True(f.Check));
        Assert.Equal(new[] { "1", "0" }, card.Fields!.Select(f => f.Value));
        Assert.Equal("Daylight: the daylight factor in each room, from the last run", card.Fields![0].Label);
        Assert.Equal("Areas: net, gross and usable area by floor and use", card.Fields![1].Label);
        Assert.Equal(new[] { "print", "save", "cancel" }, card.Pills.Select(p => p.Id));
        Assert.True(card.Pills[0].Primary);
    }

    [Fact]
    public void TheTicks_BecomeTheStoredChoice()
    {
        var values = new Newtonsoft.Json.Linq.JObject { ["set.daylight"] = "1", ["set.areas"] = "0", ["other"] = "1" };
        Assert.Equal(new[] { ("set.daylight", "1"), ("set.areas", "0") }, Analysis.SetChoice(k => values[k]?.ToString()));
    }

    [Fact]
    public void ThePrintedAnalysisSet_SaysSo_InOneLine()
    {
        Assert.Equal("✓ Printed the analysis set: 2 sheets on A3 · Holmen Analysis.pdf",
            ForskReceipt.AnalysisPrintLine(2, "/Users/a/Desktop/Holmen Analysis.pdf", "A3"));
        Assert.Equal("✓ Printed the analysis set: 1 sheet on A3 · a.pdf", ForskReceipt.AnalysisPrintLine(1, "a.pdf", null));
        Assert.Equal("Holmen Analysis.pdf", ForskReceipt.AnalysisPdfName("Holmen"));
        Assert.Equal("forsk-analysis.pdf", ForskReceipt.AnalysisPdfName(""));
    }

    [Theory]
    [InlineData("print the analysis set", true)]
    [InlineData("Print analysis set", true)]
    [InlineData("skriv ut analysene", true)]
    [InlineData("analysis pdf", true)]
    [InlineData("print", false)]
    [InlineData("run the analysis", false)]
    [InlineData("how do i print the analysis set", false)]
    public void TypedPrintOfTheAnalyses_IsTheAnalysisSet(string said, bool expected)
    {
        Assert.Equal(expected, ForskIntentRouter.AnalysisPrint(said));
    }
}
