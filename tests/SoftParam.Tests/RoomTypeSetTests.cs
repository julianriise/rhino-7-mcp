using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// A room with no type suggests Set room type, which writes "Change room type
/// to: " into the chat box. Several rooms picked show one Type in the
/// Properties panel and set them all; the receipt names every room it set.
/// </summary>
public class RoomTypeSetTests
{
    static ChipRow Room(string name, string? type = null) => new ChipRow
    {
        Id = System.Guid.NewGuid().ToString(), Generated = true, Kind = "room", Selected = true, Name = name, RoomType = type, Area = "12000000"
    };

    static FileFacts Picked(params ChipRow[] rows) => new FileFacts
    {
        Selected = rows.ToList(), PickedCount = rows.Length, Picked = RhinoMCPPlugin.Forsk.Picked.Room, HasWalls = true, HasRooms = true, Kind = FileKind.Model
    };

    [Fact]
    public void AnUntypedRoom_SuggestsSetRoomType_ATypedOneDoesNot()
    {
        Assert.Contains(ForskRegistry.Bar(Picked(Room("Rom"))).Context, a => a.Id == "room.set_type");
        Assert.Contains(ForskRegistry.Bar(Picked(Room("Rom", RoomTypes.Unassigned))).Context, a => a.Id == "room.set_type");
        Assert.DoesNotContain(ForskRegistry.Bar(Picked(Room("Kitchen", RoomTypes.Kitchen))).Context, a => a.Id == "room.set_type");
    }

    [Fact]
    public void SetRoomType_WritesTheStartOfTheSentence_CursorAtTheEnd()
    {
        var prefill = ForskPrefill.For("room.set_type", Picked(Room("Rom")), "")!.ToJson(1);
        Assert.Equal("Change room type to: ", prefill["text"]!.ToString());
        Assert.Equal(21, prefill["start"]!.Value<int>());
        Assert.Equal(21, prefill["end"]!.Value<int>());
    }

    [Fact]
    public void SeveralRooms_ShowOneTypeField_ThatSetsTheSelection()
    {
        var panel = ForskInfo.For(Picked(Room("A"), Room("B", RoomTypes.Kitchen)))!;
        Assert.Equal(ForskInfo.SelectionId, panel.Id);
        var type = Assert.Single(panel.Rows, r => r.Field == "room_type");
        Assert.Equal("Mixed", type.Value);
        var edit = ForskInfo.Edit(panel, ForskInfo.SelectionId, "room_type", RoomTypes.Bedroom, out var error);
        Assert.Null(error);
        Assert.Equal("rooms_set_type", edit!.Value.Tool);
        Assert.Null(edit.Value.Args["id"]);
        Assert.Equal(RoomTypes.Bedroom, edit.Value.Args["room_type"]!.ToString());
    }

    [Fact]
    public void TheReceipt_NamesEveryRoomItSet()
    {
        Assert.Equal("Rom is Bedroom.", RoomTypes.SetSentence(new List<string> { "Rom" }, RoomTypes.Bedroom));
        Assert.Equal("2 rooms are Bedroom: Rom, Rom 2.", RoomTypes.SetSentence(new List<string> { "Rom", "Rom 2" }, RoomTypes.Bedroom));
    }
}
