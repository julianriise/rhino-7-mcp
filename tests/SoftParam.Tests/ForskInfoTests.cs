using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The info panel (2.0 UX.5): pick anything Forsk made and the top of the
/// window says what it is, from its record, without asking in chat. A field
/// that can change runs the same tool the chat would.
/// </summary>
public class ForskInfoTests
{
    static ChipRow Picked(string kind, params (string Key, string Value)[] info)
    {
        var row = new ChipRow { Generated = true, Selected = true, Kind = kind, Id = Guid.NewGuid().ToString(), Info = new Dictionary<string, string>() };
        foreach (var (key, value) in info) row.Info[key] = value;
        return row;
    }

    static FileFacts Facts(params ChipRow[] picked) => new FileFacts { Selected = picked.ToList(), PickedCount = picked.Length };

    static string Value(ForskInfo.Panel panel, string label) => panel.Rows.Single(r => r.Label == label).Value;

    static ChipRow Door()
    {
        var door = Picked("opening_marker", (OpeningTypes.TypeKey, "door.hinged_single"), (OpeningTypes.HandKey, "left"), (OpeningTypes.SwingKey, "in"), (ForskInfo.HostKey, "w03"));
        door.OpeningKind = "door";
        door.Mark = "D02";
        door.Width = "900";
        door.Sill = "0";
        door.Head = "2100";
        return door;
    }

    [Fact]
    public void NothingPicked_OrNothingForskMade_ShowsNoPanel()
    {
        Assert.Null(ForskInfo.For(Facts()));
        var loose = new ChipRow { Selected = true, Kind = null, Curve = true };
        Assert.Null(ForskInfo.For(Facts(loose)));
    }

    [Fact]
    public void ARoom_ShowsItsTypeAreaCeilingFloorAndDaylight()
    {
        var room = Picked("room_plate", ("forsk:room_id", "R04"), ("forsk:level", "0"), (ForskInfo.CeilingKey, "2700"));
        room.Name = "Living";
        room.Area = "24500000";
        room.RoomType = RoomTypes.Living;
        room.Marker = "marker-guid";
        var facts = Facts(room);
        facts.Analysis.RoomDf["R04"] = 2.34;
        var panel = ForskInfo.For(facts)!;
        Assert.Equal(("Living", "Room R04", "marker-guid"), (panel.Title, panel.Subtitle, panel.Id));
        Assert.Equal("24.5 m²", Value(panel, "Net area"));
        Assert.Equal("2700 mm", Value(panel, "Ceiling height"));
        Assert.Equal("Ground floor", Value(panel, "Floor"));
        Assert.Equal("2.3 % mean", Value(panel, "Daylight"));
        var type = panel.Rows.Single(r => r.Label == "Type");
        Assert.Equal(("living", "room_type"), (type.Value, type.Field));
        Assert.Contains(type.Options!, o => o.Id == RoomTypes.Bathroom);
    }

    [Fact]
    public void ARoomWithoutDaylight_SaysItHasNotRun()
    {
        var room = Picked("room", ("forsk:room_id", "R01"));
        Assert.Equal("Not run yet", Value(ForskInfo.For(Facts(room))!, "Daylight"));
    }

    [Fact]
    public void ADoor_ShowsTypeSizeHangAndHostWall_AndItsSizesCanChange()
    {
        var panel = ForskInfo.For(Facts(Door()))!;
        Assert.Equal(("Door D02", "Hinged door"), (panel.Title, panel.Subtitle));
        Assert.Equal("2100 mm", Value(panel, "Height"));
        Assert.Equal("Left hand, opens in", Value(panel, "Hand and swing"));
        Assert.Equal("w03", Value(panel, "In wall"));
        Assert.Equal(new[] { "type", "width", "sill", "head" }, panel.Rows.Where(r => r.Field != null).Select(r => r.Field));
        Assert.Equal("900", panel.Rows.Single(r => r.Field == "width").Value);
        Assert.All(panel.Rows.Single(r => r.Field == "type").Options!, o => Assert.StartsWith("door.", o.Id));
    }

    [Fact]
    public void AWall_ShowsThicknessHeightAndLength()
    {
        var wall = Picked("wall", ("forsk:path", "{\"outer\":[[0,0],[4200,0],[4200,200],[0,200]]}"), ("forsk:height", "2700"));
        wall.ForskId = "w01";
        wall.Thickness = "200";
        wall.RunName = "the north wall";
        var panel = ForskInfo.For(Facts(wall))!;
        Assert.Equal(("Wall w01", "The north wall"), (panel.Title, panel.Subtitle));
        Assert.Equal("200 mm", Value(panel, "Thickness"));
        Assert.Equal("2700 mm", Value(panel, "Height"));
        Assert.Equal("4.20 m", Value(panel, "Length"));
        Assert.DoesNotContain(panel.Rows, r => r.Field != null);
    }

    [Fact]
    public void AWallWithoutAStoredHeight_UsesItsMeasuredOne()
    {
        var wall = Picked("wall", (ForskInfo.HeightKey, "3000"));
        Assert.Equal("3000 mm", Value(ForskInfo.For(Facts(wall))!, "Height"));
    }

    [Fact]
    public void AStair_AndAPieceOfFurniture()
    {
        var stair = Picked("stair", (Stairs.RiserKey, "178.5"), (Stairs.RiseKey, "2856"));
        stair.ForskId = "S01";
        stair.Risers = "16";
        stair.Going = "250";
        var panel = ForskInfo.For(Facts(stair))!;
        Assert.Equal("Stair S01", panel.Title);
        Assert.Equal(("16", "178.5 mm", "250 mm", "2856 mm"), (Value(panel, "Steps"), Value(panel, "Riser"), Value(panel, "Going"), Value(panel, "Total rise")));

        var sofa = Picked("furniture", (Furniture.CatalogKey, "sofa.3seat"), (Furniture.RoomKey, "R04"));
        var piece = ForskInfo.For(Facts(sofa))!;
        Assert.Equal("3-seat sofa", piece.Title);
        Assert.Equal("2200 × 900 × 800 mm", Value(piece, "Size"));
        Assert.Equal("R04", Value(piece, "Room"));
    }

    [Fact]
    public void TwoDoors_GetOneLineEach()
    {
        var other = Door();
        other.Mark = "D03";
        other.Width = "800";
        var panel = ForskInfo.For(Facts(Door(), other))!;
        Assert.Equal("2 doors", panel.Title);
        Assert.Equal(new[] { "D02", "D03" }, panel.Rows.Select(r => r.Label));
        Assert.Equal("Hinged door · 800 × 2100 mm", Value(panel, "D03"));
        Assert.Null(panel.Id);
    }

    [Fact]
    public void AMixedPick_CountsEachKind_WithTheWallLengthAndRoomArea()
    {
        var wall = Picked("wall", ("forsk:path", "{\"outer\":[[0,0],[4000,0],[4000,200],[0,200]]}"));
        wall.Thickness = "200";
        var other = Picked("wall", ("forsk:path", "{\"outer\":[[0,0],[200,0],[200,3000],[0,3000]]}"));
        other.Thickness = "200";
        var room = Picked("room");
        room.Area = "12000000";
        var panel = ForskInfo.For(Facts(wall, other, room, Door()))!;
        Assert.Equal("4 things", panel.Title);
        Assert.Equal(("2", "1", "1"), (Value(panel, "Walls"), Value(panel, "Doors"), Value(panel, "Rooms")));
        Assert.Equal("7.00 m", Value(panel, "Wall length"));
        Assert.Equal("12.0 m²", Value(panel, "Net area"));
    }

    [Theory]
    [InlineData("width", "1000", "set_opening", "width", 1000.0)]
    [InlineData("sill", "900 mm", "set_opening", "sill", 900.0)]
    [InlineData("head", "2200", "set_opening", "head", 2200.0)]
    public void ATypedSize_IsTheSameEditAsTheChat(string field, string typed, string tool, string key, double mm)
    {
        var edit = ForskInfo.Edit("marker-guid", field, typed, out var error)!.Value;
        Assert.Null(error);
        Assert.Equal(tool, edit.Tool);
        Assert.Equal("marker-guid", edit.Args["id"]!.ToString());
        Assert.Equal(mm, edit.Args[key]!.Value<double>());
    }

    [Fact]
    public void AChosenTypeOrRoomType_RunsItsTool_AndNonsenseIsRefused()
    {
        Assert.Equal("set_opening_type", ForskInfo.Edit("m", "type", "door.sliding", out _)!.Value.Tool);
        Assert.Equal("rooms_set_type", ForskInfo.Edit("m", "room_type", RoomTypes.Bathroom, out _)!.Value.Tool);
        Assert.Null(ForskInfo.Edit("m", "width", "wide", out var error));
        Assert.Equal("Type a size in millimetres, like 900.", error);
        Assert.Null(ForskInfo.Edit("m", "width", "0", out _));
        Assert.Null(ForskInfo.Edit("m", "type", "door.revolving", out _));
        Assert.Null(ForskInfo.Edit("m", "thickness", "300", out error));
        Assert.Equal("That cannot be changed here.", error);
        Assert.Null(ForskInfo.Edit("", "width", "900", out _));
    }

    [Fact]
    public void ThePanel_IsInTheWindowModelOnlyWhileSomethingIsPicked()
    {
        var thread = new DocThread();
        Assert.Null(WindowView.Build(thread, Facts())["info"]);
        var model = WindowView.Build(thread, Facts(Door()));
        Assert.Equal("Door D02", model["info"]!["title"]!.ToString());
        Assert.Equal("width", model["info"]!["rows"]![1]!["field"]!.ToString());
        Assert.Equal("mm", model["info"]!["rows"]![1]!["unit"]!.ToString());
    }
}
