using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// AN.5 and AN.6, first cut: save the model as option A, B, …, and compare an
/// option with the model now, side by side: areas, daylight per room and
/// opening counts, with the better daylight marked.
/// </summary>
public class OptionCompareTests
{
    static List<Pt> Box(double w, double d) => new() { new(0, 0), new(w, 0), new(w, d), new(0, d) };

    static OptionSnapshot.Snapshot Model(double bedroomDepth, string df, int windows)
    {
        var s = new OptionSnapshot.Snapshot { Name = "now" };
        s.Rooms.Add(new OptionSnapshot.Room { Id = "R01", Name = "Bedroom", Outline = Box(4000, bedroomDepth) });
        s.Rooms.Add(new OptionSnapshot.Room { Id = "R02", Name = "Bath", Outline = Box(2000, 2000) });
        if (df != null) s.Analysis[Analysis.RoomsKey] = df;
        s.Openings.Add(new OptionSnapshot.Opening { Id = "d1", Kind = "door" });
        for (var i = 0; i < windows; i++) s.Openings.Add(new OptionSnapshot.Opening { Id = "w" + i, Kind = "window" });
        return s;
    }

    [Theory]
    [InlineData(new string[0], "A")]
    [InlineData(new[] { "A", "B" }, "C")]
    [InlineData(new[] { "A", "C" }, "B")]
    [InlineData(new[] { "a" }, "B")]
    public void ANewOption_TakesTheFirstFreeLetter(string[] taken, string expected)
    {
        Assert.Equal(expected, OptionCompare.NextName(taken));
    }

    [Fact]
    public void TheCompare_ListsAreasDaylightAndOpenings_AndMarksTheBetterDaylight()
    {
        var a = Model(5000, "R01=1.8;R02=0.4", 1);
        var now = Model(6000, "R01=2.3;R02=0.4", 2);
        var rows = OptionCompare.Rows(a, now);
        Assert.Equal(new[] { "Net area", "Bedroom", "Bath", "Bedroom daylight", "Bath daylight", "Doors", "Windows" }, rows.Select(r => r.Label));
        Assert.Equal(("24.0 m²", "28.0 m²", ""), (rows[0].Option, rows[0].Now, rows[0].Better));
        Assert.Equal(("20.0 m²", "24.0 m²"), (rows[1].Option, rows[1].Now));
        Assert.Equal(("1.8 %", "2.3 %", "now"), (rows[3].Option, rows[3].Now, rows[3].Better));
        Assert.Equal("", rows[4].Better);
        Assert.Equal(("1", "2"), (rows[6].Option, rows[6].Now));
    }

    [Fact]
    public void ARoomOnOneSideOnly_ShowsADash_AndDaylightNotRunIsLeftOut()
    {
        var a = Model(5000, null, 1);
        var now = Model(5000, null, 1);
        now.Rooms.Add(new OptionSnapshot.Room { Id = "R03", Name = "Office", Outline = Box(3000, 3000) });
        var rows = OptionCompare.Rows(a, now);
        Assert.DoesNotContain(rows, r => r.Label.EndsWith(" daylight"));
        var office = rows.Single(r => r.Label == "Office");
        Assert.Equal(("–", "9.0 m²"), (office.Option, office.Now));
    }

    [Fact]
    public void TheCard_SaysWhatChanged_AndOneLinePerRow()
    {
        var a = Model(5000, "R01=1.8", 1);
        a.Name = "A";
        var now = Model(6000, "R01=2.3", 1);
        var card = ForskCards.OptionCompare("A", OptionSnapshot.Compare(a, now).Summary(), OptionCompare.Rows(a, now));
        Assert.Equal("option.compare.done", card.Kind);
        Assert.Equal("Option A against now: 1 room changed, analysis settings changed.", card.Question);
        Assert.Contains("Bedroom daylight: A 1.8 % · now 2.3 % · now is better", card.Rows!);
        Assert.Contains("Net area: A 24.0 m² · now 28.0 m²", card.Rows!);
    }

    [Fact]
    public void SaveAndCompare_AreInTheAnalysesMenu()
    {
        var facts = Docs.Facts("house");
        facts.Saved = true;
        Assert.True(ForskRegistry.Find("option.save")!.Shows(facts));
        Assert.False(ForskRegistry.Find("option.compare")!.Shows(facts));
        facts.Options = new List<string> { "A", "B" };
        Assert.True(ForskRegistry.Find("option.compare")!.Shows(facts));
        Assert.Equal("Save as option", ForskRegistry.Find("option.save")!.Label);
        Assert.Equal("Compare options", ForskRegistry.Find("option.compare")!.Label);
        // The Analyses menu opens one Options card for the four option actions (UI unification).
        Assert.Contains("options", ForskCards.Analyser(facts).Pills.Select(p => p.Id));
        Assert.Contains("option.compare", ForskCards.For("options", facts)!.Pills.Select(p => p.Id));
        var pick = ForskCards.For("option.compare", facts)!;
        Assert.Equal(new[] { "B", "A", "cancel" }, pick.Pills.Select(p => p.Id));
    }

    [Fact]
    public void AnUnsavedFile_CannotSaveAnOption()
    {
        var facts = Docs.Facts("house");
        facts.Saved = false;
        Assert.False(ForskRegistry.Find("option.save")!.Shows(facts));
    }

    [Fact]
    public void TheCompareTool_ResultBecomesTheCard()
    {
        var envelope = Newtonsoft.Json.Linq.JObject.Parse(@"{""status"":""success"",""result"":{""name"":""B"",""summary"":""2 walls moved."",
            ""rows"":[{""label"":""Living daylight"",""option"":""2.0 %"",""now"":""1.5 %"",""better"":""option""}]}}");
        var card = ForskCards.OptionCompareFrom(envelope)!;
        Assert.Equal("Option B against now: 2 walls moved.", card.Question);
        Assert.Equal(new[] { "Living daylight: B 2.0 % · now 1.5 % · B is better" }, card.Rows);
        Assert.Null(ForskCards.OptionCompareFrom(Newtonsoft.Json.Linq.JObject.Parse(@"{""status"":""error"",""message"":""No option C.""}"))) ;
    }

    [Fact]
    public void RestoreAndDelete_PickAnOption_NewestFirst()
    {
        var facts = Docs.Facts("saved, two options");
        Assert.True(ForskRegistry.Find("option.restore")!.Shows(facts));
        Assert.True(ForskRegistry.Find("option.delete")!.Shows(facts));
        Assert.False(ForskRegistry.Find("option.restore")!.Shows(Docs.Facts("house")));
        Assert.Equal("Restore an option", ForskRegistry.Find("option.restore")!.Label);
        Assert.Equal("Delete an option", ForskRegistry.Find("option.delete")!.Label);
        var restore = ForskCards.For("option.restore", facts)!;
        Assert.Equal("option.restore", restore.Kind);
        Assert.Equal(new[] { "B", "A", "cancel" }, restore.Pills.Select(p => p.Id));
        Assert.Equal("Restore which option? The model goes back to it; one Undo brings it back.", restore.Question);
        Assert.Contains("option.restore", ForskCards.For("options", facts)!.Pills.Select(p => p.Id));
    }
}
