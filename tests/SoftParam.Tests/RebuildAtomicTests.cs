using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Small band: outer 8000×6000, two rooms, a 200 mm interior wall.
/// Door sits on the room edge. Two windows sit on the outer path.
/// A forced miss must leave the post-bake wall unchanged.
/// </summary>
public class RebuildAtomicTests
{
    [Fact]
    public void RoomEdgeDoor_IgnoresShuffledOffset_AndFailureLeavesTheWall()
    {
        var segs = SmallPlan();

        Assert.True(SoftParamPlan.TryPlace(segs, 3950, 3000, 2000, out var door, out var doorT, out var doorDist));
        Assert.False(segs[door].Outer);
        Assert.InRange(doorDist, 40, 60);
        Assert.InRange(doorT, 0.4, 0.6);

        Assert.True(SoftParamPlan.TryPlace(segs, 2000, 40, 2000, out var south, out _, out var southDist));
        Assert.True(segs[south].Outer);
        Assert.InRange(southDist, 30, 50);

        Assert.True(SoftParamPlan.TryPlace(segs, 7960, 3000, 2000, out var east, out _, out var eastDist));
        Assert.True(segs[east].Outer);
        Assert.InRange(eastDist, 30, 50);

        var offset = SoftParamPlan.OffsetOf(segs, door, doorT);
        var flipped = new List<SoftParamPlan.Seg>(segs);
        flipped.Reverse();
        Assert.True(SoftParamPlan.TryPlaceByOffset(flipped, offset, out var byOffset, out _));
        Assert.False(SoftParamPlan.SameEdge(segs[door], flipped[byOffset], 1e-6));

        Assert.True(SoftParamPlan.TryPlace(flipped, 3950, 3000, 2000, out var byWorld, out _, out var worldDist));
        Assert.True(SoftParamPlan.SameEdge(segs[door], flipped[byWorld], 1e-6));
        Assert.InRange(worldDist, 40, 60);

        const string baked = "post-bake";
        var doc = baked;
        var names = new[] { "door", "window-s", "window-e" };
        var failedOk = SoftParamPlan.ApplyAtomic(
            baked,
            names,
            (wall, name) => name == "window-e"
                ? new SoftParamPlan.CutStep<string>(false, wall + "+window-e")
                : new SoftParamPlan.CutStep<string>(true, wall + "+" + name),
            out var failedResult,
            out var failedName);
        if (failedOk) doc = failedResult;

        Assert.False(failedOk);
        Assert.Equal("window-e", failedName);
        Assert.Equal(baked, failedResult);
        Assert.Equal(baked, doc);

        var committed = SoftParamPlan.ApplyAtomic(
            baked,
            names,
            (wall, name) => new SoftParamPlan.CutStep<string>(true, wall + "+" + name),
            out var cutWall,
            out _);
        if (committed) doc = cutWall;

        Assert.True(committed);
        Assert.Equal("post-bake+door+window-s+window-e", doc);
    }

    [Theory]
    [InlineData(1, 1000, 800, false, true)]
    [InlineData(2, 0, 0, true, true)]
    [InlineData(1, 1000, 1000, true, true)]
    [InlineData(1, 1000, 1000, false, false)]
    [InlineData(0, 1000, 0, true, false)]
    [InlineData(1, 500, 900, false, false)]
    [InlineData(1, 1000, 1200, true, false)]
    public void AcceptCut_UsesPiecesAndVolumeOrIntersection(
        int pieces,
        double before,
        double after,
        bool intersects,
        bool expect)
    {
        Assert.Equal(expect, SoftParamPlan.AcceptCut(pieces, before, after, intersects));
    }

    // Outer rectangle 0,0–8000,6000. Rooms split by a wall from x=3900 to x=4100.
    private static List<SoftParamPlan.Seg> SmallPlan()
    {
        var segs = new List<SoftParamPlan.Seg>();
        void Edge(double x0, double y0, double x1, double y1, bool outer)
        {
            segs.Add(new SoftParamPlan.Seg(x0, y0, x1, y1, outer));
        }

        Edge(0, 0, 8000, 0, true);
        Edge(8000, 0, 8000, 6000, true);
        Edge(8000, 6000, 0, 6000, true);
        Edge(0, 6000, 0, 0, true);

        Edge(200, 200, 3900, 200, false);
        Edge(3900, 200, 3900, 5800, false);
        Edge(3900, 5800, 200, 5800, false);
        Edge(200, 5800, 200, 200, false);

        Edge(4100, 200, 7800, 200, false);
        Edge(7800, 200, 7800, 5800, false);
        Edge(7800, 5800, 4100, 5800, false);
        Edge(4100, 5800, 4100, 200, false);
        return segs;
    }
}
