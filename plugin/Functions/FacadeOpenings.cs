using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Edit facade openings on vertical host walls: delete / add / move.
/// Local fill-box recut reuses BuildOpeningCutter; markers stay the handle.
/// </summary>
public partial class RhinoMCPFunctions
{
    private enum OpeningKind
    {
        Door,
        Window
    }

    private sealed class OpeningRecord
    {
        public Guid MarkerId;
        public Guid HostId;
        public OpeningKind Kind;
        public double Sill;
        public double Head;
        public double Width;
        public BoundingBox MarkerBbox;
    }

    private sealed class OpeningSpec
    {
        public OpeningKind Kind;
        public double Width;
        public double Sill;
        public double Head;
        public double? T;
        public double? DistanceMm;
    }

    private sealed class WallSegment
    {
        public Point3d Start;
        public Point3d End;
        public Vector3d Tangent;
        public Vector3d Inward;
        public double Length;
        public double Thickness;
    }

    private sealed class Placement
    {
        public WallSegment Segment;
        public double T;
        public OpeningFootprint Foot;
    }

    private static class FacadeConst
    {
        public const double Pad = 50.0;
        public const double MinDepth = 250.0;
        public const double MarkerSelectDepth = 50.0;
        public const double EdgeMargin = 50.0;
        public const double SectionBelowTop = 50.0;
        public const double SlopeNzMin = 0.1;
        public const double SlopeNzMax = 0.9;
        public const double DummyThin = 1.0;
    }

    [McpCommand("delete_opening")]
    public JObject DeleteOpening(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var markerObj = ResolveFacadeTarget(parameters, "id", expectMarker: true);
        var rec = ReadOpeningRecord(markerObj);
        var host = ReadHostWall(doc, rec.HostId, requireVertical: true);
        if (!TryFillOpening(doc, host, rec, tol))
            throw new InvalidOperationException("Could not close opening.");
        if (!doc.Objects.Delete(rec.MarkerId, true))
            throw new InvalidOperationException("Opening marker not found.");
        doc.Views.Redraw();
        return new JObject
        {
            ["deleted_marker_id"] = rec.MarkerId.ToString(),
            ["host_id"] = host.Id.ToString(),
            ["ok"] = true
        };
    }

    [McpCommand("add_opening")]
    public JObject AddOpening(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var spec = ParseOpeningSpec(parameters);
        var host = ResolveHostWall(parameters);
        var placement = PlaceOnHost(host, spec);
        if (!TryCutOpening(doc, host, spec, placement.Foot, tol, out var hostId))
            throw new InvalidOperationException("Could not cut opening.");

        var openLayer = EnsureLayer(doc, "A-OPEN", Color.FromArgb(120, 160, 200));
        var kindStr = KindToTag(spec.Kind);
        var prefix = spec.Kind == OpeningKind.Window ? "window-" : "door-";
        var index = NextNameIndex(doc, prefix);
        var sourceLayer = host.Attributes?.GetUserString("forsk:source_layer");
        var markerId = AddOpeningMarker(
            doc,
            openLayer,
            placement.Foot,
            hostId,
            kindStr,
            prefix,
            index,
            spec.Sill,
            spec.Head,
            sourceLayer);
        if (markerId == Guid.Empty)
            throw new InvalidOperationException("Could not cut opening.");

        doc.Views.Redraw();
        return new JObject
        {
            ["marker_id"] = markerId.ToString(),
            ["host_id"] = hostId.ToString(),
            ["opening_kind"] = kindStr,
            ["width"] = spec.Width,
            ["sill"] = spec.Sill,
            ["head"] = spec.Head,
            ["t"] = placement.T,
            ["ok"] = true,
            ["message"] = $"Added {kindStr} opening on wall."
        };
    }

    [McpCommand("move_opening")]
    public JObject MoveOpening(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var markerObj = ResolveFacadeTarget(parameters, "id", expectMarker: true);
        var rec = ReadOpeningRecord(markerObj);
        var host = ReadHostWall(doc, rec.HostId, requireVertical: true);
        ParseMove(parameters, out var deltaMm, out var tAbs);

        var spec = new OpeningSpec
        {
            Kind = rec.Kind,
            Width = rec.Width,
            Sill = rec.Sill,
            Head = rec.Head
        };
        var segs = ExtractWallSegments(host, tol);
        if (segs.Count == 0)
            throw new InvalidOperationException("Wall is too short for this opening.");
        var segment = PickSegmentNearest(segs, rec.MarkerBbox.Center);
        var tNow = ProjectT(segment, rec.MarkerBbox.Center);
        var rawT = tAbs ?? (tNow + deltaMm.Value / segment.Length);
        var tNew = ClampT(segment, spec.Width, rawT);

        if (Math.Abs(tNew - tNow) * segment.Length < tol)
        {
            StampOpeningHostId(doc, rec.MarkerId, host.Id);
            return new JObject
            {
                ["marker_id"] = rec.MarkerId.ToString(),
                ["host_id"] = host.Id.ToString(),
                ["t"] = tNow,
                ["ok"] = true,
                ["message"] = "Opening already at that position."
            };
        }

        if (!TryFillOpening(doc, host, rec, tol))
            throw new InvalidOperationException("Could not close opening.");

        var placement = FootprintAtT(segment, spec, tNew);
        if (!TryCutOpening(doc, host, spec, placement.Foot, tol, out var hostId))
        {
            var oldFoot = FootprintFromMarker(rec);
            if (!TryCutOpening(doc, host, spec, oldFoot, tol, out hostId))
                throw new InvalidOperationException("Could not cut opening. Undo.");
            throw new InvalidOperationException("Could not cut opening.");
        }

        var markerBrep = BuildOpeningMarkerBox(
            placement.Foot, spec.Sill, spec.Head, FacadeConst.MarkerSelectDepth);
        if (markerBrep == null || !markerBrep.IsValid)
            throw new InvalidOperationException("Could not cut opening.");
        if (!doc.Objects.Replace(rec.MarkerId, markerBrep))
            throw new InvalidOperationException("Opening marker not found.");
        StampOpeningHostId(doc, rec.MarkerId, hostId);

        doc.Views.Redraw();
        return new JObject
        {
            ["marker_id"] = rec.MarkerId.ToString(),
            ["host_id"] = hostId.ToString(),
            ["t"] = tNew,
            ["ok"] = true,
            ["message"] = "Moved opening along wall."
        };
    }

    private RhinoObject ResolveFacadeTarget(JObject parameters, string idKey, bool expectMarker)
    {
        var doc = RhinoDoc.ActiveDoc;
        var idToken = parameters?[idKey]?.ToString();
        if (!string.IsNullOrWhiteSpace(idToken))
        {
            if (!Guid.TryParse(idToken, out var guid))
            {
                throw new InvalidOperationException(
                    expectMarker ? "Opening marker not found." : "Opening host wall is missing.");
            }
            var found = doc.Objects.Find(guid);
            if (found == null)
            {
                throw new InvalidOperationException(
                    expectMarker ? "Opening marker not found." : "Opening host wall is missing.");
            }
            return found;
        }

        var selected = doc.Objects.GetSelectedObjects(false, false).ToList();
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                expectMarker
                    ? "Click one opening marker, then say it again."
                    : "Click one wall, then say it again.");
        }
        if (selected.Count != 1)
        {
            throw new InvalidOperationException(
                expectMarker
                    ? "Select exactly one opening marker."
                    : "Select exactly one wall.");
        }
        return selected[0];
    }

    private static OpeningSpec ParseOpeningSpec(JObject parameters)
    {
        var kindRaw = parameters?["opening_kind"]?.ToString();
        if (!TryParseOpeningKind(kindRaw, out var kind))
            throw new ArgumentException("opening_kind must be door or window.");

        var hasT = parameters?["t"] != null && parameters["t"].Type != JTokenType.Null;
        var hasDist = parameters?["distance_mm"] != null && parameters["distance_mm"].Type != JTokenType.Null;
        if (hasT && hasDist)
            throw new ArgumentException("Specify t or distance_mm, not both.");

        double width;
        double sill;
        double head;
        if (kind == OpeningKind.Door)
        {
            width = parameters?["width"]?.ToObject<double?>() ?? ForskDefaults.DoorWidth;
            sill = parameters?["sill"]?.ToObject<double?>() ?? ForskDefaults.DoorSill;
            head = parameters?["head"]?.ToObject<double?>() ?? ForskDefaults.DoorHead;
        }
        else
        {
            width = parameters?["width"]?.ToObject<double?>() ?? ForskDefaults.WindowWidth;
            sill = parameters?["sill"]?.ToObject<double?>() ?? ForskDefaults.WindowSill;
            head = parameters?["head"]?.ToObject<double?>() ?? ForskDefaults.WindowHead;
        }

        if (width <= 0)
            throw new ArgumentException("width must be positive.");
        if (head <= sill)
            throw new ArgumentException("head must be greater than sill.");

        return new OpeningSpec
        {
            Kind = kind,
            Width = width,
            Sill = sill,
            Head = head,
            T = hasT ? parameters["t"].ToObject<double?>() : null,
            DistanceMm = hasDist ? parameters["distance_mm"].ToObject<double?>() : null
        };
    }

    private static void ParseMove(JObject parameters, out double? deltaMm, out double? tAbs)
    {
        var hasDelta = parameters?["delta_mm"] != null && parameters["delta_mm"].Type != JTokenType.Null;
        var hasT = parameters?["t"] != null && parameters["t"].Type != JTokenType.Null;
        if (hasDelta && hasT)
            throw new ArgumentException("Specify delta_mm or t, not both.");
        if (!hasDelta && !hasT)
            throw new ArgumentException("Specify delta_mm or t.");
        deltaMm = hasDelta ? parameters["delta_mm"].ToObject<double?>() : null;
        tAbs = hasT ? parameters["t"].ToObject<double?>() : null;
    }

    private static bool TryParseOpeningKind(string raw, out OpeningKind kind)
    {
        kind = OpeningKind.Door;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (raw.Equals("door", StringComparison.OrdinalIgnoreCase))
        {
            kind = OpeningKind.Door;
            return true;
        }
        if (raw.Equals("window", StringComparison.OrdinalIgnoreCase))
        {
            kind = OpeningKind.Window;
            return true;
        }
        return false;
    }

    private static string KindToTag(OpeningKind kind)
    {
        return kind == OpeningKind.Window ? "window" : "door";
    }

    private OpeningRecord ReadOpeningRecord(RhinoObject obj)
    {
        if (obj == null)
            throw new InvalidOperationException("Opening marker not found.");
        if (!string.Equals(GetForskKind(obj), "opening_marker", StringComparison.Ordinal))
            throw new InvalidOperationException("Not an opening marker.");

        var hostRaw = obj.Attributes.GetUserString("forsk:host");
        if (string.IsNullOrWhiteSpace(hostRaw) || !Guid.TryParse(hostRaw, out var hostId))
            throw new InvalidOperationException("Opening host wall is missing.");

        var kindRaw = obj.Attributes.GetUserString("forsk:opening_kind");
        if (!TryParseOpeningKind(kindRaw, out var kind))
            throw new InvalidOperationException("opening_kind must be door or window.");

        var bbox = obj.Geometry.GetBoundingBox(true);
        if (!bbox.IsValid)
            throw new InvalidOperationException("Opening marker not found.");

        var sill = TryParseUserDouble(obj, "forsk:sill") ?? bbox.Min.Z;
        var head = TryParseUserDouble(obj, "forsk:head") ?? bbox.Max.Z;
        var width = TryParseUserDouble(obj, "forsk:width") ?? LongerXySide(bbox);
        if (width <= 0)
            throw new ArgumentException("width must be positive.");
        if (head <= sill)
            throw new ArgumentException("head must be greater than sill.");

        return new OpeningRecord
        {
            MarkerId = obj.Id,
            HostId = hostId,
            Kind = kind,
            Sill = sill,
            Head = head,
            Width = width,
            MarkerBbox = bbox
        };
    }

    private static double? TryParseUserDouble(RhinoObject obj, string key)
    {
        var raw = obj.Attributes.GetUserString(key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        return null;
    }

    private WallSolid ReadHostWall(RhinoDoc doc, Guid id, bool requireVertical)
    {
        var obj = doc.Objects.Find(id);
        if (obj == null)
            throw new InvalidOperationException("Opening host wall is missing.");
        if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Not a Forsk wall.");
        var brep = GetBrepFromObject(obj);
        if (brep == null)
            throw new InvalidOperationException("Opening host wall is missing.");
        if (requireVertical && !IsVerticalWall(brep))
            throw new InvalidOperationException("Host wall is not vertical.");
        return new WallSolid
        {
            Id = obj.Id,
            Brep = brep.DuplicateBrep() ?? brep,
            Attributes = obj.Attributes.Duplicate()
        };
    }

    private WallSolid ResolveHostWall(JObject parameters)
    {
        var obj = ResolveFacadeTarget(parameters, "host_id", expectMarker: false);
        return ReadHostWall(RhinoDoc.ActiveDoc, obj.Id, requireVertical: true);
    }

    // Fill uses BooleanUnion of the same AABB cutter that cut the hole.
    private bool TryFillOpening(RhinoDoc doc, WallSolid host, OpeningRecord rec, double tol)
    {
        var foot = FootprintFromMarker(rec);
        var cutter = BuildOpeningCutter(
            foot, rec.Sill, rec.Head, FacadeConst.Pad, FacadeConst.MinDepth, tol);
        if (cutter == null || !cutter.IsValid) return false;
        return TryBooleanReplaceWall(doc, host, cutter, difference: false, tol);
    }

    private bool TryCutOpening(
        RhinoDoc doc,
        WallSolid host,
        OpeningSpec spec,
        OpeningFootprint foot,
        double tol,
        out Guid hostId)
    {
        hostId = host.Id;
        var cutter = BuildOpeningCutter(
            foot, spec.Sill, spec.Head, FacadeConst.Pad, FacadeConst.MinDepth, tol);
        if (cutter == null || !cutter.IsValid) return false;
        if (!TryBooleanReplaceWall(doc, host, cutter, difference: true, tol))
            return false;
        hostId = host.Id;
        return true;
    }

    // Single-host boolean + GUID-preserving replace. Not extracted from bake:
    // OpeningsFromLayer continues across walls and records per-footprint failures.
    private bool TryBooleanReplaceWall(
        RhinoDoc doc,
        WallSolid host,
        Brep tool,
        bool difference,
        double tol)
    {
        Brep[] results;
        try
        {
            results = difference
                ? Brep.CreateBooleanDifference(new[] { host.Brep }, new[] { tool }, tol)
                : Brep.CreateBooleanUnion(new[] { host.Brep, tool }, tol);
        }
        catch
        {
            return false;
        }

        if (results == null || results.Length == 0) return false;
        var valid = results.Where(b => b != null && b.IsValid).ToList();
        if (valid.Count == 0) return false;

        if (valid.Count == 1)
            return TryReplaceWallBrep(doc, host, valid[0]);

        var oldId = host.Id;
        if (!doc.Objects.Delete(host.Id, true)) return false;

        WallSolid first = null;
        for (var p = 0; p < valid.Count; p++)
        {
            var attr = host.Attributes.Duplicate();
            if (p > 0 && !string.IsNullOrEmpty(attr.Name))
                attr.Name = $"{attr.Name}-{p + 1}";
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

    private static bool IsVerticalWall(Brep brep)
    {
        if (brep?.Faces == null || brep.Faces.Count == 0) return false;
        foreach (var face in brep.Faces)
        {
            if (!face.FrameAt(face.Domain(0).Mid, face.Domain(1).Mid, out var frame))
                continue;
            var nz = Math.Abs(frame.ZAxis.Z);
            var vertical = nz <= FacadeConst.SlopeNzMin;
            var cap = nz >= FacadeConst.SlopeNzMax;
            if (!vertical && !cap) return false;
        }
        return true;
    }

    private List<WallSegment> ExtractWallSegments(WallSolid host, double tol)
    {
        var bbox = host.Brep.GetBoundingBox(true);
        if (!bbox.IsValid) return new List<WallSegment>();

        var height = bbox.Max.Z - bbox.Min.Z;
        var zTop = bbox.Max.Z - Math.Max(FacadeConst.SectionBelowTop, 0.05 * Math.Max(height, 0));
        var closed = ContourClosedCurves(host.Brep, zTop, tol);
        if (closed.Count == 0)
        {
            var zMid = 0.5 * (bbox.Min.Z + bbox.Max.Z);
            closed = ContourClosedCurves(host.Brep, zMid, tol);
        }
        if (closed.Count == 0) return new List<WallSegment>();

        closed = closed
            .OrderByDescending(c => Math.Abs(AreaMassProperties.Compute(c)?.Area ?? 0))
            .ToList();

        if (closed.Count >= 2)
            return SegmentsFromBand(closed[0], closed[1], tol);

        var only = closed[0];
        var cb = only.GetBoundingBox(true);
        var dx = cb.Max.X - cb.Min.X;
        var dy = cb.Max.Y - cb.Min.Y;
        if (Math.Min(dx, dy) <= 2.0 * FacadeConst.MinDepth)
            return SegmentsFromLinear(cb);

        // Single large ring without a clear inner: treat outer edges as a band
        // with inward toward the ring centroid.
        return SegmentsFromOuterOnly(only, tol);
    }

    private static List<Curve> ContourClosedCurves(Brep brep, double z, double tol)
    {
        var plane = new Plane(new Point3d(0, 0, z), Vector3d.ZAxis);
        Curve[] curves = null;
        try
        {
            curves = Brep.CreateContourCurves(brep, plane);
        }
        catch
        {
            curves = null;
        }
        var closed = new List<Curve>();
        if (curves == null) return closed;
        foreach (var c in curves)
        {
            if (c == null || !c.IsValid) continue;
            var flat = c.DuplicateCurve();
            if (flat == null) continue;
            if (!flat.IsClosed)
            {
                var gap = flat.PointAtStart.DistanceTo(flat.PointAtEnd);
                if (gap <= Math.Max(tol * 10.0, 1.0))
                    flat.MakeClosed(Math.Max(tol * 10.0, 1.0));
            }
            if (flat.IsClosed) closed.Add(flat);
        }
        return closed;
    }

    private static List<WallSegment> SegmentsFromBand(Curve outer, Curve inner, double tol)
    {
        var result = new List<WallSegment>();
        foreach (var raw in ExplodeLineSegments(outer, tol))
        {
            var length = raw.From.DistanceTo(raw.To);
            if (length <= 2.0 * FacadeConst.EdgeMargin) continue;

            var line = raw;
            OrientSegmentEnds(ref line, out var start, out var end);
            var tangent = end - start;
            tangent.Z = 0;
            if (!tangent.Unitize()) continue;
            var mid = new Point3d(
                0.5 * (start.X + end.X),
                0.5 * (start.Y + end.Y),
                0);
            if (!inner.ClosestPoint(mid, out var tInner)) continue;
            var onInner = inner.PointAt(tInner);
            var inward = new Vector3d(onInner.X - mid.X, onInner.Y - mid.Y, 0);
            if (!inward.Unitize())
            {
                inward = new Vector3d(-tangent.Y, tangent.X, 0);
                if (!inward.Unitize()) continue;
            }
            var thickness = mid.DistanceTo(new Point3d(onInner.X, onInner.Y, mid.Z));
            if (thickness < tol) thickness = FacadeConst.MinDepth;
            result.Add(new WallSegment
            {
                Start = new Point3d(start.X, start.Y, 0),
                End = new Point3d(end.X, end.Y, 0),
                Tangent = tangent,
                Inward = inward,
                Length = length,
                Thickness = thickness
            });
        }
        return result;
    }

    private static List<WallSegment> SegmentsFromOuterOnly(Curve outer, double tol)
    {
        var amp = AreaMassProperties.Compute(outer);
        var centroid = amp != null
            ? new Point3d(amp.Centroid.X, amp.Centroid.Y, 0)
            : outer.GetBoundingBox(true).Center;
        var result = new List<WallSegment>();
        foreach (var raw in ExplodeLineSegments(outer, tol))
        {
            var length = raw.From.DistanceTo(raw.To);
            if (length <= 2.0 * FacadeConst.EdgeMargin) continue;
            var line = raw;
            OrientSegmentEnds(ref line, out var start, out var end);
            var tangent = end - start;
            tangent.Z = 0;
            if (!tangent.Unitize()) continue;
            var mid = new Point3d(0.5 * (start.X + end.X), 0.5 * (start.Y + end.Y), 0);
            var inward = new Vector3d(centroid.X - mid.X, centroid.Y - mid.Y, 0);
            if (!inward.Unitize())
            {
                inward = new Vector3d(-tangent.Y, tangent.X, 0);
                if (!inward.Unitize()) continue;
            }
            result.Add(new WallSegment
            {
                Start = new Point3d(start.X, start.Y, 0),
                End = new Point3d(end.X, end.Y, 0),
                Tangent = tangent,
                Inward = inward,
                Length = length,
                Thickness = FacadeConst.MinDepth
            });
        }
        return result;
    }

    private static List<WallSegment> SegmentsFromLinear(BoundingBox cb)
    {
        var dx = cb.Max.X - cb.Min.X;
        var dy = cb.Max.Y - cb.Min.Y;
        var cx = 0.5 * (cb.Min.X + cb.Max.X);
        var cy = 0.5 * (cb.Min.Y + cb.Max.Y);
        Point3d start;
        Point3d end;
        Vector3d tangent;
        Vector3d inward;
        double length;
        double thickness;
        if (dx >= dy)
        {
            start = new Point3d(cb.Min.X, cy, 0);
            end = new Point3d(cb.Max.X, cy, 0);
            tangent = new Vector3d(1, 0, 0);
            inward = new Vector3d(0, 1, 0);
            length = dx;
            thickness = dy;
        }
        else
        {
            start = new Point3d(cx, cb.Min.Y, 0);
            end = new Point3d(cx, cb.Max.Y, 0);
            tangent = new Vector3d(0, 1, 0);
            inward = new Vector3d(1, 0, 0);
            length = dy;
            thickness = dx;
        }
        OrientSegmentEnds(ref start, ref end, ref tangent);
        if (thickness < 1e-9) thickness = FacadeConst.MinDepth;
        return new List<WallSegment>
        {
            new WallSegment
            {
                Start = start,
                End = end,
                Tangent = tangent,
                Inward = inward,
                Length = length,
                Thickness = thickness
            }
        };
    }

    private static List<Line> ExplodeLineSegments(Curve curve, double tol)
    {
        var lines = new List<Line>();
        if (curve == null) return lines;

        if (curve.TryGetPolyline(out var pl))
        {
            for (var i = 1; i < pl.Count; i++)
            {
                var a = pl[i - 1];
                var b = pl[i];
                if (a.DistanceTo(b) > tol)
                    lines.Add(new Line(a, b));
            }
            return lines;
        }

        var segs = curve.DuplicateSegments();
        if (segs != null)
        {
            foreach (var s in segs)
            {
                if (s == null) continue;
                if (s.IsLinear(tol))
                    lines.Add(new Line(s.PointAtStart, s.PointAtEnd));
                else if (s.TryGetPolyline(out var sub))
                {
                    for (var i = 1; i < sub.Count; i++)
                    {
                        var a = sub[i - 1];
                        var b = sub[i];
                        if (a.DistanceTo(b) > tol)
                            lines.Add(new Line(a, b));
                    }
                }
            }
        }
        return lines;
    }

    private static void OrientSegmentEnds(ref Line line, out Point3d start, out Point3d end)
    {
        start = line.From;
        end = line.To;
        var tangent = end - start;
        OrientSegmentEnds(ref start, ref end, ref tangent);
        line = new Line(start, end);
    }

    private static void OrientSegmentEnds(ref Point3d start, ref Point3d end, ref Vector3d tangent)
    {
        var swap = false;
        if (start.X > end.X + 1e-9) swap = true;
        else if (Math.Abs(start.X - end.X) <= 1e-9 && start.Y > end.Y + 1e-9) swap = true;
        if (swap)
        {
            var tmp = start;
            start = end;
            end = tmp;
            tangent = -tangent;
        }
        tangent.Z = 0;
        tangent.Unitize();
    }

    private WallSegment PickLongestSegment(List<WallSegment> segs)
    {
        WallSegment best = null;
        foreach (var s in segs)
        {
            if (best == null || s.Length > best.Length)
                best = s;
        }
        return best;
    }

    private WallSegment PickSegmentNearest(List<WallSegment> segs, Point3d worldPt)
    {
        WallSegment best = null;
        var bestDist = double.PositiveInfinity;
        var p = new Point3d(worldPt.X, worldPt.Y, 0);
        foreach (var s in segs)
        {
            var line = new Line(s.Start, s.End);
            var closest = line.ClosestPoint(p, false);
            var d = closest.DistanceTo(p);
            if (d < bestDist)
            {
                bestDist = d;
                best = s;
            }
        }
        return best;
    }

    private Placement PlaceOnHost(WallSolid host, OpeningSpec spec)
    {
        var tol = Math.Max(RhinoDoc.ActiveDoc.ModelAbsoluteTolerance, 1e-6);
        var segs = ExtractWallSegments(host, tol);
        if (segs.Count == 0)
            throw new InvalidOperationException("Wall is too short for this opening.");
        var seg = PickLongestSegment(segs);
        double rawT;
        if (spec.T.HasValue) rawT = spec.T.Value;
        else if (spec.DistanceMm.HasValue) rawT = spec.DistanceMm.Value / seg.Length;
        else rawT = 0.5;
        var t = ClampT(seg, spec.Width, rawT);
        return FootprintAtT(seg, spec, t);
    }

    private static double ClampT(WallSegment seg, double width, double rawT)
    {
        var minT = (FacadeConst.EdgeMargin + width * 0.5) / seg.Length;
        var maxT = 1.0 - minT;
        if (maxT < minT)
            throw new InvalidOperationException("Wall is too short for this opening.");
        if (rawT < minT) return minT;
        if (rawT > maxT) return maxT;
        return rawT;
    }

    private static double ProjectT(WallSegment seg, Point3d pt)
    {
        var v = new Vector3d(pt.X - seg.Start.X, pt.Y - seg.Start.Y, 0);
        return v * seg.Tangent / seg.Length;
    }

    private static OpeningFootprint FootprintFromMarker(OpeningRecord rec)
    {
        var b = rec.MarkerBbox;
        var xy = new BoundingBox(
            new Point3d(b.Min.X, b.Min.Y, 0),
            new Point3d(b.Max.X, b.Max.Y, 0));
        return new OpeningFootprint
        {
            SourceId = rec.MarkerId.ToString(),
            Bbox = xy
        };
    }

    private static Placement FootprintAtT(WallSegment seg, OpeningSpec spec, double t)
    {
        var center = seg.Start + (t * seg.Length) * seg.Tangent
                     + seg.Inward * (seg.Thickness * 0.5);
        var halfW = spec.Width * 0.5;
        var halfThin = FacadeConst.DummyThin * 0.5;
        BoundingBox bbox;
        if (Math.Abs(seg.Tangent.X) >= Math.Abs(seg.Tangent.Y))
        {
            bbox = new BoundingBox(
                new Point3d(center.X - halfW, center.Y - halfThin, 0),
                new Point3d(center.X + halfW, center.Y + halfThin, 0));
        }
        else
        {
            bbox = new BoundingBox(
                new Point3d(center.X - halfThin, center.Y - halfW, 0),
                new Point3d(center.X + halfThin, center.Y + halfW, 0));
        }
        return new Placement
        {
            Segment = seg,
            T = t,
            Foot = new OpeningFootprint
            {
                SourceId = "placed",
                Bbox = bbox
            }
        };
    }
}
