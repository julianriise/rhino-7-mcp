using System;
using System.Collections.Generic;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// Merge walls: walls end to end on one line merge into one record; walls
/// that go into each other are cut apart at the join, or merged when that
/// does not stand. What the walls cover stays the same.
/// </summary>
public class WallMergeTests
{
    const double Tol = 1.0;

    static List<List<Pt>> R(double x0, double y0, double x1, double y1) => new() { WallJoinsTests.Rect(x0, y0, x1, y1) };

    static double Area(List<List<Pt>> rings) =>
        Math.Abs(RoomDetect.Area(rings[0])) - rings.Skip(1).Sum(r => Math.Abs(RoomDetect.Area(r)));

    static (double, double, double, double) Box(List<List<Pt>> rings) =>
        (rings[0].Min(p => p.X), rings[0].Min(p => p.Y), rings[0].Max(p => p.X), rings[0].Max(p => p.Y));

    static List<List<List<Pt>>> After(List<List<List<Pt>>> records, WallMerge.Result result)
    {
        var after = new List<List<List<Pt>>>();
        for (var i = 0; i < records.Count; i++)
        {
            if (result.Into.ContainsKey(i)) continue;
            after.Add(result.Records.TryGetValue(i, out var rings) ? rings : records[i]);
        }
        return after;
    }

    [Fact]
    public void EndToEnd_OnOneLine_MergeIntoOneRecord()
    {
        var records = new List<List<List<Pt>>> { R(0, 0, 3000, 200), R(3000, 0, 6000, 200) };
        var result = WallMerge.Plan(records, new[] { 1 }, Tol);
        Assert.Single(result.Into);
        var after = After(records, result);
        Assert.Single(after);
        Assert.Equal((0.0, 0.0, 6000.0, 200.0), Box(after[0]));
        Assert.Equal(4, after[0][0].Count);
        Assert.Single(WallJoins.Runs(after[0], Tol));
    }

    [Fact]
    public void ThreeOnOneLine_MergeIntoOne()
    {
        var records = new List<List<List<Pt>>> { R(0, 0, 2000, 200), R(2000, 0, 4000, 200), R(4000, 0, 5000, 200) };
        var result = WallMerge.Plan(records, new[] { 1 }, Tol);
        Assert.Equal(2, result.Into.Count);
        var keeper = Assert.Single(result.Into.Values.Distinct());
        Assert.Equal((0.0, 0.0, 5000.0, 200.0), Box(result.Records[keeper]));
    }

    [Fact]
    public void Overlapping_OnOneLine_MergeIntoOne()
    {
        var records = new List<List<List<Pt>>> { R(0, 0, 3000, 200), R(2500, 0, 6000, 200) };
        var result = WallMerge.Plan(records, new[] { 0 }, Tol);
        var after = After(records, result);
        Assert.Single(after);
        Assert.Equal((0.0, 0.0, 6000.0, 200.0), Box(after[0]));
    }

    [Fact]
    public void Corner_DifferentThickness_OrOtherKind_StayApart()
    {
        // A clean corner is two walls already.
        var corner = new List<List<List<Pt>>> { R(0, 0, 4000, 200), R(3800, 200, 4000, 3000) };
        Assert.False(WallMerge.Plan(corner, new[] { 0, 1 }, Tol).Any);
        // On one line but not the same thickness.
        var stepped = new List<List<List<Pt>>> { R(0, 0, 3000, 200), R(3000, 0, 6000, 300) };
        Assert.False(WallMerge.Plan(stepped, new[] { 1 }, Tol).Any);
        // On one line, the same thickness, but another height.
        var lines = new List<List<List<Pt>>> { R(0, 0, 3000, 200), R(3000, 0, 6000, 200) };
        Assert.False(WallMerge.Plan(lines, new[] { 1 }, Tol, new[] { "0|2700", "0|1200" }).Any);
        // Apart.
        var apart = new List<List<List<Pt>>> { R(0, 0, 3000, 200), R(3100, 0, 6000, 200) };
        Assert.False(WallMerge.Plan(apart, new[] { 1 }, Tol).Any);
    }

    [Fact]
    public void DrawWall_MitredCorner_StaysTwoWalls()
    {
        var records = new List<List<List<Pt>>>
        {
            new() { WallDraw.SegmentRing(null, new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 3000), 200) },
            new() { WallDraw.SegmentRing(new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 3000), null, 200) }
        };
        Assert.False(WallMerge.Plan(records, new[] { 1 }, Tol).Any);
    }

    [Fact]
    public void DrawWall_StraightOn_MergesIntoOne()
    {
        // A second click on the same line: two square-ended segments end to end.
        var records = new List<List<List<Pt>>>
        {
            new() { WallDraw.SegmentRing(null, new Pt(0, 0), new Pt(3000, 0), new Pt(5000, 0), 200) },
            new() { WallDraw.SegmentRing(new Pt(0, 0), new Pt(3000, 0), new Pt(5000, 0), null, 200) }
        };
        var after = After(records, WallMerge.Plan(records, new[] { 1 }, Tol));
        Assert.Single(after);
        Assert.Equal((0.0, -100.0, 5000.0, 100.0), Box(after[0]));
    }

    [Fact]
    public void StemIntoABar_IsCutAtTheBarsFace()
    {
        // The stem runs 150 mm into the bar.
        var records = new List<List<List<Pt>>> { R(0, 0, 4000, 200), R(1900, 50, 2100, 2000) };
        var before = Area(WallMerge_Union(records));
        var result = WallMerge.Plan(records, new[] { 1 }, Tol);
        Assert.Equal(1, result.Simplified);
        Assert.Empty(result.Into);
        var after = After(records, result);
        Assert.Equal(2, after.Count);
        Assert.Equal((0.0, 0.0, 4000.0, 200.0), Box(after[0]));
        Assert.Equal((1900.0, 200.0, 2100.0, 2000.0), Box(after[1]));
        Assert.Equal(before, Area(after[0]) + Area(after[1]), 0);
    }

    [Fact]
    public void Overlapping_Corner_IsCutClean()
    {
        // Both walls run to the outside of the corner, so they share its square.
        var records = new List<List<List<Pt>>> { R(0, 0, 4000, 200), R(3800, 0, 4000, 3000) };
        var result = WallMerge.Plan(records, new[] { 1 }, Tol);
        Assert.Equal(1, result.Simplified);
        var after = After(records, result);
        Assert.Equal(2, after.Count);
        var total = Area(after[0]) + Area(after[1]);
        Assert.Equal(4000 * 200 + 2800 * 200, total, 0);
    }

    [Fact]
    public void Crossing_Walls_EndUpWithoutOverlap_AndCoverTheSame()
    {
        var records = new List<List<List<Pt>>> { R(0, 900, 4000, 1100), R(1900, 0, 2100, 2000) };
        var before = Area(WallMerge_Union(records));
        var result = WallMerge.Plan(records, new[] { 1 }, Tol);
        Assert.True(result.Any);
        var after = After(records, result);
        Assert.Equal(before, after.Sum(Area), 0);
    }

    static List<List<Pt>> WallMerge_Union(List<List<List<Pt>>> records) =>
        WallEdit.Group(RoomDetect.Union(records, Tol))[0];
}
