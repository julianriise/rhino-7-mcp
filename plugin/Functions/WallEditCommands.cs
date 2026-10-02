using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using RhinoMCPPlugin.Forsk;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// F3 wall edits on the soft-param record. A baked plan is one wall record, so
/// an edit works on one straight run inside its outline (WallEdit): the path
/// is written, the openings on the run go with it, and the host is rebuilt
/// from its path. The floor slab and flat roof that came from that record
/// are rebuilt, and rooms are detected again. A refused edit puts the record
/// back and the document is unchanged.
/// </summary>
public partial class RhinoMCPFunctions
{
    private sealed class WallPick
    {
        public WallSolid Host;
        /// <summary>The host record's own rings.</summary>
        public List<List<RoomDetect.Pt>> Rings;
        /// <summary>The run, picked on the cluster's shape.</summary>
        public WallEdit.Run Run;
        /// <summary>The run as the receipt names it: the north wall, or the wall at (x, y).</summary>
        public string Label;
        /// <summary>Every wall record whose path reads, and its rings, by the same index.</summary>
        public List<RhinoObject> Walls;
        public List<List<List<RoomDetect.Pt>>> Records;
        /// <summary>The host's cluster: the records that touch it, read as one shape (WallJoins).</summary>
        public WallJoins.Graph Graph;
    }

    [McpCommand("move_wall", ModelView = true, Map = MapEdit.Wall)]
    public JObject MoveWall(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var distance = ReadOptionalDouble(parameters, "distance_mm");
        if (!distance.HasValue || distance.Value <= 0)
            throw new ArgumentException("distance_mm must be positive.");
        var pick = PickWallRun(doc, parameters, tol);
        if (!WallEdit.TryToward(pick.Run, parameters?["toward"]?.ToString(), distance.Value, out var by, out var why)
            || !WallJoins.TryMove(pick.Records, pick.Graph, pick.Run, by, tol, out var moved, out why))
            throw new InvalidOperationException(why);

        // The openings on the run go with it, from every record in the cluster. One on
        // a wall that meets the run stays, and the run must not land on it or leave it
        // past the end of its wall.
        var onRun = new List<(int Wall, RhinoObject Marker)>();
        foreach (var i in pick.Graph.Records)
        {
            var wall = pick.Walls[i];
            foreach (var marker in MarkersOnHost(doc, wall.Id, wall.Attributes?.GetUserString("forsk:id")))
            {
                var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (!box.IsValid) continue;
                if (WallEdit.InBand(pick.Run, new RoomDetect.Pt(box.Center.X, box.Center.Y), tol))
                    onRun.Add((i, marker));
                else if (!WallEdit.Clear(moved.Shape, pick.Run, by, new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y), tol))
                    throw new InvalidOperationException("Not moved: " + MarkerLabel(marker)
                        + " would sit in the moved wall or past the end of its own.");
            }
        }

        // The record the run stands in names it; else the wall picked.
        var owner = pick.Graph.Records.FindIndex(i => WallEdit.InRegion(pick.Records[i], WallJoins.Middle(pick.Run)));
        var named = owner >= 0 ? pick.Walls[pick.Graph.Records[owner]].Id : pick.Host.Id;
        var forskId = doc.Objects.FindId(named)?.Attributes?.GetUserString("forsk:id");
        var touched = new List<int>(moved.Records.Keys);
        foreach (var item in onRun)
            if (!touched.Contains(item.Wall)) touched.Add(item.Wall);

        var shift = new Vector3d(pick.Run.Normal.X * by, pick.Run.Normal.Y * by, 0);
        var sourceLayer = pick.Host.Attributes?.GetUserString("forsk:source_layer");
        var undos = new List<HostUndo>();
        var carried = new JArray();
        var rebuiltIds = new Dictionary<Guid, Guid>();
        JObject rebuilt = null;
        string followed;
        try
        {
            foreach (var i in touched) undos.Add(SnapshotWholeHost(doc, pick.Walls[i].Id));
            foreach (var record in moved.Records)
                WriteWallPath(doc, pick.Walls[record.Key].Id, WallEdit.Path(record.Value));
            foreach (var item in onRun)
            {
                var brep = GetBrepFromObject(item.Marker)?.DuplicateBrep();
                if (brep == null || !brep.Translate(shift) || !doc.Objects.Replace(item.Marker.Id, brep))
                    throw new InvalidOperationException("Opening marker not found.");
                carried.Add(item.Marker.Id.ToString());
            }
            foreach (var undo in undos)
            {
                var result = RebuildHostWall(new JObject { ["id"] = undo.HostBefore.ToString() });
                undo.Committed = true;
                undo.HostAfter = Guid.TryParse(result?["host_id"]?.ToString(), out var after) ? after : undo.HostBefore;
                undo.NewBlocks = GuidList(result?["block_ids"] as JArray);
                rebuiltIds[undo.HostBefore] = undo.HostAfter;
                if (undo.HostBefore == named) rebuilt = result;
            }
            followed = FollowNeighbours(doc, sourceLayer, pick.Graph.Shape, moved.Shape);
        }
        catch (Exception ex)
        {
            for (var i = undos.Count - 1; i >= 0; i--) RollbackCommittedHost(doc, undos[i]);
            throw new InvalidOperationException("Wall not moved. " + ex.Message, ex);
        }

        var hostId = rebuiltIds.TryGetValue(named, out var renamed) ? renamed : named;
        var heading = WallEdit.Heading(pick.Run, by);
        var with = carried.Count == 0 ? "" : carried.Count == 1 ? ", 1 opening with it" : ", " + carried.Count + " openings with it";
        var also = new List<string>();
        foreach (var f in moved.Followed) also.Add(f.Wall);
        if (also.Count > 0) with += "; " + WallFollowPlan.Walls(also) + " followed";
        var moveResult = new JObject
        {
            ["host_id"] = hostId.ToString(),
            ["forsk_id"] = forskId ?? "",
            ["wall"] = pick.Label,
            ["toward"] = heading,
            ["distance_mm"] = distance.Value,
            ["normal"] = new JArray(pick.Run.Normal.X, pick.Run.Normal.Y),
            ["faces_before"] = new JArray(pick.Run.Near, pick.Run.Far),
            ["faces_after"] = new JArray(pick.Run.Near + by, pick.Run.Far + by),
            ["length_mm"] = pick.Run.Length,
            ["thickness"] = rebuilt?["thickness"],
            ["path_points"] = rebuilt?["path_points"],
            ["openings_moved"] = carried,
            ["followed"] = FollowedJson(pick, moved.Followed),
            ["records"] = RecordIds(pick, moved.Records.Keys),
            ["rebuilt"] = followed,
            ["warnings"] = rebuilt?["warnings"] ?? new JArray(),
            ["ok"] = true,
            ["message"] = "Moved " + pick.Label + " of " + (string.IsNullOrEmpty(forskId) ? "the wall" : forskId) + " "
                + FormatMm(Math.Round(distance.Value)) + " mm " + heading + with
                + ". " + followed
        };
        var report = HostOpeningReport(doc, hostId, Guid.Empty, Point3d.Unset);
        if (report != null) moveResult.Merge(report);
        doc.Views.Redraw();
        return moveResult;
    }

    [McpCommand("delete_wall", ModelView = true, Map = MapEdit.Wall)]
    public JObject DeleteWall(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var pick = PickWallRun(doc, parameters, tol);
        if (!WallJoins.TryDelete(pick.Records, pick.Graph, pick.Run, tol, out var cut, out var why))
            throw new InvalidOperationException(why);

        // The openings in the run go with it, and those of a record that goes whole.
        // Every other one on a record the cut changed must still sit in that record.
        var orphans = new List<(int Wall, RhinoObject Marker)>();
        foreach (var record in cut.Records)
        {
            var wall = pick.Walls[record.Key];
            foreach (var marker in MarkersOnHost(doc, wall.Id, wall.Attributes?.GetUserString("forsk:id")))
            {
                var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (!box.IsValid) continue;
                if (record.Value == null || WallEdit.InBand(pick.Run, new RoomDetect.Pt(box.Center.X, box.Center.Y), tol))
                    orphans.Add((record.Key, marker));
                else if (!WallEdit.Holds(record.Value, new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y)))
                    throw new InvalidOperationException("Not deleted: " + MarkerLabel(marker) + " would hang past the end of its wall.");
            }
        }

        // The record the run stood in names it; else the wall picked.
        var owner = pick.Graph.Records.FindIndex(i => WallEdit.InRegion(pick.Records[i], WallJoins.Middle(pick.Run)));
        var namedIndex = owner >= 0 ? pick.Graph.Records[owner] : pick.Walls.FindIndex(w => w.Id == pick.Host.Id);
        var named = pick.Walls[namedIndex].Id;
        var forskId = pick.Walls[namedIndex].Attributes?.GetUserString("forsk:id");
        var namedGone = cut.Records.TryGetValue(namedIndex, out var namedLeft) && namedLeft == null;

        var sourceLayer = pick.Host.Attributes?.GetUserString("forsk:source_layer");
        var undos = new Dictionary<int, HostUndo>();
        JObject rebuilt = null;
        var rebuiltIds = new Dictionary<Guid, Guid>();
        string followed;
        try
        {
            foreach (var record in cut.Records) undos[record.Key] = SnapshotWholeHost(doc, pick.Walls[record.Key].Id);
            foreach (var item in orphans)
                RemoveOpeningPieces(doc, item.Marker.Id, undos[item.Wall].Removed);
            foreach (var record in cut.Records)
            {
                var undo = undos[record.Key];
                if (record.Value == null)
                {
                    var wall = doc.Objects.FindId(undo.HostBefore);
                    if (wall == null || !TrackDelete(doc, wall, undo.Removed))
                        throw new InvalidOperationException("Could not delete the wall.");
                    continue;
                }
                WriteWallPath(doc, undo.HostBefore, WallEdit.Path(record.Value));
                var result = RebuildHostWall(new JObject { ["id"] = undo.HostBefore.ToString() });
                undo.Committed = true;
                undo.HostAfter = Guid.TryParse(result?["host_id"]?.ToString(), out var after) ? after : undo.HostBefore;
                undo.NewBlocks = GuidList(result?["block_ids"] as JArray);
                rebuiltIds[undo.HostBefore] = undo.HostAfter;
                if (undo.HostBefore == named) rebuilt = result;
            }
            followed = FollowNeighbours(doc, sourceLayer, pick.Graph.Shape, cut.Shape);
        }
        catch (Exception ex)
        {
            var list = new List<HostUndo>(undos.Values);
            for (var i = list.Count - 1; i >= 0; i--) RollbackCommittedHost(doc, list[i]);
            throw new InvalidOperationException("Wall not deleted. " + ex.Message, ex);
        }

        var label = string.IsNullOrEmpty(forskId) ? "the wall" : forskId;
        var openingMarkers = new List<RhinoObject>();
        var deleted = new JArray();
        foreach (var item in orphans)
        {
            openingMarkers.Add(item.Marker);
            deleted.Add(item.Marker.Id.ToString());
        }
        var openings = OpeningsLine(openingMarkers);
        var joined = new List<string>();
        foreach (var f in cut.Followed) joined.Add(f.Wall);
        var hostId = namedGone ? Guid.Empty : rebuiltIds.TryGetValue(named, out var renamed) ? renamed : named;
        var left = namedGone ? null : namedLeft ?? pick.Records[namedIndex];
        var wholeRecord = namedGone && pick.Graph.Records.Count == 1;
        var deleteResult = new JObject
        {
            ["host_id"] = hostId == Guid.Empty ? "" : hostId.ToString(),
            ["forsk_id"] = forskId ?? "",
            ["wall"] = pick.Label,
            ["record_deleted"] = namedGone,
            ["openings_deleted"] = deleted,
            ["length_mm"] = pick.Run.Length,
            ["thickness"] = pick.Run.Thickness,
            ["path_points"] = rebuilt?["path_points"] ?? 0,
            ["holes"] = left == null ? 0 : left.Count - 1,
            ["followed"] = FollowedJson(pick, cut.Followed),
            ["records"] = RecordIds(pick, cut.Records.Keys),
            ["rebuilt"] = followed,
            ["warnings"] = rebuilt?["warnings"] ?? new JArray(),
            ["ok"] = true,
            ["message"] = (wholeRecord
                    ? "Deleted " + label + ", a wall standing on its own"
                    : namedGone
                        ? "Deleted " + pick.Label + ", " + label
                        : "Deleted " + pick.Label + " of " + label)
                + (openings.Length == 0 ? "" : ", and its " + openings)
                + (joined.Count == 0 ? "" : "; it was joined to " + WallFollowPlan.Walls(joined))
                + ". " + followed
        };
        if (hostId != Guid.Empty)
        {
            var report = HostOpeningReport(doc, hostId, Guid.Empty, Point3d.Unset);
            if (report != null) deleteResult.Merge(report);
        }
        doc.Views.Redraw();
        return deleteResult;
    }

    [McpCommand("add_wall", ModelView = true, Map = MapEdit.Wall)]
    public JObject AddWall(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        ReadWallLine(doc, parameters, out var from, out var to);

        var walls = new List<RhinoObject>();
        var records = new List<List<List<RoomDetect.Pt>>>();
        RhinoObject nearest = null;
        var best = double.MaxValue;
        var mid = new RoomDetect.Pt((from.X + to.X) / 2.0, (from.Y + to.Y) / 2.0);
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase) || IsExistingUnderlay(doc, obj))
                continue;
            var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (rings == null) continue;
            walls.Add(obj);
            records.Add(rings);
            var d = WallEdit.Distance(rings, mid, tol);
            if (d < best)
            {
                best = d;
                nearest = obj;
            }
        }
        // A new wall takes the nearest wall's thickness, height and level unless told otherwise.
        var thickness = ReadOptionalDouble(parameters, "thickness")
            ?? ParseMm(nearest?.Attributes.GetUserString("forsk:thickness")) ?? 200.0;
        if (!WallEdit.TryAdd(records, from, to, thickness, tol, out var added, out var why))
            throw new InvalidOperationException(why);

        JObject result;
        if (added.Joined >= 0)
        {
            var host = ReadHostWall(doc, walls[added.Joined].Id, requireVertical: true);
            var forskId = host.Attributes?.GetUserString("forsk:id");
            foreach (var marker in MarkersOnHost(doc, host.Id, forskId))
            {
                var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (box.IsValid && WallEdit.InTheWay(added, thickness, new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y), tol))
                    throw new InvalidOperationException("Not added: " + MarkerLabel(marker) + " is in the way.");
            }
            var undo = SnapshotWholeHost(doc, host.Id);
            var sourceLayer = host.Attributes?.GetUserString("forsk:source_layer");
            JObject rebuilt;
            string followed;
            try
            {
                WriteWallPath(doc, host.Id, WallEdit.Path(added.Rings));
                rebuilt = RebuildHostWall(new JObject { ["id"] = host.Id.ToString() });
                followed = FollowNeighbours(doc, sourceLayer, records[added.Joined], added.Rings);
            }
            catch (Exception ex)
            {
                RollbackCommittedHost(doc, undo);
                throw new InvalidOperationException("Wall not added. " + ex.Message, ex);
            }
            var hostId = Guid.TryParse(rebuilt?["host_id"]?.ToString(), out var parsed) ? parsed : host.Id;
            var height = rebuilt?["height"]?.ToObject<double>() ?? 0;
            result = AddedWall(added, hostId, forskId, thickness, height, true, rebuilt?["path_points"]);
            result["message"] = "Added a " + FormatMm(Math.Round(thickness)) + " mm wall to "
                + (string.IsNullOrEmpty(forskId) ? "the wall" : forskId) + ", "
                + FormatMm(Math.Round(Length(added))) + " mm long. " + followed;
            var report = HostOpeningReport(doc, hostId, Guid.Empty, Point3d.Unset);
            if (report != null) result.Merge(report);
        }
        else
        {
            var height = ReadOptionalDouble(parameters, "height")
                ?? ParseMm(nearest?.Attributes.GetUserString("forsk:height")) ?? ForskDefaults.WallHeight;
            if (height <= 0)
                throw new ArgumentException("height must be positive.");
            var level = nearest?.Attributes.GetUserString("forsk:level");
            var path = WallEdit.Path(added.Rings);
            var warnings = new JArray();
            var solid = PrepareFreshSolid(ExtrudeFromPath(path, height, tol, warnings), tol, out var diagnostic);
            if (solid == null)
                throw new InvalidOperationException("Wall not added. " + diagnostic);
            var number = NextWallNumber(doc);
            var forskId = FormatStableId("w", number);
            var layer = EnsureLayer(doc, "A-WALL", Color.FromArgb(180, 180, 180));
            var attr = new ObjectAttributes
            {
                Name = "wall-" + number.ToString("D2", CultureInfo.InvariantCulture),
                LayerIndex = layer.Index,
                MaterialSource = ObjectMaterialSource.MaterialFromLayer
            };
            StampForskTags(attr, new ForskStamp
            {
                Kind = "wall",
                Level = string.IsNullOrEmpty(level) ? "0" : level,
                Id = forskId,
                Height = height,
                Thickness = thickness,
                Path = path
            });
            var id = doc.Objects.AddBrep(solid, attr);
            if (id == Guid.Empty)
                throw new InvalidOperationException("Wall not added. Rhino did not take the solid.");
            string followed;
            try
            {
                followed = FollowNeighbours(doc, null, added.Rings, added.Rings);
            }
            catch (Exception ex)
            {
                if (doc.Objects.FindId(id) != null)
                    doc.Objects.Delete(id, true);
                throw new InvalidOperationException("Wall not added. " + ex.Message, ex);
            }
            result = AddedWall(added, id, forskId, thickness, height, false, WallEdit.Rings(path)[0].Count);
            var joins = new JArray();
            foreach (var i in added.Touches) joins.Add(walls[i].Attributes.GetUserString("forsk:id") ?? "");
            result["joins"] = joins;
            var where = joins.Count > 1
                ? " joined to " + string.Join(" and ", joins.Select(j => j.ToString())) + ", "
                : " standing on its own, ";
            result["message"] = "Added " + forskId + ", a " + FormatMm(Math.Round(thickness)) + " mm wall" + where
                + FormatMm(Math.Round(Length(added))) + " mm long and " + FormatMm(Math.Round(height)) + " mm high. "
                + followed;
        }
        doc.Views.Redraw();
        return result;
    }

    /// <summary>
    /// F2 push-pull: one side of a room moves out or in. The side is the wall
    /// run whose face is that side of the room, and it moves as move_wall moves
    /// it, joined walls and all. The room is the one given, else the one selected.
    /// </summary>
    [McpCommand("room_push_pull", ModelView = true, Map = MapEdit.Wall)]
    public JObject RoomPushPull(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var side = (parameters?["side"]?.ToString() ?? "").Trim().ToLowerInvariant();
        var way = (parameters?["way"]?.ToString() ?? "out").Trim().ToLowerInvariant();
        if (way != "out" && way != "in")
            throw new ArgumentException("way is out or in.");
        var distance = ReadOptionalDouble(parameters, "distance_mm");
        if (!distance.HasValue || distance.Value <= 0)
            throw new ArgumentException("distance_mm must be positive.");

        var room = ResolveRoom(doc, parameters);
        var outline = RoomMarkerOutline(room);
        var ring = outline == null ? null : PathPoints(outline);
        if (!WallJoins.TryRoomSide(ring, side, tol, out var at, out var why))
            throw new InvalidOperationException(why);
        var name = room.Attributes?.GetUserString("forsk:room_name");
        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(room.Name) ? "the room" : room.Name;

        var toward = way == "out" ? side : Opposite(side);
        JObject moved;
        try
        {
            moved = MoveWall(new JObject { ["at"] = new JArray(at.X, at.Y), ["toward"] = toward, ["distance_mm"] = distance.Value });
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ex.Message.Replace("Not moved:", "Not " + (way == "out" ? "pushed" : "pulled") + ":"), ex);
        }

        var count = (moved["openings_moved"] as JArray)?.Count ?? 0;
        var with = count == 0 ? "" : count == 1 ? ", 1 opening with it" : ", " + count + " openings with it";
        var names = new List<string>();
        foreach (var f in moved["followed"] as JArray ?? new JArray()) names.Add(f["wall"]?.ToString());
        if (names.Count > 0) with += "; " + WallFollowPlan.Walls(names) + " followed";
        moved["room"] = name;
        moved["room_id"] = room.Id.ToString();
        moved["side"] = side;
        moved["way"] = way;
        moved["message"] = (way == "out" ? "Pushed " : "Pulled ") + name + (name.EndsWith("s", StringComparison.Ordinal) ? "' " : "'s ")
            + side + " side " + FormatMm(Math.Round(distance.Value)) + " mm " + way + with + ". " + moved["rebuilt"];
        return moved;
    }

    private static string Opposite(string side)
    {
        switch (side)
        {
            case "north": return "south";
            case "south": return "north";
            case "east": return "west";
            default: return "east";
        }
    }

    /// <summary>The room given by id, else the one room selected.</summary>
    private static RhinoObject ResolveRoom(RhinoDoc doc, JObject parameters)
    {
        var idToken = parameters?["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(idToken))
        {
            var obj = Guid.TryParse(idToken, out var guid) ? doc.Objects.FindId(guid) : null;
            if (obj == null || !IsRoomMarker(obj))
                throw new InvalidOperationException("Not a Forsk room.");
            return obj;
        }
        var rooms = new List<RhinoObject>();
        foreach (var obj in ListSelected(doc))
            if (IsRoomMarker(obj)) rooms.Add(obj);
        if (rooms.Count != 1)
            throw new InvalidOperationException("Select one room, then say it again.");
        return rooms[0];
    }

    /// <summary>The walls that followed, for the result and the review card: the record, the name, the change in length.</summary>
    private static JArray FollowedJson(WallPick pick, IEnumerable<WallJoins.Followed> followed)
    {
        var list = new JArray();
        foreach (var f in followed)
        {
            var record = f.Records.Count > 0 ? pick.Walls[f.Records[0]].Attributes?.GetUserString("forsk:id") : null;
            list.Add(new JObject { ["forsk_id"] = record ?? "", ["wall"] = f.Wall, ["change_mm"] = f.ChangeMm });
        }
        return list;
    }

    private static JArray RecordIds(WallPick pick, IEnumerable<int> records)
    {
        var ids = new JArray();
        foreach (var i in records) ids.Add(pick.Walls[i].Attributes?.GetUserString("forsk:id") ?? "");
        return ids;
    }

    private static JObject AddedWall(WallEdit.Added added, Guid hostId, string forskId, double thickness, double height, bool joined, JToken pathPoints)
    {
        return new JObject
        {
            ["host_id"] = hostId.ToString(),
            ["forsk_id"] = forskId ?? "",
            ["joined"] = joined,
            ["from"] = new JArray(added.From.X, added.From.Y),
            ["to"] = new JArray(added.To.X, added.To.Y),
            ["length_mm"] = Length(added),
            ["thickness"] = thickness,
            ["height"] = height,
            ["path_points"] = pathPoints,
            ["holes"] = added.Rings.Count - 1,
            ["ok"] = true
        };
    }

    private static double Length(WallEdit.Added added)
    {
        var dx = added.To.X - added.From.X;
        var dy = added.To.Y - added.From.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The new wall's centreline: from and to, or a straight line the user drew (line_id).</summary>
    private static void ReadWallLine(RhinoDoc doc, JObject parameters, out RoomDetect.Pt from, out RoomDetect.Pt to)
    {
        var lineToken = parameters?["line_id"]?.ToString();
        var hasPoints = parameters?["from"] != null || parameters?["to"] != null;
        if (!string.IsNullOrWhiteSpace(lineToken) && hasPoints)
            throw new ArgumentException("Give from and to, or line_id, not both.");
        if (!string.IsNullOrWhiteSpace(lineToken))
        {
            if (!Guid.TryParse(lineToken, out var lineId) || !(doc.Objects.FindId(lineId)?.Geometry is Curve curve))
                throw new InvalidOperationException("line_id is not a curve in this document.");
            if (!curve.IsLinear(Math.Max(doc.ModelAbsoluteTolerance, 1.0)))
                throw new InvalidOperationException("line_id is not a straight line. Draw the wall as one line.");
            from = new RoomDetect.Pt(curve.PointAtStart.X, curve.PointAtStart.Y);
            to = new RoomDetect.Pt(curve.PointAtEnd.X, curve.PointAtEnd.Y);
            return;
        }
        if (!TryReadPoint(parameters?["from"], out from) || !TryReadPoint(parameters?["to"], out to))
            throw new ArgumentException("Give the wall's centreline: from and to [x, y] in mm, or line_id.");
    }

    private static int NextWallNumber(RhinoDoc doc)
    {
        var max = 0;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase)) continue;
            var id = obj.Attributes.GetUserString("forsk:id") ?? "";
            if (id.Length > 1 && id[0] == 'w'
                && int.TryParse(id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                max = Math.Max(max, n);
        }
        return max + 1;
    }

    private static string MarkerLabel(RhinoObject marker) =>
        string.IsNullOrEmpty(marker?.Name) ? "an opening" : marker.Name;

    /// <summary>The openings a delete took: 1 door, 2 windows, 1 door and 1 window.</summary>
    private static string OpeningsLine(List<RhinoObject> markers)
    {
        var doors = 0;
        var windows = 0;
        foreach (var marker in markers)
        {
            if (string.Equals(marker.Attributes?.GetUserString("forsk:opening_kind"), "window", StringComparison.OrdinalIgnoreCase))
                windows++;
            else
                doors++;
        }
        var parts = new List<string>();
        if (doors > 0) parts.Add(doors + (doors == 1 ? " door" : " doors"));
        if (windows > 0) parts.Add(windows + (windows == 1 ? " window" : " windows"));
        return string.Join(" and ", parts);
    }

    /// <summary>
    /// The wall record and the run in it. The record is the id, else the one
    /// selected wall, else the wall nearest at, else the only wall. The run is
    /// side (an outer wall) or the face nearest at.
    /// </summary>
    private WallPick PickWallRun(RhinoDoc doc, JObject parameters, double tol)
    {
        var side = parameters?["side"]?.ToString();
        var hasSide = !string.IsNullOrWhiteSpace(side);
        var atToken = parameters?["at"];
        var hasAt = atToken != null && atToken.Type != JTokenType.Null;
        RoomDetect.Pt at = default;
        if (hasAt && !TryReadPoint(atToken, out at))
            throw new ArgumentException("at is [x, y] in mm.");
        if (hasAt && hasSide)
            throw new ArgumentException("Give side or at, not both.");
        if (!hasAt && !hasSide)
            throw new ArgumentException("Say which wall: a side (north, south, east or west) or a point at [x, y] in mm.");

        var host = ResolveWallRecord(doc, parameters, hasAt ? at : (RoomDetect.Pt?)null, tol);
        var rings = WallEdit.Rings(host.Attributes?.GetUserString("forsk:path"));
        if (rings == null)
            throw new InvalidOperationException(MissingWallPathMessage);
        var walls = new List<RhinoObject>();
        var records = new List<List<List<RoomDetect.Pt>>>();
        var index = -1;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase) || IsExistingUnderlay(doc, obj))
                continue;
            var path = obj.Id == host.Id ? rings : WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (path == null) continue;
            if (obj.Id == host.Id) index = walls.Count;
            walls.Add(obj);
            records.Add(path);
        }
        if (index < 0)
            throw new InvalidOperationException("Not a Forsk wall.");
        // The records that touch the host are read as one shape; a lone record is its own rings.
        var graph = WallJoins.Build(records, WallJoins.ClusterOf(records, index, tol), tol)
            ?? WallJoins.Build(records, new List<int> { index }, tol);

        WallEdit.Run run;
        string why;
        var ok = hasAt
            ? WallEdit.TryPick(graph.Shape, at, tol, out run, out why)
            : WallEdit.TryPickSide(graph.Shape, side, tol, out run, out why);
        if (!ok) throw new InvalidOperationException(why);
        var label = hasSide
            ? "the " + side.Trim().ToLowerInvariant() + " wall"
            : "the wall at (" + FormatMm(Math.Round(at.X)) + ", " + FormatMm(Math.Round(at.Y)) + ")";
        return new WallPick { Host = host, Rings = rings, Run = run, Label = label, Walls = walls, Records = records, Graph = graph };
    }

    private WallSolid ResolveWallRecord(RhinoDoc doc, JObject parameters, RoomDetect.Pt? at, double tol)
    {
        var idToken = parameters?["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(idToken))
        {
            if (!Guid.TryParse(idToken, out var guid))
                throw new InvalidOperationException("Not a Forsk wall.");
            return ReadHostWall(doc, guid, requireVertical: true);
        }

        var selected = ListSelected(doc);
        if (selected.Count == 1 && string.Equals(GetForskKind(selected[0]), "wall", StringComparison.OrdinalIgnoreCase))
            return ReadHostWall(doc, selected[0].Id, requireVertical: true);

        var walls = new List<RhinoObject>();
        foreach (var obj in EnumerateDocObjects(doc))
            if (string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase) && !IsExistingUnderlay(doc, obj))
                walls.Add(obj);
        if (walls.Count == 0)
            throw new InvalidOperationException("No Forsk walls. Generate the 3D model first.");
        if (walls.Count == 1)
            return ReadHostWall(doc, walls[0].Id, requireVertical: true);
        if (!at.HasValue)
            throw new InvalidOperationException("Click one wall, then say it again.");

        RhinoObject nearest = null;
        var best = double.MaxValue;
        foreach (var wall in walls)
        {
            var rings = WallEdit.Rings(wall.Attributes.GetUserString("forsk:path"));
            if (rings == null) continue;
            var d = WallEdit.Distance(rings, at.Value, tol);
            if (d < best)
            {
                best = d;
                nearest = wall;
            }
        }
        if (nearest == null)
            throw new InvalidOperationException(MissingWallPathMessage);
        return ReadHostWall(doc, nearest.Id, requireVertical: true);
    }

    private static void WriteWallPath(RhinoDoc doc, Guid id, string path)
    {
        var obj = doc.Objects.FindId(id);
        if (obj?.Attributes == null)
            throw new InvalidOperationException("Not a Forsk wall.");
        var attr = obj.Attributes.Duplicate();
        attr.SetUserString("forsk:path", path);
        if (!doc.Objects.ModifyAttributes(id, attr, true))
            throw new InvalidOperationException("Could not write the wall's path.");
    }
}
