using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F7: the panel classifies plan import and set scale from chat, and the
/// Import chip offers Import plan in a document with no plan, then Set scale
/// on the imported plan until two points have set it.
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
    public void ImportWords_ClassifyAsImport(string text)
    {
        Assert.Equal(ForskIntent.Import, ForskIntentRouter.Classify(text, ""));
    }

    [Theory]
    [InlineData("print pdf at the plan scale", ForskIntent.Print)]
    [InlineData("make sheets", ForskIntent.Sheets)]
    [InlineData("run daylight", ForskIntent.Daylight)]
    [InlineData("generate the 3D model", ForskIntent.Build)]
    [InlineData("move the window 200 along the wall", ForskIntent.Edit)]
    [InlineData("import the dxf", ForskIntent.General)]
    [InlineData("hello", ForskIntent.General)]
    public void OtherTurns_KeepTheirIntent(string text, ForskIntent expected)
    {
        Assert.Equal(expected, ForskIntentRouter.Classify(text, ""));
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
