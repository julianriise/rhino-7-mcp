using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Plan symbols and line weights at print. Marks come from the opening
/// record. Nothing is stored on the marker. Frames stay out of the plan cut.
/// </summary>
public partial class RhinoMCPFunctions
{
    private struct PlanStats
    {
        public int Symbols;
        public int Arcs;
        public int Dashed;
        public int Roof;
        public int Rooms;
        public int Skipped;
        public string Note;
        public string RoomText;
    }

    private const double PlanCutMm = 0.50;
    private const double PlanBeyondMm = 0.18;
    private const double PlanThinMm = 0.13;

    /// <summary>
    /// Replace plan centre-lines with width ribbons, then add symbols, the
    /// dashed roof outline, and room tags. Returns false when nothing was
    /// added so the caller keeps the v1 curves.
    /// </summary>
    private bool TryBakePlanLinework(
        RhinoDoc doc,
        Layer layer,
        int scale,
        double cutZ,
        Transform worldToHld,
        Vector3d delta,
        List<WeightedCurve> visible,
        List<List<Curve>> fillGroups,
        double tolerance,
        ref BoundingBox box,
        ref int index,
        ref int count,
        out PlanStats stats)
    {
        stats = new PlanStats();
        if (doc == null || layer == null || scale < 1) return false;
        var pattern = SolidPatternIndex(doc);
        if (pattern < 0) return false;
        var tol = tolerance > 0 ? tolerance : 0.01;
        var added = 0;

        var loops = new List<Curve>();
        if (fillGroups != null)
        {
            foreach (var group in fillGroups)
            {
                if (group == null) continue;
                foreach (var curve in group)
                    if (curve != null) loops.Add(curve);
            }
        }

        var openings = PlanOpeningRects(doc, cutZ, worldToHld, delta, tol);
        if (visible != null)
        {
            foreach (var item in visible)
            {
                var curve = item.Curve;
                if (curve == null || !curve.IsValid) continue;
                if (LiesOnLoops(curve, loops, 2.0)) continue;
                if (InsideOpening(curve, openings, tol)) continue;
                added += AddStroke(doc, layer, curve, PlanBeyondMm, scale, false, pattern, tol,
                    "greyscale", null, null, null, ref box, ref index, ref count);
            }
        }

        foreach (var loop in loops)
        {
            added += AddStroke(doc, layer, loop, PlanCutMm, scale, false, pattern, tol,
                "cut", null, null, null, ref box, ref index, ref count);
        }

        added += BakeOpeningSymbols(
            doc, layer, scale, cutZ, worldToHld, delta, pattern, tol,
            ref box, ref index, ref count, ref stats);
        added += BakeRoofOutlines(
            doc, layer, scale, worldToHld, delta, pattern, tol,
            ref box, ref index, ref count, ref stats);
        added += BakeRoomTags(
            doc, layer, scale, worldToHld, delta, ref box, ref index, ref count, ref stats);

        foreach (var rect in openings)
            rect?.Dispose();
        if (stats.Skipped > 0)
        {
            stats.Note = stats.Skipped.ToString(CultureInfo.InvariantCulture)
                + (stats.Skipped == 1 ? " opening had no symbol." : " openings had no symbol.");
        }
        return added > 0;
    }

    private int BakeOpeningSymbols(
        RhinoDoc doc,
        Layer layer,
        int scale,
        double cutZ,
        Transform worldToHld,
        Vector3d delta,
        int pattern,
        double tol,
        ref BoundingBox box,
        ref int index,
        ref int count,
        ref PlanStats stats)
    {
        var added = 0;
        foreach (var marker in EnumerateDocObjects(doc))
        {
            if (marker?.Attributes == null) continue;
            if (!string.Equals(GetForskKind(marker), "opening_marker", StringComparison.OrdinalIgnoreCase))
                continue;
            List<OpeningTypes.PlanMark> marks = null;
            Plane plane = Plane.Unset;
            OpeningTypes.PlanFrame frame = null;
            try
            {
                if (!TrySymbolFrame(doc, marker, out var record, out plane, out frame))
                {
                    stats.Skipped++;
                    continue;
                }
                frame.CutZ = cutZ;
                marks = OpeningTypes.PlanSymbol(record, "1:100", frame);
            }
            catch (Exception)
            {
                stats.Skipped++;
                continue;
            }
            if (marks == null || marks.Count == 0)
            {
                stats.Skipped++;
                continue;
            }

            var baked = 0;
            var markerId = marker.Id.ToString();
            foreach (var mark in marks)
            {
                Curve curve = null;
                try
                {
                    curve = MarkCurve(mark, plane, worldToHld, delta);
                    if (curve == null) continue;
                    var n = AddStroke(doc, layer, curve, PlanThinMm, scale, mark.Dashed, pattern, tol,
                        "symbol", mark.Part, markerId,
                        mark.Part == "leaf" || mark.Part == "arc" ? mark.Y1 * frame.YInward : (double?)null,
                        ref box, ref index, ref count);
                    curve.Dispose();
                    curve = null;
                    baked += n;
                    if (n > 0 && mark.Shape == "arc") stats.Arcs++;
                    if (n > 0 && mark.Dashed) stats.Dashed++;
                }
                catch (Exception)
                {
                    curve?.Dispose();
                }
            }
            if (baked > 0)
            {
                stats.Symbols++;
                added += baked;
            }
            else
                stats.Skipped++;
        }
        return added;
    }

    private bool TrySymbolFrame(
        RhinoDoc doc,
        RhinoObject marker,
        out OpeningTypes.Record record,
        out Plane plane,
        out OpeningTypes.PlanFrame frame)
    {
        record = null;
        plane = Plane.Unset;
        frame = null;
        var kind = marker.Attributes.GetUserString("forsk:opening_kind");
        record = ResolvedOpeningStyle(doc, marker.Id, kind);
        if (record == null) return false;
        var width = ParseMm(marker.Attributes.GetUserString("forsk:width")) ?? 0;
        var sill = ParseMm(marker.Attributes.GetUserString("forsk:sill")) ?? 0;
        var head = ParseMm(marker.Attributes.GetUserString("forsk:head")) ?? 0;
        if (width <= 1 || head <= sill) return false;

        var hostRaw = marker.Attributes.GetUserString("forsk:host");
        if (!Guid.TryParse(hostRaw, out var hostId)) return false;
        var host = doc.Objects.FindId(hostId);
        if (host == null) return false;
        var thickness = ParseMm(host.Attributes?.GetUserString("forsk:thickness")) ?? 0;
        if (thickness < 40) return false;

        var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (!box.IsValid) return false;
        var center = box.Center;
        center.Z = 0;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var segs = SegmentsFromPath(host.Attributes?.GetUserString("forsk:path"), tol);
        if (!TryOffsetOnSegments(segs, center, out var segment, out _, out _) || segment == null)
            return false;
        var on = new Line(segment.Start, segment.End).ClosestPoint(center, true);
        center = new Point3d(on.X, on.Y, 0);

        var widthDir = segment.Tangent;
        widthDir.Z = 0;
        if (!widthDir.Unitize()) return false;
        var inward = segment.Inward;
        inward.Z = 0;
        if (!inward.Unitize()) return false;
        if (!TryOpeningPlane(center, widthDir, inward, out plane)) return false;
        OpeningFacing(plane, inward, out var yInward, out var xLeft);

        var outerHalf = width * 0.5 + FacadeConst.Pad - OpeningFrameInsetMm;
        var halfThick = thickness * 0.5 - OpeningFrameInsetMm;
        var clear = head - sill - (2.0 * OpeningFrameInsetMm);
        if (clear < 80 || outerHalf < 30 || halfThick < 8) return false;
        var face = Math.Min(OpeningFrameFaceMm, Math.Min(outerHalf * 0.4, clear * 0.22));
        if (face < 12) face = 12;
        var innerHalf = outerHalf - face;
        if (innerHalf < 15) return false;

        frame = new OpeningTypes.PlanFrame
        {
            OuterHalf = outerHalf,
            InnerHalf = innerHalf,
            HalfThick = halfThick,
            Sill = sill,
            Head = head,
            YInward = yInward,
            XLeft = xLeft
        };
        return true;
    }

    private static Curve MarkCurve(OpeningTypes.PlanMark mark, Plane plane, Transform worldToHld, Vector3d delta)
    {
        if (mark == null) return null;
        if (mark.Shape == "arc" && mark.Radius > 1)
        {
            var start = MapPlan(mark.X0, mark.Y0, plane, worldToHld, delta);
            var end = MapPlan(mark.X1, mark.Y1, plane, worldToHld, delta);
            var midVec = new Vector3d(mark.X0 - mark.Cx, mark.Y0 - mark.Cy, 0)
                + new Vector3d(mark.X1 - mark.Cx, mark.Y1 - mark.Cy, 0);
            if (midVec.Unitize())
            {
                var mid = MapPlan(
                    mark.Cx + (midVec.X * mark.Radius),
                    mark.Cy + (midVec.Y * mark.Radius),
                    plane, worldToHld, delta);
                var arc = new Arc(start, mid, end);
                if (arc.IsValid) return new ArcCurve(arc);
            }
        }
        var a = MapPlan(mark.X0, mark.Y0, plane, worldToHld, delta);
        var b = MapPlan(mark.X1, mark.Y1, plane, worldToHld, delta);
        if (a.DistanceTo(b) < 0.5) return null;
        return new LineCurve(a, b);
    }

    private static Point3d MapPlan(double x, double y, Plane plane, Transform worldToHld, Vector3d delta)
    {
        var point = plane.Origin + (plane.XAxis * x) + (plane.YAxis * y);
        point.Z = 0;
        if (worldToHld.IsValid && !worldToHld.IsIdentity)
            point.Transform(worldToHld);
        point += delta;
        point.Z = 0;
        return point;
    }

    private int BakeRoofOutlines(
        RhinoDoc doc,
        Layer layer,
        int scale,
        Transform worldToHld,
        Vector3d delta,
        int pattern,
        double tol,
        ref BoundingBox box,
        ref int index,
        ref int count,
        ref PlanStats stats)
    {
        var added = 0;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "roof", StringComparison.OrdinalIgnoreCase))
                continue;
            var geom = obj.Geometry?.Duplicate();
            if (geom == null) continue;
            try
            {
                var bbox = geom.GetBoundingBox(true);
                if (!bbox.IsValid) continue;
                var z = (bbox.Min.Z + bbox.Max.Z) * 0.5;
                var cut = new Plane(new Point3d(0, 0, z), Vector3d.ZAxis);
                var group = new List<Curve>();
                ContourMass(geom, cut, tol, group);
                foreach (var curve in group)
                {
                    if (curve == null) continue;
                    if (worldToHld.IsValid && !curve.Transform(worldToHld))
                    {
                        curve.Dispose();
                        continue;
                    }
                    curve.Translate(delta);
                    var n = AddStroke(doc, layer, curve, PlanThinMm, scale, true, pattern, tol,
                        "roof_outline", null, null, null, ref box, ref index, ref count);
                    curve.Dispose();
                    if (n > 0) stats.Roof += n;
                    added += n;
                }
            }
            finally
            {
                geom.Dispose();
            }
        }
        return added;
    }

    private int BakeRoomTags(
        RhinoDoc doc,
        Layer layer,
        int scale,
        Transform worldToHld,
        Vector3d delta,
        ref BoundingBox box,
        ref int index,
        ref int count,
        ref PlanStats stats)
    {
        var added = 0;
        var texts = new List<string>();
        var height = 2.5 * scale;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), "room", StringComparison.OrdinalIgnoreCase))
                continue;
            var area = ParseMm(obj.Attributes?.GetUserString("forsk:area"));
            if (!area.HasValue || area.Value <= 0) continue;
            var bbox = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!bbox.IsValid) continue;
            var origin = bbox.Center;
            origin.Z = 0;
            if (worldToHld.IsValid && !worldToHld.IsIdentity)
                origin.Transform(worldToHld);
            origin += delta;
            origin.Z = 0;

            var name = RoomLabelInside(doc, obj, bbox);
            if (!string.IsNullOrEmpty(name))
            {
                var nameAt = origin + new Vector3d(0, height * 1.15, 0);
                if (AddPlanText(doc, layer, name, nameAt, height, "room_tag", ref box, ref index, ref count))
                    added++;
            }
            var line = OpeningTypes.RoomTag(area.Value);
            if (AddPlanText(doc, layer, line, origin, height, "room_tag", ref box, ref index, ref count))
            {
                added++;
                stats.Rooms++;
                texts.Add(line);
            }
        }
        if (texts.Count > 0)
            stats.RoomText = string.Join(" | ", texts.ToArray());
        return added;
    }

    private static string RoomLabelInside(RhinoDoc doc, RhinoObject room, BoundingBox bbox)
    {
        if (doc == null || !bbox.IsValid) return null;
        var midZ = (bbox.Min.Z + bbox.Max.Z) * 0.5;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null) continue;
            var layer = doc.Layers[obj.Attributes.LayerIndex];
            var name = layer?.Name ?? "";
            if (!name.Equals("label", StringComparison.OrdinalIgnoreCase)) continue;
            if (!(obj.Geometry is TextEntity text)) continue;
            var point = text.Plane.Origin;
            if (point.X < bbox.Min.X || point.X > bbox.Max.X) continue;
            if (point.Y < bbox.Min.Y || point.Y > bbox.Max.Y) continue;
            if (room.Geometry is Brep brep)
            {
                if (!brep.IsPointInside(new Point3d(point.X, point.Y, midZ), 1.0, false))
                    continue;
            }
            var plain = text.PlainText;
            if (string.IsNullOrWhiteSpace(plain)) continue;
            return plain.Trim();
        }
        return null;
    }

    private static bool AddPlanText(
        RhinoDoc doc,
        Layer layer,
        string text,
        Point3d origin,
        double height,
        string role,
        ref BoundingBox box,
        ref int index,
        ref int count)
    {
        if (string.IsNullOrWhiteSpace(text) || height <= 0) return false;
        var plane = Plane.WorldXY;
        plane.Origin = origin;
        var stableId = FormatStableId("d", index);
        var attr = DrawAttr(layer, stableId, role, null, null);
        Guid id;
        try
        {
            id = doc.Objects.AddText(text, plane, height, "Arial", false, false, attr);
        }
        catch (Exception)
        {
            return false;
        }
        if (id == Guid.Empty) return false;
        index++;
        count++;
        box.Union(new BoundingBox(origin, origin + new Vector3d(height * text.Length * 0.6, height, 0)));
        return true;
    }

    private List<Curve> PlanOpeningRects(
        RhinoDoc doc, double cutZ, Transform worldToHld, Vector3d delta, double tol)
    {
        var rects = new List<Curve>();
        foreach (var marker in EnumerateDocObjects(doc))
        {
            if (marker?.Attributes == null) continue;
            if (!string.Equals(GetForskKind(marker), "opening_marker", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TrySymbolFrame(doc, marker, out _, out var plane, out var frame)) continue;
            frame.CutZ = cutZ;
            Point3d Corner(double x, double y) => MapPlan(x, y, plane, worldToHld, delta);
            var poly = new PolylineCurve(new List<Point3d>
            {
                Corner(-frame.InnerHalf, -frame.HalfThick),
                Corner(frame.InnerHalf, -frame.HalfThick),
                Corner(frame.InnerHalf, frame.HalfThick),
                Corner(-frame.InnerHalf, frame.HalfThick),
                Corner(-frame.InnerHalf, -frame.HalfThick)
            });
            if (poly.IsValid) rects.Add(poly);
            else poly.Dispose();
        }
        return rects;
    }

    private static bool InsideOpening(Curve curve, List<Curve> rects, double tol)
    {
        if (curve == null || rects == null || rects.Count == 0) return false;
        var mid = Mid(curve);
        foreach (var rect in rects)
        {
            if (rect == null) continue;
            var hit = rect.Contains(mid, Plane.WorldXY, Math.Max(tol, 0.1));
            if (hit == PointContainment.Inside) return true;
        }
        return false;
    }

    private static bool LiesOnLoops(Curve curve, List<Curve> loops, double maxDist)
    {
        if (curve == null || loops == null || loops.Count == 0) return false;
        var points = new[] { curve.PointAtStart, Mid(curve), curve.PointAtEnd };
        foreach (var loop in loops)
        {
            if (loop == null) continue;
            var on = true;
            foreach (var point in points)
            {
                if (DistanceTo(loop, point) > maxDist)
                {
                    on = false;
                    break;
                }
            }
            if (on) return true;
        }
        return false;
    }

    private static double DistanceTo(Curve curve, Point3d point)
    {
        double t;
        if (!curve.ClosestPoint(point, out t)) return double.MaxValue;
        return curve.PointAt(t).DistanceTo(point);
    }

    private static Point3d Mid(Curve curve)
    {
        double t;
        if (curve.LengthParameter(curve.GetLength() * 0.5, out t))
            return curve.PointAt(t);
        return curve.PointAt(curve.Domain.Mid);
    }

    private int AddStroke(
        RhinoDoc doc,
        Layer layer,
        Curve curve,
        double paperMm,
        int scale,
        bool dashed,
        int pattern,
        double tol,
        string role,
        string part,
        string markerId,
        double? openY,
        ref BoundingBox box,
        ref int index,
        ref int count)
    {
        if (curve == null || paperMm <= 0 || scale < 1) return 0;
        // A swing arc ribbon fills the sector. Draw the arc as a thin curve.
        if (part == "arc")
        {
            var stableId = FormatStableId("d", index);
            var attr = DrawAttr(layer, stableId, role, part, markerId, openY, dashed);
            attr.PlotWeight = paperMm;
            Guid id;
            try { id = doc.Objects.AddCurve(curve, attr); }
            catch (Exception) { id = Guid.Empty; }
            if (id == Guid.Empty) return 0;
            index++;
            count++;
            var arcBox = curve.GetBoundingBox(true);
            if (arcBox.IsValid) box.Union(arcBox);
            return 1;
        }
        var width = paperMm * scale;
        var added = 0;
        if (!dashed)
        {
            added += AddRibbon(doc, layer, curve, width, pattern, tol, role, part, markerId, openY, dashed, ref box, ref index, ref count);
            return added;
        }

        var length = curve.GetLength();
        if (length < 1) return 0;
        var dash = 12.0 * paperMm * scale;
        var gap = 3.0 * paperMm * scale;
        if (dash < 1) dash = 1;
        var cursor = 0.0;
        var on = true;
        while (cursor < length - 0.2)
        {
            var take = on ? dash : gap;
            var next = Math.Min(length, cursor + take);
            if (on && next - cursor > 0.4)
            {
                double t0;
                double t1;
                if (curve.LengthParameter(cursor, out t0) && curve.LengthParameter(next, out t1) && t1 > t0)
                {
                    Curve piece = null;
                    try { piece = curve.Trim(t0, t1); }
                    catch (Exception) { piece = null; }
                    if (piece != null)
                    {
                        added += AddRibbon(doc, layer, piece, width, pattern, tol, role, part, markerId, openY, true, ref box, ref index, ref count);
                        piece.Dispose();
                    }
                }
            }
            on = !on;
            cursor = next;
        }
        return added;
    }

    private static int AddRibbon(
        RhinoDoc doc,
        Layer layer,
        Curve curve,
        double width,
        int pattern,
        double tol,
        string role,
        string part,
        string markerId,
        double? openY,
        bool dashed,
        ref BoundingBox box,
        ref int index,
        ref int count)
    {
        var half = width * 0.5;
        if (half <= 0 || curve == null) return 0;
        var samples = RibbonSamples(curve, tol);
        if (samples.Count < 2) return 0;
        var left = new List<Point3d>();
        var right = new List<Point3d>();
        for (var i = 0; i < samples.Count; i++)
        {
            Vector3d tan;
            if (i == 0) tan = samples[1] - samples[0];
            else if (i == samples.Count - 1) tan = samples[i] - samples[i - 1];
            else tan = samples[i + 1] - samples[i - 1];
            tan.Z = 0;
            if (!tan.Unitize()) continue;
            var normal = new Vector3d(-tan.Y, tan.X, 0);
            left.Add(samples[i] + (normal * half));
            right.Add(samples[i] - (normal * half));
        }
        if (left.Count < 2) return 0;
        var pts = new List<Point3d>();
        pts.AddRange(left);
        for (var i = right.Count - 1; i >= 0; i--)
            pts.Add(right[i]);
        pts.Add(pts[0]);
        var boundary = new PolylineCurve(pts);
        Hatch[] hatches = null;
        try
        {
            hatches = Hatch.Create(new List<Curve> { boundary }, pattern, 0.0, 1.0, Math.Max(tol, 0.01));
        }
        catch (Exception)
        {
            hatches = null;
        }
        boundary.Dispose();
        if (hatches == null) return 0;
        var added = 0;
        foreach (var hatch in hatches)
        {
            if (hatch == null) continue;
            var stableId = FormatStableId("d", index);
            var attr = DrawAttr(layer, stableId, role, part, markerId, openY, dashed);
            Guid id;
            try { id = doc.Objects.AddHatch(hatch, attr); }
            catch (Exception) { id = Guid.Empty; }
            var hatchBox = hatch.GetBoundingBox(true);
            var area = 0.0;
            try
            {
                var props = AreaMassProperties.Compute(hatch);
                if (props != null) area = props.Area;
            }
            catch (Exception)
            {
                area = 0;
            }
            hatch.Dispose();
            var expect = Math.Max(curve.GetLength(), 1.0) * width;
            if (id != Guid.Empty && area > expect * 8.0 && area > width * width * 4.0)
            {
                try { doc.Objects.Delete(id, true); } catch (Exception) { }
                id = Guid.Empty;
            }
            if (id == Guid.Empty) continue;
            index++;
            count++;
            if (hatchBox.IsValid) box.Union(hatchBox);
            added++;
        }
        return added;
    }

    private static List<Point3d> RibbonSamples(Curve curve, double tol)
    {
        var pts = new List<Point3d>();
        if (curve == null) return pts;
        var length = curve.GetLength();
        if (length < 0.2) return pts;
        var linear = false;
        try { linear = curve.IsLinear(Math.Max(tol, 0.1)); }
        catch (Exception) { linear = false; }
        if (linear || length < 80)
        {
            pts.Add(curve.PointAtStart);
            pts.Add(curve.PointAtEnd);
            return pts;
        }
        var steps = (int)Math.Ceiling(length / 80.0);
        if (steps < 2) steps = 2;
        if (steps > 32) steps = 32;
        for (var i = 0; i <= steps; i++)
        {
            double t;
            if (!curve.LengthParameter(length * i / steps, out t)) continue;
            pts.Add(curve.PointAt(t));
        }
        return pts;
    }

    private static ObjectAttributes DrawAttr(
        Layer layer, string stableId, string role, string part, string markerId, double? openY = null, bool dashed = false)
    {
        var attr = new ObjectAttributes
        {
            LayerIndex = layer.Index,
            Name = stableId,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = Color.Black,
            PlotColorSource = ObjectPlotColorSource.PlotColorFromObject,
            PlotColor = Color.Black,
            PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject,
            PlotWeight = PlanThinMm,
            DisplayOrder = string.Equals(role, "symbol", StringComparison.Ordinal) ? 3 : 2
        };
        StampForskTags(attr, new ForskStamp
        {
            Kind = "drawing",
            Level = "0",
            Id = stableId,
            View = "plan",
            MarkerId = markerId
        });
        if (!string.IsNullOrEmpty(role))
            attr.SetUserString("forsk:role", role);
        if (!string.IsNullOrEmpty(part))
            attr.SetUserString("forsk:symbol", part);
        if (dashed)
            attr.SetUserString("forsk:dashed", "1");
        if (openY.HasValue)
            attr.SetUserString("forsk:open_y", openY.Value.ToString("0.###", CultureInfo.InvariantCulture));
        return attr;
    }
}
