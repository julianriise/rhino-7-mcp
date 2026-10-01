using System.Collections.Generic;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// The floor and flat roof that belong to a wall, and the sentence that says so.
/// </summary>
public class WallFollowPlanTests
{
    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };

    [Fact]
    public void Sentence_NamesWhatFollowed_AndAStaleDaylightMap()
    {
        Assert.Equal("Floor, roof and 2 rooms updated.", WallFollowPlan.Sentence(true, true, 2, false));
        Assert.Equal(
            "Floor, roof and 2 rooms updated. Daylight is out of date, run it again.",
            WallFollowPlan.Sentence(true, true, 2, true));
        Assert.Equal("1 room updated.", WallFollowPlan.Sentence(false, false, 1, false));
        Assert.Equal("Roof and 2 rooms updated.", WallFollowPlan.Sentence(false, true, 2, false));
        Assert.Equal("Floor and roof updated.", WallFollowPlan.Sentence(true, true, 0, false));
        Assert.Equal("Floor, roof and rooms are unchanged.", WallFollowPlan.Sentence(false, false, 0, false));
        Assert.Equal(
            "Floor, roof and rooms are unchanged. Daylight is out of date, run it again.",
            WallFollowPlan.Sentence(false, false, 0, true));
    }

    [Fact]
    public void Outline_MatchesTheWall_ARoofSitsItsOverhangOutside_AFreeWallDoesNot()
    {
        var wall = Rect(0, 0, 8000, 4000);
        var same = Rect(0, 0, 8000, 4000);
        var roof = Rect(-500, -500, 8500, 4500);
        var free = Rect(2000, 6000, 6000, 6400);
        Assert.True(WallFollowPlan.SameOutline(wall, same, 5));
        Assert.False(WallFollowPlan.SameOutline(wall, free, 5));
        Assert.False(WallFollowPlan.SameOutline(free, wall, 5));
        Assert.True(WallFollowPlan.Covers(wall, roof, 500, 5));
        Assert.False(WallFollowPlan.Covers(wall, same, 500, 5));
        Assert.False(WallFollowPlan.Covers(free, roof, 500, 5));
        Assert.True(WallFollowPlan.Covers(wall, same, 0, 5));
    }
}
