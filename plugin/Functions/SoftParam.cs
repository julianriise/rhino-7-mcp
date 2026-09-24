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
        public double Distance;
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
        var solid = PrepareFreshSolid(freshParts, tol, out var solidDiag);
        if (solid == null)
        {
            if (warnings.Count > 0)
                solidDiag = solidDiag + " " + warnings[0];
            throw new InvalidOperationException(solidDiag);
        }

        var solidVolume = BrepVolume(solid);
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
            var placement = PlaceFromWorld(segs, spec, rec, out var distance);
            planned.Add(new PlannedOpening
            {
                Marker = marker,
                Spec = spec,
                Placement = placement,
                T = placement.T,
                Offset = OffsetAlong(segs, placement),
                Distance = distance
            });
        }

        var seed = new List<Brep> { solid.DuplicateBrep() ?? solid };
        var cutWhy = "";
        var cutOk = SoftParamPlan.ApplyAtomic(
            seed,
            planned,
            (state, item) =>
            {
                var next = new List<Brep>();
                foreach (var brep in state)
                {
                    var dup = brep?.DuplicateBrep();
                    if (dup != null) next.Add(dup);
                }

                if (!TryCutOpeningIntoPieces(next, item, tol, out var why))
                {
                    cutWhy = why;
                    return new SoftParamPlan.CutStep<List<Brep>>(false, state);
                }

                return new SoftParamPlan.CutStep<List<Brep>>(true, next);
            },
            out var cutPieces,
            out var failed);
        if (!cutOk)
        {
            var name = failed?.Marker == null || string.IsNullOrEmpty(failed.Marker.Name)
                ? "opening"
                : failed.Marker.Name;
            throw new InvalidOperationException("Could not cut opening " + name + ". " + cutWhy);
        }

        var pieces = CollapseWallPieces(cutPieces, tol);
        if (pieces == null || pieces.Count == 0
            || !CommitWallPieces(doc, host, pieces))
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
            ["solid_volume"] = solidVolume,
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
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj.Attributes == null) continue;
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
        var path = host?.Attributes?.GetUserString("forsk:path");
        if (string.IsNullOrWhiteSpace(path) && host != null)
            path = ReadForskUserString(doc, host.Id, "forsk:path");
        var segs = SegmentsFromPath(path, tol);
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
        foreach (var obj in EnumerateDocObjects(doc))
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

        if (holes.Count == 0)
        {
            var box = outer.GetBoundingBox(true);
            var dx = box.Max.X - box.Min.X;
            var dy = box.Max.Y - box.Min.Y;
            if (Math.Min(dx, dy) <= 2.0 * FacadeConst.MinDepth)
                return SegmentsFromLinear(box);
        }

        // Short jogs in the outline are the wall thickness, not facade runs.
        var nominal = NominalThickness(outer, tol);
        var minLength = 2.0 * FacadeConst.EdgeMargin;
        if (nominal > 0)
            minLength = Math.Max(minLength, nominal * 1.75);

        var result = new List<WallSegment>();
        // Interior doors sit on room outlines, about 2.5 m from the outside of this plan.
        AppendPathEdges(result, outer, tol, minLength, nominal, holes, intoCurveIsWall: true, fromOuter: true);
        foreach (var hole in holes)
            AppendPathEdges(result, hole, tol, minLength, nominal, null, intoCurveIsWall: false, fromOuter: false);
        return result;
    }

    private static void AppendPathEdges(
        List<WallSegment> result,
        Curve curve,
        double tol,
        double minLength,
        double nominal,
        List<Curve> holes,
        bool intoCurveIsWall,
        bool fromOuter)
    {
        if (curve == null) return;
        foreach (var raw in ExplodeLineSegments(curve, tol))
        {
            var length = raw.From.DistanceTo(raw.To);
            if (length < minLength) continue;
            var line = raw;
            OrientSegmentEnds(ref line, out var start, out var end);
            var tangent = end - start;
            tangent.Z = 0;
            if (!tangent.Unitize()) continue;
            var mid = new Point3d(
                0.5 * (start.X + end.X),
                0.5 * (start.Y + end.Y),
                0);
            var thick = nominal > 0 ? nominal : FacadeConst.MinDepth;
            var inward = InwardIntoCurve(curve, mid, tangent, 20.0);
            if (!intoCurveIsWall)
                inward.Reverse();
            else if (TryNearestHole(holes, mid, out var holeDist, out var holeDir)
                && holeDist >= 40.0 && holeDist <= 800.0)
            {
                thick = holeDist;
                inward = holeDir;
            }
            if (!inward.Unitize()) continue;
            result.Add(new WallSegment
            {
                Start = new Point3d(start.X, start.Y, 0),
                End = new Point3d(end.X, end.Y, 0),
                Tangent = tangent,
                Inward = inward,
                Length = length,
                Thickness = thick,
                FromOuter = fromOuter
            });
        }
    }

    private static double NominalThickness(Curve outer, double tol)
    {
        var lengths = new List<double>();
        foreach (var raw in ExplodeLineSegments(outer, tol))
        {
            var length = raw.From.DistanceTo(raw.To);
            if (length >= 50.0 && length <= 600.0)
                lengths.Add(length);
        }
        if (lengths.Count < 2) return 0;
        lengths.Sort();
        return lengths[lengths.Count / 2];
    }

    private static bool TryNearestHole(
        List<Curve> holes,
        Point3d mid,
        out double dist,
        out Vector3d dir)
    {
        dist = 0;
        dir = Vector3d.Zero;
        if (holes == null || holes.Count == 0) return false;
        var best = double.MaxValue;
        var at = Point3d.Unset;
        foreach (var hole in holes)
        {
            if (hole == null) continue;
            if (!hole.ClosestPoint(mid, out var t)) continue;
            var point = hole.PointAt(t);
            var d = mid.DistanceTo(new Point3d(point.X, point.Y, mid.Z));
            if (d < best)
            {
                best = d;
                at = point;
            }
        }
        if (at == Point3d.Unset) return false;
        dir = new Vector3d(at.X - mid.X, at.Y - mid.Y, 0);
        if (!dir.Unitize()) return false;
        dist = best;
        return true;
    }

    private static Vector3d InwardIntoCurve(Curve outer, Point3d mid, Vector3d tangent, double probe)
    {
        var left = new Vector3d(-tangent.Y, tangent.X, 0);
        if (!left.Unitize()) return left;
        if (probe < 1.0) probe = 20.0;
        try
        {
            if (outer.Contains(mid + left * probe, Plane.WorldXY, 1.0) == PointContainment.Inside)
                return left;
        }
        catch
        {
            return left;
        }
        left.Reverse();
        return left;
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

    private const double PlaceMaxDist = 2000.0;

    // Marker world box, on the nearest outer edge or room edge. Not forsk:offset.
    private Placement PlaceFromWorld(
        List<WallSegment> segs,
        OpeningSpec spec,
        OpeningRecord rec,
        out double distance)
    {
        var world = rec.MarkerBbox.Center;
        if (!TryNearestPlan(segs, world, PlaceMaxDist, out var index, out var rawT, out distance))
        {
            throw new InvalidOperationException(
                "Could not place an opening on the host path. dist=" + Fmt(distance));
        }

        var segment = segs[index];
        var placement = new Placement
        {
            Segment = segment,
            T = ClampOrRaw(segment, spec.Width, rawT),
            Foot = FootprintFromMarker(rec)
        };
        distance = FootDistance(segs, placement);
        return placement;
    }

    private static double ClampOrRaw(WallSegment seg, double width, double rawT)
    {
        try
        {
            return ClampT(seg, width, rawT);
        }
        catch (InvalidOperationException)
        {
            if (rawT < 0) return 0;
            if (rawT > 1) return 1;
            return rawT;
        }
    }

    private static bool TryNearestPlan(
        List<WallSegment> segs,
        Point3d world,
        double maxDist,
        out int index,
        out double t,
        out double distance)
    {
        index = -1;
        t = 0;
        distance = double.PositiveInfinity;
        if (segs == null || segs.Count == 0) return false;
        var plan = new List<SoftParamPlan.Seg>(segs.Count);
        foreach (var seg in segs)
        {
            if (seg == null)
            {
                plan.Add(null);
                continue;
            }

            plan.Add(new SoftParamPlan.Seg(
                seg.Start.X, seg.Start.Y, seg.End.X, seg.End.Y, seg.FromOuter));
        }

        return SoftParamPlan.TryPlace(plan, world.X, world.Y, maxDist, out index, out t, out distance);
    }

    private static double FootDistance(List<WallSegment> segs, Placement placement)
    {
        if (placement?.Foot == null || !placement.Foot.Bbox.IsValid)
            return double.PositiveInfinity;
        var center = placement.Foot.Bbox.Center;
        if (!TryNearestPlan(segs, center, double.PositiveInfinity, out _, out _, out var distance))
            return double.PositiveInfinity;
        return distance;
    }

    private bool TryCutOpeningIntoPieces(
        List<Brep> pieces,
        PlannedOpening item,
        double tol,
        out string why)
    {
        why = "";
        if (pieces == null || item?.Placement?.Foot == null)
        {
            why = "no footprint dist=" + Fmt(item == null ? double.PositiveInfinity : item.Distance);
            return false;
        }

        var cutter = BuildOpeningCutter(
            item.Placement.Foot,
            item.Spec.Sill,
            item.Spec.Head,
            FacadeConst.Pad,
            FacadeConst.MinDepth,
            tol);
        if (cutter == null || !cutter.IsValid)
        {
            why = "no cutter dist=" + Fmt(item.Distance)
                + " edge=" + EdgeName(item)
                + " foot=" + FormatFoot(item.Placement);
            return false;
        }

        var next = new List<Brep>();
        var hit = false;
        var refused = "";
        var cutterBox = cutter.GetBoundingBox(true);
        cutterBox.Inflate(tol);
        for (var i = 0; i < pieces.Count; i++)
        {
            var wall = pieces[i];
            if (wall == null) continue;
            var wallBox = wall.GetBoundingBox(true);
            if (!BboxesOverlapXY(wallBox, cutterBox))
            {
                next.Add(wall);
                continue;
            }

            var diff = DifferencePieces(wall, cutter, tol, out var detail);
            if (diff == null)
            {
                next.Add(wall);
                refused = detail;
                continue;
            }

            next.AddRange(diff);
            hit = true;
        }

        if (!hit)
        {
            why = (string.IsNullOrEmpty(refused) ? "pieces=0 intersects=false" : refused)
                + " dist=" + Fmt(item.Distance)
                + " edge=" + EdgeName(item)
                + " foot=" + FormatFoot(item.Placement);
            return false;
        }

        pieces.Clear();
        pieces.AddRange(next);
        return true;
    }

    private static List<Brep> DifferencePieces(Brep wall, Brep cutter, double tol, out string detail)
    {
        detail = "";
        if (wall == null || cutter == null || !cutter.IsValid)
        {
            detail = "volume 0 -> 0 pieces=0 intersects=false";
            return null;
        }

        // An inward wall unions the cutter (the opening band gets thicker)
        // instead of leaving a hole.
        wall = EnsureOutward(wall);

        Brep[] results = null;
        string threw = null;
        try
        {
            results = Brep.CreateBooleanDifference(new[] { wall }, new[] { cutter }, tol);
        }
        catch (Exception ex)
        {
            threw = ex.Message;
            results = null;
        }

        var valid = new List<Brep>();
        if (results != null)
        {
            foreach (var brep in results)
            {
                if (brep != null && brep.IsValid)
                    valid.Add(brep);
            }
        }

        var before = BrepVolume(wall);
        var after = 0.0;
        foreach (var brep in valid)
            after += BrepVolume(brep);
        var dropped = before > 0 && valid.Count > 0 && after < before;
        var intersects = false;
        if (!dropped)
            intersects = CutterHitsSolid(wall, cutter, tol);

        detail = "volume " + Fmt(before) + " -> " + Fmt(after)
            + " pieces=" + valid.Count
            + " intersects=" + (intersects ? "true" : "false");
        if (threw != null)
            detail = detail + " boolean=" + threw;

        if (!SoftParamPlan.AcceptCut(valid.Count, before, after, intersects))
            return null;
        return valid;
    }

    private static bool CutterHitsSolid(Brep wall, Brep cutter, double tol)
    {
        if (wall == null || cutter == null) return false;
        Brep[] hit = null;
        try
        {
            hit = Brep.CreateBooleanIntersection(new[] { wall }, new[] { cutter }, tol);
        }
        catch
        {
            hit = null;
        }

        if (hit == null) return false;
        foreach (var brep in hit)
        {
            if (brep != null && brep.IsValid)
                return true;
        }

        return false;
    }

    private static Brep PrepareFreshSolid(List<Brep> parts, double tol, out string diagnostic)
    {
        diagnostic = "";
        if (parts == null || parts.Count == 0)
        {
            diagnostic = "Could not rebuild host wall as one solid. volume=0.";
            return null;
        }

        Brep fresh;
        if (parts.Count == 1)
        {
            fresh = parts[0];
        }
        else
        {
            Brep[] joined = null;
            try { joined = Brep.JoinBreps(parts, tol); }
            catch { joined = null; }
            if (joined == null || joined.Length != 1 || joined[0] == null || !joined[0].IsValid)
            {
                var vol = 0.0;
                foreach (var part in parts)
                    vol += BrepVolume(part);
                diagnostic = "Could not rebuild host wall as one solid. pieces="
                    + parts.Count + " volume=" + Fmt(vol) + ".";
                return null;
            }

            fresh = joined[0];
        }

        return CloseWallSolid(fresh, tol, out diagnostic);
    }

    private static Brep CloseWallSolid(Brep brep, double tol, out string diagnostic)
    {
        diagnostic = "";
        if (brep == null)
        {
            diagnostic = "Fresh wall is not a closed solid. IsSolid=false IsManifold=false volume=0.";
            return null;
        }

        var current = EnsureOutward(TryClose(brep.DuplicateBrep() ?? brep, tol));
        var volume = BrepVolume(current);
        if (IsClosedSolid(current))
        {
            diagnostic = "volume=" + Fmt(volume);
            return current;
        }

        diagnostic = "Fresh wall is not a closed solid."
            + " IsSolid=" + (current != null && current.IsSolid)
            + " IsManifold=" + (current != null && current.IsManifold)
            + " volume=" + Fmt(volume) + ".";
        return null;
    }

    private static Brep TryClose(Brep brep, double tol)
    {
        var current = brep;
        if (IsClosedSolid(current)) return current;
        current = Cap(current, tol);
        if (IsClosedSolid(current)) return current;

        Brep[] joined = null;
        try { joined = Brep.JoinBreps(new[] { current }, tol); }
        catch { joined = null; }
        if (joined != null && joined.Length == 1 && joined[0] != null && joined[0].IsValid)
            current = joined[0];
        if (IsClosedSolid(current)) return current;
        return Cap(current, tol);
    }

    private static Brep Cap(Brep brep, double tol)
    {
        if (brep == null) return null;
        try
        {
            var capped = brep.CapPlanarHoles(tol);
            if (capped != null && capped.IsValid) return capped;
        }
        catch
        {
            return brep;
        }

        return brep;
    }

    private static bool IsClosedSolid(Brep brep)
    {
        return brep != null && brep.IsValid && brep.IsSolid && brep.IsManifold;
    }

    private static Brep EnsureOutward(Brep brep)
    {
        if (brep == null || brep.SolidOrientation != BrepSolidOrientation.Inward)
            return brep;
        var copy = brep.DuplicateBrep();
        if (copy == null) return brep;
        copy.Flip();
        return copy;
    }

    private static List<Brep> CollapseWallPieces(List<Brep> pieces, double tol)
    {
        if (pieces == null) return null;
        var kept = new List<Brep>();
        foreach (var brep in pieces)
        {
            if (brep != null && brep.IsValid) kept.Add(brep);
        }

        if (kept.Count <= 1) return kept;
        Brep[] joined = null;
        try { joined = Brep.JoinBreps(kept, tol); }
        catch { joined = null; }
        if (joined != null && joined.Length == 1 && joined[0] != null
            && joined[0].IsValid && joined[0].IsSolid)
            return new List<Brep> { joined[0] };
        return kept;
    }

    private static string EdgeName(PlannedOpening item)
    {
        return item?.Placement?.Segment != null && item.Placement.Segment.FromOuter
            ? "outer"
            : "room";
    }

    private static string FormatFoot(Placement placement)
    {
        if (placement?.Foot == null || !placement.Foot.Bbox.IsValid) return "none";
        var box = placement.Foot.Bbox;
        return box.Min.X.ToString("F0", CultureInfo.InvariantCulture) + ","
            + box.Min.Y.ToString("F0", CultureInfo.InvariantCulture) + " "
            + box.Max.X.ToString("F0", CultureInfo.InvariantCulture) + ","
            + box.Max.Y.ToString("F0", CultureInfo.InvariantCulture);
    }

    private static string Fmt(double value)
    {
        if (double.IsPositiveInfinity(value) || double.IsNaN(value)) return "none";
        return value.ToString("F0", CultureInfo.InvariantCulture);
    }

    // N==1 keeps the host GUID. N>1 follows OpeningsFromLayer.
    private bool CommitWallPieces(RhinoDoc doc, WallSolid host, List<Brep> valid)
    {
        if (doc == null || host == null || valid == null || valid.Count == 0) return false;
        if (valid.Count == 1)
            return TryReplaceWallBrep(doc, host, valid[0]);

        var oldId = host.Id;
        if (!doc.Objects.Delete(host.Id, true)) return false;
        WallSolid first = null;
        for (var p = 0; p < valid.Count; p++)
        {
            var attr = host.Attributes.Duplicate();
            if (p > 0 && !string.IsNullOrEmpty(attr.Name))
                attr.Name = attr.Name + "-" + (p + 1);
            attr.MaterialSource = ObjectMaterialSource.MaterialFromLayer;
            attr.MaterialIndex = -1;
            var newId = doc.Objects.AddBrep(valid[p], attr);
            if (newId == Guid.Empty) continue;
            if (p == 0)
            {
                first = new WallSolid
                {
                    Id = newId,
                    Brep = valid[p],
                    Attributes = attr
                };
            }
        }

        if (first == null) return false;
        RetargetOpeningMarkers(doc, oldId, first.Id);
        host.Id = first.Id;
        host.Brep = first.Brep;
        host.Attributes = first.Attributes;
        return true;
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
