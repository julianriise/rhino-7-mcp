using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// Selection S3: a click inside a room picks the room. Each room the walls
/// close gets a thin plate on the slab, kind room_plate, drawn from its
/// marker. A room the walls do not close gets none, and the receipt names it
/// and says why: that is also why daylight has no cells there.
/// </summary>
public class RoomPlateTests
{
    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    [Fact]
    public void ThePlate_Is20mm_UnderTheDaylightMesh_StandingOnTheMarker()
    {
        Assert.Equal(20.0, RoomPlate.ThicknessMm);
        // forsk_daylight paints its mesh FLOOR_OFFSET_MM = 50 mm above the room.
        Assert.True(RoomPlate.ThicknessMm < 50.0);
        // Extruded up from the marker's own curve: its underside is the marker's Z.
        Assert.Contains("ExtrudeClosedCurve(outline, RoomPlate.ThicknessMm", Source("RoomPlates.cs"));
    }

    [Fact]
    public void AClosedRoom_HasAPlate()
    {
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(new() { Rect(0, 0, 4200, 4200), Rect(200, 200, 4000, 4000) });
        var walls = RoomDetect.Detect(scene);
        Assert.Null(RoomPlate.Open(Rect(200, 200, 4000, 4000), walls));
    }

    [Fact]
    public void ARoomTheWallsDoNotClose_HasNoPlate_AndSaysWhy()
    {
        // The bottom wall has a 900 gap at x 1500..2400 to the outside, no door.
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(new() { Rect(0, 0, 1500, 200) });
        scene.Walls.Add(new() { Rect(2400, 0, 4200, 200) });
        scene.Walls.Add(new() { Rect(0, 0, 200, 4200) });
        scene.Walls.Add(new() { Rect(4000, 0, 4200, 4200) });
        scene.Walls.Add(new() { Rect(0, 4000, 4200, 4200) });
        var walls = RoomDetect.Detect(scene);
        // An outline drawn on A-ROOM over it is a marker, but no plate.
        Assert.Equal("gap 0.9 m without a door", RoomPlate.Open(Rect(200, 200, 4000, 4000), walls));
        // A marker with no walls around it at all.
        Assert.Equal("its walls do not close", RoomPlate.Open(Rect(10000, 0, 12000, 2000), walls));
    }

    [Fact]
    public void TheRoomsReceipt_NamesEachOpenRoom_AndWhy()
    {
        var scene = new RoomDetect.Scene();
        scene.Walls.Add(new() { Rect(0, 0, 8200, 200) });
        scene.Walls.Add(new() { Rect(0, 4000, 8200, 4200) });
        scene.Walls.Add(new() { Rect(0, 0, 200, 4200) });
        scene.Walls.Add(new() { Rect(8000, 0, 8200, 4200) });
        scene.Walls.Add(new() { Rect(4000, 200, 4200, 1500) });
        scene.Walls.Add(new() { Rect(4000, 2400, 4200, 4000) });
        var found = RoomDetect.Detect(scene);
        var labels = new List<RoomDetect.Label>
        {
            new("Bod", 250, new Pt(2000, 2000)),
            new("Hall", 250, new Pt(6000, 2000))
        };
        Assert.Equal("0 rooms, 0.0 m². 2 open: Bod, Hall (gap 0.9 m without a door).",
            RoomDetect.Message(0, 0.0, found.Open, labels));
        Assert.Equal(new[] { "Bod", "Hall" }, found.Open.Select(o => RoomDetect.Name(labels, o.Ring)).OrderBy(n => n));
        Assert.Equal("3 rooms, 42.0 m².", RoomDetect.Message(3, 42.0, new List<RoomDetect.Open>(), labels));
    }

    [Fact]
    public void APlateSelected_IsOneRoom_AndAPlateWithItsMarkerIsStillOne()
    {
        var marker = Row.Room();
        var plate = Row.Plate(marker, selected: true);
        var f = FileClassifier.Read(Docs.Of(Row.Wall(), Row.Floor(), marker, plate));
        Assert.Equal(Picked.Room, f.Picked);
        Assert.Equal(1, f.PickedCount);

        marker.Selected = true;
        f = FileClassifier.Read(Docs.Of(Row.Wall(), Row.Floor(), marker, plate));
        Assert.Equal(Picked.Room, f.Picked);
        Assert.Equal(1, f.PickedCount);
        // Plates never count as rooms of their own.
        Assert.Single(f.Rooms);
    }

    [Fact]
    public void APlateSelected_GivesTheRoomBar()
    {
        var marker = Row.Room();
        var f = FileClassifier.Read(Docs.Of(Row.Wall(), Row.Floor(), Row.Window(), marker, Row.Plate(marker, selected: true)));
        var card = ForskRegistry.Card(f).Actions.Select(a => a.Id).ToList();
        Assert.Contains("daylight.room", card);
        Assert.Contains("section.room", card);
    }

    [Fact]
    public void PrintAndSections_NeverSeeAPlate_ClearGeneratedRemovesIt()
    {
        var make2d = Source("Make2dView.cs");
        var layout = Source("LayoutPack.cs");
        var section = Source("SectionSheet.cs");
        foreach (var source in new[] { make2d, layout, section })
            Assert.DoesNotContain("room_plate", source);
        Assert.Contains("\"room_plate\"", Source("ClearGenerated.cs"));
        // The one writer is RoomPlates.cs; the marker code adds no surface (RoomMarkerTests).
        Assert.Contains("AddRoomPlates(", Source("RoomsFromLayer.cs"));
        Assert.Contains("AddRoomPlates(", Source("RoomsDetect.cs"));
    }

    static string Source(string file) => File.ReadAllText(Path.Combine(FunctionsDir(), file));

    static string FunctionsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Functions");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("plugin/Functions above " + AppContext.BaseDirectory);
    }
}
