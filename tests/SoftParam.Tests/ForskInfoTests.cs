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
        Assert.Equal("2100", Value(panel, "Height"));
        Assert.Equal("w03", Value(panel, "In wall"));
        Assert.Equal(new[] { "type", "width", "height", "sill", "head", "hand", "swing" }, panel.Rows.Where(r => r.Field != null).Select(r => r.Field));
        Assert.Equal("900", panel.Rows.Single(r => r.Field == "width").Value);
        Assert.Equal(("L", "in"), (Value(panel, "Hand"), Value(panel, "Opens")));
        Assert.All(panel.Rows.Single(r => r.Field == "type").Options!, o => Assert.StartsWith("door.", o.Id));
    }

    static ChipRow OneRunWall()
    {
        var wall = Picked("wall", ("forsk:path", "{\"outer\":[[0,0],[4200,0],[4200,200],[0,200]]}"), ("forsk:height", "2700"));
        wall.ForskId = "w01";
        wall.Thickness = "200";
        wall.Runs = 1;
        wall.RunName = "the north wall";
        wall.RunLength = 4200;
        wall.RunFreeEnd = true;
        return wall;
    }

    [Fact]
    public void AWallOfOneRun_ChangesItsThicknessHeightAndLength()
    {
        var wall = OneRunWall();
        var panel = ForskInfo.For(Facts(wall))!;
        Assert.Equal(("Wall w01", "The north wall", wall.Id), (panel.Title, panel.Subtitle, panel.Id));
        Assert.Equal(new[] { "thickness", "wall_height", "length" }, panel.Rows.Select(r => r.Field));
        Assert.Equal(("200", "2700", "4200"), (Value(panel, "Thickness"), Value(panel, "Height"), Value(panel, "Length")));
        Assert.All(panel.Rows, r => Assert.Equal("mm", r.Unit));
        Assert.Null(panel.Note);
    }

    [Fact]
    public void AWallJoinedAtBothEnds_ReadsItsLength()
    {
        var wall = OneRunWall();
        wall.RunFreeEnd = false;
        var panel = ForskInfo.For(Facts(wall))!;
        Assert.Equal("4.20 m", Value(panel, "Length"));
        Assert.Null(panel.Rows.Single(r => r.Label == "Length").Field);
        Assert.Equal("thickness", panel.Rows.Single(r => r.Label == "Thickness").Field);
    }

    [Fact]
    public void AWholeWallRecord_ReadsItsThicknessAndLength_AndSaysToSplit()
    {
        var wall = OneRunWall();
        wall.Runs = 4;
        wall.RunName = null;
        wall.RunLength = null;
        var panel = ForskInfo.For(Facts(wall))!;
        Assert.Equal(("200 mm", "4.20 m"), (Value(panel, "Thickness"), Value(panel, "Length")));
        Assert.Equal(new[] { "wall_height" }, panel.Rows.Where(r => r.Field != null).Select(r => r.Field));
        Assert.Equal(ForskInfo.SplitNote, panel.Note);
        Assert.Equal(ForskInfo.SplitNote, panel.ToJson()["note"]!.ToString());
    }

    [Fact]
    public void AWallWithoutAStoredHeight_UsesItsMeasuredOne()
    {
        var wall = Picked("wall", (ForskInfo.HeightKey, "3000"));
        Assert.Equal("3000", Value(ForskInfo.For(Facts(wall))!, "Height"));
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
        Assert.Equal(("16", "178.5", "250", "2856"), (Value(panel, "Steps"), Value(panel, "Riser"), Value(panel, "Going"), Value(panel, "Total rise")));
        Assert.Equal(stair.Id, panel.Id);
        Assert.Equal(new[] { "riser_max", "going", "rise" }, panel.Rows.Where(r => r.Field != null).Select(r => r.Field));
        Assert.Equal("–", Value(panel, "Width"));

        var sofa = Picked("furniture", (Furniture.CatalogKey, "sofa.3seat"), (Furniture.RoomKey, "R04"), (ForskInfo.RotationKey, "90"));
        var piece = ForskInfo.For(Facts(sofa))!;
        Assert.Equal("3-seat sofa", piece.Title);
        Assert.Equal("2200 × 900 × 800 mm", Value(piece, "Size"));
        Assert.Equal("R04", Value(piece, "Room"));
        var turn = piece.Rows.Single(r => r.Label == "Rotation");
        Assert.Equal(("90", "rotation", "°"), (turn.Value, turn.Field, turn.Unit));
    }

    [Fact]
    public void AStairThatFollowsTheWalls_ShowsAuto_AndItsWidthChanges()
    {
        var stair = Picked("stair", (Stairs.RiserKey, "175"), (Stairs.RiseKey, "auto"), (Stairs.WidthKey, "900"));
        var panel = ForskInfo.For(Facts(stair))!;
        var rise = panel.Rows.Single(r => r.Label == "Total rise");
        Assert.Equal(("auto", "rise", null), (rise.Value, rise.Field, rise.Unit));
        Assert.Equal("900", Value(panel, "Width"));
        Assert.Equal("stair_width", panel.Rows.Single(r => r.Label == "Width").Field);
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

    static ForskInfo.Panel PanelOf(ChipRow row) => ForskInfo.For(Facts(row))!;

    static (string Tool, JObject Args) Edit(ChipRow row, string field, string typed)
    {
        var panel = PanelOf(row);
        var edit = ForskInfo.Edit(panel, panel.Id, field, typed, out var error);
        Assert.Null(error);
        return edit!.Value;
    }

    [Theory]
    [InlineData("width", "1000", "set_opening", "width", 1000.0)]
    [InlineData("sill", "900 mm", "set_opening", "sill", 900.0)]
    [InlineData("head", "2200", "set_opening", "head", 2200.0)]
    // A new height keeps the sill: 0 + 2300.
    [InlineData("height", "2300", "set_opening", "head", 2300.0)]
    public void ATypedOpeningSize_IsTheSameEditAsTheChat(string field, string typed, string tool, string key, double mm)
    {
        var door = Door();
        var edit = Edit(door, field, typed);
        Assert.Equal(tool, edit.Tool);
        Assert.Equal(door.Id, edit.Args["id"]!.ToString());
        Assert.Equal(mm, edit.Args[key]!.Value<double>());
    }

    [Fact]
    public void AWindowsHeight_KeepsItsSill()
    {
        var window = Picked("opening_marker", (OpeningTypes.TypeKey, "window.side_hung"));
        window.OpeningKind = "window";
        window.Sill = "900";
        window.Head = "2100";
        window.Width = "1200";
        Assert.Equal(2400.0, Edit(window, "height", "1500").Args["head"]!.Value<double>());
    }

    [Fact]
    public void AChosenTypeHandSwingOrRoomType_RunsItsTool()
    {
        var door = Door();
        Assert.Equal("set_opening_type", Edit(door, "type", "door.sliding").Tool);
        Assert.Equal("R", Edit(door, "hand", "R").Args["hand"]!.ToString());
        Assert.Equal("out", Edit(door, "swing", "out").Args["swing"]!.ToString());
        var room = Picked("room", ("forsk:room_id", "R01"));
        room.Marker = "marker-guid";
        var edit = Edit(room, "room_type", RoomTypes.Bathroom);
        Assert.Equal(("rooms_set_type", "marker-guid"), (edit.Tool, edit.Args["id"]!.ToString()));
    }

    [Fact]
    public void AWallEdit_IsSetWall()
    {
        var wall = OneRunWall();
        var thick = Edit(wall, "thickness", "150");
        Assert.Equal(("set_wall", wall.Id, 150.0), (thick.Tool, thick.Args["id"]!.ToString(), thick.Args["thickness_mm"]!.Value<double>()));
        Assert.Equal(3600.0, Edit(wall, "length", "3600").Args["length_mm"]!.Value<double>());
        // The height is the storey's: no id, every wall follows.
        var height = Edit(wall, "wall_height", "2500");
        Assert.Equal(("set_wall", null, 2500.0), (height.Tool, height.Args["id"], height.Args["height_mm"]!.Value<double>()));
        Assert.Null(ForskInfo.Edit(PanelOf(wall), wall.Id, "thickness", "700", out var error));
        Assert.Equal("A wall is above 0 and at most 600 mm thick.", error);
        Assert.Null(ForskInfo.Edit(PanelOf(wall), wall.Id, "thickness", "0", out _));
    }

    [Fact]
    public void ARoomsCeiling_IsTheWallsHeightAboveTheWallsFoot()
    {
        var room = Picked("room", ("forsk:room_id", "R01"), (ForskInfo.CeilingKey, "2500"), (ForskInfo.CeilingLiftKey, "200"));
        room.Marker = "marker-guid";
        var row = PanelOf(room).Rows.Single(r => r.Label == "Ceiling height");
        Assert.Equal(("2500", "ceiling", "mm"), (row.Value, row.Field, row.Unit));
        var edit = Edit(room, "ceiling", "2600");
        Assert.Equal(("set_wall", 2800.0), (edit.Tool, edit.Args["height_mm"]!.Value<double>()));
    }

    [Fact]
    public void AStairEdit_IsEditStair_AndRiseTakesAuto()
    {
        var stair = Picked("stair", (Stairs.RiserKey, "175"), (Stairs.RiseKey, "2800"), (Stairs.WidthKey, "900"));
        stair.Going = "250";
        Assert.Equal(170.0, Edit(stair, "riser_max", "170").Args["riser_max"]!.Value<double>());
        Assert.Equal(280.0, Edit(stair, "going", "280").Args["going"]!.Value<double>());
        Assert.Equal(1000.0, Edit(stair, "stair_width", "1000").Args["width"]!.Value<double>());
        Assert.Equal(2900.0, Edit(stair, "rise", "2900").Args["rise"]!.Value<double>());
        var auto = Edit(stair, "rise", "Auto");
        Assert.Equal(("edit_stair", "auto"), (auto.Tool, auto.Args["rise"]!.ToString()));
    }

    [Theory]
    [InlineData("180", 90.0)]
    [InlineData("0", -90.0)]
    [InlineData("315°", -135.0)]
    public void AFurnitureTurn_RotatesTheShortWayRound(string typed, double rotate)
    {
        var bed = Picked("furniture", (Furniture.CatalogKey, "sofa.3seat"), (ForskInfo.RotationKey, "90"));
        var edit = Edit(bed, "rotation", typed);
        Assert.Equal(("move_furniture", rotate), (edit.Tool, edit.Args["rotate"]!.Value<double>()));
        Assert.Null(ForskInfo.Edit(PanelOf(bed), bed.Id, "rotation", "90", out var error));
        Assert.Equal("That is how it stands already.", error);
    }

    [Fact]
    public void NonsenseOrSomethingNoLongerPicked_IsRefused()
    {
        var door = Door();
        var panel = PanelOf(door);
        Assert.Null(ForskInfo.Edit(panel, door.Id, "width", "wide", out var error));
        Assert.Equal("Type a size in millimetres, like 900.", error);
        Assert.Null(ForskInfo.Edit(panel, door.Id, "width", "0", out _));
        Assert.Null(ForskInfo.Edit(panel, door.Id, "type", "door.revolving", out _));
        Assert.Null(ForskInfo.Edit(panel, door.Id, "hand", "up", out _));
        Assert.Null(ForskInfo.Edit(panel, door.Id, "colour", "300", out error));
        Assert.Equal("That cannot be changed here.", error);
        Assert.Null(ForskInfo.Edit(panel, "another-guid", "width", "900", out error));
        Assert.Equal("Pick it again, then change it.", error);
        Assert.Null(ForskInfo.Edit(null, door.Id, "width", "900", out _));
        Assert.Null(ForskInfo.Edit(panel, "", "width", "900", out _));
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

    // ---- while Draw wall or Add stair runs: the thing being drawn ----

    [Fact]
    public void DrawingAWall_ShowsItsThicknessHeightAndAnchor()
    {
        var drawing = new ForskInfo.Drawing { Kind = "wall", Thickness = 200, Anchor = WallDraw.Anchor.Centre };
        var panel = ForskInfo.ForDrawing(drawing);
        Assert.Equal("Drawing a wall", panel.Title);
        Assert.Equal(ForskInfo.DrawingId, panel.Id);
        Assert.Equal("200", Value(panel, "Thickness"));
        Assert.Equal("thickness", panel.Rows.Single(r => r.Label == "Thickness").Field);
        Assert.Equal("", Value(panel, "Height"));
        var anchor = panel.Rows.Single(r => r.Label == "Anchor");
        Assert.Equal("Centre", anchor.Value);
        Assert.Equal(new[] { "Left", "Centre", "Right" }, anchor.Options.Select(o => o.Id));
    }

    [Fact]
    public void DrawingAStair_ShowsItsWidthRiseAndAnchor()
    {
        var drawing = new ForskInfo.Drawing { Kind = "stair", Width = 900, Rise = 3000, Anchor = WallDraw.Anchor.Left };
        var panel = ForskInfo.ForDrawing(drawing);
        Assert.Equal("Adding a stair", panel.Title);
        Assert.Equal("900", Value(panel, "Width"));
        Assert.Equal("3000 mm", Value(panel, "Total rise"));
        Assert.Equal("Left", Value(panel, "Anchor"));
    }

    [Fact]
    public void AnEditWhileDrawing_ChangesWhatIsBeingDrawn_NotTheModel()
    {
        var drawing = new ForskInfo.Drawing { Kind = "wall", Thickness = 200 };
        Assert.True(ForskInfo.EditDrawing(drawing, "thickness", "150", out var error), error);
        Assert.Equal(150, drawing.Thickness);
        Assert.True(ForskInfo.EditDrawing(drawing, "anchor", "Left", out error), error);
        Assert.Equal(WallDraw.Anchor.Left, drawing.Anchor);
        Assert.True(ForskInfo.EditDrawing(drawing, "height", "2700", out error), error);
        Assert.Equal(2700, drawing.Height);
        Assert.True(ForskInfo.EditDrawing(drawing, "height", "", out error), error);
        Assert.Equal(0, drawing.Height);
        Assert.False(ForskInfo.EditDrawing(drawing, "thickness", "0", out error));
        Assert.False(ForskInfo.EditDrawing(drawing, "anchor", "Middle", out error));
        var stair = new ForskInfo.Drawing { Kind = "stair", Width = 900 };
        Assert.False(ForskInfo.EditDrawing(stair, "width", "400", out error));
        Assert.True(ForskInfo.EditDrawing(stair, "width", "1000", out error), error);
        Assert.Equal(1000, stair.Width);
    }

    [Fact]
    public void WhileDrawing_TheWindowShowsTheDrawingPanel_OverWhatIsPicked()
    {
        var facts = Facts(Door());
        var drawing = new ForskInfo.Drawing { Kind = "wall", Thickness = 200 };
        var model = WindowView.Build(new DocThread { Serial = 1 }, facts, drawing: drawing);
        Assert.Equal("Drawing a wall", model["info"]?["title"]?.ToString());
        Assert.Equal("Door D02", WindowView.Build(new DocThread { Serial = 1 }, facts)["info"]?["title"]?.ToString());
    }
}
