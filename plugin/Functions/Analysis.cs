using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// AN.1 and AN.2: the analyses Forsk runs, each one's last result and its
/// Live switch, kept on the file (doc strings, section forsk.analysis), and
/// the one line a live edit shows: "Bedroom 2: +1.4 m², daylight 1.8 → 2.3 %".
/// No RhinoCommon.
/// </summary>
public static class Analysis
{
    public const string Section = "forsk.analysis";
    public const string Daylight = "daylight";
    public const string Areas = "areas";
    public static readonly string[] All = { Daylight, Areas };
    /// <summary>Each room's last daylight mean, "R01=1.8;R02=2.3".</summary>
    public const string RoomsKey = "daylight.rooms";
    /// <summary>A live edit runs daylight on the Low grid; the chosen quality runs on demand.</summary>
    public const string LiveQuality = "low";
    /// <summary>A room changes in the line from these: a tenth of a square metre, a twentieth of a percent.</summary>
    public const double AreaStepM2 = 0.05;
    public const double DfStep = 0.05;
    const int MaxRooms = 3;

    public static string LiveKey(string id) => "live." + id;
    public static string LastKey(string id) => "last." + id;
    /// <summary>AN.3: the analysis is in the Analysis set ("1"), or not ("0"). Nothing stored is not.</summary>
    public static string SetKey(string id) => "set." + id;

    /// <summary>The analysis's own table sheet in the Analysis set.</summary>
    public static string SheetId(string id) => SheetPrefix + id;
    public const string SheetPrefix = "analysis_";

    /// <summary>The analysis an analysis sheet is of.</summary>
    public static bool TryAnalysis(string sheetId, out string id)
    {
        var key = (sheetId ?? "").Trim().ToLowerInvariant();
        id = key.StartsWith(SheetPrefix, StringComparison.Ordinal) ? key.Substring(SheetPrefix.Length) : null;
        if (id != null && Array.IndexOf(All, id) >= 0) return true;
        id = null;
        return false;
    }

    /// <summary>The analysis a window job just ran, by its action id, or null.</summary>
    public static string JustRan(string kind)
    {
        switch (kind)
        {
            case "daylight.run":
            case "daylight.again":
            case "daylight.room":
                return Daylight;
            case "area.stats":
                return Areas;
            default:
                return null;
        }
    }

    /// <summary>Areas live by default; daylight takes seconds, so it is off until switched on.</summary>
    public static bool LiveDefault(string id) => id == Areas;

    /// <summary>What the file says, with the defaults filled in.</summary>
    public sealed class State
    {
        public readonly Dictionary<string, bool> Live = new Dictionary<string, bool>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Last = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, double> RoomDf = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly HashSet<string> Set = new HashSet<string>(StringComparer.Ordinal);

        public bool IsLive(string id) => Live.TryGetValue(id, out var on) ? on : LiveDefault(id);

        public bool InSet(string id) => Set.Contains(id);

        public bool AnyLive => All.Any(IsLive);
    }

    public static State Read(Func<string, string> get)
    {
        var state = new State();
        foreach (var id in All)
        {
            var live = get?.Invoke(LiveKey(id));
            if (live == "1" || live == "0") state.Live[id] = live == "1";
            var last = get?.Invoke(LastKey(id));
            if (!string.IsNullOrWhiteSpace(last)) state.Last[id] = last.Trim();
            if (get?.Invoke(SetKey(id)) == "1") state.Set.Add(id);
        }
        foreach (var pair in (get?.Invoke(RoomsKey) ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.LastIndexOf('=');
            if (cut > 0 && double.TryParse(pair.Substring(cut + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var df))
                state.RoomDf[pair.Substring(0, cut)] = df;
        }
        return state;
    }

    public static string WriteRoomDf(IDictionary<string, double> rooms) =>
        string.Join(";", rooms.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key.Replace(";", ",").Replace("=", "-") + "=" + p.Value.ToString("0.##", CultureInfo.InvariantCulture)));

    /// <summary>"DF mean 2.1 % in 8 rooms".</summary>
    public static string DaylightLast(double dfMean, int rooms) =>
        "DF mean " + dfMean.ToString("0.0", CultureInfo.InvariantCulture) + " % in " + Rooms(rooms);

    /// <summary>"142.3 m² in 8 rooms".</summary>
    public static string AreasLast(double areaM2, int rooms) =>
        areaM2.ToString("0.0", CultureInfo.InvariantCulture) + " m² in " + Rooms(rooms);

    static string Rooms(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " room" : " rooms");

    /// <summary>The Analysis set: a sheet per analysis in the menu's order, on when it is in the set.</summary>
    public static List<SheetSet.Sheet> SetSheets(State state) =>
        All.Select(id => new SheetSet.Sheet(SheetId(id), state != null && state.InSet(id))).ToList();

    /// <summary>The Choose analyses card's ticks as the keys to store: "1" in the set, "0" out.</summary>
    public static List<(string Key, string Value)> SetChoice(Func<string, string> values)
    {
        var choice = new List<(string, string)>();
        foreach (var id in All)
        {
            var value = values?.Invoke(SetKey(id));
            if (value == "1" || value == "0") choice.Add((SetKey(id), value));
        }
        return choice;
    }

    /// <summary>
    /// The daylight sheet's table: each room with its last daylight mean, a
    /// dash for a room the last run did not trace, and the run's line under it.
    /// </summary>
    public static Schedules.Table DaylightTable(IList<Room> rooms, State state, bool norwegian = false)
    {
        var table = new Schedules.Table
        {
            Kind = "daylight",
            Title = SheetLang.Pick(norwegian, "Daylight factor", "Dagslysfaktor"),
            Heads = norwegian ? new[] { "Rom", "DF snitt" } : new[] { "Room", "DF mean" },
            Right = new[] { false, true },
            ContinuedSuffix = norwegian ? " (forts.)" : " (cont.)"
        };
        var df = state?.RoomDf ?? new Dictionary<string, double>();
        foreach (var room in rooms ?? new List<Room>())
        {
            if (room?.Id == null) continue;
            table.Ids.Add(room.Id);
            table.Rows.Add(new[]
            {
                Name(room),
                df.TryGetValue(room.Id, out var mean) ? mean.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "–"
            });
        }
        if (state != null && state.Last.TryGetValue(Daylight, out var last))
            table.Note = last + SheetLang.Pick(norwegian, ". A room with – has not been traced.", ". Rom med – er ikke beregnet.");
        else
            table.Note = SheetLang.Pick(norwegian, "Daylight has not run. Run Daylight from the Analyses menu.",
                "Dagslys er ikke kjørt. Kjør Dagslys i Analyser-menyen.");
        return Schedules.Fit(table, null);
    }

    /// <summary>A room as a live edit sees it: its record id, name and floor area.</summary>
    public sealed class Room
    {
        public string Id;
        public string Name;
        public double AreaM2;
    }

    /// <summary>
    /// The line after an edit: each room whose area or daylight changed,
    /// biggest change first, at most three and then "and N more". A new room
    /// says so; a room that went says gone. Null when nothing changed.
    /// </summary>
    public static string LiveLine(IList<Room> before, IList<Room> after, IDictionary<string, double> dfBefore, IDictionary<string, double> dfAfter)
    {
        before = before ?? new List<Room>();
        after = after ?? new List<Room>();
        var parts = new List<(double Weight, string Text)>();
        var was = before.Where(r => r.Id != null).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var room in after)
        {
            var bits = new List<string>();
            var weight = 0.0;
            if (room.Id == null || !was.TryGetValue(room.Id, out var old))
            {
                bits.Add("new, " + room.AreaM2.ToString("0.0", CultureInfo.InvariantCulture) + " m²");
                weight = room.AreaM2;
            }
            else
            {
                var change = room.AreaM2 - old.AreaM2;
                if (Math.Abs(change) >= AreaStepM2)
                {
                    bits.Add((change > 0 ? "+" : "−") + Math.Abs(change).ToString("0.0", CultureInfo.InvariantCulture) + " m²");
                    weight = Math.Abs(change);
                }
            }
            if (room.Id != null && dfAfter != null && dfAfter.TryGetValue(room.Id, out var dfNow))
            {
                if (dfBefore != null && dfBefore.TryGetValue(room.Id, out var dfWas))
                {
                    if (Math.Abs(dfNow - dfWas) >= DfStep)
                    {
                        bits.Add("daylight " + dfWas.ToString("0.0", CultureInfo.InvariantCulture) + " → " + dfNow.ToString("0.0", CultureInfo.InvariantCulture) + " %");
                        weight = Math.Max(weight, Math.Abs(dfNow - dfWas));
                    }
                }
                else if (bits.Count > 0)
                    bits.Add("daylight " + dfNow.ToString("0.0", CultureInfo.InvariantCulture) + " %");
            }
            if (bits.Count > 0) parts.Add((weight, Name(room) + ": " + string.Join(", ", bits)));
        }
        var now = new HashSet<string>(after.Where(r => r.Id != null).Select(r => r.Id));
        foreach (var gone in before.Where(r => r.Id != null && !now.Contains(r.Id)))
            parts.Add((gone.AreaM2, Name(gone) + ": gone"));
        if (parts.Count == 0) return null;
        var ordered = parts.OrderByDescending(p => p.Weight).Select(p => p.Text).ToList();
        var shown = ordered.Take(MaxRooms).ToList();
        if (ordered.Count > MaxRooms) shown.Add("and " + (ordered.Count - MaxRooms).ToString(CultureInfo.InvariantCulture) + " more");
        return string.Join("; ", shown);
    }

    static string Name(Room room) => string.IsNullOrWhiteSpace(room.Name) ? room.Id ?? "a room" : room.Name;
}
