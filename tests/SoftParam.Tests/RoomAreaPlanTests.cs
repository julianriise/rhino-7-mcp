using System;
using System.Collections.Generic;
using System.IO;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// The area tool's outline from clicked corners, and a drawn room a new wall
/// cuts in two. Plain points, no document.
/// </summary>
public class RoomAreaPlanTests
{
    static List<Pt> Pts(params double[] xy)
    {
        var list = new List<Pt>();
        for (var i = 0; i + 1 < xy.Length; i += 2) list.Add(new Pt(xy[i], xy[i + 1]));
        return list;
    }

    [Fact]
    public void ClickedCorners_CloseCounterclockwise_CountingRepeatsOnce()
    {
        // Clockwise, the first corner clicked again to close, one corner clicked twice, one on a straight edge.
        var clicks = Pts(0, 0, 0, 3000, 0, 3000, 4000, 3000, 4000, 1500, 4000, 0, 0, 0);
        Assert.True(RoomAreaPlan.TryRing(clicks, 1.0, out var ring, out var why));
        Assert.Null(why);
        Assert.Equal(4, ring.Count);
        Assert.Equal(12000000.0, RoomDetect.Area(ring), 3);
    }

    [Fact]
    public void TooFew_TooSmall_AndCrossing_AreRefused()
    {
        Assert.False(RoomAreaPlan.TryRing(Pts(0, 0, 1000, 0), 1.0, out _, out var why));
        Assert.Equal(RoomAreaPlan.TooFew, why);
        Assert.False(RoomAreaPlan.TryRing(Pts(0, 0, 500, 0, 500, 500, 0, 500), 1.0, out _, out why));
        Assert.Equal(RoomAreaPlan.TooSmall, why);
        Assert.False(RoomAreaPlan.TryRing(Pts(0, 0, 4000, 3000, 4000, 0, 0, 3000), 1.0, out _, out why));
        Assert.Equal(RoomAreaPlan.Crossing, why);
    }

    [Fact]
    public void AWallAcrossADrawnRoom_SplitsItInTwo()
    {
        var room = Pts(0, 0, 4000, 0, 4000, 3000, 0, 3000);
        var pieces = RoomAreaPlan.Split(room, new Pt(2500, 0), new Pt(2500, 3000), 100, 1.0);
        Assert.NotNull(pieces);
        Assert.Equal(2, pieces!.Count);
        Assert.Equal(2450.0 * 3000, RoomDetect.Area(pieces[0]), 0);
        Assert.Equal(1450.0 * 3000, RoomDetect.Area(pieces[1]), 0);

        // The wall stops on wall faces 50 mm outside the drawn outline: it still cuts the room through.
        Assert.Equal(2, RoomAreaPlan.Split(room, new Pt(2000, 50), new Pt(2000, 2950), 100, 1.0)!.Count);
    }

    [Fact]
    public void AStubWall_LeavesTheRoomWhole()
    {
        var room = Pts(0, 0, 4000, 0, 4000, 3000, 0, 3000);
        Assert.Null(RoomAreaPlan.Split(room, new Pt(2000, 0), new Pt(2000, 1500), 100, 1.0));
        // A wall outside the room does not touch it.
        Assert.Null(RoomAreaPlan.Split(room, new Pt(6000, 0), new Pt(6000, 3000), 100, 1.0));
        Assert.Equal(" Split 1 room in two.", RoomAreaPlan.SplitClause(1));
        Assert.Equal("", RoomAreaPlan.SplitClause(0));
    }

    [Fact]
    public void DrawArea_ShowsTheWallsOnly()
    {
        // Julian 2026-10-09: doors, windows, furniture, roof and floor leave the view while the user draws.
        foreach (var kind in new[] { "door", "window", "opening", "opening_marker", "furniture", "roof", "floor", "room", "room_plate", "stair" })
            Assert.True(RoomAreaPlan.HiddenWhileDrawing("1", kind), kind);
        Assert.False(RoomAreaPlan.HiddenWhileDrawing("1", "wall"));
        Assert.False(RoomAreaPlan.HiddenWhileDrawing("1", "Existing"));
        // The user's own curves and the imported plan are not Forsk's.
        Assert.False(RoomAreaPlan.HiddenWhileDrawing(null, null));
        Assert.False(RoomAreaPlan.HiddenWhileDrawing(null, "door"));
    }

    [Fact]
    public void DrawArea_HidesByCulling_AndAlwaysShowsEverythingAgain()
    {
        var tool = Source("Forsk", "ForskDrawArea.cs");
        // One display pass: no layer, hidden flag or undo record changes.
        Assert.Contains("override void ObjectCulling(", tool);
        Assert.DoesNotContain("Layers.Modify", tool);
        Assert.DoesNotContain("Objects.Hide", tool);
        // Esc, an error or a finished area all switch it off again.
        var end = tool.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(end > 0);
        Assert.Contains("focus.Enabled = false;", tool.Substring(end));
        // Forsk's own overlays (the hidden roof, the room colours) stay off while it runs.
        Assert.Contains("ForskDrawArea.WallsOnly", Source("Functions", "ForskInteriorHost.cs"));
        Assert.Contains("ForskDrawArea.WallsOnly", Source("Functions", "RoomTypeColorHost.cs"));
    }

    [Fact]
    public void DrawArea_TakesAClosedCurveTheUserDrew()
    {
        var tool = Source("Forsk", "ForskDrawArea.cs");
        // Picked first (pick-then-act), or with the Curve option at the first corner.
        Assert.Contains("PickedCurve(doc)", tool);
        Assert.Contains("AddOption(\"Curve\")", tool);
        // Only the user's own closed curves, and the curve becomes the area in the same record.
        Assert.Contains("curve.IsClosed", tool);
        Assert.Contains("\"forsk:generated\") != \"1\"", tool);
        Assert.Contains("doc.Objects.Delete(sourceId, true)", tool);
    }

    static string Source(string folder, string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", folder, file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("plugin/" + folder + "/" + file);
    }

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) };

    [Fact]
    public void AnOutlineOnTheCentreline_GoesToTheInnerFaces()
    {
        // Julian 2026-10-09: a room is never drawn in the middle of a wall. Four 200 walls
        // round a 3800 x 3800 room; the outline was clicked on their centrelines.
        var walls = new List<List<List<Pt>>>
        {
            new() { Rect(0, 0, 4200, 200) }, new() { Rect(0, 4000, 4200, 4200) },
            new() { Rect(0, 0, 200, 4200) }, new() { Rect(4000, 0, 4200, 4200) }
        };
        var inside = RoomDetect.InsideWalls(Rect(100, 100, 4100, 4100), walls, 1.0);
        Assert.NotNull(inside);
        Assert.Equal(3800.0 * 3800.0, RoomDetect.Area(inside), 0);
        // On the outer faces too, and already on the inner faces it stays as drawn.
        Assert.Equal(3800.0 * 3800.0, RoomDetect.Area(RoomDetect.InsideWalls(Rect(0, 0, 4200, 4200), walls, 1.0)), 0);
        Assert.Equal(3800.0 * 3800.0, RoomDetect.Area(RoomDetect.InsideWalls(Rect(200, 200, 4000, 4000), walls, 1.0)), 0);
    }

    [Fact]
    public void AWallOutlineWithTheRoomAsAHole_StillLeavesTheRoom()
    {
        // One wall record around the room: its outline has the room as a hole.
        var walls = new List<List<List<Pt>>> { new() { Rect(0, 0, 4200, 4200), Rect(200, 4000, 4000, 200) } };
        var inside = RoomDetect.InsideWalls(Rect(100, 100, 4100, 4100), walls, 1.0);
        Assert.Equal(3800.0 * 3800.0, RoomDetect.Area(inside), 0);
        // Wholly inside the wall: nothing is left.
        Assert.Null(RoomDetect.InsideWalls(Rect(20, 20, 180, 4000), walls, 1.0));
        // No walls: the outline as drawn.
        Assert.Equal(16.0e6, RoomDetect.Area(RoomDetect.InsideWalls(Rect(0, 0, 4000, 4000), new List<List<List<Pt>>>(), 1.0)), 0);
    }

    [Fact]
    public void EveryRoomOutlinePath_FollowsTheInnerFaces()
    {
        // Draw area, the outlines on A-ROOM (drawn, AI detection's rooms) and rooms_from_layer.
        Assert.Contains("RoomDetect.InsideWalls(", Source("Functions", "RoomArea.cs"));
        Assert.Contains("RoomDetect.InsideWalls(", Source("Functions", "RoomsDetect.cs"));
        Assert.Contains("RoomDetect.InsideWalls(", Source("Functions", "RoomsFromLayer.cs"));
    }
}
