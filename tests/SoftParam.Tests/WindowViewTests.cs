using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D1: the view model the window draws, on the F1.1 fixtures. The pinned bar
/// replaces the chips: slot 1 from the file, two contextual slots, then "?".
/// </summary>
public class WindowViewTests
{
    static JObject View(string fixture, DocThread? thread = null, bool help = false) =>
        WindowView.Build(thread ?? new DocThread { Serial = 1, File = "holmen.3dm" }, Docs.Facts(fixture), help);

    static string[] Slots(JObject view) => ((JArray)view["bar"]!["slots"]!).Select(s => s["id"]!.ToString()).ToArray();

    [Fact]
    public void ANewMillimetreFile_HeaderOneLocalSentence_ImportDrawAndHelp()
    {
        var view = View("empty");
        Assert.Equal("holmen.3dm", view["file"]!.ToString());
        var thread = (JArray)view["thread"]!;
        Assert.Single(thread);
        Assert.Equal("This file is empty.", thread[0]["text"]!.ToString());
        Assert.Equal(new[] { "file.import", "file.draw" }, Slots(view));
        Assert.Equal("?", view["bar"]!["help"]!["label"]!.ToString());
        Assert.Equal("", view["status"]!.ToString());
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void TheViewModelsBar_IsTheRegistrysBar(string fixture)
    {
        var facts = Docs.Facts(fixture);
        var expected = ForskRegistry.Bar(facts).Slots.Select(a => a.Id).ToArray();
        Assert.Equal(expected, Slots(View(fixture)));
        var keys = ((JArray)View(fixture)["bar"]!["slots"]!).Select(s => s["key"]!.ToString()).ToArray();
        Assert.Equal(Enumerable.Range(1, expected.Length).Select(i => "⌘" + i), keys);
    }

    [Fact]
    public void ADoorSelected_PrintStaysSlot1_MoveAndResizePrefill_DeleteNotInTheFirstTwo()
    {
        var view = View("house, door selected");
        var slots = (JArray)view["bar"]!["slots"]!;
        Assert.Equal("Print PDF", slots[0]["label"]!.ToString());
        Assert.Equal(new[] { "prefill", "prefill" }, slots.Skip(1).Select(s => s["runs"]!.ToString()));
        Assert.DoesNotContain(slots.Take(2), s => s["label"]!.ToString() == "Delete");
    }

    [Theory]
    [InlineData("empty, no key")]
    [InlineData("house, no key")]
    [InlineData("house, door selected, no key")]
    [InlineData("house, wall selected, no key")]
    [InlineData("rooms, no window, no key")]
    public void NoKey_NoAskActionInTheBarOrOnTheCard(string fixture)
    {
        var view = View(fixture, help: true);
        Assert.DoesNotContain((JArray)view["bar"]!["slots"]!, s => s["runs"]!.ToString() == "ask");
        var card = view["help"]!["groups"]!.SelectMany(g => g["actions"]!);
        Assert.DoesNotContain(card, a => a["runs"]!.ToString() == "ask");
        Assert.Contains(view["help"]!["hints"]!, h => h.ToString().StartsWith("Chat actions are hidden"));
    }

    [Fact]
    public void UndoIsAbsent_AfterAnOutsideEdit()
    {
        var tracker = new LastActionTracker();
        tracker.Record(new LastAction { Kind = "answer", Doc = 1, Record = "Forsk: move the north wall 500 mm north", Undoable = true });
        DocInput Input() => Docs.Of(Docs.House()).With(d => d.UndoNewest = tracker.UndoNewest(1));

        var before = WindowView.Build(new DocThread { Serial = 1 }, FileClassifier.Read(Input()));
        Assert.Equal("edit.undo", Slots(before)[1]);

        tracker.ObjectChanged(insideForskCall: false);
        var after = WindowView.Build(new DocThread { Serial = 1 }, FileClassifier.Read(Input()), helpOpen: true);
        Assert.DoesNotContain("edit.undo", Slots(after));
        Assert.DoesNotContain(after["help"]!["groups"]!.SelectMany(g => g["actions"]!), a => a["id"]!.ToString() == "edit.undo");
    }

    [Fact]
    public void TheStatusLine_ListenerDownAndNonDefaultInk_AndStartBridgeOnTheCard()
    {
        var view = View("bridge down, grey ink", help: true);
        Assert.Equal("Bridge off · ink: grey", view["status"]!.ToString());
        var start = view["help"]!["groups"]![0]!;
        Assert.Equal("Start a project", start["title"]!.ToString());
        Assert.Equal("bridge.start", start["actions"]![0]!["id"]!.ToString());
        Assert.DoesNotContain("bridge.start", Slots(view));
    }

    [Fact]
    public void TheMoreMenu_ListsSettingsThatAreTrueNow()
    {
        string[] Ids(string fixture) => ((JArray)View(fixture)["settings"]!).Select(s => s["id"]!.ToString()).ToArray();

        Assert.Equal(new[] { "meta.title", "debug.copy" }, Ids("empty"));
        Assert.Equal(new[] { "Ink", "Title block", "Copy debug report" }, ((JArray)View("house")["settings"]!).Select(s => s["label"]!.ToString()));
        Assert.Equal(new[] { "ink.set", "meta.title", "bridge.start", "debug.copy" }, Ids("bridge down, grey ink"));
        Assert.DoesNotContain("file.print", Ids("house"));
        Assert.Equal(new[] { "meta.title" }, ((JArray)View("empty")["attention"]!).Select(a => a["id"]!.ToString()));
        var help = View("house", help: true)["help"]!["groups"]!.SelectMany(g => g["actions"]!).Select(a => a["id"]!.ToString());
        Assert.DoesNotContain("debug.copy", help);
    }

    /// <summary>
    /// Project details need attention until the project name is stored.
    /// A client alone does not clear it. The next view after a save is this
    /// same list with needs false.
    /// </summary>
    [Fact]
    public void Attention_MarksAnEmptyProjectName_AndClearsWhenItIsStored()
    {
        JObject Row(FileFacts facts) => (JObject)WindowView.Build(new DocThread { Serial = 1 }, facts)["attention"]![0]!;

        var empty = Row(Docs.Facts("empty"));
        Assert.Equal("meta.title", empty["id"]!.ToString());
        Assert.Equal("Title block", empty["label"]!.ToString());
        Assert.True(empty["needs"]!.Value<bool>());

        var clientOnly = Row(FileClassifier.Read(Docs.Of(Docs.House()).With(d => d.Meta["client"] = "Holmen")));
        Assert.True(clientOnly["needs"]!.Value<bool>());

        var blank = Row(FileClassifier.Read(Docs.Of(Docs.House()).With(d => d.Meta["project"] = "   ")));
        Assert.True(blank["needs"]!.Value<bool>());

        var named = Row(FileClassifier.Read(Docs.Of(Docs.House()).With(d => d.Meta["project"] = "Tilbygg Holmen")));
        Assert.Equal("meta.title", named["id"]!.ToString());
        Assert.Equal("Title block", named["label"]!.ToString());
        Assert.False(named["needs"]!.Value<bool>());
    }

    [Fact]
    public void TheHelpCard_IsOnlyInTheModelWhileOpen()
    {
        Assert.Null(View("house")["help"]);
        Assert.Equal("What can I do here?", View("house", help: true)["help"]!["title"]!.ToString());
    }

    [Fact]
    public void ThinkingAndAStepLine_AreDifferentBusyKinds()
    {
        var thread = new DocThread { Serial = 1, Thinking = true };
        Assert.Equal("thinking", View("house", thread)["busy"]!["kind"]!.ToString());
        thread.Thinking = false;
        thread.Busy = "Generating… step 3 of 6";
        var busy = View("house", thread)["busy"]!;
        Assert.Equal("step", busy["kind"]!.ToString());
        Assert.Equal("Generating… step 3 of 6", busy["text"]!.ToString());
        thread.Busy = null;
        Assert.Null(View("house", thread)["busy"]);
    }

    [Fact]
    public void AStoredThread_ComesBackWithTheBarForTheFileAsItIsNow()
    {
        var thread = new DocThread { Serial = 2, File = "holmen.3dm" };
        thread.Add("user", "project is Tilbygg Holmen");
        thread.Add("line", "Reopened. The bar shows what is true for this file now.");
        var view = View("printed, then edited", thread);
        Assert.Equal(2, ((JArray)view["thread"]!).Count);
        Assert.Equal("file.print", Slots(view)[0]);
        Assert.Equal("sheets are older than the model.", view["bar"]!["reason"]!.ToString());
    }
}

/// <summary>D1: prefill, the exact-label shortcut, and the receipt's bold object in the page.</summary>
public class ComposerTests
{
    [Fact]
    public void MoveOnADoor_FillsTheComposer_WithTheNumberSelected()
    {
        var p = ForskPrefill.For("opening.move", Docs.Facts("house, door selected"), "print the plan")!;
        Assert.Equal("Move this door 500 mm along the wall", p.Text);
        Assert.Equal("500", p.Text.Substring(p.Start, p.Length));
        var json = p.ToJson(3);
        Assert.Equal(3, json["n"]!.Value<int>());
        Assert.Equal(p.Start + 3, json["end"]!.Value<int>());
    }

    [Fact]
    public void ThePrefill_FollowsTheLanguageOfTheLastMessage()
    {
        var p = ForskPrefill.For("opening.move", Docs.Facts("house, door selected"), "skriv ut planen")!;
        Assert.Equal("Flytt døra 500 mm langs veggen", p.Text);
        Assert.Equal("500", p.Text.Substring(p.Start, p.Length));
        Assert.Equal("Flytt veggen i nord 500 mm mot nord", ForskPrefill.For("wall.move", Docs.Facts("house, wall selected"), "flytt veggen")!.Text);
    }

    [Theory]
    [InlineData("print the plan", "en")]
    [InlineData("skriv ut", "nb")]
    [InlineData("dagslys", "nb")]
    [InlineData("sett målestokk", "nb")]
    [InlineData("move the north wall", "en")]
    [InlineData(null, "en")]
    public void Language_IsReadFromTheMessage(string? text, string expected)
    {
        Assert.Equal(expected, ForskPrefill.Language(text));
    }

    [Fact]
    public void Resize_DefaultsToTheOpeningsOwnWidth()
    {
        Assert.Equal("Make this door 900 mm wide", ForskPrefill.For("opening.resize", Docs.Facts("house, door selected"), null)!.Text);
        var window = FileClassifier.Read(Docs.Of(Docs.House().Append(Row.Window(selected: true)).ToArray()));
        Assert.Equal("Make this window 1200 mm wide", ForskPrefill.For("opening.resize", window, null)!.Text);
        Assert.Null(ForskPrefill.For("file.print", window, null));
    }

    [Theory]
    [InlineData("Print PDF", "file.print")]
    [InlineData("print pdf", "file.print")]
    [InlineData("  Print PDF.  ", "file.print")]
    [InlineData("Add a section", "section.add")]
    [InlineData("print", null)]
    [InlineData("Print PDF now", null)]
    [InlineData("Import a plan", null)]
    public void TypingASlotsExactLabel_FiresIt(string typed, string? expected)
    {
        var bar = ForskRegistry.Bar(Docs.Facts("house"));
        Assert.Equal(expected, ForskRegistry.ByLabel(bar, typed)?.Id);
    }

    [Theory]
    [InlineData("Moved D02 400 mm along w01.", "D02", "Moved |D02| 400 mm along w01.")]
    [InlineData("1", "Walls", "|Walls| 1")]
    [InlineData("Scale set: 4000 mm.", "", "Scale set: 4000 mm.||")]
    public void TheReceiptsObject_IsBoldWhereTheTextNamesIt(string text, string subject, string expected)
    {
        var engine = PageScript.Load();
        var js = "Forsk.splitSubject(" + PageScript.Quote(text) + "," + PageScript.Quote(subject) + ").join('|')";
        Assert.Equal(expected, engine.Evaluate(js).ToString());
    }
}
