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
using RhinoMCPPlugin.Forsk;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
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

    /// <summary>The stored project info, every key (empty when unset), with date defaulting to today and scale_label to 1:100.</summary>
    private static JObject ProjectMetaRecord(RhinoDoc doc)
    {
        var info = ReadProjectInfo(doc);
        var record = new JObject();
        foreach (var key in ProjectInfo.Keys)
            record[key] = info[key];
        record[ProjectInfo.Date] = ProjectInfo.SheetDate(info, DateTime.Now);
        record["scale_label"] = MetaOr(doc, "scale_label", "1:100");
        record["logo"] = doc.Strings.GetValue(ProjectInfo.Section, OfficeLogo.NameKey) ?? "";
        return record;
    }

    private static ProjectInfo.Record ReadProjectInfo(RhinoDoc doc)
    {
        var record = ProjectInfo.Read(key => doc.Strings.GetValue(ProjectInfo.Section, key));
        // The title block, the PDF, the CSV and the IFC share this. A stored name wins; the date stays empty until typed.
        return ProjectInfo.WithDefaults(record, ForskPrint.FirmArchitect());
    }

    private static string MetaOr(RhinoDoc doc, string key, string fallback)
    {
        var value = doc.Strings.GetValue(LayoutMetaSection, key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>
    /// The views asked for, or with none the set's sheets that are on, in set
    /// order. off lists the set's sheets that are off (empty when views were
    /// given): a whole Print removes their old pages.
    /// </summary>
    private static List<string> ReadLayoutViews(RhinoDoc doc, JObject parameters, out List<string> off)
    {
        if (IsAnalysisSet(parameters) && !(parameters?["views"] is JArray))
        {
            // AN.3: the Analysis set's sheets; those taken out of it lose their old pages.
            var analysis = Analysis.SetSheets(ReadAnalysis(doc), HasDaylightMap(doc));
            off = analysis.Where(s => !s.On).Select(s => s.Id).ToList();
            var on = analysis.Where(s => s.On).Select(s => s.Id).ToList();
            if (on.Count == 0)
                throw new InvalidOperationException(NoAnalysisOnMessage);
            return on;
        }
        var views = new List<string>();
        off = new List<string>();
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

        var set = PrintSet(doc);
        views.AddRange(set.Where(s => s.On).Select(s => s.Id));
        off.AddRange(set.Where(s => !s.On).Select(s => s.Id));
        if (views.Count == 0 && off.Count > 0)
            throw new InvalidOperationException(NoSheetOnMessage);
        return views;
    }

    /// <summary>The set the next Print writes: inferred from the model, under the stored one.</summary>
    private static List<SheetSet.Sheet> PrintSet(RhinoDoc doc)
    {
        var stored = SheetSet.Read(doc.Strings.GetValue(SheetSet.MetaSection, SheetSet.MetaEntry));
        return SheetSet.Merge(SheetSet.Infer(PrintSetFacts(doc)), stored);
    }

    private static SheetSet.SetFacts PrintSetFacts(RhinoDoc doc)
    {
        var facts = new SheetSet.SetFacts { Level = WallLevel(doc) };
        foreach (var def in ReadSectionDefs(doc))
            facts.Sections.Add(def.Letter);
        var doors = false;
        var windows = false;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj) || IsExistingUnderlay(doc, obj)) continue;
            var kind = GetForskKind(obj);
            if (string.Equals(kind, "wall", StringComparison.OrdinalIgnoreCase))
                facts.Walls = true;
            else if (string.Equals(kind, "opening_marker", StringComparison.OrdinalIgnoreCase))
            {
                var opening = obj.Attributes.GetUserString("forsk:opening_kind");
                if (string.Equals(opening, "door", StringComparison.OrdinalIgnoreCase)) doors = true;
                if (string.Equals(opening, "window", StringComparison.OrdinalIgnoreCase)) windows = true;
            }
        }
        if (doors) facts.Lists.Add("door");
        if (windows) facts.Lists.Add("window");
        if (PlanRooms(doc).Any(r => r.Tagged)) facts.Lists.Add("room");
        if (facts.Walls) facts.DetailSheets = DetailSheetIds(doc);
        return facts;
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

    /// <summary>A drawing sheet of one pack: its view, how it is cut, and its last bake.</summary>
    private sealed class PackSheet
    {
        public LayoutViewSpec Spec;
        public bool Plan;
        public bool Section;
        public Plane? Clip;
        /// <summary>The scale its tags and marks were baked at.</summary>
        public int Stroke;
        public GreyscaleDrawing Drawn;
    }

    /// <summary>The baked drawing's box. S-DRAW packs are flattened into XY and the detail looks down.</summary>
    private static SheetScale.Span PackSpan(PackSheet sheet)
    {
        var box = sheet.Drawn.Box;
        return box.IsValid ? new SheetScale.Span(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y) : new SheetScale.Span(0, 0);
    }

    /// <summary>"1 sheet", "7 sheets".</summary>
    private static string SheetCountText(int count)
    {
        return count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " sheet" : " sheets");
    }

    /// <summary>
    /// The scale asked for: the scale parameter, snapped onto the list and
    /// stored for the next Print, or the one stored before. scale 0 clears
    /// it, so the set fits again. Null: the set takes the largest listed
    /// scale at which every sheet fits.
    /// </summary>
    private static int? ReadAskedScale(RhinoDoc doc, JObject parameters)
    {
        var token = parameters?["scale"];
        if (token != null && token.Type != JTokenType.Null)
            return StoreAskedScale(doc, token.ToObject<int>());
        return StoredPrintScale(doc);
    }

    /// <summary>Stores a listed denominator. 0 clears. Returns the stored scale, or null when cleared.</summary>
    private static int? StoreAskedScale(RhinoDoc doc, int value)
    {
        if (value < 0)
            throw new InvalidOperationException("Scale must be a positive number.");
        if (doc == null) return null;
        if (value == 0)
        {
            doc.Strings.Delete(LayoutMetaSection, PrintScaleEntry);
            return null;
        }
        var listed = SheetScale.Listed(value);
        doc.Strings.SetString(LayoutMetaSection, PrintScaleEntry, listed.ToString(CultureInfo.InvariantCulture));
        return listed;
    }

    /// <summary>The scale kept from an earlier ask, snapped onto the list, or null.</summary>
    private static int? StoredPrintScale(RhinoDoc doc)
    {
        var stored = doc.Strings.GetValue(LayoutMetaSection, PrintScaleEntry);
        if (!int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kept) || kept < 1)
            return null;
        var listed = SheetScale.Listed(kept);
        if (listed != kept)
            doc.Strings.SetString(LayoutMetaSection, PrintScaleEntry, listed.ToString(CultureInfo.InvariantCulture));
        return listed;
    }

    /// <summary>
    /// The set's scale for the cards: the one asked for, else what the last
    /// Print's drawing pages show (one scale for the set). 0 when neither.
    /// </summary>
    private static int KnownPrintScale(RhinoDoc doc)
    {
        var asked = StoredPrintScale(doc);
        if (asked.HasValue) return asked.Value;
        foreach (var page in MatchingForskPages(doc, null))
        {
            if (SheetSet.IsListSheet(ViewKeyForPage(page))) continue;
            var scale = DetailModelScale(page);
            if (scale > 0) return scale;
        }
        return 0;
    }

    private DetailViewObject AddClayDetail(
        RhinoDoc doc,
        RhinoPageView page,
        LayoutViewSpec spec,
        BoundingBox bbox,
        int scale,
        string drawLayerPath,
        out bool scaleLocked,
        double rightInsetMm = 0)
    {
        scaleLocked = false;
        var left = MmToPage(doc, LayoutMarginMm);
        var bottom = MmToPage(doc, LayoutMarginMm + FooterReserveMm);
        // A column kept clear at the right: the daylight map's legend.
        var right = MmToPage(doc, SheetWidthMm - LayoutMarginMm - rightInsetMm);
        var top = MmToPage(doc, SheetHeightMm - LayoutMarginMm);
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
            // The lists and the front sheet have no detail: they show their tables.
            if (SheetSet.IsListSheet(view))
            {
                if (ScheduleObjects(doc, page).Count == 0) return false;
                continue;
            }
            var bbox = PrintDrawingBounds(doc, SheetSet.DrawingOf(view));
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
                || (parent != null && layer.Index == parent.Index)
                || layer.ParentLayerId == drawLayer.Id;
            layer.SetPerViewportVisible(viewportId, show);
            doc.Layers.Modify(layer, layer.Index, true);
        }
    }

    /// <summary>
    /// One band along the bottom margin, left to right: north arrow (plan
    /// only), scale bar, title block. Fills <paramref name="footer"/> with
    /// the measured boxes in paper mm.
    /// </summary>
    private JArray AddSheetFooter(
        RhinoDoc doc, RhinoPageView page, LayoutViewSpec spec, string stableId, string sheetNo, int pageScale,
        string viewTitle, JObject footer)
    {
        var ids = new JArray();
        var layer = EnsureLayer(doc, "A-ANNO", Color.FromArgb(200, 160, 40));
        var pageId = page.MainViewport.Id;
        var bandMid = LayoutMarginMm + FooterBandMm / 2.0;

        ObjectAttributes Attr(string role)
        {
            var attr = LayoutAttr(layer.Index, pageId, spec.View, stableId);
            attr.SetUserString("forsk:role", role);
            // Rhino 7 pages carry no user text: the sheet's id and number ride on its footer.
            attr.SetUserString("forsk:sheet_id", spec.View);
            attr.SetUserString("forsk:sheet_no", sheetNo ?? "");
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
        var tx0 = SheetWidthMm - LayoutMarginMm - TitleBlockWidthMm;
        var tx1 = SheetWidthMm - LayoutMarginMm;
        var ty0 = LayoutMarginMm;
        var ty1 = LayoutMarginMm + FooterBandMm;
        var info = ReadProjectInfo(doc);
        var cells = TitleBlock.Cells(new TitleBlock.Fields
        {
            Drawing = viewTitle,
            Number = sheetNo,
            Scale = pageScale > 0 ? "1:" + pageScale.ToString(CultureInfo.InvariantCulture) : "",
            Format = _sheet.Name,
            // Project info: a stored date wins, none is the day the sheet is printed.
            Date = ProjectInfo.SheetDate(info, DateTime.Now),
            Revision = info[ProjectInfo.Revision],
            ProjectNo = info[ProjectInfo.ProjectNo],
            Project = info[ProjectInfo.Project],
            Client = info[ProjectInfo.Client],
            Address = info[ProjectInfo.Address],
            Architect = info[ProjectInfo.Architect]
        });
        // Widths from Rhino's own layout of each text at its height, not a glyph guess.
        Func<string, double, double> measure = (text, mm) => PaperTextWidth(doc,
            OneToOneTextStyle(doc, "Forsk paper " + mm.ToString("0.0", CultureInfo.InvariantCulture), MmToPage(doc, mm)), text);
        // The office logo, when Project info has one: its own cell at the right end, the fields share the rest.
        var logo = OfficeLogo.Decode(doc.Strings.GetValue(ProjectInfo.Section, OfficeLogo.Key));
        var logoBox = logo == null ? null : TitleBlock.Logo(logo.Aspect, tx1, ty0, ty1);
        var fieldsWidth = TitleBlockWidthMm - (logoBox?.CellMm ?? 0);
        var fx1 = tx0 + fieldsWidth;
        // One row, or Project no. and Architect in a smaller second row at the bottom of the band.
        var rows = TitleBlock.Rows(cells, fieldsWidth, measure);
        var titleStart = ids.Count;
        var frameId = Rect(tx0, ty0, tx1, ty1, Attr("title_block"));
        var cellRows = new JArray();
        var firstBottom = rows.Count > 1 ? ty0 + TitleBlock.SecondRowMm : ty0;
        if (rows.Count > 1)
            Line(tx0, firstBottom, fx1, firstBottom, Attr("title_block"));
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var rowBottom = r == 0 ? firstBottom : ty0;
            var rowTop = r == 0 ? ty1 : firstBottom;
            // The first row's value sits 4 mm up a full band, 2 mm up a split one; the second row's 1.2 mm.
            var valueY = r > 0 ? rowBottom + 1.2 : rowBottom + (rows.Count > 1 ? 2.0 : 4.0);
            var captionY = rowTop - (r > 0 ? 1.2 : 2.0) - TitleBlock.CaptionMm;
            var widths = TitleBlock.Widths(row, fieldsWidth, measure);
            var cx = tx0;
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                    Line(cx, rowBottom, cx, rowTop, Attr("title_block"));
                // forsk:cell is the English key the smokes read; the caption may be Norwegian.
                var captionAttr = Attr("title_cell");
                captionAttr.SetUserString("forsk:cell", row[i].Key);
                captionAttr.SetUserString("forsk:part", "caption");
                Text(row[i].Caption, cx + TitleBlock.PadMm, captionY, TitleBlock.CaptionMm, TextHorizontalAlignment.Left, captionAttr);
                var valueAttr = Attr("title_cell");
                valueAttr.SetUserString("forsk:cell", row[i].Key);
                valueAttr.SetUserString("forsk:part", "value");
                Text(row[i].Value, cx + TitleBlock.PadMm, valueY, row[i].Mm, TextHorizontalAlignment.Left, valueAttr);
                var cellRow = new JObject { ["name"] = row[i].Key, ["caption"] = row[i].Caption, ["text"] = row[i].Value };
                if (rows.Count > 1) cellRow["row"] = r + 1;
                cellRows.Add(cellRow);
                cx += widths[i];
            }
        }
        if (logoBox != null)
        {
            Line(fx1, ty0, fx1, ty1, Attr("title_block"));
            AddLogoPicture(doc, ids, logo, logoBox, Attr(OfficeLogo.Role));
            cellRows.Add(new JObject { ["name"] = "logo", ["width"] = Math.Round(logoBox.CellMm, 2) });
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
        if (TitleBlock.NorthArrow(spec.View))
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
        if (TitleBlock.ScaleBar(spec.View, pageScale))
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

    /// <summary>
    /// The logo on the layout, as Rhino shows and prints the page: a picture frame
    /// in page space with the bitmap embedded. Its paper box rides on it for the PDF,
    /// which draws the stored file itself.
    /// </summary>
    private static void AddLogoPicture(RhinoDoc doc, JArray ids, OfficeLogo.Picture logo, TitleBlock.LogoBox box, ObjectAttributes attr)
    {
        try
        {
            var bytes = logo.Jpeg ?? Convert.FromBase64String(doc.Strings.GetValue(ProjectInfo.Section, OfficeLogo.Key));
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "forsk-logo-" + doc.RuntimeSerialNumber.ToString(CultureInfo.InvariantCulture) + (logo.Jpeg != null ? ".jpg" : ".png"));
            System.IO.File.WriteAllBytes(path, bytes);
            var plane = new Plane(new Point3d(MmToPage(doc, box.X0), MmToPage(doc, box.Y0), 0), Vector3d.XAxis, Vector3d.YAxis);
            var id = doc.Objects.AddPictureFrame(plane, path, false,
                MmToPage(doc, box.X1 - box.X0), MmToPage(doc, box.Y1 - box.Y0), true, true);
            if (id == Guid.Empty) return;
            // The frame lands in model space: its surface and textured material go to the page instead.
            var frame = doc.Objects.FindId(id);
            var geometry = frame?.Geometry?.Duplicate();
            attr.SetUserString(OfficeLogo.BoxKey, OfficeLogo.FormatBox(box.X0, box.Y0, box.X1, box.Y1));
            if (frame != null)
            {
                attr.MaterialIndex = frame.Attributes.MaterialIndex;
                attr.MaterialSource = frame.Attributes.MaterialSource;
            }
            doc.Objects.Delete(id, true);
            if (geometry == null) return;
            var placed = doc.Objects.Add(geometry, attr);
            if (placed != Guid.Empty) ids.Add(placed.ToString());
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Forsk logo skipped: " + ex.Message);
        }
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

    /// <summary>
    /// The Forsk pages: the one layout asked for, else the Sheets set's pages,
    /// or with analysis the Analysis set's (AN.3). The two sets never print,
    /// export or list together.
    /// </summary>
    private static List<RhinoPageView> MatchingForskPages(RhinoDoc doc, string layout, bool analysis = false)
    {
        var pages = new List<RhinoPageView>();
        var all = doc.Views.GetPageViews();
        if (all == null) return pages;
        foreach (var page in all)
        {
            if (!IsForskLayoutPage(page)) continue;
            if (!string.IsNullOrWhiteSpace(layout))
            {
                if (!LayoutNameMatches(page, layout)) continue;
            }
            else if (SheetSet.IsAnalysisSheet(ViewKeyForPage(page)) != analysis)
                continue;
            pages.Add(page);
        }
        return pages;
    }

    private static bool IsAnalysisSet(JObject parameters) =>
        string.Equals(parameters?["set"]?.ToString()?.Trim(), AnalysisSetName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The pages in set order, a flowing list's pages first to last. Rhino
    /// lists pages as they were added, so a sheet printed again would come last.
    /// </summary>
    private static List<RhinoPageView> InSetOrder(RhinoDoc doc, List<RhinoPageView> pages)
    {
        var byPage = pages.OrderBy(p => IsSchedulesPage(p) ? SchedulesPageNumber(p) : 0).ToList();
        var ids = byPage.Select(p => ViewKeyForPage(p) ?? "").ToList();
        var set = PrintSet(doc).Concat(Analysis.SetSheets(ReadAnalysis(doc))).ToList();
        return SheetSet.Order(ids, set).Select(i => byPage[i]).ToList();
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
