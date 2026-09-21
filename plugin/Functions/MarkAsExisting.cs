using System;
using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Mark selected (or named) objects as existing underlay on X-EXIST.
/// Stamps forsk:kind=existing and removes forsk:generated.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("mark_as_existing")]
    public JObject MarkAsExisting(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var targetName = parameters?["target_layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(targetName)) targetName = ExistingLayerName;

        var hadIds = parameters?["ids"] is JArray requested && requested.Count > 0;
        var objects = ResolveExistingTargets(doc, parameters);
        if (objects.Count == 0)
        {
            throw new InvalidOperationException(
                hadIds
                    ? "No objects found to mark as existing."
                    : "Nothing is selected. Select the existing building, then mark it again.");
        }

        var layer = EnsureLayer(doc, targetName.Trim(), Color.FromArgb(140, 140, 140));
        var ids = new JArray();
        var forskIds = new JArray();
        var index = 1;

        foreach (var obj in objects)
        {
            if (obj?.Attributes == null) continue;
            var attrs = obj.Attributes.Duplicate();
            attrs.LayerIndex = layer.Index;
            var forskId = FormatStableId("x", index);
            StampExisting(attrs, forskId);
            if (!doc.Objects.ModifyAttributes(obj, attrs, true))
                continue;
            ids.Add(obj.Id.ToString());
            forskIds.Add(forskId);
            index++;
        }

        if (ids.Count > 0)
            doc.Views.Redraw();

        return new JObject
        {
            ["ids"] = ids,
            ["forsk_ids"] = forskIds,
            ["count"] = ids.Count,
            ["target_layer"] = layer.Name,
            ["message"] = $"Marked {ids.Count} object(s) as existing on {layer.Name}."
        };
    }

    private static List<RhinoObject> ResolveExistingTargets(RhinoDoc doc, JObject parameters)
    {
        var result = new List<RhinoObject>();
        var seen = new HashSet<Guid>();
        if (parameters?["ids"] is JArray ids && ids.Count > 0)
        {
            foreach (var token in ids)
            {
                var text = token?.ToString();
                if (string.IsNullOrWhiteSpace(text) || !Guid.TryParse(text, out var guid))
                    continue;
                if (!seen.Add(guid)) continue;
                var obj = doc.Objects.Find(guid);
                if (obj != null) result.Add(obj);
            }
            return result;
        }

        foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
        {
            if (obj == null || !seen.Add(obj.Id)) continue;
            result.Add(obj);
        }
        return result;
    }
}
