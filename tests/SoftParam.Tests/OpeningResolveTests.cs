using System.Collections.Generic;
using System.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// A selected frame, glass pane, block, or group is the opening's marker.
/// A wall in that group is still a wall.
/// </summary>
public class OpeningResolveTests
{
    static OpeningResolve.Part Piece(
        string id, string kind, string marker = null, string forskId = null, string group = null, string part = null) =>
        new OpeningResolve.Part
        {
            Id = id,
            Kind = kind,
            Marker = marker,
            ForskId = forskId,
            Group = group,
            Member = part
        };

    static ChipRow Marked(string kind, string id, string mark, string width, string sill, string head) =>
        new ChipRow
        {
            Id = id,
            Generated = true,
            Kind = "opening_marker",
            OpeningKind = kind,
            Mark = mark,
            Width = width,
            Sill = sill,
            Head = head,
            Visible = false
        };

    static ChipRow PieceRow(ChipRow marker, string part, bool selected, string group = "1") =>
        new ChipRow
        {
            Id = part + "-" + marker.Id,
            Generated = true,
            Kind = "opening",
            OpeningKind = marker.OpeningKind,
            Part = part,
            Marker = marker.Id,
            Group = group,
            Selected = selected,
            Visible = true
        };

    [Fact]
    public void FrameOnly_ResolvesToTheMarker()
    {
        var marker = Piece("m", "opening_marker", forskId: "D01");
        var frame = Piece("f", "opening", marker: "m", part: "frame");
        var all = new[] { marker, frame, Piece("g", "opening", marker: "m", part: "glass") };
        Assert.Equal("m", OpeningResolve.MarkerOf(all, frame));
        Assert.Equal(new[] { "m" }, OpeningResolve.Selected(all, new[] { frame }));
    }

    [Fact]
    public void GlassOnly_ResolvesToTheMarker()
    {
        var marker = Piece("m", "opening_marker");
        var glass = Piece("g", "opening", marker: "m", part: "glass");
        Assert.Equal("m", OpeningResolve.MarkerOf(new[] { marker, glass }, glass));
        var named = Piece("pane", "glass", marker: "m");
        Assert.Equal("m", OpeningResolve.MarkerOf(new[] { marker, named }, named));
    }

    [Fact]
    public void WholeGroup_IsOneMarker()
    {
        var marker = Piece("m", "opening_marker", group: "2");
        var frame = Piece("f", "opening", part: "frame", group: "2");
        var glass = Piece("g", "opening", part: "glass", group: "2");
        var leaf = Piece("l", "opening", marker: "m", part: "leaf", group: "2");
        var all = new[] { marker, frame, glass, leaf };
        Assert.Equal("m", OpeningResolve.MarkerOf(all, frame));
        Assert.Equal("m", OpeningResolve.MarkerOf(all, glass));
        Assert.Equal(new[] { "m" }, OpeningResolve.Selected(all, new[] { frame, glass, leaf }));
    }

    [Fact]
    public void AGroupMemberCarriesTheMarker_WhenTheMarkerItselfIsNotGrouped()
    {
        var marker = Piece("m", "opening_marker");
        var glass = Piece("g", "opening", marker: "m", part: "glass", group: "4");
        var frame = Piece("f", "opening", part: "frame", group: "4");
        Assert.Equal("m", OpeningResolve.MarkerOf(new[] { marker, glass, frame }, frame));
    }

    [Fact]
    public void BlockInstance_ResolvesByMarkerId()
    {
        var marker = Piece("m", "opening_marker", forskId: "V02");
        var block = Piece("b", "opening", marker: "m");
        Assert.Equal("m", OpeningResolve.MarkerOf(new[] { marker, block }, block));
    }

    [Fact]
    public void SharedForskId_Resolves_IncludingTheMarkerGuid()
    {
        var marker = Piece("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "opening_marker", forskId: "D03");
        var byName = Piece("f", "opening", forskId: "d03", part: "frame");
        var byGuid = Piece("g", "opening", forskId: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", part: "glass");
        var all = new[] { marker, byName, byGuid };
        Assert.Equal(marker.Id, OpeningResolve.MarkerOf(all, byName));
        Assert.Equal(marker.Id, OpeningResolve.MarkerOf(all, byGuid));
    }

    [Fact]
    public void TwoOpenings_StayTwo()
    {
        var first = Piece("m1", "opening_marker");
        var second = Piece("m2", "opening_marker");
        var frame = Piece("f1", "opening", marker: "m1", part: "frame");
        var glass = Piece("g2", "opening", marker: "m2", part: "glass");
        var ids = OpeningResolve.Selected(new[] { first, second, frame, glass }, new[] { frame, glass });
        Assert.Equal(new[] { "m1", "m2" }, ids);
    }

    [Fact]
    public void AWallInTheSameGroup_IsNotAnOpening()
    {
        var marker = Piece("m", "opening_marker", group: "1", forskId: "w01");
        var wall = Piece("w", "wall", forskId: "w01", group: "1");
        Assert.Null(OpeningResolve.MarkerOf(new[] { marker, wall }, wall));
        Assert.Empty(OpeningResolve.Selected(new[] { marker, wall }, new[] { wall }));
    }

    [Fact]
    public void FrameOnly_GlassOnly_AndTheWholeGroup_PickAsOneOpening()
    {
        var marker = Marked("window", "win-1", "V02", "1200", "900", "2100");
        var frame = PieceRow(marker, "frame", true);
        var glass = PieceRow(marker, "glass", false);
        var leaf = PieceRow(marker, "leaf", false);
        var rows = new List<ChipRow> { Row.Wall(), marker, frame, glass, leaf };

        var frameOnly = FileClassifier.Read(Docs.Of(rows.ToArray()));
        Assert.Equal(Picked.Opening, frameOnly.Picked);
        Assert.Equal(1, frameOnly.PickedCount);
        Assert.Equal("window", frameOnly.PickedOpeningKind);
        Assert.Equal("Window V02 · 1200 × 1200", ForskPick.Line(rows, false));

        frame.Selected = false;
        glass.Selected = true;
        var glassOnly = FileClassifier.Read(Docs.Of(rows.ToArray()));
        Assert.Equal(Picked.Opening, glassOnly.Picked);
        Assert.Equal(1, glassOnly.PickedCount);
        Assert.Equal("window", glassOnly.PickedOpeningKind);
        Assert.Equal("Window V02 · 1200 × 1200", ForskPick.Line(rows, false));

        frame.Selected = true;
        glass.Selected = true;
        leaf.Selected = true;
        var whole = FileClassifier.Read(Docs.Of(rows.ToArray()));
        Assert.Equal(Picked.Opening, whole.Picked);
        Assert.Equal(1, whole.PickedCount);
        Assert.Equal("window", whole.PickedOpeningKind);
        Assert.Equal("Window V02 · 1200 × 1200", ForskPick.Line(rows, false));
    }

    [Fact]
    public void ABlockInstance_PicksAsItsDoor_AndTwoWindowsStayTwo()
    {
        var door = Marked("door", "door-1", null, "900", "0", "2100");
        var block = Row.DoorFrame(selected: true);
        block.Marker = door.Id;
        var one = FileClassifier.Read(Docs.Of(Row.Wall(), door, block));
        Assert.Equal(Picked.Opening, one.Picked);
        Assert.Equal(1, one.PickedCount);
        Assert.Equal("door", one.PickedOpeningKind);
        Assert.Equal("Door · 900 × 2100", ForskPick.Line(new[] { Row.Wall(), door, block }, false));

        var first = Marked("window", "w-a", "V01", "900", "900", "2100");
        var second = Marked("window", "w-b", "V02", "900", "900", "2100");
        var rows = new[] { PieceRow(first, "frame", true, null), PieceRow(second, "glass", true, null), first, second };
        var two = FileClassifier.Read(Docs.Of(rows));
        Assert.Equal(Picked.Opening, two.Picked);
        Assert.Equal(2, two.PickedCount);
        Assert.Equal("2 windows", ForskPick.Line(rows, false));
    }

    [Fact]
    public void AWallShareAGroup_StaysAWall_AndAPlateWithItsMarkerStaysOneRoom()
    {
        var marker = Row.Door();
        marker.Group = "7";
        var wall = Row.Wall(selected: true, run: "the north wall", toward: "north");
        wall.Group = "7";
        var wallPick = FileClassifier.Read(Docs.Of(wall, marker));
        Assert.Equal(Picked.Wall, wallPick.Picked);
        Assert.Equal(1, wallPick.PickedCount);

        var room = Row.Room();
        var plate = Row.Plate(room, selected: true);
        room.Selected = true;
        var roomPick = FileClassifier.Read(Docs.Of(Row.Wall(), room, plate));
        Assert.Equal(Picked.Room, roomPick.Picked);
        Assert.Equal(1, roomPick.PickedCount);
    }
}
