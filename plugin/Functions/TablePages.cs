using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The one-page sheets of tables: the front sheet (v3 P5, A-00-001: the
/// Tegningsliste and the Arealtabell) and the Mengdeliste (v3 P7, A-00-050).
/// Each is drawn on the lists' path (Schedules.Flow, DrawSchedules) with the
/// title block and no scale or north arrow, and export draws its tables
/// again from the model, as it does the lists. The Tegningsliste reads each
/// page's own title block, so it lists exactly what the PDF holds.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string FrontPageName = "Forsk — Front";
    private const string TakeoffPageName = "Forsk — Takeoff";

    private static string TablePageName(string view)
    {
        return view == SheetSet.TakeoffId ? TakeoffPageName : FrontPageName;
    }

    /// <summary>The front sheet or the Mengdeliste as a page, with its record.</summary>
    private JObject AddTablePage(RhinoDoc doc, string view, int number, int level)
    {
        var name = TablePageName(view);
        var page = doc.Views.AddPageView(name, A3WidthMm, A3HeightMm);
        if (page == null)
            throw new InvalidOperationException(LayoutDetailFailedMessage);
        page.SetPageAsActive();
        ApplyPaperDisplay(page);
        var spec = new LayoutViewSpec { View = view, PageName = name };
        var stableId = FormatStableId("l", number);
        var title = TablePageTitle(doc, view, level);
        var sheetNo = SheetSet.Number(view, level);
        var footer = new JObject();
        // The footer first: the Tegningsliste reads this page's own number and title too.
        var ids = AddSheetFooter(doc, page, spec, stableId, sheetNo, 0, title, footer);
        var over = DrawTablePage(doc, page, view, stableId, ids);
        return new JObject
        {
            ["view"] = view,
            ["page"] = name,
            ["number"] = sheetNo,
            ["title"] = title,
            ["scale"] = 0,
            ["page_scale"] = 0,
            ["detail_count"] = 0,
            ["ids"] = ids,
            ["footer"] = footer,
            ["view_title"] = title,
            [view] = new JObject
            {
                ["lists"] = ReadSchedules(doc, page),
                ["rows_over"] = over,
                ["cells_over"] = new JArray(CellsOver(doc, page).ToArray())
            }
        };
    }

    /// <summary>Export draws the page's tables again, from the pages and the model as they are now.</summary>
    private void RefreshTablePage(RhinoDoc doc, RhinoPageView page, string view)
    {
        var stableId = "";
        foreach (var obj in ScheduleObjects(doc, page))
        {
            if (stableId.Length == 0) stableId = obj.Attributes.GetUserString("forsk:id") ?? "";
            doc.Objects.Delete(obj.Id, true);
        }
        DrawTablePage(doc, page, view, stableId, new JArray());
    }

    private string TablePageTitle(RhinoDoc doc, string view, int level)
    {
        if (view != SheetSet.FrontId) return SheetSet.Title(view, level);
        // No rooms, no Arealtabell: the front sheet is the Tegningsliste alone.
        return SheetSet.Title(view, level, FrontAreaTable(doc) == null ? new List<string>() : null);
    }

    /// <summary>
    /// The page's tables from its top-left margin. These sheets are one page:
    /// returns how many table lines would not fit it (0 for any real set).
    /// </summary>
    private int DrawTablePage(RhinoDoc doc, RhinoPageView page, string view, string stableId, JArray ids)
    {
        var tables = new List<Schedules.Table>();
        if (view == SheetSet.TakeoffId)
            tables.Add(Takeoff.Table(ReadTakeoff(doc)));
        else
        {
            tables.Add(Schedules.DrawingList(PrintedSheets(doc)));
            var area = FrontAreaTable(doc);
            if (area != null) tables.Add(area);
        }
        var style = OneToOneTextStyle(
            doc, "Forsk paper " + Schedules.TextMm.ToString("0.0", CultureInfo.InvariantCulture), MmToPage(doc, Schedules.TextMm));
        foreach (var table in tables)
            Schedules.Fit(table, text => PaperTextWidth(doc, style, text));
        var blocks = SheetBlocks(tables, out var why);
        if (blocks == null)
            throw new InvalidOperationException(why);
        DrawSchedules(doc, page, view, stableId, blocks.Where(b => b.Page == 0).ToList(), ids);
        return blocks.Where(b => b.Page > 0).Sum(b => b.Count);
    }

    /// <summary>The Arealtabell, or null when the model has no rooms.</summary>
    private Schedules.Table FrontAreaTable(RhinoDoc doc)
    {
        var result = ReadAreaStats(doc);
        if (result.Rooms.Count == 0) return null;
        var table = Schedules.AreaTable(result);
        return table.Lines == 0 ? null : table;
    }

    /// <summary>
    /// Every Forsk page in set order as its title block prints it: number,
    /// title and scale. A page without a number (laid out before sheets were
    /// numbered) is left out.
    /// </summary>
    private static List<Schedules.Drawing> PrintedSheets(RhinoDoc doc)
    {
        var sheets = new List<Schedules.Drawing>();
        foreach (var page in InSetOrder(doc, MatchingForskPages(doc, null)))
        {
            var cells = PageTitleCells(doc, page);
            if (!cells.TryGetValue("number", out var number) || string.IsNullOrWhiteSpace(number)) continue;
            cells.TryGetValue("drawing", out var title);
            var scale = 0;
            if (cells.TryGetValue("scale", out var text) && text.StartsWith("1:", StringComparison.Ordinal))
                int.TryParse(text.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out scale);
            sheets.Add(new Schedules.Drawing { Number = number, Title = title ?? "", Scale = scale });
        }
        return sheets;
    }

    /// <summary>A page's title block values by cell key (drawing, number, scale, …), as printed.</summary>
    private static Dictionary<string, string> PageTitleCells(RhinoDoc doc, RhinoPageView page)
    {
        var cells = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = true,
            ViewportFilter = page.MainViewport,
            ObjectTypeFilter = ObjectType.Annotation
        };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (!(obj?.Geometry is TextEntity text)) continue;
            if (obj.Attributes.GetUserString("forsk:role") != "title_cell") continue;
            if (obj.Attributes.GetUserString("forsk:part") != "value") continue;
            var key = obj.Attributes.GetUserString("forsk:cell");
            if (!string.IsNullOrEmpty(key)) cells[key] = text.PlainText;
        }
        return cells;
    }

    /// <summary>
    /// takeoff, read only: the Mengdeliste's lines as card rows and one
    /// summary line. A window tool, not a bridge command: no server tool, no
    /// contract.
    /// </summary>
    public JObject TakeoffTool(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");
        var result = ReadTakeoff(doc);
        return new JObject
        {
            ["message"] = result.Summary,
            ["rows"] = new JArray(result.Lines.Select(line => Takeoff.Row(line)).ToArray())
        };
    }

    /// <summary>The takeoff from the model's own records, and area_stats' figures.</summary>
    private Takeoff.Result ReadTakeoff(RhinoDoc doc)
    {
        var inputs = ReadTakeoffInputs(doc);
        return Takeoff.Compute(inputs.Walls, inputs.Openings, inputs.Slabs, inputs.Roofs, ReadAreaStats(doc), inputs.Tol,
            stairs: inputs.Stairs.Select(s => s.Flight).ToList());
    }

    private sealed class TakeoffInputs
    {
        public List<Takeoff.Wall> Walls = new List<Takeoff.Wall>();
        public List<Takeoff.Slab> Slabs = new List<Takeoff.Slab>();
        public List<Takeoff.Slab> Roofs = new List<Takeoff.Slab>();
        public List<Schedules.Opening> Openings = new List<Schedules.Opening>();
        public List<TakeoffCsv.Stair> Stairs = new List<TakeoffCsv.Stair>();
        public double Tol;
    }

    /// <summary>
    /// One walk of the document for the takeoff and the CSV: each wall's
    /// forsk:id, level, path, thickness and height (Forsk walls, and walls
    /// kept on X-EXIST apart, x01… when they have no id), each slab and
    /// roof, the doors and windows the lists read, and each stair as built.
    /// </summary>
    private TakeoffInputs ReadTakeoffInputs(RhinoDoc doc)
    {
        var inputs = new TakeoffInputs { Tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0) };
        var unnamed = 0;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null) continue;
            var existing = IsExistingUnderlay(doc, obj);
            if (!existing && !IsForskGenerated(obj)) continue;
            var kind = GetForskKind(obj) ?? "";
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            var depth = box.Max.Z - box.Min.Z;
            var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (rings != null && (existing || kind.Equals("wall", StringComparison.OrdinalIgnoreCase)))
            {
                var id = obj.Attributes.GetUserString("forsk:id");
                inputs.Walls.Add(new Takeoff.Wall
                {
                    Id = string.IsNullOrWhiteSpace(id) ? "x" + (++unnamed).ToString("00", CultureInfo.InvariantCulture) : id,
                    Level = obj.Attributes.GetUserString("forsk:level"),
                    Rings = rings,
                    ThicknessMm = ParseMm(obj.Attributes.GetUserString("forsk:thickness")) ?? 0,
                    HeightMm = ParseMm(obj.Attributes.GetUserString("forsk:height")) ?? depth,
                    Existing = existing
                });
                continue;
            }
            if (existing) continue;
            var slab = kind.Equals("floor", StringComparison.OrdinalIgnoreCase);
            if (!slab && !kind.Equals("roof", StringComparison.OrdinalIgnoreCase)) continue;
            var thickness = ParseMm(obj.Attributes.GetUserString("forsk:thickness")) ?? depth;
            var volume = SolidVolume(obj.Geometry);
            if (thickness <= 0 || volume <= 0) continue;
            (slab ? inputs.Slabs : inputs.Roofs).Add(new Takeoff.Slab { AreaMm2 = volume / thickness, ThicknessMm = thickness });
        }
        inputs.Openings = OpeningRows(doc, null, out _);
        // R5: each stair as it was last built: its record's rise, risers and sizes.
        foreach (var obj in StairObjects(doc))
            if (TryStairAsBuilt(obj, out _, out var flight))
                inputs.Stairs.Add(new TakeoffCsv.Stair { Id = obj.Attributes.GetUserString("forsk:id") ?? obj.Id.ToString(), Flight = flight });
        return inputs;
    }

    /// <summary>
    /// v3 N2 export_csv: the takeoff as a CSV at path, UTF-8 with a BOM. The
    /// project info heads it; rooms, walls (one row per run), doors, windows
    /// and stairs follow, each figure the takeoff's own.
    /// </summary>
    [McpCommand("export_csv", ReadOnly = true)]
    public JObject ExportCsv(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");
        var path = parameters?["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path is required.");
        path = Path.GetFullPath(path.Trim());
        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) path += ".csv";
        var inputs = ReadTakeoffInputs(doc);
        var areas = ReadAreaStats(doc);
        var rooms = areas.Rooms;
        var runs = Takeoff.Runs(inputs.Walls, inputs.Openings, inputs.Tol);
        var text = TakeoffCsv.Write(ReadProjectInfo(doc), rooms, runs, inputs.Openings, inputs.Stairs, DateTime.Now, areas.Gross);
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllBytes(path, TakeoffCsv.Bytes(text));
        var doors = inputs.Openings.Count(o => o.Record?.Kind == "door");
        var windows = inputs.Openings.Count(o => o.Record?.Kind == "window");
        return new JObject
        {
            ["path"] = path,
            ["rooms"] = rooms.Count,
            ["walls"] = runs.Count,
            ["doors"] = doors,
            ["windows"] = windows,
            ["stairs"] = inputs.Stairs.Count,
            ["message"] = TakeoffCsv.Receipt(rooms.Count, runs.Count, doors + windows, inputs.Stairs.Count, path)
        };
    }

    /// <summary>A closed solid's volume in mm³, 0 when Rhino cannot measure it.</summary>
    private static double SolidVolume(GeometryBase geometry)
    {
        try
        {
            using (var mass = geometry is Brep brep ? VolumeMassProperties.Compute(brep)
                : geometry is Extrusion extrusion ? VolumeMassProperties.Compute(extrusion)
                : geometry is Mesh mesh ? VolumeMassProperties.Compute(mesh)
                : null)
                return Math.Abs(mass?.Volume ?? 0);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
