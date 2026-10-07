using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// AN.5 and AN.6, first cut: the next option's name, and an option against
/// the model now, side by side: the net area, each room's area, each room's
/// daylight mean (the daylight run stored with the option, and the last one
/// now), and the door and window counts. The higher daylight is the better
/// one; areas are not judged. No RhinoCommon.
/// </summary>
public static class OptionCompare
{
    public sealed class Row
    {
        public string Label;
        public string Option;
        public string Now;
        /// <summary>"option", "now", or "" when the row is not judged or both are equal.</summary>
        public string Better = "";
    }

    /// <summary>The first free letter: A, then B, … Names are matched without case.</summary>
    public static string NextName(IEnumerable<string> taken)
    {
        var used = new HashSet<string>((taken ?? Enumerable.Empty<string>()).Select(n => (n ?? "").Trim().ToUpperInvariant()));
        for (var c = 'A'; c <= 'Z'; c++)
            if (!used.Contains(c.ToString())) return c.ToString();
        for (var n = 2; ; n++)
            if (!used.Contains("A" + n.ToString(CultureInfo.InvariantCulture))) return "A" + n.ToString(CultureInfo.InvariantCulture);
    }

    public static List<Row> Rows(OptionSnapshot.Snapshot option, OptionSnapshot.Snapshot now)
    {
        option = option ?? new OptionSnapshot.Snapshot();
        now = now ?? new OptionSnapshot.Snapshot();
        var rows = new List<Row>
        {
            new Row { Label = "Net area", Option = Area(option.Rooms.Sum(r => RoomArea(r))), Now = Area(now.Rooms.Sum(r => RoomArea(r))) }
        };
        // Rooms in the option's order, then the ones only the model now has.
        var keys = option.Rooms.Select(Key).Concat(now.Rooms.Select(Key)).Distinct(StringComparer.Ordinal).ToList();
        OptionSnapshot.Room Find(OptionSnapshot.Snapshot s, string key) => s.Rooms.FirstOrDefault(r => Key(r) == key);
        foreach (var key in keys)
        {
            var a = Find(option, key);
            var b = Find(now, key);
            rows.Add(new Row { Label = Name(a ?? b), Option = a == null ? "–" : Area(RoomArea(a)), Now = b == null ? "–" : Area(RoomArea(b)) });
        }
        var dfA = Analysis.Read(k => option.Analysis.TryGetValue(k, out var v) ? v : null).RoomDf;
        var dfNow = Analysis.Read(k => now.Analysis.TryGetValue(k, out var v) ? v : null).RoomDf;
        foreach (var key in keys)
        {
            var a = Find(option, key);
            var b = Find(now, key);
            var hasA = a != null && dfA.TryGetValue(a.Id ?? "", out var x);
            var hasB = b != null && dfNow.TryGetValue(b.Id ?? "", out var y);
            if (!hasA && !hasB) continue;
            var va = hasA ? dfA[a.Id] : (double?)null;
            var vb = hasB ? dfNow[b.Id] : (double?)null;
            var better = va.HasValue && vb.HasValue && Math.Abs(va.Value - vb.Value) >= Analysis.DfStep
                ? (vb.Value > va.Value ? "now" : "option")
                : "";
            rows.Add(new Row { Label = Name(a ?? b) + " daylight", Option = Df(va), Now = Df(vb), Better = better });
        }
        rows.Add(new Row { Label = "Doors", Option = Count(option, "door"), Now = Count(now, "door") });
        rows.Add(new Row { Label = "Windows", Option = Count(option, "window"), Now = Count(now, "window") });
        return rows;
    }

    /// <summary>A room is the same room by its id, else by its name.</summary>
    static string Key(OptionSnapshot.Room room) => !string.IsNullOrEmpty(room.Id) ? "id:" + room.Id : "name:" + (room.Name ?? "");

    static string Name(OptionSnapshot.Room room) => string.IsNullOrWhiteSpace(room?.Name) ? room?.Id ?? "Room" : room.Name;

    static double RoomArea(OptionSnapshot.Room room) => Math.Abs(RoomDetect.Area(room.Outline ?? new List<RoomDetect.Pt>()));

    static string Area(double mm2) => (mm2 / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " m²";

    static string Df(double? df) => df.HasValue ? df.Value.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "–";

    static string Count(OptionSnapshot.Snapshot s, string kind) =>
        s.Openings.Count(o => string.Equals(o.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
}
