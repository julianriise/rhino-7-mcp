using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// v3 P3: the title block in English, with the sheet's number, and a
/// revision only when one is set. The cell key stays English (forsk:cell,
/// plan_smoke reads name == "scale"); only the printed caption changes.
/// Bokmål captions stay available.
/// </summary>
public class TitleBlockTests
{
    static TitleBlock.Fields Office(string revision = null) => new TitleBlock.Fields
    {
        Drawing = "Ground floor plan",
        Number = SheetSet.Number("plan", 0),
        Scale = "1:200",
        Format = "A3",
        Date = "2026-10-03",
        Revision = revision,
        Project = "Kontor Holmen",
        Client = "Holmen AS",
        Address = "Storgata 1"
    };

    [Fact]
    public void Captions_AreEnglish_AndTheKeysStayEnglish()
    {
        var cells = TitleBlock.Cells(Office("B"));
        Assert.Equal(new[] { "drawing", "number", "scale", "sheet", "date", "revision", "project", "client", "address" },
            cells.Select(c => c.Key).ToArray());
        Assert.Equal(new[] { "Drawing", "Sheet no.", "Scale", "Format", "Date", "Rev.", "Project", "Client", "Address" },
            cells.Select(c => c.Caption).ToArray());
    }

    [Fact]
    public void Captions_StayNorwegian_WhenAsked()
    {
        var cells = TitleBlock.Cells(Office("B"), true);
        Assert.Equal(new[] { "Tegning", "Tegningsnr.", "Målestokk", "Format", "Dato", "Rev.", "Prosjekt", "Byggherre", "Adresse" },
            cells.Select(c => c.Caption).ToArray());
    }

    /// <summary>N1: the garage smoke's project info, as the morning checklist types it, on the plan sheet.</summary>
    static TitleBlock.Fields Smoke()
    {
        var info = ProjectInfoTests.Read(ProjectInfoTests.Smoke);
        return new TitleBlock.Fields
        {
            Drawing = SheetSet.Title("plan", 0),
            Number = SheetSet.Number("plan", 0),
            Scale = "1:100",
            Format = "A3",
            Date = ProjectInfo.SheetDate(info, new DateTime(2026, 10, 5)),
            Revision = info["revision"],
            ProjectNo = info["project_no"],
            Project = info["project"],
            Client = info["client"],
            Address = info["address"],
            Architect = info["architect"]
        };
    }

    [Fact]
    public void TheElevenCells_ComeInOrder_WithProjectNoAndArchitect_InEnglishAndBokmal()
    {
        var cells = TitleBlock.Cells(Smoke());
        Assert.Equal(new[] { "drawing", "number", "scale", "sheet", "date", "revision", "project_no", "project", "client", "address", "architect" },
            cells.Select(c => c.Key).ToArray());
        Assert.Equal(new[] { "Drawing", "Sheet no.", "Scale", "Format", "Date", "Rev.", "Project no.", "Project", "Client", "Address", "Architect" },
            cells.Select(c => c.Caption).ToArray());
        Assert.Equal(new[] { "Tegning", "Tegningsnr.", "Målestokk", "Format", "Dato", "Rev.", "Prosjektnr.", "Prosjekt", "Byggherre", "Adresse", "Arkitekt" },
            TitleBlock.Cells(Smoke(), true).Select(c => c.Caption).ToArray());
        Assert.Equal("2026-07", cells.Single(c => c.Key == "project_no").Value);
        Assert.Equal("Riise Arkitekter", cells.Single(c => c.Key == "architect").Value);
    }

    [Fact]
    public void AStoredDate_ShowsInsteadOfToday()
    {
        Assert.Equal("2026-10-04", TitleBlock.Cells(Smoke()).Single(c => c.Key == "date").Value);
        var undated = ProjectInfoTests.Read(new() { ["project"] = "Garage" });
        Assert.Equal("2026-10-05", ProjectInfo.SheetDate(undated, new DateTime(2026, 10, 5)));
    }

    /// <summary>
    /// All eleven cells hold their caption and their value in 280 mm,
    /// measured by the character share. One row does not (284 mm), so
    /// Project no. and Architect take the smaller second row (risk 1).
    /// </summary>
    [Fact]
    public void AllElevenCells_Fit280Mm_AtTheSmokeValues()
    {
        var cells = TitleBlock.Cells(Smoke());
        Assert.Equal(11, cells.Count);
        var rows = TitleBlock.Rows(cells, 280, null);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "drawing", "number", "scale", "sheet", "date", "revision", "project", "client", "address" }, rows[0].Select(c => c.Key));
        Assert.Equal(new[] { "project_no", "architect" }, rows[1].Select(c => c.Key));
        Assert.All(rows[1], c => Assert.Equal(TitleBlock.SecondValueMm, c.Mm));
        foreach (var row in rows)
        {
            var widths = TitleBlock.Widths(row, 280, null);
            Assert.Equal(280, widths.Sum(), 6);
            for (var i = 0; i < row.Count; i++)
            {
                var want = Math.Max(TitleBlock.CaptionWidth(row[i], null), row[i].Value.Length * 0.8 * row[i].Mm + 2 * TitleBlock.PadMm);
                Assert.True(widths[i] >= want - 1e-6, row[i].Key + " " + widths[i].ToString("0.0") + " < " + want.ToString("0.0"));
            }
        }
    }

    [Fact]
    public void AShortTitleBlock_StaysOneRow()
    {
        var rows = TitleBlock.Rows(TitleBlock.Cells(Office("B")), 280, null);
        Assert.Single(rows);
        Assert.Equal(9, rows[0].Count);
    }

    [Fact]
    public void NoRevision_LeavesTheRevCellOut_NeverADash()
    {
        Assert.DoesNotContain("revision", TitleBlock.Cells(Office()).Select(c => c.Key));
        Assert.DoesNotContain("revision", TitleBlock.Cells(Office("  ")).Select(c => c.Key));
        Assert.DoesNotContain("revision", TitleBlock.Cells(Office("-")).Select(c => c.Key));
    }

    [Fact]
    public void ASetRevision_Appears()
    {
        var rev = TitleBlock.Cells(Office(" B ")).Single(c => c.Key == "revision");
        Assert.Equal("B", rev.Value);
    }

    [Fact]
    public void TheNumberCell_ShowsTheSheetsNumber()
    {
        var fields = Office();
        fields.Number = SheetSet.Number("section_b", 0);
        Assert.Equal("A-40-102", TitleBlock.Cells(fields).Single(c => c.Key == "number").Value);
    }

    [Fact]
    public void EmptyCells_Drop_TheListsSheetHasNoScale()
    {
        var fields = Office();
        fields.Scale = "";
        fields.Client = "—";
        fields.Address = null;
        Assert.Equal(new[] { "drawing", "number", "sheet", "date", "project" }, TitleBlock.Cells(fields).Select(c => c.Key).ToArray());
    }

    [Fact]
    public void NineCellsWithLongValues_Fit280mm_NoCellUnderItsCaption()
    {
        var cells = TitleBlock.Cells(new TitleBlock.Fields
        {
            Drawing = "Dør-, vindus- og romliste (2/3)",
            Number = "A-00-003",
            Scale = "1:500",
            Format = "A3",
            Date = "2026-10-03",
            Revision = "B",
            Project = "Tilbygg og påbygg Holmenveien 12, enebolig med sokkel",
            Client = "Holmen Eiendom AS v/ Kari Nordmann",
            Address = "Holmenveien 12, 0374 Oslo, gnr. 41 bnr. 112"
        });
        Assert.Equal(9, cells.Count);
        var widths = TitleBlock.Widths(cells, 280, null);
        Assert.Equal(280, widths.Sum(), 6);
        for (var i = 0; i < cells.Count; i++)
            Assert.True(widths[i] >= TitleBlock.CaptionWidth(cells[i], null), cells[i].Key + " " + widths[i]);
    }

    [Fact]
    public void TheNumberCell_TakesTheSecondWidestShare_AfterTheTitle()
    {
        var cells = TitleBlock.Cells(Office("B"));
        var widths = TitleBlock.Widths(cells, 280, null);
        Assert.Equal(280, widths.Sum(), 6);
        var number = cells.FindIndex(c => c.Key == "number");
        Assert.True(widths[0] >= widths[number]);
        for (var i = 0; i < cells.Count; i++)
            if (i != 0 && i != number)
                Assert.True(widths[number] >= widths[i], cells[i].Key);
    }

    [Fact]
    public void Widths_UseTheMeasuredText_WhenRhinoMeasuresIt()
    {
        var cells = TitleBlock.Cells(Office());
        // Every text 10 mm wide whatever its height: all cells want the same width.
        var widths = TitleBlock.Widths(cells, 280, (text, mm) => 10);
        Assert.All(widths, w => Assert.Equal(280.0 / cells.Count, w, 6));
        Assert.Empty(TitleBlock.Widths(new List<TitleBlock.Cell>(), 280, null));
    }

    [Theory]
    [InlineData("plan", true, true)]
    [InlineData("north", false, true)]
    [InlineData("west", false, true)]
    [InlineData("section_a", false, true)]
    [InlineData("schedules", false, false)]
    [InlineData("detail_20_1", false, true)]
    public void TheScaleBar_IsOnEveryDrawingSheet_TheNorthArrowOnThePlan(string sheet, bool north, bool bar)
    {
        Assert.Equal(north, TitleBlock.NorthArrow(sheet));
        // Facades, sections and detail sheets lock their scale, so they get the bar; the lists have no scale.
        Assert.Equal(bar, TitleBlock.ScaleBar(sheet, 100));
        Assert.False(TitleBlock.ScaleBar(sheet, 0));
    }

    [Fact]
    public void ADetailSheet_ShowsItsOwnScale_AndItsNumber()
    {
        var fields = Office();
        fields.Drawing = SheetSet.Title("detail_20_1", 0);
        fields.Number = SheetSet.Number("detail_20_1", 0, 0, new[] { "detail_20_1" });
        fields.Scale = "1:20";
        var cells = TitleBlock.Cells(fields);
        Assert.Equal("Details 1:20", cells.Single(c => c.Key == "drawing").Value);
        Assert.Equal("A-50-001", cells.Single(c => c.Key == "number").Value);
        Assert.Equal("1:20", cells.Single(c => c.Key == "scale").Value);
    }
}
