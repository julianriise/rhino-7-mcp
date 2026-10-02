using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Smoke hook for the Forsk panel's Daylight chip. The same chip state, run, and
/// hide or show the button uses, plus the chat intent for a message and the Import
/// chip as it stands (Import plan, or Set scale on an imported plan). No UI.
/// </summary>
public partial class RhinoMCPFunctions
{
    // ReadOnly: run goes through the panel dispatch, which records undo. Hide and show record their own.
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
        else if (action == "hide" || action == "show")
        {
            label = action == "hide" ? "Hide daylight map" : "Show daylight map";
            envelope = ForskDaylight.ApplyVisible(action == "show");
        }
        else if (action == "rooms")
        {
            envelope = ForskDaylight.MakeRooms(ForskTools.Command);
        }
        else if (action != "state")
        {
            throw new ArgumentException("action must be state, run, hide, show, or rooms.");
        }

        var chip = ForskBake.Detect();
        var result = new JObject
        {
            ["visible"] = chip.ShowDaylight,
            ["enabled"] = chip.DaylightEnabled,
            ["label"] = chip.DaylightLabel,
            ["import_visible"] = chip.ShowImport,
            ["import_label"] = chip.ImportLabel
        };
        if (text != null)
            result["intent"] = ForskIntentRouter.Classify(text, "").ToString().ToLowerInvariant();
        if (envelope != null)
        {
            result["ok"] = string.Equals(envelope["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
            result["line"] = action == "rooms" ? ForskDaylight.RoomsLine(envelope) : ForskDaylight.Line(label, envelope);
        }
        return result;
    }
}
