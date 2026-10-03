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
        Assert.Equal("A-ELEV", SheetFlat.LayerFor("greyscale"));
        Assert.Equal("A-GRND", SheetFlat.LayerFor("ground_line"));
        Assert.Equal("A-ANNO-DIMS", SheetFlat.LayerFor("dimension"));
        Assert.Equal("A-ANNO-TEXT", SheetFlat.LayerFor("room_tag"));
        Assert.Equal("A-ANNO-TTLB", SheetFlat.LayerFor("title_cell"));
    }

    [Fact]
    public void TheEightLayers_CarryTheirWeights()
    {
        Assert.Equal(
            new[] { "A-WALL-CUT", "A-WALL-PATT", "A-SYMB", "A-ELEV", "A-GRND", "A-ANNO-DIMS", "A-ANNO-TEXT", "A-ANNO-TTLB", "A-ANNO-MISC" },
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
        var segs = SheetFlat.Decode(stroke).Select(s => SheetFlat.Map(s, page)).ToList();

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
    public void AFileName_IsProjectNumberTitle()
    {
        Assert.Equal("Holmen A-20-001 Plan.dwg", SheetFlat.FileName("Holmen", "A-20-001", "Plan", "dwg"));
        Assert.Equal("A-40-101 Section A-A.dxf", SheetFlat.FileName("", "A-40-101", "Section A-A", "DXF"));
        Assert.Equal("Hytte-Lia A-00-002 Door list.dwg", SheetFlat.FileName("Hytte/Lia", "A-00-002", "Door list", "dwg"));
        Assert.Equal("Holmen DWG", SheetFlat.FolderName(" Holmen ", "dwg"));
        Assert.Equal("Forsk DXF", SheetFlat.FolderName(null, "dxf"));
    }
}
