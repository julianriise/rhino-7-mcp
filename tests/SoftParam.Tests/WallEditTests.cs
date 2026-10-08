using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// F3.1: one straight wall run inside a wall record moves, both faces, and the
/// walls that meet it stretch. A move that would close a room or cross a wall
/// is refused with the depth it ran into.
/// </summary>
public class WallEditTests
{
    const double Tol = 1.0;

    [Fact]
    public void Side_North_IsTheGarageNorthWall()
    {
        Assert.True(WallEdit.TryPickSide(Garage(), "north", Tol, out var run, out var why), why);
        Assert.Equal(3800, run.Near, 6);
        Assert.Equal(4000, run.Far, 6);
        Assert.Equal(200, run.Thickness, 6);
        Assert.Equal(8000, run.Length, 6);
        Assert.Equal(2, run.Edges.Count);
        Assert.Equal("east–west", WallEdit.Runs(run));
    }

    [Theory]
    [InlineData(4000, 3900)]
    [InlineData(4000, 4500)]
    [InlineData(4000, 3500)]
    public void Pick_InTheWallOrBesideEitherFace_FindsTheSameRun(double x, double y)
    {
        Assert.True(WallEdit.TryPick(Garage(), new Pt(x, y), Tol, out var run, out var why), why);
        Assert.Equal(3800, run.Near, 6);
        Assert.Equal(4000, run.Far, 6);
        Assert.Equal(8000, run.Length, 6);
    }

    [Fact]
    public void Pick_FarFromAnyWall_IsRefused()
    {
        Assert.False(WallEdit.TryPick(Garage(), new Pt(4000, 9000), Tol, out _, out var why));
        Assert.Equal("No wall face within 1000 mm of (4000, 9000).", why);
    }

    [Fact]
    public void MoveNorth500_MovesBothFaces_AndTheSideWallsStretch()
    {
        var rings = Garage();
        Assert.True(WallEdit.TryPickSide(rings, "north", Tol, out var run, out _));
        Assert.True(WallEdit.TryToward(run, "north", 500, out var by, out var why), why);
        Assert.Equal(500, by, 6);
        Assert.True(WallEdit.TryMove(rings, run, by, Tol, out var moved, out why), why);

        Assert.Equal(Box(0, 0, 8000, 4500), Sorted(moved[0]));
        Assert.Equal(Box(200, 200, 7800, 4300), Sorted(moved[1]));
        // The room is 500 mm deeper; the wall is as thick as before.
        Assert.Equal(7600.0 * 4100.0, Math.Abs(RoomDetect.Area(moved[1])), 3);
    }

    [Fact]
    public void MoveSouth_ClosesTheRoom_IsRefusedWithItsDepth()
    {
        var rings = Garage();
        Assert.True(WallEdit.TryPickSide(rings, "north", Tol, out var run, out _));
        Assert.True(WallEdit.TryToward(run, "south", 3700, out var by, out _));
        Assert.Equal(-3700, by, 6);
        Assert.False(WallEdit.TryMove(rings, run, by, Tol, out var moved, out var why));
        Assert.Null(moved);
        Assert.Equal("Not moved: 3700 mm would close the room or wall beyond it, 3600 mm deep.", why);

        Assert.True(WallEdit.TryMove(rings, run, -3500, Tol, out moved, out why), why);
        Assert.Equal(Box(200, 200, 7800, 300), Sorted(moved[1]));
    }

    [Fact]
    public void Toward_AlongTheRun_IsRefused()
    {
        Assert.True(WallEdit.TryPickSide(Garage(), "north", Tol, out var run, out _));
        Assert.False(WallEdit.TryToward(run, "east", 500, out _, out var why));
        Assert.Equal("That wall runs east–west, so it moves north or south.", why);
    }

    [Fact]
    public void Partition_MovesEast_OneRoomGrowsTheOtherShrinks()
    {
        var rings = TwoRooms();
        Assert.True(WallEdit.TryPick(rings, new Pt(4000, 2000), Tol, out var run, out var why), why);
        Assert.Equal(3900, run.Near, 6);
        Assert.Equal(4100, run.Far, 6);
        Assert.Equal(3600, run.Length, 6);
        Assert.Equal("north–south", WallEdit.Runs(run));
        Assert.True(WallEdit.TryToward(run, "east", 500, out var by, out _));
        Assert.True(WallEdit.TryMove(rings, run, by, Tol, out var moved, out why), why);

        Assert.Equal(Box(0, 0, 8000, 4000), Sorted(moved[0]));
        Assert.Equal(Box(200, 200, 4400, 3800), Sorted(moved[1]));
        Assert.Equal(Box(4600, 200, 7800, 3800), Sorted(moved[2]));
    }

    [Fact]
    public void Partition_PastTheNextWall_IsRefused()
    {
        var rings = TwoRooms();
        Assert.True(WallEdit.TryPick(rings, new Pt(4000, 2000), Tol, out var run, out _));
        Assert.False(WallEdit.TryMove(rings, run, 3800, Tol, out _, out var why));
        Assert.Equal("Not moved: 3800 mm would close the room or wall beyond it, 3700 mm deep.", why);
    }

    [Fact]
    public void ExteriorWall_OverTwoRooms_IsOneRun_AndThePartitionStretches()
    {
        var rings = TwoRooms();
        Assert.True(WallEdit.TryPickSide(rings, "north", Tol, out var run, out _));
        // The outer face and both rooms' north faces.
        Assert.Equal(3, run.Edges.Count);
        Assert.True(WallEdit.TryMove(rings, run, 500, Tol, out var moved, out var why), why);
        Assert.Equal(Box(200, 200, 3900, 4300), Sorted(moved[1]));
        Assert.Equal(Box(4100, 200, 7800, 4300), Sorted(moved[2]));
    }

    [Fact]
    public void FreeStandingRun_MovesWhole()
    {
        var rings = new List<List<Pt>> { Rect(1000, 1000, 4000, 1200) };
        Assert.True(WallEdit.TryPick(rings, new Pt(2000, 900), Tol, out var run, out var why), why);
        Assert.Equal(200, run.Thickness, 6);
        Assert.True(WallEdit.TryMove(rings, run, 300, Tol, out var moved, out why), why);
        Assert.Equal(Box(1000, 1300, 4000, 1500), Sorted(moved[0]));
    }

    [Fact]
    public void Crossing_TheArmsAreSeparateRuns()
    {
        // Four rooms around a + of 200 mm walls in a 200 mm ring.
        var rings = new List<List<Pt>>
        {
            Rect(0, 0, 8200, 8200),
            Rect(200, 200, 4000, 4000),
            Rect(4200, 200, 8000, 4000),
            Rect(200, 4200, 4000, 8000),
            Rect(4200, 4200, 8000, 8000)
        };
        Assert.True(WallEdit.TryPick(rings, new Pt(4100, 2000), Tol, out var south, out var why), why);
        Assert.Equal(3800, south.Length, 6);
        Assert.True(WallEdit.TryMove(rings, south, 300, Tol, out var moved, out why), why);
        // The south arm moved; the north arm stayed.
        Assert.Equal(Box(200, 200, 4300, 4000), Sorted(moved[1]));
        Assert.Equal(Box(4500, 200, 8000, 4000), Sorted(moved[2]));
        Assert.Equal(Box(200, 4200, 4000, 8000), Sorted(moved[3]));
    }

    [Fact]
    public void DiagonalNeighbour_SlidesAlongItsOwnLine()
    {
        // A ring whose east wall leans. F2: the north wall's east end slides down
        // the leaning wall's faces, so that wall keeps its line and its thickness.
        var rings = new List<List<Pt>>
        {
            new List<Pt> { new(0, 0), new(8000, 0), new(9000, 4000), new(0, 4000) },
            new List<Pt> { new(200, 200), new(7845, 200), new(8745, 3800), new(200, 3800) }
        };
        Assert.True(WallEdit.TryPickSide(rings, "north", Tol, out var run, out var why), why);
        Assert.True(WallEdit.TryMove(rings, run, -1000, Tol, out var moved, out why), why);
        Assert.Contains(new Pt(8750, 3000), moved[0]);
        Assert.Contains(new Pt(8495, 2800), moved[1]);
    }

    [Fact]
    public void NoFaceAcross_IsRefused()
    {
        // 800 mm walls are thicker than any wall run.
        var rings = new List<List<Pt>> { Rect(0, 0, 8000, 4000), Rect(800, 800, 7200, 3200) };
        Assert.False(WallEdit.TryPickSide(rings, "north", Tol, out _, out var why));
        Assert.Equal("No face across the wall within 600 mm of (4000, 4000).", why);
    }

    [Fact]
    public void InBand_HoldsTheOpeningsOnTheRunOnly()
    {
        Assert.True(WallEdit.TryPickSide(Garage(), "north", Tol, out var run, out _));
        Assert.True(WallEdit.InBand(run, new Pt(2000, 3900), Tol));
        Assert.True(WallEdit.InBand(run, new Pt(100, 3950), Tol));
        Assert.False(WallEdit.InBand(run, new Pt(7900, 2000), Tol));
        Assert.False(WallEdit.InBand(run, new Pt(2000, 3500), Tol));
    }

    [Fact]
    public void AnOpeningOnAWallThatMeetsTheRun_StaysClearOrRefuses()
    {
        var rings = Garage();
        Assert.True(WallEdit.TryPickSide(rings, "north", Tol, out var run, out _));
        // A door on the east wall near its north end, its marker across the wall's middle.
        var door = new RoomDetect.Box(7875, 3000, 7925, 3700);

        Assert.True(WallEdit.TryMove(rings, run, 500, Tol, out var north, out _));
        Assert.True(WallEdit.Clear(north, run, 500, door, Tol));

        // South 500: the north wall now runs across the door.
        Assert.True(WallEdit.TryMove(rings, run, -500, Tol, out var south, out _));
        Assert.False(WallEdit.Clear(south, run, -500, door, Tol));

        // A door at the very top of the east wall: the same move leaves it past the wall's new end.
        Assert.False(WallEdit.Clear(south, run, -500, new RoomDetect.Box(7875, 3850, 7925, 3950), Tol));
    }

    /// <summary>
    /// The sample house (first_run.py): its doors stand in gaps between wall
    /// records, the marker's ends on the two jambs. A move that leaves both
    /// jambs where they were leaves every such door clear.
    /// </summary>
    [Theory]
    [InlineData(100, 4000, "west", 500)]
    [InlineData(14300, 4000, "east", 500)]
    [InlineData(7000, 8100, "north", 500)]
    [InlineData(12000, 5300, "north", 300)]
    public void SampleHouse_AMoveThatKeepsTheJambs_LeavesEveryGapDoorClear(double x, double y, string toward, double mm)
    {
        var records = SampleHouse();
        var g = WallJoins.Build(records, WallJoins.ClusterOf(records, 0, Tol), Tol);
        Assert.Equal(records.Count, g.Records.Count);
        Assert.True(WallEdit.TryPick(g.Shape, new Pt(x, y), Tol, out var run, out var why), why);
        Assert.True(WallEdit.TryToward(run, toward, mm, out var by, out why), why);
        Assert.True(WallJoins.TryMove(records, g, run, by, Tol, out var moved, out why), why);
        foreach (var door in SampleHouseDoors())
        {
            var center = new Pt((door.MinX + door.MaxX) / 2, (door.MinY + door.MaxY) / 2);
            if (WallEdit.InBand(run, center, Tol)) continue;
            Assert.True(WallEdit.Clear(moved.Shape, run, by, door, Tol), $"door at {door.MinX},{door.MinY}");
        }
    }

    [Fact]
    public void SampleHouse_AMoveThatTakesAJambAway_StillRefuses()
    {
        var records = SampleHouse();
        var g = WallJoins.Build(records, WallJoins.ClusterOf(records, 0, Tol), Tol);
        Assert.True(WallEdit.TryPick(g.Shape, new Pt(4500, 3300), Tol, out var run, out var why), why);
        Assert.True(WallEdit.TryToward(run, "north", 300, out var by, out why), why);
        Assert.True(WallJoins.TryMove(records, g, run, by, Tol, out var moved, out why), why);
        // The door between w10 and the moved w12 keeps only one jamb.
        Assert.False(WallEdit.Clear(moved.Shape, run, by, new RoomDetect.Box(2550, 3200, 3450, 3400), Tol));
    }

    [Fact]
    public void DeleteNorth_OpensTheRingIntoAU()
    {
        var rings = Garage();
        Assert.True(WallEdit.TryPickSide(rings, "north", Tol, out var run, out _));
        Assert.True(WallEdit.TryDelete(rings, run, Tol, out var left, out var why), why);
        Assert.Single(left);
        // The corners go with the run: the side walls end at the old inner face.
        Assert.Equal(Box(0, 0, 8000, 3800), Sorted(left[0]));
        Assert.Equal(8000.0 * 200 + 2 * 200.0 * 3600, RoomDetect.Area(left[0]), 3);
    }

    [Fact]
    public void DeletePartition_TheTwoRoomsBecomeOne()
    {
        var rings = TwoRooms();
        Assert.True(WallEdit.TryPick(rings, new Pt(4000, 2000), Tol, out var run, out _));
        Assert.True(WallEdit.TryDelete(rings, run, Tol, out var left, out var why), why);
        Assert.Equal(2, left.Count);
        Assert.Equal(Box(0, 0, 8000, 4000), Sorted(left[0]));
        Assert.Equal(Box(200, 200, 7800, 3800), Sorted(left[1]));
        Assert.Equal(-7600.0 * 3600.0, RoomDetect.Area(left[1]), 3);
    }

    [Fact]
    public void DeleteFreeStanding_TakesTheWholeRecord()
    {
        var rings = new List<List<Pt>> { Rect(1000, 1000, 4000, 1200) };
        Assert.True(WallEdit.TryPick(rings, new Pt(2000, 1100), Tol, out var run, out _));
        Assert.True(WallEdit.TryDelete(rings, run, Tol, out var left, out var why), why);
        Assert.Null(left);
    }

    [Fact]
    public void Delete_ThatSplitsTheRecord_IsRefused()
    {
        // The U left after the north wall went: its south wall holds the two arms together.
        var u = new List<List<Pt>>
        {
            new List<Pt> { new(0, 0), new(8000, 0), new(8000, 3800), new(7800, 3800), new(7800, 200), new(200, 200), new(200, 3800), new(0, 3800) }
        };
        Assert.True(WallEdit.TryPickSide(u, "south", Tol, out var run, out var why), why);
        Assert.False(WallEdit.TryDelete(u, run, Tol, out _, out why));
        Assert.Equal("Not deleted: the walls left would stand in 2 separate pieces, and this version keeps one wall record.", why);
    }

    [Fact]
    public void Holds_AnOpeningPastTheEndOfItsShortenedWall_DoesNot()
    {
        var rings = Garage();
        Assert.True(WallEdit.TryPickSide(rings, "north", Tol, out var run, out _));
        Assert.True(WallEdit.TryDelete(rings, run, Tol, out var left, out _));
        // A window on the east wall: one low enough stays held, one running into the old corner does not.
        Assert.True(WallEdit.Holds(left, new RoomDetect.Box(7875, 2000, 7925, 3200)));
        Assert.False(WallEdit.Holds(left, new RoomDetect.Box(7875, 3000, 7925, 3900)));
    }

    [Fact]
    public void Add_APartitionAcrossTheRoom_JoinsTheRecord_AndSplitsTheRoom()
    {
        var records = new List<List<List<Pt>>> { Garage() };
        Assert.True(WallEdit.TryAdd(records, new Pt(5000, 200), new Pt(5000, 3800), 100, Tol, out var added, out var why), why);
        Assert.Equal(0, added.Joined);
        // Ends on the faces stay on them: they do not run on through the wall.
        Assert.Equal(new Pt(5000, 200), added.From);
        Assert.Equal(new Pt(5000, 3800), added.To);
        Assert.Equal(3, added.Rings.Count);
        Assert.Equal(Box(0, 0, 8000, 4000), Sorted(added.Rings[0]));
        Assert.Contains(added.Rings.Skip(1), ring => Sorted(ring) == Box(200, 200, 4950, 3800));
        Assert.Contains(added.Rings.Skip(1), ring => Sorted(ring) == Box(5050, 200, 7800, 3800));

        // The partition is a run of its own: a delete gives back the one room.
        Assert.True(WallEdit.TryPick(added.Rings, new Pt(5000, 2000), Tol, out var run, out why), why);
        Assert.Equal(100, run.Thickness, 6);
        Assert.True(WallEdit.TryDelete(added.Rings, run, Tol, out var left, out why), why);
        Assert.Equal(2, left.Count);
        Assert.Equal(Box(200, 200, 7800, 3800), Sorted(left[1]));
    }

    [Fact]
    public void Add_EndsShortOfTheFaces_RunOnToThem()
    {
        var records = new List<List<List<Pt>>> { Garage() };
        Assert.True(WallEdit.TryAdd(records, new Pt(5000, 450), new Pt(5000, 3600), 100, Tol, out var added, out var why), why);
        Assert.Equal(new Pt(5000, 200), added.From);
        Assert.Equal(new Pt(5000, 3800), added.To);
        Assert.Equal(3, added.Rings.Count);
    }

    [Fact]
    public void Add_TooFarFromAWall_StandsFree_AsItsOwnRecord()
    {
        var records = new List<List<List<Pt>>> { Garage() };
        Assert.True(WallEdit.TryAdd(records, new Pt(2000, 6000), new Pt(6000, 6000), 200, Tol, out var added, out var why), why);
        Assert.Equal(-1, added.Joined);
        Assert.Single(added.Rings);
        Assert.Equal(Box(2000, 5900, 6000, 6100), Sorted(added.Rings[0]));
        // A free wall in the room touches nothing either.
        Assert.True(WallEdit.TryAdd(records, new Pt(2000, 2000), new Pt(4000, 2000), 100, Tol, out var inRoom, out why), why);
        Assert.Equal(-1, inRoom.Joined);
    }

    [Fact]
    public void Add_TouchingTwoRecords_StandsOnItsOwn_AndJoinsThemInOneCluster()
    {
        var records = new List<List<List<Pt>>>
        {
            Garage(),
            new List<List<Pt>> { Rect(2000, 5900, 6000, 6100) }
        };
        Assert.True(WallEdit.TryAdd(records, new Pt(4000, 4000), new Pt(4000, 5900), 200, Tol, out var added, out var why), why);
        Assert.Equal(-1, added.Joined);
        Assert.Equal(new[] { 0, 1 }, added.Touches);
        Assert.Equal(Box(3900, 4000, 4100, 5900), Sorted(Assert.Single(added.Rings)));
        // F2: the three records share ends, so the join graph reads them as one.
        Assert.Equal(2, WallJoins.Clusters(records, Tol).Count);
        records.Add(added.Rings);
        Assert.Single(WallJoins.Clusters(records, Tol));
    }

    [Fact]
    public void Add_RefusesABadThicknessOrAStub()
    {
        var records = new List<List<List<Pt>>> { Garage() };
        Assert.False(WallEdit.TryAdd(records, new Pt(0, 6000), new Pt(4000, 6000), 0, Tol, out _, out var why));
        Assert.Equal("thickness is above 0 and at most 600 mm.", why);
        Assert.False(WallEdit.TryAdd(records, new Pt(0, 6000), new Pt(150, 6000), 200, Tol, out _, out why));
        Assert.Equal("The wall is no longer than it is thick: give two points further apart.", why);
    }

    [Fact]
    public void InTheWay_AnOpeningUnderTheNewWall()
    {
        var records = new List<List<List<Pt>>> { Garage() };
        // Drawn from the south wall's centre: the strip runs into the wall, over its door.
        Assert.True(WallEdit.TryAdd(records, new Pt(5000, 100), new Pt(5000, 3800), 100, Tol, out var added, out _));
        Assert.True(WallEdit.InTheWay(added, 100, new RoomDetect.Box(4600, 75, 5500, 125), Tol));
        Assert.False(WallEdit.InTheWay(added, 100, new RoomDetect.Box(1200, 75, 2100, 125), Tol));
    }

    [Fact]
    public void Path_RoundTrips_InThePluginsForm()
    {
        const string path = "{\"outer\":[[0,0],[8000,0],[8000,4000],[0,4000]],\"holes\":[[[200,200],[7800,200],[7800,3800],[200,3800]]]}";
        var rings = WallEdit.Rings(path);
        Assert.Equal(2, rings.Count);
        Assert.Equal("{\"outer\":[[0.0,0.0],[8000.0,0.0],[8000.0,4000.0],[0.0,4000.0]],\"holes\":[[[200.0,200.0],[7800.0,200.0],[7800.0,3800.0],[200.0,3800.0]]]}",
            WallEdit.Path(rings));
        Assert.Null(WallEdit.Rings("{\"outer\":[[0,0],[1,1]]}"));
        Assert.Null(WallEdit.Rings("not json"));
    }

    [Theory]
    [InlineData("move the north wall 500 mm north")]
    [InlineData("move this wall 300 east")]
    [InlineData("nudge the partition 200 mm west")]
    [InlineData("flytt veggen 500 mot nord")]
    [InlineData("move the window 200 along the wall")]
    [InlineData("delete the north wall")]
    [InlineData("remove this wall")]
    [InlineData("fjern veggen")]
    [InlineData("slett skilleveggen mellom rommene")]
    [InlineData("add a wall from 5000,200 to 5000,3800")]
    [InlineData("draw a partition across the garage")]
    [InlineData("make this line a wall")]
    [InlineData("tegn en vegg")]
    public void WallEdits_RouteToEdit(string text)
    {
        Assert.Equal(ForskIntent.Edit, ForskIntentRouter.Classify(text));
    }

    [Theory]
    [InlineData("walls 3000")]
    [InlineData("generate walls 2700")]
    [InlineData("wall height 2700")]
    [InlineData("make the wall 3000 high")]
    [InlineData("delete the walls and rebuild")]
    public void WallHeights_StayBuild(string text)
    {
        Assert.Equal(ForskIntent.Build, ForskIntentRouter.Classify(text));
    }

    static List<List<Pt>> Garage() => new() { Rect(0, 0, 8000, 4000), Rect(200, 200, 7800, 3800) };

    /// <summary>The sample house's 19 wall records (/tmp/forsk-houseprobe-built.3dm), one rectangle each.</summary>
    internal static List<List<List<Pt>>> SampleHouse() => new double[][]
    {
        new double[] { 10600, 5200, 14200, 5400 }, new double[] { 3450, 3200, 5800, 3400 }, new double[] { 200, 3200, 2550, 3400 },
        new double[] { 5800, 1850, 6000, 8000 }, new double[] { 5800, 200, 6000, 950 }, new double[] { 200, 0, 7750, 200 },
        new double[] { 8650, 0, 14200, 200 }, new double[] { 8650, 2600, 10400, 2800 }, new double[] { 6000, 2600, 7750, 2800 },
        new double[] { 8650, 4800, 10400, 5000 }, new double[] { 6000, 4800, 7750, 5000 }, new double[] { 10400, 200, 10600, 950 },
        new double[] { 10400, 1850, 10600, 6250 }, new double[] { 10400, 7150, 10600, 8000 }, new double[] { 10600, 3400, 11950, 3600 },
        new double[] { 12850, 3400, 14200, 3600 }, new double[] { 0, 0, 200, 8000 }, new double[] { 0, 8000, 14400, 8200 },
        new double[] { 14200, 0, 14400, 8000 },
    }.Select(r => new List<List<Pt>> { Rect(r[0], r[1], r[2], r[3]) }).ToList();

    /// <summary>Its eight door markers, each filling the gap between two wall ends.</summary>
    internal static RoomDetect.Box[] SampleHouseDoors() => new[]
    {
        new RoomDetect.Box(2550, 3200, 3450, 3400), new RoomDetect.Box(5800, 950, 6000, 1850),
        new RoomDetect.Box(7750, 0, 8650, 200), new RoomDetect.Box(7750, 2600, 8650, 2800),
        new RoomDetect.Box(7750, 4800, 8650, 5000), new RoomDetect.Box(10400, 950, 10600, 1850),
        new RoomDetect.Box(10400, 6250, 10600, 7150), new RoomDetect.Box(11950, 3400, 12850, 3600),
    };

    static List<List<Pt>> TwoRooms() => new()
    {
        Rect(0, 0, 8000, 4000),
        Rect(200, 200, 3900, 3800),
        Rect(4100, 200, 7800, 3800)
    };

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    /// <summary>A loop's corners as min/max, so a test reads the box whatever the start vertex.</summary>
    static (double, double, double, double) Sorted(List<Pt> ring) =>
        (ring.Min(p => p.X), ring.Min(p => p.Y), ring.Max(p => p.X), ring.Max(p => p.Y));

    static (double, double, double, double) Box(double x0, double y0, double x1, double y1) => (x0, y0, x1, y1);
}
