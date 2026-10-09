using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The Properties panel's wall fields, as a typed tool the chat can use too.
/// thickness_mm and length_mm change one straight wall with no face clicked
/// (WallFace.TrySetThickness, TrySetLength) and commit as edit_wall_face
/// does. height_mm sets the storey: every wall, with the flat roof lifted or
/// lowered on top of them, so the rooms' ceiling height follows.
/// </summary>
public partial class RhinoMCPFunctions
{
    /// <summary>The tallest a storey's walls can be, mm.</summary>
    const double MaxWallHeightMm = 10000;

    [McpCommand("set_wall", ModelView = true, Map = MapEdit.Wall)]
    public JObject SetWall(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var thickness = ReadOptionalDouble(parameters, "thickness_mm");
        var length = ReadOptionalDouble(parameters, "length_mm");
        var height = ReadOptionalDouble(parameters, "height_mm");
        var given = (thickness.HasValue ? 1 : 0) + (length.HasValue ? 1 : 0) + (height.HasValue ? 1 : 0);
        if (given != 1)
            throw new ArgumentException("Give one of thickness_mm, length_mm or height_mm.");
        if (height.HasValue) return SetStoreyHeight(doc, height.Value);

        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var gather = new JObject();
        if (parameters?["id"] != null && parameters["id"].Type != JTokenType.Null) gather["id"] = parameters["id"];
        var pick = Gather(doc, gather, tol, out _, out _, out _, out _);
        if (pick.Graph == null)
            throw new InvalidOperationException("This wall does not read as straight runs, so it cannot be changed here.");
        var record = pick.Walls.FindIndex(w => w.Id == pick.Host.Id);
        var index = record < 0 ? -1 : WallJoins.RunIn(pick.Graph, pick.Records[record]);
        var hostName = pick.Host.Attributes?.GetUserString("forsk:id");
        if (index < 0)
            throw new InvalidOperationException((string.IsNullOrEmpty(hostName) ? "This wall" : hostName)
                + " holds more than one wall. Split walls for picking, then pick one wall.");
        var nb = ForskSpeech.Norwegian;
        pick.Run = pick.Graph.Runs[index];
        pick.Label = RunLabel(pick.Graph, index, RoomRings(doc), nb);

        WallJoins.Moved edit;
        string why;
        var shift = Vector3d.Zero;
        var before = pick.Run;
        if (thickness.HasValue)
        {
            // A front door drawn as a gap opens the outline; close it at its door before asking which face looks out.
            var outline = WallFollowPlan.CloseDoorGaps(pick.Graph.Shape[0], DoorGapBoxes(doc), tol);
            if (!WallFace.TrySetThickness(pick.Records, pick.Graph, index, thickness.Value, tol, out edit, out var across, out why, outline))
                throw new InvalidOperationException(why);
            shift = new Vector3d(before.Normal.X * across, before.Normal.Y * across, 0);
        }
        else if (!WallFace.TrySetLength(pick.Records, pick.Graph, index, length.Value, tol, out edit, out _, out why))
            throw new InvalidOperationException(why);

        var refusal = thickness.HasValue ? "Thickness not changed: " : length.Value > before.Length ? "Not lengthened: " : "Not shortened: ";
        var commit = CommitWallEdit(doc, pick, edit, shift, (marker, box, onRun) =>
        {
            var moved = onRun && !shift.IsZero ? Shifted(box, shift) : box;
            return WallEdit.Holds(edit.Shape, moved, tol)
                ? null
                : refusal + MarkerLabel(marker) + " would hang past the end of the wall. Move it or make it narrower first.";
        }, thickness.HasValue ? "Thickness not changed. " : "Length not changed. ");

        var idWord = string.IsNullOrEmpty(commit.ForskId) ? (nb ? "veggen" : "the wall") : commit.ForskId;
        var also = new List<string>();
        foreach (var f in edit.Followed) also.Add(f.Wall);
        var clause = WallFollowPlan.FollowedClause(also, nb);
        string sentence;
        if (thickness.HasValue)
        {
            var mm = FormatMm(Math.Round(thickness.Value));
            var kept = shift.IsZero ? (nb ? "midtlinjen står" : "its centreline stays") : (nb ? "ytterflaten står" : "the outside face stays");
            sentence = nb
                ? "Ga " + pick.Label + " av " + idWord + " en tykkelse på " + mm + " mm; " + kept
                : "Made " + pick.Label + " of " + idWord + " " + mm + " mm thick; " + kept;
        }
        else
        {
            var mm = FormatMm(Math.Round(length.Value));
            sentence = nb
                ? "Ga " + pick.Label + " av " + idWord + " en lengde på " + mm + " mm"
                : "Made " + pick.Label + " of " + idWord + " " + mm + " mm long";
        }
        if (clause.Length > 0) sentence += "; " + clause;
        var result = new JObject
        {
            ["host_id"] = commit.HostId.ToString(),
            ["forsk_id"] = commit.ForskId ?? "",
            ["wall"] = pick.Label,
            ["length_before"] = before.Length,
            ["length_mm"] = length ?? before.Length,
            ["thickness_before"] = before.Thickness,
            ["thickness"] = thickness ?? before.Thickness,
            ["kept"] = thickness.HasValue ? (shift.IsZero ? "centreline" : "outside") : "start",
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
    /// Every generated wall made height tall from its foot, rebuilt from its
    /// path with its openings cut again, and every flat roof moved by the
    /// change in the walls' top. An opening whose head would reach the top
    /// refuses it. Any failure puts every wall and roof back.
    /// </summary>
    JObject SetStoreyHeight(RhinoDoc doc, double height)
    {
        if (height <= 0 || height > MaxWallHeightMm)
            throw new ArgumentException("height_mm is above 0 and at most " + FormatMm(MaxWallHeightMm) + " mm.");
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var walls = new List<RhinoObject>();
        var top = double.MinValue;
        var foot = double.MaxValue;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase) || IsExistingUnderlay(doc, obj)) continue;
            if (string.IsNullOrWhiteSpace(obj.Attributes?.GetUserString("forsk:path"))) continue;
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
            if (!box.IsValid) continue;
            walls.Add(obj);
            top = Math.Max(top, box.Max.Z);
            foot = Math.Min(foot, box.Min.Z);
        }
        if (walls.Count == 0) throw new InvalidOperationException("No walls to change.");
        var nb = ForskSpeech.Norwegian;
        var change = foot + height - top;
        var same = walls.TrueForAll(w =>
        {
            var box = w.Geometry.GetBoundingBox(true);
            return Math.Abs(box.Max.Z - box.Min.Z - height) < tol;
        });
        if (same)
            throw new InvalidOperationException("Not changed: the walls are already " + FormatMm(Math.Round(height)) + " mm high.");

        foreach (var wall in walls)
            foreach (var marker in MarkersOnHost(doc, wall.Id, wall.Attributes.GetUserString("forsk:id")))
            {
                var head = ReadOpeningRecord(marker).Head;
                if (head >= height - tol)
                    throw new InvalidOperationException("Height not changed: " + MarkerLabel(marker) + " has its head at "
                        + FormatMm(Math.Round(head)) + " mm. Lower it first, or keep the walls higher.");
            }

        var undos = new List<HostUndo>();
        var snaps = new List<SlabUndo>();
        // The roof is locked, as in a wall edit's follow: unlock it for the move, then lock it again.
        UnlockFloors(doc);
        try
        {
            foreach (var wall in walls)
            {
                var undo = SnapshotWholeHost(doc, wall.Id);
                undos.Add(undo);
                var attr = wall.Attributes.Duplicate();
                attr.SetUserString("forsk:height", FormatMm(height));
                if (!doc.Objects.ModifyAttributes(wall.Id, attr, true))
                    throw new InvalidOperationException("Could not write the wall's height.");
                RebuildUnder(undo);
            }
            foreach (var obj in EnumerateDocObjects(doc))
            {
                if (!string.Equals(GetForskKind(obj), "roof", StringComparison.OrdinalIgnoreCase) || IsExistingUnderlay(doc, obj)) continue;
                var brep = GetBrepFromObject(obj)?.DuplicateBrep();
                if (brep == null || Math.Abs(change) < tol) continue;
                if (!brep.Translate(new Vector3d(0, 0, change)))
                    throw new InvalidOperationException("The roof did not move.");
                ReplaceSlab(doc, obj, brep, snaps);
            }
        }
        catch (Exception ex)
        {
            RestoreSlabs(doc, snaps);
            for (var i = undos.Count - 1; i >= 0; i--) RollbackCommittedHost(doc, undos[i]);
            throw new InvalidOperationException("Height not changed. " + ex.Message, ex);
        }
        finally
        {
            LockFloors(doc);
        }
        doc.Views.Redraw();
        var mm = FormatMm(Math.Round(height));
        var roof = snaps.Count > 0 ? (nb ? "; taket fulgte" : "; the roof followed") : "";
        return new JObject
        {
            ["height"] = height,
            ["walls"] = walls.Count,
            ["roofs"] = snaps.Count,
            ["ok"] = true,
            ["message"] = (nb
                ? "Ga alle " + walls.Count.ToString(CultureInfo.InvariantCulture) + " veggene en høyde på " + mm + " mm"
                : "Made all " + walls.Count.ToString(CultureInfo.InvariantCulture) + " walls " + mm + " mm high") + roof + "."
        };
    }
}
