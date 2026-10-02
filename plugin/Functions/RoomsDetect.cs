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
    private const string RoomNameKey = "forsk:room_name";
    private const string RoomAtKey = "forsk:room_at";
    private const string DetectedRoomSource = "detected";
    private const string DetectedRoomPrefix = "rd-";
    private const string DividerLayerName = "space_divider";
    private const string RoomLabelLayerName = "label";

    [McpCommand("rooms_detect", ModelView = true)]
    public JObject RoomsDetect(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var warnings = new JArray();
        var scene = RoomScene(doc, tol, warnings, out var z);

        var roomLayer = ResolveRoomSourceLayer(doc, "A-ROOM");
        var earlier = new List<RhinoObject>();
        var earlierRings = new List<KeyValuePair<string, List<RoomDetect.Pt>>>();
        if (roomLayer != null)
        {
            foreach (var obj in EnumerateDocObjects(doc))
            {
                if (!ObjectOnLayer(doc, obj, roomLayer) || IsRoomMarker(obj) || !(obj.Geometry is Curve curve) || !curve.IsClosed) continue;
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
        // Each detected room is named once; the marker carries the name to the plan tag.
        var suspect = new List<RoomDetect.Label>();
        var labels = RoomLabels(doc, suspect);
        var names = found.Rooms.Select(room => RoomDetect.Name(labels, room.Ring)).ToArray();

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

        // Markers follow the outlines: one per closed A-ROOM curve. Each is
        // stamped with its room's record, and the same records are the result,
        // so the plan tags show what is reported here.
        foreach (var marker in RoomMarkers(doc))
            doc.Objects.Delete(marker.Id, true);
        var markers = BakeRoomMarkers(new JObject { ["layer"] = layer.Name });
        var ids = markers["ids"] as JArray ?? new JArray();
        var tags = new List<RoomDetect.Tag>();
        var area = 0.0;
        foreach (var token in ids)
        {
            var marker = doc.Objects.FindId(Guid.Parse(token.ToString()));
            if (marker == null) continue;
            var tag = StampRoomMarker(doc, marker, found.Rooms, roomIds, names, labels);
            if (tag != null) tags.Add(tag);
            area += tag?.Area ?? ParseMm(marker.Attributes.GetUserString("forsk:area")) ?? 0;
        }
        foreach (var warning in markers["warnings"] as JArray ?? new JArray())
            warnings.Add(warning);

        // Detected rooms first, in the order they were found, then the outlines drawn by hand.
        var rooms = new JArray();
        foreach (var tag in tags.OrderBy(t => t.Detected ? Array.IndexOf(roomIds, t.Id) : int.MaxValue))
        {
            rooms.Add(new JObject
            {
                ["id"] = tag.Id,
                ["name"] = tag.Name,
                ["area_m2"] = Math.Round(tag.Area / 1000000.0, 2, MidpointRounding.AwayFromZero),
                ["x"] = tag.At.X,
                ["y"] = tag.At.Y,
                ["source"] = tag.Detected ? DetectedRoomSource : "drawn"
            });
        }
        // A region the walls do not close has no room: no marker, no plate, no daylight cells.
        var open = new JArray();
        foreach (var region in found.Open)
            open.Add(new JObject
            {
                ["name"] = region.Ring == null ? "" : RoomDetect.Name(labels, region.Ring),
                ["reason"] = region.Reason,
                ["x"] = region.At.X,
                ["y"] = region.At.Y
            });

        // Labels not imported by dxf_import that look mangled: which room each names.
        var suspects = new JArray();
        var suspectRooms = new List<string>();
        foreach (var label in suspect)
        {
            var at = found.Rooms.FindIndex(r => RoomDetect.Contains(r.Ring, label.At));
            var roomId = at >= 0 ? roomIds[at] : "";
            suspects.Add(new JObject { ["text"] = label.Text, ["room_id"] = roomId, ["x"] = label.At.X, ["y"] = label.At.Y });
            if (roomId.Length > 0 && !suspectRooms.Contains(roomId)) suspectRooms.Add(roomId);
        }

        doc.Views.Redraw();
        var squareMetres = Math.Round(area / 1000000.0, 1, MidpointRounding.AwayFromZero);
        var message = RoomDetect.Message(ids.Count, squareMetres, found.Open, labels);
        if (suspect.Count > 0)
            message += " Labels suspect " + suspect.Count
                + (suspectRooms.Count > 0 ? " (" + string.Join(" ", suspectRooms) + ")" : "")
                + ": a DXF escape lost on import. Import the DXF with dxf_import.";
        var result = new JObject
        {
            ["ids"] = ids,
            ["rooms"] = rooms,
            ["count"] = ids.Count,
            ["detected"] = found.Rooms.Count,
            ["kept"] = found.Kept,
            ["removed"] = removed,
            ["slivers"] = found.Slivers,
            ["area_m2"] = squareMetres,
            ["open"] = open,
            ["labels_suspect"] = suspects,
            ["layer"] = layer.Name,
            ["warnings"] = warnings,
            ["message"] = message
        };
        AddRoomPlates(doc, result);
        return result;
    }

    /// <summary>
    /// What rooms are found in: the Forsk walls' footprints, the doors that
    /// close a gap, and the space dividers. Z is the lowest wall loop, or 0.
    /// </summary>
    private RoomDetect.Scene RoomScene(RhinoDoc doc, double tol, JArray warnings, out double z)
    {
        var scene = new RoomDetect.Scene { Tol = Math.Max(tol, 1.0) };
        z = double.MaxValue;
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
        if (z == double.MaxValue) z = 0;

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
        return scene;
    }

    private static List<RhinoObject> RoomMarkers(RhinoDoc doc)
    {
        var list = new List<RhinoObject>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (IsRoomMarker(obj)) list.Add(obj);
        }
        return list;
    }

    /// <summary>
    /// A room marker: the closed curve rooms_from_layer adds on A-ROOM. It is
    /// not a room outline, so the readers of A-ROOM outlines skip it.
    /// </summary>
    private static bool IsRoomMarker(RhinoObject obj)
    {
        return IsForskGenerated(obj) && string.Equals(GetForskKind(obj), "room", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A marker's outline. Markers are curves; a Brep marker from a file made
    /// before F2.6 gives its face's outer loop. Null when there is none.
    /// </summary>
    private static Curve RoomMarkerOutline(RhinoObject marker)
    {
        if (marker?.Geometry is Curve curve) return curve.IsClosed ? curve.DuplicateCurve() : null;
        if (!(marker?.Geometry is Brep brep) || brep.Faces.Count == 0) return null;
        try { return brep.Faces[0].OuterLoop?.To3dCurve(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The texts on the label layer that name rooms, at their insertion points,
    /// as stored: dxf_import has already decoded what it imported. A label it
    /// did not import that looks like a mangled \U+ escape goes in
    /// <paramref name="suspect"/> as is; it is reported, not guessed at.
    /// </summary>
    private static List<RoomDetect.Label> RoomLabels(RhinoDoc doc, List<RoomDetect.Label> suspect = null)
    {
        var labels = new List<RoomDetect.Label>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!(obj.Geometry is TextEntity text)) continue;
            var index = obj.Attributes.LayerIndex;
            if (index < 0 || index >= doc.Layers.Count) continue;
            if (!doc.Layers[index].Name.Equals(RoomLabelLayerName, StringComparison.OrdinalIgnoreCase)) continue;
            var label = new RoomDetect.Label(text.PlainText, text.TextHeight,
                new RoomDetect.Pt(text.Plane.Origin.X, text.Plane.Origin.Y));
            labels.Add(label);
            if (suspect != null && obj.Attributes.GetUserString(LabelSourceKey) != "dxf" && DxfText.LooksMangled(label.Text))
                suspect.Add(label);
        }
        return labels;
    }

    /// <summary>
    /// Stamps a marker with its room's record (RoomDetect.Report): the name as
    /// forsk:room_name and the tag point as forsk:room_at. A marker made from a
    /// detected outline also takes the stable id as forsk:id and name, and the
    /// room's net area as forsk:area. Returns the record, or null for a marker
    /// with no outline to read.
    /// </summary>
    private static RoomDetect.Tag StampRoomMarker(RhinoDoc doc, RhinoObject marker, List<RoomDetect.Room> rooms,
        string[] roomIds, string[] names, List<RoomDetect.Label> labels)
    {
        if (!TryRoomPolygon(marker, out var polygon)) return null;
        var outline = PlanPoints(polygon);
        var tag = RoomDetect.Report(outline, marker.Name, ParseMm(marker.Attributes.GetUserString("forsk:area")) ?? 0,
            rooms, roomIds, names, labels);
        if (tag == null) return null;
        var attr = marker.Attributes.Duplicate();
        attr.SetUserString(RoomNameKey, tag.Name);
        attr.SetUserString(RoomAtKey, RoomDetect.StampAt(outline, tag.At));
        if (tag.Detected)
        {
            attr.Name = tag.Id;
            attr.SetUserString("forsk:id", tag.Id);
            attr.SetUserString(RoomIdKey, tag.Id);
            attr.SetUserString("forsk:area", FormatMm(tag.Area));
        }
        doc.Objects.ModifyAttributes(marker.Id, attr, true);
        return tag;
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
