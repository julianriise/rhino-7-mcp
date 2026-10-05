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
/// doors and split by space_divider curves, drawn as one closed curve on A-ROOM.
/// A room keeps that curve: the next run replaces it by forsk:id and deletes the
/// copies. Outlines already on A-ROOM win; detection fills the rest. The floor
/// plate is the click target. The curve stays, locked once a plate exists, for print.
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
        var inventory = new List<RoomCurves.Curve>();
        var earlierRings = new List<KeyValuePair<string, List<RoomDetect.Pt>>>();
        if (roomLayer != null)
        {
            foreach (var obj in EnumerateDocObjects(doc))
            {
                if (!ObjectOnLayer(doc, obj, roomLayer) || !(obj.Geometry is Curve curve) || !curve.IsClosed) continue;
                var pts = LoopPoints(FlattenToWorldXY(curve, tol), tol);
                if (pts == null || pts.Count < 3) continue;
                var plan = PlanPoints(pts);
                var forskId = obj.Attributes.GetUserString("forsk:id");
                if (string.IsNullOrEmpty(forskId)) forskId = obj.Attributes.GetUserString(RoomIdKey) ?? "";
                var detected = string.Equals(obj.Attributes.GetUserString(RoomSourceKey), DetectedRoomSource, StringComparison.Ordinal)
                    || forskId.StartsWith(DetectedRoomPrefix, StringComparison.Ordinal);
                var marker = IsRoomMarker(obj);
                inventory.Add(new RoomCurves.Curve
                {
                    Id = obj.Id.ToString(),
                    ForskId = forskId,
                    Ring = RoomCurves.RingKey(plan),
                    Detected = detected,
                    Marker = marker
                });
                // A detected curve, including one already stamped as the room record, keeps its id.
                // A curve the user drew wins the region. A marker copy is neither: the next pass deletes it.
                if (detected)
                    earlierRings.Add(new KeyValuePair<string, List<RoomDetect.Pt>>(forskId, plan));
                else if (!marker)
                    scene.Keep.Add(plan);
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
        var doors = OpeningBoxes(doc, false);
        var windows = OpeningBoxes(doc, true);

        var roomRings = new List<string>(found.Rooms.Count);
        foreach (var room in found.Rooms) roomRings.Add(RoomCurves.RingKey(room.Ring));
        var plans = RoomCurves.Plan(inventory, roomIds, roomRings);
        var drop = new HashSet<string>(RoomCurves.Leftovers(inventory, plans), StringComparer.Ordinal);
        foreach (var plan in plans)
            foreach (var id in plan.Delete) drop.Add(id);
        var removed = 0;
        foreach (var id in drop)
            if (DeleteRoomCurve(doc, id)) removed++;

        var ids = new JArray();
        var tags = new List<RoomDetect.Tag>();
        var area = 0.0;
        var stamped = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < found.Rooms.Count; i++)
        {
            var room = found.Rooms[i];
            var outline = RoomOutline(room.Ring, z);
            var kept = FindRoomCurve(doc, plans[i].Keep);
            if (kept != null)
            {
                if (kept.IsLocked) doc.Objects.Unlock(kept.Id, false);
                if (!doc.Objects.Replace(kept.Id, outline))
                {
                    if (DeleteRoomCurve(doc, kept.Id.ToString())) removed++;
                    kept = null;
                }
            }
            if (kept == null)
            {
                var attr = new ObjectAttributes { Name = roomIds[i], LayerIndex = layer.Index };
                attr.SetUserString(RoomSourceKey, DetectedRoomSource);
                attr.SetUserString(RoomIdKey, roomIds[i]);
                attr.SetUserString("forsk:id", roomIds[i]);
                attr.SetUserString("forsk:area", FormatMm(room.Area));
                BakePace.Breathe(null);
                var added = BakePace.AddCurves(doc, new[] { outline }, new[] { attr }, null)[0];
                if (added == Guid.Empty)
                {
                    warnings.Add($"Room outline {roomIds[i]} was not added.");
                    continue;
                }
                kept = doc.Objects.FindId(added);
            }
            if (kept == null) continue;
            NoteRoom(doc, kept, found.Rooms, roomIds, names, labels, doors, windows, UserDrawn(inventory, kept.Id.ToString()), ids, tags, ref area);
            stamped.Add(kept.Id.ToString());
        }
        // A curve the user drew that the walls did not take stays, named, and its marker copy is already gone.
        foreach (var curve in inventory)
        {
            if (curve.Marker || curve.Detected || drop.Contains(curve.Id) || stamped.Contains(curve.Id)) continue;
            var drawn = FindRoomCurve(doc, curve.Id);
            if (drawn == null) continue;
            if (drawn.IsLocked) doc.Objects.Unlock(drawn.Id, false);
            NoteRoom(doc, drawn, found.Rooms, roomIds, names, labels, doors, windows, true, ids, tags, ref area);
        }

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
                ["source"] = tag.Detected ? DetectedRoomSource : "drawn",
                ["room_type"] = tag.RoomType ?? RoomTypes.Unassigned,
                ["room_type_source"] = tag.RoomTypeSource ?? ""
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

        BakePace.Redraw(doc);
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
            if (IsRoomRecord(obj)) list.Add(obj);
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
    /// The one curve that is the room: a generated room marker, or a curve the
    /// user drew once it carries a name and a tag point. Plates and plan tags read these.
    /// </summary>
    private static bool IsRoomRecord(RhinoObject obj)
    {
        if (IsRoomMarker(obj)) return true;
        if (obj?.Attributes == null) return false;
        return !string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(RoomNameKey))
            && !string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(RoomAtKey));
    }

    static bool UserDrawn(List<RoomCurves.Curve> inventory, string id)
    {
        foreach (var curve in inventory)
            if (curve.Id == id) return !curve.Marker && !curve.Detected;
        return false;
    }

    static RhinoObject FindRoomCurve(RhinoDoc doc, string id)
    {
        return Guid.TryParse(id, out var guid) ? doc.Objects.FindId(guid) : null;
    }

    static bool DeleteRoomCurve(RhinoDoc doc, string id)
    {
        var obj = FindRoomCurve(doc, id);
        if (obj == null) return false;
        if (obj.IsLocked) doc.Objects.Unlock(obj.Id, false);
        return doc.Objects.Delete(obj.Id, true);
    }

    static void NoteRoom(RhinoDoc doc, RhinoObject curve, List<RoomDetect.Room> rooms, string[] roomIds, string[] names,
        List<RoomDetect.Label> labels, List<RoomDetect.Box> doors, List<RoomDetect.Box> windows, bool userDrawn,
        JArray ids, List<RoomDetect.Tag> tags, ref double area)
    {
        var tag = StampRoomMarker(doc, curve, rooms, roomIds, names, labels, doors, windows, userDrawn);
        if (tag != null) tags.Add(tag);
        area += tag?.Area ?? ParseMm(curve.Attributes.GetUserString("forsk:area")) ?? 0;
        ids.Add(curve.Id.ToString());
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
    /// The texts on the label layer that name rooms, at their insertion points.
    /// dxf_import has already decoded what it imported. A label Rhino imported
    /// on its own can still say B00F8ttekott; the letter is put back here, and
    /// that raw text is the suspect row.
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
            var raw = text.PlainText;
            var label = new RoomDetect.Label(DxfText.RepairRemnant(raw), text.TextHeight,
                new RoomDetect.Pt(text.Plane.Origin.X, text.Plane.Origin.Y));
            labels.Add(label);
            if (suspect != null && obj.Attributes.GetUserString(LabelSourceKey) != "dxf" && DxfText.LooksMangled(raw))
                suspect.Add(new RoomDetect.Label(raw, label.Height, label.At));
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
        string[] roomIds, string[] names, List<RoomDetect.Label> labels, List<RoomDetect.Box> doors,
        List<RoomDetect.Box> windows, bool userDrawn)
    {
        if (!TryRoomPolygon(marker, out var polygon)) return null;
        var outline = PlanPoints(polygon);
        var tag = RoomDetect.Report(outline, marker.Name, ParseMm(marker.Attributes.GetUserString("forsk:area")) ?? 0,
            rooms, roomIds, names, labels);
        if (tag == null) return null;
        var attr = marker.Attributes.Duplicate();
        attr.SetUserString(RoomNameKey, tag.Name);
        attr.SetUserString(RoomAtKey, RoomDetect.StampAt(outline, tag.At));
        var decision = WriteRoomType(attr, outline, tag.Area, tag.Name, doors, windows);
        tag.RoomType = decision.Type;
        tag.RoomTypeSource = decision.Source;
        // The user's curve keeps its name and tag point. It does not become generated, so clear_generated leaves it.
        if (tag.Detected && !userDrawn)
        {
            StampForskTags(attr, new ForskStamp { Kind = "room", Level = "0", Id = tag.Id, Area = tag.Area });
            attr.Name = tag.Id;
            attr.SetUserString(RoomSourceKey, DetectedRoomSource);
            attr.SetUserString(RoomIdKey, tag.Id);
        }
        BakePace.Modify(doc, marker.Id, attr, true);
        return tag;
    }

    /// <summary>Door boxes, or window boxes. Windows are not doors: they do not close a gap.</summary>
    static List<RoomDetect.Box> OpeningBoxes(RhinoDoc doc, bool windows)
    {
        var boxes = new List<RoomDetect.Box>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj)) continue;
            if (!string.Equals(GetForskKind(obj), "opening_marker", StringComparison.OrdinalIgnoreCase)) continue;
            var isWindow = string.Equals(obj.Attributes.GetUserString("forsk:opening_kind"), "window", StringComparison.OrdinalIgnoreCase);
            if (isWindow != windows) continue;
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            boxes.Add(new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y));
        }
        return boxes;
    }

    /// <summary>
    /// Writes forsk:room_type from the name inside the room, else a guess.
    /// A stored user or label type is kept. Returns the decision that was written.
    /// </summary>
    static RoomTypes.Decision WriteRoomType(ObjectAttributes attr, IList<RoomDetect.Pt> ring, double areaMm2, string name,
        IList<RoomDetect.Box> doors, IList<RoomDetect.Box> windows)
    {
        var decision = RoomTypes.Choose(
            attr.GetUserString(RoomTypes.Key),
            attr.GetUserString(RoomTypes.SourceKey),
            name,
            RoomTypes.Measure(ring, areaMm2, doors, windows));
        attr.SetUserString(RoomTypes.Key, decision.Type);
        attr.SetUserString(RoomTypes.SourceKey, string.IsNullOrEmpty(decision.Source) ? null : decision.Source);
        return decision;
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
