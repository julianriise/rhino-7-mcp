using System.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F7: the panel classifies plan import and set scale from chat, and the
/// Import chip offers Import plan in a document with no plan, then Set scale
/// on the imported plan until two points have set it. F2.6: a DXF asked
/// for in chat routes to dxf_import, with a file picker when no path is given
/// and the tool's whole receipt.
/// </summary>
public class PanelImportTests
{
    [Theory]
    [InlineData("import the floor plan")]
    [InlineData("Import plan /tmp/plan.png with /tmp/plan.json")]
    [InlineData("bring in this plan image as an underlay")]
    [InlineData("set the scale")]
    [InlineData("set scale: these two points are 4000 mm apart")]
    [InlineData("scale the plan")]
    [InlineData("importer plantegningen")]
    [InlineData("sett målestokk")]
    [InlineData("import plan.pdf")]
    [InlineData("Import /Users/jr/plans/plan1.pdf")]
    [InlineData("importer pdf-en")]
    [InlineData("bring in the PDF plan")]
    [InlineData("import scan.jpg")]
    [InlineData("Import /Users/jr/Downloads/IMG_1234.jpeg")]
    [InlineData("importer tegning.png")]
    [InlineData("bring in the scan")]
    public void ImportWords_ClassifyAsImport(string text)
    {
        Assert.Equal(ForskIntent.Import, ForskIntentRouter.Classify(text));
    }

    [Theory]
    [InlineData("print pdf at the plan scale", ForskIntent.Print)]
    [InlineData("make a pdf of the plan", ForskIntent.Print)]
    [InlineData("make sheets", ForskIntent.Sheets)]
    [InlineData("run daylight", ForskIntent.Daylight)]
    [InlineData("generate the 3D model", ForskIntent.Build)]
    [InlineData("move the window 200 along the wall", ForskIntent.Edit)]
    [InlineData("why did my dxf labels come in wrong", ForskIntent.General)]
    [InlineData("hello", ForskIntent.General)]
    public void OtherTurns_KeepTheirIntent(string text, ForskIntent expected)
    {
        Assert.Equal(expected, ForskIntentRouter.Classify(text));
    }

    [Theory]
    [InlineData("import plan.dxf")]
    [InlineData("import a DXF")]
    [InlineData("import the dxf")]
    [InlineData("Import /Users/jr/plans/office_2D.dxf")]
    [InlineData("open the DXF of the plan")]
    [InlineData("importer dxf-filen")]
    public void DxfWords_RouteToDxfImport(string text)
    {
        // "import plan.dxf" names a plan too; the DXF wins over the plan image import.
        Assert.Equal(ForskIntent.Dxf, ForskIntentRouter.Classify(text));
        Assert.Equal("dxf_import", ForskDxf.Tool);
        Assert.Contains("Call " + ForskDxf.Tool, ForskDxf.Bias);
        Assert.Contains("no path", ForskDxf.Bias);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plan.dxf")]
    [InlineData("/no/such/folder/plan.dxf")]
    public void Dxf_NoPathToOpenAsItStands_TheUserPicksTheFile(string path)
    {
        Assert.True(ForskDxf.NeedsPick(path));
    }

    [Fact]
    public void Dxf_AnAbsolutePathToAFileThatIsThere_IsImportedAsGiven()
    {
        Assert.False(ForskDxf.NeedsPick(OfficeRoomsTests.OfficePath));
    }

    [Fact]
    public void Dxf_Receipt_IsTheToolsWholeMessage_WithSuspectLabelsAndUnits()
    {
        // What dxf_import answers, MCP client or panel.
        const string guess = "Units not stated ($INSUNITS missing): guessed m from its size, 12 across, scale ×1000. Check a known length.";
        const string message = "Imported plan.dxf: 40 objects. Texts 15/16 read from the DXF, 0 rewritten, 1 left as Rhino made them. "
            + guess + " Labels suspect 1 (B00F8ttekott): a DXF escape lost, and no DXF text matched to restore it.";
        var unmatched = new[] { "label 'B00F8ttekott' at 2000,3000, no DXF text on its layer" };
        var warnings = new[] { guess, "2 objects were not scaled to size." };

        Assert.Equal("Import DXF · ok · Imported plan.dxf: 40 objects.", ForskDxf.Line(true, message));
        var note = ForskDxf.Note(message, unmatched, warnings).Split('\n');
        // The rest of the message, word for word, then what needs a look. The
        // guess is in the message already, so it is not a row again.
        Assert.Equal(message.Substring("Imported plan.dxf: 40 objects. ".Length), note[0]);
        Assert.Equal(new[] { "· " + unmatched[0], "· 2 objects were not scaled to size." }, note.Skip(1).ToArray());

        Assert.Equal("Import DXF · error · Import DXF cancelled.", ForskDxf.Line(false, "Import DXF cancelled."));
        Assert.Equal("Import DXF · ok · Imported a.dxf: 1 object.", ForskDxf.Line(true, "Imported a.dxf: 1 object."));
        Assert.Equal("", ForskDxf.Note("Imported a.dxf: 1 object.", new string[0], new string[0]));
    }

    [Theory]
    [InlineData("/tmp/plan1.pdf", "pdf_path")]
    [InlineData("/tmp/Plan1.PDF", "pdf_path")]
    [InlineData("/tmp/scan.png", "image_path")]
    [InlineData("/tmp/scan.jpg", "image_path")]
    [InlineData("/tmp/photo.JPEG", "image_path")]
    [InlineData("/tmp/office_2D.dxf", "dxf")]
    [InlineData("/tmp/plan.json", null)]
    public void ImportPlanChip_TakesAPdfAnImageOrADxf(string file, string argument)
    {
        // A PDF is plan_import's pdf_path, an image its image_path for the raster source, a DXF goes to dxf_import.
        Assert.Equal(argument, ForskPlanFile.Argument(file));
        Assert.Equal(argument != null, ForskPlanFile.Extensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant()));
    }

    [Fact]
    public void ImportPlanReceipt_OnAnError_PutsTheReasonOnTheRowAndTheNextStepUnderIt()
    {
        const string weights = "plan.png: the raster source has no model weights. Nothing was imported. "
            + "Fetch them once with: uv run --frozen --project \"/Users/jr/Documents/hobby/forsk/tools/cubicasa\" cubicasa-plan --fetch-weights, then import again.";
        Assert.Equal("Import plan · error · plan.png: the raster source has no model weights.", ForskPlanFile.Line(false, weights));
        Assert.Equal("Nothing was imported. Fetch them once with: uv run --frozen --project \"/Users/jr/Documents/hobby/forsk/tools/cubicasa\" "
            + "cubicasa-plan --fetch-weights, then import again.", ForskPlanFile.Note(false, weights, null));
        Assert.Equal("Import plan · error · Import plan cancelled.", ForskPlanFile.Line(false, "Import plan cancelled."));
        Assert.Equal("", ForskPlanFile.Note(false, "Import plan cancelled.", null));
    }

    [Fact]
    public void ImportPlanReceipt_CarriesTheScaleTheLicenceAndTheReviewRows()
    {
        const string message = "Imported 4 walls, 1 door, 1 window, 1 room. Wall cleanup: 4 wall pieces merged into 1 outline, 0 gaps closed, 0 overlaps left. "
            + "Scale not detected (assumed 1:100): set it with two points and a known length. Nothing to review. "
            + "Plan read by the CubiCasa5k model, licensed CC BY-NC 4.0 — non-commercial use only.";
        Assert.Equal("Import plan · ok · Imported 4 walls, 1 door, 1 window, 1 room.", ForskPlanFile.Line(true, message));
        var review = Enumerable.Range(1, 8).Select(i => "row " + i).ToArray();
        var note = ForskPlanFile.Note(true, message, review).Split('\n');
        Assert.Equal(message.Substring("Imported 4 walls, 1 door, 1 window, 1 room. ".Length), note[0]);
        Assert.Contains("licensed CC BY-NC 4.0 — non-commercial use only.", note[0]);
        Assert.Equal(new[] { "· row 1", "· row 2", "· row 3", "· row 4", "· row 5", "· row 6", "· and 2 more" }, note.Skip(1).ToArray());
    }

    static ChipRow Underlay(string status) => new ChipRow { ImportKind = "underlay", ScaleStatus = status, Layer = "X-PLAN" };
    static ChipRow ImportedWall() => new ChipRow { ImportKind = "wall", Layer = "wall" };

    static BakeChip Chip(bool plan, bool walls, params ChipRow[] rows)
    {
        var chip = new BakeChip { HasPlan = plan, HasWalls = walls };
        chip.ReadImport(rows);
        return chip;
    }

    [Fact]
    public void EmptyDocument_OffersImportPlan()
    {
        var chip = Chip(false, false);
        Assert.True(chip.ShowImport);
        Assert.Equal(ImportAction.ImportPlan, chip.Import);
        Assert.Equal("Import plan", chip.ImportLabel);
        Assert.False(chip.Visible);
    }

    [Fact]
    public void DrawnPlanWithNoUnderlay_HidesTheImportChip()
    {
        var chip = Chip(true, false);
        Assert.False(chip.ShowImport);
        Assert.Equal("Generate 3D model", chip.Label);
    }

    [Theory]
    [InlineData("detected")]
    [InlineData("unconfirmed")]
    [InlineData(null)]
    public void ImportedPlan_OffersSetScaleUntilTwoPointsSetIt(string status)
    {
        var chip = Chip(true, false, Underlay(status), ImportedWall());
        Assert.True(chip.HasUnderlay);
        Assert.True(chip.ShowImport);
        Assert.Equal(ImportAction.SetScale, chip.Import);
        Assert.Equal("Set scale", chip.ImportLabel);
        // Generate stays the primary chip beside it.
        Assert.True(chip.ShowGenerate);
    }

    [Fact]
    public void ScaleSetByTheUser_HidesTheImportChip()
    {
        var chip = Chip(true, false, Underlay("user"), ImportedWall());
        Assert.True(chip.ScaleSet);
        Assert.False(chip.ShowImport);
    }

    [Fact]
    public void GeneratedModel_HidesTheImportChip()
    {
        Assert.False(Chip(true, true, Underlay("detected")).ShowImport);
        Assert.False(Chip(false, true).ShowImport);
    }

    [Fact]
    public void ImportedWallsAlone_AreNotAnUnderlay()
    {
        var chip = Chip(true, false, ImportedWall());
        Assert.False(chip.HasUnderlay);
        Assert.False(chip.ShowImport);
    }
}
