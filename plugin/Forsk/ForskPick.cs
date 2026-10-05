using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Selection S4: the one line the window shows for what is picked, in
    /// words a builder uses. "North wall · 200 mm", "Bedroom · 12.4 m²",
    /// "Door D03 · 900 × 2100", "3 walls", "4 objects". It is the header and
    /// the chat's target. A room's plate and a door's block stand for their
    /// marker, so picking both is one thing. Strings live in ForskText under
    /// pick.*, Norwegian beside English. No RhinoCommon.
    /// </summary>
    public static class ForskPick
    {
        static readonly string[] Sides = { "north", "south", "east", "west" };

        public static string Line(IEnumerable<ChipRow> rows, bool nb)
        {
            var things = Things(rows);
            if (things.Count == 0) return Text("pick.none", nb);
            var kinds = things.Select(Kind).Distinct().ToList();
            if (kinds.Count > 1)
            {
                var openings = kinds.All(k => k == "door" || k == "window" || k == "opening");
                if (openings) return Text("pick.openings", nb, "n", Count(things.Count));
                // R2: walls with doors and windows, by kind: "2 walls, 1 window".
                if (kinds.Contains("wall") && kinds.All(k => k == "wall" || k == "door" || k == "window" || k == "opening"))
                    return string.Join(", ", new[] { "wall", "door", "window", "opening" }
                        .Select(k => (Kind: k, N: things.Count(t => Kind(t) == k)))
                        .Where(p => p.N > 0)
                        .Select(p => p.N == 1 ? Text("pick.mix." + p.Kind, nb) : Text("pick." + p.Kind + "s", nb, "n", Count(p.N))));
                return Text("pick.objects", nb, "n", Count(things.Count));
            }
            var kind = kinds[0];
            if (things.Count == 1) return One(things[0], kind, nb);
            switch (kind)
            {
                case "wall": return Text("pick.walls", nb, "n", Count(things.Count));
                case "room": return Text("pick.rooms", nb, "n", Count(things.Count), "area", SquareMetres(things.Sum(r => Mm(r.Area) ?? 0), nb));
                case "door": return Text("pick.doors", nb, "n", Count(things.Count));
                case "window": return Text("pick.windows", nb, "n", Count(things.Count));
                case "opening": return Text("pick.openings", nb, "n", Count(things.Count));
                case "stair": return Text("pick.stairs", nb, "n", Count(things.Count));
                default: return Text("pick.objects", nb, "n", Count(things.Count));
            }
        }

        /// <summary>
        /// A selected row that drives the pick line, the suggestion bar and the
        /// selection key. The daylight mesh is visible and stays out of all three.
        /// </summary>
        public static bool DrivesSelection(ChipRow row) =>
            row != null && row.Selected
            && !string.Equals(row.Kind, "analysis", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The selected rows as things. A plate whose marker is also picked is
        /// that room. A frame, leaf, glass, block, or group is its opening marker.
        /// The daylight mesh is not a thing.
        /// </summary>
        public static List<ChipRow> Things(IEnumerable<ChipRow> rows)
        {
            var all = (rows ?? Enumerable.Empty<ChipRow>()).Where(r => r != null).ToList();
            var picked = all.Where(DrivesSelection).ToList();
            var parts = all.ConvertAll(ToPart);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var things = new List<ChipRow>();
            foreach (var row in picked)
            {
                if (IsPlate(row) && PickedId(picked, row.Marker)) continue;
                var markerId = OpeningResolve.MarkerOf(parts, ToPart(row));
                if (string.IsNullOrEmpty(markerId) && !string.IsNullOrEmpty(row.Marker) && PickedId(picked, row.Marker))
                    continue;
                var key = string.IsNullOrEmpty(markerId) ? "id:" + (row.Id ?? "") : "opening:" + markerId;
                if (!seen.Add(key)) continue;
                if (!string.IsNullOrEmpty(markerId))
                {
                    var marker = all.Find(r => SameId(r.Id, markerId));
                    if (marker != null)
                    {
                        things.Add(marker.Selected ? marker : marker.SelectedCopy());
                        continue;
                    }
                }
                things.Add(row);
            }
            return things;
        }

        static OpeningResolve.Part ToPart(ChipRow row) => new OpeningResolve.Part
        {
            Id = row.Id,
            Kind = row.Kind,
            Member = row.Part,
            Marker = row.Marker,
            ForskId = row.ForskId,
            Group = row.Group
        };

        static bool IsPlate(ChipRow row) =>
            string.Equals(row?.Kind, "room_plate", StringComparison.OrdinalIgnoreCase);

        static bool PickedId(List<ChipRow> picked, string id) =>
            !string.IsNullOrEmpty(id) && picked.Exists(r => SameId(r.Id, id));

        static bool SameId(string a, string b) =>
            !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The one picked wall that is a single run, or null: a whole record
        /// (more than one run), a wall with no run name, or anything else picked.
        /// </summary>
        public static ChipRow OneRunWall(IEnumerable<ChipRow> rows)
        {
            var things = Things(rows);
            if (things.Count != 1 || Kind(things[0]) != "wall") return null;
            var wall = things[0];
            return wall.Runs == 1 && !string.IsNullOrEmpty(wall.RunName) ? wall : null;
        }

        /// <summary>The run's name inside a sentence: "the north wall", "veggen i nord", "the wall at (4000, 2000)".</summary>
        public static string InSentence(string runName, bool nb)
        {
            if (TrySide(runName, out var side))
                return nb ? "veggen i " + Compass(side, true) : "the " + side + " wall";
            if (TryAt(runName, out var at))
                return nb ? "veggen ved " + at : "the wall at " + at;
            return runName ?? "";
        }

        /// <summary>The compass word, in the sentence's language.</summary>
        public static string Compass(string side, bool nb) => Text("compass." + side, nb);

        static string One(ChipRow row, string kind, bool nb)
        {
            switch (kind)
            {
                case "wall":
                    if (row.Runs > 1) return Text("pick.wall.whole", nb, "id", row.ForskId ?? "", "n", Count(row.Runs));
                    var name = Heading(row, nb);
                    var t = Mm(row.Thickness);
                    return t.HasValue ? name + " · " + Count((int)Math.Round(t.Value)) + " mm" : name;
                case "room":
                    var room = string.IsNullOrWhiteSpace(row.Name) ? Text("pick.room", nb) : row.Name.Trim();
                    var area = Mm(row.Area);
                    var line = area.HasValue && area.Value > 0 ? room + " · " + SquareMetres(area.Value, nb) : room;
                    return line + global::RhinoMCPPlugin.Functions.RoomTypes.LineSuffix(row.RoomType);
                case "door":
                case "window":
                case "opening":
                    var head = Text("pick." + kind, nb) + (string.IsNullOrWhiteSpace(row.Mark) ? "" : " " + row.Mark.Trim());
                    var width = Mm(row.Width);
                    var sill = Mm(row.Sill) ?? 0;
                    var top = Mm(row.Head);
                    if (!width.HasValue || !top.HasValue) return head;
                    return head + " · " + Count((int)Math.Round(width.Value)) + " × " + Count((int)Math.Round(top.Value - sill));
                case "stair":
                    return global::RhinoMCPPlugin.Functions.Stairs.PickLine(row.Risers, nb);
                default:
                    return Text("pick.object", nb);
            }
        }

        /// <summary>The wall's name as a heading: "North wall", "Veggen i nord", "Wall at (4000, 2000)", else "Wall w03".</summary>
        static string Heading(ChipRow row, bool nb)
        {
            if (TrySide(row.RunName, out var side))
                return nb ? "Veggen i " + Compass(side, true) : char.ToUpperInvariant(side[0]) + side.Substring(1) + " wall";
            if (TryAt(row.RunName, out var at))
                return Text("pick.wall.at", nb, "at", at);
            return Text("pick.wall", nb, "id", row.ForskId ?? "").Trim();
        }

        static bool TrySide(string runName, out string side)
        {
            side = null;
            if (string.IsNullOrEmpty(runName)) return false;
            foreach (var s in Sides)
                if (string.Equals(runName, "the " + s + " wall", StringComparison.Ordinal))
                {
                    side = s;
                    return true;
                }
            return false;
        }

        static bool TryAt(string runName, out string at)
        {
            at = null;
            const string prefix = "the wall at ";
            if (string.IsNullOrEmpty(runName) || !runName.StartsWith(prefix, StringComparison.Ordinal)) return false;
            at = runName.Substring(prefix.Length);
            return true;
        }

        static string Kind(ChipRow row)
        {
            if (!row.Generated || row.Existing) return "object";
            var kind = (row.Kind ?? "").ToLowerInvariant();
            if (kind == "wall") return "wall";
            if (kind == "room" || kind == "room_plate") return "room";
            if (kind == "stair") return "stair";
            if (kind == "opening" || kind == "opening_marker")
            {
                var opening = (row.OpeningKind ?? "").ToLowerInvariant();
                return opening == "door" || opening == "window" ? opening : "opening";
            }
            return "object";
        }

        static string Text(string key, bool nb, params string[] pairs)
        {
            var localized = nb && ForskText.Has(key + ".nb") ? key + ".nb" : key;
            return ForskText.Format(localized, pairs);
        }

        static double? Mm(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }

        static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

        /// <summary>12.4 m², or 12,4 m² in Norwegian.</summary>
        static string SquareMetres(double mm2, bool nb)
        {
            var text = (mm2 / 1e6).ToString("0.0", CultureInfo.InvariantCulture);
            return (nb ? text.Replace('.', ',') : text) + " m²";
        }
    }
}
