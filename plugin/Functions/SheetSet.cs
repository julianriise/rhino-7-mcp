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
    /// <summary>The front sheet: the Tegningsliste and the Arealtabell.</summary>
    public const string FrontId = "front";
    /// <summary>The Mengdeliste: in every set, off until the user turns it on.</summary>
    public const string TakeoffId = "takeoff";

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
    /// The default set: the front sheet, the plan and the four facades when
    /// walls exist, each stored section by letter, the lists when one has
    /// rows, then the Mengdeliste. Every sheet on but the Mengdeliste. No
    /// walls, no set.
    /// </summary>
    public static List<Sheet> Infer(SetFacts facts)
    {
        var set = new List<Sheet>();
        if (facts == null || !facts.Walls) return set;
        set.Add(new Sheet(FrontId, true));
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
        set.Add(new Sheet(TakeoffId, false));
        return set;
    }

    /// <summary>
    /// The stored set over the inferred one. The stored order and on/off
    /// stand. A sheet the model has lost is dropped. A sheet the model has
    /// gained goes in as inferred (on; the Mengdeliste off), just before the next sheet of the inferred order
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
            merged.Insert(at, new Sheet(sheet.Id, sheet.On));
        }
        return merged;
    }

    /// <summary>
    /// The sheet number: the front sheet A-00-001, plan A-20-00n (n the
    /// storey), facades A-40-001 to 004, section A A-40-101 and on by letter,
    /// the lists A-00-002 and one more per page they flow onto (page is
    /// 0-based). A-10-001 is kept for the site plan. Empty for an id that is
    /// no sheet.
    /// </summary>
    public static string Number(string id, int level, int page = 0)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        if (key == FrontId) return Format(0, 1);
        // Clear of the lists' pages, so it keeps its number however many they take.
        if (key == TakeoffId) return Format(0, 50);
        if (key == PlanId) return Format(20, Math.Max(0, level) + 1);
        var facade = IndexOf(Facades, key);
        if (facade >= 0) return Format(40, facade + 1);
        if (Sections.TryLetter(key, out var letter))
            return Format(40, 101 + (letter[0] - 'A'));
        if (key == SchedulesId) return Format(0, 2 + Math.Max(0, page));
        return "";
    }

    /// <summary>
    /// The sheet's title as its title block prints it. listKinds are the
    /// lists that have rows (all three when null): the lists sheet names
    /// them, and the front sheet holds the Arealtabell only with rooms.
    /// </summary>
    public static string Title(string id, int level, IList<string> listKinds = null, bool norwegian = false)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        if (key == FrontId)
            return listKinds == null || listKinds.Contains("room")
                ? SheetLang.Pick(norwegian, "Drawing list and areas", "Tegningsliste og arealer")
                : SheetLang.Pick(norwegian, "Drawing list", "Tegningsliste");
        if (key == TakeoffId) return SheetLang.Pick(norwegian, "Quantities", "Mengdeliste");
        if (key == SchedulesId)
            return Schedules.SheetTitle(listKinds ?? ListKinds.ToList(), norwegian);
        return OpeningTypes.ViewTitle(key, level, norwegian);
    }

    /// <summary>
    /// The set after a change from the pages card or chat: the sheets in on
    /// switched on, those in off switched off, and the sheets in order moved
    /// so they stand in that order from where the earliest of them stood; the
    /// rest keep theirs. An id that is no sheet of the set changes nothing and
    /// comes back in unknown.
    /// </summary>
    public static List<Sheet> Apply(IList<Sheet> set, IList<string> on, IList<string> off, IList<string> order, out List<string> unknown)
    {
        var current = (set ?? new List<Sheet>()).ToList();
        var ids = new HashSet<string>(current.Select(s => s.Id), StringComparer.Ordinal);
        var missing = new List<string>();
        List<string> Known(IList<string> list)
        {
            var known = new List<string>();
            foreach (var raw in list ?? new List<string>())
            {
                var id = (raw ?? "").Trim().ToLowerInvariant();
                if (id.Length == 0) continue;
                if (!ids.Contains(id)) { if (!missing.Contains(id)) missing.Add(id); }
                else if (!known.Contains(id)) known.Add(id);
            }
            return known;
        }
        var turnOn = Known(on);
        var turnOff = Known(off);
        var moved = Known(order);
        unknown = missing;
        var switched = current
            .Select(s => new Sheet(s.Id, turnOff.Contains(s.Id) ? false : turnOn.Contains(s.Id) || s.On))
            .ToList();
        if (moved.Count == 0) return switched;
        var at = switched.FindIndex(s => moved.Contains(s.Id));
        var rest = switched.Where(s => !moved.Contains(s.Id)).ToList();
        var before = switched.Take(at).Count(s => !moved.Contains(s.Id));
        rest.InsertRange(before, moved.Select(id => switched.First(s => s.Id == id)));
        return rest;
    }

    /// <summary>The set in one line: "Set: 7 sheets, facades off." The four facades off are "facades".</summary>
    public static string Summary(IList<Sheet> set, int level = 0, bool norwegian = false)
    {
        set = set ?? new List<Sheet>();
        var on = set.Count(s => s.On);
        // Quantities is off unless asked for: it is named only when it is on.
        var off = set.Where(s => !s.On && s.Id != TakeoffId).Select(s => s.Id).ToList();
        var takeoff = set.Any(s => s.On && s.Id == TakeoffId)
            ? SheetLang.Pick(norwegian, ", with Quantities", ", with the Mengdeliste")
            : "";
        var named = new List<string>();
        if (Facades.All(off.Contains))
        {
            named.Add("facades");
            off.RemoveAll(id => Facades.Contains(id));
        }
        named.AddRange(off.Select(id => Title(id, level, null, norwegian)));
        var text = "Set: " + on.ToString(CultureInfo.InvariantCulture) + (on == 1 ? " sheet" : " sheets");
        if (named.Count == 0) return text + takeoff + ".";
        var list = named.Count == 1
            ? named[0]
            : string.Join(", ", named.Take(named.Count - 1)) + " and " + named[named.Count - 1];
        return text + ", " + list + " off" + takeoff + ".";
    }

    /// <summary>A sheet of tables and no detail: the front sheet and the lists. It has no scale.</summary>
    public static bool IsListSheet(string id)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        return key == FrontId || key == SchedulesId || key == TakeoffId;
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
