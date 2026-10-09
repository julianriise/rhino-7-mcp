using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using RhinoMCPPlugin.Forsk;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Merge walls in the document (WallMerge): after a wall is drawn or edited,
/// and on merge_walls. A record merged away goes, and its openings move to
/// the record that took it in; records cut apart where they went into each
/// other are rebuilt. Every opening stays in the wall that holds it, so a
/// merge that would leave one unheld is not made. It runs inside the edit's
/// own undo record, so the edit and its merge are one undo.
/// </summary>
public partial class RhinoMCPFunctions
{
    private sealed class MergeOutcome
    {
        /// <summary>One sentence for the receipt, or empty when nothing merged.</summary>
        public string Sentence = "";
        /// <summary>Each wall GUID that changed: merged away (to the wall that took it in) or rebuilt.</summary>
        public Dictionary<Guid, Guid> Hosts = new Dictionary<Guid, Guid>();
        public JArray Merged = new JArray();
        public JArray Cut = new JArray();
        public int OpeningsMoved;

        public Guid Follow(Guid id) => Hosts.TryGetValue(id, out var to) ? to : id;
    }

    /// <summary>
    /// Merge walls, the tool: the walls around id, else around the selected
    /// walls, else every wall. One undo.
    /// </summary>
    [McpCommand("merge_walls", ModelView = true, Map = MapEdit.Wall)]
    public JObject MergeWalls(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var seeds = new List<Guid>();
        var idToken = parameters?["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(idToken))
        {
            if (!Guid.TryParse(idToken, out var id) || !IsHostWall(doc, doc.Objects.FindId(id)))
                throw new InvalidOperationException("Not a Forsk wall.");
            seeds.Add(id);
        }
        else
            seeds.AddRange(ListSelected(doc).Where(o => IsHostWall(doc, o)).Select(o => o.Id));
        var all = seeds.Count == 0;
        var outcome = MergeWallsAround(doc, all ? null : seeds, tol, strict: true);
        var nb = ForskSpeech.Norwegian;
        var message = outcome.Sentence.Length > 0
            ? outcome.Sentence
            : nb ? "Ingen vegger å slå sammen." : "No walls to merge.";
        doc.Views.Redraw();
        return new JObject
        {
            ["merged"] = outcome.Merged,
            ["cut"] = outcome.Cut,
            ["openings_moved"] = outcome.OpeningsMoved,
            ["ok"] = true,
            ["message"] = message
        };
    }

    /// <summary>
    /// The merges around the walls given (null: every wall), made in the
    /// document. A merge that fails puts every wall back: with strict it
    /// throws, else the edit before it stands and the outcome is empty.
    /// </summary>
    private MergeOutcome MergeWallsAround(RhinoDoc doc, IEnumerable<Guid> around, double tol, bool strict = false)
    {
        var outcome = new MergeOutcome();
        var walls = new List<RhinoObject>();
        var records = new List<List<List<RoomDetect.Pt>>>();
        var keys = new List<string>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase) || IsExistingUnderlay(doc, obj))
                continue;
            var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (rings == null) continue;
            walls.Add(obj);
            records.Add(rings);
            keys.Add(MergeKey(obj));
        }
        var seeds = new List<int>();
        var wanted = around == null ? null : new HashSet<Guid>(around);
        for (var i = 0; i < walls.Count; i++)
            if (wanted == null || wanted.Contains(walls[i].Id)) seeds.Add(i);
        if (seeds.Count == 0) return outcome;

        var plan = WallMerge.Plan(records, seeds, tol, keys);
        if (!plan.Any) return outcome;

        // Every opening on a wall the merge touches goes to the wall that holds it after.
        var after = new Dictionary<int, List<List<RoomDetect.Pt>>>();
        var touched = new HashSet<int>(plan.Records.Keys);
        foreach (var gone in plan.Into) { touched.Add(gone.Key); touched.Add(gone.Value); }
        foreach (var i in touched)
            if (!plan.Into.ContainsKey(i))
                after[i] = plan.Records.TryGetValue(i, out var rings) ? rings : records[i];
        var moves = new List<(RhinoObject Marker, int From, int To)>();
        foreach (var i in touched)
        {
            foreach (var marker in MarkersOnHost(doc, walls[i].Id, walls[i].Attributes.GetUserString("forsk:id")))
            {
                var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (!box.IsValid) continue;
                var footprint = new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y);
                if (after.TryGetValue(i, out var own) && WallEdit.Holds(own, footprint, tol)) continue;
                var to = -1;
                foreach (var k in after.Keys)
                    if (k != i && WallEdit.Holds(after[k], footprint, tol)) { to = k; break; }
                if (to < 0)
                {
                    var why = "Walls not merged: " + MarkerLabel(marker) + " would stand in no wall.";
                    if (strict) throw new InvalidOperationException(why);
                    return outcome;
                }
                moves.Add((marker, i, to));
            }
        }

        var undos = new Dictionary<int, HostUndo>();
        try
        {
            foreach (var i in touched) undos[i] = SnapshotWholeHost(doc, walls[i].Id);
            foreach (var move in moves)
            {
                RehostOpening(doc, move.Marker, walls[move.To].Id);
                var block = doc.Objects.FindId(FindOpeningBlock(doc, move.Marker.Id));
                if (block != null) RehostOpening(doc, block, walls[move.To].Id);
            }
            foreach (var gone in plan.Into)
            {
                var undo = undos[gone.Key];
                var wall = doc.Objects.FindId(undo.HostBefore);
                if (wall == null || !TrackDelete(doc, wall, undo.Removed))
                    throw new InvalidOperationException("Could not delete " + WallName(walls[gone.Key]) + ".");
            }
            var rebuild = new HashSet<int>(plan.Records.Keys);
            foreach (var move in moves) rebuild.Add(move.To);
            foreach (var i in rebuild)
            {
                if (plan.Records.TryGetValue(i, out var rings)) WriteWallPath(doc, walls[i].Id, WallEdit.Path(rings));
                var undo = undos[i];
                RebuildUnder(undo);
                outcome.Hosts[undo.HostBefore] = undo.HostAfter;
            }
        }
        catch (Exception ex)
        {
            var list = new List<HostUndo>(undos.Values);
            for (var i = list.Count - 1; i >= 0; i--) RollbackCommittedHost(doc, list[i]);
            if (strict) throw new InvalidOperationException("Walls not merged. " + ex.Message, ex);
            RhinoApp.WriteLine("Forsk: walls not merged. " + ex.Message);
            return new MergeOutcome();
        }

        foreach (var gone in plan.Into)
            outcome.Hosts[walls[gone.Key].Id] = outcome.Follow(walls[gone.Value].Id);
        outcome.OpeningsMoved = moves.Count;
        var nb = ForskSpeech.Norwegian;
        var parts = new List<string>();
        foreach (var keeper in plan.Into.Values.Distinct())
        {
            var into = plan.Into.Where(p => p.Value == keeper).Select(p => WallName(walls[p.Key])).ToList();
            outcome.Merged.Add(new JObject { ["forsk_id"] = WallName(walls[keeper]), ["took_in"] = new JArray(into) });
            parts.Add(nb
                ? "slo sammen " + string.Join(" og ", into) + " med " + WallName(walls[keeper])
                : "merged " + string.Join(" and ", into) + " into " + WallName(walls[keeper]));
        }
        var cut = plan.Records.Keys.Where(k => !plan.Into.ContainsValue(k)).Select(k => WallName(walls[k])).ToList();
        foreach (var name in cut) outcome.Cut.Add(name);
        if (cut.Count > 0)
            parts.Add(nb
                ? "rettet opp " + string.Join(" og ", cut) + " der de gikk inn i hverandre"
                : "cleaned up " + string.Join(" and ", cut) + " where they went into each other");
        var sentence = string.Join("; ", parts);
        if (sentence.Length > 0) sentence = char.ToUpper(sentence[0], CultureInfo.InvariantCulture) + sentence.Substring(1) + ".";
        if (moves.Count > 0)
            sentence += nb
                ? (moves.Count == 1 ? " 1 åpning fulgte med." : " " + moves.Count + " åpninger fulgte med.")
                : (moves.Count == 1 ? " 1 opening went with it." : " " + moves.Count + " openings went with them.");
        outcome.Sentence = sentence;
        return outcome;
    }

    /// <summary>Walls merge into one only with the same height and level.</summary>
    static string MergeKey(RhinoObject wall)
    {
        var level = wall.Attributes.GetUserString("forsk:level") ?? "0";
        var height = ParseMm(wall.Attributes.GetUserString("forsk:height"));
        if (!height.HasValue)
        {
            var box = wall.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
            if (box.IsValid) height = box.Max.Z - box.Min.Z;
        }
        var bottom = wall.Geometry?.GetBoundingBox(true).Min.Z ?? 0;
        return level + "|" + Math.Round(height ?? 0).ToString(CultureInfo.InvariantCulture)
            + "|" + Math.Round(bottom).ToString(CultureInfo.InvariantCulture);
    }

    static string WallName(RhinoObject wall)
    {
        var id = wall.Attributes.GetUserString("forsk:id");
        return string.IsNullOrEmpty(id) ? wall.Name ?? "a wall" : id;
    }
}
