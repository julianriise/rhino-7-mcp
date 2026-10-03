using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 Full print sets: the set one Print PDF writes, as one ordered list of
/// sheets. Forsk infers it from the model; the user changes it only on the
/// pages card or in chat, and that is stored in the document as JSON
/// ([{id, on}] in the user's order). A sheet's number follows its type and
/// place (after Statsbygg PA 0603, simplified): 00 Forsk's own front sheet
/// and lists, 10 the site plan (reserved), 20 plans, 40 facades and
/// sections. A number belongs to its sheet, so reordering does not
/// renumber. Pure, no Rhino document, so it tests headless.
/// </summary>
public static class SheetSet
{
    public const string MetaSection = "forsk";
    public const string MetaEntry = "print_pages";

    public const string PlanId = "plan";
    public const string SchedulesId = "schedules";

    /// <summary>The facades in the order they print and number: A-40-001 to A-40-004.</summary>
    public static readonly IReadOnlyList<string> Facades = new[] { "north", "east", "south", "west" };

    /// <summary>The door, window and room lists in the order they print.</summary>
    public static readonly IReadOnlyList<string> ListKinds = new[] { "door", "window", "room" };

    public sealed class Sheet
    {
        public Sheet(string id, bool on)
        {
            Id = id;
            On = on;
        }

        public string Id { get; }
        public bool On { get; }
    }

    /// <summary>What the model holds that decides the set.</summary>
    public sealed class SetFacts
    {
        public bool Walls;
        /// <summary>The walls' forsk:level. Stored 0 is 1. etg.</summary>
        public int Level;
        /// <summary>The stored sections' letters.</summary>
        public List<string> Sections = new List<string>();
        /// <summary>The lists that have rows: door, window, room.</summary>
        public List<string> Lists = new List<string>();
    }

    /// <summary>
    /// The default set: the plan and the four facades when walls exist, each
    /// stored section by letter, then the lists when one has rows. Every
    /// sheet on. No walls, no set.
    /// </summary>
    public static List<Sheet> Infer(SetFacts facts)
    {
        var set = new List<Sheet>();
        if (facts == null || !facts.Walls) return set;
        set.Add(new Sheet(PlanId, true));
        foreach (var facade in Facades)
            set.Add(new Sheet(facade, true));
        var letters = (facts.Sections ?? new List<string>())
            .Select(l => (l ?? "").Trim().ToUpperInvariant())
            .Where(l => Sections.TryLetter(Sections.View(l), out _))
            .Distinct()
            .OrderBy(l => l, StringComparer.Ordinal);
        foreach (var letter in letters)
            set.Add(new Sheet(Sections.View(letter), true));
        if ((facts.Lists ?? new List<string>()).Any(k => ListKinds.Contains(k)))
            set.Add(new Sheet(SchedulesId, true));
        return set;
    }

    /// <summary>
    /// The stored set over the inferred one. The stored order and on/off
    /// stand. A sheet the model has lost is dropped. A sheet the model has
    /// gained goes in on, just before the next sheet of the inferred order
    /// that is left: a new section lands before the lists, wherever the
    /// user moved section A. The inferred order's first sheet goes first,
    /// and a sheet with nothing after it goes last. Nothing stored: the
    /// inferred set.
    /// </summary>
    public static List<Sheet> Merge(IList<Sheet> inferred, IList<Sheet> stored)
    {
        inferred = inferred ?? new List<Sheet>();
        if (stored == null) return inferred.ToList();
        var known = new HashSet<string>(inferred.Select(s => s.Id), StringComparer.Ordinal);
        var merged = stored.Where(s => s != null && known.Contains(s.Id))
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        for (var i = 0; i < inferred.Count; i++)
        {
            var sheet = inferred[i];
            if (merged.Any(s => s.Id == sheet.Id)) continue;
            var at = i == 0 ? 0 : merged.Count;
            for (var j = i + 1; i > 0 && j < inferred.Count; j++)
            {
                var next = merged.FindIndex(s => s.Id == inferred[j].Id);
                if (next < 0) continue;
                at = next;
                break;
            }
            merged.Insert(at, new Sheet(sheet.Id, true));
        }
        return merged;
    }

    /// <summary>
    /// The sheet number: plan A-20-00n (n the storey), facades A-40-001 to
    /// 004, section A A-40-101 and on by letter, the lists A-00-002 and one
    /// more per page they flow onto (page is 0-based). Empty for an id that
    /// is no sheet.
    /// </summary>
    public static string Number(string id, int level, int page = 0)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        if (key == PlanId) return Format(20, Math.Max(0, level) + 1);
        var facade = IndexOf(Facades, key);
        if (facade >= 0) return Format(40, facade + 1);
        if (Sections.TryLetter(key, out var letter))
            return Format(40, 101 + (letter[0] - 'A'));
        if (key == SchedulesId) return Format(0, 2 + Math.Max(0, page));
        return "";
    }

    /// <summary>
    /// The sheet's title as its title block prints it. The lists sheet names
    /// the lists it shows (all three when kinds is null).
    /// </summary>
    public static string Title(string id, int level, IList<string> listKinds = null)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        if (key == SchedulesId)
            return Schedules.SheetTitle(listKinds ?? ListKinds.ToList());
        return OpeningTypes.ViewTitle(key, level);
    }

    /// <summary>
    /// The pages in set order, as indexes into pageIds. Pages of one sheet
    /// (a list that flows) keep the order they came in; a page whose sheet
    /// is not in the set goes last. Never the order Rhino lists the pages.
    /// </summary>
    public static List<int> Order(IList<string> pageIds, IList<Sheet> set)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; set != null && i < set.Count; i++)
            if (!rank.ContainsKey(set[i].Id)) rank[set[i].Id] = i;
        var indexes = Enumerable.Range(0, pageIds?.Count ?? 0).ToList();
        // OrderBy is stable: equal ranks keep their incoming order.
        return indexes
            .OrderBy(i => rank.TryGetValue(pageIds[i] ?? "", out var r) ? r : int.MaxValue)
            .ToList();
    }

    /// <summary>The stored set, or null when nothing (readable) is stored.</summary>
    public static List<Sheet> Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JArray rows;
        try { rows = JArray.Parse(json); }
        catch (Exception) { return null; }
        var set = new List<Sheet>();
        foreach (var row in rows.OfType<JObject>())
        {
            var id = row["id"]?.ToString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id) || set.Any(s => s.Id == id)) continue;
            var on = row["on"]?.Type == JTokenType.Boolean ? row["on"].Value<bool>() : true;
            set.Add(new Sheet(id, on));
        }
        return set;
    }

    public static string Write(IEnumerable<Sheet> set)
    {
        var rows = new JArray();
        foreach (var sheet in set ?? new List<Sheet>())
            rows.Add(new JObject { ["id"] = sheet.Id, ["on"] = sheet.On });
        return rows.ToString(Newtonsoft.Json.Formatting.None);
    }

    static string Format(int type, int n)
    {
        return "A-" + type.ToString("00", CultureInfo.InvariantCulture) + "-" + n.ToString("000", CultureInfo.InvariantCulture);
    }

    static int IndexOf(IReadOnlyList<string> list, string key)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == key) return i;
        return -1;
    }
}
