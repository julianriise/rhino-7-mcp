using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 P6: print_pages, the Choose sheets card's and chat's way to change the
/// set. It writes forsk/print_pages, the one stored list layout_pack, Print
/// one sheet and the card read. A window tool like debug_report, not a
/// bridge command: no server tool and no contract.
/// </summary>
public partial class RhinoMCPFunctions
{
    public JObject PrintPages(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");
        var level = WallLevel(doc);
        List<SheetSet.Sheet> set;
        if (ReadBoolParam(parameters, "reset", false))
        {
            doc.Strings.Delete(SheetSet.MetaSection, SheetSet.MetaEntry);
            set = PrintSet(doc);
        }
        else
        {
            set = SheetSet.Apply(PrintSet(doc), Ids(parameters?["on"]), Ids(parameters?["off"]), Ids(parameters?["order"]), out var unknown);
            if (unknown.Count > 0)
                throw new InvalidOperationException("No sheet " + string.Join(", ", unknown) + " in the set. Sheets: "
                    + string.Join(", ", PrintSet(doc).Select(s => s.Id)) + ".");
            if (set.Count == 0)
                throw new InvalidOperationException(NothingToLayOutMessage);
            doc.Strings.SetString(SheetSet.MetaSection, SheetSet.MetaEntry, SheetSet.Write(set));
        }
        var sheets = new JArray();
        foreach (var sheet in set)
            sheets.Add(new JObject
            {
                ["id"] = sheet.Id,
                ["number"] = SheetSet.Number(sheet.Id, level),
                ["title"] = SheetSet.Title(sheet.Id, level),
                ["on"] = sheet.On
            });
        return new JObject { ["message"] = SheetSet.Summary(set, level), ["sheets"] = sheets };
    }

    private static List<string> Ids(JToken token)
    {
        return (token as JArray)?.Select(t => t?.ToString()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
            ?? new List<string>();
    }
}
