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
    public void DiagonalNeighbour_Turns_AndACrossingIsRefused()
    {
        // A ring whose east wall leans: the north wall's east end follows it naively.
        var rings = new List<List<Pt>>
        {
            new List<Pt> { new(0, 0), new(8000, 0), new(9000, 4000), new(0, 4000) },
            new List<Pt> { new(200, 200), new(7845, 200), new(8745, 3800), new(200, 3800) }
        };
        Assert.True(WallEdit.TryPickSide(rings, "north", Tol, out var run, out var why), why);
        Assert.True(WallEdit.TryMove(rings, run, -1000, Tol, out var moved, out why), why);
        Assert.Contains(new Pt(9000, 3000), moved[0]);
        Assert.Contains(new Pt(8745, 2800), moved[1]);
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
    public void WallMoves_RouteToEdit(string text)
    {
        Assert.Equal(ForskIntent.Edit, ForskIntentRouter.Classify(text, ""));
    }

    [Theory]
    [InlineData("walls 3000")]
    [InlineData("generate walls 2700")]
    [InlineData("wall height 2700")]
    public void WallHeights_StayBuild(string text)
    {
        Assert.Equal(ForskIntent.Build, ForskIntentRouter.Classify(text, ""));
    }

    static List<List<Pt>> Garage() => new() { Rect(0, 0, 8000, 4000), Rect(200, 200, 7800, 3800) };

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
