using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// AN.5 and AN.6, first cut: save_option writes the model's records as
/// option A, B, … beside the 3dm (OptionSnapshot); compare_option sets one
/// against the model now (OptionCompare). Restore, rename and delete come next.
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
                Existing = existing
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
