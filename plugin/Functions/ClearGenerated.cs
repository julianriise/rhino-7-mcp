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
        // Default excludes drawing and layout so a 3D rebuild leaves S-* sheets
        // and title-block objects. Layout pages are views, not objects.
        // An explicit kinds list that contains "drawing" or "layout" still deletes those objects.
        var kinds = parameters["kinds"]?.ToObject<List<string>>()
            ?? new List<string> { "wall", "floor", "roof", "opening", "opening_marker", "room", "room_plate", "analysis" };
        var kindSet = new HashSet<string>(
            kinds.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var levelFilter = parameters["level"]?.ToString();
        var dryRun = parameters["dry_run"]?.ToObject<bool?>() ?? false;
        var includeUntagged = parameters["include_untagged_prefixes"]?.ToObject<bool?>() ?? false;
        var prefixes = parameters["name_prefixes"]?.ToObject<List<string>>()
            ?? new List<string> { "wall-", "floor-", "roof-", "door-", "window-", "room-" };

        var matched = new List<Guid>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj == null) continue;
            // X-EXIST / forsk:kind=existing stays even if generated=1 or a name prefix matches.
            if (IsExistingUnderlay(doc, obj)) continue;

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
            // The daylight mesh and the floor slabs are locked. A rebuild that
            // lists them has to unlock them to delete them, then lock what remains.
            var clearsMap = kindSet.Contains("analysis");
            var clearsFloors = kindSet.Contains("floor") || kindSet.Contains("roof");
            if (clearsMap) UnlockAnalysis(doc);
            if (clearsFloors) UnlockFloors(doc);
            try
            {
                foreach (var id in matched)
                {
                    var obj = doc.Objects.FindId(id);
                    var gone = string.Equals(GetForskKind(obj), "opening_marker", StringComparison.OrdinalIgnoreCase)
                        ? DeleteOpeningMarker(doc, id)
                        : doc.Objects.Delete(id, true);
                    if (gone)
                        deleted.Add(id.ToString());
                }
                if (deleted.Count > 0)
                    BakePace.Redraw(doc);
                PurgeOpeningBlockDefinitions(doc);
            }
            finally
            {
                if (clearsMap) LockAnalysis(doc);
                if (clearsFloors) LockFloors(doc);
            }
        }

        return new JObject
        {
            ["deleted"] = deleted,
            ["count"] = deleted.Count,
            ["dry_run"] = dryRun
        };
    }
}
