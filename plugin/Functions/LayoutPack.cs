using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Paper Layouts of the clay. Details use a parallel camera (same look
/// directions as Make2D) in Wireframe, and only the clay layers are visible
/// in the detail. Source plan layers, especially labels, fill the sheet.
/// PDF is Rhino.FileIO.FilePdf. Vector output uses ViewCaptureSettings with
/// RasterMode false. On Rhino 7 Mac that capture, and a raster capture of the
/// same page, wrote a white sheet, so Mac export draws the page preview into
/// the PDF instead. Each export appends a line to /tmp/forsk-print.log.
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
        var pages = new JArray();
        var applied = new List<int>();
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
            pages.Add(new JObject
            {
                ["view"] = spec.View,
                ["page"] = spec.PageName,
                ["scale"] = scale,
                ["detail_count"] = 1,
                ["ids"] = ids
            });
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
            ["message"] = $"Laid out {pages.Count} page(s) on A3 {at}."
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
            LogPrint(full, names.Count, "vector");
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
    /// Mac FilePdf capture of a Layout is a white bitmap. Draw the page preview,
    /// which already shows the clay and the title block, onto each PDF page.
    /// </summary>
    private JObject ExportMacPreviewPdf(RhinoDoc doc, List<RhinoPageView> pages, string full)
    {
        int dpi = (int)Math.Round(PdfDpi);
        int dotsW = (int)Math.Round(A3WidthMm / 25.4 * dpi);
        int dotsH = (int)Math.Round(A3HeightMm / 25.4 * dpi);
        var names = new JArray();
        var notes = new List<string>();
        var previous = doc.Views.ActiveView;
        try
        {
            var pdf = FilePdf.Create();
            int pageNumber = 0;
            foreach (var page in pages)
            {
                pageNumber++;
                LeaveDetail(page);
                doc.Views.ActiveView = page;
                page.SetPageAsActive();
                page.Redraw();
                Bitmap bmp = null;
                try
                {
                    bmp = page.GetPreviewImage(new Size(dotsW, dotsH), false);
                    var ink = CountDarkSamples(bmp);
                    if (ink == 0)
                    {
                        if (bmp != null) bmp.Dispose();
                        page.Redraw();
                        bmp = page.GetPreviewImage(new Size(dotsW, dotsH), false);
                        ink = CountDarkSamples(bmp);
                    }
                    pdf.AddPage(dotsW, dotsH, dpi);
                    if (bmp != null)
                        pdf.DrawBitmap(pageNumber, bmp, 0, 0, dotsW, dotsH, 0);
                    var size = bmp == null ? "null" : bmp.Width + "x" + bmp.Height;
                    notes.Add((page.PageName ?? "") + " ink " + ink + " " + size);
                }
                finally
                {
                    if (bmp != null) bmp.Dispose();
                }
                names.Add(page.PageName ?? "");
            }

            pdf.Write(full);
            LogPrint(full, names.Count, string.Join("; ", notes.ToArray()));
            return ExportPdfResult(full, names, $"Wrote {names.Count} page(s) to {full}.");
        }
        catch (Exception)
        {
            LogPrint(full, names.Count, "write failed");
            return ExportPdfResult(full, names, PdfWriteFailedMessage);
        }
        finally
        {
            if (previous != null)
                doc.Views.ActiveView = previous;
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

        var mode = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.WireframeId);
        if (mode != null)
        {
            if (page.MainViewport != null)
                page.MainViewport.DisplayMode = mode;
        }

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
        LeaveDetail(page);
        return detail;
    }

    /// <summary>
    /// Parallel camera, Wireframe, zoomed to the clay. Technical filled the floor slab solid black.
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
        var mode = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.WireframeId);
        if (mode != null)
            vp.DisplayMode = mode;
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
                // PlotWeight -1 is "do not print". 0 is the default pen.
                layer.SetPerViewportPlotColor(viewportId, Color.Black);
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
            if (!string.Equals(GetForskKind(obj), "layout", StringComparison.OrdinalIgnoreCase))
                continue;
            if (viewSet != null)
            {
                var objView = obj.Attributes.GetUserString("forsk:view") ?? "";
                if (!viewSet.Contains(objView)) continue;
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
