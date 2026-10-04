using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// details: add stores one detail per picked wall, door or window by Forsk
/// ids, in forsk/details; remove drops some or all; list reads them. Nothing
/// is drawn here: Print resolves each one from the live records (Details)
/// and draws it on a detail sheet.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("details")]
    public JObject DetailsTool(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");
        var action = (parameters?["action"]?.ToString() ?? "list").Trim().ToLowerInvariant();
        var records = Details.Read(doc.Strings.GetValue(Details.Section, Details.Entry));
        switch (action)
        {
            case "add":
                return AddDetails(doc, records, parameters?["refs"] as JArray);
            case "remove":
                return RemoveDetails(doc, records, parameters?["ids"] as JArray);
            case "list":
                return DetailsResult(doc, records, records.Count == 0
                    ? "No details yet."
                    : DetailCount(records.Count) + ": " + string.Join(", ", DetailNames(doc, records).Values) + ".");
            default:
                throw new ArgumentException("action is add, remove or list.");
        }
    }

    private JObject AddDetails(RhinoDoc doc, List<Details.Record> records, JArray given)
    {
        var refs = given != null && given.Count > 0 ? ReadDetailRefs(given) : DetailRefsFromSelection(doc);
        if (refs.Count == 0)
            throw new InvalidOperationException(Details.NeedsPick);
        var model = ReadIfcModel(doc);
        foreach (var r in refs)
        {
            var probe = new Details.Record { Id = "DET00", Wall = r.Wall, Opening = r.Wall == null ? r.Opening : null };
            if (Details.Resolve(probe, model, 1.0) == null)
                throw new InvalidOperationException(r.Wall != null ? "No wall " + r.Wall + "." : "No door or window " + r.Opening + ".");
        }
        var stored = Details.Add(records, refs, out var added, out var already);
        if (added > 0) doc.Strings.SetString(Details.Section, Details.Entry, Details.Write(stored));
        var message = added == 0
            ? (already == 1 ? "It already has a detail." : "They already have details.")
            : "✓ " + DetailCount(added) + " added · " + (added == 1 ? "it prints" : "they print") + " on a detail sheet.";
        if (added > 0 && already > 0)
            message += " " + already.ToString(CultureInfo.InvariantCulture) + (already == 1 ? " was" : " were") + " already there.";
        var result = DetailsResult(doc, stored, message, model);
        result["added"] = added;
        result["already"] = already;
        return result;
    }

    private static JObject RemoveDetails(RhinoDoc doc, List<Details.Record> records, JArray ids)
    {
        if (records.Count == 0)
            return DetailsResult(doc, records, "No details to remove.");
        var kept = Details.Remove(records, ids?.Select(t => t.ToString()).ToList());
        var removed = records.Count - kept.Count;
        if (removed == 0)
            return DetailsResult(doc, records, "No detail " + string.Join(", ", ids.Select(t => t.ToString())) + ".");
        if (kept.Count == 0) doc.Strings.Delete(Details.Section, Details.Entry);
        else doc.Strings.SetString(Details.Section, Details.Entry, Details.Write(kept));
        return DetailsResult(doc, kept, "✓ Removed " + DetailCount(removed) + " · "
            + (removed == 1 ? "its drawings go" : "their drawings go") + " at the next Print.");
    }

    private static JObject DetailsResult(RhinoDoc doc, List<Details.Record> records, string message, IfcExport.Model model = null)
    {
        var names = DetailNames(doc, records, model);
        var rows = new JArray();
        foreach (var record in records)
        {
            var row = new JObject { ["id"] = record.Id };
            if (record.Wall != null) row["wall"] = record.Wall;
            else row["opening"] = record.Opening;
            row["name"] = names.TryGetValue(record.Id, out var name) ? name : "";
            row["views"] = new JArray(Details.Views(record).ToArray());
            rows.Add(row);
        }
        return new JObject { ["details"] = rows, ["count"] = records.Count, ["message"] = message };
    }

    /// <summary>Each stored detail's name by id, from the model as it is now. One whose element is gone has none.</summary>
    internal static Dictionary<string, string> DetailNames(RhinoDoc doc, List<Details.Record> records, IfcExport.Model model = null)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (records == null || records.Count == 0) return names;
        model = model ?? ReadIfcModel(doc);
        var live = records.Select(r => Details.Resolve(r, model, 1.0)).Where(f => f != null).ToList();
        var titles = Details.Names(live);
        for (var i = 0; i < live.Count; i++) names[live[i].Record.Id] = titles[i];
        return names;
    }

    private static List<Details.Ref> ReadDetailRefs(JArray given)
    {
        var refs = new List<Details.Ref>();
        foreach (var item in given.OfType<JObject>())
        {
            var wall = item["wall"]?.ToString();
            var opening = item["opening"]?.ToString();
            if (!string.IsNullOrWhiteSpace(wall)) refs.Add(new Details.Ref { Wall = wall.Trim() });
            else if (!string.IsNullOrWhiteSpace(opening)) refs.Add(new Details.Ref { Opening = opening.Trim() });
        }
        return refs;
    }

    /// <summary>The picked walls by forsk:id, and the picked doors and windows by their marker's forsk:id, each once.</summary>
    private static List<Details.Ref> DetailRefsFromSelection(RhinoDoc doc)
    {
        var refs = new List<Details.Ref>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = ListSelected(doc);
        foreach (var obj in selected)
        {
            if (!IsForskGenerated(obj) || !string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase)) continue;
            var id = obj.Attributes.GetUserString("forsk:id");
            if (!string.IsNullOrEmpty(id) && seen.Add("w:" + id)) refs.Add(new Details.Ref { Wall = id });
        }
        foreach (var marker in MarkersOfSelection(doc, selected))
        {
            var id = marker.Attributes.GetUserString("forsk:id");
            if (string.IsNullOrEmpty(id)) id = marker.Id.ToString();
            if (seen.Add("o:" + id)) refs.Add(new Details.Ref { Opening = id });
        }
        return refs;
    }

    private static string DetailCount(int n) =>
        n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " detail" : " details");
}
