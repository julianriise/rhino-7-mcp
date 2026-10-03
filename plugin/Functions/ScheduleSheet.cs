using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// F5.1 schedules on paper: the door, window and room lists drawn as tables
/// in page space on their own sheet, from the same room and opening records
/// the plan draws. Marks are stamped on the opening markers here. Export
/// draws the tables again, so they follow the model.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string SchedulesView = "schedules";
    private const string SchedulesPageName = "Forsk — Schedules";
    private const string ScheduleMetaSection = "forsk_schedules";
    private const string MarkNextEntry = "mark_next";
    /// <summary>How far past the wall face a room is looked for on each side of an opening.</summary>
    private const double OpeningProbeMm = 300.0;

    private static readonly string[] ScheduleKinds = { "door", "window", "room" };

    /// <summary>
    /// A room marker as the plan tags it and the room list lists it: one read,
    /// so the two cannot disagree. Ring is null when there is no outline to
    /// read; Untagged says why a room with an outline gets no tag (too small).
    /// </summary>
    private sealed class PlanRoom
    {
        public string RoomId;
        public string Who;
        public string Name;
        public double Area;
        /// <summary>forsk:level. Empty when the marker does not carry one.</summary>
        public string Level;
        public List<Point3d> Ring;
        public List<RoomDetect.Pt> Outline;
        public RoomDetect.Pt Inside;
        public string Untagged;

        public bool Tagged
        {
            get { return Ring != null && Untagged == null; }
        }

        public string ScheduleId
        {
            get { return string.IsNullOrEmpty(RoomId) ? Who : RoomId; }
        }
    }

    private static List<PlanRoom> PlanRooms(RhinoDoc doc)
    {
        var labels = RoomLabels(doc);
        var rooms = new List<PlanRoom>();
        foreach (var obj in RoomMarkers(doc))
        {
            var roomId = obj.Attributes.GetUserString(RoomIdKey);
            var room = new PlanRoom
            {
                RoomId = roomId ?? "",
                Who = !string.IsNullOrEmpty(roomId) ? roomId : obj.Name ?? obj.Id.ToString(),
                Level = obj.Attributes?.GetUserString("forsk:level")
            };
            rooms.Add(room);
            var area = ParseMm(obj.Attributes?.GetUserString("forsk:area"));
            if (!area.HasValue || area.Value <= 0 || !TryRoomPolygon(obj, out var ring)) continue;
            var outline = PlanPoints(ring);
            // rooms_detect decided which regions are rooms, named them and
            // placed their tags; the marker's stamps say so and the tag shows that.
            if (!RoomDetect.TryTag(obj.Attributes.GetUserString(RoomNameKey), obj.Attributes.GetUserString(RoomAtKey),
                    area.Value, labels, outline, out var name, out var inside, out var untagged))
                continue;
            room.Ring = ring;
            room.Outline = outline;
            room.Inside = inside;
            room.Area = area.Value;
            room.Name = name;
            room.Untagged = untagged;
        }
        return rooms;
    }

    /// <summary>The tagged room holding each side of an opening, null where there is none.</summary>
    private static string[] OpeningRooms(List<PlanRoom> rooms, Plane plane, double halfThick)
    {
        var names = new string[2];
        for (var side = 0; side < 2; side++)
        {
            var probe = plane.Origin + plane.YAxis * ((side == 0 ? -1 : 1) * (halfThick + OpeningProbeMm));
            var at = new RoomDetect.Pt(probe.X, probe.Y);
            var room = rooms.FirstOrDefault(r => r.Tagged && RoomDetect.Contains(r.Outline, at));
            names[side] = room?.Name;
        }
        return names;
    }

    /// <summary>
    /// Every Forsk door and window as a schedule row, with its mark stamped on
    /// the marker (forsk:mark). Marks already there stay; the next free number
    /// per prefix is kept in the document so a deleted mark is not reused.
    /// </summary>
    private List<Schedules.Opening> ScheduleOpenings(RhinoDoc doc, List<PlanRoom> rooms)
    {
        var openings = new List<Schedules.Opening>();
        var markers = new List<RhinoObject>();
        foreach (var marker in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(marker) || IsExistingUnderlay(doc, marker)) continue;
            if (!string.Equals(GetForskKind(marker), "opening_marker", StringComparison.OrdinalIgnoreCase)) continue;
            var kind = marker.Attributes.GetUserString("forsk:opening_kind");
            var record = ResolvedOpeningStyle(doc, marker.Id, kind);
            var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (record == null || !box.IsValid) continue;
            var opening = new Schedules.Opening
            {
                Id = marker.Id.ToString(),
                Mark = marker.Attributes.GetUserString(Schedules.MarkKey),
                Record = record,
                X = box.Center.X,
                Y = box.Center.Y,
                Width = ParseMm(marker.Attributes.GetUserString("forsk:width")) ?? 0,
                Sill = ParseMm(marker.Attributes.GetUserString("forsk:sill")) ?? 0,
                Head = ParseMm(marker.Attributes.GetUserString("forsk:head")) ?? 0
            };
            if (TrySymbolFrame(doc, marker, out _, out var plane, out var frame))
                opening.Rooms = OpeningRooms(rooms, plane, frame.HalfThick);
            openings.Add(opening);
            markers.Add(marker);
        }

        var next = ReadMarkNext(doc);
        var marks = Schedules.AssignMarks(openings, next);
        doc.Strings.SetString(ScheduleMetaSection, MarkNextEntry,
            string.Join(";", next.Select(pair => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture))));
        for (var i = 0; i < openings.Count; i++)
        {
            if (string.Equals(openings[i].Mark, marks[i], StringComparison.Ordinal)) continue;
            var attr = markers[i].Attributes.Duplicate();
            attr.SetUserString(Schedules.MarkKey, marks[i]);
            doc.Objects.ModifyAttributes(markers[i].Id, attr, true);
            openings[i].Mark = marks[i];
        }
        return openings;
    }

    private static Dictionary<string, int> ReadMarkNext(RhinoDoc doc)
    {
        var next = new Dictionary<string, int>(StringComparer.Ordinal);
        var raw = doc.Strings.GetValue(ScheduleMetaSection, MarkNextEntry) ?? "";
        foreach (var pair in raw.Split(';'))
        {
            var kv = pair.Split('=');
            if (kv.Length == 2 && int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                next[kv[0]] = n;
        }
        return next;
    }

    /// <summary>The lists asked for, in door, window, room order. An empty list is left out.</summary>
    private List<Schedules.Table> ScheduleTables(RhinoDoc doc, IList<string> kinds)
    {
        var rooms = PlanRooms(doc);
        var openings = ScheduleOpenings(doc, rooms);
        var tables = new List<Schedules.Table>();
        if (kinds.Contains("door"))
            tables.Add(Schedules.DoorTable(openings.Where(o => o.Record.Kind == "door").ToList()));
        if (kinds.Contains("window"))
            tables.Add(Schedules.WindowTable(openings.Where(o => o.Record.Kind == "window").ToList()));
        if (kinds.Contains("room"))
        {
            var tagged = rooms.Where(r => r.Tagged).Select(r => new Schedules.Room
            {
                Id = r.ScheduleId,
                Name = r.Name,
                AreaMm2 = r.Area
            }).ToList();
            tables.Add(Schedules.RoomTable(tagged));
        }
        // Column widths from Rhino's own layout of each text, not a glyph guess.
        var style = OneToOneTextStyle(
            doc, "Forsk paper " + Schedules.TextMm.ToString("0.0", CultureInfo.InvariantCulture), MmToPage(doc, Schedules.TextMm));
        foreach (var table in tables)
            Schedules.Fit(table, text => PaperTextWidth(doc, style, text));
        return tables.Where(t => t.Rows.Count > 0).ToList();
    }

    /// <summary>Printed width in paper mm of text on the cells' paper style. 0 when Rhino cannot say.</summary>
    private static double PaperTextWidth(RhinoDoc doc, DimensionStyle style, string text)
    {
        if (string.IsNullOrEmpty(text) || style == null) return 0;
        try
        {
            using (var entity = TextEntity.Create(text, Plane.WorldXY, style, false, 0, 0))
            {
                var box = entity?.GetBoundingBox(true) ?? BoundingBox.Empty;
                var mm = MmToPage(doc, 1.0);
                return box.IsValid && mm > 0 ? (box.Max.X - box.Min.X) / mm : 0;
            }
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static List<string> ReadScheduleKinds(JObject parameters)
    {
        var token = parameters?["schedule_kinds"] as JArray;
        if (token == null || token.Count == 0) return new List<string>(ScheduleKinds);
        var kinds = new List<string>();
        foreach (var item in token)
        {
            var kind = item?.ToString()?.Trim().ToLowerInvariant();
            if (Array.IndexOf(ScheduleKinds, kind) < 0)
                throw new InvalidOperationException("schedule_kinds are door, window, and room.");
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }
        return ScheduleKinds.Where(kinds.Contains).ToList();
    }

    /// <summary>
    /// Draw the blocks in page space from the sheet's top-left margin. Every
    /// cell is its own text, stamped with its list, line, column, and the
    /// column's width, so the smoke reads back what the page shows and the
    /// page can tell a text that runs past its cell.
    /// </summary>
    private void DrawSchedules(RhinoDoc doc, RhinoPageView page, string view, string stableId, List<Schedules.Block> blocks, JArray ids)
    {
        var layer = EnsureLayer(doc, "A-ANNO", Color.FromArgb(200, 160, 40));
        var pageId = page.MainViewport.Id;
        const double left = LayoutMarginMm;
        const double top = A3HeightMm - LayoutMarginMm;
        // F5.4: the rules are the profile's thin pen, the cells its text ink.
        var profile = ReadPrintProfile(doc);
        PrintProfiles.Active = profile;
        ObjectAttributes Attr(string role, string kind)
        {
            var attr = LayoutAttr(layer.Index, pageId, view, stableId);
            var ink = role == "schedule_line" ? profile.Thin.Color : profile.Text;
            attr.ObjectColor = ink;
            attr.PlotColor = ink;
            attr.SetUserString("forsk:role", role);
            attr.SetUserString("forsk:schedule", kind);
            return attr;
        }
        foreach (var block in blocks)
        {
            var table = block.Table;
            var x0 = left + block.X;
            var yTop = top - block.Top;
            AddPaperText(doc, ids, block.Title, x0, yTop - Schedules.TitleSpaceMm + 1.5, Schedules.TitleMm,
                TextHorizontalAlignment.Left, TextVerticalAlignment.Bottom, Attr("schedule_title", table.Kind));

            var tableTop = yTop - Schedules.TitleSpaceMm;
            var tableBottom = tableTop - Schedules.RowMm * (1 + block.Count);
            var x1 = x0 + table.Width;
            AddPaperRect(doc, ids, x0, tableBottom, x1, tableTop, Attr("schedule_line", table.Kind));
            var cx = x0;
            for (var c = 0; c + 1 < table.Widths.Length; c++)
            {
                cx += table.Widths[c];
                AddPaperLine(doc, ids, cx, tableBottom, cx, tableTop, Attr("schedule_line", table.Kind));
            }
            for (var r = 1; r <= block.Count; r++)
            {
                var y = tableTop - Schedules.RowMm * r;
                AddPaperLine(doc, ids, x0, y, x1, y, Attr("schedule_line", table.Kind));
            }

            for (var r = -1; r < block.Count; r++)
            {
                var line = r < 0 ? -1 : block.First + r;
                var cells = line < 0 ? table.Heads : table.Line(line);
                var id = line < 0 ? "head" : table.LineId(line);
                var y = tableTop - Schedules.RowMm * (r + 1) - Schedules.RowMm / 2.0;
                cx = x0;
                for (var c = 0; c < table.Widths.Length; c++)
                {
                    var text = c < cells.Length ? cells[c] : null;
                    if (!string.IsNullOrEmpty(text))
                    {
                        var right = table.Right[c];
                        var attr = Attr(line < 0 ? "schedule_head" : "schedule_cell", table.Kind);
                        attr.SetUserString("forsk:row", id);
                        attr.SetUserString("forsk:line", line.ToString(CultureInfo.InvariantCulture));
                        attr.SetUserString("forsk:col", c.ToString(CultureInfo.InvariantCulture));
                        attr.SetUserString("forsk:cell_mm", table.Widths[c].ToString("0.###", CultureInfo.InvariantCulture));
                        AddPaperText(doc, ids, text,
                            right ? cx + table.Widths[c] - Schedules.PadMm : cx + Schedules.PadMm, y, Schedules.TextMm,
                            right ? TextHorizontalAlignment.Right : TextHorizontalAlignment.Left,
                            TextVerticalAlignment.Middle, attr);
                    }
                    cx += table.Widths[c];
                }
            }
            if (block.Note)
                AddPaperText(doc, ids, table.Note, x0, tableBottom - Schedules.RowMm / 2.0, Schedules.TextMm,
                    TextHorizontalAlignment.Left, TextVerticalAlignment.Middle, Attr("schedule_note", table.Kind));
        }
    }

    /// <summary>Page-space objects of the schedules on this page.</summary>
    private static List<RhinoObject> ScheduleObjects(RhinoDoc doc, RhinoPageView page)
    {
        var settings = new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = true,
            ViewportFilter = page.MainViewport
        };
        var found = new List<RhinoObject>();
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            var role = obj?.Attributes?.GetUserString("forsk:role") ?? "";
            if (role.StartsWith("schedule", StringComparison.Ordinal)) found.Add(obj);
        }
        return found;
    }

    /// <summary>
    /// The lists as the page shows them, read back from the texts Rhino
    /// stored: per list its title and each line's id and cells in order.
    /// </summary>
    private static JObject ReadSchedules(RhinoDoc doc, RhinoPageView page)
    {
        var lists = new JObject();
        var lines = new Dictionary<string, SortedDictionary<int, (string Id, SortedDictionary<int, string> Cells)>>();
        foreach (var obj in ScheduleObjects(doc, page))
        {
            if (!(obj.Geometry is TextEntity text)) continue;
            var kind = obj.Attributes.GetUserString("forsk:schedule") ?? "";
            if (!(lists[kind] is JObject list))
            {
                list = new JObject { ["title"] = "", ["rows"] = new JArray() };
                lists[kind] = list;
            }
            var role = obj.Attributes.GetUserString("forsk:role");
            if (role == "schedule_title")
            {
                // The first block's title; a continued block adds (forts.).
                if (!text.PlainText.EndsWith("(forts.)", StringComparison.Ordinal))
                    list["title"] = text.PlainText;
                continue;
            }
            if (role == "schedule_note")
            {
                list["note"] = text.PlainText;
                continue;
            }
            if (role != "schedule_cell") continue;
            if (!int.TryParse(obj.Attributes.GetUserString("forsk:line"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line)) continue;
            if (!int.TryParse(obj.Attributes.GetUserString("forsk:col"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var col)) continue;
            if (!lines.TryGetValue(kind, out var byLine))
                lines[kind] = byLine = new SortedDictionary<int, (string, SortedDictionary<int, string>)>();
            if (!byLine.TryGetValue(line, out var row))
                byLine[line] = row = (obj.Attributes.GetUserString("forsk:row") ?? "", new SortedDictionary<int, string>());
            row.Cells[col] = text.PlainText;
        }
        foreach (var pair in lines)
        {
            var list = (JObject)lists[pair.Key];
            var rows = (JArray)list["rows"];
            foreach (var row in pair.Value.Values)
            {
                var cells = new JArray(row.Cells.Values.ToArray());
                if (row.Id == "total") list["total"] = cells;
                else rows.Add(new JObject { ["id"] = row.Id, ["cells"] = cells });
            }
        }
        return lists;
    }

    /// <summary>Which lists a schedules page shows, kept so export can draw them again.</summary>
    private static void RememberSchedules(RhinoDoc doc, RhinoPageView page, IList<string> kinds, string stableId)
    {
        doc.Strings.SetString(ScheduleMetaSection, page.PageName, string.Join(",", kinds.ToArray()) + ";" + stableId);
    }

    private static void ForgetSchedules(RhinoDoc doc, string pageName)
    {
        if (!string.IsNullOrEmpty(pageName)) doc.Strings.Delete(ScheduleMetaSection, pageName);
    }

    /// <summary>
    /// The tables flowed over as many sheets as they need, above the footer.
    /// Null with a reason when a table cannot fit a sheet at all.
    /// </summary>
    private static List<Schedules.Block> SheetBlocks(List<Schedules.Table> tables, out string why)
    {
        why = null;
        var blocks = Schedules.Flow(tables, A3WidthMm - 2.0 * LayoutMarginMm, ScheduleAreaHeight);
        if (blocks == null)
            why = "A schedule is wider than an A3 sheet, or the sheet cannot hold one of its lines.";
        return blocks;
    }

    /// <summary>Page name of schedules page number (1-based): Forsk — Schedules, Forsk — Schedules 2, …</summary>
    private static string SchedulesPageNameFor(int number)
    {
        return number <= 1 ? SchedulesPageName : SchedulesPageName + " " + number.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsSchedulesPage(RhinoPageView page)
    {
        var name = page?.PageName ?? "";
        return string.Equals(name, SchedulesPageName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(SchedulesPageName + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static int SchedulesPageNumber(RhinoPageView page)
    {
        var name = page?.PageName ?? "";
        if (name.Length <= SchedulesPageName.Length) return 1;
        return int.TryParse(name.Substring(SchedulesPageName.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : 1;
    }

    /// <summary>The document's schedules pages, first to last.</summary>
    private static List<RhinoPageView> SchedulePages(RhinoDoc doc)
    {
        var pages = (doc.Views.GetPageViews() ?? new RhinoPageView[0]).Where(IsSchedulesPage).ToList();
        pages.Sort((a, b) => SchedulesPageNumber(a).CompareTo(SchedulesPageNumber(b)));
        return pages;
    }

    /// <summary>
    /// Cell texts on the page that Rhino laid out wider than their column
    /// allows (the column less its padding), as "text w/cell mm". None when
    /// every cell holds its text.
    /// </summary>
    private static List<string> CellsOver(RhinoDoc doc, RhinoPageView page)
    {
        var over = new List<string>();
        var mm = MmToPage(doc, 1.0);
        foreach (var obj in ScheduleObjects(doc, page))
        {
            if (!(obj.Geometry is TextEntity text) || mm <= 0) continue;
            if (!double.TryParse(obj.Attributes.GetUserString("forsk:cell_mm"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cell))
                continue;
            var box = text.GetBoundingBox(true);
            if (!box.IsValid) continue;
            var width = (box.Max.X - box.Min.X) / mm;
            if (width + 2 * Schedules.PadMm > cell + 0.05)
                over.Add(text.PlainText + " " + width.ToString("0.0", CultureInfo.InvariantCulture)
                    + "/" + cell.ToString("0.#", CultureInfo.InvariantCulture) + " mm");
        }
        return over;
    }

    /// <summary>
    /// Export draws the schedules pages again from the model, as it bakes the
    /// plan again, so an edit after layout_pack still shows. Null, or why not:
    /// lists that now need a different number of pages, or nothing left to
    /// list, need layout_pack again.
    /// </summary>
    private string RefreshSchedules(RhinoDoc doc)
    {
        var pages = SchedulePages(doc);
        if (pages.Count == 0) return null;
        var raw = doc.Strings.GetValue(ScheduleMetaSection, pages[0].PageName);
        if (string.IsNullOrEmpty(raw)) return null;
        var kinds = raw.Split(';')[0].Split(',').ToList();
        var tables = ScheduleTables(doc, kinds);
        if (tables.Count == 0)
            return "No doors, windows or rooms left to schedule. Run layout_pack again.";
        var blocks = SheetBlocks(tables, out var why);
        if (blocks == null) return why;
        var need = Schedules.Pages(blocks);
        if (need != pages.Count)
            return "The schedules need " + need.ToString(CultureInfo.InvariantCulture) + " page(s) now, not "
                + pages.Count.ToString(CultureInfo.InvariantCulture) + ". Run layout_pack again.";
        for (var i = 0; i < pages.Count; i++)
        {
            var own = doc.Strings.GetValue(ScheduleMetaSection, pages[i].PageName) ?? "";
            var parts = own.Split(';');
            var stableId = parts.Length > 1 ? parts[1] : "";
            foreach (var obj in ScheduleObjects(doc, pages[i]))
                doc.Objects.Delete(obj.Id, true);
            DrawSchedules(doc, pages[i], SchedulesView, stableId, blocks.Where(b => b.Page == i).ToList(), new JArray());
        }
        return null;
    }

    /// <summary>Same band as the detail: top margin down to the footer reserve.</summary>
    private static double ScheduleAreaHeight
    {
        get { return A3HeightMm - LayoutMarginMm - FooterReserveMm - LayoutMarginMm; }
    }

    private static string ScheduleCounts(List<Schedules.Table> tables)
    {
        var parts = new List<string>();
        foreach (var table in tables)
        {
            var noun = table.Kind == "door" ? "door" : table.Kind == "window" ? "window" : "room";
            parts.Add(table.Rows.Count.ToString(CultureInfo.InvariantCulture) + " " + noun + (table.Rows.Count == 1 ? "" : "s"));
        }
        return string.Join(", ", parts.ToArray());
    }
}
