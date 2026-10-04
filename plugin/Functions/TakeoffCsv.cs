using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 N2: the takeoff as one long CSV table with the project info on top.
/// UTF-8 with a BOM, comma separated, decimal point, CRLF, RFC 4180
/// quoting. The figures are the takeoff's own (Takeoff.Runs, area_stats'
/// rooms, the lists' openings, the stairs as built), rounded as it rounds
/// them: m and m² to 2 decimals, mm whole. Pure, so it tests headless.
/// </summary>
public static class TakeoffCsv
{
    public const string Source = "Forsk takeoff (quantities are approximate)";

    public static readonly IReadOnlyList<string> Columns = new[]
    {
        "Kind", "Id", "Name", "Type", "Level", "Wall", "Length (m)", "Perimeter (m)", "Area (m²)",
        "Height (mm)", "Thickness (mm)", "Width (mm)", "Sill (mm)", "Risers", "Riser (mm)", "Going (mm)"
    };

    /// <summary>One stair as built, with its forsk:id.</summary>
    public sealed class Stair
    {
        public string Id;
        public Stairs.Flight Flight;
    }

    /// <summary>The file's text. The caller writes Bytes(text): UTF-8 with its BOM.</summary>
    public static string Write(ProjectInfo.Record info, IList<AreaStats.RoomLine> rooms, IList<Takeoff.Run> runs,
        IList<Schedules.Opening> openings, IList<Stair> stairs, DateTime exported)
    {
        var text = new StringBuilder();
        void Line(IEnumerable<string> fields) => text.Append(string.Join(",", fields.Select(Quote))).Append("\r\n");

        foreach (var pair in ProjectInfo.CsvHeader(info))
            Line(new[] { pair.Key, pair.Value });
        Line(new[] { "Exported", exported.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) });
        Line(new[] { "Source", Source });
        text.Append("\r\n");
        Line(Columns);

        string[] Row(string kind)
        {
            var row = Enumerable.Repeat("", Columns.Count).ToArray();
            row[0] = kind;
            return row;
        }
        void Set(string[] row, string column, string value) => row[Index(column)] = value ?? "";

        foreach (var room in rooms ?? new AreaStats.RoomLine[0])
        {
            if (room == null) continue;
            var row = Row("Room");
            Set(row, "Id", room.Id);
            Set(row, "Name", room.Name);
            Set(row, "Level", room.Level);
            Set(row, "Area (m²)", Two(room.AreaMm2 / 1e6));
            if (room.PerimeterMm > 0) Set(row, "Perimeter (m)", Two(room.PerimeterMm / 1000.0));
            Line(row);
        }
        foreach (var run in runs ?? new Takeoff.Run[0])
        {
            if (run == null) continue;
            var row = Row("Wall");
            Set(row, "Id", run.Id);
            Set(row, "Type", WallType(run));
            Set(row, "Level", run.Level);
            Set(row, "Length (m)", Two(run.Length / 1000.0));
            Set(row, "Height (mm)", Whole(run.Height));
            Set(row, "Thickness (mm)", Whole(run.Thickness));
            Set(row, "Area (m²)", Two(run.Area / 1e6));
            Line(row);
        }
        foreach (var kind in new[] { "door", "window" })
            foreach (var opening in (openings ?? new Schedules.Opening[0]).Where(o => o?.Record?.Kind == kind))
            {
                var row = Row(kind == "door" ? "Door" : "Window");
                Set(row, "Id", opening.Mark);
                Set(row, "Type", Schedules.TypeText(opening.Record, false));
                Set(row, "Wall", opening.HostId);
                Set(row, "Width (mm)", Whole(opening.Width));
                Set(row, "Height (mm)", Whole(opening.Head - opening.Sill));
                Set(row, "Sill (mm)", Whole(opening.Sill));
                Line(row);
            }
        foreach (var stair in stairs ?? new Stair[0])
        {
            var flight = stair?.Flight;
            if (flight == null) continue;
            var row = Row("Stair");
            Set(row, "Id", stair.Id);
            Set(row, "Type", "straight");
            Set(row, "Width (mm)", Whole(flight.Width));
            Set(row, "Length (m)", Two(flight.Run / 1000.0));
            Set(row, "Risers", flight.Risers.ToString(CultureInfo.InvariantCulture));
            Set(row, "Riser (mm)", Whole(flight.Riser));
            Set(row, "Going (mm)", Whole(flight.Going));
            Line(row);
        }
        return text.ToString();
    }

    /// <summary>The file's bytes: the UTF-8 BOM (EF BB BF), then the text.</summary>
    public static byte[] Bytes(string text)
    {
        var encoding = new UTF8Encoding(true);
        return encoding.GetPreamble().Concat(encoding.GetBytes(text ?? "")).ToArray();
    }

    /// <summary>"✓ Exported takeoff CSV · 6 rooms, 14 walls, 5 doors and windows, 1 stair · Garage Takeoff.csv".</summary>
    public static string Receipt(int rooms, int walls, int openings, int stairs, string path)
    {
        var parts = new List<string>();
        void Add(int n, string one, string many)
        {
            if (n > 0) parts.Add(n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many));
        }
        Add(rooms, "room", "rooms");
        Add(walls, "wall", "walls");
        Add(openings, "door or window", "doors and windows");
        Add(stairs, "stair", "stairs");
        return "✓ Exported takeoff CSV · " + (parts.Count == 0 ? "nothing" : string.Join(", ", parts)) + " · " + Path.GetFileName(path ?? "");
    }

    /// <summary>The file name beside a PDF or in a DWG folder: "&lt;stem&gt; Takeoff.csv".</summary>
    public static string FileName(string stem)
    {
        var name = (stem ?? "").Trim();
        return (name.Length == 0 ? "Forsk" : name) + " Takeoff.csv";
    }

    /// <summary>As the takeoff groups them: Exterior, Interior, Existing exterior, Existing interior.</summary>
    public static string WallType(Takeoff.Run run)
    {
        var side = run.Outer ? "exterior" : "interior";
        return run.Existing ? "Existing " + side : char.ToUpperInvariant(side[0]) + side.Substring(1);
    }

    /// <summary>RFC 4180: a quote, comma or line break quotes the field and doubles its quotes.</summary>
    public static string Quote(string value)
    {
        var text = value ?? "";
        if (text.IndexOfAny(new[] { '"', ',', '\r', '\n' }) < 0) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    static int Index(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (Columns[i] == column) return i;
        throw new ArgumentException("No CSV column " + column);
    }

    static string Two(double value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    static string Whole(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
}
