using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Smoke hook for the Forsk panel's Daylight chip. The same chip state, run, and
/// clear the button uses, plus the chat intent for a message. No UI.
/// </summary>
public partial class RhinoMCPFunctions
{
    // ReadOnly: the panel dispatch records its own undo for paint and clear, as the chip does.
    [McpCommand("panel_daylight", ReadOnly = true)]
    public JObject PanelDaylight(JObject parameters)
    {
        var action = parameters["action"]?.ToString() ?? "state";
        var text = parameters["text"]?.ToString();
        JObject envelope = null;
        string label = null;
        if (action == "run")
        {
            label = "Daylight";
            envelope = ForskDaylight.Run("floor", ForskTools.Command);
        }
        else if (action == "clear")
        {
            label = "Clear daylight";
            envelope = ForskDaylight.Clear(ForskTools.Command);
        }
        else if (action != "state")
        {
            throw new ArgumentException("action must be state, run, or clear.");
        }

        var chip = ForskBake.Detect();
        var result = new JObject
        {
            ["visible"] = chip.ShowDaylight,
            ["enabled"] = chip.DaylightEnabled,
            ["label"] = chip.DaylightLabel
        };
        if (text != null)
            result["intent"] = ForskIntentRouter.Classify(text, "").ToString().ToLowerInvariant();
        if (envelope != null)
        {
            result["ok"] = string.Equals(envelope["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
            result["line"] = ForskDaylight.Line(label, envelope);
        }
        return result;
    }

    /// <summary>
    /// Daylight chip rows from the objects daylight_scene reads: hidden layers
    /// (A-OPEN) included, X-EXIST left out, the overlay as it is on A-ANALYSE now.
    /// </summary>
    public static List<ChipRow> ChipRows(RhinoDoc doc)
    {
        var rows = new List<ChipRow>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null || IsExistingUnderlay(doc, obj)) continue;
            var index = obj.Attributes.LayerIndex;
            rows.Add(new ChipRow
            {
                Generated = IsForskGenerated(obj),
                Kind = GetForskKind(obj),
                OpeningKind = obj.Attributes.GetUserString("forsk:opening_kind"),
                Layer = index >= 0 && index < doc.Layers.Count ? doc.Layers[index].Name : "",
                ClosedCurve = obj.Geometry is Curve curve && curve.IsClosed
            });
        }
        return rows;
    }
}
