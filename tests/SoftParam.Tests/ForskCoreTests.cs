using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F1.1: the sheet fingerprint, last-action clearing, structured receipts,
/// and the C# view model the window draws.
/// </summary>
public class ForskCoreTests
{
    [Fact]
    public void TheFingerprint_IgnoresOrderDrawingsLayoutsAndTheMap()
    {
        var model = Docs.House();
        var printed = SheetFingerprint.Of(model);
        var later = model.Reverse()
            .Append(Row.Drawing("S-DRAW"))
            .Append(new ChipRow { Id = "layout", Generated = true, Kind = "layout", Layer = "Default" })
            .Append(Row.Map(visible: true, stale: true));

        Assert.Equal(printed, SheetFingerprint.Of(later));
        Assert.Equal(printed, SheetFingerprint.Of(model.Append(Row.PlanCurve("wall"))));
    }

    [Fact]
    public void TheFingerprint_ChangesWhenAModelObjectIsEdited()
    {
        var model = Docs.House();
        var printed = SheetFingerprint.Of(model);
        model[0].Stamp = "w01 after a move";
        Assert.NotEqual(printed, SheetFingerprint.Of(model));
    }

    [Fact]
    public void SheetsAreOnlyOlder_WhenThereAreSheets()
    {
        var rows = Docs.House();
        var stamped = SheetFingerprint.Of(rows);
        var input = Docs.Of(rows).With(d => { d.StoredFingerprint = stamped; d.Layouts = 6; });
        Assert.False(FileClassifier.Read(input).SheetsStale);

        rows[0].Stamp = "moved";
        Assert.True(FileClassifier.Read(input).SheetsStale);
        input.Layouts = 0;
        Assert.False(FileClassifier.Read(input).SheetsStale);
    }

    [Fact]
    public void LastAction_ClearsOnUndoRedo_AFileOpened_AndAnotherDocument()
    {
        var tracker = new LastActionTracker();
        Action record = () => tracker.Record(new LastAction { Kind = "file.print", Doc = 2, Turn = 3, Undoable = true });

        record();
        tracker.UndoRedo();
        Assert.Null(tracker.Current);

        record();
        tracker.DocumentOpened();
        Assert.Null(tracker.Current);

        record();
        Assert.True(tracker.Was("file.print", 2));
        tracker.ActiveDocumentChanged();
        Assert.False(tracker.Was("file.print", 2));
    }

    [Fact]
    public void Undo_IsOnlyOfferedForARecordThatHoldsAChange_OnItsOwnDocument()
    {
        var tracker = new LastActionTracker();
        tracker.Record(new LastAction { Kind = "answer", Doc = 2, Undoable = false });
        Assert.False(tracker.UndoNewest(2));
        tracker.Record(new LastAction { Kind = "answer", Doc = 2, Undoable = true });
        Assert.True(tracker.UndoNewest(2));
        Assert.False(tracker.UndoNewest(5));
    }

    [Fact]
    public void AReceipt_NamesTheObjectFromTheMessage()
    {
        var envelope = JObject.Parse("{\"status\":\"success\",\"result\":{\"message\":\"Moved D02 400 mm along w01. The wall was rebuilt. Extra.\"}}");
        var receipt = ForskReceipt.From("move_opening", envelope);
        Assert.True(receipt.Ok);
        Assert.Equal("D02", receipt.Subject);
        Assert.Equal("Moved D02 400 mm along w01. The wall was rebuilt.", receipt.Text);
    }

    [Fact]
    public void AReceipt_WithNoObjectInItsMessage_UsesTheStepLabel()
    {
        var walls = ForskReceipt.From("walls_from_layer", JObject.Parse("{\"status\":\"success\",\"result\":{\"count\":1}}"));
        Assert.Equal("Walls", walls.Subject);
        Assert.Equal("1", walls.Text);

        var failed = ForskReceipt.From("layout_pack", JObject.Parse("{\"status\":\"error\",\"message\":\"Nothing to lay out. Generate first.\"}"));
        Assert.False(failed.Ok);
        Assert.Equal("Sheets", failed.Subject);
        Assert.Equal("Nothing to lay out.", failed.Text);
    }

    [Theory]
    [InlineData("Print PDF · ok · 7 · /Users/jr/Desktop/Holmen.pdf", true, "Print PDF", "7 · /Users/jr/Desktop/Holmen.pdf")]
    [InlineData("Print PDF · error · Document units must be millimetres.", false, "Print PDF", "Document units must be millimetres.")]
    [InlineData("Print PDF · cancelled", null, "Print PDF", "cancelled")]
    [InlineData("walls_from_layer · ok · 1", true, "Walls", "1")]
    public void ALegacyLine_BecomesAStructuredReceipt(string line, bool? ok, string subject, string text)
    {
        var receipt = ForskReceipt.FromLine(line);
        Assert.Equal(ok, receipt.Ok);
        Assert.Equal(subject, receipt.Subject);
        Assert.Equal(text, receipt.Text);
        Assert.Equal("receipt", receipt.ToJson()["role"]!.ToString());
    }

    [Fact]
    public void TheViewModel_CarriesTheThreadTheBarAndTheStatus()
    {
        var thread = new DocThread { Serial = 9, File = "holmen.3dm" };
        thread.Add("user", "skriv ut");
        var model = WindowView.Build(thread, Docs.Facts("bridge down, grey ink"), "Target: w01 · A-WALL · forsk:wall");

        Assert.Equal("holmen.3dm", model["file"]!.ToString());
        Assert.Equal("Bridge off · ink: grey", model["status"]!.ToString());
        Assert.Equal("skriv ut", model["thread"]![0]!["text"]!.ToString());
        var slots = (JArray)model["bar"]!["slots"]!;
        Assert.Equal("Print PDF", slots[0]["label"]!.ToString());
        Assert.Equal("⌘1", slots[0]["key"]!.ToString());
        Assert.Equal("Suggested because", model["bar"]!["because"]!.ToString());
        Assert.Equal("a 3D model is in the file.", model["bar"]!["reason"]!.ToString());
        Assert.Equal("⌘/", model["bar"]!["help"]!["key"]!.ToString());
    }

    [Fact]
    public void AnEmptyThread_ShowsTheLocalSentence_WithoutStoringIt()
    {
        var thread = new DocThread { Serial = 1 };
        var model = WindowView.Build(thread, Docs.Facts("empty"), "");
        Assert.Equal("This file is empty.", model["thread"]![0]!["text"]!.ToString());
        Assert.Empty(thread.Items);
        Assert.Equal(new[] { "Import a plan", "Draw a wall" }, ((JArray)model["bar"]!["slots"]!).Select(s => s["label"]!.ToString()));
    }
}
