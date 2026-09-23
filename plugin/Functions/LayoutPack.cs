using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Paper Layouts of the clay. Details use a parallel camera (same look
/// directions as Make2D). Plan and elevations use a Pen copy. The plan is a
/// horizontal cut 1200 mm above the floor, clipped only in that detail.
/// Only the clay layers are visible
/// in the detail. Source plan layers, especially labels, fill the sheet.
/// PDF is Rhino.FileIO.FilePdf. Vector output uses ViewCaptureSettings with
/// RasterMode false. On Rhino 7 Mac that capture wrote a white sheet, so Mac
/// export activates each layout, redraws, waits, then draws GetPreviewImage
/// into the PDF. Each export appends a line to /tmp/forsk-print.log.
/// AddPageView width and height are millimetres (A3 landscape 420 x 297).
/// Detail corners and the title block are converted into the document page units.
/// Does not bake model-space drawing curves and does not require S-* layers.
/// </summary>
public partial class RhinoMCPFunctions
{
    private struct LayoutViewSpec
    {
        public string View;
        public string PageName;
        public string SheetLabel;
        public Vector3d Look;
        public Vector3d Up;
    }

    private const string LayoutMetaSection = "forsk";
    private const string LayoutPagePrefix = "Forsk — ";
    private const string NothingToLayOutMessage = "Nothing to lay out. Bake walls first.";
    private const string UnknownPaperMessage = "Unknown paper. Use A3.";
    private const string ExportNeedsPathMessage = "export_pdf requires a file path.";
    private const string ExportNeedsPdfMessage = "export_pdf path must be an absolute .pdf file.";
    private const string NoLayoutsMessage = "No layouts to print. Call layout_pack first.";
    private const string PdfWriteFailedMessage = "PDF write failed.";
    private const string LayoutDetailFailedMessage = "Layout detail failed.";
    private const string EmptyDetailMessage = "Layout detail is empty. The sheet does not show the clay.";
    private const string EmptyPdfMessage = "PDF detail is empty. The sheet does not show the clay.";
    private const string CaptureFailedPrefix = "capture failed after activate/Wait";
    private const string PlanCutMissingMessage = "Plan cut failed. The plan detail has no clipping plane.";
    private const string PlanCutRole = "plan_cut";
    private const string ForskPenName = "Forsk Pen";
    private const int PenEdgePx = 1;
    private const int PenSilhouettePx = 2;
    private static int _penPass;
    private static int _penApplied = -1;
    private static string _penLog = "";

    private const double A3WidthMm = 420.0;
    private const double A3HeightMm = 297.0;
    private const double LayoutMarginMm = 10.0;
    private const double TitleWidthMm = 168.0;
    private const double TitleHeightMm = 46.0;
    private const double TitleGapMm = 6.0;
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
                spec = LayoutSpec("plan", "Forsk — Plan", "Plan", -Vector3d.ZAxis, Vector3d.YAxis);
                return true;
            case "north":
                spec = LayoutSpec("north", "Forsk — North", "Elevation north", -Vector3d.YAxis, Vector3d.ZAxis);
                return true;
            case "east":
                spec = LayoutSpec("east", "Forsk — East", "Elevation east", -Vector3d.XAxis, Vector3d.ZAxis);
                return true;
            case "south":
                spec = LayoutSpec("south", "Forsk — South", "Elevation south", Vector3d.YAxis, Vector3d.ZAxis);
                return true;
            case "west":
                spec = LayoutSpec("west", "Forsk — West", "Elevation west", Vector3d.XAxis, Vector3d.ZAxis);
                return true;
            default:
                return false;
        }
    }

    private static LayoutViewSpec LayoutSpec(
        string view, string pageName, string sheetLabel, Vector3d look, Vector3d up)
    {
        return new LayoutViewSpec
        {
            View = view,
            PageName = pageName,
            SheetLabel = sheetLabel,
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

        var requestedScale = 100;
        var scaleToken = parameters?["scale"];
        if (scaleToken != null && scaleToken.Type != JTokenType.Null)
        {
            requestedScale = scaleToken.ToObject<int>();
            if (requestedScale < 1)
                throw new InvalidOperationException("Scale must be a positive number.");
        }

        var views = ReadLayoutViews(parameters);
        var includeExisting = ReadBoolParam(parameters, "include_existing", true);
        var replace = ReadBoolParam(parameters, "replace", true);

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

        var detailW = A3WidthMm - (2.0 * LayoutMarginMm);
        var detailH = A3HeightMm - LayoutMarginMm - TitleHeightMm - TitleGapMm - LayoutMarginMm;
        _penPass++;
        var pages = new JArray();
        var applied = new List<int>();
        string cutNote = null;
        foreach (var viewName in views)
        {
            if (!TryGetLayoutView(viewName, out var spec))
                throw new InvalidOperationException(UnknownViewMessage);

            var scale = FitLayoutScale(requestedScale, ViewSpan(bbox, spec.View), detailW, detailH);
            applied.Add(scale);
            if (replace)
                RemoveLayoutPages(doc, spec.View, false);

            var page = doc.Views.AddPageView(spec.PageName, A3WidthMm, A3HeightMm);
            if (page == null)
                throw new InvalidOperationException(LayoutDetailFailedMessage);

            page.SetPageAsActive();
            DetailViewObject detail;
            bool scaleLocked;
            try
            {
                detail = AddClayDetail(
                    doc, page, spec, bbox, scale, includeExisting, clay, out scaleLocked);
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
            var scaleLabel = scaleLocked
                ? "1:" + scale.ToString(CultureInfo.InvariantCulture)
                : "fit";
            var ids = AddTitleBlock(doc, page, spec, stableId, scaleLabel);
            var pageRecord = new JObject
            {
                ["view"] = spec.View,
                ["page"] = spec.PageName,
                ["scale"] = scale,
                ["detail_count"] = 1,
                ["ids"] = ids
            };
            if (string.Equals(spec.View, "plan", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryAddPlanCut(doc, detail, clay, bbox, out var cutZ))
                {
                    RhinoApp.WriteLine(PlanCutMissingMessage);
                    try { LeaveDetail(page); } catch (Exception) { }
                    try { page.Close(); } catch (Exception) { }
                    throw new InvalidOperationException(PlanCutMissingMessage);
                }
                pageRecord["cut_z"] = cutZ;
                pageRecord["cut_height_mm"] = ForskDefaults.PlanCutHeightMm;
                cutNote = " Plan cut "
                    + ForskDefaults.PlanCutHeightMm.ToString("0", CultureInfo.InvariantCulture)
                    + " mm above the floor (Z "
                    + cutZ.ToString("0.###", CultureInfo.InvariantCulture)
                    + ").";
                RhinoApp.WriteLine("Forsk " + cutNote.Trim());
            }
            pages.Add(pageRecord);
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
        return new JObject
        {
            ["pages"] = pages,
            ["count"] = pages.Count,
            ["scale"] = reported,
            ["message"] = $"Laid out {pages.Count} page(s) on A3 {at}.{cutNote}"
        };
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

        _penPass++;
        var layout = parameters?["layout"]?.ToString();
        var pages = MatchingForskPages(doc, layout);
        if (pages.Count == 0)
        {
            var message = string.IsNullOrWhiteSpace(layout) ? NoLayoutsMessage : "Unknown layout.";
            return ExportPdfResult("", new JArray(), message);
        }

        foreach (var page in pages)
            LeaveDetail(page);

        if (!ForskPagesShowClay(doc, pages))
            return ExportPdfResult("", new JArray(), EmptyPdfMessage);

        var full = Path.GetFullPath(path);
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
                LeaveDetail(page);
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
            LogPrint(full, names.Count, "vector" + PenLogSuffix());
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
    /// Draw the layout preview onto the PDF page. The save dialog has already
    /// closed. Activate each page, redraw, and wait before GetPreviewImage so
    /// the capture is not the unpainted frame left by the modal.
    /// </summary>
    private JObject ExportMacPreviewPdf(RhinoDoc doc, List<RhinoPageView> pages, string full)
    {
        int dpi = (int)Math.Round(PdfDpi);
        int dotsW = (int)Math.Round(A3WidthMm / 25.4 * dpi);
        int dotsH = (int)Math.Round(A3HeightMm / 25.4 * dpi);
        var names = new JArray();
        var notes = new List<string>();
        var blanks = new List<string>();
        var shots = new List<Bitmap>();
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
                    bmp = CapturePageAfterWait(page, dotsW, dotsH, out var ink);
                    var size = bmp == null ? "null" : bmp.Width + "x" + bmp.Height;
                    var note = (page.PageName ?? "") + " ink " + ink + " " + size;
                    if (ink <= 0)
                    {
                        var debug = "/tmp/forsk-print-page-" + pageNumber.ToString(CultureInfo.InvariantCulture) + ".png";
                        if (!SaveDebugPng(bmp, debug))
                            note += " debug save failed " + debug;
                        else
                            note += " " + debug;
                        blanks.Add(debug);
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

            if (blanks.Count > 0)
            {
                LogPrint(full, 0, string.Join("; ", notes.ToArray()) + PenLogSuffix());
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
            LogPrint(full, names.Count, string.Join("; ", notes.ToArray()) + PenLogSuffix());
            return ExportPdfResult(full, names, $"Wrote {names.Count} page(s) to {full}.");
        }
        catch (Exception)
        {
            LogPrint(full, names.Count, "write failed");
            return ExportPdfResult(full, names, PdfWriteFailedMessage);
        }
        finally
        {
            foreach (var shot in shots)
            {
                if (shot != null) shot.Dispose();
            }
        }
    }

    /// <summary>
    /// Page active, detail not active, then paint. The paper stays Wireframe
    /// so the Mac capture still has a page to photograph. Each detail uses
    /// Forsk Pen.
    /// </summary>
    private static void PrepareMacPage(RhinoPageView page)
    {
        if (page == null) return;
        LeaveDetail(page);
        ApplyPaperDisplay(page);
        var details = page.GetDetailViews();
        if (details != null)
        {
            foreach (var detail in details)
            {
                if (detail == null) continue;
                if (detail.IsActive)
                    detail.IsActive = false;
                ApplyDetailDisplay(detail.Viewport);
            }
        }

        var doc = page.Document ?? RhinoDoc.ActiveDoc;
        if (doc != null)
            doc.Views.ActiveView = page;
        page.SetPageAsActive();
        page.Redraw();
        RhinoApp.Wait();
        WaitForOneIdle();
    }

    private static Bitmap CapturePageAfterWait(RhinoPageView page, int dotsW, int dotsH, out int ink)
    {
        PrepareMacPage(page);
        var size = new Size(dotsW, dotsH);
        var bmp = page.GetPreviewImage(size, false);
        ink = CountDarkSamples(bmp);
        if (ink > 0) return bmp;
        if (bmp != null) bmp.Dispose();
        // The first preview after a paint can still be empty. One more wait, then the same call.
        RhinoApp.Wait();
        bmp = page.GetPreviewImage(size, false);
        ink = CountDarkSamples(bmp);
        return bmp;
    }

    private static void WaitForOneIdle()
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

    private static bool SaveDebugPng(Bitmap bmp, string path)
    {
        if (bmp == null || string.IsNullOrEmpty(path)) return false;
        try
        {
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int CountDarkSamples(Bitmap bmp)
    {
        if (bmp == null || bmp.Width < 2 || bmp.Height < 2) return 0;
        int dark = 0;
        int step = Math.Max(8, bmp.Width / 40);
        for (int y = 0; y < bmp.Height; y += step)
        {
            for (int x = 0; x < bmp.Width; x += step)
            {
                Color color;
                try { color = bmp.GetPixel(x, y); }
                catch (Exception) { return dark; }
                if ((color.R + color.G + color.B) / 3 < 248) dark++;
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
                if (!TryGetLayoutView(token?.ToString(), out var spec))
                    throw new InvalidOperationException(UnknownViewMessage);
                viewSet.Add(spec.View);
            }
        }

        var dryRun = ReadBoolParam(parameters, "dry_run", false);
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

    private static string Dash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "—" : value;
    }

    private static List<string> ReadLayoutViews(JObject parameters)
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
                throw new InvalidOperationException(UnknownViewMessage);
            return views;
        }

        views.Add("plan");
        views.Add("north");
        views.Add("east");
        views.Add("south");
        views.Add("west");
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
        if (view == "east" || view == "west")
            return bbox.Max.Y - bbox.Min.Y;
        return bbox.Max.X - bbox.Min.X;
    }

    private static double ViewSpanHeight(BoundingBox bbox, string view)
    {
        if (view == "plan")
            return bbox.Max.Y - bbox.Min.Y;
        return bbox.Max.Z - bbox.Min.Z;
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
    private static int FitLayoutScale(int requested, Span span, double paperW, double paperH)
    {
        var scale = requested < 1 ? 100 : requested;
        if (span.Width <= 0 || span.Height <= 0 || paperW <= 0 || paperH <= 0)
            return scale;
        var guard = 0;
        while (guard++ < 8 &&
               (span.Width / scale > paperW * 0.9 || span.Height / scale > paperH * 0.9))
        {
            scale *= 2;
        }
        return scale;
    }

    private DetailViewObject AddClayDetail(
        RhinoDoc doc,
        RhinoPageView page,
        LayoutViewSpec spec,
        BoundingBox bbox,
        int scale,
        bool includeExisting,
        List<RhinoObject> clay,
        out bool scaleLocked)
    {
        scaleLocked = false;
        var left = MmToPage(doc, LayoutMarginMm);
        var bottom = MmToPage(doc, LayoutMarginMm + TitleHeightMm + TitleGapMm);
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
        page.SetActiveDetail(detail.Id);
        AimDetailCamera(detail, spec, bbox);
        detail.CommitViewportChanges();
        LeaveDetail(page);

        scaleLocked = LockDetailScale(detail, scale);
        detail.CommitChanges();

        // That commit can leave the locked scale looking at empty space.
        // Pan onto the clay. Do not CommitChanges again: it resets the frame.
        detail = ReloadDetail(page, detail);
        if (detail == null) return null;
        page.SetActiveDetail(detail.Id);
        PanDetailOntoClay(detail, spec, bbox);
        detail.CommitViewportChanges();
        LeaveDetail(page);

        if (!DetailSeesClay(detail.Viewport, bbox))
        {
            page.SetActiveDetail(detail.Id);
            var geom = detail.DetailGeometry;
            if (geom != null)
                geom.IsProjectionLocked = false;
            AimDetailCamera(detail, spec, bbox);
            detail.CommitViewportChanges();
            LeaveDetail(page);
            scaleLocked = LockDetailScale(detail, scale);
            detail.CommitChanges();
            detail = ReloadDetail(page, detail);
            if (detail == null) return null;
            page.SetActiveDetail(detail.Id);
            PanDetailOntoClay(detail, spec, bbox);
            detail.CommitViewportChanges();
            LeaveDetail(page);
        }

        if (detail.Viewport == null || detail.Viewport.Id == Guid.Empty)
            throw new InvalidOperationException(EmptyDetailMessage);
        if (!DetailSeesClay(detail.Viewport, bbox))
            throw new InvalidOperationException(EmptyDetailMessage);

        SetDetailLayerVisibility(doc, detail.Viewport.Id, includeExisting, clay);
        ApplyDetailDisplay(detail.Viewport);
        LeaveDetail(page);
        return detail;
    }

    /// <summary>
    /// Parallel camera, zoomed to the clay. Details use Forsk Pen. A shaded
    /// Technical mode filled the roof footprint, so the plan cut removes
    /// everything above the floor plus 1200 mm instead.
    /// </summary>
    private static void AimDetailCamera(DetailViewObject detail, LayoutViewSpec spec, BoundingBox bbox)
    {
        var look = spec.Look;
        if (!look.Unitize())
            look = -Vector3d.ZAxis;
        var target = bbox.Center;
        var dist = Math.Max(bbox.Diagonal.Length * 2.0, 5000.0);
        var vp = detail.Viewport;
        vp.ChangeToParallelProjection(true);
        vp.SetCameraLocations(target, target - (look * dist));
        vp.CameraUp = spec.Up;
        vp.SetCameraDirection(look, false);
        var framed = bbox;
        var pad = Math.Max(500.0, framed.Diagonal.Length * 0.02);
        framed.Inflate(pad);
        vp.ZoomBoundingBox(framed);
        ApplyDetailDisplay(vp);
    }

    private static void ApplyPaperDisplay(RhinoPageView page)
    {
        var mode = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.WireframeId);
        if (mode != null && page?.MainViewport != null)
            page.MainViewport.DisplayMode = mode;
    }

    /// <summary>
    /// Pen copy, re-applied on every layout and export so a saved preference
    /// cannot leave object-colored edges in place.
    /// </summary>
    private static void ApplyDetailDisplay(RhinoViewport viewport)
    {
        if (viewport == null) return;
        var mode = EnsureForskPen();
        if (mode != null)
            viewport.DisplayMode = mode;
    }

    private static string PenLogSuffix()
    {
        EnsureForskPen();
        return string.IsNullOrEmpty(_penLog) ? "" : " | " + _penLog;
    }

    private static DisplayModeDescription EnsureForskPen()
    {
        if (_penApplied == _penPass && _penModeCached != null)
            return _penModeCached;
        var mode = DisplayModeDescription.FindByName(ForskPenName);
        if (mode == null || mode.Id == DisplayModeDescription.PenId)
        {
            var id = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.PenId, ForskPenName);
            if (id != Guid.Empty)
                mode = DisplayModeDescription.GetDisplayMode(id);
        }
        if (mode == null || mode.Id == DisplayModeDescription.PenId)
        {
            mode = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.PenId)
                ?? DisplayModeDescription.GetDisplayMode(DisplayModeDescription.WireframeId);
        }
        else
        {
            var iniNote = RepatchPenIni(mode);
            mode = DisplayModeDescription.FindByName(ForskPenName) ?? mode;
            var reflected = "";
            try { reflected = ApplyManagedPen(mode); }
            catch (Exception) { reflected = "attrs-failed"; }
            _penLog = "pen edge " + PenEdgePx + "px silhouette " + PenSilhouettePx
                + "px " + iniNote
                + (reflected.Length == 0 ? " r7-props-only" : " reflected " + reflected);
        }
        _penModeCached = mode;
        _penApplied = _penPass;
        return mode;
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
        if (TrySet(attr, "ClippingEdgeThickness", PenSilhouettePx)) hit.Add("ClippingEdgeThickness");
        if (TrySet(attr, "ClippingEdgeColorUsage", 1)) hit.Add("ClippingEdgeColorUsage=1");
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

    private static string RepatchPenIni(DisplayModeDescription mode)
    {
        var path = "/tmp/forsk-pen.ini";
        try
        {
            var exported = DisplayModeDescription.ExportToFile(mode, path);
            if (exported is bool ok && !ok) return "ini-export-failed";
            if (!File.Exists(path)) return "ini-missing";
            var text = File.ReadAllText(path);
            int replaced;
            var patched = PatchPenIni(text, out replaced);
            File.WriteAllText(path, patched);
            if (mode.Id != DisplayModeDescription.PenId)
            {
                try { DisplayModeDescription.DeleteDisplayMode(mode.Id); }
                catch (Exception) { }
            }
            var imported = DisplayModeDescription.ImportFromFile(path);
            if (imported == Guid.Empty) return "ini-import-failed keys " + replaced;
            return "ini-keys " + replaced;
        }
        catch (Exception)
        {
            return "ini-export-failed";
        }
    }

    private static string PatchPenIni(string text, out int replaced)
    {
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "EdgeColorUsage", "2" },
            { "EdgeColor", "0,0,0" },
            { "EdgeThickness", PenEdgePx.ToString(CultureInfo.InvariantCulture) },
            { "NakedEdgeColorUsage", "2" },
            { "NakedEdgeColor", "0,0,0" },
            { "NakedEdgeThickness", PenEdgePx.ToString(CultureInfo.InvariantCulture) },
            { "ClippingEdgesUsage", "1" },
            { "ClippingEdgeColor", "0,0,0" },
            { "ClippingEdgeThickness", PenSilhouettePx.ToString(CultureInfo.InvariantCulture) },
            { "THThickness", PenEdgePx.ToString(CultureInfo.InvariantCulture) },
            { "TEThickness", PenEdgePx.ToString(CultureInfo.InvariantCulture) },
            { "TSiThickness", PenSilhouettePx.ToString(CultureInfo.InvariantCulture) },
            { "TCThickness", PenEdgePx.ToString(CultureInfo.InvariantCulture) },
            { "TSThickness", PenEdgePx.ToString(CultureInfo.InvariantCulture) },
            { "TIThickness", PenEdgePx.ToString(CultureInfo.InvariantCulture) },
            { "THColor", "0,0,0" },
            { "TEColor", "0,0,0" },
            { "TSiColor", "0,0,0" },
            { "TCColor", "0,0,0" },
            { "TSColor", "0,0,0" },
            { "TIColor", "0,0,0" },
            { "surfaceEdgeColorUsageIndex", "2" },
            { "singleCurveColorIndex", "1" },
            { "clippingEdgesUsage", "1" }
        };
        replaced = 0;
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Length; i++)
        {
            var eq = lines[i].IndexOf('=');
            if (eq <= 0) continue;
            var key = lines[i].Substring(0, eq).Trim();
            string value;
            if (!wanted.TryGetValue(key, out value)) continue;
            lines[i] = key + "=" + value;
            seen.Add(key);
            replaced++;
        }
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (seen.Count < wanted.Count)
        {
            sb.Append("\n[ForskPen]\n");
            foreach (var pair in wanted)
            {
                if (seen.Contains(pair.Key)) continue;
                sb.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Horizontal section for the plan detail only. The Rhino pointer faces the
    /// visible side, so the normal points down: geometry below the cut stays,
    /// and the roof above it is clipped. The plane is infinite; the widget size
    /// is only the on-screen grip.
    /// </summary>
    private bool TryAddPlanCut(
        RhinoDoc doc, DetailViewObject detail, List<RhinoObject> clay, BoundingBox bbox, out double cutZ)
    {
        cutZ = 0;
        if (doc == null || detail?.Viewport == null || detail.Viewport.Id == Guid.Empty)
            return false;
        DeletePlanCuts(doc);
        var ffl = FloorTopZ(clay);
        cutZ = ffl + ForskDefaults.PlanCutHeightMm;
        var origin = new Point3d(bbox.Center.X, bbox.Center.Y, cutZ);
        var plane = new Plane(origin, -Vector3d.ZAxis);
        var u = Math.Max(1000.0, bbox.Max.X - bbox.Min.X);
        var v = Math.Max(1000.0, bbox.Max.Y - bbox.Min.Y);
        var layer = EnsureLayer(doc, "A-ANNO", Color.FromArgb(200, 160, 40));
        var attr = new ObjectAttributes
        {
            LayerIndex = layer.Index,
            Name = "forsk-plan-cut",
            Space = ActiveSpace.ModelSpace
        };
        StampForskTags(attr, new ForskStamp
        {
            Kind = "layout",
            View = "plan",
            Id = "cut"
        });
        attr.SetUserString("forsk:role", PlanCutRole);
        attr.SetUserString("forsk:cut_z", cutZ.ToString("0.###", CultureInfo.InvariantCulture));
        var clipId = doc.Objects.AddClippingPlane(
            plane, u, v, new[] { detail.Viewport.Id }, attr);
        if (clipId == Guid.Empty) return false;
        doc.Strings.SetString(
            LayoutMetaSection,
            "plan_cut_z",
            cutZ.ToString("0.###", CultureInfo.InvariantCulture));
        doc.Strings.SetString(
            LayoutMetaSection,
            "plan_cut_height_mm",
            ForskDefaults.PlanCutHeightMm.ToString("0", CultureInfo.InvariantCulture));
        ShowPlanCutInDetail(doc, layer, detail.Viewport.Id);
        return PlanDetailIsClipped(doc, clipId, detail.Viewport.Id);
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

    private static void ShowPlanCutInDetail(RhinoDoc doc, Layer layer, Guid detailId)
    {
        if (doc == null || layer == null || detailId == Guid.Empty) return;
        var modelViews = doc.Views.GetViewList(true, false);
        if (modelViews != null)
        {
            foreach (var view in modelViews)
            {
                var id = view?.MainViewport?.Id ?? Guid.Empty;
                if (id == Guid.Empty) continue;
                layer.SetPerViewportVisible(id, false);
            }
        }
        layer.SetPerViewportVisible(detailId, true);
        layer.SetPerViewportPlotWeight(detailId, 0);
        doc.Layers.Modify(layer, layer.Index, true);
    }

    private static bool PlanDetailIsClipped(RhinoDoc doc, Guid clipId, Guid detailId)
    {
        var obj = doc?.Objects.FindId(clipId) as ClippingPlaneObject;
        var geom = obj?.ClippingPlaneGeometry;
        if (geom == null) return false;
        var ids = geom.ViewportIds();
        if (ids == null) return false;
        foreach (var id in ids)
        {
            if (id == detailId) return true;
        }
        return false;
    }

    private static void DeletePlanCuts(RhinoDoc doc)
    {
        if (doc == null) return;
        var ids = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj == null) continue;
            var role = obj.Attributes?.GetUserString("forsk:role") ?? "";
            if (role.Equals(PlanCutRole, StringComparison.OrdinalIgnoreCase))
                ids.Add(obj.Id);
        }
        foreach (var id in ids)
            doc.Objects.Delete(id, true);
    }

    /// <summary>
    /// Move the locked view onto the clay. ZoomBoundingBox would change the scale.
    /// </summary>
    private static void PanDetailOntoClay(DetailViewObject detail, LayoutViewSpec spec, BoundingBox bbox)
    {
        var look = spec.Look;
        if (!look.Unitize())
            look = -Vector3d.ZAxis;
        var vp = detail.Viewport;
        vp.ChangeToParallelProjection(true);
        vp.CameraUp = spec.Up;
        vp.SetCameraTarget(bbox.Center, true);
        vp.SetCameraDirection(look, false);
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

    private static bool ForskPagesShowClay(RhinoDoc doc, List<RhinoPageView> pages)
    {
        var clay = CollectLayoutClay(doc, true, out var hasWall);
        if (!hasWall) return false;
        var bbox = ClayBoundingBox(clay);
        if (!bbox.IsValid) return false;
        foreach (var page in pages)
        {
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
    /// The detail shows clay only. Source layers such as label text fill the sheet.
    /// Opening markers stay off. Walls, floor, and roof stay visible and printable
    /// even when the bake layer is not the A-WALL / A-FLOR / A-ROOF name.
    /// </summary>
    private void SetDetailLayerVisibility(
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

    private JArray AddTitleBlock(
        RhinoDoc doc, RhinoPageView page, LayoutViewSpec spec, string stableId, string scaleLabel)
    {
        var ids = new JArray();
        var layer = EnsureLayer(doc, "A-ANNO", Color.FromArgb(200, 160, 40));
        var pageId = page.MainViewport.Id;
        var x0 = MmToPage(doc, A3WidthMm - LayoutMarginMm - TitleWidthMm);
        var y0 = MmToPage(doc, LayoutMarginMm);
        var x1 = MmToPage(doc, A3WidthMm - LayoutMarginMm);
        var y1 = MmToPage(doc, LayoutMarginMm + TitleHeightMm);

        var rect = new Polyline
        {
            new Point3d(x0, y0, 0),
            new Point3d(x1, y0, 0),
            new Point3d(x1, y1, 0),
            new Point3d(x0, y1, 0),
            new Point3d(x0, y0, 0)
        };
        var frameId = doc.Objects.AddCurve(rect.ToNurbsCurve(), LayoutAttr(layer.Index, pageId, spec.View, stableId));
        if (frameId != Guid.Empty)
            ids.Add(frameId.ToString());

        var meta = ProjectMetaRecord(doc);
        var lines = new[]
        {
            "Project  " + Dash(meta["project"]?.ToString()),
            "Client   " + Dash(meta["client"]?.ToString()),
            "Address  " + Dash(meta["address"]?.ToString()),
            "Sheet    " + spec.SheetLabel,
            "Scale    " + scaleLabel,
            "Date     " + Dash(meta["date"]?.ToString())
        };
        var textH = MmToPage(doc, 2.6);
        var gap = MmToPage(doc, 1.15);
        var y = y1 - MmToPage(doc, 4.0) - textH;
        var x = x0 + MmToPage(doc, 3.0);
        foreach (var line in lines)
        {
            var plane = Plane.WorldXY;
            plane.Origin = new Point3d(x, y, 0);
            var id = doc.Objects.AddText(
                line,
                plane,
                textH,
                "Arial",
                false,
                false,
                LayoutAttr(layer.Index, pageId, spec.View, stableId));
            if (id != Guid.Empty)
                ids.Add(id.ToString());
            y -= textH + gap;
        }
        return ids;
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
            if (!string.Equals(GetForskKind(obj), "layout", StringComparison.OrdinalIgnoreCase) && !isCut)
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
                page.Close();
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
