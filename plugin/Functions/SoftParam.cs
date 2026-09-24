using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Param records live on the baked wall and its opening markers.
/// rebuild_host_wall re-extrudes that one wall from forsk:path and recuts
/// its openings. Floor, roof, rooms, and the other walls stay.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string MissingWallPathMessage =
        "Wall has no param path. Bake the wall again before rebuilding this host.";

    private sealed class PlannedOpening
    {
        public RhinoObject Marker;
        public OpeningSpec Spec;
        public Placement Placement;
        public double T;
        public double Offset;
    }

    [McpCommand("rebuild_host_wall")]
    public JObject RebuildHostWall(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var host = ResolveRebuildHost(parameters ?? new JObject());
        var path = host.Attributes?.GetUserString("forsk:path");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(MissingWallPathMessage);

        var height = ParseMm(host.Attributes?.GetUserString("forsk:height"));
        if (!height.HasValue || height.Value <= 0)
            throw new InvalidOperationException(
                "Wall has no param height. Bake the wall again before rebuilding this host.");

        var warnings = new JArray();
        var freshParts = ExtrudeFromPath(path, height.Value, tol, warnings);
        if (freshParts.Count != 1)
            throw new InvalidOperationException("Could not rebuild host wall as one solid.");

        var forskId = host.Attributes?.GetUserString("forsk:id");
        var level = host.Attributes?.GetUserString("forsk:level");
        if (string.IsNullOrEmpty(level)) level = "0";
        var markers = MarkersOnHost(doc, host.Id, forskId);
        var segs = SegmentsFromPath(path, tol);
        if (markers.Count > 0 && segs.Count == 0)
            throw new InvalidOperationException("Wall is too short for this opening.");

        var planned = new List<PlannedOpening>();
        foreach (var marker in markers)
        {
            var rec = ReadOpeningRecord(marker);
            var spec = new OpeningSpec
            {
                Kind = rec.Kind,
                Width = rec.Width,
                Sill = rec.Sill,
                Head = rec.Head
            };
            var storedOffset = ParseMm(marker.Attributes?.GetUserString("forsk:offset"));
            double offset;
            if (storedOffset.HasValue)
                offset = storedOffset.Value;
            else if (!TryOffsetOnSegments(segs, rec.MarkerBbox.Center, out _, out _, out offset))
                throw new InvalidOperationException("Could not place an opening on the host path.");

            var placement = PlaceOpeningOnPath(segs, spec, offset);
            planned.Add(new PlannedOpening
            {
                Marker = marker,
                Spec = spec,
                Placement = placement,
                T = placement.T,
                Offset = OffsetAlong(segs, placement)
            });
        }

        var cutters = new List<Brep>();
        foreach (var item in planned)
        {
            var cutter = BuildOpeningCutter(
                item.Placement.Foot,
                item.Spec.Sill,
                item.Spec.Head,
                FacadeConst.Pad,
                FacadeConst.MinDepth,
                tol);
            if (cutter == null || !cutter.IsValid)
                throw new InvalidOperationException("Could not cut opening.");
            cutters.Add(cutter);
        }

        var rebuilt = CutOpeningsFromFresh(freshParts[0], cutters, tol);
        if (rebuilt == null)
            throw new InvalidOperationException("Could not cut opening.");
        if (!TryReplaceWallBrep(doc, host, rebuilt))
            throw new InvalidOperationException("Could not rebuild host wall as one solid.");

        var thickness = MedianThickness(segs);
        if (thickness <= 0)
            thickness = ParseMm(host.Attributes?.GetUserString("forsk:thickness")) ?? 0;
        SyncWallRecord(doc, host.Id, new ForskStamp
        {
            Kind = "wall",
            Level = level,
            Id = forskId,
            Height = height.Value,
            Thickness = thickness > 0 ? (double?)thickness : null,
            Path = path,
            SourceLayer = host.Attributes?.GetUserString("forsk:source_layer")
        });

        var markerIds = new JArray();
        var blockIds = new JArray();
        foreach (var item in planned)
        {
            try
            {
                var markerBrep = BuildOpeningMarkerBox(
                    item.Placement.Foot,
                    item.Spec.Sill,
                    item.Spec.Head,
                    FacadeConst.MarkerSelectDepth);
                if (markerBrep == null || !doc.Objects.Replace(item.Marker.Id, markerBrep))
                {
                    warnings.Add("Opening marker not found.");
                    continue;
                }

                StampOpeningHostId(doc, item.Marker.Id, host.Id);
                WriteOpeningParams(doc, item.Marker.Id, item.T, item.Offset);
                DeleteOpeningBlocks(doc, item.Marker.Id);
                var blockId = AddOpeningBlock(
                    doc,
                    item.Marker.Id,
                    item.Marker.Name,
                    host.Id,
                    KindToTag(item.Spec.Kind),
                    item.Placement.Foot,
                    item.Spec.Sill,
                    item.Spec.Head,
                    item.Spec.Width,
                    FacadeConst.Pad,
                    item.Marker.Attributes?.GetUserString("forsk:source_layer"),
                    host.Brep,
                    item.Placement.Segment);
                if (blockId != Guid.Empty)
                {
                    StampOpeningHostId(doc, blockId, host.Id);
                    WriteOpeningParams(doc, blockId, item.T, item.Offset);
                    blockIds.Add(blockId.ToString());
                }
                else
                {
                    warnings.Add("Could not rebuild opening frame.");
                }

                markerIds.Add(item.Marker.Id.ToString());
            }
            catch (Exception ex)
            {
                warnings.Add(ex.Message);
            }
        }

        doc.Views.Redraw();
        var label = string.IsNullOrEmpty(forskId) ? host.Attributes?.Name : forskId;
        if (string.IsNullOrEmpty(label)) label = "host wall";
        var noun = planned.Count == 1 ? "opening" : "openings";
        return new JObject
        {
            ["host_id"] = host.Id.ToString(),
            ["forsk_id"] = forskId ?? "",
            ["level"] = level,
            ["height"] = height.Value,
            ["thickness"] = thickness,
            ["path_points"] = PathOuterCount(path),
            ["opening_count"] = planned.Count,
            ["marker_ids"] = markerIds,
            ["block_ids"] = blockIds,
            ["warnings"] = warnings,
            ["ok"] = true,
            ["message"] = $"Rebuilt wall {label} with {planned.Count} {noun}."
        };
    }

    private WallSolid ResolveRebuildHost(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var obj = ResolveFacadeTarget(parameters, "id", expectMarker: false);
        var handle = ResolveOpeningHandle(obj);
        var kind = GetForskKind(handle);
        if (string.Equals(kind, "opening_marker", StringComparison.OrdinalIgnoreCase))
        {
            var rec = ReadOpeningRecord(handle);
            return ReadHostWall(doc, rec.HostId, requireVertical: true);
        }

        if (string.Equals(GetForskKind(obj), "opening", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Opening marker not found.");
        return ReadHostWall(doc, obj.Id, requireVertical: true);
    }

    private static List<RhinoObject> MarkersOnHost(RhinoDoc doc, Guid hostId, string forskId)
    {
        var list = new List<RhinoObject>();
        if (doc == null || hostId == Guid.Empty) return list;
        var hostStr = hostId.ToString();
        foreach (var obj in doc.Objects)
        {
            if (obj?.Attributes == null) continue;
            if (!string.Equals(GetForskKind(obj), "opening_marker", StringComparison.OrdinalIgnoreCase))
                continue;
            var byGuid = string.Equals(
                obj.Attributes.GetUserString("forsk:host"),
                hostStr,
                StringComparison.OrdinalIgnoreCase);
            var byId = !string.IsNullOrEmpty(forskId) && string.Equals(
                obj.Attributes.GetUserString("forsk:host_id"),
                forskId,
                StringComparison.OrdinalIgnoreCase);
            if (byGuid || byId) list.Add(obj);
        }
        return list;
    }

    private void SyncWallRecord(RhinoDoc doc, Guid id, ForskStamp stamp)
    {
        var obj = doc?.Objects.FindId(id);
        if (obj?.Attributes == null) return;
        var attr = obj.Attributes.Duplicate();
        StampForskTags(attr, stamp);
        doc.Objects.ModifyAttributes(id, attr, true);
    }

    private void StampOpeningOnHost(
        RhinoDoc doc,
        WallSolid host,
        Guid markerId,
        Guid blockId,
        Point3d center,
        Placement fallback,
        double tol)
    {
        if (doc == null || markerId == Guid.Empty) return;
        var segs = SegmentsFromPath(host?.Attributes?.GetUserString("forsk:path"), tol);
        if (segs.Count == 0 && host != null)
            segs = ExtractWallSegments(host, tol);

        double t;
        double offset;
        if (segs.Count > 0 && TryOffsetOnSegments(segs, center, out _, out t, out offset))
        {
            // Record the clamped placement when the opening fits the segment.
            try
            {
                var spec = new OpeningSpec
                {
                    Width = LongerXySide(new BoundingBox(center, center))
                };
                var marker = doc.Objects.FindId(markerId);
                var width = ParseMm(marker?.Attributes?.GetUserString("forsk:width"));
                if (width.HasValue && width.Value > 0) spec.Width = width.Value;
                else if (fallback != null) spec.Width = LongerXySide(fallback.Foot.Bbox);
                if (spec.Width > 0)
                {
                    var placed = PlaceOpeningOnPath(segs, spec, offset);
                    t = placed.T;
                    offset = OffsetAlong(segs, placed);
                }
            }
            catch (InvalidOperationException)
            {
                // Keep the projected t. The opening still records where it sits.
            }
        }
        else if (fallback?.Segment != null)
        {
            t = fallback.T;
            offset = fallback.T * fallback.Segment.Length;
        }
        else
        {
            return;
        }

        WriteOpeningParams(doc, markerId, t, offset);
        if (blockId == Guid.Empty)
            blockId = FindOpeningBlock(doc, markerId);
        WriteOpeningParams(doc, blockId, t, offset);
    }

    private static void WriteOpeningParams(RhinoDoc doc, Guid id, double t, double offset)
    {
        if (doc == null || id == Guid.Empty) return;
        var obj = doc.Objects.FindId(id);
        if (obj?.Attributes == null) return;
        obj.Attributes.SetUserString("forsk:t", FormatParamT(t));
        obj.Attributes.SetUserString("forsk:offset", FormatMm(offset));
        obj.CommitChanges();
    }

    private static Guid FindOpeningBlock(RhinoDoc doc, Guid markerId)
    {
        if (doc == null || markerId == Guid.Empty) return Guid.Empty;
        var key = markerId.ToString();
        foreach (var obj in doc.Objects)
        {
            if (obj?.Attributes == null) continue;
            if (!string.Equals(GetForskKind(obj), "opening", StringComparison.OrdinalIgnoreCase))
                continue;
            var mid = obj.Attributes.GetUserString("forsk:marker_id");
            if (string.Equals(mid, key, StringComparison.OrdinalIgnoreCase))
                return obj.Id;
        }
        return Guid.Empty;
    }

    private string EncodeWallPath(Curve outer, IList<Curve> holes, double tol)
    {
        var outerPts = LoopPoints(outer, tol);
        if (outerPts == null) return null;
        var obj = new JObject { ["outer"] = PointsToJson(outerPts) };
        if (holes != null && holes.Count > 0)
        {
            var arr = new JArray();
            foreach (var hole in holes)
            {
                var pts = LoopPoints(hole, tol);
                if (pts == null) continue;
                arr.Add(PointsToJson(pts));
            }
            if (arr.Count > 0) obj["holes"] = arr;
        }
        return obj.ToString(Formatting.None);
    }

    private string EncodePathFromBrep(Brep brep, double tol)
    {
        if (brep == null) return null;
        var bbox = brep.GetBoundingBox(true);
        if (!bbox.IsValid) return null;
        var height = bbox.Max.Z - bbox.Min.Z;
        var z = bbox.Max.Z - Math.Max(FacadeConst.SectionBelowTop, 0.05 * Math.Max(height, 0));
        var closed = ContourClosedCurves(brep, z, tol);
        if (closed.Count == 0)
            closed = ContourClosedCurves(brep, 0.5 * (bbox.Min.Z + bbox.Max.Z), tol);
        if (closed.Count == 0) return null;
        closed = closed
            .OrderByDescending(c => Math.Abs(AreaMassProperties.Compute(c)?.Area ?? 0))
            .ToList();
        var holes = new List<Curve>();
        for (var i = 1; i < closed.Count; i++)
        {
            if (CurveContainsPointOf(closed[0], closed[i], Math.Max(tol, 1.0)))
                holes.Add(closed[i]);
        }
        return EncodeWallPath(closed[0], holes, tol);
    }

    private static bool TryDecodeWallPath(string json, out Curve outer, out List<Curve> holes)
    {
        outer = null;
        holes = new List<Curve>();
        if (string.IsNullOrWhiteSpace(json)) return false;
        JObject obj;
        try { obj = JObject.Parse(json); }
        catch { return false; }
        outer = LoopFromToken(obj["outer"]);
        if (outer == null) return false;
        if (obj["holes"] is JArray arr)
        {
            foreach (var hole in arr)
            {
                var curve = LoopFromToken(hole);
                if (curve != null) holes.Add(curve);
            }
        }
        return true;
    }

    private List<Brep> ExtrudeFromPath(string path, double height, double tol, JArray warnings)
    {
        if (!TryDecodeWallPath(path, out var outer, out var holes))
            return new List<Brep>();
        List<Brep> parts;
        if (holes.Count == 0)
        {
            var one = ExtrudeClosedCurve(outer, height, tol);
            parts = one == null ? new List<Brep>() : new List<Brep> { one };
        }
        else
        {
            parts = WallBandFromParentAndHoles(outer, holes, height, tol, warnings);
        }

        if (parts.Count <= 1) return parts;
        Brep[] joined = null;
        try { joined = Brep.JoinBreps(parts, tol); }
        catch { joined = null; }
        if (joined != null && joined.Length == 1 && joined[0] != null && joined[0].IsValid)
            return new List<Brep> { joined[0] };
        return parts;
    }

    private List<WallSegment> SegmentsFromPath(string path, double tol)
    {
        if (!TryDecodeWallPath(path, out var outer, out var holes))
            return new List<WallSegment>();
        if (holes.Count > 0)
        {
            var inner = holes[0];
            var best = Math.Abs(AreaMassProperties.Compute(inner)?.Area ?? 0);
            for (var i = 1; i < holes.Count; i++)
            {
                var area = Math.Abs(AreaMassProperties.Compute(holes[i])?.Area ?? 0);
                if (area > best)
                {
                    best = area;
                    inner = holes[i];
                }
            }
            return SegmentsFromBand(outer, inner, tol);
        }

        var box = outer.GetBoundingBox(true);
        var dx = box.Max.X - box.Min.X;
        var dy = box.Max.Y - box.Min.Y;
        if (Math.Min(dx, dy) <= 2.0 * FacadeConst.MinDepth)
            return SegmentsFromLinear(box);
        return SegmentsFromOuterOnly(outer, tol);
    }

    private double MeasureRecordedThickness(string path, Brep brep, double tol)
    {
        var segs = SegmentsFromPath(path, tol);
        if (segs.Count == 0 && brep != null)
        {
            segs = ExtractWallSegments(new WallSolid
            {
                Id = Guid.Empty,
                Brep = brep,
                Attributes = new ObjectAttributes()
            }, tol);
        }
        return MedianThickness(segs);
    }

    private static double MedianThickness(List<WallSegment> segs)
    {
        if (segs == null || segs.Count == 0) return 0;
        var vals = new List<double>();
        foreach (var seg in segs)
        {
            if (seg != null && seg.Thickness > 0) vals.Add(seg.Thickness);
        }
        if (vals.Count == 0) return 0;
        vals.Sort();
        return vals[vals.Count / 2];
    }

    private bool TryOffsetOnSegments(
        List<WallSegment> segs,
        Point3d world,
        out WallSegment segment,
        out double t,
        out double offset)
    {
        segment = null;
        t = 0;
        offset = 0;
        if (segs == null || segs.Count == 0) return false;
        var p = new Point3d(world.X, world.Y, 0);
        var bestDist = double.PositiveInfinity;
        foreach (var seg in segs)
        {
            if (seg == null || seg.Length <= 1e-6) continue;
            var closest = new Line(seg.Start, seg.End).ClosestPoint(p, true);
            var dist = closest.DistanceTo(p);
            if (dist < bestDist)
            {
                bestDist = dist;
                segment = seg;
            }
        }

        if (segment == null || bestDist > 2000.0) return false;
        var on = new Line(segment.Start, segment.End).ClosestPoint(p, true);
        t = ProjectT(segment, on);
        if (double.IsNaN(t) || double.IsInfinity(t)) return false;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        double acc = 0;
        foreach (var seg in segs)
        {
            if (ReferenceEquals(seg, segment)) break;
            acc += seg.Length;
        }
        offset = acc + t * segment.Length;
        return true;
    }

    private Placement PlaceOpeningOnPath(List<WallSegment> segs, OpeningSpec spec, double offset)
    {
        try
        {
            return PlaceFromOffset(segs, spec, offset);
        }
        catch (InvalidOperationException)
        {
            return PlaceUnclamped(segs, spec, offset);
        }
    }

    private Placement PlaceFromOffset(List<WallSegment> segs, OpeningSpec spec, double offset)
    {
        if (!TrySegmentAtOffset(segs, offset, out var seg, out var local))
            throw new InvalidOperationException("Wall is too short for this opening.");
        var t = ClampT(seg, spec.Width, local);
        return FootprintAtT(seg, spec, t);
    }

    private static Placement PlaceUnclamped(List<WallSegment> segs, OpeningSpec spec, double offset)
    {
        if (!TrySegmentAtOffset(segs, offset, out var seg, out var local))
            throw new InvalidOperationException("Wall is too short for this opening.");
        return FootprintAtT(seg, spec, local);
    }

    private static bool TrySegmentAtOffset(
        List<WallSegment> segs,
        double offset,
        out WallSegment seg,
        out double local)
    {
        seg = null;
        local = 0;
        if (segs == null || segs.Count == 0) return false;
        if (offset < 0) offset = 0;
        double acc = 0;
        WallSegment last = null;
        double lastAcc = 0;
        foreach (var item in segs)
        {
            if (item == null || item.Length <= 1e-6) continue;
            last = item;
            lastAcc = acc;
            if (offset <= acc + item.Length)
            {
                seg = item;
                local = (offset - acc) / item.Length;
                if (local < 0) local = 0;
                if (local > 1) local = 1;
                return true;
            }
            acc += item.Length;
        }

        if (last == null) return false;
        seg = last;
        local = last.Length > 1e-6 ? (offset - lastAcc) / last.Length : 0;
        if (local < 0) local = 0;
        if (local > 1) local = 1;
        return true;
    }

    private static double OffsetAlong(List<WallSegment> segs, Placement placement)
    {
        if (placement?.Segment == null) return 0;
        double acc = 0;
        if (segs != null)
        {
            foreach (var seg in segs)
            {
                if (ReferenceEquals(seg, placement.Segment))
                    return acc + placement.T * seg.Length;
                if (seg != null) acc += seg.Length;
            }
        }
        return placement.T * placement.Segment.Length;
    }

    private static Brep CutOpeningsFromFresh(Brep wall, List<Brep> cutters, double tol)
    {
        if (wall == null) return null;
        if (cutters == null || cutters.Count == 0) return wall;

        // Each cutter has to remove volume. A miss fails before the wall is replaced.
        var current = wall;
        foreach (var cutter in cutters)
        {
            var next = TryDifference(current, new List<Brep> { cutter }, tol);
            if (next == null) return null;
            if (BrepVolume(next) > BrepVolume(current) - 1.0) return null;
            current = next;
        }
        return current;
    }

    private static Brep TryDifference(Brep wall, List<Brep> cutters, double tol)
    {
        if (wall == null || cutters == null || cutters.Count == 0) return null;
        Brep[] diff;
        try
        {
            diff = Brep.CreateBooleanDifference(new[] { wall }, cutters.ToArray(), tol);
        }
        catch
        {
            return null;
        }

        if (diff == null) return null;
        Brep only = null;
        foreach (var brep in diff)
        {
            if (brep == null || !brep.IsValid) continue;
            if (only != null) return null;
            only = brep;
        }
        return only;
    }

    private static double BrepVolume(Brep brep)
    {
        if (brep == null) return 0;
        var mass = VolumeMassProperties.Compute(brep);
        return mass == null ? 0 : Math.Abs(mass.Volume);
    }

    private static int PathOuterCount(string path)
    {
        try
        {
            var obj = JObject.Parse(path ?? "");
            return (obj["outer"] as JArray)?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double? ParseMm(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    private static List<Point3d> LoopPoints(Curve curve, double tol)
    {
        if (curve == null) return null;
        var dup = curve.DuplicateCurve();
        if (dup == null) return null;
        if (!dup.IsClosed)
        {
            var gap = dup.PointAtStart.DistanceTo(dup.PointAtEnd);
            if (gap <= Math.Max(tol * 10.0, 1.0) && gap > 1e-9)
                dup.MakeClosed(Math.Max(tol * 10.0, 1.0));
        }
        if (!dup.IsClosed) return null;

        if (dup.TryGetPolyline(out Polyline polyline) && polyline != null && polyline.Count >= 4)
            return CleanLoop(polyline.ToList(), tol);

        var length = 0.0;
        try { length = dup.GetLength(); }
        catch { length = 0; }
        if (length <= tol) return null;
        var count = (int)Math.Ceiling(length / 250.0);
        if (count < 4) count = 4;
        if (count > 400) count = 400;
        double[] parameters = null;
        try { parameters = dup.DivideByCount(count, true); }
        catch { parameters = null; }
        if (parameters == null || parameters.Length < 3) return null;
        var sampled = new List<Point3d>();
        foreach (var parameter in parameters)
            sampled.Add(dup.PointAt(parameter));
        return CleanLoop(sampled, tol);
    }

    private static List<Point3d> CleanLoop(List<Point3d> raw, double tol)
    {
        if (raw == null) return null;
        var min = Math.Max(tol, 0.01);
        var pts = new List<Point3d>();
        foreach (var p in raw)
        {
            var q = new Point3d(Math.Round(p.X, 3), Math.Round(p.Y, 3), 0);
            if (pts.Count > 0 && pts[pts.Count - 1].DistanceTo(q) <= min)
                continue;
            pts.Add(q);
        }

        if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= min)
            pts.RemoveAt(pts.Count - 1);
        return pts.Count >= 3 ? pts : null;
    }

    private static JArray PointsToJson(List<Point3d> pts)
    {
        var arr = new JArray();
        foreach (var p in pts)
            arr.Add(new JArray { p.X, p.Y });
        return arr;
    }

    private static Curve LoopFromToken(JToken token)
    {
        if (!(token is JArray arr) || arr.Count < 3) return null;
        var pts = new List<Point3d>();
        foreach (var item in arr)
        {
            if (!(item is JArray xy) || xy.Count < 2) return null;
            double x;
            double y;
            try
            {
                x = xy[0].Value<double>();
                y = xy[1].Value<double>();
            }
            catch
            {
                return null;
            }
            pts.Add(new Point3d(x, y, 0));
        }

        if (pts.Count < 3) return null;
        if (pts[0].DistanceTo(pts[pts.Count - 1]) > 1e-6)
            pts.Add(pts[0]);
        var polyline = new Polyline(pts);
        if (!polyline.IsClosed || polyline.Count < 4) return null;
        return new PolylineCurve(polyline);
    }
}
