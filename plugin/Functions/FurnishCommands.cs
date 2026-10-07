using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// FU.7: furnish_room. The room's outline, type, doors and windows and the
/// furniture already in it go to Furnish.Plan; its pieces are added as
/// add_furniture adds one. "all" furnishes every room of a type Forsk
/// furnishes. A room that cannot be furnished says why and the rest go on.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("furnish_room", ModelView = true)]
    public JObject FurnishRoom(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var density = (parameters?["density"]?.ToString() ?? Furnish.Relaxed).Trim().ToLowerInvariant();
        if (!Furnish.Densities.Contains(density)) throw new ArgumentException("density is spacious, relaxed or compact.");
        var variant = (parameters?["variant"]?.ToString() ?? Furnish.Consistent).Trim().ToLowerInvariant();
        if (variant != Furnish.Consistent && variant != Furnish.Creative) throw new ArgumentException("variant is consistent or creative.");
        var replace = parameters?["replace"]?.Type == JTokenType.Boolean && parameters["replace"].Value<bool>();

        var rooms = FurnitureRooms(doc);
        var named = parameters?["room"]?.ToString()?.Trim();
        List<PlanRoom> targets;
        if (string.Equals(named, "all", StringComparison.OrdinalIgnoreCase))
        {
            targets = rooms.Where(r => Furnish.Types.Contains(r.RoomType ?? "")).ToList();
            if (targets.Count == 0) throw new InvalidOperationException("No room has a type Forsk furnishes. Set the rooms' types first.");
        }
        else targets = new List<PlanRoom> { PickFurnitureRoom(doc, rooms, named, null) };

        var openings = FurnishOpenings(doc);
        var floor = StairFloorTop(doc);
        var results = new JArray();
        var lines = new List<string>();
        var total = 0;
        foreach (var room in targets)
        {
            if (replace)
                foreach (var obj in FurnitureObjects(doc).Where(o => InRoom(o, room)).ToList())
                    doc.Objects.Delete(obj.Id, true);
            var inRoom = FurnitureObjects(doc).Where(o => InRoom(o, room)).ToList();
            var layout = Furnish.Plan(room.RoomType ?? "", room.Outline, openings, FurnitureFootprints(doc, null),
                inRoom.Select(o => o.Attributes.GetUserString(Furniture.CatalogKey)).ToList(), density, variant);
            var added = new JArray();
            if (layout.Why == null)
                foreach (var item in layout.Items)
                {
                    var (id, forskId) = AddFurniturePiece(doc, item.Piece, item.Frame, room, floor);
                    added.Add(new JObject { ["id"] = id.ToString(), ["forsk_id"] = forskId, ["catalog_id"] = item.Piece.Id });
                }
            total += added.Count;
            var words = RoomWords(room);
            lines.Add(layout.Why != null
                ? "Did not furnish " + words + ": " + layout.Why + "."
                : layout.Items.Count == 0 ? words.Substring(0, 1).ToUpperInvariant() + words.Substring(1) + " already has its furniture."
                : Furnish.Receipt(words, layout));
            results.Add(new JObject
            {
                ["room"] = room.ScheduleId,
                ["type"] = room.RoomType ?? "",
                ["added"] = added,
                ["skipped"] = new JArray(layout.Skipped),
                ["why"] = layout.Why ?? ""
            });
        }
        doc.Views.Redraw();
        return new JObject
        {
            ["rooms"] = results,
            ["count"] = total,
            ["density"] = density,
            ["variant"] = variant,
            ["message"] = string.Join(" ", lines)
        };
    }

    /// <summary>A piece belongs to the room its record names, else to the room its centre stands in.</summary>
    private static bool InRoom(Rhino.DocObjects.RhinoObject obj, PlanRoom room)
    {
        var stamped = obj.Attributes.GetUserString(Furniture.RoomKey);
        if (!string.IsNullOrEmpty(stamped)) return string.Equals(stamped, room.ScheduleId, StringComparison.OrdinalIgnoreCase);
        return TryFurniture(obj, out var piece, out var frame, out _) && RoomDetect.Contains(room.Outline, Furniture.CentreOf(piece, frame));
    }

    /// <summary>Every door and window marker: its middle in plan and its width (the longer side of its box).</summary>
    private static List<Furnish.Opening> FurnishOpenings(RhinoDoc doc)
    {
        var list = new List<Furnish.Opening>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj) || !string.Equals(GetForskKind(obj), "opening_marker", StringComparison.OrdinalIgnoreCase)) continue;
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            var width = ParseMm(obj.Attributes.GetUserString("forsk:width")) ?? Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);
            list.Add(new Furnish.Opening
            {
                Centre = new Pt((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2),
                Width = width,
                Door = !string.Equals(obj.Attributes.GetUserString("forsk:opening_kind"), "window", StringComparison.OrdinalIgnoreCase)
            });
        }
        return list;
    }
}
