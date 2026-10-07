using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// AN.5 and AN.6: save_option writes the model's records as option A, B, …
/// beside the 3dm (OptionSnapshot); compare_option sets one against the model
/// now (OptionCompare); restore_option puts one back (OptionRestore), one
/// Undo; rename_option and delete_option manage the files.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("save_option", ReadOnly = true)]
    public JObject SaveOption(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active document.");
        if (string.IsNullOrEmpty(doc.Path)) throw new InvalidOperationException("Save the file first: options are kept beside it.");
        var name = (parameters?["name"]?.ToString() ?? "").Trim();
        if (name.Length == 0) name = OptionCompare.NextName(OptionNames(doc.Path));
        var path = OptionSnapshot.PathFor(doc.Path, name);
        var snapshot = ReadOptionSnapshot(doc, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
        File.WriteAllText(path, OptionSnapshot.Write(snapshot));
        var openings = snapshot.Openings.Count;
        return new JObject
        {
            ["name"] = name,
            ["path"] = path,
            ["walls"] = snapshot.Walls.Count,
            ["openings"] = openings,
            ["rooms"] = snapshot.Rooms.Count,
            ["message"] = "Saved option " + name + " beside the file: " + OptionCount(snapshot.Walls.Count, "wall") + ", "
                + OptionCount(openings, "door or window", "doors and windows") + ", " + OptionCount(snapshot.Rooms.Count, "room") + "."
        };
    }

    [McpCommand("compare_option", ReadOnly = true)]
    public JObject CompareOption(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active document.");
        if (string.IsNullOrEmpty(doc.Path)) throw new InvalidOperationException("Save the file first: options are kept beside it.");
        var name = (parameters?["name"]?.ToString() ?? "").Trim();
        var path = OptionSnapshot.PathFor(doc.Path, name);
        if (!File.Exists(path)) throw new InvalidOperationException("No option " + name + ". Saved: " + string.Join(", ", OptionNames(doc.Path)) + ".");
        var option = OptionSnapshot.Read(File.ReadAllText(path), out var error) ?? throw new InvalidOperationException(error);
        var now = ReadOptionSnapshot(doc, "now");
        var summary = OptionSnapshot.Compare(option, now).Summary();
        var rows = new JArray();
        foreach (var row in OptionCompare.Rows(option, now))
            rows.Add(new JObject { ["label"] = row.Label, ["option"] = row.Option, ["now"] = row.Now, ["better"] = row.Better });
        return new JObject
        {
            ["name"] = name,
            ["summary"] = summary,
            ["rows"] = rows,
            ["message"] = "Option " + name + " against now: " + summary.TrimEnd('.') + "."
        };
    }

    [McpCommand("restore_option", ModelView = true, Map = Forsk.MapEdit.Wall)]
    public JObject RestoreOption(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active document.");
        var (name, option) = ReadOption(doc, parameters);
        var steps = OptionRestore.Plan(option, ReadOptionSnapshot(doc, "now"));
        if (steps.Refusal != null) throw new InvalidOperationException(steps.Refusal);
        if (steps.Nothing)
            return OptionResult(name, 0, 0, 0, "The model already matches option " + name + ".");

        var undos = new List<HostUndo>();
        // Floor, roof and rooms follow each wall cluster's whole shape, as a wall move does.
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var walls = EnumerateDocObjects(doc)
            .Where(o => IsForskGenerated(o) && string.Equals(GetForskKind(o), "wall", StringComparison.OrdinalIgnoreCase))
            .Select(o => (Obj: o, Rings: WallEdit.Rings(o.Attributes.GetUserString("forsk:path"))))
            .Where(w => w.Rings != null && w.Rings.Count > 0)
            .ToList();
        var before = walls.Select(w => w.Rings).ToList();
        var after = walls.Select(w => w.Rings).ToList();
        var changed = new List<int>();
        void Hold(Guid host)
        {
            if (host != Guid.Empty && undos.All(u => u.HostBefore != host)) undos.Add(SnapshotWholeHost(doc, host));
        }
        try
        {
            // The walls take their saved records back.
            foreach (var wall in steps.Walls)
            {
                var index = walls.FindIndex(w => string.Equals(w.Obj.Attributes.GetUserString("forsk:id"), wall.Id, StringComparison.Ordinal));
                if (index < 0) throw new InvalidOperationException("Wall " + wall.Id + " is not in the model.");
                var obj = walls[index].Obj;
                Hold(obj.Id);
                WriteWallPath(doc, obj.Id, wall.Record);
                after[index] = WallEdit.Rings(wall.Record);
                changed.Add(index);
            }
            // The doors and windows go back where they stood, before their walls cut them again.
            foreach (var move in steps.Moves)
            {
                if (!Guid.TryParse(move.Id, out var markerId)) continue;
                var marker = doc.Objects.FindId(markerId) ?? throw new InvalidOperationException("An opening of option " + name + " is not in the model.");
                Hold(HostOfMarker(doc, marker));
                var brep = GetBrepFromObject(marker)?.DuplicateBrep();
                if (brep == null || !brep.Translate(new Vector3d(move.Dx, move.Dy, 0)) || !ReplaceOpeningMarker(doc, markerId, brep))
                    throw new InvalidOperationException("An opening could not move back.");
            }
            foreach (var undo in undos) RebuildUnder(undo);
            var done = new HashSet<int>();
            foreach (var index in changed)
            {
                if (done.Contains(index)) continue;
                var cluster = WallJoins.ClusterOf(before, index, tol);
                foreach (var i in cluster) done.Add(i);
                FollowNeighbours(doc, walls[index].Obj.Attributes.GetUserString("forsk:source_layer"),
                    WallJoins.Shape(before, cluster, tol), WallJoins.Shape(after, cluster, tol));
            }
            // Sizes last: set_opening rebuilds the host with the opening's own size.
            foreach (var size in steps.Sizes)
                SetOpening(new JObject { ["id"] = size.Id, ["width"] = size.Width, ["sill"] = size.Sill, ["head"] = size.Head });
        }
        catch (Exception ex)
        {
            for (var i = undos.Count - 1; i >= 0; i--) RollbackCommittedHost(doc, undos[i]);
            throw new InvalidOperationException("Option " + name + " not restored. " + ex.Message, ex);
        }
        doc.Views.Redraw();
        return OptionResult(name, steps.Walls.Count, steps.Moves.Count, steps.Sizes.Count,
            "Restored option " + name + ": " + OptionCount(steps.Walls.Count, "wall") + " back, "
            + OptionCount(steps.Moves.Count, "opening") + " moved and " + OptionCount(steps.Sizes.Count, "opening") + " resized.");
    }

    [McpCommand("rename_option", ReadOnly = true)]
    public JObject RenameOption(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active document.");
        var (name, _) = ReadOption(doc, parameters);
        var to = (parameters?["to"]?.ToString() ?? "").Trim();
        var target = OptionSnapshot.PathFor(doc.Path, to);
        if (OptionNames(doc.Path).Any(n => string.Equals(n, to, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("There is already an option " + to + ".");
        var source = OptionSnapshot.PathFor(doc.Path, name);
        var json = JObject.Parse(File.ReadAllText(source));
        json["name"] = to;
        File.WriteAllText(target, json.ToString(Newtonsoft.Json.Formatting.Indented));
        File.Delete(source);
        return new JObject { ["name"] = to, ["was"] = name, ["path"] = target, ["message"] = "Option " + name + " is now " + to + "." };
    }

    [McpCommand("delete_option", ReadOnly = true)]
    public JObject DeleteOption(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active document.");
        var (name, _) = ReadOption(doc, parameters);
        File.Delete(OptionSnapshot.PathFor(doc.Path, name));
        return new JObject { ["name"] = name, ["message"] = "Deleted option " + name + ". The model did not change." };
    }

    /// <summary>The option the parameters name, read from beside the 3dm. Throws with what is saved when it is not there.</summary>
    private static (string Name, OptionSnapshot.Snapshot Option) ReadOption(RhinoDoc doc, JObject parameters)
    {
        if (string.IsNullOrEmpty(doc.Path)) throw new InvalidOperationException("Save the file first: options are kept beside it.");
        var name = (parameters?["name"]?.ToString() ?? "").Trim();
        var path = OptionSnapshot.PathFor(doc.Path, name);
        if (!File.Exists(path)) throw new InvalidOperationException("No option " + name + ". Saved: " + string.Join(", ", OptionNames(doc.Path)) + ".");
        var option = OptionSnapshot.Read(File.ReadAllText(path), out var error) ?? throw new InvalidOperationException(error);
        return (name, option);
    }

    private static JObject OptionResult(string name, int walls, int moved, int resized, string message) => new JObject
    {
        ["name"] = name,
        ["walls"] = walls,
        ["moved"] = moved,
        ["resized"] = resized,
        ["message"] = message
    };

    /// <summary>The wall an opening marker stands in, or empty.</summary>
    private Guid HostOfMarker(RhinoDoc doc, RhinoObject marker)
    {
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj) || !string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase)) continue;
            if (MarkersOnHost(doc, obj.Id, obj.Attributes.GetUserString("forsk:id")).Any(m => m.Id == marker.Id)) return obj.Id;
        }
        return Guid.Empty;
    }

    /// <summary>The saved options beside the 3dm, oldest first. Empty for an unsaved file or none saved.</summary>
    internal static List<string> OptionNames(string projectFile)
    {
        if (string.IsNullOrEmpty(projectFile)) return new List<string>();
        try
        {
            var folder = Path.GetDirectoryName(OptionSnapshot.PathFor(projectFile, "A"));
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return new List<string>();
            return new DirectoryInfo(folder).GetFiles("*.json")
                .OrderBy(f => f.LastWriteTimeUtc)
                .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                .Where(OptionSnapshot.IsName)
                .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>The model's records as an option: walls, doors and windows, tagged rooms, project info and the analysis strings.</summary>
    private OptionSnapshot.Snapshot ReadOptionSnapshot(RhinoDoc doc, string name)
    {
        var snapshot = new OptionSnapshot.Snapshot { Name = name, Saved = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") };
        foreach (var key in ProjectInfo.Keys)
        {
            var value = doc.Strings.GetValue(ProjectInfo.Section, key);
            if (!string.IsNullOrEmpty(value)) snapshot.Project[key] = value;
        }
        foreach (var key in doc.Strings.GetEntryNames(Analysis.Section) ?? new string[0])
        {
            var value = doc.Strings.GetValue(Analysis.Section, key);
            if (!string.IsNullOrEmpty(value)) snapshot.Analysis[key] = value;
        }
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null) continue;
            var existing = IsExistingUnderlay(doc, obj);
            if (!existing && (!IsForskGenerated(obj) || !string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase))) continue;
            var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (rings == null || rings.Count == 0) continue;
            var box = obj.Geometry?.GetBoundingBox(true);
            snapshot.Walls.Add(new OptionSnapshot.Wall
            {
                Id = obj.Attributes.GetUserString("forsk:id") ?? obj.Id.ToString(),
                Level = obj.Attributes.GetUserString("forsk:level"),
                Path = rings[0],
                Thickness = ParseMm(obj.Attributes.GetUserString("forsk:thickness")) ?? 0,
                Height = ParseMm(obj.Attributes.GetUserString("forsk:height")) ?? (box.HasValue && box.Value.IsValid ? box.Value.Max.Z - box.Value.Min.Z : 0),
                Existing = existing,
                Record = obj.Attributes.GetUserString("forsk:path")
            });
        }
        foreach (var o in OpeningRows(doc, null, out _))
            snapshot.Openings.Add(new OptionSnapshot.Opening
            {
                Id = o.Id,
                Host = o.HostId,
                Kind = o.Record?.Kind,
                Type = o.Record?.TypeId,
                Hand = o.Record?.Hand,
                Swing = o.Record?.Swing,
                Mark = o.Mark,
                Centre = new RoomDetect.Pt(o.X, o.Y),
                Width = o.Width,
                Sill = o.Sill,
                Head = o.Head
            });
        foreach (var room in PlanRooms(doc).Where(r => r.Tagged))
            snapshot.Rooms.Add(new OptionSnapshot.Room { Id = room.ScheduleId, Name = room.Name, Type = room.RoomType, Outline = room.Outline });
        return snapshot;
    }

    static string OptionCount(int n, string one, string many = null) =>
        n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many ?? one + "s");
}
