using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Door, window and room schedules (F5.1) from the model's own records: the
/// opening markers and the room markers the plan tags show. Each door and
/// window carries a stable mark (D01, V01) that the plan prints beside it and
/// the schedule prints on its row. Pure, no Rhino document, so it tests
/// headless. Sizes are paper millimetres.
/// </summary>
public static class Schedules
{
    public const string MarkKey = "forsk:mark";
    public const string DoorPrefix = "D";
    public const string WindowPrefix = "V";

    public const double TextMm = 2.0;
    public const double TitleMm = 3.5;
    public const double RowMm = 4.0;
    /// <summary>Space the title takes above its table.</summary>
    public const double TitleSpaceMm = 6.0;
    public const double PadMm = 1.5;
    /// <summary>Between two tables in a column, between columns, and beside the plan.</summary>
    public const double GapMm = 8.0;
    /// <summary>
    /// Glyph advance at TextMm when Rhino cannot measure the text: generous,
    /// since a cell must hold its text (Sidehengslet ran into the next column
    /// at 0.62 × the height).
    /// </summary>
    const double CharMm = 0.8 * TextMm;
    const double MinColumnMm = 8.0;

    public sealed class Opening
    {
        public string Id;
        public string Mark;
        public OpeningTypes.Record Record;
        public double X;
        public double Y;
        public double Width;
        public double Sill;
        public double Head;
        /// <summary>Tagged room names on each side of the wall, null where there is none.</summary>
        public string[] Rooms = new string[2];
    }

    public sealed class Room
    {
        public string Id;
        public string Name;
        public double AreaMm2;
    }

    public sealed class Table
    {
        public string Kind;
        public string Title;
        public string[] Heads;
        public bool[] Right;
        public double[] Widths;
        /// <summary>Row id: the mark, or the room id.</summary>
        public List<string> Ids = new List<string>();
        public List<string[]> Rows = new List<string[]>();
        public string[] Total;
        /// <summary>One line printed under the table, after its last row. Null for none.</summary>
        public string Note;
        /// <summary>The note's printed width (Fit sets it): its column is at least this wide.</summary>
        public double NoteWidth;

        public double Width
        {
            get
            {
                var width = 0.0;
                foreach (var w in Widths) width += w;
                return width;
            }
        }

        /// <summary>Body lines: the rows, then the total.</summary>
        public int Lines
        {
            get { return Rows.Count + (Total == null ? 0 : 1); }
        }

        /// <summary>The width a column needs for this table: the table, or its note when that is wider.</summary>
        public double ColumnWidth
        {
            get { return Note == null ? Width : Math.Max(Width, NoteWidth); }
        }

        /// <summary>Cells of body line i: a row, or the total.</summary>
        public string[] Line(int i)
        {
            if (i < Rows.Count) return Rows[i];
            return Total != null && i == Rows.Count ? Total : null;
        }

        public string LineId(int i)
        {
            if (i < Rows.Count) return Ids[i];
            return Total != null && i == Rows.Count ? "total" : "";
        }
    }

    /// <summary>
    /// Part of a table in one column. Top is measured down from the top of the
    /// schedule area, X right from its left edge.
    /// </summary>
    public sealed class Block
    {
        public Table Table;
        public int First;
        public int Count;
        public bool Continued;
        public double X;
        public double Top;
        /// <summary>0 for the first schedules page, 1 for the next, and on.</summary>
        public int Page;
        /// <summary>The table's note goes under this block: its last.</summary>
        public bool Note;

        public string Title
        {
            get { return Continued ? Table.Title + " (forts.)" : Table.Title; }
        }

        /// <summary>Title, head row, the body lines, and the note's line when it is here.</summary>
        public double Height
        {
            get { return TitleSpaceMm + RowMm * (1 + Count + (Note ? 1 : 0)); }
        }
    }

    public static string Prefix(string kind)
    {
        return string.Equals(kind, "window", StringComparison.OrdinalIgnoreCase) ? WindowPrefix : DoorPrefix;
    }

    public static string Format(string prefix, int number)
    {
        return prefix + number.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>The number of a mark with this prefix: D07 is 7. False for any other text.</summary>
    public static bool TryNumber(string mark, string prefix, out int number)
    {
        number = 0;
        if (string.IsNullOrEmpty(mark) || !mark.StartsWith(prefix, StringComparison.Ordinal)
            || mark.Length == prefix.Length || mark.Length > prefix.Length + 6)
            return false;
        for (var i = prefix.Length; i < mark.Length; i++)
            if (mark[i] < '0' || mark[i] > '9') return false;
        number = int.Parse(mark.Substring(prefix.Length), CultureInfo.InvariantCulture);
        return number > 0;
    }

    /// <summary>
    /// The mark of each opening. A mark the opening already carries stays, so
    /// it survives edits, rebuilds, and other openings coming and going. A
    /// missing, foreign or repeated mark (a copied marker) takes the next free
    /// number, in reading order: top of the plan first, then left to right.
    /// Doors are D01…, windows V01…. next holds the next free number per
    /// prefix from the last run and is updated, so a deleted mark is not given
    /// to a new opening. With no mark of a prefix left, numbering starts at 01.
    /// </summary>
    public static string[] AssignMarks(IList<Opening> openings, IDictionary<string, int> next)
    {
        var marks = new string[openings.Count];
        var order = ReadingOrder(openings);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var free = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var i in order)
        {
            var prefix = Prefix(openings[i].Record?.Kind);
            if (!TryNumber(openings[i].Mark, prefix, out var number)) continue;
            var mark = Format(prefix, number);
            if (!used.Add(mark)) continue;
            marks[i] = mark;
            var stored = next != null && next.TryGetValue(prefix, out var n) ? n : 1;
            free[prefix] = Math.Max(free.TryGetValue(prefix, out var f) ? f : stored, number + 1);
        }
        foreach (var i in order)
        {
            if (marks[i] != null) continue;
            var prefix = Prefix(openings[i].Record?.Kind);
            if (!free.ContainsKey(prefix)) free[prefix] = 1;
            string mark;
            do mark = Format(prefix, free[prefix]++);
            while (!used.Add(mark));
            marks[i] = mark;
        }
        if (next != null)
        {
            next.Clear();
            foreach (var pair in free) next[pair.Key] = pair.Value;
        }
        return marks;
    }

    /// <summary>
    /// The side of the wall, -1 or +1 across it in the symbol frame, that the
    /// plan prints the mark on. One rule for doors and windows: outside, the
    /// side with no room, whichever way a leaf swings. Between two rooms (or
    /// with no room found) a hinged door's mark takes the side its leaf does
    /// not swing into, anything else +1.
    /// </summary>
    public static int MarkSide(OpeningTypes.Record record, int yInward, string[] rooms)
    {
        var below = rooms != null && rooms.Length > 0 ? rooms[0] : null;
        var above = rooms != null && rooms.Length > 1 ? rooms[1] : null;
        if ((below == null) != (above == null))
            return below == null ? -1 : 1;
        var id = record?.TypeId ?? "";
        if (id == "door.hinged_single" || id == "door.hinged_double")
            return -OpeningTypes.SwingSign(record, yInward);
        return 1;
    }

    /// <summary>Paper cap height of a plan mark: half the room tags' 2.5 mm, so marks do not crowd the plan.</summary>
    public const double MarkMm = 1.25;

    /// <summary>
    /// A mark to place on the plan, in drawing mm: the wall centre at the
    /// opening, unit vectors along the wall and across it toward the mark's
    /// side (MarkSide), the wall's half thickness, the opening's half width,
    /// the mark text's half extents, and the paper gap times the scale.
    /// </summary>
    public sealed class MarkSpot
    {
        public RoomDetect.Pt At;
        public RoomDetect.Pt Along;
        public RoomDetect.Pt Out;
        public double HalfThick;
        public double HalfWidth;
        public double Hx;
        public double Hy;
        public double Gap;
    }

    /// <summary>How many steps away from the wall a mark may go to find a clear spot.</summary>
    public const int MarkSteps = 3;

    /// <summary>
    /// Where a mark's centre goes. First choice: at the opening's centre, the
    /// gap clear of the wall face on its side (MarkSide). If a room tag or a
    /// mark already placed (taken, kept the gap away) or wall poché (walls,
    /// each wall's rings read even-odd) is in the way, the mark steps away
    /// from the wall, one mark height and a gap at a time, up to MarkSteps;
    /// at each step it tries its own face, then the other, each slid along
    /// the wall within the opening's width. A door into a tiny room whose
    /// name overflows it so takes the other room's side, as a drafter would.
    /// Tags are placed first and keep their place. None clear: the first
    /// choice, and false.
    /// </summary>
    public static bool PlaceMark(
        MarkSpot spot, IList<RoomDetect.Box> taken, List<List<List<RoomDetect.Pt>>> walls, out RoomDetect.Pt centre)
    {
        var across = Math.Abs(spot.Out.X) * spot.Hx + Math.Abs(spot.Out.Y) * spot.Hy;
        var along = Math.Abs(spot.Along.X) * spot.Hx + Math.Abs(spot.Along.Y) * spot.Hy;
        var slide = 2.0 * along + spot.Gap;
        var shifts = new List<double> { 0.0 };
        for (var k = 1; k * slide <= spot.HalfWidth + along + 1e-9; k++)
        {
            shifts.Add(k * slide);
            shifts.Add(-k * slide);
        }
        var near = spot.HalfThick + spot.Gap + across;
        var step = 2.0 * across + spot.Gap;
        centre = Spot(spot, 1, near, 0.0);
        for (var k = 0; k <= MarkSteps; k++)
        {
            foreach (var side in new[] { 1, -1 })
            {
                foreach (var shift in shifts)
                {
                    var at = Spot(spot, side, near + k * step, shift);
                    if (!Clear(at, spot, taken, walls)) continue;
                    centre = at;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// True when the box touches wall poché: a wall edge crosses it, or its
    /// centre lies inside a wall (even-odd over that wall's rings).
    /// </summary>
    public static bool OnWalls(RoomDetect.Box box, List<List<List<RoomDetect.Pt>>> walls)
    {
        if (walls == null) return false;
        var centre = new RoomDetect.Pt((box.MinX + box.MaxX) / 2.0, (box.MinY + box.MaxY) / 2.0);
        foreach (var wall in walls)
        {
            if (wall == null) continue;
            var inside = false;
            foreach (var ring in wall)
            {
                if (ring == null || ring.Count < 2) continue;
                if (RoomDetect.Contains(ring, centre)) inside = !inside;
                for (var i = 0; i < ring.Count; i++)
                    if (SegmentHitsBox(ring[i], ring[(i + 1) % ring.Count], box)) return true;
            }
            if (inside) return true;
        }
        return false;
    }

    /// <summary>Liang-Barsky: does segment a-b cross or lie in the box.</summary>
    public static bool SegmentHitsBox(RoomDetect.Pt a, RoomDetect.Pt b, RoomDetect.Box box)
    {
        double t0 = 0, t1 = 1;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            var r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
        return Clip(-dx, a.X - box.MinX) && Clip(dx, box.MaxX - a.X)
            && Clip(-dy, a.Y - box.MinY) && Clip(dy, box.MaxY - a.Y);
    }

    static RoomDetect.Pt Spot(MarkSpot spot, int side, double reach, double shift)
    {
        return new RoomDetect.Pt(
            spot.At.X + side * spot.Out.X * reach + spot.Along.X * shift,
            spot.At.Y + side * spot.Out.Y * reach + spot.Along.Y * shift);
    }

    static bool Clear(RoomDetect.Pt at, MarkSpot spot, IList<RoomDetect.Box> taken, List<List<List<RoomDetect.Pt>>> walls)
    {
        var box = MarkBox(at, spot.Hx, spot.Hy);
        if (taken != null)
            foreach (var other in taken)
                if (Overlaps(box, other, spot.Gap)) return false;
        return !OnWalls(box, walls);
    }

    /// <summary>The box of a mark text centred at at.</summary>
    public static RoomDetect.Box MarkBox(RoomDetect.Pt at, double hx, double hy)
    {
        return new RoomDetect.Box(at.X - hx, at.Y - hy, at.X + hx, at.Y + hy);
    }

    /// <summary>True when two boxes come closer than clearance.</summary>
    public static bool Overlaps(RoomDetect.Box a, RoomDetect.Box b, double clearance)
    {
        return a.MinX < b.MaxX + clearance && b.MinX < a.MaxX + clearance
            && a.MinY < b.MaxY + clearance && b.MinY < a.MaxY + clearance;
    }

    static List<int> ReadingOrder(IList<Opening> openings)
    {
        var order = new List<int>();
        for (var i = 0; i < openings.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            var byRow = Math.Round(openings[b].Y).CompareTo(Math.Round(openings[a].Y));
            if (byRow != 0) return byRow;
            var byColumn = openings[a].X.CompareTo(openings[b].X);
            return byColumn != 0 ? byColumn : a.CompareTo(b);
        });
        return order;
    }

    /// <summary>Dørliste: mark, type, width × height, hand and swing, the rooms either side.</summary>
    public static Table DoorTable(IList<Opening> doors)
    {
        var table = new Table
        {
            Kind = "door",
            Title = "Dørliste",
            Heads = new[] { "Nr.", "Type", "B × H (mm)", "Slag", "Rom" },
            Right = new[] { false, false, true, false, false }
        };
        foreach (var door in ByMark(doors))
        {
            table.Ids.Add(door.Mark);
            table.Rows.Add(new[] { door.Mark, TypeText(door.Record), Size(door), Hand(door.Record), RoomText(door.Rooms) });
        }
        return Fit(table, null);
    }

    /// <summary>Vindusliste: mark, type, width × height, sill height, the room it lights.</summary>
    public static Table WindowTable(IList<Opening> windows)
    {
        var table = new Table
        {
            Kind = "window",
            Title = "Vindusliste",
            Heads = new[] { "Nr.", "Type", "B × H (mm)", "Brystning (mm)", "Rom" },
            Right = new[] { false, false, true, true, false }
        };
        foreach (var window in ByMark(windows))
        {
            table.Ids.Add(window.Mark);
            table.Rows.Add(new[] { window.Mark, TypeText(window.Record), Size(window), Mm(window.Sill), RoomText(window.Rooms) });
        }
        return Fit(table, null);
    }

    /// <summary>
    /// Romliste: each tagged room's name and area as its plan tag prints them,
    /// in room id order, then the sum of the rooms' areas. BRA and BTA are in
    /// the Arealtabell on the front sheet, once.
    /// </summary>
    public static Table RoomTable(IList<Room> rooms)
    {
        var table = new Table
        {
            Kind = "room",
            Title = "Romliste",
            Heads = new[] { "Rom", "Areal" },
            Right = new[] { false, true }
        };
        var sorted = new List<Room>(rooms);
        sorted.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var total = 0.0;
        foreach (var room in sorted)
        {
            table.Ids.Add(room.Id);
            table.Rows.Add(new[] { room.Name, OpeningTypes.AreaText(room.AreaMm2) });
            total += room.AreaMm2;
        }
        if (sorted.Count > 0)
            table.Total = new[] { "Sum", OpeningTypes.AreaText(total) };
        return Fit(table, null);
    }

    public const string AreaNote = "Arealer er ca.-tall fra modellen, ikke målt etter NS 3940.";

    /// <summary>
    /// Arealtabell for the front sheet, from area_stats' own result (no second
    /// computation): per floor ("1. etasje") its BTA, BRA and Netto (the sum
    /// of its rooms), then the net area per use, largest first. A floor whose
    /// BRA and BTA could not be derived shows Netto alone, and the note under
    /// the table says so. Every figure is an estimate, and the note says
    /// that too. No rooms: no lines.
    /// </summary>
    public static Table AreaTable(AreaStats.Result result)
    {
        var table = new Table
        {
            Kind = "area",
            Title = "Arealer",
            Heads = new[] { "", "Areal" },
            Right = new[] { false, true }
        };
        if (result == null || result.Floors.Count == 0) return Fit(table, null);
        var missing = new List<string>();
        void Row(string id, string label, string value)
        {
            table.Ids.Add(id);
            table.Rows.Add(new[] { label, value });
        }
        foreach (var floor in result.Floors)
        {
            var level = string.IsNullOrWhiteSpace(floor.Key) ? "0" : floor.Key.Trim();
            var name = AreaStats.FloorName(level, true);
            Row("floor-" + level, name, "");
            var gross = result.Gross?.Find(g => g != null && (string.IsNullOrWhiteSpace(g.Level) ? "0" : g.Level.Trim()) == level);
            if (gross?.BtaMm2 != null) Row("bta-" + level, "BTA", OpeningTypes.AreaText(gross.BtaMm2.Value));
            if (gross?.BraMm2 != null) Row("bra-" + level, "BRA", OpeningTypes.AreaText(gross.BraMm2.Value));
            if (gross?.BtaMm2 == null && gross?.BraMm2 == null) missing.Add(name);
            Row("net-" + level, "Netto", OpeningTypes.AreaText(floor.AreaMm2));
        }
        Row("uses", "Netto per bruk", "");
        foreach (var use in result.Uses)
            Row("use-" + use.Key, use.Key, OpeningTypes.AreaText(use.AreaMm2));
        table.Note = AreaNote + (missing.Count == 0 ? "" : " " + string.Join(", ", missing) + ": BRA og BTA mangler.");
        return Fit(table, null);
    }

    /// <summary>One sheet on the Tegningsliste. Scale 0: the sheet has none (the lists).</summary>
    public sealed class Drawing
    {
        public string Number;
        public string Title;
        public int Scale;
    }

    /// <summary>Tegningsliste: each sheet that prints, in set order: its number, its title, its scale.</summary>
    public static Table DrawingList(IList<Drawing> sheets)
    {
        var table = new Table
        {
            Kind = "drawings",
            Title = "Tegningsliste",
            Heads = new[] { "Nr.", "Tegning", "Målestokk" },
            Right = new[] { false, false, false }
        };
        foreach (var sheet in sheets ?? new Drawing[0])
        {
            if (sheet == null || string.IsNullOrEmpty(sheet.Number)) continue;
            table.Ids.Add(sheet.Number);
            table.Rows.Add(new[]
            {
                sheet.Number, sheet.Title ?? "",
                sheet.Scale > 0 ? "1:" + sheet.Scale.ToString(CultureInfo.InvariantCulture) : ""
            });
        }
        return Fit(table, null);
    }

    /// <summary>Sheet title for the lists shown: Dørliste; Dør- og vindusliste; Dør-, vindus- og romliste.</summary>
    public static string SheetTitle(IList<string> kinds)
    {
        var stems = new List<string>();
        if (kinds.Contains("door")) stems.Add("Dør");
        if (kinds.Contains("window")) stems.Add("vindus");
        if (kinds.Contains("room")) stems.Add("rom");
        if (stems.Count == 0) return "";
        stems[0] = char.ToUpperInvariant(stems[0][0]) + stems[0].Substring(1);
        if (stems.Count == 1) return stems[0] + "liste";
        var head = string.Join("-, ", stems.GetRange(0, stems.Count - 1).ToArray());
        return head + "- og " + stems[stems.Count - 1] + "liste";
    }

    /// <summary>
    /// Stacks the tables down a column of the given height and flows on into
    /// the next column when one runs out, repeating the title (forts.) and the
    /// head. A column that would run past the page width starts the next page,
    /// so long lists take as many pages as they need. A table starts a new
    /// column rather than leave fewer than three of its lines under a title.
    /// Columns step right by their widest table plus GapMm. Null when a page
    /// cannot hold a title, a head and one line, or a table is wider than it.
    /// </summary>
    public static List<Block> Flow(IList<Table> tables, double width, double height)
    {
        var blocks = new List<Block>();
        var page = 0;
        double x = 0, y = 0, column = 0;
        foreach (var table in tables)
        {
            if (table == null || table.Lines == 0) continue;
            if (table.ColumnWidth > width + 1e-9) return null;
            var first = 0;
            while (first < table.Lines)
            {
                if (x + table.ColumnWidth > width + 1e-9)
                {
                    page++;
                    x = 0;
                    y = 0;
                    column = 0;
                }
                var room = (int)Math.Floor((height - y - TitleSpaceMm - RowMm) / RowMm + 1e-9);
                var left = table.Lines - first;
                if (room < Math.Min(3, left) && y > 0)
                {
                    x += column + GapMm;
                    y = 0;
                    column = 0;
                    continue;
                }
                if (room < 1) return null;
                var count = Math.Min(room, left);
                var note = table.Note != null && count == left;
                if (note && count + 1 > room)
                {
                    // The note needs a line more: the last row goes on with it.
                    if (count > 1)
                    {
                        count--;
                        note = false;
                    }
                    else if (y > 0)
                    {
                        x += column + GapMm;
                        y = 0;
                        column = 0;
                        continue;
                    }
                    else return null;
                }
                var block = new Block
                {
                    Table = table,
                    First = first,
                    Count = count,
                    Continued = first > 0,
                    X = x,
                    Top = y,
                    Page = page,
                    Note = note
                };
                blocks.Add(block);
                column = Math.Max(column, note ? table.ColumnWidth : table.Width);
                y += block.Height + GapMm;
                first += block.Count;
                if (first < table.Lines)
                {
                    x += column + GapMm;
                    y = 0;
                    column = 0;
                }
            }
        }
        return blocks;
    }

    /// <summary>How many pages the blocks take.</summary>
    public static int Pages(IList<Block> blocks)
    {
        var pages = 0;
        foreach (var block in blocks) pages = Math.Max(pages, block.Page + 1);
        return pages;
    }

    static List<Opening> ByMark(IList<Opening> openings)
    {
        var sorted = new List<Opening>(openings);
        sorted.Sort((a, b) =>
        {
            var na = MarkNumber(a);
            var nb = MarkNumber(b);
            return na != nb ? na.CompareTo(nb) : string.CompareOrdinal(a.Mark, b.Mark);
        });
        return sorted;
    }

    static int MarkNumber(Opening opening)
    {
        return TryNumber(opening.Mark, Prefix(opening.Record?.Kind), out var n) ? n : int.MaxValue;
    }

    static string TypeText(OpeningTypes.Record record)
    {
        var label = record?.Def?.ScheduleLabel ?? "";
        var door = string.Equals(record?.Kind, "door", StringComparison.OrdinalIgnoreCase);
        return door && record.Glazed ? label + " m/glass" : label;
    }

    static string Size(Opening opening)
    {
        return Mm(opening.Width) + " × " + Mm(opening.Head - opening.Sill);
    }

    static string Mm(double value)
    {
        return Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>Hand as V (venstre) or H (høyre), swing as inn or ut: V inn.</summary>
    static string Hand(OpeningTypes.Record record)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(record?.Hand))
            parts.Add(string.Equals(record.Hand, "R", StringComparison.OrdinalIgnoreCase) ? "H" : "V");
        if (!string.IsNullOrEmpty(record?.Swing))
            parts.Add(string.Equals(record.Swing, "out", StringComparison.OrdinalIgnoreCase) ? "ut" : "inn");
        return parts.Count == 0 ? "–" : string.Join(" ", parts.ToArray());
    }

    static string RoomText(string[] rooms)
    {
        var names = new List<string>();
        if (rooms != null)
            foreach (var name in rooms)
                if (!string.IsNullOrEmpty(name)) names.Add(name);
        return names.Count == 0 ? "–" : string.Join(" / ", names.ToArray());
    }

    /// <summary>
    /// Column widths from the widest text in each column, head included, plus
    /// the cell padding. measure gives a text's printed width in paper mm at
    /// TextMm (Rhino's layout); without it, or when it gives nothing, the width
    /// is estimated from the character count.
    /// </summary>
    public static Table Fit(Table table, Func<string, double> measure)
    {
        table.Widths = new double[table.Heads.Length];
        for (var c = 0; c < table.Heads.Length; c++)
        {
            var widest = TextWidth(table.Heads[c], measure);
            for (var i = 0; i < table.Lines; i++)
            {
                var line = table.Line(i);
                if (line != null && c < line.Length && line[c] != null)
                    widest = Math.Max(widest, TextWidth(line[c], measure));
            }
            table.Widths[c] = Math.Max(MinColumnMm, Math.Ceiling(widest + 2 * PadMm));
        }
        table.NoteWidth = table.Note == null ? 0 : Math.Ceiling(TextWidth(table.Note, measure));
        return table;
    }

    static double TextWidth(string text, Func<string, double> measure)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var measured = measure == null ? 0 : measure(text);
        return measured > 0 ? measured : text.Length * CharMm;
    }
}
