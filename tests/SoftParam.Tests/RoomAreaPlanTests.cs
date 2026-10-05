using System.Collections.Generic;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// The area tool's outline from clicked corners, and a drawn room a new wall
/// cuts in two. Plain points, no document.
/// </summary>
public class RoomAreaPlanTests
{
    static List<Pt> Pts(params double[] xy)
    {
        var list = new List<Pt>();
        for (var i = 0; i + 1 < xy.Length; i += 2) list.Add(new Pt(xy[i], xy[i + 1]));
        return list;
    }

    [Fact]
    public void ClickedCorners_CloseCounterclockwise_CountingRepeatsOnce()
    {
        // Clockwise, the first corner clicked again to close, one corner clicked twice, one on a straight edge.
        var clicks = Pts(0, 0, 0, 3000, 0, 3000, 4000, 3000, 4000, 1500, 4000, 0, 0, 0);
        Assert.True(RoomAreaPlan.TryRing(clicks, 1.0, out var ring, out var why));
        Assert.Null(why);
        Assert.Equal(4, ring.Count);
        Assert.Equal(12000000.0, RoomDetect.Area(ring), 3);
    }

    [Fact]
    public void TooFew_TooSmall_AndCrossing_AreRefused()
    {
        Assert.False(RoomAreaPlan.TryRing(Pts(0, 0, 1000, 0), 1.0, out _, out var why));
        Assert.Equal(RoomAreaPlan.TooFew, why);
        Assert.False(RoomAreaPlan.TryRing(Pts(0, 0, 500, 0, 500, 500, 0, 500), 1.0, out _, out why));
        Assert.Equal(RoomAreaPlan.TooSmall, why);
        Assert.False(RoomAreaPlan.TryRing(Pts(0, 0, 4000, 3000, 4000, 0, 0, 3000), 1.0, out _, out why));
        Assert.Equal(RoomAreaPlan.Crossing, why);
    }

    [Fact]
    public void AWallAcrossADrawnRoom_SplitsItInTwo()
    {
        var room = Pts(0, 0, 4000, 0, 4000, 3000, 0, 3000);
        var pieces = RoomAreaPlan.Split(room, new Pt(2500, 0), new Pt(2500, 3000), 100, 1.0);
        Assert.NotNull(pieces);
        Assert.Equal(2, pieces!.Count);
        Assert.Equal(2450.0 * 3000, RoomDetect.Area(pieces[0]), 0);
        Assert.Equal(1450.0 * 3000, RoomDetect.Area(pieces[1]), 0);

        // The wall stops on wall faces 50 mm outside the drawn outline: it still cuts the room through.
        Assert.Equal(2, RoomAreaPlan.Split(room, new Pt(2000, 50), new Pt(2000, 2950), 100, 1.0)!.Count);
    }

    [Fact]
    public void AStubWall_LeavesTheRoomWhole()
    {
        var room = Pts(0, 0, 4000, 0, 4000, 3000, 0, 3000);
        Assert.Null(RoomAreaPlan.Split(room, new Pt(2000, 0), new Pt(2000, 1500), 100, 1.0));
        // A wall outside the room does not touch it.
        Assert.Null(RoomAreaPlan.Split(room, new Pt(6000, 0), new Pt(6000, 3000), 100, 1.0));
        Assert.Equal(" Split 1 room in two.", RoomAreaPlan.SplitClause(1));
        Assert.Equal("", RoomAreaPlan.SplitClause(0));
    }
}
