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
    /// <summary>The selected wall on the graph move_wall uses. No Rhino types, so a drag can check it.</summary>
    internal sealed class SelectedDrag
    {
        public WallEdit.Run Run;
        public string Label;
        public List<List<RoomDetect.Pt>> Rings;
        public List<List<List<RoomDetect.Pt>>> Records;
        public WallJoins.Graph Graph;
    }

    sealed class WallPick
    {
        public WallSolid Host;
        /// <summary>The host record's own rings.</summary>
        public List<List<RoomDetect.Pt>> Rings;
        /// <summary>The run, picked on the cluster's shape.</summary>
        public WallEdit.Run Run;
        /// <summary>The run as the receipt names it: the north wall, or the rooms an inner wall bounds.</summary>
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
                HideOpeningMarker(doc, item.Marker.Id);
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
        var nb = ForskSpeech.Norwegian;
        var with = OpeningsWith(carried.Count, nb);
        var also = new List<string>();
        foreach (var f in moved.Followed) also.Add(f.Wall);
        var clause = WallFollowPlan.FollowedClause(also, nb);
        if (clause.Length > 0) with += "; " + clause;
        var idWord = string.IsNullOrEmpty(forskId) ? (nb ? "veggen" : "the wall") : forskId;
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
            ["followed"] = FollowedJson(doc, pick, moved.Followed),
            ["records"] = RecordIds(pick, moved.Records.Keys),
            ["rebuilt"] = followed,
            ["warnings"] = rebuilt?["warnings"] ?? new JArray(),
            ["ok"] = true,
            ["message"] = (nb
                    ? "Flyttet " + pick.Label + " av " + idWord + " "
                        + FormatMm(Math.Round(distance.Value)) + " mm mot " + Compass(heading, true)
                    : "Moved " + pick.Label + " of " + idWord + " "
                        + FormatMm(Math.Round(distance.Value)) + " mm " + heading)
                + with + ". " + followed
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

        var nb = ForskSpeech.Norwegian;
        var label = string.IsNullOrEmpty(forskId) ? (nb ? "veggen" : "the wall") : forskId;
        var openingMarkers = new List<RhinoObject>();
        var deleted = new JArray();
        foreach (var item in orphans)
        {
            openingMarkers.Add(item.Marker);
            deleted.Add(item.Marker.Id.ToString());
        }
        var openings = OpeningsLine(openingMarkers, nb);
        var joined = new List<string>();
        foreach (var f in cut.Followed) joined.Add(f.Wall);
        var joinedPhrase = WallFollowPlan.Walls(joined, nb);
        var hostId = namedGone ? Guid.Empty : rebuiltIds.TryGetValue(named, out var renamed) ? renamed : named;
        var left = namedGone ? null : namedLeft ?? pick.Records[namedIndex];
        var wholeRecord = namedGone && pick.Graph.Records.Count == 1;
        string deletedHead;
        if (nb)
        {
            deletedHead = wholeRecord
                ? "Slettet " + label + ", en vegg som står for seg selv"
                : namedGone
                    ? "Slettet " + pick.Label + ", " + label
                    : "Slettet " + pick.Label + " av " + label;
            if (openings.Length > 0) deletedHead += ", og dens " + openings;
            if (joinedPhrase.Length > 0) deletedHead += "; den hang sammen med " + joinedPhrase;
        }
        else
        {
            deletedHead = wholeRecord
                ? "Deleted " + label + ", a wall standing on its own"
                : namedGone
                    ? "Deleted " + pick.Label + ", " + label
                    : "Deleted " + pick.Label + " of " + label;
            if (openings.Length > 0) deletedHead += ", and its " + openings;
            if (joinedPhrase.Length > 0) deletedHead += "; it was joined to " + joinedPhrase;
        }
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
            ["followed"] = FollowedJson(doc, pick, cut.Followed),
            ["records"] = RecordIds(pick, cut.Records.Keys),
            ["rebuilt"] = followed,
            ["warnings"] = rebuilt?["warnings"] ?? new JArray(),
            ["ok"] = true,
            ["message"] = deletedHead + ". " + followed
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

        var nb = ForskSpeech.Norwegian;
        var count = (moved["openings_moved"] as JArray)?.Count ?? 0;
        var with = OpeningsWith(count, nb);
        var names = new List<string>();
        foreach (var f in moved["followed"] as JArray ?? new JArray()) names.Add(f["wall"]?.ToString());
        var clause = WallFollowPlan.FollowedClause(names, nb);
        if (clause.Length > 0) with += "; " + clause;
        moved["room"] = name;
        moved["room_id"] = room.Id.ToString();
        moved["side"] = side;
        moved["way"] = way;
        var mm = FormatMm(Math.Round(distance.Value));
        moved["message"] = nb
            ? (way == "out" ? "Skjøv " : "Dro ") + Compass(side, true) + "siden av " + name + " " + mm + " mm "
                + (way == "out" ? "ut" : "inn") + with + ". " + moved["rebuilt"]
            : (way == "out" ? "Pushed " : "Pulled ") + name + (name.EndsWith("s", StringComparison.Ordinal) ? "' " : "'s ")
                + side + " side " + mm + " mm " + way + with + ". " + moved["rebuilt"];
        return moved;
    }

    private sealed class SplitPlan
    {
        public RhinoObject Wall;
        public string ForskId;
        public List<WallSplit.Piece> Pieces;
        /// <summary>Each opening on the record and the piece that holds it.</summary>
        public List<(RhinoObject Marker, int Piece)> Openings = new List<(RhinoObject, int)>();
    }

    /// <summary>
    /// Selection S1 for a file made before it: each whole wall record (more
    /// than one straight run) becomes one record per run (WallSplit), with the
    /// same union, so the join graph, the rooms and the floor read as before.
    /// Each opening goes to the piece that holds it, and that piece is rebuilt
    /// with its openings cut; the old record goes. One undo. A record the split
    /// refuses, or one with an opening across two pieces, stays whole and the
    /// receipt says why. A failure puts every record back.
    /// </summary>
    [McpCommand("split_walls", ModelView = true, Map = MapEdit.Wall)]
    public JObject SplitWalls(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var idToken = parameters?["id"]?.ToString();
        var given = !string.IsNullOrWhiteSpace(idToken);
        var guid = Guid.Empty;
        if (given && !Guid.TryParse(idToken, out guid))
            throw new InvalidOperationException("Not a Forsk wall.");

        var walls = new List<RhinoObject>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase) || IsExistingUnderlay(doc, obj))
                continue;
            if (given && obj.Id != guid) continue;
            walls.Add(obj);
        }
        if (walls.Count == 0)
            throw new InvalidOperationException(given ? "Not a Forsk wall." : "No Forsk walls. Generate the 3D model first.");

        var plans = new List<SplitPlan>();
        var refused = new JArray();
        foreach (var wall in walls)
        {
            var forskId = wall.Attributes.GetUserString("forsk:id");
            if (string.IsNullOrEmpty(forskId)) forskId = wall.Name ?? "a wall";
            var rings = WallEdit.Rings(wall.Attributes.GetUserString("forsk:path"));
            string why = null;
            var graph = rings == null ? null : WallJoins.Build(new List<List<List<RoomDetect.Pt>>> { rings }, new List<int> { 0 }, tol);
            var pieces = graph == null ? null : WallSplit.Pieces(graph, tol, out why);
            if (pieces == null)
            {
                refused.Add(new JObject { ["forsk_id"] = forskId, ["why"] = why ?? MissingWallPathMessage });
                continue;
            }
            if (pieces.Count < 2) continue;
            var plan = new SplitPlan { Wall = wall, ForskId = forskId, Pieces = pieces };
            string across = null;
            foreach (var marker in MarkersOnHost(doc, wall.Id, wall.Attributes.GetUserString("forsk:id")))
            {
                var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (!box.IsValid) continue;
                var footprint = new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y);
                var holder = pieces.FindIndex(p => WallEdit.Holds(new List<List<RoomDetect.Pt>> { p.Ring }, footprint));
                if (holder < 0)
                {
                    across = MarkerLabel(marker);
                    break;
                }
                plan.Openings.Add((marker, holder));
            }
            if (across != null)
            {
                refused.Add(new JObject { ["forsk_id"] = forskId, ["why"] = "Not split: " + across + " stands across two of its walls." });
                continue;
            }
            plans.Add(plan);
        }

        var split = new JArray();
        var moved = 0;
        if (plans.Count > 0)
        {
            var undos = new List<HostUndo>();
            var added = new List<Guid>();
            try
            {
                foreach (var plan in plans)
                {
                    var undo = SnapshotWholeHost(doc, plan.Wall.Id);
                    undos.Add(undo);
                    var old = plan.Wall;
                    var extent = old.Geometry.GetBoundingBox(true);
                    var height = ParseMm(old.Attributes.GetUserString("forsk:height")) ?? extent.Max.Z - extent.Min.Z;
                    var hosts = new List<Guid>();
                    var into = new JArray();
                    foreach (var piece in plan.Pieces)
                    {
                        var path = WallEdit.Path(new List<List<RoomDetect.Pt>> { piece.Ring });
                        var solid = PrepareFreshSolid(ExtrudeFromPath(path, height, tol, new JArray()), tol, out var diagnostic);
                        if (solid == null)
                            throw new InvalidOperationException("A piece of " + plan.ForskId + " did not extrude. " + diagnostic);
                        if (Math.Abs(extent.Min.Z) > 1e-9) solid.Translate(new Vector3d(0, 0, extent.Min.Z));
                        var number = NextWallNumber(doc);
                        var forskId = FormatStableId("w", number);
                        var attr = old.Attributes.Duplicate();
                        attr.Name = "wall-" + number.ToString("D2", CultureInfo.InvariantCulture);
                        attr.SetUserString("forsk:id", forskId);
                        attr.SetUserString("forsk:path", path);
                        var read = MeasureWallThickness(path, solid, tol);
                        if (read.Millimetres > 0) attr.SetUserString("forsk:thickness", FormatMm(read.Millimetres));
                        var id = doc.Objects.AddBrep(solid, attr);
                        if (id == Guid.Empty)
                            throw new InvalidOperationException("Rhino did not take a piece of " + plan.ForskId + ".");
                        added.Add(id);
                        hosts.Add(id);
                        into.Add(forskId);
                    }
                    foreach (var opening in plan.Openings)
                    {
                        RehostOpening(doc, opening.Marker, hosts[opening.Piece]);
                        var block = doc.Objects.FindId(FindOpeningBlock(doc, opening.Marker.Id));
                        if (block != null) RehostOpening(doc, block, hosts[opening.Piece]);
                    }
                    if (!doc.Objects.Delete(old.Id, true))
                        throw new InvalidOperationException("Could not delete " + plan.ForskId + ".");
                    var cut = new HashSet<int>();
                    foreach (var opening in plan.Openings)
                    {
                        if (!cut.Add(opening.Piece)) continue;
                        var result = RebuildHostWall(new JObject { ["id"] = hosts[opening.Piece].ToString() });
                        undo.NewBlocks.AddRange(GuidList(result?["block_ids"] as JArray));
                        if (Guid.TryParse(result?["host_id"]?.ToString(), out var after) && after != hosts[opening.Piece])
                            added.Add(after);
                    }
                    moved += plan.Openings.Count;
                    split.Add(new JObject { ["forsk_id"] = plan.ForskId, ["into"] = into, ["openings"] = plan.Openings.Count });
                }
            }
            catch (Exception ex)
            {
                foreach (var id in added)
                    if (doc.Objects.FindId(id) != null) doc.Objects.Delete(id, true);
                for (var i = undos.Count - 1; i >= 0; i--) RollbackCommittedHost(doc, undos[i]);
                throw new InvalidOperationException("Walls not split. " + ex.Message, ex);
            }
        }
        else if (given && refused.Count > 0)
            throw new InvalidOperationException(refused[0]["why"]?.ToString());

        var walled = 0;
        foreach (var item in split) walled += ((JArray)item["into"]).Count;
        var parts = new List<string>();
        foreach (var item in split)
        {
            var into = (JArray)item["into"];
            parts.Add(item["forsk_id"] + " into " + into.Count + " walls, " + into[0] + " to " + into[into.Count - 1]);
        }
        var message = split.Count == 0
            ? (refused.Count == 0 ? "Every wall is one straight run already." : "No wall was split.")
            : "Split " + string.Join("; ", parts) + "."
                + (moved == 0 ? "" : moved == 1 ? " 1 opening went to the wall that holds it." : " " + moved + " openings went to the walls that hold them.");
        foreach (var item in refused)
            message += " " + item["forsk_id"] + " stays one record. " + item["why"];
        doc.Views.Redraw();
        return new JObject
        {
            ["split"] = split,
            ["walls"] = walled,
            ["openings_moved"] = moved,
            ["refused"] = refused,
            ["ok"] = true,
            ["message"] = message
        };
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

    /// <summary>The room given by id, else the one room selected. A floor plate stands for its room.</summary>
    private static RhinoObject ResolveRoom(RhinoDoc doc, JObject parameters)
    {
        var idToken = parameters?["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(idToken))
        {
            var obj = ResolveRoomHandle(doc, Guid.TryParse(idToken, out var guid) ? doc.Objects.FindId(guid) : null);
            if (obj == null || !IsRoomRecord(obj))
                throw new InvalidOperationException("Not a Forsk room.");
            return obj;
        }
        var rooms = new List<RhinoObject>();
        foreach (var picked in ListSelected(doc))
        {
            var obj = ResolveRoomHandle(doc, picked);
            if (IsRoomRecord(obj) && !rooms.Exists(r => r.Id == obj.Id)) rooms.Add(obj);
        }
        if (rooms.Count != 1)
            throw new InvalidOperationException("Select one room, then say it again.");
        return rooms[0];
    }

    /// <summary>
    /// The walls that followed. <c>wall</c> stays the graph name so a later
    /// phrase can still tell a side from an inner wall. <c>label</c> is what
    /// the review card prints: the side, or the rooms it bounds.
    /// </summary>
    private JArray FollowedJson(RhinoDoc doc, WallPick pick, IEnumerable<WallJoins.Followed> followed)
    {
        var nb = ForskSpeech.Norwegian;
        var rooms = RoomRings(doc);
        var list = new JArray();
        foreach (var f in followed)
        {
            var record = f.Records.Count > 0 ? pick.Walls[f.Records[0]].Attributes?.GetUserString("forsk:id") : null;
            list.Add(new JObject
            {
                ["forsk_id"] = record ?? "",
                ["wall"] = f.Wall,
                ["label"] = RunLabel(pick.Graph, NameIndex(pick.Graph, f.Wall), rooms, nb),
                ["change_mm"] = f.ChangeMm
            });
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
    private static string OpeningsLine(List<RhinoObject> markers, bool nb)
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
        if (nb)
        {
            if (doors > 0) parts.Add(doors + (doors == 1 ? " dør" : " dører"));
            if (windows > 0) parts.Add(windows + (windows == 1 ? " vindu" : " vinduer"));
            return string.Join(" og ", parts);
        }
        if (doors > 0) parts.Add(doors + (doors == 1 ? " door" : " doors"));
        if (windows > 0) parts.Add(windows + (windows == 1 ? " window" : " windows"));
        return string.Join(" and ", parts);
    }

    /// <summary>", 1 opening with it", or nothing.</summary>
    private static string OpeningsWith(int count, bool nb)
    {
        if (count <= 0) return "";
        if (nb) return count == 1 ? ", én åpning med" : ", " + count.ToString(CultureInfo.InvariantCulture) + " åpninger med";
        return count == 1 ? ", 1 opening with it" : ", " + count.ToString(CultureInfo.InvariantCulture) + " openings with it";
    }

    static string Compass(string side, bool nb)
    {
        if (!nb || string.IsNullOrEmpty(side)) return side ?? "";
        switch (side)
        {
            case "north": return "nord";
            case "south": return "sør";
            case "east": return "øst";
            case "west": return "vest";
            default: return side;
        }
    }

    /// <summary>
    /// The wall record and the run in it. The record is the id, else the one
    /// selected wall, else the wall nearest at, else the only wall. The run is
    /// side (an outer wall) or the face nearest at; with neither, the record's
    /// one run when it holds one (selection S4).
    /// </summary>
    private WallPick PickWallRun(RhinoDoc doc, JObject parameters, double tol)
    {
        var pick = Gather(doc, parameters, tol, out var hasAt, out var hasSide, out var at, out var side);
        if (!hasAt && !hasSide)
        {
            // Selection S4: a record of one run is that run.
            var only = WallJoins.RunIn(pick.Graph, pick.Rings);
            if (only < 0)
                throw new ArgumentException("Say which wall: a side (north, south, east or west) or a point at [x, y] in mm. This record holds more than one wall; Split walls for picking makes each its own.");
            pick.Run = pick.Graph.Runs[only];
            pick.Label = pick.Graph.Names[only];
            return pick;
        }
        var ok = hasAt
            ? WallEdit.TryPick(pick.Graph.Shape, at, tol, out var run, out var why)
            : WallEdit.TryPickSide(pick.Graph.Shape, side, tol, out run, out why);
        if (!ok) throw new InvalidOperationException(why);
        pick.Run = run;
        pick.Label = RunLabel(pick.Graph, pick.Graph.Find(run, tol), RoomRings(doc), ForskSpeech.Norwegian);
        return pick;
    }

    /// <summary>
    /// The selected wall on the same graph move_wall uses. Run stays empty
    /// when the record holds several runs, so a drag can say why.
    /// </summary>
    internal SelectedDrag SelectedWall(RhinoDoc doc)
    {
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var pick = Gather(doc, new JObject(), tol, out _, out _, out _, out _);
        var chosen = new SelectedDrag
        {
            Rings = pick.Rings,
            Records = pick.Records,
            Graph = pick.Graph
        };
        if (pick.Graph == null) return chosen;
        var only = WallJoins.RunIn(pick.Graph, pick.Rings);
        if (only < 0) return chosen;
        chosen.Run = pick.Graph.Runs[only];
        chosen.Label = RunLabel(pick.Graph, only, RoomRings(doc), ForskSpeech.Norwegian);
        return chosen;
    }

    /// <summary>The host, its cluster and its rings. The run is chosen by the caller.</summary>
    private WallPick Gather(RhinoDoc doc, JObject parameters, double tol, out bool hasAt, out bool hasSide, out RoomDetect.Pt at, out string side)
    {
        side = parameters?["side"]?.ToString();
        hasSide = !string.IsNullOrWhiteSpace(side);
        var atToken = parameters?["at"];
        hasAt = atToken != null && atToken.Type != JTokenType.Null;
        at = default;
        if (hasAt && !TryReadPoint(atToken, out at))
            throw new ArgumentException("at is [x, y] in mm.");
        if (hasAt && hasSide)
            throw new ArgumentException("Give side or at, not both.");

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
        return new WallPick { Host = host, Rings = rings, Walls = walls, Records = records, Graph = graph };
    }

    /// <summary>Room markers as rings the receipt can name an inner wall by.</summary>
    List<WallFollowPlan.NamedRoom> RoomRings(RhinoDoc doc)
    {
        var rooms = new List<WallFollowPlan.NamedRoom>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsRoomRecord(obj)) continue;
            var outline = RoomMarkerOutline(obj);
            var ring = outline == null ? null : PathPoints(outline);
            if (ring == null || ring.Count < 3) continue;
            var name = obj.Attributes?.GetUserString("forsk:room_name");
            if (string.IsNullOrWhiteSpace(name)) name = obj.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            rooms.Add(new WallFollowPlan.NamedRoom { Name = name.Trim(), Ring = ring });
        }
        return rooms;
    }

    /// <summary>A run for the receipt: its side, else the rooms on its faces, else "inner wall 3".</summary>
    static string RunLabel(WallJoins.Graph graph, int index, IList<WallFollowPlan.NamedRoom> rooms, bool nb)
    {
        if (graph != null && index >= 0 && index < graph.Names.Count && WallFollowPlan.IsSide(graph.Names[index], out var side))
            return WallFollowPlan.Side(side, nb);
        IList<string> beside = null;
        if (graph != null && index >= 0 && index < graph.Runs.Count)
            beside = WallFollowPlan.Beside(graph.Runs[index], rooms);
        var ordinal = graph == null ? 1 : WallFollowPlan.InnerOrdinal(graph.Names, index);
        return WallFollowPlan.InnerName(beside, ordinal, nb);
    }

    static int NameIndex(WallJoins.Graph graph, string wall)
    {
        if (graph?.Names == null || string.IsNullOrEmpty(wall)) return -1;
        for (var i = 0; i < graph.Names.Count; i++)
            if (graph.Names[i] == wall) return i;
        return -1;
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
