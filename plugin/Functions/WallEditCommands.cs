using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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

    [McpCommand("delete_wall", ModelView = true)]
    public JObject DeleteWall(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var pick = PickWallRun(doc, parameters, tol);
        if (!WallEdit.TryDelete(pick.Rings, pick.Run, tol, out var left, out var why))
            throw new InvalidOperationException(why);

        var host = pick.Host;
        var forskId = host.Attributes?.GetUserString("forsk:id");
        // The openings in the run go with it. Every other one must still sit in the walls left.
        var orphans = new List<RhinoObject>();
        foreach (var marker in MarkersOnHost(doc, host.Id, forskId))
        {
            var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
            if (!box.IsValid) continue;
            if (left == null || WallEdit.InBand(pick.Run, new RoomDetect.Pt(box.Center.X, box.Center.Y), tol))
                orphans.Add(marker);
            else if (!WallEdit.Holds(left, new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y)))
                throw new InvalidOperationException("Not deleted: " + MarkerLabel(marker) + " would hang past the end of its wall.");
        }

        var undo = SnapshotWholeHost(doc, host.Id);
        JObject rebuilt = null;
        try
        {
            foreach (var marker in orphans)
                RemoveOpeningPieces(doc, marker.Id, undo.Removed);
            if (left == null)
            {
                var wall = doc.Objects.FindId(host.Id);
                if (wall == null || !TrackDelete(doc, wall, undo.Removed))
                    throw new InvalidOperationException("Could not delete the wall.");
            }
            else
            {
                WriteWallPath(doc, host.Id, WallEdit.Path(left));
                rebuilt = RebuildHostWall(new JObject { ["id"] = host.Id.ToString() });
            }
        }
        catch (Exception ex)
        {
            RollbackCommittedHost(doc, undo);
            throw new InvalidOperationException("Wall not deleted. " + ex.Message, ex);
        }

        var label = string.IsNullOrEmpty(forskId) ? "the wall" : forskId;
        var openings = OpeningsLine(orphans);
        var deleted = new JArray();
        foreach (var marker in orphans) deleted.Add(marker.Id.ToString());
        var hostId = left == null
            ? Guid.Empty
            : Guid.TryParse(rebuilt?["host_id"]?.ToString(), out var parsed) ? parsed : host.Id;
        var result = new JObject
        {
            ["host_id"] = hostId == Guid.Empty ? "" : hostId.ToString(),
            ["forsk_id"] = forskId ?? "",
            ["wall"] = pick.Label,
            ["record_deleted"] = left == null,
            ["openings_deleted"] = deleted,
            ["length_mm"] = pick.Run.Length,
            ["thickness"] = pick.Run.Thickness,
            ["path_points"] = rebuilt?["path_points"] ?? 0,
            ["holes"] = left == null ? 0 : left.Count - 1,
            ["warnings"] = rebuilt?["warnings"] ?? new JArray(),
            ["ok"] = true,
            ["message"] = (left == null
                    ? "Deleted " + label + ", a wall standing on its own"
                    : "Deleted " + pick.Label + " of " + label)
                + (openings.Length == 0 ? "" : ", and its " + openings) + ". Floor, roof and rooms are unchanged."
        };
        if (hostId != Guid.Empty)
        {
            var report = HostOpeningReport(doc, hostId, Guid.Empty, Point3d.Unset);
            if (report != null) result.Merge(report);
        }
        doc.Views.Redraw();
        return result;
    }

    [McpCommand("add_wall", ModelView = true)]
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
            JObject rebuilt;
            try
            {
                WriteWallPath(doc, host.Id, WallEdit.Path(added.Rings));
                rebuilt = RebuildHostWall(new JObject { ["id"] = host.Id.ToString() });
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
                + FormatMm(Math.Round(Length(added))) + " mm long. Floor, roof and rooms are unchanged.";
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
            result = AddedWall(added, id, forskId, thickness, height, false, WallEdit.Rings(path)[0].Count);
            result["message"] = "Added " + forskId + ", a " + FormatMm(Math.Round(thickness)) + " mm wall standing on its own, "
                + FormatMm(Math.Round(Length(added))) + " mm long and " + FormatMm(Math.Round(height)) + " mm high.";
        }
        doc.Views.Redraw();
        return result;
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
