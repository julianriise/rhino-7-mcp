using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Saved views in the view picker (Julian, 2026-10-09): Save current view
/// keeps the active viewport's camera as a Rhino named view in the .3dm, and
/// the picker lists the file's named views under the built-in ones, so one
/// click shows one again. The Interior and Exterior render views keep their
/// own lists. Rhino's named view table is the one source of truth; Forsk only
/// adds the display mode each view was saved with. No RhinoCommon.
/// </summary>
public static class SavedViews
{
    /// <summary>A picked saved view: "saved:View 1".</summary>
    public const string Prefix = "saved:";
    /// <summary>The picker's Save current view row.</summary>
    public const string SaveId = "saved.new";
    /// <summary>The picker's Rename or delete row: it opens the saved views card.</summary>
    public const string EditId = "saved.edit";
    /// <summary>The card that renames and deletes saved views.</summary>
    public const string CardKind = "views.saved";
    /// <summary>Document strings section: saved view name → the display mode id it was saved with.</summary>
    public const string ModesSection = "forsk:view_modes";
    /// <summary>A new view's name: "View 1", "View 2"…</summary>
    public const string NameStem = "View ";
    /// <summary>The card's field keys: "view0", "view1"… in the card's order.</summary>
    public const string FieldStem = "view";

    public static string Id(string name) => Prefix + name;

    /// <summary>A picked id that is a saved view: its name.</summary>
    public static bool TryName(string id, out string name)
    {
        name = null;
        if (string.IsNullOrEmpty(id) || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        name = id.Substring(Prefix.Length);
        if (name.Trim().Length > 0) return true;
        name = null;
        return false;
    }

    /// <summary>
    /// The picker's saved views: the file's named views in table order,
    /// without the Interior and Exterior render views (they have their own
    /// lists), blanks or repeats (case ignored).
    /// </summary>
    public static List<string> Listed(IEnumerable<string> named, IEnumerable<string> renders = null)
    {
        var skip = new HashSet<string>(renders ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in named ?? Enumerable.Empty<string>())
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0 || skip.Contains(name) || !seen.Add(name)) continue;
            list.Add(name);
        }
        return list;
    }

    /// <summary>The first free "View n" (case ignored), counting from 1.</summary>
    public static string NextName(IEnumerable<string> taken)
    {
        var used = new HashSet<string>((taken ?? Enumerable.Empty<string>()).Select(n => (n ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
            if (!used.Contains(NameStem + n)) return NameStem + n;
    }

    /// <summary>A typed name: trimmed, inner runs of space made one. Empty is null.</summary>
    public static string Clean(string typed)
    {
        var words = (typed ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? null : string.Join(" ", words);
    }

    /// <summary>One change from the card: To null deletes From.</summary>
    public sealed class Change
    {
        public string From;
        public string To;
        public bool Deletes => To == null;
    }

    /// <summary>
    /// The card's answer as changes: an emptied name deletes that view, a
    /// new name renames it. A new name another view keeps (or a render view
    /// has) is refused, and that view stays as it was. Unchanged names and
    /// case-only typos of the same name do nothing unless the case changed.
    /// </summary>
    public static List<Change> Edits(IList<string> names, IList<string> typed, IEnumerable<string> reserved, out List<string> refused)
    {
        refused = new List<string>();
        var changes = new List<Change>();
        if (names == null) return changes;
        var cleaned = names.Select((n, i) => Clean(typed != null && i < typed.Count ? typed[i] : n)).ToList();
        // A name is taken by any view that stays (renamed or not) and by the render views.
        var taken = new HashSet<string>(reserved ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
            if (cleaned[i] != null && string.Equals(cleaned[i], names[i], StringComparison.Ordinal)) taken.Add(names[i]);
        for (var i = 0; i < names.Count; i++)
        {
            var from = names[i];
            var to = cleaned[i];
            if (to == null)
            {
                changes.Add(new Change { From = from });
                continue;
            }
            if (string.Equals(to, from, StringComparison.Ordinal)) continue;
            // Case only: the same view, a new spelling.
            var caseOnly = string.Equals(to, from, StringComparison.OrdinalIgnoreCase);
            var clash = !caseOnly && (taken.Contains(to)
                || names.Where((n, j) => j != i && cleaned[j] != null).Any(n => string.Equals(n, to, StringComparison.OrdinalIgnoreCase)));
            if (clash)
            {
                refused.Add(to);
                taken.Add(from);
                continue;
            }
            taken.Add(to);
            changes.Add(new Change { From = from, To = to });
        }
        return changes;
    }
}
