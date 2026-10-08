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

public partial class RhinoMCPFunctions
{
    /// <summary>
    /// Draw each layout preview onto the PDF. The save dialog has already
    /// closed. A blank frame is retried. Pages that have ink are written
    /// even when another page stays white.
    /// </summary>
    private JObject ExportMacPreviewPdf(RhinoDoc doc, List<RhinoPageView> pages, string full)
    {
        int dpi = (int)Math.Round(PdfDpi);
        int dotsW = (int)Math.Round(SheetWidthMm / 25.4 * dpi);
        int dotsH = (int)Math.Round(SheetHeightMm / 25.4 * dpi);
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
        if (doc != null && !SheetSet.IsListSheet(view) && CountPrintDrawings(doc, SheetSet.DrawingOf(view)) == 0)
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
            if (SheetSet.IsTablePage(view))
            {
                RefreshTablePage(doc, page, view);
                continue;
            }
            // The daylight map's page draws the plan's drawing.
            view = SheetSet.DrawingOf(view);
            var plan = view.Equals("plan", StringComparison.OrdinalIgnoreCase);
            // A type swap must show on the next export without a new layout_pack.
            if (!plan && CountPrintDrawings(doc, view) > 0) continue;
            if (clay == null)
                clay = CollectLayoutClay(doc, _drawIncludeExisting, out _);
            Plane? clip = null;
            if (plan)
            {
                var cutZ = FloorTopZ(clay) + ForskDefaults.PlanCutHeightMm;
                clip = new Plane(new Point3d(0, 0, cutZ), -Vector3d.ZAxis);
            }
            // Tags, marks and level marks keep the page's scale.
            var strokeScale = DetailModelScale(page);
            if (strokeScale < 1) strokeScale = SheetScale.FirstStep;
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
        if (Details.TryPage(page.PageName, out var detailScale, out var detailSheet))
            return Details.SheetId(detailScale, detailSheet);
        foreach (var name in new[] { SheetSet.FrontId, SheetSet.TakeoffId, "plan", "north", "east", "south", "west" }
                     .Concat(Analysis.All.Select(Analysis.SheetId)).Concat(new[] { Analysis.MapSheetId }))
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
        // false keeps colour. The bitmap paints object colour, which sheet
        // objects set to the same pen as their print colour. Modelling layers
        // keep their display colour and stay off in the detail.
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
}
