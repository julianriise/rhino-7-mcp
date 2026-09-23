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
/// Paper layouts of a greyscale HiddenLineDrawing. Rhino 7 has no
/// ClippingDrawings. Before each page, layout_pack bakes black curves on
/// S-DRAW (Plan, North, East, South, West). The plan drawing includes the
/// horizontal cut 1200 mm above the floor. Details show that drawing layer
/// only, in a Top view (the curves are flat in XY), Wireframe, so the Mac
/// preview is black lines on white. The model
/// viewport keeps the clay. PDF is Rhino.FileIO.FilePdf. Vector output uses
/// ViewCaptureSettings with RasterMode false. On Rhino 7 Mac that capture
/// wrote a white sheet, so Mac export activates each layout, redraws, waits,
/// then draws GetPreviewImage into the PDF. Each export appends a line to
/// /tmp/forsk-print.log. AddPageView width and height are millimetres
/// (A3 landscape 420 x 297). Detail corners and the title block are converted
/// into the document page units.
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

        RestorePrintColors(doc);
        _drawIncludeExisting = includeExisting;
        var detailW = A3WidthMm - (2.0 * LayoutMarginMm);
        var detailH = A3HeightMm - LayoutMarginMm - TitleHeightMm - TitleGapMm - LayoutMarginMm;
        var pages = new JArray();
        var applied = new List<int>();
        var drawingNotes = new List<string>();
        string cutNote = null;
        var planCutZ = FloorTopZ(clay) + ForskDefaults.PlanCutHeightMm;
        var planClip = new Plane(new Point3d(0, 0, planCutZ), -Vector3d.ZAxis);
        foreach (var viewName in views)
        {
            if (!TryGetLayoutView(viewName, out var spec))
                throw new InvalidOperationException(UnknownViewMessage);

            if (replace)
                RemoveLayoutPages(doc, spec.View, false);

            Plane? clip = null;
            if (string.Equals(spec.View, "plan", StringComparison.OrdinalIgnoreCase))
                clip = planClip;
            var drawn = BakeGreyscaleDrawing(doc, spec.View, includeExisting, clip);
            if (!string.IsNullOrEmpty(drawn.Error) || drawn.Count < 1 || !drawn.Box.IsValid)
            {
                var why = string.IsNullOrEmpty(drawn.Error)
                    ? "No visible curves for " + spec.View + "."
                    : drawn.Error;
                throw new InvalidOperationException(why);
            }
            drawingNotes.Add(
                spec.View + " " + drawn.Count.ToString(CultureInfo.InvariantCulture));

            var scale = FitLayoutScale(requestedScale, ViewSpan(drawn.Box, spec.View), detailW, detailH);
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
                ["curves"] = drawn.Count,
                ["layer"] = drawn.Layer,
                ["ids"] = ids
            };
            if (string.Equals(spec.View, "plan", StringComparison.OrdinalIgnoreCase))
            {
                // The cut is already in the S-DRAW curves. A detail clipping
                // plane is not part of this pack.
                pageRecord["cut_z"] = planCutZ;
                pageRecord["cut_height_mm"] = ForskDefaults.PlanCutHeightMm;
                cutNote = " Plan cut "
                    + ForskDefaults.PlanCutHeightMm.ToString("0", CultureInfo.InvariantCulture)
                    + " mm above the floor (Z "
                    + planCutZ.ToString("0.###", CultureInfo.InvariantCulture)
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
        var curveNote = drawingNotes.Count == 0
            ? ""
            : " Greyscale drawing: " + string.Join(", ", drawingNotes.ToArray()) + ".";
        return new JObject
        {
            ["pages"] = pages,
            ["count"] = pages.Count,
            ["scale"] = reported,
            ["message"] = $"Laid out {pages.Count} page(s) on A3 {at}.{curveNote}{cutNote}"
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
                LogPrint(full, 0, GreyscaleMake2dNote(doc) + "; " + string.Join("; ", notes.ToArray()));
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
            LogPrint(full, names.Count, GreyscaleMake2dNote(doc) + "; " + string.Join("; ", notes.ToArray()));
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
    /// Page active, detail not active, drawing layers only, then paint.
    /// The paper and the detail stay Wireframe. Curves are already black.
    /// </summary>
    private void PrepareMacPage(RhinoPageView page)
    {
        if (page == null) return;
        var doc = page.Document ?? RhinoDoc.ActiveDoc;
        if (doc != null && CountPrintDrawings(doc, ViewKeyForPage(page)) == 0)
            EnsureGreyscaleDrawings(doc, new List<RhinoPageView> { page });
        ApplyPageDrawingDisplay(doc, page);
        if (doc != null)
            doc.Views.ActiveView = page;
        page.SetPageAsActive();
        page.Redraw();
        RhinoApp.Wait();
        WaitForOneIdle();
    }

    /// <summary>
    /// Rebuild a view when its S-DRAW curves are missing. layout_pack always
    /// refreshes them; export only fills gaps.
    /// </summary>
    private string EnsureGreyscaleDrawings(RhinoDoc doc, List<RhinoPageView> pages)
    {
        if (doc == null || pages == null) return null;
        RestorePrintColors(doc);
        List<RhinoObject> clay = null;
        foreach (var page in pages)
        {
            var view = ViewKeyForPage(page);
            if (string.IsNullOrEmpty(view)) continue;
            if (CountPrintDrawings(doc, view) > 0) continue;
            if (clay == null)
                clay = CollectLayoutClay(doc, _drawIncludeExisting, out _);
            Plane? clip = null;
            if (view.Equals("plan", StringComparison.OrdinalIgnoreCase))
            {
                var cutZ = FloorTopZ(clay) + ForskDefaults.PlanCutHeightMm;
                clip = new Plane(new Point3d(0, 0, cutZ), -Vector3d.ZAxis);
            }
            var drawn = BakeGreyscaleDrawing(doc, view, _drawIncludeExisting, clip);
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
        foreach (var name in new[] { "plan", "north", "east", "south", "west" })
        {
            if (!TryGetLayoutView(name, out var spec)) continue;
            if (string.Equals(page.PageName, spec.PageName, StringComparison.OrdinalIgnoreCase))
                return spec.View;
        }
        return null;
    }

    private Bitmap CapturePageAfterWait(RhinoPageView page, int dotsW, int dotsH, out int ink)
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
        string drawLayerPath,
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
    /// Wireframe. The curves are already black, so the detail does not need a pen mode.
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

    private static void LogPenLine(string detail)
    {
        try
        {
            var line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                + " pen " + detail + "\n";
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
