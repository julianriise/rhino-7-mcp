using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Rhino;
using RhinoMCPPlugin.Forsk;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// WF: a wall edited by one of its faces. An end face changes the length, a
/// side face moves the wall (as move_wall) or, with thickness_mm, moves that
/// face alone. The edit writes the records that changed and rebuilds them as
/// move_wall does, in one go: a refused or failed rebuild puts every record
/// back.
/// </summary>
public partial class RhinoMCPFunctions
{
    private sealed class WallCommit
    {
        public Guid HostId;
        public string ForskId;
        public JObject Rebuilt;
        public JArray Carried = new JArray();
        /// <summary>The floor, roof and rooms that followed, as one sentence.</summary>
        public string Followed;
    }

    [McpCommand("edit_wall_face", ModelView = true, Map = MapEdit.Wall)]
    public JObject EditWallFace(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        if (!TryReadPoint(parameters?["at"], out var at))
            throw new ArgumentException("at is [x, y] in mm on the face.");
        var distance = ReadOptionalDouble(parameters, "distance_mm");
        var thickness = ReadOptionalDouble(parameters, "thickness_mm");
        if (distance.HasValue == thickness.HasValue)
            throw new ArgumentException("Give distance_mm or thickness_mm, not both.");
        WallFace.Kind? only = null;
        var faceWord = parameters?["face"]?.ToString();
        if (!string.IsNullOrWhiteSpace(faceWord))
        {
            if (string.Equals(faceWord, "end", StringComparison.OrdinalIgnoreCase)) only = WallFace.Kind.End;
            else if (string.Equals(faceWord, "side", StringComparison.OrdinalIgnoreCase)) only = WallFace.Kind.Side;
            else throw new ArgumentException("face is end or side.");
        }
        if (thickness.HasValue) only = WallFace.Kind.Side;

        var gather = new JObject { ["at"] = new JArray(at.X, at.Y) };
        if (parameters?["id"] != null) gather["id"] = parameters["id"];
        var pick = Gather(doc, gather, tol, out _, out _, out _, out _);
        if (pick.Graph == null)
            throw new InvalidOperationException("This wall does not read as straight runs, so it has no faces to edit.");
        if (!WallFace.TryPick(pick.Graph, at, null, 0, tol, out var hit, out var why, only))
            throw new InvalidOperationException(why);
        pick.Run = hit.Run;
        pick.Label = RunLabel(pick.Graph, hit.Index, RoomRings(doc), ForskSpeech.Norwegian);

        // A side face dragged as a whole is move_wall, with its receipt.
        if (!thickness.HasValue && hit.Kind != WallFace.Kind.End)
        {
            if (Math.Abs(distance.Value) < tol)
                throw new ArgumentException("distance_mm is not 0.");
            var by = WallFace.Across(hit, distance.Value);
            var move = new JObject
            {
                ["at"] = new JArray(hit.Middle.X, hit.Middle.Y),
                ["toward"] = WallEdit.Heading(hit.Run, by),
                ["distance_mm"] = Math.Abs(distance.Value)
            };
            if (parameters?["id"] != null) move["id"] = parameters["id"];
            var moved = MoveWall(move);
            moved["face"] = "side";
            return moved;
        }

        var nb = ForskSpeech.Norwegian;
        WallJoins.Moved edit;
        var shift = Vector3d.Zero;
        double change;
        if (hit.Kind == WallFace.Kind.End)
        {
            change = distance.Value;
            if (!WallFace.TryStretch(pick.Records, pick.Graph, hit, change, tol, out edit, out why))
                throw new InvalidOperationException(why);
        }
        else
        {
            if (!WallFace.TryThicken(pick.Records, pick.Graph, hit, thickness.Value, tol, out edit, out change, out why))
                throw new InvalidOperationException(why);
            // The openings on the wall stay in its middle.
            shift = new Vector3d(hit.Run.Normal.X * change / 2.0, hit.Run.Normal.Y * change / 2.0, 0);
        }

        // Openings keep their place; one the shorter wall no longer holds refuses the edit.
        var refusal = hit.Kind == WallFace.Kind.End
            ? (change > 0 ? "Not lengthened: " : "Not shortened: ")
            : "Thickness not changed: ";
        var commit = CommitWallEdit(doc, pick, edit, shift, (marker, box, onRun) =>
        {
            var moved = onRun && !shift.IsZero ? Shifted(box, shift) : box;
            return WallEdit.Holds(edit.Shape, moved, tol)
                ? null
                : refusal + MarkerLabel(marker) + " would hang past the end of the wall. Move it or make it narrower first.";
        }, hit.Kind == WallFace.Kind.End ? "Length not changed. " : "Thickness not changed. ");

        var idWord = string.IsNullOrEmpty(commit.ForskId) ? (nb ? "veggen" : "the wall") : commit.ForskId;
        var also = new List<string>();
        foreach (var f in edit.Followed) also.Add(f.Wall);
        var clause = WallFollowPlan.FollowedClause(also, nb);
        string sentence;
        if (hit.Kind == WallFace.Kind.End)
        {
            var length = FormatMm(Math.Round(WallFace.After(hit, change)));
            var by = FormatMm(Math.Round(Math.Abs(change)));
            sentence = nb
                ? (change > 0 ? "Forlenget " : "Forkortet ") + pick.Label + " av " + idWord + " med " + by + " mm, nå " + length + " mm lang"
                : (change > 0 ? "Lengthened " : "Shortened ") + pick.Label + " of " + idWord + " by " + by + " mm, now " + length + " mm long";
        }
        else
        {
            var mm = FormatMm(Math.Round(thickness.Value));
            sentence = nb
                ? "Ga " + pick.Label + " av " + idWord + " en tykkelse på " + mm + " mm; motsatt flate står"
                : "Made " + pick.Label + " of " + idWord + " " + mm + " mm thick; the face across stays";
        }
        if (clause.Length > 0) sentence += "; " + clause;
        var result = new JObject
        {
            ["host_id"] = commit.HostId.ToString(),
            ["forsk_id"] = commit.ForskId ?? "",
            ["wall"] = pick.Label,
            ["face"] = hit.Kind == WallFace.Kind.End ? "end" : "side",
            ["length_before"] = hit.Run.Length,
            ["length_mm"] = hit.Kind == WallFace.Kind.End ? WallFace.After(hit, change) : hit.Run.Length,
            ["thickness_before"] = hit.Run.Thickness,
            ["thickness"] = hit.Kind == WallFace.Kind.End ? hit.Run.Thickness : thickness.Value,
            ["path_points"] = commit.Rebuilt?["path_points"],
            ["openings_moved"] = commit.Carried,
            ["followed"] = FollowedJson(doc, pick, edit.Followed),
            ["records"] = RecordIds(pick, edit.Records.Keys),
            ["rebuilt"] = commit.Followed,
            ["warnings"] = commit.Rebuilt?["warnings"] ?? new JArray(),
            ["ok"] = true,
            ["message"] = sentence + ". " + commit.Followed
        };
        var report = HostOpeningReport(doc, commit.HostId, Guid.Empty, Point3d.Unset);
        if (report != null) result.Merge(report);
        doc.Views.Redraw();
        return result;
    }

    /// <summary>
    /// The records an edit changed, written and rebuilt from their paths. The
    /// openings in the run's band move by shift (none when it is zero). Every
    /// opening in the cluster is asked first: refuse returns why it stops the
    /// edit, or null. Then the floor, roof and rooms follow the new shape. Any
    /// failure puts every touched record back and throws, led by failed.
    /// </summary>
    private WallCommit CommitWallEdit(RhinoDoc doc, WallPick pick, WallJoins.Moved moved, Vector3d shift,
        Func<RhinoObject, RoomDetect.Box, bool, string> refuse, string failed)
    {
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var doorsBefore = DoorGapBoxes(doc);
        var onRun = new List<(int Wall, RhinoObject Marker)>();
        foreach (var i in pick.Graph.Records)
        {
            var wall = pick.Walls[i];
            foreach (var marker in MarkersOnHost(doc, wall.Id, wall.Attributes?.GetUserString("forsk:id")))
            {
                var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (!box.IsValid) continue;
                var inBand = WallEdit.InBand(pick.Run, new RoomDetect.Pt(box.Center.X, box.Center.Y), tol);
                var why = refuse(marker, new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y), inBand);
                if (why != null) throw new InvalidOperationException(why);
                if (inBand) onRun.Add((i, marker));
            }
        }

        // The record the run stands in names it; else the wall picked.
        var owner = pick.Graph.Records.FindIndex(i => WallEdit.InRegion(pick.Records[i], WallJoins.Middle(pick.Run)));
        var named = owner >= 0 ? pick.Walls[pick.Graph.Records[owner]].Id : pick.Host.Id;
        var commit = new WallCommit { ForskId = doc.Objects.FindId(named)?.Attributes?.GetUserString("forsk:id") };
        var touched = new List<int>(moved.Records.Keys);
        if (!shift.IsZero)
            foreach (var item in onRun)
                if (!touched.Contains(item.Wall)) touched.Add(item.Wall);

        var sourceLayer = pick.Host.Attributes?.GetUserString("forsk:source_layer");
        var undos = new List<HostUndo>();
        var rebuiltIds = new Dictionary<Guid, Guid>();
        try
        {
            foreach (var i in touched) undos.Add(SnapshotWholeHost(doc, pick.Walls[i].Id));
            foreach (var record in moved.Records)
                WriteWallPath(doc, pick.Walls[record.Key].Id, WallEdit.Path(record.Value));
            if (!shift.IsZero)
                foreach (var item in onRun)
                {
                    var brep = GetBrepFromObject(item.Marker)?.DuplicateBrep();
                    if (brep == null || !brep.Translate(shift) || !ReplaceOpeningMarker(doc, item.Marker.Id, brep))
                        throw new InvalidOperationException("Opening marker not found.");
                    commit.Carried.Add(item.Marker.Id.ToString());
                }
            foreach (var undo in undos)
            {
                var result = RebuildUnder(undo);
                rebuiltIds[undo.HostBefore] = undo.HostAfter;
                if (undo.HostBefore == named) commit.Rebuilt = result;
            }
            commit.Followed = FollowNeighbours(doc, sourceLayer, pick.Graph.Shape, moved.Shape, doorsBefore);
        }
        catch (Exception ex)
        {
            for (var i = undos.Count - 1; i >= 0; i--) RollbackCommittedHost(doc, undos[i]);
            throw new InvalidOperationException(failed + ex.Message, ex);
        }
        commit.HostId = rebuiltIds.TryGetValue(named, out var renamed) ? renamed : named;
        return commit;
    }

    static RoomDetect.Box Shifted(RoomDetect.Box box, Vector3d shift) =>
        new RoomDetect.Box(box.MinX + shift.X, box.MinY + shift.Y, box.MaxX + shift.X, box.MaxY + shift.Y);
}
