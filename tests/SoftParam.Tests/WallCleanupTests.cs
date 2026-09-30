using System;
using System.Collections.Generic;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// F7.2 wall cleanup: the import's wall rectangles are merged into closed
/// outlines, one per connected run of walls, before anything is drawn. No two
/// outlines overlap, each is a valid closed loop with no sliver or spike, the
/// openings still sit across their wall, and a wall drawn afterwards merges
/// with the outlines at the bake.
/// </summary>
public class WallCleanupTests
{
    const double M2 = 1000000.0;

    static PlanImport.Wall W(double ax, double ay, double bx, double by, double thickness = 200) =>
        new PlanImport.Wall { A = new Pt(ax, ay), B = new Pt(bx, by), Thickness = thickness };

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };

    static PlanImport.Result Clean(PlanImport.Wall[] walls, params (string kind, double ax, double ay, double bx, double by)[] openings)
    {
        var plan = new PlanImport.Plan();
        plan.Walls.AddRange(walls);
        foreach (var o in openings)
        {
            plan.Openings.Add(new PlanImport.Opening
            {
                Kind = o.kind,
                A = new Pt(o.ax, o.ay),
                B = new Pt(o.bx, o.by),
                Width = Math.Sqrt((o.bx - o.ax) * (o.bx - o.ax) + (o.by - o.ay) * (o.by - o.ay))
            });
        }
        return PlanImport.Clean(plan);
    }

    static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static double Turn(Pt a, Pt b, Pt c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    /// <summary>The two segments share a point: they cross, touch, or lie on each other.</summary>
    static bool Meet(Pt a, Pt b, Pt c, Pt d)
    {
        const double eps = 1e-6;
        double d1 = Turn(c, d, a), d2 = Turn(c, d, b), d3 = Turn(a, b, c), d4 = Turn(a, b, d);
        if (((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps)))
            return true;
        return RoomDetect.Clearance(new[] { a, b }, c) <= eps || RoomDetect.Clearance(new[] { a, b }, d) <= eps
            || RoomDetect.Clearance(new[] { c, d }, a) <= eps || RoomDetect.Clearance(new[] { c, d }, b) <= eps;
    }

    static bool InWall(PlanImport.Outline outline, Pt p) =>
        RoomDetect.Contains(outline.Outer, p) && !outline.Holes.Exists(hole => RoomDetect.Contains(hole, p));

    /// <summary>
    /// What the wall layer shows is sound: every loop closed and simple, no
    /// edge shorter than minEdge (a sliver), no corner sharper than 20
    /// degrees (a spike), and no loop meeting another, so no two outlines
    /// overlap. Returns the wall area in m2.
    /// </summary>
    internal static double AssertSound(PlanImport.Result result, double minEdge = 50.0)
    {
        Assert.Equal(0, result.Overlaps);
        var loops = new List<List<Pt>>();
        var area = 0.0;
        foreach (var outline in result.Networks)
        {
            Assert.True(RoomDetect.Area(outline.Outer) > 0, "an outer loop runs clockwise");
            Assert.All(outline.Holes, hole => Assert.True(RoomDetect.Area(hole) < 0, "a hole runs counterclockwise"));
            Assert.All(outline.Holes, hole => Assert.All(hole, p => Assert.True(RoomDetect.Contains(outline.Outer, p), "a hole lies outside its outline")));
            loops.Add(outline.Outer);
            loops.AddRange(outline.Holes);
            area += RoomDetect.Area(outline.Outer) + outline.Holes.Sum(hole => RoomDetect.Area(hole));
        }
        for (var l = 0; l < loops.Count; l++)
        {
            var loop = loops[l];
            Assert.True(loop.Count >= 3, "a loop has under three corners");
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                var c = loop[(i + 2) % loop.Count];
                Assert.True(Dist(a, b) >= minEdge, "sliver: an edge " + Dist(a, b).ToString("0.##") + " mm long at " + a.X.ToString("0") + ", " + a.Y.ToString("0"));
                var cos = ((a.X - b.X) * (c.X - b.X) + (a.Y - b.Y) * (c.Y - b.Y)) / (Dist(a, b) * Dist(b, c));
                Assert.True(cos < Math.Cos(20.0 * Math.PI / 180.0), "spike at " + b.X.ToString("0") + ", " + b.Y.ToString("0"));
                // Simple: an edge meets only its two neighbours, and only at their shared corner.
                for (var j = i + 2; j < loop.Count; j++)
                {
                    if (i == 0 && j == loop.Count - 1) continue;
                    Assert.False(Meet(a, b, loop[j], loop[(j + 1) % loop.Count]),
                        "a loop crosses itself at " + a.X.ToString("0") + ", " + a.Y.ToString("0"));
                }
                for (var m = l + 1; m < loops.Count; m++)
                    for (var j = 0; j < loops[m].Count; j++)
                        Assert.False(Meet(a, b, loops[m][j], loops[m][(j + 1) % loops[m].Count]),
                            "two loops meet at " + a.X.ToString("0") + ", " + a.Y.ToString("0"));
            }
        }
        // Loops that never meet can still lie one on the other: a corner of one inside another's wall.
        foreach (var outline in result.Networks)
            foreach (var other in result.Networks)
                Assert.True(ReferenceEquals(outline, other) || !InWall(other, outline.Outer[0]), "an outline lies inside another's wall");
        return area / M2;
    }

    /// <summary>Every opening has its wall, and its footprint goes through the merged outline from free space to free space.</summary>
    static void AssertOpeningsCut(PlanImport.Result result, int count)
    {
        Assert.Equal(count, result.Openings.Count);
        Assert.Equal(0, result.Loose);
        Assert.Equal(0, result.Uncut);
        foreach (var opening in result.Openings)
        {
            Assert.True(opening.Host >= 0);
            var outline = result.Networks[result.Walls[opening.Host].Network];
            var ring = PlanImport.Ring(opening);
            Assert.True(InWall(outline, new Pt((ring[0].X + ring[2].X) / 2, (ring[0].Y + ring[2].Y) / 2)), "an opening is not in its wall");
            Assert.All(ring, corner => Assert.False(InWall(outline, corner), "an opening stops inside its wall"));
        }
    }

    [Fact]
    public void TJunction_IsOneOutline()
    {
        var result = Clean(new[] { W(0, 0, 4000, 0), W(2000, 0, 2000, 3000) }, ("door", 2000, 1000, 2000, 1900));
        var outline = Assert.Single(result.Networks);
        Assert.Equal(8, outline.Outer.Count);
        Assert.Empty(outline.Holes);
        Assert.Equal(2, outline.Pieces);
        // The bar, and the stem from the bar's face.
        Assert.Equal((4000.0 * 200 + 2900.0 * 200) / M2, AssertSound(result), 6);
        AssertOpeningsCut(result, 1);
    }

    [Fact]
    public void LCorner_IsOneOutlineWithAFullCorner()
    {
        var result = Clean(new[] { W(0, 0, 4000, 0), W(0, 0, 0, 3000) }, ("window", 1000, 0, 2200, 0));
        var outline = Assert.Single(result.Networks);
        Assert.Equal(6, outline.Outer.Count);
        Assert.Empty(outline.Holes);
        // Each wall runs to the other's far face: the corner square is there once.
        Assert.Equal((4100.0 * 200 + 3100.0 * 200 - 200.0 * 200) / M2, AssertSound(result), 6);
        AssertOpeningsCut(result, 1);
    }

    [Fact]
    public void Crossing_IsOneOutline()
    {
        var result = Clean(new[] { W(0, 0, 4000, 0), W(2000, -2000, 2000, 2000) }, ("door", 2000, 800, 2000, 1700));
        var outline = Assert.Single(result.Networks);
        Assert.Equal(12, outline.Outer.Count);
        Assert.Empty(outline.Holes);
        Assert.Equal((2 * 4000.0 * 200 - 200.0 * 200) / M2, AssertSound(result), 6);
        AssertOpeningsCut(result, 1);
    }

    [Fact]
    public void BayAt45Degrees_IsOneOutline_TheDiagonalStaysDiagonalAndNoCornerPokesOut()
    {
        // A room with one corner cut off by a diagonal wall.
        var result = Clean(
            new[]
            {
                W(0, 0, 4000, 0), W(0, 0, 0, 4000), W(0, 4000, 3000, 4000), W(4000, 0, 4000, 3000),
                W(3000, 4000, 4000, 3000)
            },
            ("window", 1000, 0, 2200, 0), ("door", 0, 1500, 0, 2400));
        Assert.Equal(1, result.Diagonal);
        var outline = Assert.Single(result.Networks);
        // Four square corners less one, and the diagonal's two: no corner of a square wall end left over.
        Assert.Equal(5, outline.Outer.Count);
        Assert.Equal(5, Assert.Single(outline.Holes).Count);
        foreach (var loop in new[] { outline.Outer, outline.Holes[0] })
        {
            var slanted = Enumerable.Range(0, 5).Select(i => (a: loop[i], b: loop[(i + 1) % 5]))
                .Where(e => Math.Abs(e.a.X - e.b.X) > 1 && Math.Abs(e.a.Y - e.b.Y) > 1).ToList();
            var edge = Assert.Single(slanted);
            Assert.Equal(Math.Abs(edge.a.X - edge.b.X), Math.Abs(edge.a.Y - edge.b.Y), 6);
        }
        // The faces of the diagonal lie 100 mm either side of x + y = 7000.
        var cutOuter = 1200 - 100 * Math.Sqrt(2);
        var cutInner = 800 + 100 * Math.Sqrt(2);
        var wall = 4200.0 * 4200 - cutOuter * cutOuter / 2 - (3800.0 * 3800 - cutInner * cutInner / 2);
        Assert.Equal(wall / M2, AssertSound(result), 6);
        AssertOpeningsCut(result, 2);
    }

    [Fact]
    public void WallStandingFreeInARoom_IsAnOutlineOfItsOwn()
    {
        var result = Clean(
            new[]
            {
                W(0, 0, 5000, 0), W(5000, 0, 5000, 4000), W(0, 4000, 5000, 4000), W(0, 0, 0, 4000),
                W(2500, 1000, 2500, 3000)
            },
            ("door", 2500, 1500, 2500, 2400), ("window", 1000, 0, 2200, 0));
        Assert.Equal(2, result.Outlines);
        var house = Assert.Single(result.Networks, n => n.Holes.Count == 1);
        var run = Assert.Single(result.Networks, n => n.Holes.Count == 0);
        Assert.Equal(4, house.Pieces);
        Assert.Equal(4, run.Outer.Count);
        Assert.Equal(1, result.Blocks);
        Assert.Equal((5200.0 * 4200 - 4800.0 * 3800 + 2000.0 * 200) / M2, AssertSound(result), 6);
        AssertOpeningsCut(result, 2);
    }

    [Fact]
    public void NearTouchGap_IsClosed_AndTheRoomIsOneOutlineWithItsHole()
    {
        // The right wall stops 150 mm short of the top wall's face: less than the walls are thick.
        var result = Clean(
            new[] { W(0, 0, 5000, 0), W(0, 4000, 5000, 4000), W(0, 0, 0, 4000), W(5000, 0, 5000, 3750) },
            ("door", 5000, 1000, 5000, 1900));
        Assert.Equal(1, result.Closed);
        var outline = Assert.Single(result.Networks);
        Assert.Equal(4, outline.Outer.Count);
        Assert.Equal(4, Assert.Single(outline.Holes).Count);
        Assert.Equal((5200.0 * 4200 - 4800.0 * 3800) / M2, AssertSound(result), 6);
        AssertOpeningsCut(result, 1);
        Assert.Contains("Wall cleanup: 4 wall pieces merged into 1 outline, 1 gap closed, 0 overlaps left.", PlanImport.Message(result, ""));
    }

    [Fact]
    public void FacesAFewMillimetresApart_AreOneFace_ARealStepStays()
    {
        // One wall drawn in two thicknesses, as a vector PDF has it: the right faces 4 mm apart, the left 56 mm.
        var result = Clean(new[] { W(0, 0, 0, 4000, 480), W(26, 3000, 26, 7000, 420) });
        var outline = Assert.Single(result.Networks);
        Assert.Equal(6, outline.Outer.Count);
        Assert.Equal(1, outline.Outer.Count(p => Math.Abs(p.X + 184) < 1e-6 && Math.Abs(p.Y - 4000) < 1e-6));
        // The shorter face moved onto the line of the longer.
        Assert.Equal(240.0, outline.Outer.Max(p => p.X), 6);
        AssertSound(result);
    }

    /// <summary>The wall layer as the bake reads it back: the import's outlines with their holes, and what was drawn since.</summary>
    static List<List<Pt>> Bake(PlanImport.Result result, params List<Pt>[] drawn)
    {
        var outlines = result.Networks.Select(n => n.Outer).Concat(drawn).ToList();
        var holes = result.Networks.SelectMany(n => n.Holes).ToList();
        return RoomDetect.Union(RoomDetect.Regions(outlines, holes), 1.0);
    }

    static PlanImport.Wall[] Room() =>
        new[] { W(0, 0, 5000, 0), W(5000, 0, 5000, 4000), W(0, 4000, 5000, 4000), W(0, 0, 0, 4000) };

    [Fact]
    public void MergedOutline_BakesAsTheWallItIs_NotAsAFilledBlock()
    {
        var loops = Bake(Clean(Room()));
        Assert.Equal(2, loops.Count);
        Assert.Equal(new[] { -18.24, 21.84 }, loops.Select(l => Math.Round(RoomDetect.Area(l) / M2, 6)).OrderBy(a => a));
    }

    [Fact]
    public void WallDrawnAcrossARoomAfterTheImport_MergesAtTheBake()
    {
        // A partition from wall to wall, drawn as a rectangle that runs into both.
        var loops = Bake(Clean(Room()), Rect(2400, 0, 2500, 4000));
        Assert.Single(loops, l => RoomDetect.Area(l) > 0);
        Assert.Equal(new[] { -9.12, -8.74 }, loops.Where(l => RoomDetect.Area(l) < 0).Select(l => Math.Round(RoomDetect.Area(l) / M2, 6)).OrderBy(a => a));
    }

    [Fact]
    public void WallDrawnFreeInARoomAfterTheImport_StaysAWallOfItsOwn()
    {
        var loops = Bake(Clean(Room()), Rect(2400, 1000, 2500, 3000));
        Assert.Equal(new[] { -18.24, 0.2, 21.84 }, loops.Select(l => Math.Round(RoomDetect.Area(l) / M2, 6)).OrderBy(a => a));
    }

    [Fact]
    public void HoleWhoseOutlineIsGone_IsReadAsAnOutline()
    {
        var regions = RoomDetect.Regions(new List<List<Pt>>(), new List<List<Pt>> { Rect(0, 0, 1000, 1000) });
        Assert.Single(Assert.Single(regions));
    }
}
