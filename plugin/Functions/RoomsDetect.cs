using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Rooms from the model (F2.5): the regions between the Forsk walls, closed at
/// doors and split by space_divider curves, drawn as closed curves on A-ROOM.
/// Outlines already on A-ROOM win; detection fills the rest. Detected outlines
/// carry forsk:room_id and keep it across runs. Then the room markers are
/// rebuilt from every closed curve on A-ROOM, as rooms_from_layer does.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string RoomSourceKey = "forsk:room_source";
    private const string RoomIdKey = "forsk:room_id";
    private const string DetectedRoomSource = "detected";
    private const string DetectedRoomPrefix = "rd-";
    private const string DividerLayerName = "space_divider";
    private const string RoomLabelLayerName = "label";

    [McpCommand("rooms_detect")]
    public JObject RoomsDetect(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var scene = new RoomDetect.Scene { Tol = Math.Max(tol, 1.0) };
        var warnings = new JArray();
        var z = double.MaxValue;

        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj) || IsExistingUnderlay(doc, obj)) continue;
            var kind = GetForskKind(obj) ?? "";
            if (kind.Equals("wall", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryDecodeWallPath(obj.Attributes.GetUserString("forsk:path"), out var outer, out var holes))
                {
                    warnings.Add($"Wall {obj.Attributes.GetUserString("forsk:id") ?? obj.Id.ToString()} has no param path. Bake the wall again.");
                    continue;
                }
                var rings = new List<List<RoomDetect.Pt>>();
                foreach (var loop in new[] { outer }.Concat(holes))
                {
                    var pts = LoopPoints(loop, tol);
                    if (pts == null || pts.Count < 3) continue;
                    foreach (var p in pts) z = Math.Min(z, p.Z);
                    rings.Add(PlanPoints(pts));
                }
                if (rings.Count > 0) scene.Walls.Add(rings);
            }
            else if (kind.Equals("opening_marker", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(obj.Attributes.GetUserString("forsk:opening_kind"), "window", StringComparison.OrdinalIgnoreCase))
            {
                var box = obj.Geometry.GetBoundingBox(true);
                if (box.IsValid) scene.Doors.Add(new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y));
            }
        }

        var roomLayer = ResolveRoomSourceLayer(doc, "A-ROOM");
        var earlier = new List<RhinoObject>();
        var earlierRings = new List<KeyValuePair<string, List<RoomDetect.Pt>>>();
        if (roomLayer != null)
        {
            foreach (var obj in EnumerateDocObjects(doc))
            {
                if (!ObjectOnLayer(doc, obj, roomLayer) || !(obj.Geometry is Curve curve) || !curve.IsClosed) continue;
                var pts = LoopPoints(FlattenToWorldXY(curve, tol), tol);
                if (pts == null || pts.Count < 3) continue;
                if (string.Equals(obj.Attributes.GetUserString(RoomSourceKey), DetectedRoomSource, StringComparison.Ordinal))
                {
                    earlier.Add(obj);
                    earlierRings.Add(new KeyValuePair<string, List<RoomDetect.Pt>>(
                        obj.Attributes.GetUserString(RoomIdKey), PlanPoints(pts)));
                }
                else
                {
                    scene.Keep.Add(PlanPoints(pts));
                }
            }
        }

        var dividerLayer = FindLayerCaseInsensitive(doc, DividerLayerName);
        if (dividerLayer != null)
        {
            foreach (var obj in EnumerateDocObjects(doc))
            {
                if (!ObjectOnLayer(doc, obj, dividerLayer) || !(obj.Geometry is Curve curve)) continue;
                var pts = PathPoints(curve);
                if (pts.Count >= 2) scene.Dividers.Add(pts);
            }
        }

        if (scene.Walls.Count == 0 && scene.Keep.Count == 0)
        {
            return new JObject
            {
                ["ids"] = new JArray(),
                ["rooms"] = new JArray(),
                ["count"] = 0,
                ["detected"] = 0,
                ["area_m2"] = 0.0,
                ["open"] = new JArray(),
                ["warnings"] = warnings,
                ["message"] = "No Forsk walls to find rooms in. Generate the 3D model first, or draw closed room outlines on A-ROOM."
            };
        }

        var found = RoomDetect.Detect(scene);
        var roomIds = RoomDetect.Match(found.Rooms, earlierRings, DetectedRoomPrefix);
        var layer = EnsureLayer(doc, roomLayer?.Name ?? "A-ROOM", Color.FromArgb(200, 180, 120));
        if (z == double.MaxValue) z = 0;

        var reused = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < found.Rooms.Count; i++)
        {
            var room = found.Rooms[i];
            var outline = RoomOutline(room.Ring, z);
            var old = earlier.Find(o => string.Equals(o.Attributes.GetUserString(RoomIdKey), roomIds[i], StringComparison.Ordinal));
            if (old != null && doc.Objects.Replace(old.Id, outline))
            {
                reused.Add(roomIds[i]);
                var kept = old.Attributes.Duplicate();
                kept.SetUserString("forsk:area", FormatMm(room.Area));
                doc.Objects.ModifyAttributes(old.Id, kept, true);
                continue;
            }
            var attr = new ObjectAttributes { Name = roomIds[i], LayerIndex = layer.Index };
            attr.SetUserString(RoomSourceKey, DetectedRoomSource);
            attr.SetUserString(RoomIdKey, roomIds[i]);
            attr.SetUserString("forsk:area", FormatMm(room.Area));
            if (doc.Objects.AddCurve(outline, attr) == Guid.Empty)
                warnings.Add($"Room outline {roomIds[i]} was not added.");
        }
        var removed = 0;
        foreach (var old in earlier)
        {
            if (!reused.Contains(old.Attributes.GetUserString(RoomIdKey) ?? "") && doc.Objects.Delete(old.Id, true))
                removed++;
        }

        // Markers follow the outlines: one per closed A-ROOM curve, detected ones named by their id.
        foreach (var marker in RoomMarkers(doc))
            doc.Objects.Delete(marker.Id, true);
        var markers = RoomsFromLayer(new JObject { ["layer"] = layer.Name });
        var ids = markers["ids"] as JArray ?? new JArray();
        var area = 0.0;
        foreach (var token in ids)
        {
            var marker = doc.Objects.FindId(Guid.Parse(token.ToString()));
            if (marker == null) continue;
            area += ParseMm(marker.Attributes.GetUserString("forsk:area")) ?? 0;
            NameDetectedMarker(doc, marker, found.Rooms, roomIds);
        }
        foreach (var warning in markers["warnings"] as JArray ?? new JArray())
            warnings.Add(warning);

        var labels = RoomLabels(doc);
        var detected = new JArray();
        for (var i = 0; i < found.Rooms.Count; i++)
        {
            var room = found.Rooms[i];
            detected.Add(new JObject
            {
                ["id"] = roomIds[i],
                ["name"] = RoomDetect.Name(labels, room.Ring),
                ["area_m2"] = Math.Round(room.Area / 1000000.0, 2, MidpointRounding.AwayFromZero),
                ["x"] = room.Inside.X,
                ["y"] = room.Inside.Y
            });
        }
        var open = new JArray();
        foreach (var region in found.Open)
            open.Add(new JObject { ["reason"] = region.Reason, ["x"] = region.At.X, ["y"] = region.At.Y });

        doc.Views.Redraw();
        var squareMetres = Math.Round(area / 1000000.0, 1, MidpointRounding.AwayFromZero);
        return new JObject
        {
            ["ids"] = ids,
            ["rooms"] = detected,
            ["count"] = ids.Count,
            ["detected"] = found.Rooms.Count,
            ["kept"] = found.Kept,
            ["removed"] = removed,
            ["slivers"] = found.Slivers,
            ["area_m2"] = squareMetres,
            ["open"] = open,
            ["layer"] = layer.Name,
            ["warnings"] = warnings,
            ["message"] = RoomsMessage(ids.Count, squareMetres, found.Open)
        };
    }

    private static string RoomsMessage(int count, double squareMetres, List<RoomDetect.Open> open)
    {
        var text = count + " room" + (count == 1 ? "" : "s") + ", "
            + squareMetres.ToString("0.0", CultureInfo.InvariantCulture) + " m²";
        if (open.Count == 0) return text + ".";
        var reasons = new List<string>();
        foreach (var region in open)
            if (!reasons.Contains(region.Reason)) reasons.Add(region.Reason);
        return text + ". " + open.Count + " open: " + string.Join("; ", reasons) + ".";
    }

    private static List<RhinoObject> RoomMarkers(RhinoDoc doc)
    {
        var list = new List<RhinoObject>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (IsForskGenerated(obj) && string.Equals(GetForskKind(obj), "room", StringComparison.OrdinalIgnoreCase))
                list.Add(obj);
        }
        return list;
    }

    /// <summary>The texts on the label layer that name rooms, at their insertion points.</summary>
    private static List<RoomDetect.Label> RoomLabels(RhinoDoc doc)
    {
        var labels = new List<RoomDetect.Label>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!(obj.Geometry is TextEntity text)) continue;
            var index = obj.Attributes.LayerIndex;
            if (index < 0 || index >= doc.Layers.Count) continue;
            if (!doc.Layers[index].Name.Equals(RoomLabelLayerName, StringComparison.OrdinalIgnoreCase)) continue;
            labels.Add(new RoomDetect.Label(text.PlainText, text.TextHeight,
                new RoomDetect.Pt(text.Plane.Origin.X, text.Plane.Origin.Y)));
        }
        return labels;
    }

    /// <summary>A marker made from a detected outline takes its stable id as forsk:id and name.</summary>
    private static void NameDetectedMarker(RhinoDoc doc, RhinoObject marker, List<RoomDetect.Room> rooms, string[] roomIds)
    {
        if (!TryRoomPolygon(marker, out var polygon)) return;
        if (!RoomDetect.TryInside(new List<List<RoomDetect.Pt>> { PlanPoints(polygon) }, out var at)) return;
        for (var i = 0; i < rooms.Count; i++)
        {
            if (!RoomDetect.Contains(rooms[i].Ring, at)) continue;
            var attr = marker.Attributes.Duplicate();
            attr.Name = roomIds[i];
            attr.SetUserString("forsk:id", roomIds[i]);
            attr.SetUserString(RoomIdKey, roomIds[i]);
            doc.Objects.ModifyAttributes(marker.Id, attr, true);
            return;
        }
    }

    private static PolylineCurve RoomOutline(List<RoomDetect.Pt> ring, double z)
    {
        var points = new List<Point3d>(ring.Count + 1);
        foreach (var p in ring) points.Add(new Point3d(p.X, p.Y, z));
        points.Add(points[0]);
        return new PolylineCurve(points);
    }

    private static List<RoomDetect.Pt> PlanPoints(List<Point3d> points)
    {
        var ring = new List<RoomDetect.Pt>(points.Count);
        foreach (var p in points) ring.Add(new RoomDetect.Pt(p.X, p.Y));
        return ring;
    }

    /// <summary>Divider vertices: the polyline itself, or 250 mm steps along a curve.</summary>
    private static List<RoomDetect.Pt> PathPoints(Curve curve)
    {
        var pts = new List<RoomDetect.Pt>();
        if (curve.TryGetPolyline(out Polyline polyline) && polyline != null && polyline.Count >= 2)
        {
            foreach (var p in polyline) pts.Add(new RoomDetect.Pt(p.X, p.Y));
            return pts;
        }
        var count = Math.Min(Math.Max((int)Math.Ceiling(curve.GetLength() / 250.0), 2), 400);
        var ts = curve.DivideByCount(count, true);
        if (ts == null) return pts;
        foreach (var t in ts)
        {
            var p = curve.PointAt(t);
            pts.Add(new RoomDetect.Pt(p.X, p.Y));
        }
        if (curve.IsClosed && pts.Count > 0) pts.Add(pts[0]);
        return pts;
    }
}
