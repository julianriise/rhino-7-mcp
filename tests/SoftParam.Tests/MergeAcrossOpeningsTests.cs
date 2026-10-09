using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// No door or window without its wall: wall pieces drawn either side of an
/// opening (as AI detection often draws them) become one wall through it.
/// </summary>
public class MergeAcrossOpeningsTests
{
    const double Tol = 1.0;

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new() { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };

    static RoomDetect.Box Box(double x0, double y0, double x1, double y1) => new(x0, y0, x1, y1);

    [Fact]
    public void TwoPiecesEitherSideOfADoor_BecomeOneWallThroughIt()
    {
        var walls = new List<List<Pt>> { Rect(0, 0, 3000, 200), Rect(3900, 0, 8000, 200) };
        var merged = WallFollowPlan.MergeAcrossOpenings(walls, new[] { Box(3000, 0, 3900, 200) }, Tol, out var joins);
        Assert.Equal(1, joins);
        var wall = Assert.Single(merged);
        Assert.Equal(8000 * 200, System.Math.Abs(RoomDetect.Area(wall)), 1);
        Assert.True(RoomDetect.Contains(wall, new Pt(3450, 100)));
    }

    [Fact]
    public void AWindowShallowerThanTheWall_JoinsItToo()
    {
        var walls = new List<List<Pt>> { Rect(0, 0, 3000, 300), Rect(4200, 0, 8000, 300) };
        var merged = WallFollowPlan.MergeAcrossOpenings(walls, new[] { Box(3000, 125, 4200, 175) }, Tol, out var joins);
        Assert.Equal(1, joins);
        Assert.Single(merged);
    }

    [Fact]
    public void AGapWithNoOpening_StaysTwoWalls()
    {
        var walls = new List<List<Pt>> { Rect(0, 0, 3000, 200), Rect(3900, 0, 8000, 200) };
        var merged = WallFollowPlan.MergeAcrossOpenings(walls, new[] { Box(10000, 0, 11000, 200) }, Tol, out var joins);
        Assert.Equal(0, joins);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void WallsOfDifferentThickness_StaySeparate()
    {
        var walls = new List<List<Pt>> { Rect(0, 0, 3000, 200), Rect(3900, -50, 8000, 250) };
        WallFollowPlan.MergeAcrossOpenings(walls, new[] { Box(3000, 0, 3900, 200) }, Tol, out var joins);
        Assert.Equal(0, joins);
    }

    [Fact]
    public void TheSampleHouse_EveryDoorEndsUpInsideOneWall()
    {
        var walls = SampleHouse.Walls.Select(SampleHouse.Ring).ToList();
        var doors = SampleHouse.Doors.Select(d => Box(d.X0, d.Y0, d.X1, d.Y1)).ToList();
        var merged = WallFollowPlan.MergeAcrossOpenings(walls, doors, Tol, out var joins);
        Assert.Equal(SampleHouse.Doors.Length, joins);
        Assert.Equal(SampleHouse.Walls.Length - SampleHouse.Doors.Length, merged.Count);
        foreach (var d in SampleHouse.Doors)
            Assert.Single(merged, w => RoomDetect.Contains(w, new Pt((d.X0 + d.X1) / 2, (d.Y0 + d.Y1) / 2)));
    }

    [Fact]
    public void TheSampleHouseAsOneOutline_ClosesRoundItsRooms_EveryDoorInTheWall()
    {
        // walls_from_layer reads the house's overlapping wall rectangles as one outline.
        var outline = RoomDetect.Union(SampleHouse.Walls.Select(SampleHouse.Ring).ToList(), Tol)
            .Select(l => RoomDetect.Simplify(l, Tol)).ToList();
        Assert.Single(outline);
        var doors = SampleHouse.Doors.Select(d => Box(d.X0, d.Y0, d.X1, d.Y1)).ToList();
        var loops = WallFollowPlan.MergeAcrossOpenings(outline, doors, Tol, out var joins);
        Assert.Equal(SampleHouse.Doors.Length, joins);
        var outer = loops.OrderByDescending(l => System.Math.Abs(RoomDetect.Area(l))).First();
        Assert.Equal(SampleHouse.WidthMm * SampleHouse.DepthMm, System.Math.Abs(RoomDetect.Area(outer)), 0);
        // Eight rooms are the holes; each door sits in the wall, inside the outline and in no room.
        Assert.Equal(1 + SampleHouse.Rooms.Length, loops.Count);
        foreach (var d in SampleHouse.Doors)
        {
            var c = new Pt((d.X0 + d.X1) / 2, (d.Y0 + d.Y1) / 2);
            Assert.True(RoomDetect.Contains(outer, c));
            Assert.DoesNotContain(loops.Where(l => l != outer), l => RoomDetect.Contains(l, c));
        }
    }
}
