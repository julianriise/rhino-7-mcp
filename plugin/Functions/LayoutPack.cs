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
/// viewport keeps the clay. PDF is Rhino.FileIO.FilePdf. Vector output uses
/// ViewCaptureSettings with RasterMode false. On Rhino 7 Mac that capture
/// wrote a white sheet, so Mac export activates each layout, redraws, waits,
/// then draws GetPreviewImage into the PDF. A blank frame is a paint race:
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
        "Unknown view. Use plan, north, east, south, west, schedules, or a stored section (section_a).";
    private const string UnknownPaperMessage = "Unknown paper. Use A3.";
    private const string ExportNeedsPathMessage = "export_pdf requires a file path.";
    private const string ExportNeedsPdfMessage = "export_pdf path must be an absolute .pdf file.";
    private const string NoLayoutsMessage = "No layouts to print. Call layout_pack first.";
    private const string PdfWriteFailedMessage = "PDF write failed.";
    private const string LayoutDetailFailedMessage = "Layout detail failed.";
    private const string EmptyDetailMessage = "Layout detail is empty. The sheet does not show the drawing.";
    private const string EmptyPdfMessage = "PDF detail is empty. The sheet does not show the drawing.";
    private const string CaptureFailedPrefix = "capture failed after activate/Wait";
    private const string PlanCutRole = "plan_cut";
    private const string ForskPenName = "Forsk Pen";
    private const int PenEdgePx = 1;
    private const string PenIniPath = "/tmp/forsk-pen.ini";
    private const string PenAfterPath = "/tmp/forsk-pen-after.ini";
    private const string PenKeysPath = "/tmp/forsk-pen-keys.txt";
    private const string PenStockPath = "/tmp/forsk-pen-stock.ini";
    private const string PrintColorSourceKey = "forsk:print_cs";
    private const string PrintRgbKey = "forsk:print_rgb";
    private const string PenReloadFailedMessage =
        "Forsk Pen failed. The display mode did not reload, so the layout was not assigned a deleted mode.";
    private static int _penPass = 0;
    private static int _penApplied = -1;
    private static string _penLog = "";
    private static bool _penForceObjectBlack;
    private static bool _penReloadFailed;
    private static int _penUsageZero;
    private static bool _penSawEdgeUsage;
    private static int _singleColorUsage = 2;
    private static bool _singleColorLearned;
    private static Guid _penSourceId;
    private static int _penSamplePass = -2;
    private static bool _penKeysStarted;
    private static bool _drawIncludeExisting = true;
    private static PlanStats _lastPlanStats;
    private static bool _lastPlanStatsSet;

    private const double A3WidthMm = 420.0;
    private const double A3HeightMm = 297.0;
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

    private static readonly string[] LayoutShowLayerNames = { "A-WALL", "A-FLOR", "A-ROOF" };

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
            default:
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

        MaybeStoreMeta(doc, parameters, "project");
        MaybeStoreMeta(doc, parameters, "client");
        MaybeStoreMeta(doc, parameters, "address");
        MaybeStoreMeta(doc, parameters, "date");
        MaybeStoreMeta(doc, parameters, "scale_label");
        return ProjectMetaRecord(doc);
    }

    [McpCommand("layout_pack")]
    public JObject LayoutPack(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");

        var paper = parameters?["paper"]?.ToString();
        if (string.IsNullOrWhiteSpace(paper)) paper = "A3";
        if (!paper.Trim().Equals("A3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UnknownPaperMessage);

        // No scale: the plan fits the detail. Elevations keep 1:100.
        var requestedScale = 100;
        var fitPlan = true;
        var scaleToken = parameters?["scale"];
        if (scaleToken != null && scaleToken.Type != JTokenType.Null)
        {
            fitPlan = false;
            requestedScale = scaleToken.ToObject<int>();
            if (requestedScale < 1)
                throw new InvalidOperationException("Scale must be a positive number.");
        }

        var views = ReadLayoutViews(doc, parameters);
        var includeExisting = ReadBoolParam(parameters, "include_existing", true);
        var replace = ReadBoolParam(parameters, "replace", true);
        // The schedules are a page of their own, not a drawing view.
        var withSchedules = views.RemoveAll(v => string.Equals(v, SchedulesView, StringComparison.OrdinalIgnoreCase)) > 0;
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
        _drawIncludeExisting = includeExisting;
        var detailW = A3WidthMm - (2.0 * LayoutMarginMm);
        var detailH = A3HeightMm - LayoutMarginMm - FooterReserveMm - LayoutMarginMm;

        var pages = new JArray();
        var applied = new List<int>();
        var drawingNotes = new List<string>();
        string cutNote = null;
        var planCutZ = FloorTopZ(clay) + ForskDefaults.PlanCutHeightMm;
        var planClip = new Plane(new Point3d(0, 0, planCutZ), -Vector3d.ZAxis);
        // Sections print at the plan's scale when the plan is in this pack.
        var planScale = 0;
        foreach (var viewName in views)
        {
            if (!TryGetLayoutView(viewName, out var spec))
                throw new InvalidOperationException(UnknownLayoutViewMessage);
            var section = Sections.TryLetter(spec.View, out var sectionLetter);
            if (section && !TryGetSectionView(doc, spec.View, out _, out _))
                throw new InvalidOperationException("No section " + sectionLetter + ". section_add stores it first.");

            if (replace)
                RemoveLayoutPages(doc, spec.View, false);

            Plane? clip = null;
            var plan = string.Equals(spec.View, "plan", StringComparison.OrdinalIgnoreCase);
            if (plan)
                clip = planClip;
            var strokeScale = 0;
            double fitNeed = 0;
            if (plan)
                strokeScale = FitLayoutScale(requestedScale, ViewSpan(bbox, spec.View), detailW, detailH, fitPlan);
            else if (section)
                strokeScale = planScale > 0 ? planScale : requestedScale;
            // A section with the plan keeps the plan's scale unless it does
            // not fit; alone, it fits like the plan.
            var fitBase = plan || planScale <= 0 ? requestedScale : planScale;
            var fitAll = plan ? fitPlan : (planScale <= 0 && fitPlan);
            var drawn = BakeGreyscaleDrawing(doc, spec.View, includeExisting, clip, strokeScale);
            if (plan || section)
            {
                // Tags, marks and dimensions keep their paper size, so the
                // drawing grows with the scale: bake again at the scale the
                // last bake needs until it holds. After the first round the
                // scale only goes up, so it cannot swing between two steps.
                for (var round = 0; round < 4; round++)
                {
                    if (!drawn.Box.IsValid) break;
                    var fitted = FitLayoutScale(fitBase, ViewSpan(drawn.Box, spec.View), detailW, detailH, fitAll);
                    if (fitted == strokeScale || (round > 0 && fitted < strokeScale)) break;
                    drawn = BakeGreyscaleDrawing(doc, spec.View, includeExisting, clip, fitted);
                    strokeScale = fitted;
                }
                fitNeed = LayoutFitNeed(ViewSpan(drawn.Box, spec.View), detailW, detailH);
                if (plan) planScale = strokeScale;
            }
            if (!string.IsNullOrEmpty(drawn.Error) || drawn.Count < 1 || !drawn.Box.IsValid)
            {
                var why = string.IsNullOrEmpty(drawn.Error)
                    ? "No visible curves for " + spec.View + "."
                    : drawn.Error;
                throw new InvalidOperationException(why);
            }
            drawingNotes.Add(
                spec.View + " " + drawn.Count.ToString(CultureInfo.InvariantCulture));

            // Plan strokes and tags were drawn at strokeScale. The detail, the
            // title block, and the view title use that same value.
            var scale = plan || section
                ? strokeScale
                : FitLayoutScale(requestedScale, ViewSpan(drawn.Box, spec.View), detailW, detailH, false);
            applied.Add(scale);

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
            var viewTitle = OpeningTypes.ViewTitle(spec.View, WallLevel(doc));
            // The footer reads the scale the detail recorded, not the request.
            var pageScale = scaleLocked ? DetailModelScale(page) : 0;
            var footer = new JObject();
            var ids = AddSheetFooter(doc, page, spec, stableId, pageScale, viewTitle, plan, footer);
            var pageRecord = new JObject
            {
                ["view"] = spec.View,
                ["page"] = spec.PageName,
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
                pageRecord["fit_need"] = Math.Round(fitNeed, 2);
                pageRecord["fitted"] = fitPlan || scale != requestedScale;
                pageRecord["fill"] = Math.Round(LayoutFitNeed(ViewSpan(drawn.Box, spec.View), detailW, detailH) * 0.9 / scale, 3);
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
            if (section && drawn.Section != null)
            {
                pageRecord["view_title"] = viewTitle;
                pageRecord["fills"] = drawn.Fills;
                pageRecord["section"] = SectionPageRecord(doc, spec.View, drawn);
            }
            pages.Add(pageRecord);
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
                scheduleNote = " Schedules: " + ScheduleCounts(scheduleTables)
                    + (added.Count > 1 ? " on " + added.Count.ToString(CultureInfo.InvariantCulture) + " pages." : ".");
            }
        }

        doc.Views.Redraw();
        var reported = applied.Count > 0 ? applied[0] : requestedScale;
        var sameScale = true;
        foreach (var scale in applied)
        {
            if (scale != reported) sameScale = false;
        }
        var at = sameScale
            ? "at 1:" + reported.ToString(CultureInfo.InvariantCulture)
            : "with per-page scales";
        var curveNote = drawingNotes.Count == 0
            ? ""
            : " Greyscale drawing: " + string.Join(", ", drawingNotes.ToArray()) + ".";
        return new JObject
        {
            ["pages"] = pages,
            ["count"] = pages.Count,
            ["scale"] = reported,
            ["message"] = $"Laid out {pages.Count} page(s) on A3 {at}.{curveNote}{cutNote}{scheduleNote}"
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
            var footer = new JObject();
            var ids = AddSheetFooter(doc, page, spec, stableId, 0, pageTitle, false, footer);
            DrawSchedules(doc, page, stableId, blocks.Where(b => b.Page == i).ToList(), ids);
            RememberSchedules(doc, page, kinds, stableId);
            records.Add(new JObject
            {
                ["view"] = SchedulesView,
                ["page"] = name,
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
        var layout = parameters?["layout"]?.ToString();
        var pages = MatchingForskPages(doc, layout);
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

        // ViewCaptureSettings on this Mac wrote a white page (no title block,
        // no clay). The page preview of the same layout has the drawing.
        if (Rhino.Runtime.HostUtils.RunningOnOSX)
            return ExportMacPreviewPdf(doc, pages, full);

        var names = new JArray();
        var captures = new List<ViewCaptureSettings>();
        try
        {
            var pdf = FilePdf.Create();
            foreach (var page in pages)
            {
                ApplyPageDrawingDisplay(doc, page);
                var settings = new ViewCaptureSettings(page, PdfDpi)
                {
                    RasterMode = false,
                    OutputColor = ViewCaptureSettings.ColorMode.BlackAndWhite,
                    DrawGrid = false,
                    DrawAxis = false,
                    DrawMargins = false,
                    DrawWallpaper = false,
                    DefaultPrintWidthMillimeters = 0.18
                };
                captures.Add(settings);
                pdf.AddPage(settings);
                names.Add(page.PageName ?? "");
            }

            pdf.Write(full);
            LogPrint(full, names.Count, "vector " + GreyscaleMake2dNote(doc));
            return ExportPdfResult(full, names, $"Wrote {names.Count} page(s) to {full}.");
        }
        catch (Exception)
        {
            return ExportPdfResult(path, names, PdfWriteFailedMessage);
        }
        finally
        {
            foreach (var settings in captures)
                settings.Dispose();
        }
    }

    /// <summary>
    /// Draw each layout preview onto the PDF. The save dialog has already
    /// closed. A blank frame is retried. Pages that have ink are written
    /// even when another page stays white.
    /// </summary>
    private JObject ExportMacPreviewPdf(RhinoDoc doc, List<RhinoPageView> pages, string full)
    {
        int dpi = (int)Math.Round(PdfDpi);
        int dotsW = (int)Math.Round(A3WidthMm / 25.4 * dpi);
        int dotsH = (int)Math.Round(A3HeightMm / 25.4 * dpi);
        var names = new JArray();
        var notes = new List<string>();
        var blanks = new List<string>();
        var blankLabels = new List<string>();
        var shots = new List<Bitmap>();
        var reads = new List<PreviewRead>();
        try
        {
            FocusRhino();
            RhinoApp.Wait();
            int pageNumber = 0;
            foreach (var page in pages)
            {
                pageNumber++;
                Bitmap bmp = null;
                try
                {
                    var start = Environment.TickCount;
                    bmp = CapturePageAfterWait(page, pageNumber, full, dotsW, dotsH, reads, out var ink, out var attempt);
                    var size = bmp == null ? "null" : bmp.Width + "x" + bmp.Height;
                    var note = (page.PageName ?? "") + " ink " + ink + " " + size
                        + " attempt " + attempt.ToString(CultureInfo.InvariantCulture)
                        + " ms " + unchecked(Environment.TickCount - start).ToString(CultureInfo.InvariantCulture);
                    if (ink <= 0)
                    {
                        // The last read's PNG; LogPreviewReads saves it once every page is read.
                        var debug = reads.Count > 0 ? reads[reads.Count - 1].Log.Png : "-";
                        note += " " + debug;
                        blanks.Add(debug);
                        var pageName = string.IsNullOrEmpty(page.PageName)
                            ? "page " + pageNumber.ToString(CultureInfo.InvariantCulture)
                            : page.PageName;
                        blankLabels.Add(pageName + " " + debug);
                        if (bmp != null)
                        {
                            bmp.Dispose();
                            bmp = null;
                        }
                    }
                    else
                    {
                        shots.Add(bmp);
                        bmp = null;
                        names.Add(page.PageName ?? "");
                    }
                    notes.Add(note);
                }
                finally
                {
                    if (bmp != null) bmp.Dispose();
                }
            }

            var detail = GreyscaleMake2dNote(doc) + "; " + string.Join("; ", notes.ToArray());
            if (shots.Count == 0)
            {
                LogPrint(full, 0, detail);
                var message = CaptureFailedPrefix + ". Debug images: " + string.Join(", ", blanks.ToArray());
                return ExportPdfResult("", new JArray(), message);
            }

            var pdf = FilePdf.Create();
            for (int i = 0; i < shots.Count; i++)
            {
                var bmp = shots[i];
                int width = bmp.Width;
                int height = bmp.Height;
                pdf.AddPage(width, height, dpi);
                pdf.DrawBitmap(i + 1, bmp, 0, 0, width, height, 0);
            }

            pdf.Write(full);
            LogPrint(full, names.Count, detail);
            var wrote = $"Wrote {names.Count} page(s) to {full}.";
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
            if (blankLabels.Count > 0)
                wrote += " Blank preview: " + string.Join("; ", blankLabels.ToArray()) + ".";
            return ExportPdfResult(full, names, wrote);
        }
        catch (Exception)
        {
            LogPrint(full, names.Count, "write failed");
            return ExportPdfResult(full, names, PdfWriteFailedMessage);
        }
        finally
        {
            LogPreviewReads(reads);
            foreach (var shot in shots)
            {
                if (shot != null) shot.Dispose();
            }
        }
    }

    // One read of a page preview and, when it was blank, its frame for the debug PNG.
    private sealed class PreviewRead
    {
        public PreviewFrame.Attempt Log;
        public Bitmap Frame;
    }

    /// <summary>
    /// Once every page is read: save each blank frame as a PNG and log every
    /// read, one line each. Saving waits until here so it cannot change the
    /// time between reads.
    /// </summary>
    private static void LogPreviewReads(List<PreviewRead> reads)
    {
        foreach (var read in reads)
        {
            if (read.Frame != null)
            {
                var error = SaveDebugPng(read.Frame, read.Log.Png);
                if (error != null)
                    read.Log.Png = "failed:" + error;
                read.Frame.Dispose();
                read.Frame = null;
            }
            AppendPrintLog(read.Log.LogLine());
        }
    }

    /// <summary>
    /// Page active, detail not active, drawing layers only, then paint.
    /// The paper and the detail stay Wireframe. Curves are already black.
    /// The page that was read last is reached through another layout, and
    /// the read waits for the page to draw on screen.
    /// </summary>
    private void PrepareMacPage(RhinoPageView page, PreviewFrame.Attempt log, PaintWatch watch)
    {
        if (page == null) return;
        var clock = Stopwatch.StartNew();
        var doc = page.Document ?? RhinoDoc.ActiveDoc;
        var view = ViewKeyForPage(page);
        if (doc != null && view != SchedulesView && CountPrintDrawings(doc, view) == 0)
            EnsureGreyscaleDrawings(doc, new List<RhinoPageView> { page });
        ApplyPageDrawingDisplay(doc, page);
        var other = page.MainViewport.Id == _lastPreviewPage ? OtherPage(doc, page) : null;
        log.Via = other == null ? "model" : "page";
        log.SettleMs = PreviewFrame.SettleMs(log.Number);
        log.PrepMs = clock.ElapsedMilliseconds;
        clock.Restart();
        WakePagePreview(doc, page, other, log.SettleMs, watch);
        log.WakeMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var start = Environment.TickCount;
        while (watch.Page < 1 && unchecked(Environment.TickCount - start) < PreviewFrame.PaintWaitMs)
            RhinoApp.Wait();
        log.PaintMs = clock.ElapsedMilliseconds;
        clock.Restart();
        log.Idle = WaitForOneIdle();
        log.IdleMs = clock.ElapsedMilliseconds;
    }

    // The page the last preview read was of, by its paper viewport.
    private static Guid _lastPreviewPage;

    /// <summary>
    /// The Mac page preview is painted once. A later read of a page that is
    /// already active returns an empty buffer (white JPEG, transparent black
    /// pixels). Step through a model view and dirty the detail mode so the
    /// page paints again. The camera stays put.
    ///
    /// That step leaves the model view's picture on screen, and a read that
    /// comes before the page has taken its place is that picture with the
    /// page's tiles on it (PreviewFrame, Viewport). Selecting the page that
    /// was read last gave it on every read (garage cap3, all 23 reads of a
    /// blank page), and a page reached from another layout painted (cap2).
    /// So that page goes through other, another layout, first. The model
    /// view stays in between: its grey is what makes a missed read blank
    /// and not a wrong sheet. Settle keeps it up before the page is selected.
    /// </summary>
    private static void WakePagePreview(
        RhinoDoc doc, RhinoPageView page, RhinoPageView other, int settleMs, PaintWatch watch)
    {
        if (page == null) return;
        if (doc != null && other != null)
        {
            doc.Views.ActiveView = other;
            other.SetPageAsActive();
            other.Redraw();
            RhinoApp.Wait();
            WaitForOneIdle();
        }
        if (doc != null)
        {
            RhinoView model = null;
            try
            {
                var views = doc.Views.GetViewList(true, false);
                if (views != null)
                {
                    foreach (var view in views)
                    {
                        if (view == null || view is RhinoPageView) continue;
                        model = view;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                model = null;
            }
            if (model != null)
            {
                doc.Views.ActiveView = model;
                model.Redraw();
                RhinoApp.Wait();
                var start = Environment.TickCount;
                while (unchecked(Environment.TickCount - start) < settleMs)
                    RhinoApp.Wait();
            }
        }

        // Stay in Wireframe. Switching the detail to Shaded clears its
        // framebuffer to opaque black, and GetPreviewImage reads that buffer
        // before Wireframe paints. CopyPreview only whitens transparent black,
        // so the black frame is kept as ink.
        var wire = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.WireframeId);
        var details = page.GetDetailViews();
        if (details != null && wire != null)
        {
            foreach (var detail in details)
            {
                var viewport = detail?.Viewport;
                if (viewport == null) continue;
                viewport.DisplayMode = wire;
            }
        }

        watch.Start();
        if (doc != null)
            doc.Views.ActiveView = page;
        page.SetPageAsActive();
        page.Redraw();
        if (doc != null)
            doc.Views.Redraw();
        RhinoApp.Wait();
    }

    /// <summary>Another layout of the document, or null when the page is the only one.</summary>
    private static RhinoPageView OtherPage(RhinoDoc doc, RhinoPageView page)
    {
        if (doc == null || page == null) return null;
        RhinoPageView[] all;
        try { all = doc.Views.GetPageViews(); }
        catch (Exception) { return null; }
        if (all == null) return null;
        foreach (var other in all)
        {
            if (other != null && other.MainViewport.Id != page.MainViewport.Id)
                return other;
        }
        return null;
    }

    /// <summary>
    /// Counts what the display pipeline draws from Start until it is
    /// disposed: Page is the page's own paper viewport, Other every view
    /// that is not the page or one of its details. The read waits for Page:
    /// a page that has not drawn since it was selected is taken as not on
    /// screen yet. Not proven live; the capture line's paint= is the check.
    /// </summary>
    private sealed class PaintWatch : IDisposable
    {
        private readonly Guid _paper;
        private readonly HashSet<Guid> _details = new HashSet<Guid>();
        private int _page;
        private int _other;
        private bool _started;

        public PaintWatch(RhinoPageView page)
        {
            _paper = page.MainViewport.Id;
            var details = page.GetDetailViews();
            if (details == null) return;
            foreach (var detail in details)
            {
                if (detail?.Viewport != null)
                    _details.Add(detail.Viewport.Id);
            }
        }

        public int Page => Volatile.Read(ref _page);
        public int Other => Volatile.Read(ref _other);

        public void Start()
        {
            if (_started) return;
            _started = true;
            DisplayPipeline.PostDrawObjects += OnDraw;
        }

        private void OnDraw(object sender, DrawEventArgs e)
        {
            try
            {
                var viewport = e?.Viewport;
                if (viewport == null) return;
                var id = viewport.Id;
                if (id == _paper)
                    Interlocked.Increment(ref _page);
                else if (!_details.Contains(id))
                    Interlocked.Increment(ref _other);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (_started)
                DisplayPipeline.PostDrawObjects -= OnDraw;
        }
    }

    /// <summary>
    /// Elevations rebuild only when their curves are missing. The plan pack
    /// rebuilds on every export so a type swap changes the symbol. The
    /// schedules page is drawn again from the model too.
    /// </summary>
    private string EnsureGreyscaleDrawings(RhinoDoc doc, List<RhinoPageView> pages)
    {
        if (doc == null || pages == null) return null;
        // A layout is often the active view here. New objects then land in
        // page space, at model millimetres, and a ribbon draws on the sheet
        // at 1:1. Bake while a model view is active.
        UseModelView(doc);
        RestorePrintColors(doc);
        List<RhinoObject> clay = null;
        var schedulesDrawn = false;
        foreach (var page in pages)
        {
            var view = ViewKeyForPage(page);
            if (string.IsNullOrEmpty(view)) continue;
            if (view == SchedulesView)
            {
                // All schedules pages at once: rows can move from one page to the next.
                if (schedulesDrawn) continue;
                schedulesDrawn = true;
                var stale = RefreshSchedules(doc);
                if (stale != null) return stale;
                continue;
            }
            var plan = view.Equals("plan", StringComparison.OrdinalIgnoreCase);
            // A type swap must show on the next export without a new layout_pack.
            if (!plan && CountPrintDrawings(doc, view) > 0) continue;
            var section = Sections.TryLetter(view, out _);
            if (clay == null)
                clay = CollectLayoutClay(doc, _drawIncludeExisting, out _);
            Plane? clip = null;
            var strokeScale = 0;
            if (plan)
            {
                var cutZ = FloorTopZ(clay) + ForskDefaults.PlanCutHeightMm;
                clip = new Plane(new Point3d(0, 0, cutZ), -Vector3d.ZAxis);
                strokeScale = DetailModelScale(page);
                if (strokeScale < 1) strokeScale = 100;
            }
            else if (section)
            {
                strokeScale = DetailModelScale(page);
                if (strokeScale < 1) strokeScale = 100;
            }
            var drawn = BakeGreyscaleDrawing(doc, view, _drawIncludeExisting, clip, strokeScale);
            // The locked detail already frames this pack. Panning again after a
            // rebuild left the Mac preview black, so the camera stays.
            if (!string.IsNullOrEmpty(drawn.Error) || drawn.Count < 1)
            {
                return string.IsNullOrEmpty(drawn.Error)
                    ? "No visible curves for " + view + "."
                    : drawn.Error;
            }
        }
        return null;
    }

    private static void ApplyPageDrawingDisplay(RhinoDoc doc, RhinoPageView page)
    {
        if (page == null) return;
        LeaveDetail(page);
        ApplyPaperDisplay(page);
        var view = ViewKeyForPage(page);
        var drawLayer = doc == null ? null : FindDrawLayer(doc, view);
        var details = page.GetDetailViews();
        if (details == null) return;
        foreach (var detail in details)
        {
            if (detail == null) continue;
            if (detail.IsActive)
                detail.IsActive = false;
            ApplyDetailDisplay(detail.Viewport);
            if (doc != null && detail.Viewport != null && drawLayer != null)
                SetDetailDrawingVisibility(doc, detail.Viewport.Id, drawLayer);
        }
    }

    private static string ViewKeyForPage(RhinoPageView page)
    {
        if (page == null) return null;
        if (IsSchedulesPage(page))
            return SchedulesView;
        foreach (var name in new[] { "plan", "north", "east", "south", "west" })
        {
            if (!TryGetLayoutView(name, out var spec)) continue;
            if (string.Equals(page.PageName, spec.PageName, StringComparison.OrdinalIgnoreCase))
                return spec.View;
        }
        for (var c = 'a'; c <= 'z'; c++)
        {
            var view = Sections.View(c.ToString());
            if (TryGetLayoutView(view, out var spec)
                && string.Equals(page.PageName, spec.PageName, StringComparison.OrdinalIgnoreCase))
                return view;
        }
        return null;
    }

    /// <summary>
    /// Read the page until a frame is a sheet or PreviewFrame.ReadAgain says
    /// its time is up. Every read goes into reads for the print log; the
    /// first and the latest blank frame are kept there for their debug PNGs.
    /// Attempt is the number of reads made; on a miss ink is 0. A second
    /// read is of the page read last, so it goes through another layout, and
    /// from the third the model view is kept up longer each time
    /// (PreviewFrame.SettleMs). The capture line says which read painted.
    /// </summary>
    private Bitmap CapturePageAfterWait(
        RhinoPageView page, int pageNumber, string pdfPath, int dotsW, int dotsH,
        List<PreviewRead> reads, out int ink, out int attempt)
    {
        var size = new Size(dotsW, dotsH);
        var budget = Stopwatch.StartNew();
        int firstRead = reads.Count;
        PreviewRead firstBlank = null;
        PreviewRead lastBlank = null;
        Bitmap sheet = null;
        ink = 0;
        attempt = 0;
        do
        {
            attempt++;
            var log = new PreviewFrame.Attempt
            {
                Pdf = Path.GetFileName(pdfPath),
                Page = pageNumber,
                PageName = ShortPageName(page),
                Number = attempt
            };
            Bitmap bmp;
            using (var watch = new PaintWatch(page))
            {
                PrepareMacPage(page, log, watch);
                if (attempt > 1)
                {
                    // The first frame after the modal can still be unpainted.
                    // Pause, then one more idle, before reading the preview again.
                    var clock = Stopwatch.StartNew();
                    var start = Environment.TickCount;
                    while (unchecked(Environment.TickCount - start) < 150)
                        RhinoApp.Wait();
                    if (!WaitForOneIdle())
                        log.Idle = false;
                    log.IdleMs += clock.ElapsedMilliseconds;
                }
                bmp = ReadPreview(page, size, log, watch);
            }
            var read = new PreviewRead { Log = log };
            reads.Add(read);
            // A near-uniform frame is a missed paint: all white, the model
            // view's picture, or the opaque black buffer. It is not a sheet.
            // Keep looking.
            if (!log.Blank)
            {
                ink = log.Dark;
                sheet = bmp;
                break;
            }
            if (bmp != null)
            {
                read.Frame = bmp;
                log.Png = PreviewFrame.DebugPngPath(pdfPath, pageNumber, attempt);
                if (firstBlank == null)
                    firstBlank = read;
                else
                {
                    // A frame is 17 MB. Keep the first and the latest.
                    if (lastBlank != null)
                    {
                        lastBlank.Frame.Dispose();
                        lastBlank.Frame = null;
                        lastBlank.Log.Png = "-";
                    }
                    lastBlank = read;
                }
            }
        }
        while (PreviewFrame.ReadAgain(attempt, budget.ElapsedMilliseconds));
        for (int i = firstRead; i < reads.Count; i++)
            reads[i].Log.Of = attempt;
        return sheet;
    }

    /// <summary>One GetPreviewImage, copied and counted, each step timed into the log.</summary>
    private static Bitmap ReadPreview(RhinoPageView page, Size size, PreviewFrame.Attempt log, PaintWatch watch)
    {
        var clock = Stopwatch.StartNew();
        log.Paint = watch.Page;
        var raw = page.GetPreviewImage(size, false);
        log.PreviewMs = clock.ElapsedMilliseconds;
        log.ReadPaint = watch.Page - log.Paint;
        log.OtherPaint = watch.Other;
        _lastPreviewPage = page.MainViewport.Id;
        if (raw != null)
        {
            log.RawWidth = raw.Width;
            log.RawHeight = raw.Height;
            log.Format = raw.PixelFormat.ToString();
        }
        clock.Restart();
        var bmp = CopyPreview(raw, out var copy, out var clear);
        if (bmp != null && !ReferenceEquals(bmp, raw))
            raw?.Dispose();
        else
            bmp = raw;
        log.CopyMs = clock.ElapsedMilliseconds;
        log.Copy = copy;
        log.Clear = clear;
        clock.Restart();
        log.Dark = CountDarkSamples(bmp, out log.Wash, out log.Band);
        log.Width = bmp?.Width ?? 0;
        log.Height = bmp?.Height ?? 0;
        log.CheckMs = clock.ElapsedMilliseconds;
        return bmp;
    }

    private static string ShortPageName(RhinoPageView page)
    {
        var name = page?.PageName ?? "";
        return name.StartsWith(LayoutPagePrefix, StringComparison.Ordinal)
            ? name.Substring(LayoutPagePrefix.Length)
            : name;
    }

    private static void UseModelView(RhinoDoc doc)
    {
        if (doc == null) return;
        if (!(doc.Views.ActiveView is RhinoPageView)) return;
        RhinoView[] views = null;
        try { views = doc.Views.GetViewList(true, false); }
        catch (Exception) { views = null; }
        if (views == null) return;
        foreach (var view in views)
        {
            if (view == null || view is RhinoPageView) continue;
            doc.Views.ActiveView = view;
            return;
        }
    }

    /// <summary>Pump until Rhino goes idle once, at most 800 ms. False when the cap ran out first.</summary>
    private static bool WaitForOneIdle()
    {
        var idle = false;
        EventHandler handler = null;
        handler = (sender, args) =>
        {
            idle = true;
            RhinoApp.Idle -= handler;
        };
        RhinoApp.Idle += handler;
        try
        {
            var start = Environment.TickCount;
            while (!idle && unchecked(Environment.TickCount - start) < 800)
                RhinoApp.Wait();
        }
        finally
        {
            if (!idle)
                RhinoApp.Idle -= handler;
        }
        return idle;
    }

    private static void FocusRhino()
    {
        try
        {
            var window = Rhino.UI.RhinoEtoApp.MainWindow;
            if (window != null)
                window.Focus();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Null when saved, else the exception type.</summary>
    private static string SaveDebugPng(Bitmap bmp, string path)
    {
        try
        {
            bmp.Save(path, ImageFormat.Png);
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    /// <summary>
    /// Own the preview pixels, the ones the PDF has always drawn: a zero-alpha
    /// black sample is an unpainted buffer and turns white, any other is kept
    /// opaque. Rhino 7 Mac makes the preview from an NSImage, so its
    /// PixelFormat is Undefined and LockBits throws on it; the copy used to
    /// GetPixel and SetPixel every pixel, ~75 s a page, and left an NSData per
    /// pixel for the next wake to release (~15 s). Now the preview is drawn
    /// 1:1 into a 32bpp ARGB bitmap, an exact CoreGraphics copy, and locked
    /// once; PreviewFrame makes it opaque, and each colour goes through
    /// SetPixel once for the same colour match as before. Path is fast, raw
    /// (the copy failed, source returned) or none. Clear counts the unpainted
    /// pixels that turned white.
    /// </summary>
    private static Bitmap CopyPreview(Bitmap source, out string path, out long clear)
    {
        clear = 0;
        path = source == null ? "none" : "raw";
        if (source == null || source.Width < 2 || source.Height < 2) return source;
        int width = source.Width;
        int height = source.Height;
        Bitmap copy = null;
        try
        {
            byte[] frame;
            int stride;
            using (var known = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(known))
                    graphics.DrawImage(source, 0, 0, width, height);
                frame = LockedBytes(known, PixelFormat.Format32bppArgb, out stride);
            }
            var opaque = new byte[width * 4 * height];
            var unpainted = PreviewFrame.CopyOpaque(frame, stride, opaque, width * 4, width, height);
            PreviewFrame.WriteColours(opaque, width * 4, width, height, SetPixelColours);
            copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = copy.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(opaque, y * width * 4, IntPtr.Add(data.Scan0, y * data.Stride), width * 4);
            }
            finally
            {
                copy.UnlockBits(data);
            }
            clear = unpainted;
            path = "fast";
            var done = copy;
            copy = null;
            return done;
        }
        catch (Exception)
        {
            return source;
        }
        finally
        {
            copy?.Dispose();
        }
    }

    /// <summary>
    /// Each 0xRRGGBB colour as SetPixel writes it. SetPixel fills with a
    /// generic RGB colour and the Mac bitmap colour-matches it (grey 128
    /// lands as 146), so the PDF keeps the pixels the per-pixel copy wrote.
    /// </summary>
    private static int[] SetPixelColours(int[] colours)
    {
        using (var scratch = new Bitmap(colours.Length, 1, PixelFormat.Format32bppArgb))
        {
            for (int i = 0; i < colours.Length; i++)
            {
                int rgb = colours[i];
                scratch.SetPixel(i, 0, Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255));
            }
            var bgra = LockedBytes(scratch, PixelFormat.Format32bppArgb, out _);
            var written = new int[colours.Length];
            for (int i = 0; i < written.Length; i++)
                written[i] = bgra[i * 4] | (bgra[(i * 4) + 1] << 8) | (bgra[(i * 4) + 2] << 16);
            return written;
        }
    }

    /// <summary>The bitmap's rows in the given format, top row first.</summary>
    private static byte[] LockedBytes(Bitmap bmp, PixelFormat format, out int stride)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, format);
        try
        {
            stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), buffer, y * stride, stride);
            return buffer;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Dark samples, and the wash and band of a frame that locks. A raw frame (the copy failed) gives dark only.</summary>
    private static int CountDarkSamples(Bitmap bmp, out int wash, out int band)
    {
        wash = 0;
        band = 0;
        if (bmp == null || bmp.Width < 2 || bmp.Height < 2) return 0;
        // A locked buffer keeps the ink grid cheap.
        const int dense = PreviewFrame.InkStep;
        var format = bmp.PixelFormat;
        bool direct = format == PixelFormat.Format32bppArgb
            || format == PixelFormat.Format32bppRgb
            || format == PixelFormat.Format32bppPArgb
            || format == PixelFormat.Format24bppRgb;
        if (direct)
        {
            try
            {
                int bpp = format == PixelFormat.Format24bppRgb ? 3 : 4;
                var buffer = LockedBytes(bmp, format, out var stride);
                var ink = PreviewFrame.Measure(buffer, stride, bpp, bmp.Width, bmp.Height, dense);
                wash = ink.Wash;
                band = ink.Band;
                return ink.Dark;
            }
            catch (Exception)
            {
            }
        }
        int step = direct ? dense : Math.Max(8, bmp.Width / 80);
        return CountDarkPixels(bmp, step);
    }

    private static int CountDarkPixels(Bitmap bmp, int step)
    {
        if (step < 1) step = 1;
        int dark = 0;
        for (int y = 0; y < bmp.Height; y += step)
        {
            for (int x = 0; x < bmp.Width; x += step)
            {
                Color color;
                try { color = bmp.GetPixel(x, y); }
                catch (Exception) { return dark; }
                if (PreviewFrame.IsDark(color.R, color.G, color.B)) dark++;
            }
        }
        return dark;
    }

    private static void LogPrint(string path, int pages, string detail)
    {
        try
        {
            var line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                + " pages " + pages + " " + detail + " " + path + "\n";
            File.AppendAllText(PrintLogPath, line);
            RhinoApp.WriteLine("Forsk PDF log: " + PrintLogPath);
        }
        catch (Exception)
        {
        }
    }

    [McpCommand("clear_layouts")]
    public JObject ClearLayouts(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");

        HashSet<string> viewSet = null;
        if (parameters?["views"] is JArray requested && requested.Count > 0)
        {
            viewSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in requested)
            {
                var name = token?.ToString()?.Trim();
                if (string.Equals(name, SchedulesView, StringComparison.OrdinalIgnoreCase))
                {
                    viewSet.Add(SchedulesView);
                    continue;
                }
                if (!TryGetLayoutView(name, out var spec))
                    throw new InvalidOperationException(UnknownLayoutViewMessage);
                viewSet.Add(spec.View);
            }
        }

        var dryRun = ReadBoolParam(parameters, "dry_run", false);
        if (!dryRun)
            RestorePrintColors(doc);
        return RemoveLayoutPages(doc, viewSet, dryRun);
    }

    private static void MaybeStoreMeta(RhinoDoc doc, JObject parameters, string key)
    {
        var token = parameters?[key];
        if (token == null || token.Type == JTokenType.Null) return;
        var text = token.Type == JTokenType.String ? token.ToString() : null;
        if (text == null) return;
        if (string.IsNullOrWhiteSpace(text))
            doc.Strings.Delete(LayoutMetaSection, key);
        else
            doc.Strings.SetString(LayoutMetaSection, key, text.Trim());
    }

    private static JObject ProjectMetaRecord(RhinoDoc doc)
    {
        return new JObject
        {
            ["project"] = MetaOrEmpty(doc, "project"),
            ["client"] = MetaOrEmpty(doc, "client"),
            ["address"] = MetaOrEmpty(doc, "address"),
            ["date"] = MetaOr(doc, "date", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["scale_label"] = MetaOr(doc, "scale_label", "1:100")
        };
    }

    private static string MetaOrEmpty(RhinoDoc doc, string key)
    {
        return MetaOr(doc, key, "");
    }

    private static string MetaOr(RhinoDoc doc, string key, string fallback)
    {
        var value = doc.Strings.GetValue(LayoutMetaSection, key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static List<string> ReadLayoutViews(RhinoDoc doc, JObject parameters)
    {
        var views = new List<string>();
        if (parameters?["views"] is JArray requested)
        {
            foreach (var token in requested)
            {
                var text = token?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    views.Add(text.Trim());
            }
            if (views.Count == 0)
                throw new InvalidOperationException(UnknownLayoutViewMessage);
            return views;
        }

        views.Add("plan");
        views.Add("north");
        views.Add("east");
        views.Add("south");
        views.Add("west");
        foreach (var def in ReadSectionDefs(doc))
            views.Add(Sections.View(def.Letter));
        views.Add(SchedulesView);
        return views;
    }

    private static List<RhinoObject> CollectLayoutClay(RhinoDoc doc, bool includeExisting, out bool hasWall)
    {
        hasWall = false;
        var clay = new List<RhinoObject>();
        foreach (var obj in doc.Objects)
        {
            if (obj?.Attributes == null) continue;
            var kind = GetForskKind(obj) ?? "";
            if (kind.Equals("drawing", StringComparison.OrdinalIgnoreCase)) continue;
            if (kind.Equals("layout", StringComparison.OrdinalIgnoreCase)) continue;
            if (kind.Equals("opening_marker", StringComparison.OrdinalIgnoreCase)) continue;
            if (kind.Equals("room", StringComparison.OrdinalIgnoreCase)) continue;
            if (includeExisting && IsExistingUnderlay(doc, obj))
            {
                clay.Add(obj);
                continue;
            }
            if (!IsForskGenerated(obj)) continue;
            if (kind.Equals("wall", StringComparison.OrdinalIgnoreCase))
            {
                hasWall = true;
                clay.Add(obj);
            }
            else if (kind.Equals("floor", StringComparison.OrdinalIgnoreCase)
                     || kind.Equals("roof", StringComparison.OrdinalIgnoreCase)
                     || kind.Equals("opening", StringComparison.OrdinalIgnoreCase))
            {
                clay.Add(obj);
            }
        }
        return clay;
    }

    private static BoundingBox ClayBoundingBox(List<RhinoObject> clay)
    {
        var bbox = BoundingBox.Empty;
        foreach (var obj in clay)
        {
            var geom = obj?.Geometry;
            if (geom == null) continue;
            var box = geom.GetBoundingBox(true);
            if (box.IsValid) bbox.Union(box);
        }
        return bbox;
    }

    private static double ViewSpanWidth(BoundingBox bbox, string view)
    {
        // S-DRAW packs are flattened into XY and the detail looks down.
        return bbox.Max.X - bbox.Min.X;
    }

    private static double ViewSpanHeight(BoundingBox bbox, string view)
    {
        return bbox.Max.Y - bbox.Min.Y;
    }

    private readonly struct Span
    {
        public Span(double width, double height)
        {
            Width = width;
            Height = height;
        }

        public double Width { get; }
        public double Height { get; }
    }

    private static Span ViewSpan(BoundingBox bbox, string view)
    {
        return new Span(ViewSpanWidth(bbox, view), ViewSpanHeight(bbox, view));
    }

    /// <summary>
    /// 1:requested when the clay fits the detail. Otherwise double until it fits.
    /// </summary>
    /// <summary>Scale denominator that fills 90% of the detail on the tighter side.</summary>
    private static double LayoutFitNeed(Span span, double paperW, double paperH)
    {
        if (span.Width <= 0 || span.Height <= 0 || paperW <= 0 || paperH <= 0) return 0;
        return Math.Max(span.Width / (paperW * 0.9), span.Height / (paperH * 0.9));
    }

    /// <summary>
    /// fit: the largest standard scale that fits. Otherwise the requested
    /// scale, rounded up to a standard step only when it does not fit.
    /// </summary>
    private static int FitLayoutScale(int requested, Span span, double paperW, double paperH, bool fit)
    {
        var scale = requested < 1 ? 100 : requested;
        if (span.Width <= 0 || span.Height <= 0 || paperW <= 0 || paperH <= 0)
            return scale;
        var need = LayoutFitNeed(span, paperW, paperH);
        if (fit) return Math.Max(1, OpeningTypes.RoundScaleUp(need));
        return need <= scale ? scale : OpeningTypes.RoundScaleUp(need);
    }

    private DetailViewObject AddClayDetail(
        RhinoDoc doc,
        RhinoPageView page,
        LayoutViewSpec spec,
        BoundingBox bbox,
        int scale,
        string drawLayerPath,
        out bool scaleLocked)
    {
        scaleLocked = false;
        var left = MmToPage(doc, LayoutMarginMm);
        var bottom = MmToPage(doc, LayoutMarginMm + FooterReserveMm);
        var right = MmToPage(doc, A3WidthMm - LayoutMarginMm);
        var top = MmToPage(doc, A3HeightMm - LayoutMarginMm);
        var detail = page.AddDetailView(
            spec.View,
            new Point2d(left, bottom),
            new Point2d(right, top),
            DefinedViewportProjection.Top);
        if (detail == null) return null;

        ApplyPaperDisplay(page);

        // Frame while the detail is active, then leave it before the scale lock.
        // CommitChanges on an active detail puts zoom-extents back.
        // AddDetailView already starts as Top. The drawing is flat in XY.
        // Do not aim this with the elevation look: that sees the sheet edge-on.
        page.SetActiveDetail(detail.Id);
        AimDetailCamera(detail, bbox);
        detail.CommitViewportChanges();
        LeaveDetail(page);

        scaleLocked = LockDetailScale(detail, scale);
        detail.CommitChanges();

        // That commit can leave the locked scale looking at empty space.
        // Pan onto the clay. Do not CommitChanges again: it resets the frame.
        detail = ReloadDetail(page, detail);
        if (detail == null) return null;
        page.SetActiveDetail(detail.Id);
        PanDetailOntoClay(detail, bbox);
        detail.CommitViewportChanges();
        LeaveDetail(page);

        if (!DetailSeesClay(detail.Viewport, bbox))
        {
            page.SetActiveDetail(detail.Id);
            var geom = detail.DetailGeometry;
            if (geom != null)
                geom.IsProjectionLocked = false;
            AimDetailCamera(detail, bbox);
            detail.CommitViewportChanges();
            LeaveDetail(page);
            scaleLocked = LockDetailScale(detail, scale);
            detail.CommitChanges();
            detail = ReloadDetail(page, detail);
            if (detail == null) return null;
            page.SetActiveDetail(detail.Id);
            PanDetailOntoClay(detail, bbox);
            detail.CommitViewportChanges();
            LeaveDetail(page);
        }

        if (detail.Viewport == null || detail.Viewport.Id == Guid.Empty)
            throw new InvalidOperationException(EmptyDetailMessage);
        if (!DetailSeesClay(detail.Viewport, bbox))
            throw new InvalidOperationException(EmptyDetailMessage);

        ApplyDetailDisplay(detail.Viewport);
        var drawLayer = FindDrawLayer(doc, spec.View);
        if (drawLayer == null && !string.IsNullOrEmpty(drawLayerPath))
            drawLayer = FindLayerCaseInsensitive(doc, drawLayerPath);
        if (drawLayer == null)
            throw new InvalidOperationException(EmptyDetailMessage);
        SetDetailDrawingVisibility(doc, detail.Viewport.Id, drawLayer);
        LeaveDetail(page);
        return detail;
    }

    /// <summary>
    /// Top view of a flattened S-DRAW pack. Look is -Z. Camera up is world Y,
    /// or world X when the pack has no Y extent. An elevation look vector
    /// sees the sheet edge-on (one hairline).
    /// </summary>
    private static void AimDetailCamera(DetailViewObject detail, BoundingBox bbox)
    {
        var vp = detail?.Viewport;
        if (vp == null || !bbox.IsValid) return;
        var look = -Vector3d.ZAxis;
        var target = bbox.Center;
        var dist = Math.Max(bbox.Diagonal.Length * 2.0, 5000.0);
        vp.ChangeToParallelProjection(true);
        vp.SetCameraLocations(target, target - (look * dist));
        vp.CameraUp = DrawingCameraUp(bbox);
        vp.SetCameraDirection(look, false);
        var framed = bbox;
        var pad = Math.Max(500.0, framed.Diagonal.Length * 0.02);
        framed.Inflate(pad);
        if (framed.Max.Z - framed.Min.Z < 1.0)
            framed.Inflate(0, 0, 1000.0);
        vp.ZoomBoundingBox(framed);
        ApplyDetailDisplay(vp);
    }

    private static Vector3d DrawingCameraUp(BoundingBox bbox)
    {
        var dx = bbox.Max.X - bbox.Min.X;
        var dy = bbox.Max.Y - bbox.Min.Y;
        if (dy <= Math.Max(1.0, dx * 0.02) && dx > dy)
            return Vector3d.XAxis;
        return Vector3d.YAxis;
    }

    private static void ApplyPaperDisplay(RhinoPageView page)
    {
        var mode = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.WireframeId);
        if (mode != null && page?.MainViewport != null)
            page.MainViewport.DisplayMode = mode;
    }

    /// <summary>
    /// Wireframe. Solid section hatches are annotations, and Wireframe draws
    /// them. Surfaces stay unshaded. The detail shows only S-DRAW, so clay
    /// does not appear. Object color on the hatch is black.
    /// </summary>
    private static void ApplyDetailDisplay(RhinoViewport viewport)
    {
        if (viewport == null) return;
        var mode = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.WireframeId);
        if (mode != null)
            viewport.DisplayMode = mode;
    }

    private static string PenLogSuffix()
    {
        EnsureForskPen();
        return string.IsNullOrEmpty(_penLog) ? "" : " | " + _penLog;
    }

    /// <summary>
    /// Kept from the Pen experiments. Sheet ink does not call this.
    /// Details show S-DRAW curves in Wireframe.
    /// </summary>
    private static DisplayModeDescription EnsureForskPen()
    {
        if (_penApplied == _penPass && _penModeCached != null)
        {
            var cached = DisplayModeDescription.GetDisplayMode(_penModeCached.Id);
            if (cached != null) return cached;
        }

        _penForceObjectBlack = false;
        _penReloadFailed = false;
        _penUsageZero = 0;
        _penSawEdgeUsage = false;
        _penKeysStarted = false;
        _penSourceId = DisplayModeDescription.PenId;

        var mode = ResolveForskPen(DisplayModeDescription.PenId, false);
        if (mode == null)
            throw new InvalidOperationException(PenReloadFailedMessage);
        var note = RepatchPenIni(mode, out mode);
        if (_penReloadFailed || mode == null)
            throw new InvalidOperationException(PenReloadFailedMessage);

        if (EdgeUsageFailed())
        {
            var source = FindModeId("Monochrome");
            var sourceName = "Monochrome";
            if (source == Guid.Empty)
            {
                source = FindModeId("Technical");
                sourceName = "Technical";
            }
            if (source == Guid.Empty)
                source = DisplayModeDescription.PenId;
            _penSourceId = source;
            mode = ResolveForskPen(source, true);
            if (mode == null)
                throw new InvalidOperationException(PenReloadFailedMessage);
            var second = RepatchPenIni(mode, out mode);
            note = sourceName + " " + note + " | " + second;
            if (_penReloadFailed || mode == null)
                throw new InvalidOperationException(PenReloadFailedMessage);
            // Technical still on object color would fill the plan. Stay on Pen
            // and let the object-black belt draw the lines.
            if (sourceName == "Technical" && EdgeUsageFailed())
            {
                _penSourceId = DisplayModeDescription.PenId;
                mode = ResolveForskPen(DisplayModeDescription.PenId, true);
                if (mode == null)
                    throw new InvalidOperationException(PenReloadFailedMessage);
                var third = RepatchPenIni(mode, out mode);
                note = note + " | pen-again " + third;
                if (_penReloadFailed || mode == null)
                    throw new InvalidOperationException(PenReloadFailedMessage);
            }
        }

        if (EdgeUsageFailed())
            _penForceObjectBlack = true;

        var live = DisplayModeDescription.GetDisplayMode(mode.Id);
        if (live == null || live.Id == Guid.Empty)
            throw new InvalidOperationException(PenReloadFailedMessage);

        _penLog = (live.EnglishName ?? ForskPenName) + " " + live.Id
            + " edge " + PenEdgePx + "px usage0 " + _penUsageZero
            + (_penForceObjectBlack ? " object-black" : "")
            + " " + note;
        LogPenLine(_penLog);
        _penModeCached = live;
        _penApplied = _penPass;
        return live;
    }

    private static bool EdgeUsageFailed()
    {
        return _penUsageZero > 0 || !_penSawEdgeUsage;
    }

    private static DisplayModeDescription _penModeCached;

    /// <summary>
    /// R7 RhinoCommon exposes surface-edge thickness and curve color, not
    /// silhouette color. The ini keys are the R7 display-mode record:
    /// surface edge usage 2 is "single color", 0 is the object color.
    /// </summary>
    private static string ApplyManagedPen(DisplayModeDescription mode)
    {
        var attr = mode?.DisplayAttributes;
        if (attr == null) return "";
        attr.ShowIsoCurves = false;
        attr.ShowTangentEdges = false;
        attr.ShowTangentSeams = false;
        attr.ShowSurfaceEdges = true;
        attr.ShowClippingPlanes = false;
        attr.SurfaceEdgeThickness = PenEdgePx;
        attr.CurveThickness = PenEdgePx;
        attr.UseSingleCurveColor = true;
        attr.CurveColor = Color.Black;
        attr.UseAssignedObjectMaterial = false;
        attr.UseCustomObjectMaterial = false;
        try { attr.SetFill(Color.White); }
        catch (Exception) { }
        var mesh = attr.MeshSpecificAttributes;
        if (mesh != null)
        {
            mesh.AllMeshWiresColor = Color.Black;
            mesh.MeshWireThickness = PenEdgePx;
        }
        var hit = new List<string>();
        if (TrySet(attr, "SurfaceEdgeColor", Color.Black)) hit.Add("SurfaceEdgeColor");
        if (TrySet(attr, "SurfaceEdgeColorUsage", 2)) hit.Add("SurfaceEdgeColorUsage=2");
        if (TrySet(attr, "SilhouetteLineColor", Color.Black)) hit.Add("SilhouetteLineColor");
        if (TrySet(attr, "SilhouetteColor", Color.Black)) hit.Add("SilhouetteColor");
        if (TrySet(attr, "EdgeLineColor", Color.Black)) hit.Add("EdgeLineColor");
        if (TrySet(attr, "ClippingEdgeColor", Color.Black)) hit.Add("ClippingEdgeColor");
        if (TrySet(attr, "ClippingEdgeThickness", PenEdgePx)) hit.Add("ClippingEdgeThickness");
        if (TrySet(attr, "ClippingEdgeColorUsage", SingleColorUsage())) hit.Add("ClippingEdgeColorUsage");
        try { DisplayModeDescription.UpdateDisplayMode(mode); }
        catch (Exception) { }
        return string.Join(",", hit.ToArray());
    }

    private static bool TrySet(object target, string name, object value)
    {
        if (target == null || value == null) return false;
        try
        {
            var prop = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (prop == null || !prop.CanWrite) return false;
            if (prop.PropertyType.IsEnum && value is int)
                value = Enum.ToObject(prop.PropertyType, (int)value);
            else if (prop.PropertyType != value.GetType() && !prop.PropertyType.IsInstanceOfType(value))
                return false;
            prop.SetValue(target, value, null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Managed props first (UpdateDisplayMode, no delete). Real ini keys are
    /// patched only when that export already contains them, then imported.
    /// A later UpdateDisplayMode would drop those keys, so it is not called again.
    /// </summary>
    private static string RepatchPenIni(DisplayModeDescription mode, out DisplayModeDescription live)
    {
        live = mode;
        var single = SingleColorUsage();
        string reflected = "";
        try
        {
            try { reflected = ApplyManagedPen(mode); }
            catch (Exception) { reflected = "attrs-failed"; }
            live = DisplayModeDescription.FindByName(ForskPenName) ?? mode;
            if (!ExportMode(live, PenIniPath))
            {
                _penUsageZero = 1;
                return "ini-export-failed";
            }

            var text = File.ReadAllText(PenIniPath);
            DumpPenKeys(text, false);
            int replaced;
            string changed;
            var patched = PatchPenIni(text, single, out replaced, out changed);
            File.WriteAllText(PenIniPath, patched);
            if (replaced > 0)
            {
                live = ImportPatchedMode(live, PenIniPath);
                if (_penReloadFailed || live == null)
                    return "ini-import-failed keys " + replaced;
            }

            if (!ExportMode(live, PenAfterPath))
            {
                _penUsageZero = 1;
                return "ini-after-failed keys " + replaced;
            }

            var after = File.ReadAllText(PenAfterPath);
            DumpPenKeys(after, true);
            _penSawEdgeUsage = HasEdgeColorUsageKey(after);
            _penUsageZero = CountEdgeUsageZero(after);
            var note = "ini-keys " + replaced + " usage0 " + _penUsageZero
                + " single " + single.ToString(CultureInfo.InvariantCulture);
            if (reflected.Length > 0) note += " reflected " + reflected;
            if (changed.Length > 0) note += " " + changed;
            return note;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            _penUsageZero = 1;
            return "ini-export-failed";
        }
    }

    private static DisplayModeDescription ImportPatchedMode(DisplayModeDescription current, string path)
    {
        var previousId = current?.Id ?? Guid.Empty;
        // The managed UpdateDisplayMode already ran. Ini keys are applied by
        // replacing the custom mode. The caller assigns FindByName, never previousId.
        if (previousId != Guid.Empty)
            DropCustomMode(previousId);
        var leftover = LiveForskPen();
        if (leftover != null)
            DropCustomMode(leftover.Id);

        var imported = TryImport(path);
        var named = LiveForskPen();
        if (named == null)
        {
            var source = _penSourceId == Guid.Empty ? DisplayModeDescription.PenId : _penSourceId;
            var copied = DisplayModeDescription.CopyDisplayMode(source, ForskPenName);
            if (copied != Guid.Empty)
                DisplayModeDescription.GetDisplayMode(copied);
            _penReloadFailed = true;
            return null;
        }
        if (imported != Guid.Empty && imported != named.Id)
            DropCustomMode(imported);
        return named;
    }

    private static Guid TryImport(string path)
    {
        try
        {
            return DisplayModeDescription.ImportFromFile(path);
        }
        catch (Exception)
        {
            return Guid.Empty;
        }
    }

    private static DisplayModeDescription LiveForskPen()
    {
        var named = DisplayModeDescription.FindByName(ForskPenName);
        if (named == null || named.Id == Guid.Empty || named.Id == DisplayModeDescription.PenId)
            return null;
        return DisplayModeDescription.GetDisplayMode(named.Id);
    }

    private static void DropCustomMode(Guid id)
    {
        if (id == Guid.Empty || id == DisplayModeDescription.PenId) return;
        var mode = DisplayModeDescription.GetDisplayMode(id);
        if (mode == null) return;
        var name = mode.EnglishName ?? "";
        if (!name.Equals(ForskPenName, StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith(ForskPenName, StringComparison.OrdinalIgnoreCase))
            return;
        try { DisplayModeDescription.DeleteDisplayMode(id); }
        catch (Exception) { }
    }

    private static DisplayModeDescription ResolveForskPen(Guid sourceId, bool replace)
    {
        if (sourceId == Guid.Empty)
            sourceId = DisplayModeDescription.PenId;
        _penSourceId = sourceId;
        var existing = LiveForskPen();
        if (replace && existing != null)
        {
            DropCustomMode(existing.Id);
            existing = null;
        }
        if (existing != null)
            return existing;
        var id = DisplayModeDescription.CopyDisplayMode(sourceId, ForskPenName);
        if (id == Guid.Empty)
        {
            _penReloadFailed = true;
            return null;
        }
        var mode = DisplayModeDescription.GetDisplayMode(id) ?? LiveForskPen();
        if (mode == null || mode.Id == DisplayModeDescription.PenId)
        {
            _penReloadFailed = true;
            return null;
        }
        return mode;
    }

    private static Guid FindModeId(string englishName)
    {
        var named = DisplayModeDescription.FindByName(englishName);
        if (named != null && named.Id != Guid.Empty)
            return named.Id;
        var all = DisplayModeDescription.GetDisplayModes();
        if (all == null) return Guid.Empty;
        foreach (var candidate in all)
        {
            if (candidate?.EnglishName != null &&
                candidate.EnglishName.Equals(englishName, StringComparison.OrdinalIgnoreCase))
                return candidate.Id;
        }
        return Guid.Empty;
    }

    private static bool ExportMode(DisplayModeDescription mode, string path)
    {
        if (mode == null || string.IsNullOrEmpty(path)) return false;
        var exported = DisplayModeDescription.ExportToFile(mode, path);
        if (exported is bool ok && !ok) return false;
        return File.Exists(path);
    }

    private static int SingleColorUsage()
    {
        if (_singleColorLearned) return _singleColorUsage;
        _singleColorLearned = true;
        _singleColorUsage = 2;
        var pen = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.PenId);
        if (!ExportMode(pen, PenStockPath)) return _singleColorUsage;
        string text;
        try { text = File.ReadAllText(PenStockPath); }
        catch (Exception) { return _singleColorUsage; }
        var learned = LearnSingleColor(text);
        if (learned.HasValue && learned.Value != 0)
            _singleColorUsage = learned.Value;
        return _singleColorUsage;
    }

    /// <summary>
    /// Stock Pen silhouettes are already black. A non-zero usage on those
    /// keys is the "single color" enum. Surface edges stay 0 (object color).
    /// </summary>
    private static int? LearnSingleColor(string text)
    {
        int? learned = null;
        foreach (var line in IniLines(text))
        {
            string key, value;
            if (!TryIniKey(line, out key, out value)) continue;
            if (!IsTechnicalColorUsage(key)) continue;
            int number;
            if (!TryIniInt(value, out number) || number == 0) continue;
            learned = number;
        }
        return learned;
    }

    private static string PatchPenIni(string text, int singleColor, out int replaced, out string changed)
    {
        replaced = 0;
        var notes = new List<string>();
        var lines = IniLines(text);
        var singleText = singleColor.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < lines.Length; i++)
        {
            string key, value;
            if (!TryIniKey(lines[i], out key, out value)) continue;
            string next = null;
            int ignored;
            if (IsColorUsageKey(key) && TryIniInt(value, out ignored))
                next = singleText;
            else if (IsLineColorKey(key) && LooksLikeRgb(value))
                next = BlackColorLike(value);
            else if (IsLineThicknessKey(key) && TryIniInt(value, out ignored))
                next = PenEdgePx.ToString(CultureInfo.InvariantCulture);
            if (next == null || string.Equals(value, next, StringComparison.Ordinal)) continue;
            lines[i] = key + "=" + next;
            replaced++;
            if (notes.Count < 12)
                notes.Add(key + " " + value + "->" + next);
        }
        changed = notes.Count == 0 ? "" : string.Join(", ", notes.ToArray());
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }

    private static void DumpPenKeys(string text, bool after)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(after ? "--- after ---\n" : "--- before ---\n");
            foreach (var line in IniLines(text))
            {
                if (!IsPenDiagLine(line)) continue;
                sb.Append(line).Append('\n');
            }
            if (_penKeysStarted)
                File.AppendAllText(PenKeysPath, sb.ToString());
            else
                File.WriteAllText(PenKeysPath, sb.ToString());
            _penKeysStarted = true;
        }
        catch (Exception)
        {
        }
    }

    private static bool IsPenDiagLine(string line)
    {
        var lower = (line ?? "").ToLowerInvariant();
        return lower.Contains("color") || lower.Contains("thick") || lower.Contains("edge")
            || lower.Contains("usage") || lower.Contains("silhou") || lower.Contains("curve")
            || lower.Contains("wire") || lower.Contains("fill");
    }

    private static int CountEdgeUsageZero(string text)
    {
        int count = 0;
        foreach (var line in IniLines(text))
        {
            string key, value;
            if (!TryIniKey(line, out key, out value)) continue;
            if (!IsEdgeColorUsageKey(key)) continue;
            int number;
            if (TryIniInt(value, out number) && number == 0)
                count++;
        }
        return count;
    }

    private static bool HasEdgeColorUsageKey(string text)
    {
        foreach (var line in IniLines(text))
        {
            string key, value;
            if (!TryIniKey(line, out key, out value)) continue;
            if (IsEdgeColorUsageKey(key)) return true;
        }
        return false;
    }

    private static bool IsEdgeColorUsageKey(string key)
    {
        var k = (key ?? "").ToLowerInvariant();
        if (k.Contains("surface") && k.Contains("edge") && k.Contains("usage"))
            return true;
        return k.Contains("edge") && k.Contains("color") && k.Contains("usage");
    }

    private static bool IsColorUsageKey(string key)
    {
        var k = (key ?? "").ToLowerInvariant();
        if (k.Contains("mask") || k.Contains("override") || k.Contains("pattern"))
            return false;
        var usage = k.Contains("colorusage") || k.Contains("usageindex")
            || (k.Contains("usage") && k.Contains("color"));
        if (!usage) return false;
        return MentionsLineFamily(k);
    }

    private static bool IsTechnicalColorUsage(string key)
    {
        if (!IsColorUsageKey(key)) return false;
        var k = key.ToLowerInvariant();
        return k.Contains("silhou") || k.Contains("tech") || k.Contains("curve") || k.Contains("tsi");
    }

    private static bool IsLineColorKey(string key)
    {
        var k = (key ?? "").ToLowerInvariant();
        if (!k.Contains("color") || k.Contains("usage")) return false;
        if (k.Contains("reduction") || k.Contains("back") || k.Contains("ambient")
            || k.Contains("shadow") || k.Contains("lock") || k.Contains("grid")
            || k.Contains("material") || k.Contains("diffuse") || k.Contains("specul")
            || k.Contains("emiss") || k.Contains("reflect") || k.Contains("transparent")
            || k.Contains("wallpaper") || k.Contains("fill"))
            return false;
        if (k.Contains("surface") && !k.Contains("edge")) return false;
        if (k.Contains("edge") || k.Contains("naked") || k.Contains("silhou")
            || k.Contains("curve") || k.Contains("clip") || k.Contains("tech"))
            return true;
        return k == "thcolor" || k == "tecolor" || k == "tsicolor"
            || k == "tccolor" || k == "tscolor" || k == "ticolor";
    }

    private static bool IsLineThicknessKey(string key)
    {
        var k = (key ?? "").ToLowerInvariant();
        if (!k.Contains("thickness")) return false;
        if (k.Contains("shadow")) return false;
        if (k.Contains("edge") || k.Contains("naked") || k.Contains("silhou")
            || k.Contains("curve") || k.Contains("clip") || k.Contains("tech")
            || k.Contains("surface") || k.Contains("wire"))
            return true;
        return k == "ththickness" || k == "tethickness" || k == "tsithickness"
            || k == "tcthickness" || k == "tsthickness" || k == "tithickness";
    }

    private static bool MentionsLineFamily(string k)
    {
        return k.Contains("edge") || k.Contains("naked") || k.Contains("silhou")
            || k.Contains("clip") || k.Contains("curve") || k.Contains("tech")
            || k.Contains("surface");
    }

    private static string BlackColorLike(string existing)
    {
        var parts = (existing ?? "").Split(',');
        if (parts.Length >= 4) return "0,0,0,255";
        return "0,0,0";
    }

    private static bool LooksLikeRgb(string value)
    {
        var parts = (value ?? "").Split(',');
        if (parts.Length < 3 || parts.Length > 4) return false;
        foreach (var part in parts)
        {
            int number;
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return false;
        }
        return true;
    }

    private static bool TryIniInt(string value, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        var dot = text.IndexOf('.');
        if (dot >= 0) text = text.Substring(0, dot);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    private static bool TryIniKey(string line, out string key, out string value)
    {
        key = null;
        value = null;
        if (string.IsNullOrEmpty(line)) return false;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '[' || trimmed[0] == ';' || trimmed[0] == '#')
            return false;
        var eq = trimmed.IndexOf('=');
        if (eq <= 0) return false;
        key = trimmed.Substring(0, eq).Trim();
        value = trimmed.Substring(eq + 1).Trim();
        return key.Length > 0;
    }

    private static string[] IniLines(string text)
    {
        return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    private static void LogPenLine(string detail) => AppendPrintLog("pen " + detail);

    private static void AppendPrintLog(string text)
    {
        try
        {
            var line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                + " " + text + "\n";
            File.AppendAllText(PrintLogPath, line);
        }
        catch (Exception)
        {
        }
    }

    private static void LogPenWalls(RhinoDoc doc, List<RhinoObject> clay, Guid detailId)
    {
        if (doc == null || _penSamplePass == _penPass) return;
        _penSamplePass = _penPass;
        var parts = new List<string>();
        if (clay != null)
        {
            foreach (var obj in clay)
            {
                if (parts.Count >= 5 || obj?.Attributes == null) continue;
                var kind = GetForskKind(obj) ?? "";
                if (!kind.Equals("wall", StringComparison.OrdinalIgnoreCase)) continue;
                var layer = obj.Attributes.LayerIndex >= 0 ? doc.Layers[obj.Attributes.LayerIndex] : null;
                var layerRgb = layer == null ? "?" : Rgb(layer.Color);
                var perView = "?";
                if (layer != null && detailId != Guid.Empty)
                {
                    try { perView = Rgb(layer.PerViewportColor(detailId)); }
                    catch (Exception) { perView = "?"; }
                }
                parts.Add("cs=" + obj.Attributes.ColorSource
                    + " obj=" + Rgb(obj.Attributes.ObjectColor)
                    + " layer=" + layerRgb
                    + " pvc=" + perView);
            }
        }
        LogPenLine("walls " + parts.Count + " " + string.Join("; ", parts.ToArray()));
    }

    private static void PaintClayForPreview(RhinoDoc doc, List<RhinoObject> clay, bool forceObjectBlack)
    {
        if (doc == null || clay == null) return;
        foreach (var obj in clay)
        {
            var attrs = obj?.Attributes;
            if (attrs == null) continue;
            if (forceObjectBlack)
            {
                RememberPrintColor(attrs);
                attrs.ColorSource = ObjectColorSource.ColorFromObject;
                attrs.ObjectColor = Color.Black;
                obj.CommitChanges();
            }
            else if (attrs.ColorSource != ObjectColorSource.ColorFromLayer)
            {
                RememberPrintColor(attrs);
                attrs.ColorSource = ObjectColorSource.ColorFromLayer;
                obj.CommitChanges();
            }
        }
    }

    private static void RememberPrintColor(ObjectAttributes attrs)
    {
        if (attrs == null) return;
        if (!string.IsNullOrEmpty(attrs.GetUserString(PrintColorSourceKey))) return;
        attrs.SetUserString(PrintColorSourceKey, attrs.ColorSource.ToString());
        attrs.SetUserString(PrintRgbKey, Rgb(attrs.ObjectColor));
    }

    private static void RestorePrintColors(RhinoDoc doc)
    {
        if (doc == null) return;
        foreach (var obj in doc.Objects)
        {
            var attrs = obj?.Attributes;
            if (attrs == null) continue;
            var stored = attrs.GetUserString(PrintColorSourceKey);
            if (string.IsNullOrEmpty(stored)) continue;
            ObjectColorSource source;
            if (Enum.TryParse(stored, true, out source))
                attrs.ColorSource = source;
            var rgb = attrs.GetUserString(PrintRgbKey) ?? "";
            var parts = rgb.Split(',');
            int r, g, b;
            if (parts.Length >= 3
                && int.TryParse(parts[0].Trim(), out r)
                && int.TryParse(parts[1].Trim(), out g)
                && int.TryParse(parts[2].Trim(), out b))
            {
                attrs.ObjectColor = Color.FromArgb(ClampByte(r), ClampByte(g), ClampByte(b));
            }
            attrs.DeleteUserString(PrintColorSourceKey);
            attrs.DeleteUserString(PrintRgbKey);
            obj.CommitChanges();
        }
    }

    private static int ClampByte(int value)
    {
        if (value < 0) return 0;
        if (value > 255) return 255;
        return value;
    }

    private static string Rgb(Color color)
    {
        return color.R.ToString(CultureInfo.InvariantCulture) + ","
            + color.G.ToString(CultureInfo.InvariantCulture) + ","
            + color.B.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Top of the floor solids. Walls start at 0 when no floor is baked.</summary>
    private static double FloorTopZ(List<RhinoObject> clay)
    {
        double? top = null;
        if (clay == null) return 0;
        foreach (var obj in clay)
        {
            var kind = GetForskKind(obj) ?? "";
            if (!kind.Equals("floor", StringComparison.OrdinalIgnoreCase)) continue;
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            if (!top.HasValue || box.Max.Z > top.Value)
                top = box.Max.Z;
        }
        return top ?? 0;
    }

    /// <summary>
    /// Move the locked Top view onto the drawing. ZoomBoundingBox would change the scale.
    /// </summary>
    private static void PanDetailOntoClay(DetailViewObject detail, BoundingBox bbox)
    {
        var vp = detail?.Viewport;
        if (vp == null || !bbox.IsValid) return;
        vp.ChangeToParallelProjection(true);
        vp.CameraUp = DrawingCameraUp(bbox);
        vp.SetCameraTarget(bbox.Center, true);
        vp.SetCameraDirection(-Vector3d.ZAxis, false);
    }

    private static bool LockDetailScale(DetailViewObject detail, int scale)
    {
        var geom = detail?.DetailGeometry;
        if (geom == null || scale <= 0) return false;
        var locked = geom.SetScale(scale, UnitSystem.Millimeters, 1.0, UnitSystem.Millimeters);
        geom.IsProjectionLocked = locked;
        return locked;
    }

    private static DetailViewObject ReloadDetail(RhinoPageView page, DetailViewObject detail)
    {
        if (page == null) return detail;
        var all = page.GetDetailViews();
        if (all == null || all.Length == 0) return detail;
        var id = detail?.Id ?? Guid.Empty;
        if (id == Guid.Empty) return all[0];
        foreach (var item in all)
        {
            if (item != null && item.Id == id) return item;
        }
        return all[0];
    }

    /// <summary>
    /// The page is active, not the nested detail. FilePdf skips clay while a detail is active.
    /// </summary>
    private static void LeaveDetail(RhinoPageView page)
    {
        if (page == null) return;
        try
        {
            var details = page.GetDetailViews();
            if (details != null)
            {
                foreach (var detail in details)
                {
                    if (detail != null && detail.IsActive)
                        detail.IsActive = false;
                }
            }
        }
        catch (Exception)
        {
            // SetPageAsActive still exits the nested detail.
        }
        page.SetPageAsActive();
    }

    private static bool DetailSeesClay(RhinoViewport viewport, BoundingBox clay)
    {
        if (viewport == null || !clay.IsValid) return false;
        var frustum = viewport.GetFrustumBoundingBox();
        if (frustum.IsValid && BoxesIntersect(frustum, clay))
            return true;
        if (viewport.IsVisible(clay.Center)) return true;
        foreach (var corner in clay.GetCorners())
        {
            if (viewport.IsVisible(corner)) return true;
        }
        return false;
    }

    private static bool BoxesIntersect(BoundingBox a, BoundingBox b)
    {
        if (!a.IsValid || !b.IsValid) return false;
        return a.Max.X >= b.Min.X && a.Min.X <= b.Max.X
            && a.Max.Y >= b.Min.Y && a.Min.Y <= b.Max.Y
            && a.Max.Z >= b.Min.Z && a.Min.Z <= b.Max.Z;
    }

    private static bool ForskPagesShowDrawing(RhinoDoc doc, List<RhinoPageView> pages)
    {
        if (doc == null || pages == null || pages.Count == 0) return false;
        foreach (var page in pages)
        {
            var view = ViewKeyForPage(page);
            if (string.IsNullOrEmpty(view)) return false;
            // The schedules page has no detail: it shows its tables.
            if (view == SchedulesView)
            {
                if (ScheduleObjects(doc, page).Count == 0) return false;
                continue;
            }
            var bbox = PrintDrawingBounds(doc, view);
            if (!bbox.IsValid) return false;
            var details = page?.GetDetailViews();
            if (details == null || details.Length == 0) return false;
            var seen = false;
            foreach (var detail in details)
            {
                if (detail?.Viewport != null && DetailSeesClay(detail.Viewport, bbox))
                    seen = true;
            }
            if (!seen) return false;
        }
        return true;
    }

    /// <summary>
    /// The detail shows one S-DRAW child. Clay, source labels, and the other
    /// drawing views stay off. The parent has to be on in that viewport or
    /// Rhino hides the child.
    /// </summary>
    private static void SetDetailDrawingVisibility(RhinoDoc doc, Guid viewportId, Layer drawLayer)
    {
        if (doc == null || viewportId == Guid.Empty || drawLayer == null) return;
        Layer parent = null;
        if (drawLayer.ParentLayerId != Guid.Empty)
            parent = doc.Layers.FindId(drawLayer.ParentLayerId);
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer == null || layer.IsDeleted) continue;
            var show = layer.Index == drawLayer.Index
                || (parent != null && layer.Index == parent.Index);
            layer.SetPerViewportVisible(viewportId, show);
            if (show)
            {
                // GetPreviewImage paints this display color. Plot color does
                // not affect that bitmap. Black keeps the solid hatch inked.
                layer.SetPerViewportColor(viewportId, Color.Black);
                layer.SetPerViewportPlotColor(viewportId, Color.Black);
            }
            doc.Layers.Modify(layer, layer.Index, true);
        }
    }

    /// <summary>
    /// The detail shows clay only. Source layers such as label text fill the sheet.
    /// Opening markers stay off. Walls, floor, and roof stay visible and printable
    /// even when the bake layer is not the A-WALL / A-FLOR / A-ROOF name.
    /// </summary>
    private static void SetDetailLayerVisibility(
        RhinoDoc doc, Guid viewportId, bool includeExisting, List<RhinoObject> clay)
    {
        if (viewportId == Guid.Empty) return;
        var clayLayers = ClayLayerIndexes(doc, clay, includeExisting);
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer == null || layer.IsDeleted) continue;
            var show = ShowsInClayDetail(layer, includeExisting, clayLayers);
            layer.SetPerViewportVisible(viewportId, show);
            if (show)
            {
                // GetPreviewImage reads this display color. Plot color is for a
                // vector print such as ExportAll and does not affect that bitmap.
                layer.SetPerViewportColor(viewportId, Color.Black);
                layer.SetPerViewportPlotColor(viewportId, Color.Black);
                // PlotWeight -1 is "do not print". 0.18 mm is the vector pen.
                layer.SetPerViewportPlotWeight(viewportId, 0.18);
                if (layer.PlotWeight < 0)
                    layer.PlotWeight = 0;
            }
            doc.Layers.Modify(layer, layer.Index, true);
        }
    }

    private static HashSet<int> ClayLayerIndexes(RhinoDoc doc, List<RhinoObject> clay, bool includeExisting)
    {
        var indexes = new HashSet<int>();
        if (clay == null) return indexes;
        foreach (var obj in clay)
        {
            if (obj?.Attributes == null) continue;
            var kind = GetForskKind(obj) ?? "";
            if (kind.Equals("opening_marker", StringComparison.OrdinalIgnoreCase)) continue;
            if (kind.Equals("room", StringComparison.OrdinalIgnoreCase)) continue;
            if (kind.Equals("drawing", StringComparison.OrdinalIgnoreCase)) continue;
            if (kind.Equals("layout", StringComparison.OrdinalIgnoreCase)) continue;
            if (!includeExisting && IsExistingUnderlay(doc, obj)) continue;
            var index = obj.Attributes.LayerIndex;
            if (index < 0) continue;
            var layer = doc.Layers[index];
            if (layer == null || layer.IsDeleted) continue;
            if (layer.Name != null &&
                layer.Name.Equals("A-OPEN", StringComparison.OrdinalIgnoreCase))
                continue;
            indexes.Add(index);
        }
        return indexes;
    }

    private static bool ShowsInClayDetail(Layer layer, bool includeExisting, HashSet<int> clayLayers)
    {
        if (layer?.Name != null &&
            layer.Name.Equals("A-OPEN", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsClayDetailLayer(layer?.Name, includeExisting)) return true;
        return clayLayers != null && clayLayers.Contains(layer.Index);
    }

    private static bool IsClayDetailLayer(string name, bool includeExisting)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var clay in LayoutShowLayerNames)
        {
            if (name.Equals(clay, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return includeExisting &&
               name.Equals("X-EXIST", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One band along the bottom margin, left to right: north arrow (plan
    /// only), scale bar, title block. Fills <paramref name="footer"/> with
    /// the measured boxes in paper mm.
    /// </summary>
    private JArray AddSheetFooter(
        RhinoDoc doc, RhinoPageView page, LayoutViewSpec spec, string stableId, int pageScale,
        string viewTitle, bool northArrow, JObject footer)
    {
        var ids = new JArray();
        var layer = EnsureLayer(doc, "A-ANNO", Color.FromArgb(200, 160, 40));
        var pageId = page.MainViewport.Id;
        var bandMid = LayoutMarginMm + FooterBandMm / 2.0;

        ObjectAttributes Attr(string role)
        {
            var attr = LayoutAttr(layer.Index, pageId, spec.View, stableId);
            attr.SetUserString("forsk:role", role);
            return attr;
        }
        Guid Line(double x0, double y0, double x1, double y1, ObjectAttributes attr)
        {
            return AddPaperLine(doc, ids, x0, y0, x1, y1, attr);
        }
        Guid Rect(double x0, double y0, double x1, double y1, ObjectAttributes attr)
        {
            return AddPaperRect(doc, ids, x0, y0, x1, y1, attr);
        }
        Guid Text(string text, double x, double y, double height, TextHorizontalAlignment align, ObjectAttributes attr)
        {
            return AddPaperText(doc, ids, text, x, y, height, align, TextVerticalAlignment.Bottom, attr);
        }

        // Title block: flush right, full band height, one cell per field.
        var tx0 = A3WidthMm - LayoutMarginMm - TitleBlockWidthMm;
        var tx1 = A3WidthMm - LayoutMarginMm;
        var ty0 = LayoutMarginMm;
        var ty1 = LayoutMarginMm + FooterBandMm;
        var meta = ProjectMetaRecord(doc);
        var cells = OpeningTypes.TitleCells(new[]
        {
            new KeyValuePair<string, string>("Drawing", viewTitle),
            new KeyValuePair<string, string>("Scale", pageScale > 0
                ? "1:" + pageScale.ToString(CultureInfo.InvariantCulture)
                : ""),
            new KeyValuePair<string, string>("Sheet", "A3"),
            new KeyValuePair<string, string>("Date", meta["date"]?.ToString()),
            new KeyValuePair<string, string>("Project", meta["project"]?.ToString()),
            new KeyValuePair<string, string>("Client", meta["client"]?.ToString()),
            new KeyValuePair<string, string>("Address", meta["address"]?.ToString())
        });
        var widths = OpeningTypes.TitleCellWidths(cells, TitleBlockWidthMm);
        var titleStart = ids.Count;
        var frameId = Rect(tx0, ty0, tx1, ty1, Attr("title_block"));
        var cellRows = new JArray();
        var cx = tx0;
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
                Line(cx, ty0, cx, ty1, Attr("title_block"));
            var captionAttr = Attr("title_cell");
            captionAttr.SetUserString("forsk:cell", cells[i].Key.ToLowerInvariant());
            Text(cells[i].Key, cx + 2.0, ty1 - 2.0 - 1.8, 1.8, TextHorizontalAlignment.Left, captionAttr);
            var valueAttr = Attr("title_cell");
            valueAttr.SetUserString("forsk:cell", cells[i].Key.ToLowerInvariant());
            var valueHeight = i == 0 ? 3.5 : FooterTextMm;
            Text(cells[i].Value, cx + 2.0, ty0 + 4.0, valueHeight, TextHorizontalAlignment.Left, valueAttr);
            cellRows.Add(new JObject { ["name"] = cells[i].Key.ToLowerInvariant(), ["text"] = cells[i].Value });
            cx += widths[i];
        }
        var titleBox = PaperBox(doc, new[] { frameId });
        titleBox["cells"] = cellRows;
        var titleIds = new List<Guid>();
        for (var i = titleStart; i < ids.Count; i++)
            titleIds.Add(Guid.Parse(ids[i].ToString()));
        // Frame, dividers, and every cell's text: all of it must stay in the band.
        titleBox["all"] = PaperBox(doc, titleIds);
        footer["title_block"] = titleBox;

        // North arrow: far left of the band, arrow plus letter centred on it,
        // NorthArrowMm tall in paper mm whatever the plan scale.
        if (northArrow)
        {
            const double head = 3.2;
            const double letterGap = 1.0;
            var shaft = NorthArrowMm - letterGap - FooterTextMm;
            var ax = LayoutMarginMm + head + 1.0;
            var ay = bandMid - NorthArrowMm / 2.0;
            var top = ay + shaft;
            var northIds = new List<Guid>
            {
                Line(ax, ay, ax, top, Attr("north")),
                Line(ax, top, ax - head, top - head, Attr("north")),
                Line(ax, top, ax + head, top - head, Attr("north")),
                Text("N", ax, top + letterGap, FooterTextMm, TextHorizontalAlignment.Center, Attr("north"))
            };
            footer["north_arrow"] = PaperBox(doc, northIds);
        }

        // Scale bar: between the arrow and the title block, at the page scale.
        if (pageScale > 0)
        {
            var meters = OpeningTypes.ScaleBarMeters(pageScale);
            var length = OpeningTypes.ScaleBarPaperMm(meters, pageScale);
            var segments = OpeningTypes.ScaleBarSegments(meters);
            var by0 = bandMid - (ScaleBarHeightMm + 1.0 + FooterTextMm) / 2.0;
            var by1 = by0 + ScaleBarHeightMm;
            var barIds = new List<Guid>();
            var solid = SolidPatternIndex(doc);
            for (var i = 0; i < segments; i++)
            {
                var sx0 = ScaleBarLeftMm + length * i / segments;
                var sx1 = ScaleBarLeftMm + length * (i + 1) / segments;
                barIds.Add(Rect(sx0, by0, sx1, by1, Attr("scale_bar")));
                if (i % 2 == 0 && solid >= 0)
                    barIds.Add(AddSolid(doc, ids, sx0, by0, sx1, by1, solid, Attr("scale_bar")));
            }
            var labelIds = new List<Guid>(barIds)
            {
                Text("0", ScaleBarLeftMm, by1 + 1.0, FooterTextMm, TextHorizontalAlignment.Center, Attr("scale_bar_label")),
                Text(OpeningTypes.ScaleBarLabel(meters), ScaleBarLeftMm + length, by1 + 1.0, FooterTextMm,
                    TextHorizontalAlignment.Center, Attr("scale_bar_label"))
            };
            var bar = PaperBox(doc, barIds);
            bar["meters"] = meters;
            bar["segments"] = segments;
            bar["with_labels"] = PaperBox(doc, labelIds);
            footer["scale_bar"] = bar;
        }

        footer["free_labels"] = FreePageText(doc, page.MainViewport);
        return ids;
    }

    private static Guid Keep(JArray ids, Guid id)
    {
        if (id != Guid.Empty)
            ids.Add(id.ToString());
        return id;
    }

    private static Guid AddPaperLine(
        RhinoDoc doc, JArray ids, double x0, double y0, double x1, double y1, ObjectAttributes attr)
    {
        using (var line = new LineCurve(
            new Point3d(MmToPage(doc, x0), MmToPage(doc, y0), 0),
            new Point3d(MmToPage(doc, x1), MmToPage(doc, y1), 0)))
            return Keep(ids, doc.Objects.AddCurve(line, attr));
    }

    private static Guid AddPaperRect(
        RhinoDoc doc, JArray ids, double x0, double y0, double x1, double y1, ObjectAttributes attr)
    {
        using (var curve = PageRect(doc, x0, y0, x1, y1))
            return Keep(ids, doc.Objects.AddCurve(curve, attr));
    }

    /// <summary>Paper text: its own 1:1 style, never the document style or the plan scale.</summary>
    private static Guid AddPaperText(
        RhinoDoc doc, JArray ids, string text, double x, double y, double height,
        TextHorizontalAlignment align, TextVerticalAlignment valign, ObjectAttributes attr)
    {
        var style = OneToOneTextStyle(
            doc, "Forsk paper " + height.ToString("0.0", CultureInfo.InvariantCulture), MmToPage(doc, height));
        if (style == null) return Guid.Empty;
        var plane = Plane.WorldXY;
        plane.Origin = new Point3d(MmToPage(doc, x), MmToPage(doc, y), 0);
        using (var entity = TextEntity.Create(text, plane, style, false, 0, 0))
        {
            if (entity == null) return Guid.Empty;
            entity.TextHorizontalAlignment = align;
            entity.TextVerticalAlignment = valign;
            return Keep(ids, doc.Objects.AddText(entity, attr));
        }
    }

    private static Curve PageRect(RhinoDoc doc, double x0, double y0, double x1, double y1)
    {
        var rect = new Polyline
        {
            new Point3d(MmToPage(doc, x0), MmToPage(doc, y0), 0),
            new Point3d(MmToPage(doc, x1), MmToPage(doc, y0), 0),
            new Point3d(MmToPage(doc, x1), MmToPage(doc, y1), 0),
            new Point3d(MmToPage(doc, x0), MmToPage(doc, y1), 0),
            new Point3d(MmToPage(doc, x0), MmToPage(doc, y0), 0)
        };
        return rect.ToNurbsCurve();
    }

    private static Guid AddSolid(
        RhinoDoc doc, JArray ids, double x0, double y0, double x1, double y1, int pattern, ObjectAttributes attr)
    {
        using (var boundary = PageRect(doc, x0, y0, x1, y1))
        {
            Hatch[] hatches;
            try { hatches = Hatch.Create(boundary, pattern, 0.0, 1.0, doc.ModelAbsoluteTolerance); }
            catch (Exception) { return Guid.Empty; }
            if (hatches == null || hatches.Length == 0 || hatches[0] == null) return Guid.Empty;
            return Keep(ids, doc.Objects.AddHatch(hatches[0], attr));
        }
    }

    /// <summary>Union box of the objects Rhino stored, in paper mm.</summary>
    private static JObject PaperBox(RhinoDoc doc, IEnumerable<Guid> ids)
    {
        var box = BoundingBox.Empty;
        foreach (var id in ids)
        {
            var geom = id == Guid.Empty ? null : doc.Objects.FindId(id)?.Geometry;
            if (geom != null)
                box.Union(geom.GetBoundingBox(true));
        }
        var mm = MmToPage(doc, 1.0);
        if (!box.IsValid || mm <= 0) return new JObject();
        return new JObject
        {
            ["x0"] = Math.Round(box.Min.X / mm, 2),
            ["y0"] = Math.Round(box.Min.Y / mm, 2),
            ["x1"] = Math.Round(box.Max.X / mm, 2),
            ["y1"] = Math.Round(box.Max.Y / mm, 2),
            ["w"] = Math.Round((box.Max.X - box.Min.X) / mm, 2),
            ["h"] = Math.Round((box.Max.Y - box.Min.Y) / mm, 2)
        };
    }

    /// <summary>Text on the page that is not part of the footer or a schedule.</summary>
    private static int FreePageText(RhinoDoc doc, RhinoViewport pageViewport)
    {
        var settings = new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = true,
            ViewportFilter = pageViewport,
            ObjectTypeFilter = ObjectType.Annotation
        };
        var count = 0;
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (!(obj?.Geometry is TextEntity)) continue;
            var role = obj.Attributes?.GetUserString("forsk:role") ?? "";
            if (role == "title_cell" || role == "scale_bar_label" || role == "north") continue;
            if (role.StartsWith("schedule", StringComparison.Ordinal)) continue;
            count++;
        }
        return count;
    }

    private static int WallLevel(RhinoDoc doc)
    {
        if (doc == null) return 0;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase))
                continue;
            var raw = obj.Attributes?.GetUserString("forsk:level");
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
                return level;
        }
        return 0;
    }

    private static int DetailModelScale(RhinoPageView page)
    {
        var details = page?.GetDetailViews();
        if (details == null) return 0;
        foreach (var detail in details)
        {
            var ratio = detail?.DetailGeometry?.PageToModelRatio ?? 0;
            if (ratio > 1e-6 && ratio < 1)
            {
                var scale = (int)Math.Round(1.0 / ratio);
                if (scale >= 1) return scale;
            }
            if (ratio >= 1)
            {
                var scale = (int)Math.Round(ratio);
                if (scale >= 1) return scale;
            }
        }
        return 0;
    }

    private static ObjectAttributes LayoutAttr(int layerIndex, Guid pageViewportId, string view, string stableId)
    {
        var attr = new ObjectAttributes
        {
            LayerIndex = layerIndex,
            Name = stableId,
            Space = ActiveSpace.PageSpace,
            ViewportId = pageViewportId,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = Color.Black,
            PlotColorSource = ObjectPlotColorSource.PlotColorFromObject,
            PlotColor = Color.Black
        };
        StampForskTags(attr, new ForskStamp
        {
            Kind = "layout",
            Level = "0",
            Id = stableId,
            View = view
        });
        return attr;
    }

    private static double MmToPage(RhinoDoc doc, double mm)
    {
        var pageUnits = doc.PageUnitSystem;
        if (pageUnits == UnitSystem.None || pageUnits == UnitSystem.Unset)
            return mm;
        return mm * RhinoMath.UnitScale(UnitSystem.Millimeters, pageUnits);
    }

    private static List<RhinoPageView> MatchingForskPages(RhinoDoc doc, string layout)
    {
        var pages = new List<RhinoPageView>();
        var all = doc.Views.GetPageViews();
        if (all == null) return pages;
        foreach (var page in all)
        {
            if (!IsForskLayoutPage(page)) continue;
            if (!string.IsNullOrWhiteSpace(layout) && !LayoutNameMatches(page, layout))
                continue;
            pages.Add(page);
        }
        return pages;
    }

    private static bool IsForskLayoutPage(RhinoPageView page)
    {
        var name = page?.PageName;
        return !string.IsNullOrEmpty(name) &&
               name.StartsWith(LayoutPagePrefix, StringComparison.Ordinal);
    }

    private static bool LayoutNameMatches(RhinoPageView page, string layout)
    {
        if (page?.PageName != null &&
            page.PageName.Equals(layout.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        if (layout.Trim().Equals(SchedulesView, StringComparison.OrdinalIgnoreCase))
            return IsSchedulesPage(page);
        if (!TryGetLayoutView(layout, out var spec)) return false;
        return page != null &&
               string.Equals(page.PageName, spec.PageName, StringComparison.OrdinalIgnoreCase);
    }

    private JObject RemoveLayoutPages(RhinoDoc doc, string view, bool dryRun)
    {
        return RemoveLayoutPages(
            doc,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { view },
            dryRun);
    }

    private JObject RemoveLayoutPages(RhinoDoc doc, HashSet<string> viewSet, bool dryRun)
    {
        var pageNames = new JArray();
        var pages = new List<RhinoPageView>();
        var all = doc.Views.GetPageViews();
        if (all != null)
        {
            foreach (var page in all)
            {
                if (!IsForskLayoutPage(page)) continue;
                if (viewSet != null && !PageMatchesViewSet(page, viewSet)) continue;
                pages.Add(page);
                pageNames.Add(page.PageName);
            }
        }

        var objectIds = new JArray();
        var objectGuids = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj == null) continue;
            var role = obj.Attributes.GetUserString("forsk:role") ?? "";
            var isCut = role.Equals(PlanCutRole, StringComparison.OrdinalIgnoreCase);
            var isDrawing = IsPrintDrawing(doc, obj);
            if (!string.Equals(GetForskKind(obj), "layout", StringComparison.OrdinalIgnoreCase)
                && !isCut && !isDrawing)
                continue;
            if (viewSet != null)
            {
                var objView = obj.Attributes.GetUserString("forsk:view") ?? "";
                var clearsThis = viewSet.Contains(objView);
                if (isCut)
                    clearsThis = viewSet.Contains("plan") || viewSet.Contains(objView);
                if (!clearsThis) continue;
            }
            objectGuids.Add(obj.Id);
            objectIds.Add(obj.Id.ToString());
        }

        if (!dryRun)
        {
            foreach (var id in objectGuids)
                doc.Objects.Delete(id, true);
            foreach (var page in pages)
            {
                ForgetSchedules(doc, page.PageName);
                page.Close();
            }
            if (pages.Count > 0 || objectGuids.Count > 0)
                doc.Views.Redraw();
        }

        var count = pages.Count > 0 ? pages.Count : objectIds.Count;
        return new JObject
        {
            ["deleted"] = pageNames,
            ["object_ids"] = objectIds,
            ["count"] = count,
            ["dry_run"] = dryRun
        };
    }

    private static bool PageMatchesViewSet(RhinoPageView page, HashSet<string> viewSet)
    {
        foreach (var view in viewSet)
        {
            if (string.Equals(view, SchedulesView, StringComparison.OrdinalIgnoreCase) && IsSchedulesPage(page))
                return true;
            if (!TryGetLayoutView(view, out var spec)) continue;
            if (string.Equals(page.PageName, spec.PageName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static JObject ExportPdfResult(string path, JArray pages, string message)
    {
        return new JObject
        {
            ["path"] = path ?? "",
            ["count"] = pages?.Count ?? 0,
            ["pages"] = pages ?? new JArray(),
            ["message"] = message ?? ""
        };
    }
}
