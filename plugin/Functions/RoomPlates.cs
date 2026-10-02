using System;
using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Selection S3: one floor plate per room the walls close, so a click inside
/// a room picks it. The one writer: every plate is rebuilt from the room
/// markers whenever they are (rooms_from_layer, rooms_detect, and through
/// them Generate and the wall edits), in the same undo record. A plate is
/// the marker's outline 20 mm up from the slab top (RoomPlate), on
/// A-ROOM::Plate, tagged with its room and its marker. The marker stays the
/// room record. Print, the layout clay and sections never draw a plate.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string RoomPlateLayerName = "Plate";

    /// <summary>
    /// Deletes every plate and adds one per marker whose room the walls close.
    /// The result lists the rooms that got none and why.
    /// </summary>
    private JObject RebuildRoomPlates(RhinoDoc doc)
    {
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        foreach (var obj in EnumerateDocObjects(doc))
            if (IsForskGenerated(obj) && string.Equals(GetForskKind(obj), RoomPlate.Kind, StringComparison.OrdinalIgnoreCase))
                doc.Objects.Delete(obj.Id, true);

        var plates = new JArray();
        var none = new JArray();
        var markers = RoomMarkers(doc);
        if (markers.Count == 0) return PlatesResult(plates, none);

        var walls = RoomDetect.Detect(RoomScene(doc, tol, new JArray(), out _));
        var layer = EnsureRoomPlateLayer(doc);
        foreach (var marker in markers)
        {
            var name = marker.Attributes.GetUserString(RoomNameKey);
            if (string.IsNullOrWhiteSpace(name)) name = marker.Name ?? "a room";
            var outline = RoomMarkerOutline(marker);
            var points = outline == null ? null : LoopPoints(outline, tol);
            var why = RoomPlate.Open(points == null ? null : PlanPoints(points), walls);
            Brep plate = null;
            if (why == null)
            {
                // The marker lies on the slab top: the plate stands on it.
                plate = ExtrudeClosedCurve(outline, RoomPlate.ThicknessMm, tol);
                if (plate == null) why = "its outline did not extrude";
            }
            if (why != null)
            {
                none.Add(new JObject { ["room"] = name, ["why"] = why });
                continue;
            }
            var attr = new ObjectAttributes { Name = name, LayerIndex = layer.Index, MaterialSource = ObjectMaterialSource.MaterialFromLayer };
            StampForskTags(attr, new ForskStamp { Kind = RoomPlate.Kind, Level = marker.Attributes.GetUserString("forsk:level") ?? "0" });
            foreach (var key in new[] { RoomIdKey, RoomNameKey, "forsk:area" })
            {
                var value = marker.Attributes.GetUserString(key);
                if (!string.IsNullOrEmpty(value)) attr.SetUserString(key, value);
            }
            attr.SetUserString("forsk:marker", marker.Id.ToString());
            var id = doc.Objects.AddBrep(plate, attr);
            if (id == Guid.Empty) none.Add(new JObject { ["room"] = name, ["why"] = "Rhino did not take its plate" });
            else plates.Add(id.ToString());
        }
        return PlatesResult(plates, none);
    }

    /// <summary>The plates rebuilt after the markers, into a rooms result: their ids, the rooms without one, and the note.</summary>
    private void AddRoomPlates(RhinoDoc doc, JObject result)
    {
        var plates = RebuildRoomPlates(doc);
        result["plate_ids"] = plates["plate_ids"];
        result["no_plate"] = plates["no_plate"];
        var note = plates["note"]?.ToString();
        if (!string.IsNullOrEmpty(note)) result["message"] = (result["message"]?.ToString() ?? "") + " " + note;
        LockPlatedRoomCurves(doc);
        doc.Views.Redraw();
    }

    /// <summary>
    /// The plate is the click. Its room curve, and any coincident copy still on
    /// A-ROOM, lock so one click cannot select two outlines. A room with no plate
    /// stays selectable. Locked curves still print.
    /// </summary>
    private void LockPlatedRoomCurves(RhinoDoc doc)
    {
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var rings = new HashSet<string>(StringComparer.Ordinal);
        var markerIds = new List<Guid>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), RoomPlate.Kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Guid.TryParse(obj.Attributes.GetUserString("forsk:marker"), out var markerId)) continue;
            var marker = doc.Objects.FindId(markerId);
            if (marker == null) continue;
            markerIds.Add(marker.Id);
            var outline = RoomMarkerOutline(marker);
            var points = outline == null ? null : LoopPoints(outline, tol);
            if (points == null || points.Count < 3) continue;
            var key = RoomCurves.RingKey(PlanPoints(points));
            if (key.Length > 0) rings.Add(key);
        }

        var roomLayer = ResolveRoomSourceLayer(doc, "A-ROOM");
        var lockIds = new HashSet<Guid>(markerIds);
        var unlockIds = new List<Guid>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null) continue;
            if (string.Equals(GetForskKind(obj), RoomPlate.Kind, StringComparison.OrdinalIgnoreCase)) continue;
            var sameRing = false;
            if (roomLayer != null && rings.Count > 0 && ObjectOnLayer(doc, obj, roomLayer) && obj.Geometry is Curve curve && curve.IsClosed)
            {
                var pts = LoopPoints(curve, tol);
                if (pts != null && pts.Count >= 3)
                    sameRing = rings.Contains(RoomCurves.RingKey(PlanPoints(pts)));
            }
            if (lockIds.Contains(obj.Id) || sameRing) lockIds.Add(obj.Id);
            else if (IsRoomRecord(obj)) unlockIds.Add(obj.Id);
        }
        foreach (var id in markerIds)
        {
            var marker = doc.Objects.FindId(id);
            if (marker == null || marker.IsLocked) continue;
            doc.Objects.Show(marker.Id, false);
            doc.Objects.Lock(marker.Id, false);
        }
        foreach (var id in lockIds)
        {
            if (markerIds.Contains(id)) continue;
            var obj = doc.Objects.FindId(id);
            if (obj == null || obj.IsLocked) continue;
            doc.Objects.Show(obj.Id, false);
            doc.Objects.Lock(obj.Id, false);
        }
        foreach (var id in unlockIds)
        {
            if (lockIds.Contains(id)) continue;
            var obj = doc.Objects.FindId(id);
            if (obj != null && obj.IsLocked) doc.Objects.Unlock(id, false);
        }
    }

    private static JObject PlatesResult(JArray plates, JArray none)
    {
        var parts = new List<string>();
        foreach (var item in none) parts.Add(item["room"] + " (" + item["why"] + ")");
        return new JObject
        {
            ["plate_ids"] = plates,
            ["no_plate"] = none,
            ["note"] = parts.Count == 0 ? "" : "No floor plate for " + string.Join(", ", parts) + "."
        };
    }

    /// <summary>A plate stands for its room: the marker it names, else the object itself.</summary>
    internal static RhinoObject ResolveRoomHandle(RhinoDoc doc, RhinoObject obj)
    {
        if (doc == null || obj?.Attributes == null) return obj;
        if (!string.Equals(GetForskKind(obj), RoomPlate.Kind, StringComparison.OrdinalIgnoreCase)) return obj;
        if (!Guid.TryParse(obj.Attributes.GetUserString("forsk:marker"), out var markerId)) return obj;
        return doc.Objects.FindId(markerId) ?? obj;
    }

    /// <summary>A-ROOM::Plate, visible, its colour and render material the floor layer's.</summary>
    private Layer EnsureRoomPlateLayer(RhinoDoc doc)
    {
        var parent = EnsureLayer(doc, "A-ROOM", Color.FromArgb(200, 180, 120));
        var floor = FindLayerCaseInsensitive(doc, "A-FLOR");
        var layer = FindLayerCaseInsensitive(doc, "A-ROOM::" + RoomPlateLayerName);
        if (layer == null)
        {
            var index = doc.Layers.Add(new Layer { Name = RoomPlateLayerName, ParentLayerId = parent.Id, IsVisible = true });
            if (index < 0) return parent;
            layer = doc.Layers.FindIndex(index);
            if (layer == null) return parent;
        }
        layer.IsVisible = true;
        layer.Color = floor?.Color ?? Color.FromArgb(150, 145, 138);
        if (floor != null) layer.RenderMaterialIndex = floor.RenderMaterialIndex;
        doc.Layers.Modify(layer, layer.Index, true);
        return layer;
    }
}
