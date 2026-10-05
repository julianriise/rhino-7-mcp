using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The Generate 3D chat reply. One line of what was built, then a short
    /// warning only when a step that ran produced nothing or failed. Defaults,
    /// the origin note, tool names and a rooms pass that fell through to a
    /// later success stay out of the reply. Pure: no Rhino.
    /// </summary>
    public static class BakeReply
    {
        /// <summary>True when the walls step ran and added at least one wall.</summary>
        public static bool HasWalls(IEnumerable<string> raw)
        {
            int? walls = null;
            foreach (var line in raw ?? Array.Empty<string>())
                Read(line, ref walls, out _, out _, out _, out _, out _, out _, out _);
            return (walls ?? 0) > 0;
        }

        /// <summary>The lines the window shows. <paramref name="planHidden"/> adds the 2D note.</summary>
        public static List<string> Format(IEnumerable<string> raw, bool planHidden)
        {
            int? walls = null;
            int? doors = null;
            int? windows = null;
            int? roomsFrom = null;
            int? roomsDetect = null;
            var openings = false;
            var rooms = false;
            var notes = new List<string>();
            var errors = new List<string>();
            var extra = new List<string>();
            foreach (var line in raw ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (IsQuiet(line)) continue;
                if (!line.Contains(" · "))
                {
                    notes.Add(line.Trim());
                    continue;
                }
                Read(line, ref walls, ref doors, ref windows, ref roomsFrom, ref roomsDetect, ref openings, ref rooms, errors, extra);
            }

            if (notes.Count > 0 && walls == null && !openings && !rooms && errors.Count == 0)
                return notes;

            var roomCount = roomsDetect ?? roomsFrom ?? 0;
            var made = new List<string>();
            if ((walls ?? 0) > 0) made.Add(Qty(walls.Value, "wall", "walls"));
            if ((doors ?? 0) > 0) made.Add(Qty(doors.Value, "door", "doors"));
            if ((windows ?? 0) > 0) made.Add(Qty(windows.Value, "window", "windows"));
            if (roomCount > 0) made.Add(Qty(roomCount, "room", "rooms"));

            var summary = made.Count == 0 ? "Built 3D." : "Built 3D: " + string.Join(", ", made) + ".";
            if (planHidden)
                summary = summary.TrimEnd('.') + ". 2D layers hidden.";

            var result = new List<string> { summary };
            if (errors.Count > 0)
            {
                result.AddRange(errors);
                return result;
            }
            if (walls == 0) result.Add("No walls.");
            if (openings && (doors ?? 0) + (windows ?? 0) == 0) result.Add("No openings.");
            if (rooms && roomCount == 0) result.Add("No rooms.");
            result.AddRange(extra);
            return result;
        }

        static void Read(string line, ref int? walls, out bool openings, out int? doors, out int? windows,
            out int? roomsFrom, out int? roomsDetect, out bool rooms, out List<string> errors)
        {
            openings = false;
            doors = windows = roomsFrom = roomsDetect = null;
            rooms = false;
            errors = null;
            var open = false;
            var sawRooms = false;
            Read(line, ref walls, ref doors, ref windows, ref roomsFrom, ref roomsDetect, ref open, ref sawRooms, null, null);
            openings = open;
            rooms = sawRooms;
        }

        static void Read(string line, ref int? walls, ref int? doors, ref int? windows, ref int? roomsFrom,
            ref int? roomsDetect, ref bool openings, ref bool rooms, List<string> errors, List<string> extra)
        {
            if (string.IsNullOrWhiteSpace(line) || IsQuiet(line) || !line.Contains(" · ")) return;
            var parts = line.Split(new[] { " · " }, 4, StringSplitOptions.None);
            var head = parts[0].Trim();
            var state = parts.Length > 1 ? parts[1].Trim() : "";
            var third = parts.Length > 2 ? parts[2].Trim() : "";
            var fourth = parts.Length > 3 ? parts[3].Trim() : "";
            if (state.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                errors?.Add(string.IsNullOrEmpty(third) ? "failed" : One(third));
                return;
            }
            var count = Count(third);
            if (head.StartsWith("walls_from_layer", StringComparison.OrdinalIgnoreCase))
            {
                walls = count ?? 0;
                if ((count ?? 0) > 0 && fourth.Length > 0) extra?.Add(One(fourth));
            }
            else if (head.StartsWith("openings_from_layer", StringComparison.OrdinalIgnoreCase))
            {
                openings = true;
                var kind = Kind(head + " " + third + " " + fourth);
                var n = state.Equals("skipped", StringComparison.OrdinalIgnoreCase) ? 0 : count ?? 0;
                if (kind == "door") doors = n;
                else if (kind == "window") windows = n;
            }
            else if (head.StartsWith("rooms_from_layer", StringComparison.OrdinalIgnoreCase))
            {
                rooms = true;
                roomsFrom = count ?? 0;
            }
            else if (head.StartsWith("rooms_detect", StringComparison.OrdinalIgnoreCase))
            {
                rooms = true;
                roomsDetect = count ?? 0;
            }
        }

        /// <summary>The defaults sentence and the origin note are not part of the reply.</summary>
        static bool IsQuiet(string line)
        {
            var text = (line ?? "").Trim();
            return text.StartsWith("Defaults:", StringComparison.OrdinalIgnoreCase)
                || text.IndexOf("existing-building corner", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string Kind(string text)
        {
            if (text.IndexOf("window", StringComparison.OrdinalIgnoreCase) >= 0) return "window";
            if (text.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0) return "door";
            return "";
        }

        static int? Count(string text)
        {
            int n;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : (int?)null;
        }

        static string Qty(int n, string one, string many)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        }

        static string One(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
