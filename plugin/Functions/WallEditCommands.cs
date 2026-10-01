using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// F3 wall edits on the soft-param record. A baked plan is one wall record, so
/// an edit works on one straight run inside its outline (WallEdit): the path
/// is written, the openings on the run go with it, and the host is rebuilt
/// from its path. A refused edit puts the record back and the document is
/// unchanged. Floor, roof and rooms do not follow (naive neighbours).
/// </summary>
public partial class RhinoMCPFunctions
{
    private sealed class WallPick
    {
        public WallSolid Host;
        public List<List<RoomDetect.Pt>> Rings;
        public WallEdit.Run Run;
        /// <summary>The run as the receipt names it: the north wall, or the wall at (x, y).</summary>
        public string Label;
    }

    [McpCommand("move_wall", ModelView = true)]
    public JObject MoveWall(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var distance = ReadOptionalDouble(parameters, "distance_mm");
        if (!distance.HasValue || distance.Value <= 0)
            throw new ArgumentException("distance_mm must be positive.");
        var pick = PickWallRun(doc, parameters, tol);
        if (!WallEdit.TryToward(pick.Run, parameters?["toward"]?.ToString(), distance.Value, out var by, out var why)
            || !WallEdit.TryMove(pick.Rings, pick.Run, by, tol, out var moved, out why))
            throw new InvalidOperationException(why);

        var host = pick.Host;
        var forskId = host.Attributes?.GetUserString("forsk:id");
        // The openings on the run go with it. One on a wall that meets the run stays,
        // and the run must not land on it or leave it past the end of its wall.
        var onRun = new List<RhinoObject>();
        foreach (var marker in MarkersOnHost(doc, host.Id, forskId))
        {
            var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
            if (!box.IsValid) continue;
            if (WallEdit.InBand(pick.Run, new RoomDetect.Pt(box.Center.X, box.Center.Y), tol))
                onRun.Add(marker);
            else if (!WallEdit.Clear(moved, pick.Run, by, new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y), tol))
                throw new InvalidOperationException("Not moved: " + (string.IsNullOrEmpty(marker.Name) ? "an opening" : marker.Name)
                    + " would sit in the moved wall or past the end of its own.");
        }

        var shift = new Vector3d(pick.Run.Normal.X * by, pick.Run.Normal.Y * by, 0);
        var undo = SnapshotWholeHost(doc, host.Id);
        var carried = new JArray();
        JObject rebuilt;
        try
        {
            WriteWallPath(doc, host.Id, WallEdit.Path(moved));
            foreach (var marker in onRun)
            {
                var brep = GetBrepFromObject(marker)?.DuplicateBrep();
                if (brep == null || !brep.Translate(shift) || !doc.Objects.Replace(marker.Id, brep))
                    throw new InvalidOperationException("Opening marker not found.");
                carried.Add(marker.Id.ToString());
            }
            rebuilt = RebuildHostWall(new JObject { ["id"] = host.Id.ToString() });
        }
        catch (Exception ex)
        {
            RollbackCommittedHost(doc, undo);
            throw new InvalidOperationException("Wall not moved. " + ex.Message, ex);
        }

        var hostId = Guid.TryParse(rebuilt?["host_id"]?.ToString(), out var parsed) ? parsed : host.Id;
        var heading = WallEdit.Heading(pick.Run, by);
        var with = carried.Count == 0 ? "" : carried.Count == 1 ? ", 1 opening with it" : ", " + carried.Count + " openings with it";
        var result = new JObject
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
            ["warnings"] = rebuilt?["warnings"] ?? new JArray(),
            ["ok"] = true,
            ["message"] = "Moved " + pick.Label + " of " + (string.IsNullOrEmpty(forskId) ? "the wall" : forskId) + " "
                + FormatMm(Math.Round(distance.Value)) + " mm " + heading + with
                + ". Floor, roof and rooms are unchanged."
        };
        var report = HostOpeningReport(doc, hostId, Guid.Empty, Point3d.Unset);
        if (report != null) result.Merge(report);
        doc.Views.Redraw();
        return result;
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
        WallEdit.Run run;
        string why;
        var ok = hasAt
            ? WallEdit.TryPick(rings, at, tol, out run, out why)
            : WallEdit.TryPickSide(rings, side, tol, out run, out why);
        if (!ok) throw new InvalidOperationException(why);
        var label = hasSide
            ? "the " + side.Trim().ToLowerInvariant() + " wall"
            : "the wall at (" + FormatMm(Math.Round(at.X)) + ", " + FormatMm(Math.Round(at.Y)) + ")";
        return new WallPick { Host = host, Rings = rings, Run = run, Label = label };
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
