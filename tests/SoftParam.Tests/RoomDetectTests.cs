using System.Collections.Generic;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// F2.5: rooms are the closed regions between wall footprints. A door closes
/// a gap between wall ends, a space divider splits an open plan, drawn
/// outlines win, and an undoored gap leaves the region open with a reason.
/// </summary>
public class RoomDetectTests
{
    const double M2 = 1000000.0;

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };

    static List<List<Pt>> Wall(params List<Pt>[] rings) => rings.ToList();

    /// <summary>One wall band: 4200 x 4200 outside, 200 thick, a 3800 x 3800 room.</summary>
    static RoomDetect.Scene OneRoom()
    {
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(Wall(Rect(0, 0, 4200, 4200), Rect(200, 200, 4000, 4000)));
        return scene;
    }

    /// <summary>
    /// Separate wall rectangles, as a model with drawn door gaps has them:
    /// 8200 x 4200 outside, and a middle wall with a 900 gap at y 1500..2400.
    /// </summary>
    static RoomDetect.Scene TwoRoomsWithGap()
    {
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(Wall(Rect(0, 0, 8200, 200)));
        scene.Walls.Add(Wall(Rect(0, 4000, 8200, 4200)));
        scene.Walls.Add(Wall(Rect(0, 0, 200, 4200)));
        scene.Walls.Add(Wall(Rect(8000, 0, 8200, 4200)));
        scene.Walls.Add(Wall(Rect(4000, 200, 4200, 1500)));
        scene.Walls.Add(Wall(Rect(4000, 2400, 4200, 4000)));
        return scene;
    }

    /// <summary>One room whose bottom wall has a 900 gap at x 1500..2400 to the outside.</summary>
    static RoomDetect.Scene RoomWithOuterGap()
    {
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(Wall(Rect(0, 0, 1500, 200)));
        scene.Walls.Add(Wall(Rect(2400, 0, 4200, 200)));
        scene.Walls.Add(Wall(Rect(0, 0, 200, 4200)));
        scene.Walls.Add(Wall(Rect(4000, 0, 4200, 4200)));
        scene.Walls.Add(Wall(Rect(0, 4000, 4200, 4200)));
        return scene;
    }

    [Fact]
    public void OneRoom_IsTheHoleInsideTheWallBand()
    {
        var found = RoomDetect.Detect(OneRoom());
        var room = Assert.Single(found.Rooms);
        Assert.Equal(3.8 * 3.8, room.Area / M2, 3);
        Assert.Equal(4, room.Ring.Count);
        Assert.Empty(found.Open);
        Assert.True(RoomDetect.Area(room.Ring) > 0);
    }

    [Fact]
    public void TwoRoomsAndADoor_TheDoorClosesTheGap()
    {
        var scene = TwoRoomsWithGap();
        scene.Doors.Add(new RoomDetect.Box(4000, 1500, 4200, 2400));
        var found = RoomDetect.Detect(scene);
        Assert.Equal(2, found.Rooms.Count);
        Assert.All(found.Rooms, room => Assert.Equal(3.8 * 3.8, room.Area / M2, 3));
        Assert.Empty(found.Open);
        Assert.Equal(1, found.Slivers);
    }

    [Fact]
    public void TwoRoomsNoDoor_BothStayOpenWithTheGap()
    {
        var found = RoomDetect.Detect(TwoRoomsWithGap());
        Assert.Empty(found.Rooms);
        Assert.Equal(2, found.Open.Count);
        Assert.All(found.Open, open => Assert.Equal("gap 0.9 m without a door", open.Reason));
    }

    [Fact]
    public void OpenPlanAndDivider_SplitsIntoTwoRooms()
    {
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(Wall(Rect(0, 0, 8200, 4200), Rect(200, 200, 8000, 4000)));
        // Stops 50 short of both wall faces: the reach closes it.
        scene.Dividers.Add(new List<Pt> { new Pt(4100, 250), new Pt(4100, 3950) });
        var found = RoomDetect.Detect(scene);
        Assert.Equal(2, found.Rooms.Count);
        Assert.All(found.Rooms, room => Assert.Equal(3.9 * 3.8, room.Area / M2, 3));
        Assert.Empty(found.Open);
    }

    [Fact]
    public void DividerShortOfTheWalls_LeavesOneRoomAndSaysWhy()
    {
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(Wall(Rect(0, 0, 8200, 4200), Rect(200, 200, 8000, 4000)));
        scene.Dividers.Add(new List<Pt> { new Pt(4100, 1000), new Pt(4100, 3000) });
        var found = RoomDetect.Detect(scene);
        var room = Assert.Single(found.Rooms);
        Assert.Equal(7.8 * 3.8, room.Area / M2, 3);
        Assert.Equal("space divider does not meet a wall", Assert.Single(found.Open).Reason);
    }

    [Fact]
    public void UnclosedGap_NoRoomAndOneOpenRegion()
    {
        var found = RoomDetect.Detect(RoomWithOuterGap());
        Assert.Empty(found.Rooms);
        var open = Assert.Single(found.Open);
        Assert.Equal("gap 0.9 m without a door", open.Reason);
        Assert.InRange(open.At.X, 200, 4000);
        Assert.InRange(open.At.Y, 200, 4000);
    }

    [Fact]
    public void UnclosedGapWithADoor_IsARoom()
    {
        var scene = RoomWithOuterGap();
        scene.Doors.Add(new RoomDetect.Box(1500, 50, 2400, 150));
        var found = RoomDetect.Detect(scene);
        Assert.Equal(3.8 * 3.8, Assert.Single(found.Rooms).Area / M2, 3);
        Assert.Empty(found.Open);
    }

    [Fact]
    public void DrawnOutline_WinsAndDetectionFillsTheRest()
    {
        var scene = TwoRoomsWithGap();
        scene.Doors.Add(new RoomDetect.Box(4000, 1500, 4200, 2400));
        scene.Keep.Add(Rect(300, 300, 3900, 3900));
        var found = RoomDetect.Detect(scene);
        Assert.Equal(1, found.Kept);
        var room = Assert.Single(found.Rooms);
        Assert.True(room.Inside.X > 4200);
    }

    [Fact]
    public void RoomUnderOneSquareMetre_IsASliver()
    {
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(Wall(Rect(0, 0, 1200, 1200), Rect(200, 200, 1000, 1000)));
        var found = RoomDetect.Detect(scene);
        Assert.Empty(found.Rooms);
        Assert.Equal(1, found.Slivers);
    }

    [Fact]
    public void RerunKeepsIds_NewRoomsTakeTheNextNumber()
    {
        var scene = TwoRoomsWithGap();
        scene.Doors.Add(new RoomDetect.Box(4000, 1500, 4200, 2400));
        var first = RoomDetect.Detect(scene);
        var none = new List<KeyValuePair<string, List<Pt>>>();
        var ids = RoomDetect.Match(first.Rooms, none, "rd-");
        Assert.Equal(new[] { "rd-01", "rd-02" }, ids.OrderBy(id => id).ToArray());

        // The earlier outlines come back in a different order; each room keeps its id.
        var earlier = new List<KeyValuePair<string, List<Pt>>>
        {
            new KeyValuePair<string, List<Pt>>(ids[1], first.Rooms[1].Ring),
            new KeyValuePair<string, List<Pt>>(ids[0], first.Rooms[0].Ring)
        };
        var again = RoomDetect.Match(RoomDetect.Detect(scene).Rooms, earlier, "rd-");
        Assert.Equal(ids, again);

        // Only the right room was detected before, as rd-07: the left one is new.
        var right = first.Rooms.First(room => room.Inside.X > 4200);
        var left = first.Rooms.First(room => room.Inside.X < 4000);
        var seven = new List<KeyValuePair<string, List<Pt>>>
        {
            new KeyValuePair<string, List<Pt>>("rd-07", right.Ring)
        };
        var next = RoomDetect.Match(new[] { left, right }, seven, "rd-");
        Assert.Equal(new[] { "rd-08", "rd-07" }, next);
    }
}
