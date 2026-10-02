using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// Selection S1: a whole wall record cut into one piece per run. The garage
/// ring splits as F2's four butting records, a tee keeps the through run, a
/// cross keeps one run whole, and every accepted split unions back to its shape.
/// </summary>
public class WallSplitTests
{
    const double Tol = 1.0;

    [Fact]
    public void Garage_SplitsAsTheFourButtingRecords()
    {
        var pieces = Split(WallJoinsTests.Garage());
        var expected = WallJoinsTests.FourRects().Select(r => Box(r[0])).OrderBy(b => b);
        Assert.Equal(expected, pieces.Select(p => Box(p.Ring)).OrderBy(b => b));
    }

    [Fact]
    public void TwoRooms_FivePieces_ThePartitionStopsAtBothOuterWalls()
    {
        var pieces = Split(WallJoinsTests.TwoRooms());
        Assert.Equal(5, pieces.Count);
        Assert.Contains((3900.0, 200.0, 4100.0, 3800.0), pieces.Select(p => Box(p.Ring)));
        // The north and south walls stay whole through the tees.
        Assert.Contains((0.0, 3800.0, 8000.0, 4000.0), pieces.Select(p => Box(p.Ring)));
        Assert.Contains((0.0, 0.0, 8000.0, 200.0), pieces.Select(p => Box(p.Ring)));
    }

    [Fact]
    public void Cross_ThreePieces_OneRunRunsThrough()
    {
        var pieces = Split(new() { Plus() });
        Assert.Equal(3, pieces.Count);
        Assert.Contains((-3000.0, -100.0, 3000.0, 100.0), pieces.Select(p => Box(p.Ring)));
        Assert.Contains((-100.0, 100.0, 100.0, 3000.0), pieces.Select(p => Box(p.Ring)));
        Assert.Contains((-100.0, -3000.0, 100.0, -100.0), pieces.Select(p => Box(p.Ring)));
    }

    [Fact]
    public void SixtyDegreeL_TwoPieces()
    {
        var dir = new Pt(Math.Cos(Math.PI * 2 / 3), Math.Sin(Math.PI * 2 / 3));
        var l = WallJoinsTests.Mitre(new() { new(0, 0), new(4000, 0), new(4000 + 3000 * dir.X, 3000 * dir.Y) }, 200);
        var pieces = Split(new() { l });
        Assert.Equal(2, pieces.Count);
        // The longer run keeps the corner: the leaning piece starts at its face.
        var leaning = pieces.Single(p => p.Ring.Any(q => q.Y > 1000));
        Assert.True(leaning.Ring.Min(q => q.Y) >= 100 - Tol, string.Join(" ", leaning.Ring.Select(q => $"({q.X:0},{q.Y:0})")));
    }

    [Fact]
    public void Arc_Refuses_AndSaysWhy()
    {
        var arc = new List<Pt>();
        for (var i = 0; i <= 10; i++)
        {
            var a = Math.PI / 2 * i / 10;
            arc.Add(new Pt(3000 * Math.Cos(a), 3000 * Math.Sin(a)));
        }
        var records = new List<List<List<Pt>>> { new() { WallJoinsTests.Mitre(arc, 200) } };
        var graph = WallJoins.Build(records, new List<int> { 0 }, Tol);
        Assert.Null(WallSplit.Pieces(graph, Tol, out var why));
        Assert.Contains("curve", why);
    }

    [Fact]
    public void OneRun_IsOnePiece()
    {
        var pieces = Split(new() { WallJoinsTests.Rect(0, 0, 4000, 200) });
        Assert.Single(pieces);
    }

    [Fact]
    public void SplitGarage_JoinedMove_EqualsTheMoveOnTheWholeGarage()
    {
        var whole = new List<List<List<Pt>>> { WallJoinsTests.Garage() };
        var wholeGraph = WallJoins.Build(whole, new List<int> { 0 }, Tol);
        Assert.True(WallEdit.TryPickSide(wholeGraph.Shape, "north", Tol, out var run, out var why), why);
        Assert.True(WallJoins.TryMove(whole, wholeGraph, run, 500, Tol, out var a, out why), why);

        var split = Split(WallJoinsTests.Garage()).Select(p => new List<List<Pt>> { p.Ring }).ToList();
        var splitGraph = WallJoins.Build(split, WallJoins.ClusterOf(split, 0, Tol), Tol);
        Assert.True(WallEdit.TryPickSide(splitGraph.Shape, "north", Tol, out run, out why), why);
        Assert.True(WallJoins.TryMove(split, splitGraph, run, 500, Tol, out var b, out why), why);

        Assert.Equal(a.Shape.Count, b.Shape.Count);
        for (var k = 0; k < a.Shape.Count; k++) Assert.Equal(Box(a.Shape[k]), Box(b.Shape[k]));
    }

    [Fact]
    public void FacadeOutlines_SplitGarage_IsOneOutline_NotFour()
    {
        var split = Split(WallJoinsTests.Garage()).Select(p => new List<List<Pt>> { p.Ring }).ToList();
        var outline = Assert.Single(WallJoins.Outlines(split, Tol));
        Assert.Equal((0.0, 0.0, 8000.0, 4000.0), Box(outline));
        // A wall standing apart is its own facade.
        split.Add(new() { WallJoinsTests.Rect(10000, 0, 14000, 200) });
        Assert.Equal(2, WallJoins.Outlines(split, Tol).Count);
    }

    [Fact]
    public void AOneRunRecord_IsItsRun_MoveNeedsNoSide()
    {
        // S4: the split garage's north record names the north wall; a move on it equals "side": "north".
        var split = Split(WallJoinsTests.Garage()).Select(p => new List<List<Pt>> { p.Ring }).ToList();
        var north = split.FindIndex(r => r[0].All(p => p.Y >= 3800));
        var graph = WallJoins.Build(split, WallJoins.ClusterOf(split, north, Tol), Tol);
        var run = WallJoins.RunIn(graph, split[north]);
        Assert.Equal("the north wall", graph.Names[run]);
        Assert.Equal("north", WallJoins.Toward(graph, run));
        Assert.True(WallEdit.TryPickSide(graph.Shape, "north", Tol, out var side, out _));
        Assert.Equal(graph.Find(side, Tol), run);
        // The partition of the two rooms is named by its middle and moves east first.
        var rooms = Split(WallJoinsTests.TwoRooms()).Select(p => new List<List<Pt>> { p.Ring }).ToList();
        var partition = rooms.FindIndex(r => r[0].All(p => p.X >= 3900 && p.X <= 4100));
        var g = WallJoins.Build(rooms, WallJoins.ClusterOf(rooms, partition, Tol), Tol);
        var stem = WallJoins.RunIn(g, rooms[partition]);
        Assert.Equal("the wall at (4000, 2000)", g.Names[stem]);
        Assert.Equal("east", WallJoins.Toward(g, stem));
        // A whole record holds every run: no one run is the pick.
        var whole = new List<List<List<Pt>>> { WallJoinsTests.Garage() };
        Assert.Equal(-1, WallJoins.RunIn(WallJoins.Build(whole, new List<int> { 0 }, Tol), whole[0]));
    }

    /// <summary>The record split, with the union checked back against its shape.</summary>
    static List<WallSplit.Piece> Split(List<List<Pt>> rings)
    {
        var records = new List<List<List<Pt>>> { rings };
        var graph = WallJoins.Build(records, new List<int> { 0 }, Tol);
        var pieces = WallSplit.Pieces(graph, Tol, out var why);
        Assert.True(pieces != null, why);
        var union = WallEdit.Group(RoomDetect.Union(pieces.Select(p => p.Ring).ToList(), Tol));
        var region = Assert.Single(union);
        Assert.Equal(rings.Count, region.Count);
        // Within the slack the split allows: tol along the perimeter.
        var slack = Tol * rings.Sum(Perimeter);
        Assert.InRange(Area(region) - Area(rings), -slack, slack);
        Assert.InRange(pieces.Sum(p => RoomDetect.Area(p.Ring)) - Area(rings), -slack, slack);
        return pieces;
    }

    internal static List<Pt> Plus() => new()
    {
        new(-100, -3000), new(100, -3000), new(100, -100), new(3000, -100), new(3000, 100), new(100, 100),
        new(100, 3000), new(-100, 3000), new(-100, 100), new(-3000, 100), new(-3000, -100), new(-100, -100)
    };

    static double Area(List<List<Pt>> rings) =>
        Math.Abs(RoomDetect.Area(rings[0])) - rings.Skip(1).Sum(r => Math.Abs(RoomDetect.Area(r)));

    static double Perimeter(List<Pt> ring) =>
        ring.Select((p, i) => Math.Sqrt(Math.Pow(ring[(i + 1) % ring.Count].X - p.X, 2) + Math.Pow(ring[(i + 1) % ring.Count].Y - p.Y, 2))).Sum();

    static (double, double, double, double) Box(List<Pt> ring) =>
        (Math.Round(ring.Min(p => p.X)), Math.Round(ring.Min(p => p.Y)), Math.Round(ring.Max(p => p.X)), Math.Round(ring.Max(p => p.Y)));
}
