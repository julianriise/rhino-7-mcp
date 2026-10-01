using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// F5.4 print profile: the document's choice of how sheets are inked. It is one
/// document string (see PrintProfiles), so it saves with the file. The drawing
/// passes read it at their start; nothing is redrawn here.
/// </summary>
public partial class RhinoMCPFunctions
{
    /// <summary>The document's print profile. The default when none is stored or the name is unknown.</summary>
    internal static PrintProfile ReadPrintProfile(RhinoDoc doc)
    {
        if (doc == null) return PrintProfiles.Default;
        return PrintProfiles.FromStored(doc.Strings.GetValue(PrintProfiles.MetaSection, PrintProfiles.MetaKey));
    }

    [McpCommand("print_profile")]
    public JObject SetPrintProfile(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");

        var wanted = parameters?["name"]?.ToString();
        var before = ReadPrintProfile(doc);
        var current = before;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var found = PrintProfiles.Find(wanted);
            if (found == null)
                throw new InvalidOperationException(
                    "Unknown print profile \"" + wanted.Trim() + "\". Use "
                    + string.Join(", ", PrintProfiles.All.Select(p => p.Name)) + ".");
            // The default is stored as no string, so a file that never chose one stays as it was.
            if (found.Name == PrintProfiles.DefaultName)
                doc.Strings.Delete(PrintProfiles.MetaSection, PrintProfiles.MetaKey);
            else
                doc.Strings.SetString(PrintProfiles.MetaSection, PrintProfiles.MetaKey, found.Name);
            current = found;
        }

        var changed = !string.Equals(before.Name, current.Name, StringComparison.Ordinal);
        return new JObject
        {
            ["profile"] = current.Record(),
            ["available"] = PrintProfiles.Available(),
            ["changed"] = changed,
            ["message"] = changed
                ? "Print profile: " + current.Name + " (" + current.Label + "). The next layout_pack or Print draws with it; pages already made keep their old look."
                : "Print profile: " + current.Name + " (" + current.Label + ")."
        };
    }
}
