using System.IO;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Generate 3D: the chat reply, the 2D layers it hides, and the perspective
/// colours it applies before the redraw.
/// </summary>
public class Gen3dPassTests
{
    static readonly string[] Office =
    {
        "Defaults: walls 3000, floor 400, roof 200. Origin (0,0,0) is the existing-building corner.",
        "floor_from_layer · ok · 1",
        "walls_from_layer · ok · 46",
        "roof_flat_from_walls · ok · 1",
        "openings_from_layer door · ok · 14",
        "openings_from_layer window · ok · 63",
        "rooms_from_layer · ok · 0 · No closed room curves. Layer 'A-ROOM' not found.",
        "rooms_detect · ok · 16"
    };

    [Fact]
    public void TheReply_IsOneLine_AndHidesTheFallback()
    {
        Assert.Equal(
            new[] { "Built 3D: 46 walls, 14 doors, 63 windows, 16 rooms. 2D layers hidden." },
            BakeReply.Format(Office, true));
        var text = string.Join("\n", BakeReply.Format(Office, true));
        Assert.DoesNotContain("Defaults", text);
        Assert.DoesNotContain("Origin", text);
        Assert.DoesNotContain("openings_from_layer", text);
        Assert.DoesNotContain("A-ROOM", text);
        Assert.DoesNotContain("Rooms 0", text);
        Assert.True(BakeReply.HasWalls(Office));
    }

    [Fact]
    public void TheReply_WarnsWhenNothingWasFound_AndKeepsARealNote()
    {
        var empty = new[]
        {
            "walls_from_layer · ok · 10",
            "openings_from_layer door · ok · 0",
            "openings_from_layer · skipped · no window layer",
            "rooms_from_layer · ok · 0 · No closed room curves. Layer 'A-ROOM' not found.",
            "rooms_detect · ok · 0"
        };
        Assert.Equal(new[] { "Built 3D: 10 walls.", "No openings.", "No rooms." }, BakeReply.Format(empty, false));

        var one = new[]
        {
            "walls_from_layer · ok · 1",
            "openings_from_layer door · ok · 1",
            "openings_from_layer window · ok · 1",
            "rooms_from_layer · ok · 1"
        };
        Assert.Equal(new[] { "Built 3D: 1 wall, 1 door, 1 window, 1 room." }, BakeReply.Format(one, false));

        var thick = new[] { "walls_from_layer · ok · 4 · Could not measure wall thickness." };
        Assert.Equal(
            new[] { "Built 3D: 4 walls.", "Could not measure wall thickness." },
            BakeReply.Format(thick, false));

        Assert.Equal(new[] { "Document units must be millimetres. Switch the .3dm to millimetres." },
            BakeReply.Format(new[] { "Document units must be millimetres. Switch the .3dm to millimetres." }, false));
        Assert.Equal(new[] { "Built 3D.", "No walls." },
            BakeReply.Format(new[] { "floor_from_layer · ok · 1", "walls_from_layer · ok · 0 · No curves on layer 'wall'." }, false));
    }

    [Fact]
    public void HiddenLayers_AreTheVisibleSourceLayers_AndTheRecordToggles()
    {
        var hide = PlanLayers.ToHide(new[]
        {
            new PlanLayers.LayerInfo { Path = "wall", Name = "wall", Visible = true },
            new PlanLayers.LayerInfo { Path = "door", Name = "door", Visible = true },
            new PlanLayers.LayerInfo { Path = "window", Name = "window", Visible = false },
            new PlanLayers.LayerInfo { Path = "label", Name = "label", Visible = true },
            new PlanLayers.LayerInfo { Path = "A-ROOM", Name = "A-ROOM", Visible = true },
            new PlanLayers.LayerInfo { Path = "A-ROOM::Plate", Name = "Plate", Visible = true },
            new PlanLayers.LayerInfo { Path = "A-WALL", Name = "A-WALL", Visible = true, HoldsModel = true },
            new PlanLayers.LayerInfo { Path = "A-FLOR", Name = "A-FLOR", Visible = true, HoldsModel = true },
            new PlanLayers.LayerInfo { Path = "room", Name = "room", Visible = true, HoldsModel = true },
            new PlanLayers.LayerInfo { Path = "X-EXIST", Name = "X-EXIST", Visible = true }
        });
        Assert.Equal(new[] { "wall", "door", "label" }, hide);
        Assert.False(PlanLayers.IsSource("A-ROOM"));
        Assert.False(PlanLayers.IsSource("A-WALL"));
        Assert.True(PlanLayers.IsSource("space_divider"));

        var stored = PlanLayers.Store(hide);
        Assert.Equal(hide, PlanLayers.Stored(stored));
        Assert.True(PlanLayers.AreHidden(stored, path => false));
        Assert.False(PlanLayers.AreHidden(stored, path => path == "door"));
        Assert.False(PlanLayers.AreHidden("", path => false));
        Assert.False(PlanLayers.AreHidden(stored, path => null));
    }

    [Fact]
    public void Show2D_IsTheSuggestionWhenTheLayersAreHidden()
    {
        var hidden = Docs.Facts("house, 2d hidden");
        Assert.True(hidden.PlanHidden);
        Assert.True(hidden.PlanRecall);
        Assert.Equal("plan.show", ForskRegistry.Bar(hidden).Context[0].Id);
        Assert.Equal("Show 2D", ForskRegistry.Find("plan.show").Label);
        Assert.Contains(ForskRegistry.Card(hidden).Actions, a => a.Id == "plan.show");
        Assert.DoesNotContain(ForskRegistry.Card(hidden).Actions, a => a.Id == "plan.hide");

        var shown = Docs.Facts("house, 2d shown");
        Assert.False(shown.PlanHidden);
        Assert.Equal("plan.hide", ForskRegistry.Bar(shown).Context[0].Id);
        Assert.Equal("Hide 2D", ForskRegistry.Find("plan.hide").Label);
        Assert.DoesNotContain(ForskRegistry.Bar(Docs.Facts("house")).Slots, a => a.Id == "plan.show" || a.Id == "plan.hide");
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("plan.show"));
    }

    [Fact]
    public void ASelectedFloor_DoesNotDriveTheRoomPick()
    {
        var marker = Row.Room();
        var plate = Row.Plate(marker, selected: true);
        var floor = Row.Floor();
        floor.Selected = true;
        var facts = FileClassifier.Read(Docs.Of(Row.Wall(), floor, marker, plate));
        Assert.Equal(Picked.Room, facts.Picked);
        Assert.Equal(1, facts.PickedCount);
        Assert.Equal(new[] { plate.Id }, facts.Selected.Select(r => r.Id).ToArray());
        Assert.DoesNotContain(floor.Id, facts.SelectionKey);
    }

    [Fact]
    public void Generate3D_StampsTypesAndRefreshesColoursBeforeTheRedraw()
    {
        var chat = Source("Forsk", "ForskChat.cs");
        var finish = chat.Substring(chat.IndexOf("static List<string> Finish", StringComparison.Ordinal));
        var apply = finish.IndexOf("ApplyRoomTypeColours", StringComparison.Ordinal);
        var hide = finish.IndexOf("PlanLayerHost.Hide", StringComparison.Ordinal);
        var redraw = finish.IndexOf("BakePace.Redraw", StringComparison.Ordinal);
        Assert.True(apply >= 0 && apply < redraw, "colours are applied before the redraw");
        Assert.True(hide > apply && hide < redraw, "2D layers hide before the redraw");
        Assert.Contains("BakeReply.Format", finish);

        var rooms = Source("Functions", "RoomsDetect.cs");
        var pass = rooms.Substring(rooms.IndexOf("void ApplyRoomTypeColours", StringComparison.Ordinal));
        Assert.True(pass.IndexOf("WriteRoomType", StringComparison.Ordinal) < pass.IndexOf("RoomTypeColorHost.Invalidate", StringComparison.Ordinal));
        Assert.Contains("MirrorRoomType", pass);

        var plates = Source("Functions", "RoomPlates.cs");
        Assert.True(plates.IndexOf("FindId(marker.Id)", StringComparison.Ordinal) >= 0);
        Assert.Contains("ApplyRoomTypeColours", plates);

        var floors = Source("Functions", "FloorFromLayer.cs");
        Assert.Contains("UnlockFloors(", floors);
        Assert.Contains("LockFloors(", floors);
        Assert.Contains("doc.Objects.Lock(", floors);
        Assert.Contains("candidate.IsLocked = locked", floors);
        Assert.Contains("UnlockFloors(", Source("Functions", "ClearGenerated.cs"));
        Assert.Contains("UnlockFloors(", Source("Functions", "WallFollow.cs"));
        Assert.Contains("LockFloors(", Source("Functions", "WallFollow.cs"));
    }

    static string Source(string folder, string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", folder, file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new DirectoryNotFoundException("plugin/" + folder + "/" + file);
    }
}
