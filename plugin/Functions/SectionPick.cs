using System;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    /// <summary>
    /// Top view, then the user's line, locked to X or Y at z=0. The name
    /// dialog stores the cut through section_add. Cancel adds nothing.
    /// The dispatcher is already on the UI thread and already recording undo.
    /// </summary>
    [McpCommand("section_pick", ModelView = true)]
    public JObject SectionPick(JObject parameters)
    {
        var envelope = ForskSection.RunOnUi(false);
        if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(envelope?["message"]?.ToString() ?? "Cross section cancelled.");
        return envelope["result"] as JObject ?? new JObject();
    }
}
