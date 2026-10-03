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

    static readonly RoomDetect.Label[] NoLabels = new RoomDetect.Label[0];

    /// <summary>What the plan tag shows for a marker carrying this record's stamps, with no labels in reach.</summary>
    static (string Name, Pt At) Tagged(RoomDetect.Tag tag, List<Pt> outline)
    {
        Assert.True(RoomDetect.TryTag(tag.Name, RoomDetect.StampAt(outline, tag.At), tag.Area, NoLabels, outline,
            out var name, out var at, out _));
        return (name, at);
    }

    [Fact]
    public void ColumnAtTheRoomsCentre_TheTagIsWhereRoomsDetectPutIt_NotOnTheColumn()
    {
        // A 3800 x 3800 room with a free-standing 400 x 400 column at its centre.
        var scene = OneRoom();
        var column = Rect(1900, 1900, 2300, 2300);
        scene.Walls.Add(Wall(column));
        var found = RoomDetect.Detect(scene);
        var room = Assert.Single(found.Rooms);
        Assert.Equal(1, room.Holes);
        Assert.Equal(14.28 * M2, room.Area, 3);

        // The marker is the outer outline alone. Its record is the room's: net
        // area, and the inside point that keeps clear of the column.
        var tag = RoomDetect.Report(room.Ring, "room-01", 14.44 * M2, found.Rooms, new[] { "rd-01" }, new[] { "Stue" }, NoLabels);
        Assert.True(tag.Detected);
        Assert.Equal("rd-01", tag.Id);
        Assert.Equal(14.28 * M2, tag.Area, 3);
        Assert.False(RoomDetect.Contains(column, tag.At));
        var (name, at) = Tagged(tag, room.Ring);
        Assert.Equal("Stue", name);
        Assert.Equal(tag.At.X, at.X, 6);
        Assert.Equal(tag.At.Y, at.Y, 6);

        // Worked out again from the outline, as print did, the tag lands on the column.
        Assert.True(RoomDetect.TryTag(null, null, tag.Area, NoLabels, room.Ring, out _, out var again, out _));
        Assert.True(RoomDetect.Contains(column, again));
    }

    [Fact]
    public void OutlineDrawnByHand_IsReportedWithItsLabelAndPoint_AndTaggedTheSame()
    {
        var scene = TwoRoomsWithGap();
        scene.Doors.Add(new RoomDetect.Box(4000, 1500, 4200, 2400));
        var drawn = Rect(300, 300, 3900, 3900);
        scene.Keep.Add(drawn);
        var labels = new[] { Label("Bad", 250, 1000, 1000), Label("Stue", 250, 6000, 2000) };
        var found = RoomDetect.Detect(scene);
        var right = Assert.Single(found.Rooms);
        var ids = new[] { "rd-01" };
        var names = new[] { RoomDetect.Name(labels, right.Ring) };

        var detected = RoomDetect.Report(right.Ring, "room-01", 0, found.Rooms, ids, names, labels);
        var byHand = RoomDetect.Report(drawn, "room-02", 12.96 * M2, found.Rooms, ids, names, labels);
        Assert.Equal(("rd-01", "Stue", true), (detected.Id, detected.Name, detected.Detected));
        Assert.Equal(("room-02", "Bad", false), (byHand.Id, byHand.Name, byHand.Detected));
        Assert.Equal(12.96 * M2, byHand.Area, 3);
        Assert.Equal(2100, byHand.At.X, 3);
        Assert.Equal(2100, byHand.At.Y, 3);

        // The plan reads both back from the stamps; a label moved since does not rename them.
        var (name, at) = Tagged(detected, right.Ring);
        Assert.Equal("Stue", name);
        Assert.Equal(detected.At.X, at.X, 6);
        Assert.Equal(detected.At.Y, at.Y, 6);
        (name, at) = Tagged(byHand, drawn);
        Assert.Equal("Bad", name);
        Assert.Equal(2100, at.X, 6);
        Assert.Equal(2100, at.Y, 6);
    }

    [Fact]
    public void DividerSplit_EachHalfIsReportedAndTaggedOnItsOwn()
    {
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(Wall(Rect(0, 0, 8200, 4200), Rect(200, 200, 8000, 4000)));
        scene.Dividers.Add(new List<Pt> { new Pt(4100, 250), new Pt(4100, 3950) });
        var labels = new[] { Label("Kjøkken", 250, 1000, 1000), Label("Stue", 250, 7000, 3000) };
        var found = RoomDetect.Detect(scene);
        var ids = RoomDetect.Match(found.Rooms, new List<KeyValuePair<string, List<Pt>>>(), "rd-");
        var names = found.Rooms.Select(room => RoomDetect.Name(labels, room.Ring)).ToArray();

        var tags = found.Rooms.Select(room => RoomDetect.Report(room.Ring, "", 0, found.Rooms, ids, names, labels)).ToList();
        Assert.Equal(new[] { "Kjøkken", "Stue" }, tags.Select(t => t.Name).OrderBy(n => n).ToArray());
        Assert.Equal(new[] { "rd-01", "rd-02" }, tags.Select(t => t.Id).OrderBy(id => id).ToArray());
        for (var i = 0; i < tags.Count; i++)
        {
            var (name, at) = Tagged(tags[i], found.Rooms[i].Ring);
            Assert.Equal(tags[i].Name, name);
            Assert.Equal(found.Rooms[i].Inside.X, at.X, 6);
            Assert.Equal(found.Rooms[i].Inside.Y, at.Y, 6);
            Assert.True(name == "Kjøkken" ? at.X < 4100 : at.X > 4100);
        }
    }

    [Fact]
    public void StampedTagPoint_FollowsTheOutlineMovedOrScaled_AndGivesWayWhenItFallsOutside()
    {
        var ring = Rect(0, 0, 4000, 2000);
        var stamp = RoomDetect.StampAt(ring, new Pt(1000, 1500));
        var moved = ring.Select(p => new Pt(p.X * 2 + 5000, p.Y * 2 - 3000)).ToList();
        Assert.True(RoomDetect.TryTag("Stue", stamp, 32 * M2, NoLabels, moved, out _, out var at, out _));
        Assert.Equal(7000, at.X, 6);
        Assert.Equal(0, at.Y, 6);

        // An L: the stamp points at the corner it leaves open, so the point is found again.
        var ell = new List<Pt> { new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 1000), new Pt(1000, 1000), new Pt(1000, 4000), new Pt(0, 4000) };
        Assert.True(RoomDetect.TryTag("Gang", "0.9,0.9", 7 * M2, NoLabels, ell, out _, out at, out _));
        Assert.True(RoomDetect.Contains(ell, at));
    }

    static RoomDetect.Label Label(string text, double height, double x, double y) =>
        new RoomDetect.Label(text, height, new Pt(x, y));

    [Fact]
    public void Label_ACupboardDoesNotNameABigRoom()
    {
        var corridor = new[] { Label("Brannskap", 250, 100, 100) };
        Assert.Equal("Room", RoomDetect.PickLabel(corridor, 92.6 * M2, new Pt(0, 0)));
        Assert.Equal("Rom", RoomDetect.PickLabel(corridor, 92.6 * M2, new Pt(0, 0), true));
        Assert.Equal("Brannskap", RoomDetect.PickLabel(corridor, 1.5 * M2, new Pt(0, 0)));
    }

    [Fact]
    public void Label_TallestTextWinsThenNearest()
    {
        var labels = new[]
        {
            Label("Kontor", 250, 5000, 0),
            Label("Gang", 350, 9000, 0),
            Label("Sluk", 400, 0, 0)
        };
        Assert.Equal("Gang", RoomDetect.PickLabel(labels, 40 * M2, new Pt(0, 0)));
        var even = new[] { Label("Kontor", 250, 5000, 0), Label("Møterom", 250, 100, 0) };
        Assert.Equal("Møterom", RoomDetect.PickLabel(even, 40 * M2, new Pt(0, 0)));
    }

    [Fact]
    public void Label_NoneInside_IsRoom()
    {
        Assert.Equal("Room", RoomDetect.PickLabel(new RoomDetect.Label[0], 12.6 * M2, new Pt(0, 0)));
        Assert.Equal("Rom", RoomDetect.PickLabel(new RoomDetect.Label[0], 12.6 * M2, new Pt(0, 0), true));
    }

    [Fact]
    public void Tag_MarkerWithNoStamps_IsNamedByItsLabelAtItsOwnPoint_TooSmallIsUntagged()
    {
        // A marker rooms_from_layer made alone has no rooms_detect stamps.
        var ring = new List<Pt> { new Pt(0, 0), new Pt(3000, 0), new Pt(3000, 2000), new Pt(0, 2000) };
        var labels = new[] { new RoomDetect.Label("Bad", 250, new Pt(1500, 1000)) };
        Assert.True(RoomDetect.TryTag(null, null, 6 * M2, labels, ring, out var name, out var at, out var untagged));
        Assert.Equal("Bad", name);
        Assert.Equal((1500.0, 1000.0), (at.X, at.Y));
        Assert.Null(untagged);
        Assert.True(RoomDetect.TryTag("", "", 0.5 * M2, labels, ring, out name, out _, out untagged));
        Assert.Equal("Bad", name);
        Assert.Equal("0.50 m² under 1 m²", untagged);
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

    static RoomDetect.Room RoomOf(List<Pt> ring)
    {
        RoomDetect.TryInside(new List<List<Pt>> { ring }, out var at);
        return new RoomDetect.Room { Ring = ring, Inside = at, Area = Math.Abs(RoomDetect.Area(ring)) };
    }

    [Fact]
    public void MergedRoomsKeepTheLargerId_ASplitKeepsItOnTheLargerPiece()
    {
        var small = RoomOf(Rect(0, 0, 2000, 2000));
        var large = RoomOf(Rect(2000, 0, 6000, 4000));
        var merged = RoomOf(Rect(0, 0, 6000, 4000));
        var earlier = new List<KeyValuePair<string, List<Pt>>>
        {
            new KeyValuePair<string, List<Pt>>("rd-01", small.Ring),
            new KeyValuePair<string, List<Pt>>("rd-04", large.Ring)
        };
        Assert.Equal("rd-04", RoomDetect.Match(new[] { merged }, earlier, "rd-")[0]);

        var left = RoomOf(Rect(0, 0, 2000, 2000));
        var right = RoomOf(Rect(2000, 0, 8000, 4000));
        var whole = new List<KeyValuePair<string, List<Pt>>>
        {
            new KeyValuePair<string, List<Pt>>("rd-03", Rect(0, 0, 8000, 4000))
        };
        Assert.Equal(new[] { "rd-04", "rd-03" }, RoomDetect.Match(new[] { left, right }, whole, "rd-"));
    }
}
