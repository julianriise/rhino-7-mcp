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

    /// <summary>A detail added or removed makes the sheets stale.</summary>
    [Fact]
    public void TheFingerprint_ChangesWithTheDetails()
    {
        var model = new[] { Row.Wall(), Row.Floor() };
        Assert.Equal(SheetFingerprint.Of(model), SheetFingerprint.Of(model, null));
        Assert.Equal(SheetFingerprint.Of(model), SheetFingerprint.Of(model, ""));
        var one = SheetFingerprint.Of(model, "[{\"id\":\"DET01\",\"wall\":\"w01\"}]");
        Assert.NotEqual(SheetFingerprint.Of(model), one);

        var printed = new DocInput { Rows = model.ToList(), Layouts = 3, StoredFingerprint = SheetFingerprint.Of(model) };
        Assert.False(FileClassifier.Read(printed).SheetsStale);
        printed.Details = "[{\"id\":\"DET01\",\"wall\":\"w01\"}]";
        Assert.True(FileClassifier.Read(printed).SheetsStale);
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
    public void ThePrintReceipt_IsOneShortLine_TheFileNameOnly()
    {
        Assert.Equal("✓ Printed 7 sheets at 1:200 on A3 · Holmen.pdf",
            ForskReceipt.PrintLine(7, 200, "/Users/jr/Desktop/Holmen.pdf", null, null, ""));
        // A set revision is named; a bumped sheet is one clause; the blank-page note keeps its clause.
        Assert.Equal("✓ Printed 7 sheets at 1:200 on A3, rev. B · Holmen.pdf",
            ForskReceipt.PrintLine(7, 200, "/Users/jr/Desktop/Holmen.pdf", " B ", "", ""));
        Assert.Equal("✓ Printed 7 sheets at 1:200 on A3 (Section A–A at 1:500 to fit) · Holmen.pdf · Blank preview: page 3",
            ForskReceipt.PrintLine(7, 200, "/Users/jr/Desktop/Holmen.pdf", null, " Section A–A at 1:500 to fit.", " · Blank preview: page 3"));
        // One list sheet has no scale.
        Assert.Equal("✓ Printed 1 sheet on A3 · Holmen.pdf", ForskReceipt.PrintLine(1, 0, "/Users/jr/Desktop/Holmen.pdf", null, null, ""));
        // Detail sheets name their scales after the set's; a detail that dropped says so.
        Assert.Equal("✓ Printed 10 sheets at 1:100 on A3, details at 1:20 · Garage.pdf",
            ForskReceipt.PrintLine(10, 100, "/x/Garage.pdf", null, null, "", new[] { 20 }, 0));
        Assert.Equal("✓ Printed 9 sheets at 1:100 on A3, details at 1:20 and 1:25 · Garage.pdf · 1 detail dropped: its wall or opening is gone.",
            ForskReceipt.PrintLine(9, 100, "/x/Garage.pdf", null, null, "", new[] { 20, 25 }, 1));

        var receipt = ForskReceipt.FromLine("✓ Printed 7 sheets at 1:200 on A3 · Holmen.pdf");
        Assert.True(receipt.Ok);
        Assert.Equal("Printed 7 sheets at 1:200 on A3 · Holmen.pdf", receipt.Text);
        Assert.True(string.IsNullOrEmpty(receipt.Subject));
    }

    [Fact]
    public void TheViewModel_CarriesTheThreadTheBarAndTheStatus()
    {
        var thread = new DocThread { Serial = 9, File = "holmen.3dm" };
        thread.Add("user", "skriv ut");
        var model = WindowView.Build(thread, Docs.Facts("bridge down, grey ink"));

        Assert.Equal("holmen.3dm", model["file"]!.ToString());
        // Nothing is picked, and the last message was Norwegian.
        Assert.Equal("Klikk noe i modellen.", model["target"]!.ToString());
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
        var model = WindowView.Build(thread, Docs.Facts("empty"));
        Assert.Equal("This file is empty.", model["thread"]![0]!["text"]!.ToString());
        Assert.Empty(thread.Items);
        Assert.Equal(new[] { "Import a plan", "Draw a wall" }, ((JArray)model["bar"]!["slots"]!).Select(s => s["label"]!.ToString()));
    }
}
