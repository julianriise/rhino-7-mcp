using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// The sample house's front door is a gap in the south wall, so the walls are
/// one piece whose outer loop runs in through the gap. The floor built from
/// that loop covered only the walls (14.9 m² of 118). Closing the gap at the
/// door gives the house's outline.
/// </summary>
public class DoorGapOutlineTests
{
    const double Tol = 1.0;
    const double M2 = 1000000.0;

    static List<Pt> WallLoop()
    {
        var loops = RoomDetect.Union(SampleHouse.Walls.Select(SampleHouse.Ring).ToList(), Tol)
            .Select(loop => RoomDetect.Simplify(loop, Tol)).ToList();
        return loops.OrderByDescending(loop => System.Math.Abs(RoomDetect.Area(loop))).First();
    }

    static List<RoomDetect.Box> Doors() =>
        SampleHouse.Doors.Select(d => new RoomDetect.Box(d.X0, d.Y0, d.X1, d.Y1)).ToList();

    [Fact]
    public void TheSampleHouseWalls_ReadAsOnePieceRunningInThroughTheFrontDoor()
    {
        var loop = WallLoop();
        Assert.InRange(System.Math.Abs(RoomDetect.Area(loop)) / M2, 10, 20);
    }

    [Fact]
    public void ClosingTheFrontDoorGap_GivesTheHouseOutline()
    {
        var closed = WallWithGapsClosed(Doors());
        Assert.Equal(SampleHouse.WidthMm * SampleHouse.DepthMm / M2, System.Math.Abs(RoomDetect.Area(closed)) / M2, 2);
        Assert.All(closed, p => Assert.True(p.X < Tol || p.X > SampleHouse.WidthMm - Tol || p.Y < Tol || p.Y > SampleHouse.DepthMm - Tol));
    }

    [Fact]
    public void ADoorMarkerOnlyFiftyDeep_StillClosesTheGap()
    {
        // A placed marker is the door's width along the wall and 50 mm across it, centred in the wall.
        var marker = new List<RoomDetect.Box> { new RoomDetect.Box(7750, 75, 8650, 125) };
        Assert.Equal(118.08, System.Math.Abs(RoomDetect.Area(WallWithGapsClosed(marker))) / M2, 2);
    }

    [Fact]
    public void AFrontGapWithoutADoor_StaysOpen()
    {
        var loop = WallLoop();
        var inner = SampleHouse.Doors.Where(d => d.Name != "entry").Select(d => new RoomDetect.Box(d.X0, d.Y0, d.X1, d.Y1)).ToList();
        // The inner doors close the rooms behind them, but the outline still runs in through the front gap round the hall.
        var open = WallFollowPlan.CloseDoorGaps(loop, inner, Tol);
        Assert.True(System.Math.Abs(RoomDetect.Area(open)) / M2 < 118.08 - 5);
        Assert.Contains(open, p => p.X > 1000 && p.X < 13000 && p.Y > 1000 && p.Y < 7000);
        var away = new List<RoomDetect.Box> { new RoomDetect.Box(20000, 20000, 21000, 21000) };
        Assert.Equal(loop, WallFollowPlan.CloseDoorGaps(loop, away, Tol));
    }

    [Fact]
    public void AClosedOutline_ComesBackUnchanged()
    {
        var rect = new List<Pt> { new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 3000), new Pt(0, 3000) };
        Assert.Equal(rect, WallFollowPlan.CloseDoorGaps(rect, Doors(), Tol));
    }

    [Fact]
    public void AMovedFrontWall_ClosesTheSameWay()
    {
        // The east wall moved 300 east, its door gap and door with it.
        var walls = SampleHouse.Walls.Select(w => w.Name is "east" ? Shift(SampleHouse.Ring(w), 300, 0) : SampleHouse.Ring(w)).ToList();
        walls.Add(SampleHouse.Ring(new SampleHouse.Rect("ext", 14200, 0, 14500, 200)));
        walls.Add(SampleHouse.Ring(new SampleHouse.Rect("ext", 14200, 8000, 14500, 8200)));
        var loop = RoomDetect.Union(walls, Tol).Select(l => RoomDetect.Simplify(l, Tol))
            .OrderByDescending(l => System.Math.Abs(RoomDetect.Area(l))).First();
        var closed = WallFollowPlan.CloseDoorGaps(loop, Doors(), Tol);
        Assert.Equal(14.7 * 8.2, System.Math.Abs(RoomDetect.Area(closed)) / M2, 2);
    }

    static List<Pt> WallWithGapsClosed(List<RoomDetect.Box> doors) => WallFollowPlan.CloseDoorGaps(WallLoop(), doors, Tol);

    static List<Pt> Shift(List<Pt> ring, double dx, double dy) => ring.Select(p => new Pt(p.X + dx, p.Y + dy)).ToList();
}

/// <summary>
/// Properties panel thickness on the sample house: the north wall's outside
/// face looks out only once the front door gap is closed at its door.
/// </summary>
public class DoorGapOutsideFaceTests
{
    const double Tol = 1.0;

    [Fact]
    public void TheNorthWall_LooksOut_OnceTheFrontDoorGapIsClosed()
    {
        var records = SampleHouse.Walls.Select(w => new List<List<Pt>> { SampleHouse.Ring(w) }).ToList();
        var graph = WallJoins.Build(records, Enumerable.Range(0, records.Count).ToList(), Tol);
        Assert.NotNull(graph);
        var north = graph!.Runs.Single(r => System.Math.Abs(r.Normal.Y) > 0.9 && r.Length > 10000
            && System.Math.Abs(WallJoins.Middle(r).Y - 8100) < 1);
        Assert.Equal(0, WallFace.OutsideSide(graph, north, Tol));
        var doors = SampleHouse.Doors.Select(d => new RoomDetect.Box(d.X0, d.Y0, d.X1, d.Y1)).ToList();
        var outline = WallFollowPlan.CloseDoorGaps(graph.Shape[0], doors, Tol);
        var side = WallFace.OutsideSide(graph, north, Tol, outline);
        Assert.NotEqual(0, side);
        // The face that looks out is the one at y 8200.
        var middle = WallJoins.Middle(north);
        Assert.True(middle.Y + side * north.Normal.Y * 200 > 8200);
    }
}
