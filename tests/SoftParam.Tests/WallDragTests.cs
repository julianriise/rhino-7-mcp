using System;
using System.Collections.Generic;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// A wall drag projects onto the run's normal, snaps, and names the distance.
/// </summary>
public class WallDragTests
{
    static WallEdit.Run EastWest() => new WallEdit.Run
    {
        Dir = new Pt(1, 0),
        Normal = new Pt(0, 1),
        Near = 0,
        Far = 200,
        Lo = 0,
        Hi = 4000
    };

    static WallEdit.Run NorthSouth() => new WallEdit.Run
    {
        Dir = new Pt(0, 1),
        Normal = new Pt(1, 0),
        Near = 0,
        Far = 200,
        Lo = 0,
        Hi = 4000
    };

    [Fact]
    public void DiagonalDrag_ProjectsOntoTheNormal()
    {
        Assert.Equal(400, WallDrag.Along(new Pt(0, 0), new Pt(300, 400), new Pt(0, 1)), 6);
        Assert.Equal(300, WallDrag.Along(new Pt(0, 0), new Pt(300, 400), new Pt(1, 0)), 6);
        Assert.Equal(-250, WallDrag.Along(new Pt(1000, 2000), new Pt(1000, 1750), new Pt(0, 1)), 6);
    }

    [Fact]
    public void SidewaysDrag_IsZero()
    {
        Assert.Equal(0, WallDrag.Along(new Pt(0, 0), new Pt(300, 0), new Pt(0, 1)), 6);
        Assert.Equal(0, WallDrag.Along(new Pt(500, 500), new Pt(500, 900), new Pt(1, 0)), 6);
    }

    [Theory]
    [InlineData(14, 10, 10)]
    [InlineData(15, 10, 20)]
    [InlineData(5, 10, 10)]
    [InlineData(-5, 10, -10)]
    [InlineData(-15, 10, -20)]
    [InlineData(24, 50, 0)]
    [InlineData(25, 50, 50)]
    [InlineData(-25, 50, -50)]
    [InlineData(75, 50, 100)]
    [InlineData(-74, 50, -50)]
    [InlineData(140, 100, 100)]
    [InlineData(150, 100, 200)]
    public void Snap_RoundsToTheStep_HalvesAwayFromZero(double raw, double step, double expected)
    {
        Assert.Equal(expected, WallDrag.Snap(raw, step), 6);
    }

    [Theory]
    [InlineData(250, 1, 250)]
    [InlineData(250, -1, -250)]
    [InlineData(250, 0, 250)]
    [InlineData(-250, 1, -250)]
    [InlineData(-250, -1, 250)]
    public void Typed_PositiveFollowsTheMouse_ZeroIsOutward(double number, int sign, double expected)
    {
        Assert.Equal(expected, WallDrag.Typed(number, sign), 6);
    }

    [Fact]
    public void Toward_NamesTheCompass_AndDropsUnderOneStep()
    {
        var ew = EastWest();
        var ns = NorthSouth();
        Assert.Equal(("north", 300.0), WallDrag.Toward(ew, 300));
        Assert.Equal(("south", 300.0), WallDrag.Toward(ew, -300));
        Assert.Equal(("east", 250.0), WallDrag.Toward(ns, 250));
        Assert.Equal(("west", 100.0), WallDrag.Toward(ns, -80, 50));
        Assert.Null(WallDrag.Toward(ew, 4));
        Assert.Null(WallDrag.Toward(ns, -20, 50));
        Assert.Equal("Not moved: the wall was not dragged.", WallDrag.NotMoved(false));
        Assert.Equal("Ikke flyttet: veggen ble ikke dratt.", WallDrag.NotMoved(true));
    }

    [Fact]
    public void Outward_IsTheWallsOwnSide()
    {
        var ew = EastWest();
        var ns = NorthSouth();
        Assert.Equal(1, WallDrag.Outward("north", ew));
        Assert.Equal(-1, WallDrag.Outward("south", ew));
        Assert.Equal(1, WallDrag.Outward("east", ns));
        Assert.Equal(-1, WallDrag.Outward("west", ns));
        Assert.Equal(0, WallDrag.Outward(null, ew));
        Assert.Equal(0, WallDrag.Outward("", ns));
    }

    [Fact]
    public void Dimension_SnapsAndSpeaksOutInOrASign()
    {
        Assert.Equal("300 mm out", WallDrag.Dimension(304, 10, 1, false));
        Assert.Equal("300 mm ut", WallDrag.Dimension(304, 10, 1, true));
        Assert.Equal("350 mm out", WallDrag.Dimension(325, 50, 1, false));
        Assert.Equal("300 mm in", WallDrag.Dimension(-300, 10, 1, false));
        Assert.Equal("300 mm inn", WallDrag.Dimension(-300, 10, 1, true));
        Assert.Equal("300 mm out", WallDrag.Dimension(-300, 10, -1, false));
        Assert.Equal("300 mm in", WallDrag.Dimension(300, 10, -1, false));
        Assert.Equal("+300 mm", WallDrag.Dimension(300, 10, 0, false));
        Assert.Equal("+300 mm", WallDrag.Dimension(300, 10, 0, true));
        Assert.Equal("-300 mm", WallDrag.Dimension(-300, 10, 0, false));
        Assert.Equal("-300 mm", WallDrag.Dimension(-280, 50, 0, true));
        Assert.Equal("0 mm", WallDrag.Dimension(0, 10, 1, false));
        Assert.Equal("0 mm", WallDrag.Dimension(4, 10, 1, true));
        Assert.Equal("0 mm", WallDrag.Dimension(0, 10, 0, false));
        Assert.Equal("200 mm out", WallDrag.Dimension(150, 100, 1, false));
    }

    [Fact]
    public void Measure_RunsFromTheFaceAlongTheNormal()
    {
        var (from, to) = WallDrag.Measure(EastWest(), 300);
        Assert.Equal(2000, from.X, 6);
        Assert.Equal(200, from.Y, 6);
        Assert.Equal(2000, to.X, 6);
        Assert.Equal(500, to.Y, 6);

        (from, to) = WallDrag.Measure(EastWest(), -300);
        Assert.Equal(2000, from.X, 6);
        Assert.Equal(0, from.Y, 6);
        Assert.Equal(2000, to.X, 6);
        Assert.Equal(-300, to.Y, 6);

        (from, to) = WallDrag.Measure(NorthSouth(), 0);
        Assert.Equal(from.X, to.X, 6);
        Assert.Equal(from.Y, to.Y, 6);
    }

    [Fact]
    public void OneStraightRun_CanBeDragged()
    {
        var records = new List<List<List<Pt>>> { new() { WallJoinsTests.Rect(0, 0, 4000, 200) } };
        var graph = WallJoins.Build(records, new List<int> { 0 }, 1);
        var only = WallJoins.RunIn(graph, records[0]);
        Assert.True(only >= 0);
        Assert.Null(WallDrag.Check(graph, records[0], graph.Runs[only]));
    }

    [Fact]
    public void OneRunInsideAJoinedCluster_CanBeDragged()
    {
        var whole = new List<List<List<Pt>>> { WallJoinsTests.Garage() };
        var pieces = WallSplit.Pieces(WallJoins.Build(whole, new List<int> { 0 }, 1), 1, out var why);
        Assert.True(pieces != null, why);
        var split = pieces.Select(p => new List<List<Pt>> { p.Ring }).ToList();
        var north = split.FindIndex(r => r[0].All(p => p.Y >= 3800));
        var graph = WallJoins.Build(split, WallJoins.ClusterOf(split, north, 1), 1);
        var only = WallJoins.RunIn(graph, split[north]);
        Assert.Null(WallDrag.Check(graph, split[north], graph.Runs[only]));
    }

    [Fact]
    public void WholePlan_AsksToSplit_WithNoCoordinates()
    {
        var records = new List<List<List<Pt>>> { WallJoinsTests.Garage() };
        var graph = WallJoins.Build(records, new List<int> { 0 }, 1);
        var why = WallDrag.Check(graph, records[0], null);
        Assert.Equal("Drag works on one wall. Split walls for picking first.", why);
        Assert.DoesNotContain("(", why);
        Assert.Equal("Du kan dra én vegg. Del veggene for plukking først.",
            WallDrag.Check(graph, records[0], null, 1, true));
    }

    [Fact]
    public void CurvedWall_StaysOneRecord_WithNoCoordinates()
    {
        var arc = new List<Pt>();
        for (var i = 0; i <= 10; i++)
        {
            var a = Math.PI / 2 * i / 10.0;
            arc.Add(new Pt(3000 * Math.Cos(a), 3000 * Math.Sin(a)));
        }
        var records = new List<List<List<Pt>>> { new() { WallJoinsTests.Mitre(arc, 200) } };
        var graph = WallJoins.Build(records, new List<int> { 0 }, 1);
        var why = WallDrag.Check(graph, records[0], null);
        Assert.Equal("A curved wall can't be dragged; it stays one record.", why);
        Assert.DoesNotContain("(", why);
        Assert.Equal("En buet vegg kan ikke dras. Den blir stående som én vegg.",
            WallDrag.Check(graph, records[0], null, 1, true));
    }

    [Fact]
    public void ARunFromAnotherWall_IsTheSplitSentence()
    {
        var records = new List<List<List<Pt>>> { new() { WallJoinsTests.Rect(0, 0, 4000, 200) } };
        var graph = WallJoins.Build(records, new List<int> { 0 }, 1);
        Assert.Equal(WallDrag.Many(false), WallDrag.Check(graph, records[0], NorthSouth()));
        Assert.Equal(WallDrag.Many(false), WallDrag.Check(null, records[0], null));
    }

    [Fact]
    public void Receipt_NamesOutOrIn_OrTheCompass_AndWhoFollowed()
    {
        Assert.Equal("North wall moved 300 mm out", WallDrag.Receipt("the north wall", "north", 300, "north", false));
        Assert.Equal("Nordveggen flyttet 300 mm ut", WallDrag.Receipt("nordveggen", "north", 300.4, "north", true));
        Assert.Equal("North wall moved 300 mm in", WallDrag.Receipt("the north wall", "south", 300, "north", false));
        Assert.Equal("Nordveggen flyttet 300 mm inn", WallDrag.Receipt("nordveggen", "south", 300, "north", true));
        Assert.Equal("South wall moved 250 mm out", WallDrag.Receipt("the south wall", "south", 250, "south", false));
        Assert.Equal("Sørveggen flyttet 250 mm ut", WallDrag.Receipt("sørveggen", "south", 250, "south", true));
        Assert.Equal("East wall moved 100 mm in", WallDrag.Receipt("the east wall", "west", 100, "east", false));
        Assert.Equal("Vestveggen flyttet 100 mm ut", WallDrag.Receipt("vestveggen", "west", 100, "west", true));

        Assert.Equal("Wall between Kitchen and Bath moved 300 mm east",
            WallDrag.Receipt("wall between Kitchen and Bath", "east", 300, null, false));
        Assert.Equal("Vegg mellom Kitchen og Bath flyttet 300 mm mot øst",
            WallDrag.Receipt("vegg mellom Kitchen og Bath", "east", 299.6, "", true));
        Assert.Equal("Wall of Kitchen moved 300 mm north",
            WallDrag.Receipt("wall of Kitchen", "north", 300, null, false));

        Assert.Equal("North wall moved 300 mm out; 1 wall followed",
            WallDrag.Receipt("the north wall", "north", 300, "north", false, 1));
        Assert.Equal("North wall moved 300 mm out; 2 walls followed",
            WallDrag.Receipt("the north wall", "north", 300, "north", false, 2));
        Assert.Equal("Nordveggen flyttet 300 mm ut; 1 vegg fulgte",
            WallDrag.Receipt("nordveggen", "north", 300, "north", true, 1));
        Assert.Equal("Vegg mellom Kitchen og Bath flyttet 300 mm mot øst; 2 vegger fulgte",
            WallDrag.Receipt("vegg mellom Kitchen og Bath", "east", 300, null, true, 2));
        Assert.DoesNotContain("followed", WallDrag.Receipt("the north wall", "north", 300, "north", false, 0));
        Assert.DoesNotContain(".", WallDrag.Receipt("the north wall", "north", 300, "north", false, 2));
    }

    [Fact]
    public void Plain_DropsTheCoordinateClause()
    {
        Assert.Equal("Not moved: the wall would cross another wall.",
            WallDrag.Plain("Not moved: the wall would cross another wall near (4000, 2000)."));
        Assert.Equal("Not moved: a wall meets it at under 15°.",
            WallDrag.Plain("Not moved: a wall meets it at under 15° near (0, 0)."));
        Assert.Equal("Not moved: 500 mm would close the room or wall beyond it, 200 mm deep.",
            WallDrag.Plain("Not moved: 500 mm would close the room or wall beyond it, 200 mm deep."));
        Assert.Equal("", WallDrag.Plain("  "));
    }
}
