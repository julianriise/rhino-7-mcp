using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    [McpCommand("clear_generated")]
    public JObject ClearGenerated(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var kinds = parameters["kinds"]?.ToObject<List<string>>()
            ?? new List<string> { "wall", "floor", "roof", "opening", "opening_marker" };
        var kindSet = new HashSet<string>(
            kinds.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var levelFilter = parameters["level"]?.ToString();
        var dryRun = parameters["dry_run"]?.ToObject<bool?>() ?? false;
        var includeUntagged = parameters["include_untagged_prefixes"]?.ToObject<bool?>() ?? false;
        var prefixes = parameters["name_prefixes"]?.ToObject<List<string>>()
            ?? new List<string> { "wall-", "floor-", "roof-", "door-", "window-" };

        var matched = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj == null) continue;

            if (IsForskGenerated(obj))
            {
                var kind = GetForskKind(obj) ?? "";
                if (!kindSet.Contains(kind)) continue;
                if (!string.IsNullOrEmpty(levelFilter))
                {
                    var level = obj.Attributes.GetUserString("forsk:level") ?? "";
                    if (!level.Equals(levelFilter, StringComparison.Ordinal))
                        continue;
                }
                matched.Add(obj.Id);
                continue;
            }

            if (!includeUntagged) continue;
            var name = obj.Name ?? "";
            if (prefixes.Any(p =>
                    !string.IsNullOrEmpty(p) &&
                    name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                matched.Add(obj.Id);
            }
        }

        var deleted = new JArray();
        if (dryRun)
        {
            foreach (var id in matched)
                deleted.Add(id.ToString());
        }
        else
        {
            foreach (var id in matched)
            {
                if (doc.Objects.Delete(id, true))
                    deleted.Add(id.ToString());
            }
            if (deleted.Count > 0)
                doc.Views.Redraw();
        }

        return new JObject
        {
            ["deleted"] = deleted,
            ["count"] = deleted.Count,
            ["dry_run"] = dryRun
        };
    }
}
