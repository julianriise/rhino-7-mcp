using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// UX.2: the guided AI detection card. One card walks Choose file, Set scale,
/// Review and Generate 3D; each step says what happens, and a failed read
/// says why in plain words and offers another file.
/// </summary>
public class ImportGuideTests
{
    static JObject Card(FileFacts? f = null)
    {
        var card = new JObject { ["role"] = "card", ["kind"] = ForskImportGuide.Kind, ["state"] = "open", ["data"] = ForskImportGuide.Start(f) };
        ForskImportGuide.Paint(card, f);
        return card;
    }

    static string[] States(JObject card) => card["steps"]!.Select(s => s["state"]!.ToString()).ToArray();
    static string[] Pills(JObject card) => card["pills"]!.Select(p => p["id"]!.ToString()).ToArray();
    static JObject Data(JObject card) => (JObject)card["data"]!;

    static JObject Result(string status = "unconfirmed", string? ratio = "1:100", bool scan = false, params string[] review)
    {
        var result = new JObject
        {
            ["walls"] = 30, ["doors"] = 7, ["windows"] = 12, ["rooms"] = 1,
            ["scale"] = new JObject { ["status"] = status, ["ratio"] = ratio },
            ["review"] = new JArray(review)
        };
        if (scan) result["raster"] = new JObject { ["model"] = "cubicasa5k" };
        else result["pdf"] = new JObject { ["page"] = 1 };
        return result;
    }

    static JObject Imported(JObject result, FileFacts f)
    {
        var card = Card(new FileFacts());
        ForskImportGuide.Reading(Data(card), "/plans/holmen.pdf");
        ForskImportGuide.Imported(Data(card), result);
        ForskImportGuide.Paint(card, f);
        return card;
    }

    static FileFacts Plan(bool scaled = false) => new FileFacts { HasUnderlay = true, HasPlanCurves = true, ScaleSet = scaled };

    [Fact]
    public void TheCardOpens_OnChooseFile_WithTheFourStepsAndWhatFilesWork()
    {
        var card = Card(new FileFacts());

        Assert.Equal("Import a plan with AI detection", card["question"]!.ToString());
        Assert.Equal(new[] { "Choose file", "Set scale", "Review", "Generate 3D" }, card["steps"]!.Select(s => s["title"]!.ToString()));
        Assert.Equal(new[] { "now", "todo", "todo", "todo" }, States(card));
        Assert.Contains("scan or photo of a plan (PNG or JPEG), or a DXF", card["steps"]![0]!["detail"]!.ToString());
        Assert.Equal(new[] { "choose", "cancel" }, Pills(card));
        Assert.Equal("file-up", card["pills"]![0]!["icon"]!.ToString());
        Assert.Null(card["note"]);
    }

    [Fact]
    public void AFileWithAPlan_SaysTheNewOneReplacesIt()
    {
        var card = Card(new FileFacts { HasUnderlay = true });

        Assert.StartsWith("This file already has a plan.", card["note"]!.ToString());
    }

    [Theory]
    [InlineData(3, 3, null)]
    [InlineData(30, 24, "Showing the first 24 pages.")]
    public void APdfOfSeveralPages_AsksWhichPage_OnTheSameCard(int pages, int pagePills, string? note)
    {
        var card = Card(new FileFacts());
        ForskImportGuide.Pages(Data(card), "/plans/holmen.pdf", pages);
        ForskImportGuide.Paint(card, new FileFacts());

        Assert.Equal("holmen.pdf has " + pages + " pages. Which is the plan?", card["question"]!.ToString());
        Assert.Equal(pagePills, Pills(card).Count(id => ForskImportGuide.Page(id) != null));
        Assert.Equal("page:1", Pills(card)[0]);
        Assert.Equal(1, ForskImportGuide.Page("page:1"));
        Assert.Null(ForskImportGuide.Page("choose"));
        Assert.Equal(note, card["note"]?.ToString());
        Assert.Contains("cancel", Pills(card));
    }

    [Fact]
    public void WhileReading_StepOneNamesTheFile_AndNothingCanBeClicked()
    {
        var card = Card(new FileFacts());
        ForskImportGuide.Reading(Data(card), "/plans/holmen.pdf");
        ForskImportGuide.Paint(card, new FileFacts());

        Assert.Equal("Reading holmen.pdf…", card["steps"]![0]!["detail"]!.ToString());
        Assert.Empty(Pills(card));
    }

    [Fact]
    public void AFailedRead_SaysWhyInPlainWords_AndOffersAnotherFile()
    {
        var card = Card(new FileFacts());
        ForskImportGuide.Reading(Data(card), "/plans/scan.png");
        ForskImportGuide.Failed(Data(card), "scan.png: the raster source found no plan (no walls). Nothing was imported. Try a sharper scan of the plan alone, at 200 dpi or more, or a vector PDF or DXF of it.");
        ForskImportGuide.Paint(card, new FileFacts());

        Assert.Equal("AI detection could not read scan.png", card["question"]!.ToString());
        Assert.Equal("failed", States(card)[0]);
        var why = card["steps"]![0]!["detail"]!.ToString();
        Assert.Contains("AI detection found no plan", why);
        Assert.Contains("Try a sharper scan", why);
        Assert.DoesNotContain("raster", why);
        Assert.Equal(new[] { "choose", "cancel" }, Pills(card));
    }

    [Fact]
    public void AFileNotInMillimetres_SaysToStartFromAMillimetresTemplate()
    {
        Assert.StartsWith("This Rhino file is not in millimetres.", ForskImportGuide.Plain("Document units must be millimetres. Switch the .3dm to millimetres."));
    }

    [Fact]
    public void AnUnsureScale_IsTheNextStep_WithSetScaleAndSkip()
    {
        var card = Imported(Result(), Plan());

        Assert.Equal(new[] { "done", "now", "todo", "todo" }, States(card));
        Assert.Equal("holmen.pdf, read from the PDF's own lines", card["steps"]![0]!["detail"]!.ToString());
        Assert.Contains("(it assumes 1:100)", card["steps"]![1]!["detail"]!.ToString());
        Assert.Equal(new[] { "scale", "keep" }, Pills(card));
        Assert.Equal("Skip for now", card["pills"]![1]!["label"]!.ToString());
        Assert.Equal("ruler", card["pills"]![0]!["icon"]!.ToString());
    }

    [Fact]
    public void AScaleReadFromThePlan_CanBeKeptByName()
    {
        var card = Imported(Result("detected"), Plan());

        Assert.StartsWith("1:100 was read from the plan.", card["steps"]![1]!["detail"]!.ToString());
        Assert.Equal("Keep 1:100", card["pills"]![1]!["label"]!.ToString());

        Data(card)["kept"] = true;
        ForskImportGuide.Paint(card, Plan());
        Assert.Equal(new[] { "done", "done", "now", "todo" }, States(card));
        Assert.Equal("Kept 1:100 from the plan", card["steps"]![1]!["detail"]!.ToString());
    }

    [Fact]
    public void OnceTwoPointsSetTheScale_ReviewShowsTheCountsAndWhatWasUnsure()
    {
        var card = Imported(Result(review: new[] { "2 openings not on a wall", "1 room without a label" }), Plan(scaled: true));

        Assert.Equal(new[] { "done", "done", "now", "todo" }, States(card));
        Assert.Equal("Set from two points", card["steps"]![1]!["detail"]!.ToString());
        Assert.StartsWith("AI detection found 30 walls, 7 doors, 12 windows and 1 room.", card["steps"]![2]!["detail"]!.ToString());
        Assert.Equal(new[] { "2 openings not on a wall", "1 room without a label" }, card["rows"]!.Select(r => r.ToString()));
        Assert.Equal(new[] { "looks", "choose" }, Pills(card));
    }

    [Fact]
    public void ASkippedScale_ReadsAsSkipped()
    {
        var card = Imported(Result(), Plan());
        Data(card)["kept"] = true;
        ForskImportGuide.Paint(card, Plan());

        Assert.Equal("skipped", States(card)[1]);
    }

    [Fact]
    public void AScan_CarriesTheModelsLicenceLine()
    {
        var card = Imported(Result(scan: true), Plan());

        Assert.Equal("holmen.pdf, read as a scan by the AI detection model", card["steps"]![0]!["detail"]!.ToString());
        Assert.Contains("CC BY-NC 4.0", card["note"]!.ToString());
        Assert.Null(Imported(Result(), Plan())["note"]);
    }

    [Fact]
    public void LooksRight_LeadsToGenerate3D_AndTheCardsLineSaysWhatHappened()
    {
        var card = Imported(Result(), Plan(scaled: true));
        Data(card)["reviewed"] = true;
        ForskImportGuide.Paint(card, Plan(scaled: true));

        Assert.Equal(new[] { "done", "done", "done", "now" }, States(card));
        Assert.Equal(new[] { "generate", "done" }, Pills(card));
        Assert.Equal("box", card["pills"]![0]!["icon"]!.ToString());
        Assert.Equal("Plan imported with AI detection · generating 3D", ForskImportGuide.Receipt("generate"));
        Assert.StartsWith("Plan imported with AI detection.", ForskImportGuide.Receipt("done"));
    }

    [Fact]
    public void ADxf_SkipsTheScaleStep_AndItsNotesAreTheReview()
    {
        var card = Card(new FileFacts());
        ForskImportGuide.Reading(Data(card), "/plans/plan.dxf");
        ForskImportGuide.ImportedDxf(Data(card), new[] { "Units: mm", "" });
        ForskImportGuide.Paint(card, new FileFacts { HasPlanCurves = true });

        Assert.Equal(new[] { "done", "done", "now", "todo" }, States(card));
        Assert.Equal("From the DXF's units", card["steps"]![1]!["detail"]!.ToString());
        Assert.Equal(new[] { "Units: mm" }, card["rows"]!.Select(r => r.ToString()));
    }

    [Fact]
    public void AnUndoneImport_TakesTheCardBackToChooseFile()
    {
        var card = Imported(Result(), Plan());
        ForskImportGuide.Paint(card, new FileFacts());

        Assert.Equal(new[] { "now", "todo", "todo", "todo" }, States(card));
        Assert.Equal("The import was undone. Choose a file to start again.", card["steps"]![0]!["detail"]!.ToString());
        Assert.Equal(new[] { "choose", "cancel" }, Pills(card));
    }

    [Theory]
    [InlineData("import a plan", true)]
    [InlineData("Import plan.pdf", true)]
    [InlineData("can you import my floor plan", true)]
    [InlineData("importer plantegningen", true)]
    [InlineData("how do I use AI detection?", true)]
    [InlineData("bring in this scan", true)]
    [InlineData("Import plan /tmp/plan.png with /tmp/plan.json", false)]
    [InlineData("set the scale", false)]
    [InlineData("import the scale from the plan", false)]
    [InlineData("export dwg", false)]
    [InlineData("draw a wall", false)]
    [InlineData("print the plan", false)]
    public void ASentenceAskingToImportAPlan_OpensTheCard(string text, bool opens)
    {
        Assert.Equal(opens, ForskImportGuide.Opens(text));
    }

    [Fact]
    public void TheStepLine_NamesTheStageAndTheTimeTaken()
    {
        Assert.Equal("AI detection: reading the PDF's own lines… 0:42", ForskImportGuide.Progress(PlanSource.StagePdf, "a.pdf", TimeSpan.FromSeconds(42)));
        Assert.StartsWith("AI detection: reading the plan as a scan", ForskImportGuide.Progress(PlanSource.StageScan, "a.png", TimeSpan.FromSeconds(125)));
        Assert.EndsWith("… 2:05", ForskImportGuide.Progress(PlanSource.StageScan, "a.png", TimeSpan.FromSeconds(125)));
        Assert.Equal("AI detection: opening a.pdf… 0:00", ForskImportGuide.Progress("start", "a.pdf", TimeSpan.Zero));
    }

    [Fact]
    public void TheCardNeverNamesTheRasterSource()
    {
        foreach (var key in ForskText.Keys.Where(k => k.StartsWith("guide.", StringComparison.Ordinal)))
            Assert.DoesNotContain("raster", ForskText.Get(key), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void EveryIconOnTheCard_HasItsLucideHolder_AndAStepShowsItsMark()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.html"));
        foreach (var icon in new[] { "file-up", "ruler", "check", "box", "x" })
            Assert.Contains("<div id=\"icon-" + icon + "\"><svg", html);

        var engine = PageScript.Load();
        Assert.Equal("check", engine.Evaluate("Forsk.stepMark({ n: 1, state: 'done' })").ToString());
        Assert.Equal("x", engine.Evaluate("Forsk.stepMark({ n: 1, state: 'failed' })").ToString());
        Assert.Equal("3", engine.Evaluate("Forsk.stepMark({ n: 3, state: 'now' })").ToString());
    }
}
