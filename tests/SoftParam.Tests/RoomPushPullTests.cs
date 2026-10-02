using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// F2 J5: room.push_pull is a prefill on the room row, true when one room is
/// selected and every wall has a path the join graph can read. The command
/// finds the wall run whose face is that side of the room.
/// </summary>
public class RoomPushPullTests
{
    const double Tol = 1.0;

    [Fact]
    public void IsTrue_WithOneRoomSelected_AndTheJoinGraph()
    {
        var f = Docs.Facts("house, room selected");
        Assert.True(f.JoinGraph);
        Assert.True(ForskRegistry.Find("room.push_pull")!.Shows(f));
        Assert.Contains("room.push_pull", ForskRegistry.Card(f).Actions.Select(a => a.Id));
    }

    [Fact]
    public void IsAbsent_WithAWallTheGraphCannotRead_NoRoom_OrTwoRooms()
    {
        var action = ForskRegistry.Find("room.push_pull")!;
        var noPath = Docs.Facts("house, room selected, a wall without a path");
        Assert.False(noPath.JoinGraph);
        Assert.False(action.Shows(noPath));
        Assert.False(action.Shows(Docs.Facts("house")));
        var twoRooms = FileClassifier.Read(Docs.Of(Docs.House(roomSelected: true).Append(Row.Room(selected: true, name: "Kjøkken")).ToArray()));
        Assert.Equal(2, twoRooms.PickedCount);
        Assert.False(action.Shows(twoRooms));
    }

    [Fact]
    public void Slot1_StaysPrint_AndPushPullWaitsOnTheCard()
    {
        var f = Docs.Facts("house, room selected");
        var bar = ForskRegistry.Bar(f);
        Assert.Equal(new[] { "file.print", "daylight.room", "section.room" }, bar.Slots.Select(a => a.Id));
        // A Modeller pick is a small boost: slot 3 may go to push-pull. Slot 1 never moves.
        var modeller = ForskRegistry.Bar(f, ForskRole.Modeller);
        Assert.Equal(new[] { "file.print", "daylight.room", "room.push_pull" }, modeller.Slots.Select(a => a.Id));
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("room.push_pull"));
        Assert.Equal("Push or pull a side", ForskText.Label("room.push_pull"));
    }

    [Theory]
    [InlineData("", "Push the north side of this room 500 mm out")]
    [InlineData("flytt veggen", "Skyv nordsiden av rommet 500 mm ut")]
    public void Prefill_SelectsTheNumber_InTheLastMessagesLanguage(string last, string text)
    {
        var prefill = ForskPrefill.For("room.push_pull", Docs.Facts("house, room selected"), last)!;
        Assert.Equal(text, prefill.Text);
        Assert.Equal("500", prefill.Text.Substring(prefill.Start, prefill.Length));
    }

    [Theory]
    [InlineData("Push the north side of this room 500 mm out")]
    [InlineData("pull the east side of the room in by 300 mm")]
    [InlineData("skyv nordsiden av rommet 500 mm ut")]
    [InlineData("dra rommet 300 mm mot sør")]
    public void Sentence_RoutesToEdit(string text)
    {
        Assert.Equal(ForskIntent.Edit, ForskIntentRouter.Classify(text));
    }

    [Fact]
    public void Side_PicksTheRunWhoseFaceIsThatSideOfTheRoom()
    {
        var shape = WallJoinsTests.TwoRooms();
        var west = shape[1];
        Assert.True(WallJoins.TryRoomSide(west, "north", Tol, out var at, out var why), why);
        Assert.True(WallEdit.TryPick(shape, at, Tol, out var run, out why), why);
        Assert.Equal((3800.0, 4000.0), (run.Near, run.Far));

        Assert.True(WallJoins.TryRoomSide(west, "east", Tol, out at, out why), why);
        Assert.True(WallEdit.TryPick(shape, at, Tol, out run, out why), why);
        Assert.Equal((3900.0, 4100.0), (run.Near, run.Far));

        // Wound the other way, the room's outside is still found.
        var reversed = Enumerable.Reverse(west).ToList();
        Assert.True(WallJoins.TryRoomSide(reversed, "south", Tol, out at, out why), why);
        Assert.Equal(199, at.Y, 6);
        Assert.False(WallJoins.TryRoomSide(west, "up", Tol, out _, out why));
        Assert.Equal("side is north, south, east or west.", why);
    }
}
