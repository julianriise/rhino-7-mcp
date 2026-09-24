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
/// Edit facade openings on vertical host walls: delete, add, move, and set size.
/// Each edit writes the opening record, then rebuilds that host from its path.
/// Markers stay the handle. A failed rebuild puts the record back.
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
        public bool FromOuter;
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
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var removals = ReadRemovals(ResolveDeleteTargets(parameters));
        var before = SnapshotObjectIds(doc);
        var groups = GroupRemovals(removals);
        var commits = new List<HostUndo>();
        try
        {
            foreach (var group in groups)
            {
                var undo = PrepareHostUndo(doc, group);
                commits.Add(undo);
                try
                {
                    foreach (var item in group)
                        RemoveOpeningPieces(doc, item.Record.MarkerId, undo.Removed);
                    var rebuilt = RebuildHostWall(new JObject
                    {
                        ["id"] = group[0].HostId.ToString()
                    });
                    undo.Committed = true;
                    var rebuiltId = rebuilt?["host_id"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(rebuiltId) && Guid.TryParse(rebuiltId, out var parsed))
                        undo.HostAfter = parsed;
                    else
                        undo.HostAfter = group[0].HostId;
                    undo.NewBlocks = GuidList(rebuilt?["block_ids"] as JArray);
                }
                catch
                {
                    if (!undo.Committed)
                        UndeletePieces(doc, undo.Removed);
                    throw;
                }
            }
        }
        catch
        {
            for (var i = commits.Count - 1; i >= 0; i--)
            {
                if (!commits[i].Committed) continue;
                try { RollbackCommittedHost(doc, commits[i]); }
                catch (Exception) { }
            }
            throw;
        }

        doc.Views.Redraw();
        return DeleteOpeningResult(doc, removals, commits, before, tol);
    }

    [McpCommand("add_opening")]
    public JObject AddOpening(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var spec = ParseOpeningSpec(parameters);
        var host = ResolveHostWall(parameters);
        var placement = PlaceOnHost(host, spec);
        var openLayer = EnsureLayer(doc, "A-OPEN", Color.FromArgb(120, 160, 200));
        var kindStr = KindToTag(spec.Kind);
        var prefix = spec.Kind == OpeningKind.Window ? "window-" : "door-";
        var index = NextNameIndex(doc, prefix);
        var sourceLayer = host.Attributes?.GetUserString("forsk:source_layer");
        var markerId = AddOpeningMarker(
            doc,
            openLayer,
            placement.Foot,
            host.Id,
            kindStr,
            prefix,
            index,
            spec.Sill,
            spec.Head,
            sourceLayer);
        if (markerId == Guid.Empty)
            throw new InvalidOperationException("Could not cut opening.");

        JObject rebuilt;
        try
        {
            rebuilt = RebuildHostWall(new JObject { ["id"] = host.Id.ToString() });
        }
        catch
        {
            DeleteOpeningBlocks(doc, markerId);
            if (doc.Objects.FindId(markerId) != null)
                doc.Objects.Delete(markerId, true);
            throw;
        }

        var hostId = host.Id;
        var rebuiltId = rebuilt?["host_id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(rebuiltId) && Guid.TryParse(rebuiltId, out var parsed))
            hostId = parsed;
        var blockId = FindOpeningBlock(doc, markerId);
        var label = host.Attributes?.GetUserString("forsk:id");
        if (string.IsNullOrWhiteSpace(label)) label = "the wall";
        var noun = spec.Kind == OpeningKind.Window ? "window" : "door";
        var added = new JObject
        {
            ["marker_id"] = markerId.ToString(),
            ["host_id"] = hostId.ToString(),
            ["opening_kind"] = kindStr,
            ["width"] = spec.Width,
            ["sill"] = spec.Sill,
            ["head"] = spec.Head,
            ["t"] = placement.T,
            ["ok"] = true,
            ["message"] = "Added a " + noun + " on " + label
        };
        if (blockId != Guid.Empty)
            added["block_id"] = blockId.ToString();
        var report = HostOpeningReport(doc, hostId, markerId, Point3d.Unset);
        if (report != null) added.Merge(report);
        var recorded = TryParseUserDouble(doc.Objects.FindId(markerId), "forsk:t");
        if (recorded.HasValue) added["t"] = recorded.Value;
        doc.Views.Redraw();
        return added;
    }

    [McpCommand("move_opening")]
    public JObject MoveOpening(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var markerObj = ResolveOpeningHandle(
            ResolveFacadeTarget(parameters, "id", expectMarker: true));
        RefuseExistingUnderlay(doc, markerObj);
        var rec = ReadOpeningRecord(markerObj);
        var host = ReadHostWall(doc, rec.HostId, requireVertical: true);
        ParseMove(parameters, out var deltaMm, out var tAbs);

        var pathSegs = HostPathSegments(host, tol);
        if (!TryOpeningOnPath(pathSegs, rec.MarkerBbox.Center, out var index, out var tNow))
            throw new InvalidOperationException("Could not place an opening on the host path.");

        if (!SoftParamPlan.TrySlide(
                PlanSegs(pathSegs),
                index,
                tNow,
                rec.Width,
                deltaMm,
                tAbs,
                FacadeConst.EdgeMargin,
                tol,
                out var slide))
            throw new InvalidOperationException("Wall is too short for this opening.");

        if (!slide.Moved)
        {
            StampOpeningHostId(doc, rec.MarkerId, host.Id);
            StampOpeningOnHost(
                doc, host, rec.MarkerId, Guid.Empty, rec.MarkerBbox.Center, null, tol);
            return OpeningEditResult(
                doc, rec, host.Id, rec.Width, rec.Sill, rec.Head, tNow,
                "Opening already at that position.");
        }

        var spec = new OpeningSpec
        {
            Kind = rec.Kind,
            Width = rec.Width,
            Sill = rec.Sill,
            Head = rec.Head
        };
        var placement = FootprintAtT(pathSegs[slide.Index], spec, slide.T);
        var markerBrep = BuildOpeningMarkerBox(
            placement.Foot, spec.Sill, spec.Head, FacadeConst.MarkerSelectDepth);
        if (markerBrep == null || !markerBrep.IsValid)
            throw new InvalidOperationException("Could not cut opening.");

        var message = deltaMm.HasValue
            ? "Moved the opening " + FormatMm(Math.Abs(deltaMm.Value)) + " mm along the wall."
            : "Moved the opening along the wall.";
        return CommitOpeningThenRebuild(
            doc, rec, host, markerBrep, rec.Width, rec.Sill, rec.Head, slide.T, message);
    }

    [McpCommand("set_opening")]
    public JObject SetOpening(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var markerObj = ResolveOpeningHandle(
            ResolveFacadeTarget(parameters, "id", expectMarker: true));
        RefuseExistingUnderlay(doc, markerObj);
        var rec = ReadOpeningRecord(markerObj);
        var host = ReadHostWall(doc, rec.HostId, requireVertical: true);

        double? width = ReadOptionalDouble(parameters, "width");
        double? sill = ReadOptionalDouble(parameters, "sill");
        double? head = ReadOptionalDouble(parameters, "head");
        if (!SoftParamPlan.TrySetSize(
                rec.Width, rec.Sill, rec.Head, width, sill, head, out var size, out var why))
            throw new ArgumentException(why);

        if (!size.Changed)
        {
            return OpeningEditResult(
                doc, rec, host.Id, rec.Width, rec.Sill, rec.Head, ProjectRecordedT(doc, rec),
                "Opening already at that size.");
        }

        Brep markerBrep = null;
        var widthChanged = Math.Abs(size.Width - rec.Width) > 1e-6;
        if (widthChanged)
        {
            var pathSegs = HostPathSegments(host, tol);
            if (!TryOpeningOnPath(pathSegs, rec.MarkerBbox.Center, out var index, out var tNow))
                throw new InvalidOperationException("Could not place an opening on the host path.");
            if (!SoftParamPlan.TrySlide(
                    PlanSegs(pathSegs),
                    index,
                    tNow,
                    size.Width,
                    null,
                    tNow,
                    FacadeConst.EdgeMargin,
                    tol,
                    out var slide))
                throw new InvalidOperationException("Wall is too short for this opening.");

            var spec = new OpeningSpec
            {
                Kind = rec.Kind,
                Width = size.Width,
                Sill = size.Sill,
                Head = size.Head
            };
            var placement = FootprintAtT(pathSegs[slide.Index], spec, slide.T);
            markerBrep = BuildOpeningMarkerBox(
                placement.Foot, size.Sill, size.Head, FacadeConst.MarkerSelectDepth);
            if (markerBrep == null || !markerBrep.IsValid)
                throw new InvalidOperationException("Could not cut opening.");
        }

        var message = "Set the opening to width " + FormatMm(size.Width)
            + " mm, sill " + FormatMm(size.Sill)
            + " mm, head " + FormatMm(size.Head) + " mm.";
        return CommitOpeningThenRebuild(
            doc, rec, host, markerBrep, size.Width, size.Sill, size.Head,
            ProjectRecordedT(doc, rec), message);
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

        var selected = ListSelected(doc);
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                expectMarker
                    ? "Click one opening marker or frame, then say it again."
                    : "Click one wall, then say it again.");
        }
        if (selected.Count != 1)
        {
            throw new InvalidOperationException(
                expectMarker
                    ? "Select exactly one opening marker or frame."
                    : "Select exactly one wall.");
        }
        return selected[0];
    }

    private List<RhinoObject> ResolveDeleteTargets(JObject parameters)
    {
        var idToken = parameters?["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(idToken))
            return new List<RhinoObject> { ResolveFacadeTarget(parameters, "id", expectMarker: true) };

        var selected = ListSelected(RhinoDoc.ActiveDoc);
        if (selected.Count == 0)
            throw new InvalidOperationException(
                "Click one opening marker or frame, then say it again.");

        var openings = new List<RhinoObject>();
        foreach (var obj in selected)
        {
            var handle = ResolveOpeningHandle(obj);
            if (!string.Equals(GetForskKind(handle), "opening_marker", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Not an opening marker.");
            openings.Add(obj);
        }
        return openings;
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
        RefuseExistingUnderlay(doc, obj);
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
        var segs = HostPathSegments(host, tol);
        var pool = new List<WallSegment>();
        foreach (var seg in segs)
        {
            if (seg != null && seg.FromOuter) pool.Add(seg);
        }
        if (pool.Count == 0) pool = segs;
        var chosen = PickLongestSegment(pool);
        if (chosen == null)
            throw new InvalidOperationException("Wall is too short for this opening.");
        double rawT;
        if (spec.T.HasValue) rawT = spec.T.Value;
        else if (spec.DistanceMm.HasValue) rawT = spec.DistanceMm.Value / chosen.Length;
        else rawT = 0.5;
        var t = ClampT(chosen, spec.Width, rawT);
        return FootprintAtT(chosen, spec, t);
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

    private List<WallSegment> HostPathSegments(WallSolid host, double tol)
    {
        var path = host?.Attributes?.GetUserString("forsk:path");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(MissingWallPathMessage);
        var segs = SegmentsFromPath(path, tol);
        if (segs.Count == 0)
            throw new InvalidOperationException("Wall is too short for this opening.");
        return segs;
    }

    private static List<SoftParamPlan.Seg> PlanSegs(List<WallSegment> segs)
    {
        var plan = new List<SoftParamPlan.Seg>(segs?.Count ?? 0);
        if (segs == null) return plan;
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

        return plan;
    }

    private bool TryOpeningOnPath(
        List<WallSegment> segs,
        Point3d center,
        out int index,
        out double t)
    {
        index = -1;
        t = 0;
        if (!TryOffsetOnSegments(segs, center, out var segment, out t, out _))
            return false;
        index = segs.IndexOf(segment);
        return index >= 0;
    }

    private JObject CommitOpeningThenRebuild(
        RhinoDoc doc,
        OpeningRecord rec,
        WallSolid host,
        Brep newMarker,
        double width,
        double sill,
        double head,
        double t,
        string message)
    {
        var snap = CaptureMarker(doc, rec.MarkerId);
        try
        {
            if (newMarker != null && !doc.Objects.Replace(rec.MarkerId, newMarker))
                throw new InvalidOperationException("Opening marker not found.");
            WriteOpeningSize(doc, rec.MarkerId, width, sill, head);
            var rebuilt = RebuildHostWall(new JObject { ["id"] = host.Id.ToString() });
            var hostId = host.Id;
            var rebuiltId = rebuilt?["host_id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(rebuiltId) && Guid.TryParse(rebuiltId, out var parsed))
                hostId = parsed;
            var result = OpeningEditResult(doc, rec, hostId, width, sill, head, t, message);
            var previous = Point3d.Unset;
            if (snap.Geometry != null)
            {
                var oldBox = snap.Geometry.GetBoundingBox(true);
                if (oldBox.IsValid) previous = oldBox.Center;
            }
            var report = HostOpeningReport(doc, hostId, rec.MarkerId, previous);
            if (report != null) result.Merge(report);
            return result;
        }
        catch
        {
            RestoreMarker(doc, rec.MarkerId, snap);
            throw;
        }
    }

    private JObject HostOpeningReport(RhinoDoc doc, Guid hostId, Guid editedId, Point3d previous)
    {
        if (doc == null || hostId == Guid.Empty) return null;
        var wall = doc.Objects.FindId(hostId);
        var brep = GetBrepFromObject(wall);
        var forskId = wall?.Attributes?.GetUserString("forsk:id");
        var markers = MarkersOnHost(doc, hostId, forskId);
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var rows = new JArray();
        var voids = 0;
        var maxFrame = 0.0;
        foreach (var marker in markers)
        {
            if (marker?.Geometry == null) continue;
            var box = marker.Geometry.GetBoundingBox(true);
            if (!box.IsValid) continue;
            var center = box.Center;
            var inside = PointInside(brep, center, tol);
            if (!inside) voids++;
            var frameDist = -1.0;
            var frameId = FindOpeningBlock(doc, marker.Id);
            if (frameId != Guid.Empty)
            {
                var frame = doc.Objects.FindId(frameId);
                var frameBox = frame?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
                if (frameBox.IsValid)
                {
                    var dx = center.X - frameBox.Center.X;
                    var dy = center.Y - frameBox.Center.Y;
                    frameDist = Math.Sqrt(dx * dx + dy * dy);
                }
            }
            if (frameDist > maxFrame) maxFrame = frameDist;
            var row = new JObject
            {
                ["id"] = marker.Id.ToString(),
                ["x"] = center.X,
                ["y"] = center.Y,
                ["z"] = center.Z,
                ["inside"] = inside,
                ["frame_mm"] = frameDist
            };
            if (marker.Id == editedId && previous.IsValid)
            {
                var pdx = center.X - previous.X;
                var pdy = center.Y - previous.Y;
                row["moved_mm"] = Math.Sqrt(pdx * pdx + pdy * pdy);
            }
            rows.Add(row);
        }
        return new JObject
        {
            ["host_openings"] = rows.Count,
            ["host_voids"] = voids,
            ["max_frame_mm"] = maxFrame,
            ["markers"] = rows
        };
    }

    private static bool PointInside(Brep brep, Point3d point, double tol)
    {
        if (brep == null || !brep.IsSolid || !point.IsValid) return false;
        try
        {
            return brep.IsPointInside(point, tol, false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private JObject OpeningEditResult(
        RhinoDoc doc,
        OpeningRecord rec,
        Guid hostId,
        double width,
        double sill,
        double head,
        double t,
        string message)
    {
        var marker = doc?.Objects.FindId(rec.MarkerId);
        var recorded = TryParseUserDouble(marker, "forsk:t") ?? t;
        var result = new JObject
        {
            ["marker_id"] = rec.MarkerId.ToString(),
            ["host_id"] = hostId.ToString(),
            ["width"] = width,
            ["sill"] = sill,
            ["head"] = head,
            ["t"] = recorded,
            ["ok"] = true,
            ["message"] = message
        };
        var blockId = FindOpeningBlock(doc, rec.MarkerId);
        if (blockId != Guid.Empty)
            result["block_id"] = blockId.ToString();
        return result;
    }

    private static double ProjectRecordedT(RhinoDoc doc, OpeningRecord rec)
    {
        var marker = doc?.Objects.FindId(rec.MarkerId);
        return TryParseUserDouble(marker, "forsk:t") ?? 0;
    }

    private static double? ReadOptionalDouble(JObject parameters, string key)
    {
        var token = parameters?[key];
        if (token == null || token.Type == JTokenType.Null) return null;
        return token.ToObject<double?>();
    }

    private sealed class MarkerSnapshot
    {
        public Brep Geometry;
        public string Width;
        public string Sill;
        public string Head;
        public string T;
        public string Offset;
    }

    private MarkerSnapshot CaptureMarker(RhinoDoc doc, Guid id)
    {
        var obj = doc?.Objects.FindId(id);
        var brep = GetBrepFromObject(obj);
        return new MarkerSnapshot
        {
            Geometry = brep?.DuplicateBrep(),
            Width = obj?.Attributes?.GetUserString("forsk:width"),
            Sill = obj?.Attributes?.GetUserString("forsk:sill"),
            Head = obj?.Attributes?.GetUserString("forsk:head"),
            T = obj?.Attributes?.GetUserString("forsk:t"),
            Offset = obj?.Attributes?.GetUserString("forsk:offset")
        };
    }

    private static void RestoreMarker(RhinoDoc doc, Guid id, MarkerSnapshot snap)
    {
        if (doc == null || snap == null || id == Guid.Empty) return;
        if (snap.Geometry != null)
        {
            var copy = snap.Geometry.DuplicateBrep();
            if (copy != null)
                doc.Objects.Replace(id, copy);
        }

        var obj = doc.Objects.FindId(id);
        if (obj?.Attributes == null) return;
        obj.Attributes.SetUserString("forsk:width", snap.Width);
        obj.Attributes.SetUserString("forsk:sill", snap.Sill);
        obj.Attributes.SetUserString("forsk:head", snap.Head);
        obj.Attributes.SetUserString("forsk:t", snap.T);
        obj.Attributes.SetUserString("forsk:offset", snap.Offset);
        obj.CommitChanges();
    }

    private static void WriteOpeningSize(RhinoDoc doc, Guid id, double width, double sill, double head)
    {
        if (doc == null || id == Guid.Empty) return;
        var obj = doc.Objects.FindId(id);
        if (obj?.Attributes == null) return;
        obj.Attributes.SetUserString("forsk:width", FormatMm(width));
        obj.Attributes.SetUserString("forsk:sill", FormatMm(sill));
        obj.Attributes.SetUserString("forsk:head", FormatMm(head));
        obj.CommitChanges();
    }

    private sealed class OpeningRemoval
    {
        public OpeningRecord Record;
        public Guid HostId;
        public string HostLabel;
        public Point3d Center;
    }

    private sealed class RemovedPiece
    {
        public RhinoObject Object;
        public int DefinitionIndex;
    }

    private sealed class KeptMarker
    {
        public Guid Id;
        public MarkerSnapshot Snap;
    }

    private sealed class HostUndo
    {
        public RhinoObject Wall;
        public Brep Brep;
        public ObjectAttributes Attr;
        public Guid HostBefore;
        public Guid HostAfter;
        public bool Committed;
        public List<RemovedPiece> Removed = new List<RemovedPiece>();
        public List<RemovedPiece> KeptFrames = new List<RemovedPiece>();
        public List<KeptMarker> KeptMarkers = new List<KeptMarker>();
        public List<Guid> NewBlocks = new List<Guid>();
    }

    private List<OpeningRemoval> ReadRemovals(List<RhinoObject> targets)
    {
        var doc = RhinoDoc.ActiveDoc;
        var removals = new List<OpeningRemoval>();
        var seen = new HashSet<Guid>();
        foreach (var target in targets)
        {
            var marker = ResolveOpeningHandle(target);
            RefuseExistingUnderlay(doc, marker);
            var rec = ReadOpeningRecord(marker);
            if (!seen.Add(rec.MarkerId)) continue;
            var host = ReadHostWall(doc, rec.HostId, requireVertical: true);
            var label = host.Attributes?.GetUserString("forsk:id");
            if (string.IsNullOrWhiteSpace(label)) label = host.Attributes?.Name;
            if (string.IsNullOrWhiteSpace(label)) label = "the wall";
            removals.Add(new OpeningRemoval
            {
                Record = rec,
                HostId = host.Id,
                HostLabel = label,
                Center = rec.MarkerBbox.Center
            });
        }
        if (removals.Count == 0)
            throw new InvalidOperationException("Opening marker not found.");
        return removals;
    }

    private static List<List<OpeningRemoval>> GroupRemovals(List<OpeningRemoval> removals)
    {
        var groups = new List<List<OpeningRemoval>>();
        foreach (var item in removals)
        {
            List<OpeningRemoval> found = null;
            foreach (var group in groups)
            {
                if (group[0].HostId == item.HostId)
                {
                    found = group;
                    break;
                }
            }
            if (found == null)
            {
                found = new List<OpeningRemoval>();
                groups.Add(found);
            }
            found.Add(item);
        }
        return groups;
    }

    private HostUndo PrepareHostUndo(RhinoDoc doc, List<OpeningRemoval> group)
    {
        var hostId = group[0].HostId;
        var wall = doc.Objects.FindId(hostId);
        var undo = new HostUndo
        {
            Wall = wall,
            Brep = GetBrepFromObject(wall)?.DuplicateBrep(),
            Attr = wall?.Attributes?.Duplicate(),
            HostBefore = hostId
        };
        var removing = new HashSet<Guid>();
        foreach (var item in group)
            removing.Add(item.Record.MarkerId);
        var forskId = wall?.Attributes?.GetUserString("forsk:id");
        foreach (var marker in MarkersOnHost(doc, hostId, forskId))
        {
            if (marker == null || removing.Contains(marker.Id)) continue;
            undo.KeptMarkers.Add(new KeptMarker
            {
                Id = marker.Id,
                Snap = CaptureMarker(doc, marker.Id)
            });
            var frameId = FindOpeningBlock(doc, marker.Id);
            if (frameId == Guid.Empty) continue;
            var frame = doc.Objects.FindId(frameId);
            if (frame == null) continue;
            undo.KeptFrames.Add(new RemovedPiece
            {
                Object = frame,
                DefinitionIndex = OpeningBlockDefIndex(frame)
            });
        }
        return undo;
    }

    private void RemoveOpeningPieces(RhinoDoc doc, Guid markerId, List<RemovedPiece> bag)
    {
        var key = markerId.ToString();
        var frames = new List<RhinoObject>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null) continue;
            if (!string.Equals(GetForskKind(obj), "opening", StringComparison.OrdinalIgnoreCase))
                continue;
            var mid = obj.Attributes.GetUserString("forsk:marker_id");
            if (!string.Equals(mid, key, StringComparison.OrdinalIgnoreCase)) continue;
            frames.Add(obj);
        }
        foreach (var frame in frames)
        {
            if (!TrackDelete(doc, frame, bag))
                throw new InvalidOperationException("Opening marker not found.");
        }
        var marker = doc.Objects.FindId(markerId);
        if (marker == null || !TrackDelete(doc, marker, bag))
            throw new InvalidOperationException("Opening marker not found.");
    }

    private static bool TrackDelete(RhinoDoc doc, RhinoObject obj, List<RemovedPiece> bag)
    {
        if (doc == null || obj == null) return false;
        var def = OpeningBlockDefIndex(obj);
        bag.Add(new RemovedPiece { Object = obj, DefinitionIndex = def });
        if (!doc.Objects.Delete(obj.Id, true)) return false;
        if (def >= 0)
            doc.InstanceDefinitions.Delete(def, true, true);
        return true;
    }

    private static void UndeletePieces(RhinoDoc doc, List<RemovedPiece> pieces)
    {
        if (doc == null || pieces == null) return;
        for (var i = pieces.Count - 1; i >= 0; i--)
        {
            var piece = pieces[i];
            if (piece == null || piece.DefinitionIndex < 0) continue;
            try { doc.InstanceDefinitions.Undelete(piece.DefinitionIndex); }
            catch (Exception) { }
        }
        for (var i = pieces.Count - 1; i >= 0; i--)
        {
            var piece = pieces[i];
            if (piece?.Object == null) continue;
            if (doc.Objects.FindId(piece.Object.Id) != null) continue;
            try { doc.Objects.Undelete(piece.Object); }
            catch (Exception) { }
        }
    }

    private void RollbackCommittedHost(RhinoDoc doc, HostUndo undo)
    {
        if (doc == null || undo == null) return;
        if (undo.NewBlocks != null)
        {
            foreach (var id in undo.NewBlocks)
            {
                var obj = doc.Objects.FindId(id);
                if (obj == null) continue;
                var def = OpeningBlockDefIndex(obj);
                doc.Objects.Delete(obj.Id, true);
                if (def >= 0)
                    doc.InstanceDefinitions.Delete(def, true, true);
            }
        }

        if (undo.Wall != null)
        {
            if (doc.Objects.FindId(undo.HostBefore) == null)
            {
                try { doc.Objects.Undelete(undo.Wall); }
                catch (Exception) { }
            }
            else if (undo.Brep != null)
            {
                var copy = undo.Brep.DuplicateBrep();
                if (copy != null)
                    doc.Objects.Replace(undo.HostBefore, copy);
            }
            if (undo.Attr != null && doc.Objects.FindId(undo.HostBefore) != null)
                doc.Objects.ModifyAttributes(undo.HostBefore, undo.Attr.Duplicate(), true);
        }
        if (undo.HostAfter != Guid.Empty && undo.HostAfter != undo.HostBefore)
        {
            var extra = doc.Objects.FindId(undo.HostAfter);
            if (extra != null)
                doc.Objects.Delete(undo.HostAfter, true);
        }

        if (undo.KeptMarkers != null)
        {
            foreach (var kept in undo.KeptMarkers)
                RestoreMarker(doc, kept.Id, kept.Snap);
        }
        UndeletePieces(doc, undo.KeptFrames);
        UndeletePieces(doc, undo.Removed);
    }

    private JObject DeleteOpeningResult(
        RhinoDoc doc,
        List<OpeningRemoval> removals,
        List<HostUndo> commits,
        HashSet<Guid> before,
        double tol)
    {
        var windows = 0;
        var doors = 0;
        var labels = new List<string>();
        foreach (var item in removals)
        {
            if (item.Record.Kind == OpeningKind.Window) windows++;
            else doors++;
            var seen = false;
            foreach (var label in labels)
            {
                if (string.Equals(label, item.HostLabel, StringComparison.Ordinal))
                {
                    seen = true;
                    break;
                }
            }
            if (!seen) labels.Add(item.HostLabel);
        }

        var hostIds = new HashSet<Guid>();
        var deleted = new JArray();
        var ids = new JArray();
        foreach (var item in removals)
        {
            var hostId = item.HostId;
            foreach (var undo in commits)
            {
                if (undo.HostBefore != item.HostId || undo.HostAfter == Guid.Empty) continue;
                hostId = undo.HostAfter;
                break;
            }
            hostIds.Add(hostId);
            var inside = PointInside(GetBrepFromObject(doc.Objects.FindId(hostId)), item.Center, tol);
            deleted.Add(new JObject
            {
                ["id"] = item.Record.MarkerId.ToString(),
                ["host_id"] = hostId.ToString(),
                ["x"] = item.Center.X,
                ["y"] = item.Center.Y,
                ["z"] = item.Center.Z,
                ["inside"] = inside
            });
            ids.Add(item.Record.MarkerId.ToString());
        }

        var openings = 0;
        var voids = 0;
        var maxFrame = 0.0;
        var markers = new JArray();
        foreach (var undo in commits)
        {
            var report = HostOpeningReport(doc, undo.HostAfter, Guid.Empty, Point3d.Unset);
            if (report == null) continue;
            openings += report["host_openings"]?.ToObject<int>() ?? 0;
            voids += report["host_voids"]?.ToObject<int>() ?? 0;
            var frame = report["max_frame_mm"]?.ToObject<double>() ?? 0;
            if (frame > maxFrame) maxFrame = frame;
            if (report["markers"] is JArray rows)
            {
                foreach (var row in rows)
                    markers.Add(row.DeepClone());
            }
        }

        var firstHost = removals[0].HostId;
        foreach (var undo in commits)
        {
            if (undo.HostBefore != removals[0].HostId || undo.HostAfter == Guid.Empty) continue;
            firstHost = undo.HostAfter;
            break;
        }

        return new JObject
        {
            ["deleted_marker_id"] = removals[0].Record.MarkerId.ToString(),
            ["deleted_marker_ids"] = ids,
            ["deleted"] = deleted,
            ["host_id"] = firstHost.ToString(),
            ["host_openings"] = openings,
            ["host_voids"] = voids,
            ["max_frame_mm"] = maxFrame,
            ["markers"] = markers,
            ["plate_count"] = CountStrayObjects(doc, before, hostIds),
            ["ok"] = true,
            ["message"] = SoftParamPlan.RemovalLine(windows, doors, labels)
        };
    }

    private static HashSet<Guid> SnapshotObjectIds(RhinoDoc doc)
    {
        var ids = new HashSet<Guid>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj != null) ids.Add(obj.Id);
        }
        return ids;
    }

    private static int CountStrayObjects(RhinoDoc doc, HashSet<Guid> before, HashSet<Guid> hostIds)
    {
        var plates = 0;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj == null || before.Contains(obj.Id)) continue;
            var kind = GetForskKind(obj);
            if (string.Equals(kind, "opening", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kind, "opening_marker", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kind, "wall", StringComparison.OrdinalIgnoreCase)
                && hostIds != null && hostIds.Contains(obj.Id))
                continue;
            plates++;
        }
        return plates;
    }

    private static List<Guid> GuidList(JArray array)
    {
        var list = new List<Guid>();
        if (array == null) return list;
        foreach (var token in array)
        {
            if (Guid.TryParse(token?.ToString(), out var id))
                list.Add(id);
        }
        return list;
    }
}
