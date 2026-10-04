using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// R3: a sheet flattened for DWG/DXF without a document. Every role the
/// sheets bake has an export layer, pens snap to DWG lineweights, and a
/// stroke ribbon comes back as its centreline at paper size.
/// </summary>
public class SheetFlatTests
{
    /// <summary>
    /// The roles the garage set bakes: the plan (cut, beyond, symbols, marks,
    /// tags, dimensions, section marker), a facade and a section (lines,
    /// fills, ground, levels, free height), the lists and every footer.
    /// </summary>
    static readonly string[] GarageRoles =
    {
        "cut", "section_fill", "greyscale", "beyond", "roof_outline", "symbol", "opening_mark",
        "room_tag", "room_leader", "dimension", "section_marker", "ground_line", "level", "free_height",
        "schedule_title", "schedule_head", "schedule_cell", "schedule_line", "schedule_note",
        "north", "scale_bar", "scale_bar_label", "title_block", "title_cell"
    };

    [Fact]
    public void EveryGarageRole_HasALayer_AndNoneIsUnknown()
    {
        foreach (var role in GarageRoles)
            Assert.NotEqual(SheetFlat.Misc, SheetFlat.LayerFor(role));
        Assert.Equal(SheetFlat.Misc, SheetFlat.LayerFor("brand_new_role"));
        Assert.Equal(SheetFlat.Misc, SheetFlat.LayerFor(null));

        Assert.Equal("A-WALL-CUT", SheetFlat.LayerFor("cut"));
        Assert.Equal("A-WALL-PATT", SheetFlat.LayerFor("section_fill"));
        Assert.Equal("A-SYMB", SheetFlat.LayerFor("symbol"));
        // R5: every piece of the stair symbol goes to its own layer.
        Assert.Equal("A-STAIR", SheetFlat.LayerFor("stair"));
        Assert.Equal("A-ELEV", SheetFlat.LayerFor("greyscale"));
        Assert.Equal("A-GRND", SheetFlat.LayerFor("ground_line"));
        Assert.Equal("A-ANNO-DIMS", SheetFlat.LayerFor("dimension"));
        Assert.Equal("A-ANNO-TEXT", SheetFlat.LayerFor("room_tag"));
        Assert.Equal("A-ANNO-TTLB", SheetFlat.LayerFor("title_cell"));
    }

    /// <summary>The roles a detail sheet and the plan's callouts bake.</summary>
    static readonly string[] DetailRoles =
    {
        "cut", "beyond", "section_fill", "break_line", "dimension", "level", "detail_title", "detail_marker", "callout",
        "scale_bar", "scale_bar_label", "title_block", "title_cell"
    };

    [Fact]
    public void EveryDetailRole_HasALayer()
    {
        foreach (var role in DetailRoles)
            Assert.NotEqual(SheetFlat.Misc, SheetFlat.LayerFor(role));
        Assert.Equal("A-SYMB", SheetFlat.LayerFor("callout"));
        Assert.Equal("A-SYMB", SheetFlat.LayerFor("detail_marker"));
        Assert.Equal("A-ANNO-TEXT", SheetFlat.LayerFor("detail_title"));
        Assert.Equal("A-ELEV", SheetFlat.LayerFor("break_line"));
        Assert.Equal("A-ANNO-TEXT", SheetFlat.LayerFor("level"));
    }

    [Fact]
    public void EveryRoleTheDetailBakeStamps_IsADetailRole()
    {
        var source = File.ReadAllText(Path.Combine(FunctionsDir(), "DetailBake.cs"));
        var stamped = System.Text.RegularExpressions.Regex.Matches(source, @"(?:tol|scale|false|true),\s*""([a-z_]+)"",")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(stamped);
        Assert.All(stamped, role => Assert.Contains(role, DetailRoles));
    }

    [Fact]
    public void ADetailSheet_At1To20_KeepsItsWeightsAndSize()
    {
        Assert.Equal(0.70, SheetFlat.Snap(0.70), 6);
        Assert.Equal(0.70, SheetFlat.Snap(PrintProfiles.AtScale(PrintProfiles.Default, 20).Cut.Mm), 6);
        var page = new SheetFlat.Affine { A = 1.0 / 20, E = 1.0 / 20, C = 25, F = 40 };
        var wall = SheetFlat.Map(SheetFlat.Seg.Line(1000, 0, 1000, 200), page).P;
        Assert.Equal(10, wall[3] - wall[1], 9);
        Assert.Equal("Garage A-50-001 Details 1-20.dwg", SheetFlat.FileName("Garage", "A-50-001", Details.SheetTitle(20), "dwg"));
    }

    static string FunctionsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Functions");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("plugin/Functions above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheLayers_CarryTheirWeights()
    {
        Assert.Equal(
            new[] { "A-WALL-CUT", "A-WALL-PATT", "A-SYMB", "A-STAIR", "A-ELEV", "A-GRND", "A-ANNO-DIMS", "A-ANNO-TEXT", "A-ANNO-TTLB", "A-ANNO-MISC" },
            SheetFlat.Layers.Select(l => l.Name));
        Assert.Equal(0.50, SheetFlat.Def("A-WALL-CUT").WeightMm);
        Assert.Equal(0.00, SheetFlat.Def("A-WALL-PATT").WeightMm);
        Assert.Equal(0.25, SheetFlat.Def("A-ANNO-TTLB").WeightMm);
        Assert.All(SheetFlat.Layers, l => Assert.Contains(l.WeightMm, SheetFlat.Lineweights));
    }

    [Theory]
    [InlineData(0.36, 0.35)]
    [InlineData(0.22, 0.20)]
    [InlineData(0.48, 0.50)]
    [InlineData(0.18, 0.18)]
    [InlineData(0.7, 0.70)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(9, 2.11)]
    public void APen_SnapsToTheNearestLineweight(double pen, double stored)
    {
        Assert.Equal(stored, SheetFlat.Snap(pen), 3);
    }

    /// <summary>A 4000 mm wall at 1:50 is 80 mm on paper, and 125 mm text in the model is 2.5 mm.</summary>
    [Fact]
    public void AStroke_ComesBackAtPaperSize()
    {
        var stroke = SheetFlat.Encode(new[]
        {
            SheetFlat.Seg.Line(1000, 2000, 5000, 2000),
            SheetFlat.Seg.ArcOf(5000, 2000, 5707.107, 2292.893, 6000, 3000)
        });
        Assert.Equal("L 1000,2000 5000,2000|A 5000,2000 5707.107,2292.893 6000,3000", stroke);

        var page = new SheetFlat.Affine { A = 1.0 / 50, E = 1.0 / 50, C = 210 - 3000.0 / 50, F = 148.5 - 2000.0 / 50 };
        var segs = SheetFlat.OnPage(stroke, page);

        Assert.Equal(2, segs.Count);
        var wall = segs[0].P;
        Assert.Equal(80, Math.Sqrt(Math.Pow(wall[2] - wall[0], 2) + Math.Pow(wall[3] - wall[1], 2)), 6);
        Assert.Equal(new[] { 170.0, 148.5, 250.0, 148.5 }, wall.Select(v => Math.Round(v, 6)));
        Assert.True(segs[1].Arc);
        Assert.Equal(1.0 / 50, page.Scale, 9);
        Assert.Equal(2.5, 125 * page.Scale, 9);
    }

    [Fact]
    public void ABrokenStroke_KeepsThePiecesThatRead()
    {
        var segs = SheetFlat.Decode("L 0,0 10,0|L 1,x 2,2|Q 1,1 2,2|A 0,0 1,1|L 10,0 10,10");
        Assert.Equal(2, segs.Count);
        Assert.Equal(new[] { 10.0, 0, 10, 10 }, segs[1].P);
        Assert.Empty(SheetFlat.Decode(null));
    }

    [Fact]
    public void ACentredStroke_KeepsItsLineWithThePageMap()
    {
        // A-50-001's title rule in model mm at 1:20, then the bake's +17 / −79 paper mm.
        var packed = SheetFlat.Encode(new[] { SheetFlat.Seg.Line(400, 4220, 1600, 4220) });
        var moved = SheetFlat.Shift(packed, 17 * 20, -79 * 20);
        var page = new SheetFlat.Affine { A = 1.0 / 20, E = 1.0 / 20 };
        var rule = Assert.Single(SheetFlat.OnPage(moved, page)).P;
        Assert.Equal(new[] { 37.0, 132.0, 97.0, 132.0 }, rule.Select(v => Math.Round(v, 6)));
        var left = SheetFlat.OnPage(packed, page)[0].P;
        Assert.Equal(211, left[1], 6);
        Assert.Single(SheetFlat.OnPage(packed, page));
        Assert.Single(SheetFlat.OnPage(packed, page, 17 * 20, -79 * 20));
        Assert.True(SheetFlat.TryCentre(SheetFlat.FormatCentre(340, -1580), out var dx, out var dy));
        Assert.Equal(340, dx, 9);
        Assert.Equal(-1580, dy, 9);
    }

    [Fact]
    public void AFileName_IsProjectNumberTitle()
    {
        Assert.Equal("Holmen A-20-001 Plan.dwg", SheetFlat.FileName("Holmen", "A-20-001", "Plan", "dwg"));
        Assert.Equal("A-40-101 Section A-A.dxf", SheetFlat.FileName("", "A-40-101", "Section A-A", "DXF"));
        Assert.Equal("Hytte-Lia A-00-002 Door list.dwg", SheetFlat.FileName("Hytte/Lia", "A-00-002", "Door list", "dwg"));
        Assert.Equal("Holmen DWG", SheetFlat.FolderName(" Holmen ", "dwg"));
        Assert.Equal("Forsk DXF", SheetFlat.FolderName(null, "dxf"));
    }
}

/// <summary>R3: what the export does with each sheet piece, and how it is weighted and set.</summary>
public class SheetFlatPieceTests
{
    [Fact]
    public void ARibbon_IsDrawnOnceAsItsStroke_AndOtherHatchesStayHatches()
    {
        // The first kept hatch of a ribbon carries the stroke; the rest of that ribbon go.
        Assert.Equal(SheetFlat.Draw.Stroke, SheetFlat.HowToDraw(hatch: true, hasPen: true, hasStroke: true));
        Assert.Equal(SheetFlat.Draw.Skip, SheetFlat.HowToDraw(hatch: true, hasPen: true, hasStroke: false));
        // Poché, section fills and the scale bar's blocks have no pen: they are fills.
        Assert.Equal(SheetFlat.Draw.AsIs, SheetFlat.HowToDraw(hatch: true, hasPen: false, hasStroke: false));
        Assert.Equal(SheetFlat.Draw.AsIs, SheetFlat.HowToDraw(hatch: false, hasPen: false, hasStroke: false));
    }

    [Fact]
    public void AStroke_TakesItsPen_ACurveItsPlotWeight_ElseTheLayer()
    {
        Assert.Equal(0.50, SheetFlat.Weight(SheetFlat.Draw.Stroke, 0.48, false, 0).Value, 3);
        Assert.Equal(0.35, SheetFlat.Weight(SheetFlat.Draw.AsIs, 0, true, 0.36).Value, 3);
        Assert.Null(SheetFlat.Weight(SheetFlat.Draw.AsIs, 0, false, 0.36));
        Assert.Null(SheetFlat.Weight(SheetFlat.Draw.AsIs, 0, true, 0));
        Assert.Null(SheetFlat.Weight(SheetFlat.Draw.Stroke, 0, false, 0));
    }

    [Theory]
    [InlineData("Left", "Bottom", "BottomLeft")]
    [InlineData("Center", "Middle", "MiddleCenter")]
    [InlineData("Right", "Top", "TopRight")]
    [InlineData("Center", "BottomOfTop", "TopCenter")]
    [InlineData("Auto", "MiddleOfBottom", "BottomLeft")]
    [InlineData(null, null, "BottomLeft")]
    public void TextAlignment_MapsToOneJustification(string horizontal, string vertical, string expected)
    {
        Assert.Equal(expected, SheetFlat.Justification(horizontal, vertical));
    }

    /// <summary>Plan text 125 mm high in the model is 2.5 mm on paper at 1:50; page text keeps its size.</summary>
    [Fact]
    public void TextHeight_LandsAtItsPaperSize()
    {
        var plan = new SheetFlat.Affine { A = 1.0 / 50, E = 1.0 / 50 };
        Assert.Equal(2.5, SheetFlat.TextMm(125, plan), 9);
        Assert.Equal(3.5, SheetFlat.TextMm(3.5, SheetFlat.Affine.Identity), 9);
        Assert.Equal(0, SheetFlat.TextMm(-1, plan), 9);
    }

    /// <summary>
    /// What the file stores, in sheet mm. The mm template's dimension scale is
    /// 100; a room tag stays 2.5, a door mark 1.25, a dimension value 1.8, a
    /// schedule cell 2, a title 3.5, and the box around "Room" stays about 22.6.
    /// </summary>
    [Fact]
    public void ExportText_IsWrittenAtPaperMillimetres()
    {
        const double templateScale = 100;
        Assert.Equal(2.5, SheetFlat.WrittenMm(2.5, templateScale), 9);
        Assert.Equal(1.25, SheetFlat.WrittenMm(1.25, templateScale), 9);
        Assert.Equal(1.8, SheetFlat.WrittenMm(1.8, templateScale), 9);
        Assert.Equal(2, SheetFlat.WrittenMm(2, templateScale), 9);
        Assert.Equal(3.5, SheetFlat.WrittenMm(3.5, templateScale), 9);
        Assert.Equal(22.55, SheetFlat.WrittenMm(22.55, templateScale), 9);
        Assert.Equal(2.5, SheetFlat.WrittenMm(2.5, 1), 9);
        Assert.Equal(3.5, SheetFlat.WrittenMm(3.5, 0), 9);
        Assert.Equal(0, SheetFlat.WrittenMm(0, templateScale), 9);
        Assert.Equal(0, SheetFlat.WrittenMm(-1, templateScale), 9);
    }
}

/// <summary>R3: the chat words for an export, the receipt, and the Choose sheets pill.</summary>
public class SheetExportWindowTests
{
    [Theory]
    [InlineData("export dwg", "dwg")]
    [InlineData("Export DWG", "dwg")]
    [InlineData("send dwg to the engineer", "dwg")]
    [InlineData("export the sheets as dxf", "dxf")]
    [InlineData("eksporter dwg", "dwg")]
    [InlineData("eksporter dxf", "dxf")]
    [InlineData("export ifc", "ifc")]
    [InlineData("eksporter IFC", "ifc")]
    [InlineData("send the model as ifc", "ifc")]
    [InlineData("import plan.dxf", null)]
    [InlineData("open the dwg", null)]
    [InlineData("print", null)]
    [InlineData("what is a dwg?", null)]
    public void TheChatWords_PickTheFormat(string text, string format)
    {
        Assert.Equal(format, ForskIntentRouter.ExportFormat(text));
    }

    [Fact]
    public void ImportingADxf_StillRoutesToTheDxfImport()
    {
        Assert.Equal(ForskIntent.Dxf, ForskIntentRouter.Classify("import plan.dxf"));
    }

    [Fact]
    public void TheReceipt_IsOneLine_WithTheFolderNotAPath()
    {
        Assert.Equal("✓ Exported 7 sheets as DWG · Holmen DWG/", ForskReceipt.ExportLine(7, "dwg", "/Users/jr/Desktop/Holmen DWG"));
        Assert.Equal("✓ Exported 1 sheet as DXF · Forsk DXF/", ForskReceipt.ExportLine(1, "dxf", "/tmp/Forsk DXF/"));
    }

    [Fact]
    public void ChooseSheets_OffersExportDwg()
    {
        var card = ForskCards.Pages(Docs.Facts("house"));
        Assert.Contains(card.Pills, p => p.Id == "export" && p.Label == "Export DWG");
        Assert.Contains(card.Pills, p => p.Id == "export_ifc" && p.Label == "Export IFC");
    }

    [Fact]
    public void TheAcadVersion_IsReadOffTheFile_AndOnly2004OrLaterIsModern()
    {
        var dxf = System.Text.Encoding.ASCII.GetBytes("  0\r\nSECTION\r\n  2\r\nHEADER\r\n  9\r\n$ACADVER\r\n  1\r\nAC1032\r\n  9\r\n");
        Assert.Equal("AC1032", SheetFlat.AcadVersion(dxf));
        Assert.Equal("AC1009", SheetFlat.AcadVersion(System.Text.Encoding.ASCII.GetBytes("  9\n$ACADVER\n  1\nAC1009\n")));
        Assert.Equal("AC1027", SheetFlat.AcadVersion(System.Text.Encoding.ASCII.GetBytes("AC1027\0\0\0\0\0")));
        Assert.Equal("", SheetFlat.AcadVersion(new byte[0]));
        Assert.Equal("", SheetFlat.AcadVersion(System.Text.Encoding.ASCII.GetBytes("  0\nSECTION\n")));

        Assert.True(SheetFlat.ModernAcad("AC1018"));
        Assert.True(SheetFlat.ModernAcad("AC1021"));
        Assert.True(SheetFlat.ModernAcad("AC1027"));
        Assert.True(SheetFlat.ModernAcad("AC1032"));
        Assert.False(SheetFlat.ModernAcad("AC1009"));
        Assert.False(SheetFlat.ModernAcad("AC1015"));
        Assert.False(SheetFlat.ModernAcad(""));
    }

    [Fact]
    public void TheExportScript_NamesTheFileAndTheScheme()
    {
        Assert.Equal("_-Export \"/tmp/a b/Holmen A-20-001 Plan.dwg\" _Scheme \"Default\" _Enter",
            SheetFlat.ExportScript("/tmp/a b/Holmen A-20-001 Plan.dwg"));
    }

    [Fact]
    public void AViewsDrawing_IsItsLayerAndTheRoleLayersUnderIt()
    {
        // An elevation bakes its lines on role sublayers: S-DRAW::North::facade-line.
        Assert.True(SheetFlat.InDrawing("S-DRAW::North", "S-DRAW::North"));
        Assert.True(SheetFlat.InDrawing("S-DRAW::North::facade-line", "S-DRAW::North"));
        Assert.True(SheetFlat.InDrawing("s-draw::north::Facade-Ground", "S-DRAW::North"));
        Assert.False(SheetFlat.InDrawing("S-DRAW::Northwest", "S-DRAW::North"));
        Assert.False(SheetFlat.InDrawing("S-DRAW::Plan", "S-DRAW::North"));
        Assert.False(SheetFlat.InDrawing("A-WALL", "S-DRAW::North"));
        Assert.False(SheetFlat.InDrawing(null, "S-DRAW::North"));
    }
}
