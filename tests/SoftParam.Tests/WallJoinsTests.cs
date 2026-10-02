using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// F2 J1: the join graph. Records that touch are one cluster read as one
/// shape; runs and their corners and tees come from that shape, so four
/// butting wall rectangles read as the garage ring.
/// </summary>
public class WallJoinsTests
{
    const double Tol = 1.0;

    [Fact]
    public void Garage_FourRuns_FourCorners_NamedBySide()
    {
        var g = Graph(new() { Garage() });
        Assert.Equal(4, g.Runs.Count);
        Assert.Equal(4, g.Joins.Count);
        Assert.All(g.Joins, j => Assert.Equal(WallJoins.JoinKind.Corner, j.Kind));
        Assert.Equal(new[] { "the east wall", "the north wall", "the south wall", "the west wall" }, g.Names.OrderBy(n => n));
        var north = g.Names.IndexOf("the north wall");
        Assert.Equal(new[] { "the east wall", "the west wall" }, g.Neighbours(north).Select(i => g.Names[i]).OrderBy(n => n));
    }

    [Fact]
    public void TwoRooms_ThePartitionIsTheStemOfTwoTees()
    {
        var g = Graph(new() { TwoRooms() });
        Assert.Equal(5, g.Runs.Count);
        Assert.Equal(4, g.Joins.Count(j => j.Kind == WallJoins.JoinKind.Corner));
        var tees = g.Joins.Where(j => j.Kind == WallJoins.JoinKind.Tee).ToList();
        Assert.Equal(2, tees.Count);
        var partition = g.Names.IndexOf("the wall at (4000, 2000)");
        Assert.True(partition >= 0, string.Join(", ", g.Names));
        Assert.All(tees, t => Assert.Equal(partition, t.Stem));
        // The north wall's neighbours: both corners and the partition that ends on it.
        var north = g.Names.IndexOf("the north wall");
        Assert.Equal(3, g.Neighbours(north).Count);
        // The partition's own neighbours: none. It slides along the walls it ends on.
        Assert.Empty(g.Neighbours(partition));
    }

    [Fact]
    public void FourButtingRecords_AreOneCluster_ReadAsTheGarage()
    {
        var records = FourRects();
        var clusters = WallJoins.Clusters(records, Tol);
        Assert.Single(clusters);
        Assert.Equal(new[] { 0, 1, 2, 3 }, clusters[0]);

        var g = WallJoins.Build(records, clusters[0], Tol);
        Assert.NotNull(g);
        Assert.Equal(2, g.Shape.Count);
        Assert.Equal(4, g.Runs.Count);
        Assert.Equal(4, g.Joins.Count(j => j.Kind == WallJoins.JoinKind.Corner));
        Assert.Contains("the north wall", g.Names);
    }

    [Fact]
    public void FreeStandingRecord_IsItsOwnCluster_WithNoJoins()
    {
        var records = new List<List<List<Pt>>> { Garage(), new() { Rect(10000, 0, 14000, 200) } };
        var clusters = WallJoins.Clusters(records, Tol);
        Assert.Equal(2, clusters.Count);
        var g = WallJoins.Build(records, WallJoins.ClusterOf(records, 1, Tol), Tol);
        Assert.Single(g.Runs);
        Assert.Empty(g.Joins);
        // A cluster of one is the record's own rings.
        Assert.Same(records[1], g.Shape);
    }

    [Fact]
    public void SixtyDegreeCorner_IsOneCorner()
    {
        var dir = new Pt(Math.Cos(Math.PI * 2 / 3), Math.Sin(Math.PI * 2 / 3));
        var l = Mitre(new() { new(0, 0), new(4000, 0), new(4000 + 3000 * dir.X, 3000 * dir.Y) }, 200);
        var g = Graph(new() { new() { l } });
        Assert.Equal(2, g.Runs.Count);
        var join = Assert.Single(g.Joins);
        Assert.Equal(WallJoins.JoinKind.Corner, join.Kind);
        Assert.Equal(4000, join.At.X, 3);
        Assert.Equal(0, join.At.Y, 3);
        Assert.All(g.Runs, r => Assert.Equal(200, r.Thickness, 3));
    }

    [Fact]
    public void FourButtingRecords_MoveNorth500_TheNorthRecordMoves_EastAndWestGrow()
    {
        var records = FourRects();
        var g = WallJoins.Build(records, WallJoins.ClusterOf(records, 0, Tol), Tol);
        Assert.True(WallEdit.TryPickSide(g.Shape, "north", Tol, out var run, out var why), why);
        Assert.True(WallJoins.TryMove(records, g, run, 500, Tol, out var moved, out why), why);

        Assert.Equal(new[] { 0, 2, 3 }, moved.Records.Keys.OrderBy(k => k));
        Assert.Equal(Box(0, 4300, 8000, 4500), Sorted(moved.Records[0][0]));
        Assert.Equal(Box(0, 200, 200, 4300), Sorted(moved.Records[2][0]));
        Assert.Equal(Box(7800, 200, 8000, 4300), Sorted(moved.Records[3][0]));
        // The room stays one room, 500 mm deeper.
        Assert.Equal(2, moved.Shape.Count);
        Assert.Equal(Box(200, 200, 7800, 4300), Sorted(moved.Shape[1]));
        Assert.Equal(new[] { "the east wall +500 [3]", "the west wall +500 [2]" }, Lines(moved));
    }

    [Fact]
    public void Tee_MovingTheNorthWall_StretchesThePartition()
    {
        var records = new List<List<List<Pt>>> { TwoRooms() };
        var g = Graph(records);
        Assert.True(WallEdit.TryPickSide(g.Shape, "north", Tol, out var run, out _));
        Assert.True(WallJoins.TryMove(records, g, run, 500, Tol, out var moved, out var why), why);
        Assert.Equal(new[] { "the east wall +500 [0]", "the wall at (4000, 2000) +500 [0]", "the west wall +500 [0]" }, Lines(moved));
        Assert.Equal(3, moved.Shape.Count);
    }

    [Fact]
    public void SixtyDegreeCorner_TheNeighbourSlidesAlongItsLine_AndKeepsItsThickness()
    {
        var dir = new Pt(Math.Cos(Math.PI * 2 / 3), Math.Sin(Math.PI * 2 / 3));
        var l = Mitre(new() { new(0, 0), new(4000, 0), new(4000 + 3000 * dir.X, 3000 * dir.Y) }, 200);
        var records = new List<List<List<Pt>>> { new() { l } };
        var g = Graph(records);
        Assert.True(WallEdit.TryPick(g.Shape, new Pt(2000, 0), Tol, out var run, out var why), why);
        Assert.Equal("east–west", WallEdit.Runs(run));
        Assert.True(WallJoins.TryMove(records, g, run, -300, Tol, out var moved, out why), why);

        var after = WallJoins.Runs(moved.Shape, Tol);
        Assert.Equal(2, after.Count);
        Assert.All(after, r => Assert.Equal(200, r.Thickness, 3));
        var leaning = after.Single(r => Math.Abs(r.Dir.Y) > 0.5);
        Assert.Equal(Math.Abs(dir.X), Math.Abs(leaning.Dir.X), 6);
        // 300 mm across the run is 300 / sin 60° along the leaning wall.
        var followed = Assert.Single(moved.Followed);
        Assert.Equal(Math.Round(300 / Math.Sin(Math.PI / 3)), followed.ChangeMm);
    }

    [Fact]
    public void FourButtingRecords_ClosingTheRoom_IsRefused_AndNothingMoves()
    {
        var records = FourRects();
        var g = WallJoins.Build(records, WallJoins.ClusterOf(records, 0, Tol), Tol);
        Assert.True(WallEdit.TryPickSide(g.Shape, "north", Tol, out var run, out _));
        Assert.False(WallJoins.TryMove(records, g, run, -3700, Tol, out var moved, out var why));
        Assert.Null(moved);
        Assert.Equal("Not moved: 3700 mm would close the room or wall beyond it, 3600 mm deep.", why);
        Assert.Equal(Box(0, 3800, 8000, 4000), Sorted(records[0][0]));
    }

    [Fact]
    public void ARecordTheRunLeavesBehind_IsRefused()
    {
        // A stray block inside the north wall is its own record. It touches no
        // face line, so it would stay behind when the wall moves.
        var records = new List<List<List<Pt>>> { new() { Rect(0, 3800, 8000, 4000) }, new() { Rect(3000, 3850, 3100, 3950) } };
        var g = WallJoins.Build(records, WallJoins.ClusterOf(records, 0, Tol), Tol);
        Assert.Equal(2, g.Records.Count);
        Assert.True(WallEdit.TryPickSide(g.Shape, "north", Tol, out var run, out _));
        Assert.False(WallJoins.TryMove(records, g, run, 500, Tol, out var moved, out var why));
        Assert.Null(moved);
        Assert.Equal("Not moved: the walls joined to it would not follow cleanly.", why);
    }

    static string[] Lines(WallJoins.Moved moved) => moved.Followed
        .Select(f => f.Wall + " " + (f.ChangeMm > 0 ? "+" : "") + f.ChangeMm + " [" + string.Join(",", f.Records) + "]")
        .OrderBy(x => x).ToArray();

    static (double, double, double, double) Sorted(List<Pt> ring) =>
        (ring.Min(p => p.X), ring.Min(p => p.Y), ring.Max(p => p.X), ring.Max(p => p.Y));

    static (double, double, double, double) Box(double x0, double y0, double x1, double y1) => (x0, y0, x1, y1);

    static WallJoins.Graph Graph(List<List<List<Pt>>> records) =>
        WallJoins.Build(records, WallJoins.ClusterOf(records, 0, Tol), Tol);

    internal static List<List<Pt>> Garage() => new() { Rect(0, 0, 8000, 4000), Rect(200, 200, 7800, 3800) };

    internal static List<List<Pt>> TwoRooms() => new()
    {
        Rect(0, 0, 8000, 4000),
        Rect(200, 200, 3900, 3800),
        Rect(4100, 200, 7800, 3800)
    };

    /// <summary>The garage ring drawn as four butting 200 mm rectangles, one record each: north, south, west, east.</summary>
    internal static List<List<List<Pt>>> FourRects() => new()
    {
        new() { Rect(0, 3800, 8000, 4000) },
        new() { Rect(0, 0, 8000, 200) },
        new() { Rect(0, 200, 200, 3800) },
        new() { Rect(7800, 200, 8000, 3800) }
    };

    internal static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    /// <summary>A wall of the thickness along an open centreline, square at its two ends and mitred at each bend.</summary>
    internal static List<Pt> Mitre(List<Pt> line, double thickness)
    {
        var h = thickness / 2.0;
        var left = new List<Pt>();
        var right = new List<Pt>();
        for (var i = 0; i < line.Count; i++)
        {
            if (i == 0 || i == line.Count - 1)
            {
                var a = line[i == 0 ? 0 : i - 1];
                var b = line[i == 0 ? 1 : i];
                var n = Normal(a, b);
                left.Add(new Pt(line[i].X + n.X * h, line[i].Y + n.Y * h));
                right.Add(new Pt(line[i].X - n.X * h, line[i].Y - n.Y * h));
                continue;
            }
            left.Add(Meet(line[i - 1], line[i], line[i + 1], h));
            right.Add(Meet(line[i - 1], line[i], line[i + 1], -h));
        }
        right.Reverse();
        return right.Concat(left).ToList();
    }

    static Pt Normal(Pt a, Pt b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        return new Pt(-dy / len, dx / len);
    }

    /// <summary>Where the two segments' lines, each offset by h to the left, meet.</summary>
    static Pt Meet(Pt a, Pt b, Pt c, double h)
    {
        var n1 = Normal(a, b);
        var n2 = Normal(b, c);
        var p = new Pt(a.X + n1.X * h, a.Y + n1.Y * h);
        var q = new Pt(b.X + n2.X * h, b.Y + n2.Y * h);
        var d1 = new Pt(b.X - a.X, b.Y - a.Y);
        var d2 = new Pt(c.X - b.X, c.Y - b.Y);
        var t = ((q.X - p.X) * d2.Y - (q.Y - p.Y) * d2.X) / (d1.X * d2.Y - d1.Y * d2.X);
        return new Pt(p.X + d1.X * t, p.Y + d1.Y * t);
    }
}
