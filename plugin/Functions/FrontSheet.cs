using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 P5: the front sheet, A-00-001. The Tegningsliste (number, title and
/// scale of every page that prints, in set order) and the Arealtabell (BTA,
/// BRA and Netto per floor, Netto per use, from area_stats' own result),
/// drawn on the lists' path (Schedules.Flow, DrawSchedules) with the title
/// block and no scale or north arrow. The Tegningsliste reads each page's
/// own title block, so it lists exactly what the PDF holds. Export draws it
/// again, as it does the lists.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string FrontPageName = "Forsk — Front";

    private JObject AddFrontPage(RhinoDoc doc, int number, int level)
    {
        var page = doc.Views.AddPageView(FrontPageName, A3WidthMm, A3HeightMm);
        if (page == null)
            throw new InvalidOperationException(LayoutDetailFailedMessage);
        page.SetPageAsActive();
        ApplyPaperDisplay(page);
        var spec = new LayoutViewSpec { View = SheetSet.FrontId, PageName = FrontPageName };
        var stableId = FormatStableId("l", number);
        var area = FrontAreaTable(doc);
        var title = SheetSet.Title(SheetSet.FrontId, level, area == null ? new List<string>() : null);
        var sheetNo = SheetSet.Number(SheetSet.FrontId, level);
        var footer = new JObject();
        // The footer first: the Tegningsliste reads this page's own number and title too.
        var ids = AddSheetFooter(doc, page, spec, stableId, sheetNo, 0, title, footer);
        var over = DrawFront(doc, page, stableId, area, ids);
        return new JObject
        {
            ["view"] = SheetSet.FrontId,
            ["page"] = FrontPageName,
            ["number"] = sheetNo,
            ["title"] = title,
            ["scale"] = 0,
            ["page_scale"] = 0,
            ["detail_count"] = 0,
            ["ids"] = ids,
            ["footer"] = footer,
            ["view_title"] = title,
            ["front"] = new JObject
            {
                ["lists"] = ReadSchedules(doc, page),
                ["rows_over"] = over,
                ["cells_over"] = new JArray(CellsOver(doc, page).ToArray())
            }
        };
    }

    /// <summary>Export draws the front sheet's tables again, from the pages and the model as they are now.</summary>
    private void RefreshFront(RhinoDoc doc, RhinoPageView page)
    {
        var stableId = "";
        foreach (var obj in ScheduleObjects(doc, page))
        {
            if (stableId.Length == 0) stableId = obj.Attributes.GetUserString("forsk:id") ?? "";
            doc.Objects.Delete(obj.Id, true);
        }
        DrawFront(doc, page, stableId, FrontAreaTable(doc), new JArray());
    }

    /// <summary>
    /// The Tegningsliste and the Arealtabell on the page, from its top-left
    /// margin. The front sheet is one page: returns how many table lines
    /// would not fit it (0 for any real set).
    /// </summary>
    private int DrawFront(RhinoDoc doc, RhinoPageView page, string stableId, Schedules.Table area, JArray ids)
    {
        var tables = new List<Schedules.Table> { Schedules.DrawingList(PrintedSheets(doc)) };
        if (area != null) tables.Add(area);
        var style = OneToOneTextStyle(
            doc, "Forsk paper " + Schedules.TextMm.ToString("0.0", CultureInfo.InvariantCulture), MmToPage(doc, Schedules.TextMm));
        foreach (var table in tables)
            Schedules.Fit(table, text => PaperTextWidth(doc, style, text));
        var blocks = SheetBlocks(tables, out var why);
        if (blocks == null)
            throw new InvalidOperationException(why);
        DrawSchedules(doc, page, SheetSet.FrontId, stableId, blocks.Where(b => b.Page == 0).ToList(), ids);
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
            if (!(obj?.Geometry is Rhino.Geometry.TextEntity text)) continue;
            if (obj.Attributes.GetUserString("forsk:role") != "title_cell") continue;
            if (obj.Attributes.GetUserString("forsk:part") != "value") continue;
            var key = obj.Attributes.GetUserString("forsk:cell");
            if (!string.IsNullOrEmpty(key)) cells[key] = text.PlainText;
        }
        return cells;
    }
}
