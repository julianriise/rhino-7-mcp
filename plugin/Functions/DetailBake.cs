using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// A detail sheet's drawings, baked on S-DRAW::Details 20-1 in the sheet's
/// own model region. Each drawing is the model trimmed by its cut and its
/// four crop planes, seen from its look, then clipped to the crop in 2D so
/// nothing outside it survives a trim that failed. Lines on the crop are
/// where it cuts the model and draw as break lines. Pens are the profile at
/// the sheet's scale; dimensions keep their paper size.
/// </summary>
public partial class RhinoMCPFunctions
{
    /// <summary>The drawings on detail sheet n at 1:scale, placed, and how many details dropped.</summary>
    private static List<Details.Placed> DetailSheetDrawings(RhinoDoc doc, int scale, int n, out int dropped)
    {
        dropped = 0;
        var records = Details.Read(doc.Strings.GetValue(Details.Section, Details.Entry));
        if (records.Count == 0) return new List<Details.Placed>();
        var model = ReadIfcModel(doc);
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var facts = new List<Details.Facts>();
        foreach (var record in records)
        {
            var one = Details.Resolve(record, model, tol);
            if (one == null) dropped++;
            else facts.Add(one);
        }
        var names = Details.Names(facts);
        var drawings = new List<Details.Drawing>();
        for (var i = 0; i < facts.Count; i++)
        {
            var own = Details.Drawings(facts[i], names[i]);
            if (own.Count > 0 && Details.ScaleOf(own) == scale) drawings.AddRange(own);
        }
        return n == 1 ? Details.Row(drawings, scale) : new List<Details.Placed>();
    }

    private GreyscaleDrawing BakeDetailSheet(RhinoDoc doc, int scale, int n, bool includeExisting)
    {
        var view = Details.SheetId(scale, n);
        var result = new GreyscaleDrawing { View = view, Layer = "", Box = BoundingBox.Empty, Details = new JArray() };
        var baseProfile = ReadPrintProfile(doc);
        PrintProfiles.Active = PrintProfiles.AtScale(baseProfile, scale);
        var placed = DetailSheetDrawings(doc, scale, n, out result.DetailsDropped);
        if (placed.Count == 0)
        {
            result.Error = "No details at 1:" + scale.ToString(CultureInfo.InvariantCulture) + ".";
            return result;
        }
        var layer = EnsureDrawLayer(doc, Details.LayerName(scale, n));
        if (layer == null)
        {
            result.Error = "Hidden line drawing failed.";
            return result;
        }
        DeletePrintDrawings(doc, layer);
        var pattern = SolidPatternIndex(doc);
        var tol = doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.01;
        var sources = ResolveDrawSources(doc, new JObject(), includeExisting, out _, true);
        var members = new Dictionary<Guid, List<RhinoObject>>();
        var wallSolids = WallClusterSolids(doc, sources, out result.WallNote, members);
        var origin = Details.SheetOrigin(scale, n);
        var box = BoundingBox.Empty;
        var index = 1;
        var count = 0;
        var fills = 0;
        var notes = new List<string>();
        foreach (var item in placed)
        {
            var d = item.Drawing;
            var shift = new Pt(origin.X + item.X * scale - d.U0, origin.Y + item.Y * scale - d.V0);
            try
            {
                fills += BakeDetailDrawing(doc, layer, view, d, shift, scale, baseProfile, sources, wallSolids, members,
                    pattern, tol, ref box, ref index, ref count, notes);
            }
            catch (Exception ex)
            {
                notes.Add(d.Title + " skipped");
                RhinoApp.WriteLine("Forsk detail skipped: " + d.Title + ": " + ex.Message);
            }
            result.Details.Add(new JObject
            {
                ["number"] = item.Number,
                ["title"] = d.Title,
                ["view"] = d.View,
                ["detail"] = d.Facts.Record.Id
            });
        }
        result.Layer = layer.FullPath ?? layer.Name;
        result.Count = count;
        result.Fills = fills;
        // The whole detail area: the page's one detail frames it at the sheet's scale.
        result.Box = new BoundingBox(
            new Point3d(origin.X, origin.Y, 0),
            new Point3d(origin.X + Details.AreaWidthMm * scale, origin.Y + Details.AreaHeightMm * scale, 0));
        result.SymbolNote = notes.Count == 0 ? null : string.Join("; ", notes);
        if (count == 0) result.Error = "No visible curves for " + view + ".";
        return result;
    }

    /// <summary>
    /// A detail sheet's page: its drawings baked at its own scale, one detail
    /// locked at that scale over the whole detail area, and the footer. Null
    /// when no detail is at that scale.
    /// </summary>
    private JObject AddDetailSheetPage(RhinoDoc doc, string view, bool includeExisting, int number, int wallLevel)
    {
        if (!Details.TrySheetId(view, out var scale, out var n) || !TryGetLayoutView(view, out var spec)) return null;
        var drawn = BakeGreyscaleDrawing(doc, view, includeExisting, null, scale);
        if (!string.IsNullOrEmpty(drawn.Error) || drawn.Count < 1)
        {
            RhinoApp.WriteLine("Forsk " + view + ": " + (drawn.Error ?? "nothing drawn"));
            return null;
        }
        var page = doc.Views.AddPageView(spec.PageName, A3WidthMm, A3HeightMm);
        if (page == null)
            throw new InvalidOperationException(LayoutDetailFailedMessage);
        page.SetPageAsActive();
        DetailViewObject detail;
        bool scaleLocked;
        try
        {
            detail = AddClayDetail(doc, page, spec, drawn.Box, scale, drawn.Layer, out scaleLocked);
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
        var stableId = FormatStableId("l", number);
        var title = Details.SheetTitle(scale);
        var sheetNo = SheetSet.Number(spec.View, wallLevel);
        var pageScale = scaleLocked ? DetailModelScale(page) : 0;
        var footer = new JObject();
        var ids = AddSheetFooter(doc, page, spec, stableId, sheetNo, pageScale, title, footer);
        return new JObject
        {
            ["view"] = spec.View,
            ["page"] = spec.PageName,
            ["number"] = sheetNo,
            ["title"] = title,
            ["scale"] = scale,
            ["page_scale"] = pageScale,
            ["detail_count"] = 1,
            ["curves"] = drawn.Count,
            ["fills"] = drawn.Fills,
            ["layer"] = drawn.Layer,
            ["ids"] = ids,
            ["footer"] = footer,
            ["details"] = drawn.Details ?? new JArray(),
            ["details_dropped"] = drawn.DetailsDropped,
            ["note"] = drawn.SymbolNote ?? ""
        };
    }

    /// <summary>The planes a drawing keeps, each normal toward the side kept: its cut, then its four crop sides.</summary>
    private static List<Plane> DetailPlanes(Details.Drawing d)
    {
        var x = new Vector3d(d.X.X, d.X.Y, 0);
        var y = d.Vertical ? Vector3d.ZAxis : new Vector3d(d.Y.X, d.Y.Y, 0);
        var planes = new List<Plane>();
        if (d.Clipped)
        {
            planes.Add(d.Vertical
                ? new Plane(new Point3d(d.Look.X * d.Depth, d.Look.Y * d.Depth, 0), new Vector3d(d.Look.X, d.Look.Y, 0))
                : new Plane(new Point3d(0, 0, d.CutZ), -Vector3d.ZAxis));
        }
        Point3d At(double u, double v) => Point3d.Origin + x * u + y * v;
        planes.Add(new Plane(At(d.U0, d.V0), x));
        planes.Add(new Plane(At(d.U1, d.V0), -x));
        planes.Add(new Plane(At(d.U0, d.V0), y));
        planes.Add(new Plane(At(d.U0, d.V1), -y));
        return planes;
    }

    /// <summary>A model point in the drawing's frame: u along X, v along Left(X) on a plan or up a vertical drawing.</summary>
    private static Pt DetailFrame(Details.Drawing d, Point3d p) =>
        new Pt(p.X * d.X.X + p.Y * d.X.Y, d.Vertical ? p.Z : p.X * d.Y.X + p.Y * d.Y.Y);

    private int BakeDetailDrawing(
        RhinoDoc doc, Layer layer, string view, Details.Drawing d, Pt shift, int scale, PrintProfile baseProfile,
        List<RhinoObject> sources, Dictionary<Guid, Brep> wallSolids, Dictionary<Guid, List<RhinoObject>> members,
        int pattern, double tol, ref BoundingBox box, ref int index, ref int count, List<string> notes)
    {
        var rect = new DetailClip.Rect(d.U0, d.V0, d.U1, d.V1);
        // Each stretch a break keeps, and how far along u it moves to be drawn.
        var keep = (d.Breaks?.Keep() ?? new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(d.U0, d.U1) })
            .Select(k => (Rect: new DetailClip.Rect(k.Key, d.V0, k.Value, d.V1), Du: d.Map(k.Key) - k.Key))
            .ToList();
        var near = sources.Where(o => MeetsCrop(o, d, rect)).ToList();
        var stamp = new SymbolStamp
        {
            Extra = new Dictionary<string, string>
            {
                ["forsk:view"] = view,
                ["forsk:detail"] = d.Facts.Record.Id,
                ["forsk:detail_view"] = d.View
            }
        };
        Point3d Sheet(Pt p) => new Point3d(p.X + shift.X, p.Y + shift.Y, 0);

        var geometries = new List<GeometryBase>();
        foreach (var obj in near)
            AppendSource(obj, wallSolids, geometries);
        var trimmed = new List<GeometryBase>();
        var planes = DetailPlanes(d);
        foreach (var geom in geometries)
        {
            var pieces = new List<GeometryBase> { geom };
            foreach (var plane in planes)
            {
                // Trim keeps the side opposite its plane's normal, so flip each keep-side plane first.
                var hldPlane = plane;
                hldPlane.Flip();
                var next = new List<GeometryBase>();
                foreach (var piece in pieces)
                {
                    next.AddRange(KeepSectionSide(piece, hldPlane, tol));
                    if (!ReferenceEquals(piece, geom)) piece.Dispose();
                }
                pieces = next;
            }
            trimmed.AddRange(pieces);
        }
        foreach (var geom in geometries) geom?.Dispose();

        var added = 0;
        var clipped2d = 0;
        HiddenLineDrawing hld = null;
        try
        {
            var bbox = BoundingBox.Empty;
            foreach (var geom in trimmed) bbox.Union(geom.GetBoundingBox(true));
            var look = d.Vertical ? new Vector3d(d.Look.X, d.Look.Y, 0) : -Vector3d.ZAxis;
            var up = d.Vertical ? Vector3d.ZAxis : new Vector3d(d.Y.X, d.Y.Y, 0);
            var viewport = bbox.IsValid ? BuildParallelViewport(bbox, look, up) : null;
            if (viewport != null && viewport.IsValidCamera && viewport.IsValidFrustum)
            {
                var hldParams = new HiddenLineDrawingParameters
                {
                    AbsoluteTolerance = tol,
                    Flatten = true,
                    IncludeHiddenCurves = false,
                    IncludeTangentEdges = false,
                    IncludeTangentSeams = false
                };
                hldParams.SetViewport(viewport);
                foreach (var geom in trimmed) hldParams.AddGeometry(geom, Transform.Identity, null);
                hld = HiddenLineDrawing.Compute(hldParams, true);
            }
            if (hld?.Segments != null && TryHldToFrame(d, hld.WorldToHiddenLine, out var toFrame))
            {
                foreach (var seg in hld.Segments)
                {
                    if (!KeepGreyscaleSegment(seg) || seg.CurveGeometry == null) continue;
                    var cut = IsSectionCut(seg);
                    var pen = cut ? PenCut : seg.IsSceneSilhouette ? PenSilhouette : PenBeyond;
                    var points = CurvePoints(seg.CurveGeometry, tol).Select(toFrame).ToList();
                    foreach (var part in keep)
                    foreach (var run in ClipRuns(points, part.Rect, tol, ref clipped2d))
                    {
                        using (var curve = new PolylineCurve(run.Select(p => Sheet(new Pt(p.X + part.Du, p.Y)))))
                            added += AddStroke(doc, layer, curve, pen, scale, false, pattern, tol,
                                cut ? "cut" : "beyond", null, null, null, ref box, ref index, ref count, stamp);
                    }
                }
            }
        }
        finally
        {
            hld?.Dispose();
            foreach (var geom in trimmed) geom?.Dispose();
        }
        if (clipped2d > 0) notes.Add(d.Title + ": " + clipped2d.ToString(CultureInfo.InvariantCulture) + " lines clipped in 2D");

        var rings = new List<List<Pt>>();
        var breakEdges = new List<KeyValuePair<Pt, Pt>>();
        var fills = 0;
        if (d.Clipped)
        {
            var cutPlane = d.Vertical ? planes[0] : PlanFillPlane(planes[0]);
            var groups = SectionFillLoops(near, cutPlane, d.Vertical, tol, null, wallSolids, members);
            var sheetGroups = new List<List<Curve>>();
            try
            {
                foreach (var group in groups ?? new List<List<Curve>>())
                {
                    var sheetGroup = new List<Curve>();
                    foreach (var loop in group ?? new List<Curve>())
                    {
                        if (loop == null) continue;
                        var frame = CurvePoints(loop, tol).Select(p => DetailFrame(d, p)).ToList();
                        foreach (var part in keep)
                        {
                            var ring = DetailClip.Ring(frame, part.Rect);
                            if (ring.Count < 3) continue;
                            breakEdges.AddRange(DetailClip.CropEdges(ring, part.Rect, Math.Max(tol, 0.5)).Select(e =>
                                new KeyValuePair<Pt, Pt>(new Pt(e.Key.X + part.Du, e.Key.Y), new Pt(e.Value.X + part.Du, e.Value.Y))));
                            ring = ring.Select(p => new Pt(p.X + part.Du, p.Y)).ToList();
                            rings.Add(ring);
                            var closed = ring.Select(Sheet).ToList();
                            closed.Add(closed[0]);
                            sheetGroup.Add(new PolylineCurve(closed));
                        }
                    }
                    if (sheetGroup.Count > 0) sheetGroups.Add(sheetGroup);
                }
                fills = BakeSectionFills(doc, layer, layer.Name, sheetGroups, tol, ref index, ref box, null, null, scale);
            }
            finally
            {
                DisposeFillGroups(groups);
                DisposeFillGroups(sheetGroups);
            }
        }

        foreach (var edge in breakEdges)
        {
            var line = DetailClip.BreakLine(edge.Key, edge.Value, scale);
            using (var curve = new PolylineCurve(line.Select(Sheet)))
                added += AddStroke(doc, layer, curve, PenThin, scale, false, pattern, tol,
                    "break_line", null, null, null, ref box, ref index, ref count, stamp);
        }

        var chains = d.View == Details.Plan ? DetailDims.PlanChains(d.Facts, d) : new List<PlanDims.FixedChain>();
        foreach (var chain in chains)
            chain.Origin = new Pt(chain.Origin.X + shift.X, chain.Origin.Y + shift.Y);
        var walls = rings.Select(r => new List<List<Pt>> { r.Select(p => new Pt(p.X + shift.X, p.Y + shift.Y)).ToList() }).ToList();
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        Func<string, double> measure = text =>
        {
            if (!widths.TryGetValue(text, out var paper))
                widths[text] = paper = ModelTextWidth(doc, text, PlanDims.TextMm * scale) / scale;
            return paper;
        };
        foreach (var chain in PlanDims.LayoutFixed(chains, scale, new List<PlanDims.Obstacle>(), walls, measure))
        {
            if (!chain.Placed)
            {
                notes.Add(d.Title + ": " + chain.Kind + " not placed");
                continue;
            }
            var stamps = new Dictionary<string, string>(stamp.Extra)
            {
                ["forsk:dim_chain"] = chain.Id,
                ["forsk:dim_kind"] = chain.Kind
            };
            var dimStamp = new SymbolStamp { Extra = stamps };
            foreach (var line in chain.Lines)
            {
                using (var curve = new LineCurve(DrawingPoint(line.A), DrawingPoint(line.B)))
                    added += AddStroke(doc, layer, curve, PenThin, scale, false, pattern, tol,
                        "dimension", "line", null, null, ref box, ref index, ref count, dimStamp);
            }
            // Ticks keep the plan's weight: annotation does not scale.
            foreach (var tick in chain.Ticks)
            {
                using (var curve = new LineCurve(DrawingPoint(tick.A), DrawingPoint(tick.B)))
                    added += AddStroke(doc, layer, curve, baseProfile.Silhouette, scale, false, pattern, tol,
                        "dimension", "tick", null, null, ref box, ref index, ref count, dimStamp);
            }
            foreach (var label in chain.Texts)
            {
                var values = new Dictionary<string, string>(stamps)
                {
                    ["forsk:dim_value"] = label.Value.ToString(CultureInfo.InvariantCulture),
                    ["forsk:dim_total"] = chain.Total.ToString(CultureInfo.InvariantCulture)
                };
                if (AddDimensionText(doc, layer, label, scale, values, ref box, ref index, ref count)) added++;
                if (!label.Underline) continue;
                // Not to scale (ISO 129-1): a line under the value.
                var reading = new Vector3d(label.Reading.X, label.Reading.Y, 0);
                var under = DrawingPoint(label.Centre) - new Vector3d(-reading.Y, reading.X, 0) * (label.Height / 2.0 + PlanDims.TextPadMm * scale / 2.0);
                using (var curve = new LineCurve(under - reading * (label.Width / 2.0), under + reading * (label.Width / 2.0)))
                    added += AddStroke(doc, layer, curve, PenThin, scale, false, pattern, tol,
                        "dimension", "underline", null, null, ref box, ref index, ref count, dimStamp);
            }
        }
        return fills;
    }

    /// <summary>The source's box meets the drawing's crop, in the frame.</summary>
    private static bool MeetsCrop(RhinoObject obj, Details.Drawing d, DetailClip.Rect rect)
    {
        var bbox = obj?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (!bbox.IsValid) return false;
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
        foreach (var corner in bbox.GetCorners())
        {
            var p = DetailFrame(d, corner);
            u0 = Math.Min(u0, p.X);
            u1 = Math.Max(u1, p.X);
            v0 = Math.Min(v0, p.Y);
            v1 = Math.Max(v1, p.Y);
        }
        if (!d.Vertical && d.Clipped && bbox.Min.Z > d.CutZ) return false;
        return u1 >= rect.U0 && u0 <= rect.U1 && v1 >= rect.V0 && v0 <= rect.V1;
    }

    /// <summary>
    /// HLD drawing coordinates to the frame: an affine map read off three
    /// model points of the frame, inverted in 2D. False when it does not invert.
    /// </summary>
    private static bool TryHldToFrame(Details.Drawing d, Transform worldToHld, out Func<Point3d, Pt> toFrame)
    {
        toFrame = null;
        if (!worldToHld.IsValid) return false;
        var x = new Vector3d(d.X.X, d.X.Y, 0);
        var y = d.Vertical ? Vector3d.ZAxis : new Vector3d(d.Y.X, d.Y.Y, 0);
        var o = Point3d.Origin;
        var ou = o + x;
        var ov = o + y;
        o.Transform(worldToHld);
        ou.Transform(worldToHld);
        ov.Transform(worldToHld);
        double a = ou.X - o.X, b = ov.X - o.X, c = ou.Y - o.Y, e = ov.Y - o.Y;
        var det = a * e - b * c;
        if (Math.Abs(det) < 1e-12) return false;
        var origin = o;
        toFrame = p =>
        {
            var px = p.X - origin.X;
            var py = p.Y - origin.Y;
            return new Pt((e * px - b * py) / det, (-c * px + a * py) / det);
        };
        return true;
    }

    private static List<Point3d> CurvePoints(Curve curve, double tol)
    {
        if (curve.TryGetPolyline(out var polyline)) return polyline.ToList();
        using (var approx = curve.ToPolyline(0, 0, 0.1, 0, 0, Math.Max(tol, 0.01), 0, 0, true))
            return approx != null && approx.TryGetPolyline(out var pl) ? pl.ToList() : new List<Point3d> { curve.PointAtStart, curve.PointAtEnd };
    }

    /// <summary>A line's runs inside the crop, less every piece along the crop; clipped counts pieces the 2D clip shortened.</summary>
    private static List<List<Pt>> ClipRuns(List<Pt> points, DetailClip.Rect rect, double tol, ref int clipped)
    {
        var runs = new List<List<Pt>>();
        List<Pt> run = null;
        var edge = Math.Max(tol, 0.5);
        for (var i = 0; i + 1 < points.Count; i++)
        {
            if (!DetailClip.Segment(points[i], points[i + 1], rect, out var a, out var b)
                || DetailClip.OnCrop(a, b, rect, edge))
            {
                run = null;
                continue;
            }
            if (Distance(a, points[i]) > edge || Distance(b, points[i + 1]) > edge) clipped++;
            if (run == null || Distance(run[run.Count - 1], a) > edge)
            {
                run = new List<Pt> { a };
                runs.Add(run);
            }
            run.Add(b);
        }
        return runs;
    }

    private static double Distance(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
