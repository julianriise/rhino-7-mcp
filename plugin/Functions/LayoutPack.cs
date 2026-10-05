using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Paper layouts of a greyscale HiddenLineDrawing. Rhino 7 has no
/// ClippingDrawings. Before each page, layout_pack bakes black curves on
/// S-DRAW (Plan, North, East, South, West). The plan drawing includes the
/// horizontal cut 1200 mm above the floor. Details show that drawing layer
/// only, in a Top view (the curves are flat in XY), Wireframe, so the Mac
/// preview is black lines on white. The model
/// viewport keeps the clay. v3 N3: the PDF is SheetPdf's vector file from the
/// flat sheets the DWG is written from, on every OS. ViewCapture wrote a
/// white sheet on Rhino 7 Mac; when the vector write throws, the old Mac
/// path is the fallback: it activates each layout, redraws, waits, then
/// draws GetPreviewImage into a FilePdf. A blank frame is a paint race:
/// that page is activated and read again until it paints or its read budget
/// (PreviewFrame.ReadAgain) runs out. Pages that
/// have ink are still written. Each export appends a line to
/// /tmp/forsk-print.log, and one capture line per read (PreviewFrame); a
/// blank frame is saved as a PNG beside it. AddPageView width and height are millimetres
/// (A3 landscape 420 x 297). Detail corners and the title block are converted
/// into the document page units.
/// </summary>
public partial class RhinoMCPFunctions
{
    private struct LayoutViewSpec
    {
        public string View;
        public string PageName;
        public Vector3d Look;
        public Vector3d Up;
    }

    private const string LayoutMetaSection = "forsk";
    private const string LayoutPagePrefix = "Forsk — ";
    private const string NothingToLayOutMessage = "Nothing to lay out. Bake walls first.";
    private const string UnknownLayoutViewMessage =
        "Unknown view. Use front, plan, north, east, south, west, schedules, takeoff, or a stored section (section_a).";
    private const string UnknownPaperMessage = "Unknown paper. Use A3.";
    private const string ExportNeedsPathMessage = "export_pdf requires a file path.";
    private const string ExportNeedsPdfMessage = "export_pdf path must be an absolute .pdf file.";
    private const string NoLayoutsMessage = "No layouts to print. Call layout_pack first.";
    private const string PrintScaleEntry = "print_scale";
    private const string NoSheetOnMessage = "Every sheet in the set is off. Turn one on with Choose sheets.";
    private const string PdfWriteFailedMessage = "PDF write failed.";
    private const string LayoutDetailFailedMessage = "Layout detail failed.";
    private const string EmptyDetailMessage = "Layout detail is empty. The sheet does not show the drawing.";
    private const string EmptyPdfMessage = "PDF detail is empty. The sheet does not show the drawing.";
    private const string CaptureFailedPrefix = "capture failed after activate/Wait";
    private const string PlanCutRole = "plan_cut";
    private const string PrintColorSourceKey = "forsk:print_cs";
    private const string PrintRgbKey = "forsk:print_rgb";
    private static bool _drawIncludeExisting = true;
    private static PlanStats _lastPlanStats;
    private static bool _lastPlanStatsSet;

    private const double A3WidthMm = PrintTemplate.WidthMm;
    private const double A3HeightMm = PrintTemplate.HeightMm;
    private const double LayoutMarginMm = 10.0;
    // Space under the detail: the footer band plus a 5 mm gap.
    private const double FooterReserveMm = 23.0;
    // Footer band along the bottom margin: north arrow, scale bar, title block.
    private const double FooterBandMm = 18.0;
    private const double TitleBlockWidthMm = 280.0;
    private const double NorthArrowMm = 12.0;
    private const double ScaleBarLeftMm = 26.0;
    private const double ScaleBarHeightMm = 2.0;
    private const double FooterTextMm = 2.5;
    private const double PdfDpi = 150.0;
    private const string PrintLogPath = "/tmp/forsk-print.log";

    private static bool TryGetLayoutView(string view, out LayoutViewSpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(view)) return false;
        switch (view.Trim().ToLowerInvariant())
        {
            case "plan":
                spec = LayoutSpec("plan", "Forsk — Plan", -Vector3d.ZAxis, Vector3d.YAxis);
                return true;
            case "north":
                spec = LayoutSpec("north", "Forsk — North", -Vector3d.YAxis, Vector3d.ZAxis);
                return true;
            case "east":
                spec = LayoutSpec("east", "Forsk — East", -Vector3d.XAxis, Vector3d.ZAxis);
                return true;
            case "south":
                spec = LayoutSpec("south", "Forsk — South", Vector3d.YAxis, Vector3d.ZAxis);
                return true;
            case "west":
                spec = LayoutSpec("west", "Forsk — West", Vector3d.XAxis, Vector3d.ZAxis);
                return true;
            case SheetSet.FrontId:
            case SheetSet.TakeoffId:
                // Tables only: no look, no detail.
                var key = view.Trim().ToLowerInvariant();
                spec = LayoutSpec(key, TablePageName(key), Vector3d.Zero, Vector3d.ZAxis);
                return true;
            default:
                if (Details.TrySheetId(view, out var detailScale, out var detailSheet))
                {
                    spec = LayoutSpec(Details.SheetId(detailScale, detailSheet), Details.PageName(detailScale, detailSheet),
                        -Vector3d.ZAxis, Vector3d.YAxis);
                    return true;
                }
                // A section's page. Its look lives on the stored section (TryGetSectionView).
                if (!Sections.TryLetter(view, out var letter)) return false;
                spec = LayoutSpec(Sections.View(letter), Sections.PageName(letter), Vector3d.Zero, Vector3d.ZAxis);
                return true;
        }
    }

    private static LayoutViewSpec LayoutSpec(
        string view, string pageName, Vector3d look, Vector3d up)
    {
        return new LayoutViewSpec
        {
            View = view,
            PageName = pageName,
            Look = look,
            Up = up
        };
    }

    [McpCommand("set_project_meta")]
    public JObject SetProjectMeta(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");

        foreach (var key in ProjectInfo.Keys)
            MaybeStoreMeta(doc, parameters, key);
        MaybeStoreMeta(doc, parameters, "scale_label");
        return ProjectMetaRecord(doc);
    }

    [McpCommand("layout_pack", ModelView = true)]
    public JObject LayoutPack(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");
        // A previous export leaves a layout active. Curves added then land in
        // page space, at model millimetres, and the detail looks at the model
        // and prints a blank sheet. The registry also switches first.
        UseModelView(doc);
        ApplyDocumentPrintInk(doc);

        var paper = parameters?["paper"]?.ToString();
        if (string.IsNullOrWhiteSpace(paper))
            paper = PrintTemplate.Paper(doc.Strings.GetValue(LayoutMetaSection, PrintTemplate.Key));
        if (!paper.Trim().Equals("A3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UnknownPaperMessage);

        // One scale for the set: asked (and kept for the next Print), or picked from the ladder.
        var asked = ReadAskedScale(doc, parameters);
        var requestedScale = asked ?? SheetScale.FirstStep;

        var views = ReadLayoutViews(doc, parameters, out var offViews);
        var includeExisting = ReadBoolParam(parameters, "include_existing", true);
        var replace = ReadBoolParam(parameters, "replace", true);
        // The schedules and the front sheet are pages of tables, not drawing views.
        var withSchedules = views.RemoveAll(v => string.Equals(v, SchedulesView, StringComparison.OrdinalIgnoreCase)) > 0;
        var withFront = views.RemoveAll(v => string.Equals(v, SheetSet.FrontId, StringComparison.OrdinalIgnoreCase)) > 0;
        var withTakeoff = views.RemoveAll(v => string.Equals(v, SheetSet.TakeoffId, StringComparison.OrdinalIgnoreCase)) > 0;
        var scheduleKinds = ReadScheduleKinds(parameters);

        var clay = CollectLayoutClay(doc, includeExisting, out var hasWall);
        if (!hasWall)
        {
            return new JObject
            {
                ["pages"] = new JArray(),
                ["count"] = 0,
                ["scale"] = requestedScale,
                ["message"] = NothingToLayOutMessage
            };
        }

        var bbox = ClayBoundingBox(clay);
        if (!bbox.IsValid)
        {
            return new JObject
            {
                ["pages"] = new JArray(),
                ["count"] = 0,
                ["scale"] = requestedScale,
                ["message"] = NothingToLayOutMessage
            };
        }

        RestorePrintColors(doc);
        if (doc.Strings.GetValue(Details.Section, Details.RetiredEntry) != null)
        {
            doc.Strings.Delete(Details.Section, Details.RetiredEntry);
            RhinoApp.WriteLine("Forsk: removed " + Details.Section + "/" + Details.RetiredEntry + ".");
        }
        _drawIncludeExisting = includeExisting;
        // A whole Print writes the set: pages of sheets switched off go.
        if (replace && offViews.Count > 0)
            RemoveLayoutPages(doc, new HashSet<string>(offViews, StringComparer.OrdinalIgnoreCase), false);
        var wallLevel = WallLevel(doc);
        var detailW = A3WidthMm - (2.0 * LayoutMarginMm);
        var detailH = A3HeightMm - LayoutMarginMm - FooterReserveMm - LayoutMarginMm;

        var pages = new JArray();
        var drawingNotes = new List<string>();
        string cutNote = null;
        var planCutZ = FloorTopZ(clay) + ForskDefaults.PlanCutHeightMm;
        var planClip = new Plane(new Point3d(0, 0, planCutZ), -Vector3d.ZAxis);
        var detailSpan = new SheetScale.Span(detailW, detailH);
        var sheets = new List<PackSheet>();
        // Detail sheets keep their own scale and take no part in the set's.
        var detailViews = new List<string>();
        foreach (var viewName in views)
        {
            if (Details.TrySheetId(viewName, out _, out _))
            {
                detailViews.Add(viewName);
                continue;
            }
            if (!TryGetLayoutView(viewName, out var spec))
                throw new InvalidOperationException(UnknownLayoutViewMessage);
            var section = Sections.TryLetter(spec.View, out var sectionLetter);
            if (section && !TryGetSectionView(doc, spec.View, out _, out _))
                throw new InvalidOperationException("No section " + sectionLetter + ". section_add stores it first.");
            if (replace)
                RemoveLayoutPages(doc, spec.View, false);
            var plan = string.Equals(spec.View, "plan", StringComparison.OrdinalIgnoreCase);
            var sheet = new PackSheet { Spec = spec, Plan = plan, Section = section, Clip = plan ? planClip : (Plane?)null };
            // Tags, marks, dimensions and level marks are drawn at the sheet's scale.
            sheet.Stroke = requestedScale;
            sheet.Drawn = BakeGreyscaleDrawing(doc, spec.View, includeExisting, sheet.Clip, sheet.Stroke);
            sheets.Add(sheet);
        }

        // Tags, marks and dimensions keep their paper size, so a drawing grows
        // with its scale: bake again at the scale picked until it holds. A
        // drawing never steps back down, so this cannot swing between steps.
        var picked = SheetScale.Pick(sheets.Select(PackSpan).ToList(), detailSpan, asked);
        for (var round = 0; round < 4; round++)
        {
            var again = false;
            for (var i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                if (picked.Scales[i] == sheet.Stroke || !sheet.Drawn.Box.IsValid) continue;
                sheet.Stroke = picked.Scales[i];
                sheet.Drawn = BakeGreyscaleDrawing(doc, sheet.Spec.View, includeExisting, sheet.Clip, sheet.Stroke);
                again = true;
            }
            if (!again) break;
            picked = SheetScale.Pick(sheets.Select(PackSpan).ToList(), detailSpan, asked, picked.Scales);
        }

        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            var spec = sheet.Spec;
            var plan = sheet.Plan;
            var section = sheet.Section;
            var drawn = sheet.Drawn;
            if (!string.IsNullOrEmpty(drawn.Error) || drawn.Count < 1 || !drawn.Box.IsValid)
            {
                var why = string.IsNullOrEmpty(drawn.Error)
                    ? "No visible curves for " + spec.View + "."
                    : drawn.Error;
                throw new InvalidOperationException(why);
            }
            drawingNotes.Add(
                spec.View + " " + drawn.Count.ToString(CultureInfo.InvariantCulture)
                + (string.IsNullOrEmpty(drawn.WallNote) ? "" : " (" + drawn.WallNote + ")"));

            // Strokes and tags were drawn at this scale. The detail, the title
            // block and the scale bar use that same value.
            var scale = picked.Scales[i];
            var fitNeed = SheetScale.Need(PackSpan(sheet), detailSpan);

            var page = doc.Views.AddPageView(spec.PageName, A3WidthMm, A3HeightMm);
            if (page == null)
                throw new InvalidOperationException(LayoutDetailFailedMessage);

            page.SetPageAsActive();
            DetailViewObject detail;
            bool scaleLocked;
            try
            {
                detail = AddClayDetail(
                    doc, page, spec, drawn.Box, scale, drawn.Layer, out scaleLocked);
            }
            catch
            {
                try { LeaveDetail(page); } catch (Exception) { }
                try { page.Close(); } catch (Exception) { }
                throw;
            }
            if (detail == null)
            {
                LeaveDetail(page);
                page.Close();
                throw new InvalidOperationException(LayoutDetailFailedMessage);
            }

            var stableId = FormatStableId("l", pages.Count + 1);
            var viewTitle = SheetSet.Title(spec.View, wallLevel);
            var sheetNo = SheetSet.Number(spec.View, wallLevel);
            // The footer reads the scale the detail recorded, not the request.
            var pageScale = scaleLocked ? DetailModelScale(page) : 0;
            var footer = new JObject();
            var ids = AddSheetFooter(doc, page, spec, stableId, sheetNo, pageScale, viewTitle, footer);
            var pageRecord = new JObject
            {
                ["view"] = spec.View,
                ["page"] = spec.PageName,
                ["number"] = sheetNo,
                ["title"] = viewTitle,
                ["scale"] = scale,
                ["page_scale"] = pageScale,
                ["detail_count"] = 1,
                ["curves"] = drawn.Count,
                ["layer"] = drawn.Layer,
                ["ids"] = ids,
                ["footer"] = footer
            };
            if (string.Equals(spec.View, "plan", StringComparison.OrdinalIgnoreCase))
            {
                // The cut is already in the S-DRAW curves. A detail clipping
                // plane is not part of this pack. Section fill is the solid
                // hatch of mass the plane crosses.
                pageRecord["cut_z"] = planCutZ;
                pageRecord["cut_height_mm"] = ForskDefaults.PlanCutHeightMm;
                pageRecord["fills"] = drawn.Fills;
                pageRecord["symbols"] = drawn.Symbols;
                pageRecord["symbol_arcs"] = drawn.SymbolArcs;
                pageRecord["symbol_dashed"] = drawn.SymbolDashed;
                pageRecord["roof_outline"] = drawn.RoofOutline;
                pageRecord["stairs"] = drawn.Stairs;
                pageRecord["fit_need"] = Math.Round(fitNeed, 2);
                // Picked from the ladder, not asked.
                pageRecord["fitted"] = !asked.HasValue;
                pageRecord["fill"] = Math.Round(fitNeed * SheetScale.FillShare / scale, 3);
                pageRecord["opening_marks"] = drawn.Marks;
                pageRecord["marks_on_tags"] = new JArray(drawn.MarksOnTags ?? new List<string>());
                pageRecord["room_tags"] = drawn.RoomTags;
                pageRecord["room_areas_dropped"] = drawn.RoomAreasDropped;
                pageRecord["rooms_leader"] = drawn.RoomsLeader;
                pageRecord["rooms_overflow"] = drawn.RoomsOverflow;
                pageRecord["rooms_too_small"] = drawn.RoomsTooSmall;
                pageRecord["rooms_no_outline"] = drawn.RoomsNoOutline;
                pageRecord["rooms_untagged"] = new JArray(drawn.RoomsUntagged ?? new List<string>());
                pageRecord["rooms_leading"] = new JArray(drawn.RoomsLeading ?? new List<string>());
                pageRecord["rooms_overflowing"] = new JArray(drawn.RoomsOverflowing ?? new List<string>());
                pageRecord["dimensions"] = new JObject
                {
                    ["chains"] = drawn.Dims.Dims,
                    ["exterior"] = drawn.Dims.DimsExterior,
                    ["rooms"] = drawn.Dims.DimsRoom,
                    ["values"] = drawn.Dims.DimTexts,
                    ["skipped"] = drawn.Dims.DimsSkipped,
                    ["collisions"] = drawn.Dims.DimsCollisions,
                    ["openings"] = drawn.Dims.DimOpenings,
                    ["openings_shown"] = drawn.Dims.DimOpeningsShown
                };
                pageRecord["view_title"] = viewTitle;
                pageRecord["north_arrow"] = footer["north_arrow"] != null;
                pageRecord["section_markers"] = new JArray(drawn.Dims.SectionMarkers ?? new List<string>());
                pageRecord["section_markers_blocked"] = new JArray(drawn.Dims.SectionMarkersBlocked ?? new List<string>());
                pageRecord["callouts"] = new JArray(drawn.Dims.Callouts ?? new List<string>());
                pageRecord["callouts_blocked"] = new JArray(drawn.Dims.CalloutsBlocked ?? new List<string>());
                if (!string.IsNullOrEmpty(drawn.RoomText))
                    pageRecord["room_tag_text"] = drawn.RoomText;
                _lastPlanStats = new PlanStats
                {
                    Symbols = drawn.Symbols,
                    Arcs = drawn.SymbolArcs,
                    Dashed = drawn.SymbolDashed,
                    Roof = drawn.RoofOutline,
                    Rooms = drawn.RoomTags,
                    Note = drawn.SymbolNote,
                    RoomText = drawn.RoomText
                };
                _lastPlanStatsSet = true;
                cutNote = " Plan cut "
                    + ForskDefaults.PlanCutHeightMm.ToString("0", CultureInfo.InvariantCulture)
                    + " mm above the floor (Z "
                    + planCutZ.ToString("0.###", CultureInfo.InvariantCulture)
                    + "). Section fill "
                    + drawn.Fills.ToString(CultureInfo.InvariantCulture)
                    + ". Symbols "
                    + drawn.Symbols.ToString(CultureInfo.InvariantCulture)
                    + ", arcs "
                    + drawn.SymbolArcs.ToString(CultureInfo.InvariantCulture)
                    + ", dashed "
                    + drawn.SymbolDashed.ToString(CultureInfo.InvariantCulture)
                    + ", roof outline "
                    + drawn.RoofOutline.ToString(CultureInfo.InvariantCulture)
                    + ", room tags "
                    + drawn.RoomTags.ToString(CultureInfo.InvariantCulture)
                    + ", dimensions "
                    + drawn.Dims.Dims.ToString(CultureInfo.InvariantCulture)
                    + "."
                    + (string.IsNullOrEmpty(drawn.SymbolNote) ? "" : " " + drawn.SymbolNote);
                RhinoApp.WriteLine("Forsk " + cutNote.Trim());
            }
            if (drawn.Facade != null)
            {
                pageRecord["view_title"] = viewTitle;
                pageRecord["facade"] = FacadePageRecord(drawn.Facade);
            }
            if (section && drawn.Section != null)
            {
                pageRecord["view_title"] = viewTitle;
                pageRecord["fills"] = drawn.Fills;
                pageRecord["section"] = SectionPageRecord(doc, spec.View, drawn);
            }
            pages.Add(pageRecord);
        }
        var sheetCount = sheets.Count;
        DetailSheetPlan(doc, out var detailsDropped);
        var detailScales = new SortedSet<int>();
        foreach (var detailView in detailViews)
        {
            if (replace)
                RemoveLayoutPages(doc, detailView, false);
            var detailPage = AddDetailSheetPage(doc, detailView, includeExisting, pages.Count + 1, wallLevel);
            if (detailPage == null) continue;
            pages.Add(detailPage);
            sheetCount++;
            if (Details.TrySheetId(detailView, out var detailScale, out _)) detailScales.Add(detailScale);
        }
        var scheduleNote = "";
        if (withSchedules)
        {
            var scheduleTables = ScheduleTables(doc, scheduleKinds);
            if (replace)
                RemoveLayoutPages(doc, SchedulesView, false);
            if (scheduleTables.Count == 0)
                scheduleNote = " No doors, windows or rooms to schedule.";
            else
            {
                var added = AddSchedulesPages(doc, scheduleKinds, scheduleTables, pages.Count + 1);
                foreach (var record in added)
                    pages.Add(record);
                sheetCount++;
                scheduleNote = " Schedules: " + ScheduleCounts(scheduleTables)
                    + (added.Count > 1 ? " on " + added.Count.ToString(CultureInfo.InvariantCulture) + " pages." : ".");
            }
        }

        // The front sheet last: its Tegningsliste reads every page laid out before it.
        foreach (var (with, view) in new[] { (withTakeoff, SheetSet.TakeoffId), (withFront, SheetSet.FrontId) })
        {
            if (!with) continue;
            if (replace)
                RemoveLayoutPages(doc, view, false);
            pages.Add(AddTablePage(doc, view, pages.Count + 1, wallLevel));
            sheetCount++;
        }

        doc.Views.Redraw();
        var reported = sheets.Count > 0 ? picked.Scale : requestedScale;
        var bumped = picked.Bumped;
        var bumpNote = SheetScale.Clause(
            bumped.Select(i => SheetSet.Title(sheets[i].Spec.View, wallLevel)).ToList(),
            bumped.Select(i => picked.Scales[i]).ToList());
        var curveNote = drawingNotes.Count == 0
            ? ""
            : " Greyscale drawing: " + string.Join(", ", drawingNotes.ToArray()) + ".";
        StampSheetFingerprint(doc);
        return new JObject
        {
            ["pages"] = pages,
            ["count"] = pages.Count,
            ["sheets"] = sheetCount,
            // Drawing sheets in this pack: none means the scale names no sheet.
            ["drawings"] = sheets.Count,
            ["bumped"] = bumpNote.Trim(),
            ["scale"] = reported,
            ["detail_scales"] = new JArray(detailScales),
            ["details_dropped"] = detailsDropped,
            ["asked"] = asked.HasValue,
            // "on A3 at 1:N." then the drawing notes: smoke_compare reads "at 1:N. Greyscale drawing: plan".
            ["message"] = SheetCountText(sheetCount) + " on A3 at 1:" + reported.ToString(CultureInfo.InvariantCulture) + "."
                + curveNote + cutNote + bumpNote + scheduleNote
        };
    }

    /// <summary>
    /// The schedules' own A3 pages: the tables from the top-left margin, as
    /// many pages as they need, each with the footer without scale or north.
    /// </summary>
    private List<JObject> AddSchedulesPages(RhinoDoc doc, List<string> kinds, List<Schedules.Table> tables, int number)
    {
        var blocks = SheetBlocks(tables, out var why);
        if (blocks == null)
            throw new InvalidOperationException(why);
        var count = Schedules.Pages(blocks);
        var title = Schedules.SheetTitle(tables.Select(t => t.Kind).ToList());
        var records = new List<JObject>();
        for (var i = 0; i < count; i++)
        {
            var name = SchedulesPageNameFor(i + 1);
            var page = doc.Views.AddPageView(name, A3WidthMm, A3HeightMm);
            if (page == null)
                throw new InvalidOperationException(LayoutDetailFailedMessage);
            page.SetPageAsActive();
            ApplyPaperDisplay(page);
            var spec = new LayoutViewSpec { View = SchedulesView, PageName = name };
            var stableId = FormatStableId("l", number + i);
            var pageTitle = count > 1
                ? title + " (" + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + count.ToString(CultureInfo.InvariantCulture) + ")"
                : title;
            var sheetNo = SheetSet.Number(SchedulesView, 0, i);
            var footer = new JObject();
            var ids = AddSheetFooter(doc, page, spec, stableId, sheetNo, 0, pageTitle, footer);
            DrawSchedules(doc, page, SchedulesView, stableId, blocks.Where(b => b.Page == i).ToList(), ids);
            RememberSchedules(doc, page, kinds, stableId);
            records.Add(new JObject
            {
                ["view"] = SchedulesView,
                ["page"] = name,
                ["number"] = sheetNo,
                ["title"] = pageTitle,
                ["scale"] = 0,
                ["page_scale"] = 0,
                ["detail_count"] = 0,
                ["ids"] = ids,
                ["footer"] = footer,
                ["view_title"] = pageTitle,
                ["schedules"] = new JObject
                {
                    ["page"] = i + 1,
                    ["pages"] = count,
                    ["lists"] = ReadSchedules(doc, page),
                    ["cells_over"] = new JArray(CellsOver(doc, page).ToArray())
                }
            });
        }
        return records;
    }

    [McpCommand("export_pdf")]
    public JObject ExportPdf(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");

        var path = parameters?["path"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(path))
            return ExportPdfResult("", new JArray(), ExportNeedsPathMessage);
        if (!Path.IsPathRooted(path) || !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return ExportPdfResult("", new JArray(), ExportNeedsPdfMessage);

        RestorePrintColors(doc);
        ApplyDocumentPrintInk(doc);
        var layout = parameters?["layout"]?.ToString();
        var pages = InSetOrder(doc, MatchingForskPages(doc, layout));
        if (pages.Count == 0)
        {
            var message = string.IsNullOrWhiteSpace(layout) ? NoLayoutsMessage : "Unknown layout.";
            return ExportPdfResult("", new JArray(), message);
        }

        foreach (var page in pages)
            LeaveDetail(page);

        var full = Path.GetFullPath(path);
        var drawError = EnsureGreyscaleDrawings(doc, pages);
        if (!string.IsNullOrEmpty(drawError))
        {
            LogPrint(full, 0, "greyscale make2d " + drawError);
            return ExportPdfResult("", new JArray(), drawError);
        }

        foreach (var page in pages)
            ApplyPageDrawingDisplay(doc, page);

        if (!ForskPagesShowDrawing(doc, pages))
            return ExportPdfResult("", new JArray(), EmptyPdfMessage);

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // v3 N3: the PDF is drawn from the flat sheets the DWG is written from:
        // real lines and text on every OS. ViewCapture wrote white pages on
        // Rhino 7 Mac, so the page-preview capture stays only as the fallback.
        string reason;
        try
        {
            var sheets = WriteVectorPdf(doc, pages, full, out var unmapped);
            var notes = new List<string>();
            if (sheets.HatchFallback > 0) notes.Add("hatch_fallback " + sheets.HatchFallback);
            if (sheets.OffPage > 0) notes.Add("off_page " + sheets.OffPage);
            if (sheets.OtherFonts > 0) notes.Add("non-Arial texts in Helvetica " + sheets.OtherFonts);
            if (unmapped > 0) notes.Add("unmapped " + unmapped);
            LogPrint(full, sheets.Names.Count, "vector " + string.Join(" ", notes) + " " + GreyscaleMake2dNote(doc));
            var wrote = $"Wrote {sheets.Names.Count} page(s) to {full}.";
            if (_lastPlanStatsSet)
            {
                wrote += " Symbols "
                    + _lastPlanStats.Symbols.ToString(CultureInfo.InvariantCulture)
                    + ", arcs "
                    + _lastPlanStats.Arcs.ToString(CultureInfo.InvariantCulture)
                    + ", dashed "
                    + _lastPlanStats.Dashed.ToString(CultureInfo.InvariantCulture)
                    + ".";
                if (!string.IsNullOrEmpty(_lastPlanStats.Note))
                    wrote += " " + _lastPlanStats.Note;
            }
            var result = ExportPdfResult(full, sheets.Names, wrote);
            result["vector"] = true;
            result["hatch_fallback"] = sheets.HatchFallback;
            return result;
        }
        catch (Exception e)
        {
            reason = e.GetType().Name + ": " + e.Message;
        }
        LogPrint(full, 0, "raster fallback " + reason);
        var raster = ExportMacPreviewPdf(doc, pages, full);
        raster["vector"] = false;
        return raster;
    }
}
