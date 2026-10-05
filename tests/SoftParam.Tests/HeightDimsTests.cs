using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Box = RhinoMCPPlugin.Functions.RoomDetect.Box;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// Height dimensions on a printed facade and a section: one chain left of
/// the drawing through the floors, the sills and heads, the wall top, eaves
/// and ridge, and a second row with the overall height. The garage is the
/// garage smoke's: 8 x 4 m walls 3000 high on grade, a door 0–2100 and a
/// high window 1300–2100 in the south wall.
/// </summary>
public class HeightDimsTests
{
    static readonly Sections.Solid[] GarageWalls = { new Sections.Solid { Kind = "wall", MinZ = 0, MaxZ = 3000 } };

    static readonly (double Sill, double Head)[] GarageOpenings = { (0, 2100), (1300, 2100) };

    static string[] Kinds(IEnumerable<Sections.HeightStop> stops) => stops.Select(s => s.Kind + " " + s.Value.ToString(CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    public void TheGarage_StopsAtTheFloor_TheWindowsSillAndHead_AndTheWallTop()
    {
        var heights = Sections.ModelHeights(GarageWalls, new[] { 0.0 }, null);
        var stops = Sections.HeightStops(heights, GarageWalls, GarageOpenings);
        // The door's sill is the floor; its head and the window's are one stop.
        Assert.Equal(new[] { "ground,floor 0", "sill 1300", "head 2100", "top 3000" }, Kinds(stops));
        Assert.Equal(new[] { 0.0, 1300, 2100, 3000 }, stops.Select(s => s.Z).ToArray());
    }

    [Fact]
    public void TheGarage_ChainReads1300_800_900_AndTheOverall3000()
    {
        var heights = Sections.ModelHeights(GarageWalls, new[] { 0.0 }, null);
        var stops = Sections.HeightStops(heights, GarageWalls, GarageOpenings);
        var chains = Sections.HeightChains(stops, -50, z => z + 7, "south");
        Assert.Equal(2, chains.Count);
        var row1 = chains[0];
        Assert.Equal("height", row1.Kind);
        Assert.Equal("south.height", row1.Id);
        Assert.Equal(1, row1.Row);
        Assert.Equal(new[] { 1300, 800, 900 }, row1.Values.ToArray());
        // Up the sheet from the drawing's left edge, drawn out to the left.
        Assert.Equal(new[] { 7.0, 1307, 2107, 3007 }, row1.Stops.ToArray());
        Assert.Equal(-50, row1.Origin.X);
        Assert.Equal(new Pt(0, 1), row1.Dir);
        Assert.Equal(new Pt(-1, 0), row1.Out);
        var row2 = chains[1];
        Assert.Equal("height_overall", row2.Kind);
        Assert.Equal(2, row2.Row);
        Assert.Equal(new[] { 3000 }, row2.Values.ToArray());
        Assert.Equal(new[] { 7.0, 3007 }, row2.Stops.ToArray());
    }

    [Fact]
    public void NoOpenings_GivesTheFloorAndTheTopOnly_AndNoOverallRow()
    {
        var heights = Sections.ModelHeights(GarageWalls, new[] { 0.0 }, null);
        var stops = Sections.HeightStops(heights, GarageWalls, null);
        Assert.Equal(new[] { "ground,floor 0", "top 3000" }, Kinds(stops));
        var chains = Sections.HeightChains(stops, 0, z => z, "north");
        Assert.Single(chains);
        Assert.Equal(new[] { 3000 }, chains[0].Values.ToArray());
    }

    [Fact]
    public void ASlabAndAFlatRoof_AddTheGroundBelowAndTheRoofTopAbove()
    {
        // The section tests' garage: a 400 slab with its top at ±0 and a 200 flat roof on the walls.
        var solids = new[]
        {
            new Sections.Solid { Kind = "floor", MinZ = -400, MaxZ = 0 },
            new Sections.Solid { Kind = "wall", MinZ = 0, MaxZ = 3000 },
            new Sections.Solid { Kind = "roof", MinZ = 3000, MaxZ = 3200 }
        };
        var roof = new List<List<Pt>> { new List<Pt> { new Pt(-4500, 3000), new Pt(4500, 3000), new Pt(4500, 3200), new Pt(-4500, 3200) } };
        var heights = Sections.ModelHeights(solids, new[] { 0.0 }, roof);
        var stops = Sections.HeightStops(heights, solids, new[] { (900.0, 2100.0) });
        Assert.Equal(new[] { "ground -400", "floor 0", "sill 900", "head 2100", "top 3000", "eaves,ridge 3200" }, Kinds(stops));
        var chains = Sections.HeightChains(stops, 0, z => z, "section_a");
        Assert.Equal(new[] { 400, 900, 1200, 900, 200 }, chains[0].Values.ToArray());
        Assert.Equal(new[] { 3600 }, chains[1].Values.ToArray());
    }

    [Fact]
    public void APitchedRoof_StopsAtTheEavesAndTheRidgeApart()
    {
        var gable = new List<List<Pt>> { new List<Pt> { new Pt(-4500, 2900), new Pt(0, 5000), new Pt(4500, 2900) } };
        var heights = Sections.ModelHeights(GarageWalls, new[] { 0.0 }, gable);
        var stops = Sections.HeightStops(heights, GarageWalls, null);
        Assert.Equal(new[] { "ground,floor 0", "eaves 2900", "top 3000", "ridge 5000" }, Kinds(stops));
    }

    [Fact]
    public void TwoFloors_EachFloorsOpeningsGetTheirOwnStops()
    {
        var solids = new[]
        {
            new Sections.Solid { Kind = "floor", MinZ = -200, MaxZ = 0 },
            new Sections.Solid { Kind = "floor", MinZ = 2800, MaxZ = 3000 },
            new Sections.Solid { Kind = "wall", MinZ = 0, MaxZ = 5800 }
        };
        var heights = Sections.ModelHeights(solids, null, null);
        // A door on the ground floor; a window upstairs, its sill 900 above the upper floor.
        var stops = Sections.HeightStops(heights, solids, new[] { (0.0, 2100.0), (3900.0, 5100.0) });
        Assert.Equal(new[] { "ground -200", "floor 0", "head 2100", "floor 3000", "sill 3900", "head 5100", "top 5800" }, Kinds(stops));
    }

    [Fact]
    public void TheSouthFacade_ShowsTheSouthWallsOpenings_NotTheNorthOrTheEnds()
    {
        var building = new Box(0, 0, 8000, 4000);
        var south = new Pt(0, 1);
        Assert.True(Sections.SeenOnFacade(new Box(4200, 0, 5400, 200), south, building));
        Assert.False(Sections.SeenOnFacade(new Box(4200, 3800, 5400, 4000), south, building));
        // An east wall window is edge-on from the south.
        Assert.False(Sections.SeenOnFacade(new Box(7800, 1400, 8000, 2600), south, building));
        // The north facade looks the other way and sees the north wall.
        Assert.True(Sections.SeenOnFacade(new Box(4200, 3800, 5400, 4000), new Pt(0, -1), building));
        Assert.True(Sections.SeenOnFacade(new Box(7800, 1400, 8000, 2600), new Pt(-1, 0), building));
    }

    [Fact]
    public void ASection_KeepsTheOpeningsItCuts()
    {
        // A cross section through x = 4800, looking west.
        var def = new Sections.Def { Letter = "A", A = new Pt(4800, -1000), B = new Pt(4800, 5000), Look = new Pt(-1, 0) };
        Assert.True(Sections.CutCrosses(def, new Box(4200, 0, 5400, 200)));
        Assert.False(Sections.CutCrosses(def, new Box(1000, 0, 2000, 200)));
        Assert.False(Sections.CutCrosses(def, new Box(7800, 1400, 8000, 2600)));
    }

    [Fact]
    public void TheDetailTheFacadeAndTheSection_BakeChainsThroughOneLoop()
    {
        var dir = FunctionsDir();
        var detail = File.ReadAllText(Path.Combine(dir, "DetailBake.cs"));
        var heights = File.ReadAllText(Path.Combine(dir, "SectionSheet.cs"));
        var facade = File.ReadAllText(Path.Combine(dir, "FacadeSheet.cs"));
        Assert.Contains("BakeFixedChains(", detail);
        Assert.Contains("BakeFixedChains(", heights);
        Assert.Contains("BakeHeights(", facade);
        Assert.Contains("BakeHeights(", heights);
        // LayoutFixed is called in one place only: the shared loop.
        var callers = Directory.GetFiles(dir, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("PlanDims.LayoutFixed("))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Equal(new[] { "PlanDimensions.cs" }, callers);
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
}
