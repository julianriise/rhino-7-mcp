using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
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
        // R5: stairs drawn as their plan symbol.
        public int Stairs;
        // FU.2: furniture drawn as its plan symbol.
        public int Furniture;
        // Every room marker ends in exactly one of three counts: Rooms (tagged:
        // its name is on the sheet), RoomsTooSmall (under the 1 m² room cutoff),
        // RoomsNoOutline (no outline to read). Of the
        // tagged rooms, RoomAreasDropped show the name alone (the ~ X m² line
        // did not fit), RoomsLeader have their tag outside the room on a
        // leader (the name did not fit), and RoomsOverflow are those whose name
        // runs past the room edge (no clear spot for a leader either).
        // RoomsUntagged says why, by room id, for each room without a tag;
        // RoomsLeading and RoomsOverflowing give those names and widths.
        public int Rooms;
        public int RoomAreasDropped;
        public int RoomsLeader;
        public int RoomsOverflow;
        public int RoomsTooSmall;
        public int RoomsNoOutline;
        public List<string> RoomsUntagged;
        public List<string> RoomsLeading;
        public List<string> RoomsOverflowing;
        public int Skipped;
        // Openings whose mark (forsk:mark) is printed beside them, and those
        // that found no spot clear of the room tags ("D08 on Wet Room").
        public int Marks;
        public List<string> MarksOnTags;
        // F5.2: chains drawn (exterior and room), their values, chains with no
        // clear place (left out), values with no clear spot, and the openings
        // in an outer wall with those on a drawn facade chain.
        public int Dims;
        public int DimsExterior;
        public int DimsRoom;
        public int DimTexts;
        public int DimsSkipped;
        public int DimsCollisions;
        public int DimOpenings;
        public int DimOpeningsShown;
        // F5.3: the sections whose marker A–A is on the plan, and those whose
        // marker found no spot clear of everything (drawn at its first spot).
        public List<string> SectionMarkers;
        public List<string> SectionMarkersBlocked;
        // The details whose callout is on the plan, and those with no clear spot (not drawn).
        public List<string> Callouts;
        public List<string> CalloutsBlocked;
        public string Note;
        public string RoomText;
    }

    // F5.4: the tiers come from the active print profile. The default is 0.50 / 0.35 / 0.18 / 0.13 mm, black.
    private static PrintPen PenCut => PrintProfiles.Active.Cut;
    private static PrintPen PenSilhouette => PrintProfiles.Active.Silhouette;
    private static PrintPen PenBeyond => PrintProfiles.Active.Beyond;
    private static PrintPen PenThin => PrintProfiles.Active.Thin;

    /// <summary>
    /// Replace plan centre-lines with width ribbons, then add symbols with
    /// their marks, the dashed roof outline, room tags, the dimensions and
    /// the section markers. Returns false when
    /// nothing was added so the caller keeps the v1 curves.
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
        stats = new PlanStats
        {
            RoomsUntagged = new List<string>(),
            RoomsLeading = new List<string>(),
            RoomsOverflowing = new List<string>(),
            MarksOnTags = new List<string>(),
            SectionMarkers = new List<string>(),
            SectionMarkersBlocked = new List<string>(),
            Callouts = new List<string>(),
            CalloutsBlocked = new List<string>()
        };
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
                added += AddStroke(doc, layer, curve, ForskTechnical.PenFor("greyscale", null, PrintProfiles.Active), scale, false, pattern, tol,
                    "greyscale", null, null, null, ref box, ref index, ref count);
            }
        }

        foreach (var loop in loops)
        {
            added += AddStroke(doc, layer, loop, ForskTechnical.PenFor("cut", null, PrintProfiles.Active), scale, false, pattern, tol,
                "cut", null, null, null, ref box, ref index, ref count);
        }

        // One read of the rooms for the tags, the marks' sides and the room
        // dimensions; the marks are stamped before the symbols queue them.
        // Tags go down first (a name too wide for its room on a leader), then
        // each mark takes the first spot clear of them, then the dimensions
        // go around all of it.
        var rooms = PlanRooms(doc);
        ScheduleOpenings(doc, rooms);
        var poche = PocheRings(fillGroups, tol);
        var pending = new List<PendingMark>();
        added += BakeOpeningSymbols(
            doc, layer, scale, cutZ, worldToHld, delta, pattern, tol, rooms, pending,
            ref box, ref index, ref count, ref stats);
        added += BakeRoofOutlines(
            doc, layer, scale, worldToHld, delta, pattern, tol,
            ref box, ref index, ref count, ref stats);
        added += BakeStairSymbols(
            doc, layer, scale, cutZ, worldToHld, delta, pattern, tol,
            ref box, ref index, ref count, ref stats);
        added += BakeFurnitureSymbols(
            doc, layer, scale, worldToHld, delta, pattern, tol,
            ref box, ref index, ref count, ref stats);
        var tagBoxes = new List<RoomDetect.Box>();
        var leaders = new List<RoomDetect.Box>();
        added += BakeRoomTags(
            doc, layer, scale, worldToHld, delta, rooms, poche, pattern, tol, tagBoxes, leaders,
            ref box, ref index, ref count, ref stats);
        added += BakeMarks(doc, layer, scale, worldToHld, delta, pending, tagBoxes, leaders, poche,
            ref box, ref index, ref count, ref stats);
        added += BakeDimensions(doc, layer, scale, worldToHld, delta, pending, rooms, poche, pattern, tol,
            ref box, ref index, ref count, ref stats);
        added += BakeSectionMarkers(doc, layer, scale, worldToHld, delta, pattern, tol,
            ref box, ref index, ref count, ref stats);
        added += BakeDetailCallouts(doc, layer, scale, worldToHld, delta, poche, pattern, tol,
            ref box, ref index, ref count, ref stats);

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
        List<PlanRoom> rooms,
        List<PendingMark> pending,
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
            List<OpeningTypes.PlanMark> marks;
            Plane plane;
            OpeningTypes.PlanFrame frame;
            OpeningTypes.Record record;
            try
            {
                if (!TryOpeningMarks(doc, marker, cutZ, out record, out plane, out frame, out marks))
                {
                    stats.Skipped++;
                    continue;
                }
            }
            catch (Exception)
            {
                stats.Skipped++;
                continue;
            }

            var baked = 0;
            var markerId = marker.Id.ToString();
            var faceHi = MapPlan(0, frame.HalfThick, plane, worldToHld, delta);
            var faceLo = MapPlan(0, -frame.HalfThick, plane, worldToHld, delta);
            var faces = new SymbolStamp { Faces = FaceStamp(faceHi, faceLo) };
            // The host wall's length along this opening's run, for the smoke.
            if (TryWallRun(doc, marker, out _, out var runSeg, out _, out _, out _))
            {
                faces.Run = FaceStamp(
                    ToDrawing(runSeg.Start, worldToHld, delta),
                    ToDrawing(runSeg.End, worldToHld, delta));
            }
            foreach (var mark in marks)
            {
                Curve curve = null;
                try
                {
                    curve = MarkCurve(mark, plane, worldToHld, delta);
                    if (curve == null) continue;
                    var onWall = mark.Part == "sill" || mark.Part == "frame" || mark.Part == "jamb";
                    var n = AddStroke(doc, layer, curve, ForskTechnical.PenFor("symbol", mark.Part, PrintProfiles.Active), scale, mark.Dashed, pattern, tol,
                        "symbol", mark.Part, markerId,
                        mark.Part == "leaf" || mark.Part == "arc" ? mark.Y1 * frame.YInward : (double?)null,
                        ref box, ref index, ref count, onWall ? faces : null);
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
                var mark = marker.Attributes.GetUserString(Schedules.MarkKey);
                if (!string.IsNullOrEmpty(mark))
                {
                    pending.Add(new PendingMark
                    {
                        Mark = mark,
                        MarkerId = markerId,
                        Plane = plane,
                        Side = Schedules.MarkSide(record, frame.YInward, OpeningRooms(rooms, plane, frame.HalfThick)),
                        HalfThick = frame.HalfThick,
                        HalfWidth = frame.VoidHalf
                    });
                }
            }
            else
                stats.Skipped++;
        }
        return added;
    }

    /// <summary>An opening's mark, queued until the room tags are down.</summary>
    private sealed class PendingMark
    {
        public string Mark;
        public string MarkerId;
        public Plane Plane;
        public int Side;
        public double HalfThick;
        public double HalfWidth;
    }

    /// <summary>
    /// The section fill loops as rings in drawing mm, one list per wall mass
    /// (its outer loop and holes, read even-odd): the poché a mark must not
    /// sit on.
    /// </summary>
    private static List<List<List<RoomDetect.Pt>>> PocheRings(List<List<Curve>> fillGroups, double tol)
    {
        var walls = new List<List<List<RoomDetect.Pt>>>();
        if (fillGroups == null) return walls;
        foreach (var group in fillGroups)
        {
            var rings = new List<List<RoomDetect.Pt>>();
            foreach (var curve in group ?? new List<Curve>())
            {
                if (curve == null || !curve.IsClosed) continue;
                var ring = new List<RoomDetect.Pt>();
                if (curve.TryGetPolyline(out Polyline poly) && poly != null && poly.Count >= 3)
                {
                    foreach (var point in poly) ring.Add(new RoomDetect.Pt(point.X, point.Y));
                }
                else
                {
                    var ts = curve.DivideByCount(Math.Min(Math.Max(curve.SpanCount * 4, 16), 128), true);
                    if (ts == null) continue;
                    foreach (var t in ts)
                    {
                        var point = curve.PointAt(t);
                        ring.Add(new RoomDetect.Pt(point.X, point.Y));
                    }
                }
                if (ring.Count >= 3) rings.Add(ring);
            }
            if (rings.Count > 0) walls.Add(rings);
        }
        return walls;
    }

    /// <summary>
    /// The marks beside their openings, Schedules.MarkMm tall on paper, each
    /// at the first spot Schedules.PlaceMark finds clear of the room tags and
    /// their leaders, of the marks placed before it (1 mm on paper), and of
    /// the wall poché. A mark with no clear spot keeps its first choice and is
    /// listed in MarksOnTags when it touches a tag.
    /// </summary>
    private static int BakeMarks(
        RhinoDoc doc, Layer layer, int scale, Transform worldToHld, Vector3d delta,
        List<PendingMark> pending, List<RoomDetect.Box> tagBoxes, List<RoomDetect.Box> leaders,
        List<List<List<RoomDetect.Pt>>> poche,
        ref BoundingBox box, ref int index, ref int count, ref PlanStats stats)
    {
        var height = Schedules.MarkMm * scale;
        if (height <= 0) return 0;
        var gap = 1.0 * scale;
        var taken = new List<RoomDetect.Box>(tagBoxes);
        taken.AddRange(leaders);
        var added = 0;
        foreach (var item in pending)
        {
            var at = MapPlan(0, 0, item.Plane, worldToHld, delta);
            var along = MapPlan(1, 0, item.Plane, worldToHld, delta) - at;
            var outward = MapPlan(0, item.Side, item.Plane, worldToHld, delta) - at;
            along.Z = 0;
            outward.Z = 0;
            if (!along.Unitize() || !outward.Unitize()) continue;
            var textPlane = Plane.WorldXY;
            textPlane.Origin = at;
            var entity = PlanAnnotation(doc, item.Mark, textPlane, height);
            if (entity == null) continue;
            var id = Guid.Empty;
            var placed = default(RoomDetect.Box);
            try
            {
                var extent = entity.GetBoundingBox(true);
                var spot = new Schedules.MarkSpot
                {
                    At = new RoomDetect.Pt(at.X, at.Y),
                    Along = new RoomDetect.Pt(along.X, along.Y),
                    Out = new RoomDetect.Pt(outward.X, outward.Y),
                    HalfThick = item.HalfThick,
                    HalfWidth = item.HalfWidth,
                    Hx = extent.IsValid ? (extent.Max.X - extent.Min.X) / 2.0 : 0.35 * height * item.Mark.Length,
                    Hy = extent.IsValid ? (extent.Max.Y - extent.Min.Y) / 2.0 : 0.75 * height,
                    Gap = gap
                };
                Schedules.PlaceMark(spot, taken, poche, out var centre);
                // The text is centred on its plane origin, which is at.
                entity.Translate(new Vector3d(centre.X - at.X, centre.Y - at.Y, 0));
                placed = Schedules.MarkBox(centre, spot.Hx, spot.Hy);
                var attr = DrawAttr(layer, FormatStableId("d", index), "opening_mark", null, item.MarkerId);
                attr.SetUserString(Schedules.MarkKey, item.Mark);
                id = doc.Objects.AddText(entity, attr);
            }
            catch (Exception)
            {
                id = Guid.Empty;
            }
            finally
            {
                entity.Dispose();
            }
            if (id == Guid.Empty) continue;
            var written = doc.Objects.FindId(id);
            if (written?.Geometry is TextEntity stored)
            {
                var model = stored.TextHeight * (stored.DimensionScale > 0 ? stored.DimensionScale : 1.0);
                var paper = OpeningTypes.PaperTextHeight(model, scale, doc.LayoutSpaceAnnotationScalingEnabled);
                written.Attributes.SetUserString("forsk:paper_height", paper.ToString("0.###", CultureInfo.InvariantCulture));
                written.CommitChanges();
                var stamp = stored.GetBoundingBox(true);
                box.Union(stamp);
                if (stamp.IsValid)
                    placed = new RoomDetect.Box(stamp.Min.X, stamp.Min.Y, stamp.Max.X, stamp.Max.Y);
            }
            foreach (var tag in tagBoxes)
            {
                if (!Schedules.Overlaps(placed, tag, 0)) continue;
                stats.MarksOnTags.Add(item.Mark);
                break;
            }
            taken.Add(placed);
            index++;
            count++;
            added++;
            stats.Marks++;
        }
        return added;
    }

    /// <summary>
    /// An opening's plan symbol as the plan sheet draws it, in its symbol
    /// plane: the type's marks, then the jambs (a window) or the wall frame.
    /// The screen's plan linework reads the same marks.
    /// </summary>
    private bool TryOpeningMarks(
        RhinoDoc doc,
        RhinoObject marker,
        double cutZ,
        out OpeningTypes.Record record,
        out Plane plane,
        out OpeningTypes.PlanFrame frame,
        out List<OpeningTypes.PlanMark> marks)
    {
        marks = null;
        if (!TrySymbolFrame(doc, marker, out record, out plane, out frame)) return false;
        frame.CutZ = cutZ;
        marks = OpeningTypes.PlanSymbol(record, "1:100", frame);
        if (marks == null || marks.Count == 0) return false;
        if (string.Equals(record.Kind, "window", StringComparison.OrdinalIgnoreCase))
            OpeningTypes.AddJambs(marks, frame);
        else
            OpeningTypes.AddWallFrame(marks, frame);
        return true;
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

        var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (!box.IsValid) return false;
        var opening = box.Center;
        opening.Z = 0;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var segs = SegmentsFromPath(host.Attributes?.GetUserString("forsk:path"), tol);
        if (!TryOffsetOnSegments(segs, opening, out var segment, out _, out _) || segment == null)
            return false;

        var widthDir = segment.Tangent;
        widthDir.Z = 0;
        if (!widthDir.Unitize()) return false;
        var inward = segment.Inward;
        inward.Z = 0;
        if (!inward.Unitize()) return false;

        // The path segment is one face. The symbol spans the solid's real
        // faces at this opening, not the frame depth and not a 200 mm default.
        Brep measured = null;
        var disposeMeasured = false;
        if (host.Geometry is Brep solid)
            measured = solid;
        else if (host.Geometry is Extrusion extrusion)
        {
            measured = extrusion.ToBrep();
            disposeMeasured = measured != null;
        }
        double thickness = 0;
        Point3d axis = Point3d.Origin;
        var measuredOk = false;
        if (measured != null)
        {
            measuredOk = TryMeasureWallThickness(
                measured, opening, widthDir, inward, sill, head, width, FacadeConst.Pad, tol,
                out thickness, out axis);
        }
        if (disposeMeasured) measured?.Dispose();
        if (!measuredOk || thickness < 40) return false;
        double cx, cy;
        OpeningTypes.WallCenter(
            opening.X, opening.Y, axis.X, axis.Y, inward.X, inward.Y, out cx, out cy);
        var center = new Point3d(cx, cy, 0);
        if (!TryOpeningPlane(center, widthDir, inward, out plane)) return false;
        OpeningFacing(plane, inward, out var yInward, out var xLeft);

        var outerHalf = width * 0.5 + FacadeConst.Pad - OpeningFrameInsetMm;
        var halfThick = thickness * 0.5;
        var clear = head - sill - (2.0 * OpeningFrameInsetMm);
        if (clear < 80 || outerHalf < 30 || halfThick < 8) return false;
        var window = string.Equals(record.Kind, "window", StringComparison.OrdinalIgnoreCase);
        var face = OpeningElement.FrameFace(outerHalf, clear, window);
        var innerHalf = outerHalf - face;
        if (innerHalf < 15) return false;

        frame = new OpeningTypes.PlanFrame
        {
            OuterHalf = outerHalf,
            InnerHalf = innerHalf,
            HalfThick = halfThick,
            VoidHalf = width * 0.5,
            Sill = sill,
            Head = head,
            YInward = yInward,
            XLeft = xLeft
        };
        return true;
    }

    /// <summary>
    /// A pocket door parks where its wall has room. With no hand given, a
    /// pocket that would run past the wall end or into the next opening
    /// parks the other way. With no room on either side the edit is refused.
    /// </summary>
    private void ParkPocket(RhinoDoc doc, Guid markerId, OpeningTypes.Edit edit, bool handFree)
    {
        var after = edit?.After;
        if (after == null || !string.Equals(after.TypeId, "door.pocket", StringComparison.Ordinal))
            return;
        var marker = doc.Objects.FindId(markerId);
        if (marker?.Attributes == null) return;
        if (!TrySymbolFrame(doc, marker, out _, out var plane, out var frame)) return;
        if (!TryWallRoom(doc, marker, plane, out var roomNeg, out var roomPos)) return;
        var reach = OpeningTypes.PocketReach(frame);
        if (!OpeningTypes.PocketHand(after.Hand, frame.XLeft, roomNeg, roomPos, reach, out var chosen))
            throw new InvalidOperationException("No room in the wall for the pocket.");
        if (string.Equals(chosen, after.Hand, StringComparison.OrdinalIgnoreCase)) return;
        if (!handFree)
            throw new InvalidOperationException("No room in the wall for the pocket on that side.");
        after.Hand = chosen;
        edit.HandChanged = !string.Equals(edit.Before?.Hand ?? "", chosen, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wall run an opening sits on: its segment, the centre along it,
    /// and the clear run [lo, hi] inside the cross walls of an outer face.
    /// </summary>
    private bool TryWallRun(
        RhinoDoc doc, RhinoObject marker,
        out List<WallSegment> segs, out WallSegment seg, out double at, out double lo, out double hi)
    {
        segs = null;
        seg = null;
        at = lo = hi = 0;
        var hostRaw = marker?.Attributes?.GetUserString("forsk:host");
        if (!Guid.TryParse(hostRaw, out var hostId)) return false;
        var host = doc.Objects.FindId(hostId);
        if (host == null) return false;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        segs = SegmentsFromPath(host.Attributes?.GetUserString("forsk:path"), tol);
        var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (!box.IsValid) return false;
        var center = box.Center;
        center.Z = 0;
        if (!TryOffsetOnSegments(segs, center, out seg, out var t, out _) || seg == null)
            return false;
        at = t * seg.Length;
        var reserve = seg.FromOuter ? seg.Thickness : 0;
        lo = reserve;
        hi = seg.Length - reserve;
        return hi > lo;
    }

    private static bool SameRun(WallSegment a, WallSegment b)
    {
        return a != null && b != null
            && a.Start.DistanceTo(b.Start) < 1.0 && a.End.DistanceTo(b.End) < 1.0;
    }

    /// <summary>
    /// Clear wall on each side of the opening centre, in the symbol plane's
    /// -X and +X: to the next opening on the same run, or to the run's end
    /// (inside the cross wall when the run is an outer face).
    /// </summary>
    private bool TryWallRoom(RhinoDoc doc, RhinoObject marker, Plane plane, out double roomNeg, out double roomPos)
    {
        roomNeg = 0;
        roomPos = 0;
        if (!TryWallRun(doc, marker, out var segs, out var seg, out var at, out var lo, out var hi)) return false;
        var hostRaw = marker.Attributes.GetUserString("forsk:host");
        foreach (var other in EnumerateDocObjects(doc))
        {
            if (other?.Attributes == null || other.Id == marker.Id) continue;
            if (!string.Equals(GetForskKind(other), "opening_marker", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(other.Attributes.GetUserString("forsk:host"), hostRaw, StringComparison.OrdinalIgnoreCase))
                continue;
            var ob = other.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!ob.IsValid) continue;
            var oc = ob.Center;
            oc.Z = 0;
            if (!TryOffsetOnSegments(segs, oc, out var oseg, out _, out _) || !SameRun(oseg, seg))
                continue;
            double smin = double.PositiveInfinity, smax = double.NegativeInfinity;
            foreach (var corner in ob.GetCorners())
            {
                var along = (new Point3d(corner.X, corner.Y, 0) - seg.Start) * seg.Tangent;
                smin = Math.Min(smin, along);
                smax = Math.Max(smax, along);
            }
            if (smax <= at) lo = Math.Max(lo, smax);
            else if (smin >= at) hi = Math.Min(hi, smin);
        }
        var back = Math.Max(0, at - lo);
        var ahead = Math.Max(0, hi - at);
        var forward = plane.XAxis * seg.Tangent >= 0;
        roomPos = forward ? ahead : back;
        roomNeg = forward ? back : ahead;
        return true;
    }

    private static string FaceStamp(Point3d a, Point3d b)
    {
        return a.X.ToString("0.###", CultureInfo.InvariantCulture) + ","
            + a.Y.ToString("0.###", CultureInfo.InvariantCulture) + ";"
            + b.X.ToString("0.###", CultureInfo.InvariantCulture) + ","
            + b.Y.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Wall faces and clear wall run at one opening, drawing coordinates; or,
    /// on any stroke, extra stamps (a dimension's chain).
    /// </summary>
    private sealed class SymbolStamp
    {
        public string Faces;
        public string Run;
        public IDictionary<string, string> Extra;
    }

    private static void StampSymbolLine(ObjectAttributes attr, Curve curve, string part, SymbolStamp faces)
    {
        if (attr == null || curve == null) return;
        if (faces?.Extra != null)
            foreach (var pair in faces.Extra)
                attr.SetUserString(pair.Key, pair.Value);
        if (part != "sill" && part != "frame" && part != "jamb") return;
        var a = curve.PointAtStart;
        var b = curve.PointAtEnd;
        attr.SetUserString("forsk:line",
            a.X.ToString("0.###", CultureInfo.InvariantCulture) + ","
            + a.Y.ToString("0.###", CultureInfo.InvariantCulture) + ";"
            + b.X.ToString("0.###", CultureInfo.InvariantCulture) + ","
            + b.Y.ToString("0.###", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(faces?.Faces))
            attr.SetUserString("forsk:faces", faces.Faces);
        if (!string.IsNullOrEmpty(faces?.Run))
            attr.SetUserString("forsk:run", faces.Run);
    }

    private static Curve MarkCurve(OpeningTypes.PlanMark mark, Plane plane, Transform worldToHld, Vector3d delta)
    {
        if (mark == null) return null;
        if (mark.Shape == "arc" && mark.Radius > 1 && ForskTechnical.ArcMid(mark, out var mx, out var my))
        {
            var start = MapPlan(mark.X0, mark.Y0, plane, worldToHld, delta);
            var end = MapPlan(mark.X1, mark.Y1, plane, worldToHld, delta);
            var mid = MapPlan(mx, my, plane, worldToHld, delta);
            var arc = new Arc(start, mid, end);
            if (arc.IsValid) return new ArcCurve(arc);
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
                    var n = AddStroke(doc, layer, curve, PenThin, scale, true, pattern, tol,
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

    /// <summary>
    /// R5: each stair as its plan symbol (Stairs.PlanSymbol) from its record:
    /// the outline at the beyond pen, the steps, walking line, arrow and break
    /// thin, the steps above the plan cut dashed, the start dot filled and the
    /// label along the climb, reading up or to the right. Role stair, so a DWG
    /// export puts all of it on A-STAIR.
    /// </summary>
    private int BakeStairSymbols(
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
        foreach (var obj in StairObjects(doc))
        {
            if (!TryStairAsBuilt(obj, out var spec, out var flight)) continue;
            var frame = ForskTechnical.StairFrame(spec);
            var plane = new Plane(new Point3d(frame.Ox, frame.Oy, 0), new Vector3d(frame.Xx, frame.Xy, 0), new Vector3d(frame.Yx, frame.Yy, 0));
            var stairId = obj.Id.ToString();
            var baked = 0;
            foreach (var mark in Stairs.PlanSymbol(flight, cutZ - spec.Z, scale, Stairs.LabelHeight(flight, scale)))
            {
                try
                {
                    if (mark.Shape == "line")
                    {
                        using (var curve = new LineCurve(
                            MapPlan(mark.U0, mark.V0, plane, worldToHld, delta),
                            MapPlan(mark.U1, mark.V1, plane, worldToHld, delta)))
                        {
                            var pen = ForskTechnical.PenFor("stair", mark.Part, PrintProfiles.Active);
                            baked += AddStroke(doc, layer, curve, pen, scale, mark.Dashed, pattern, tol,
                                "stair", mark.Part, stairId, null, ref box, ref index, ref count);
                        }
                    }
                    else if (mark.Shape == "dot")
                        baked += AddStairDot(doc, layer, MapPlan(mark.U0, mark.V0, plane, worldToHld, delta), mark.Radius,
                            pattern, tol, stairId, ref box, ref index, ref count);
                    else if (mark.Shape == "text")
                        baked += AddStairLabel(doc, layer, mark, plane, worldToHld, delta, scale, stairId, ref box, ref index, ref count);
                }
                catch (Exception)
                {
                    // One piece that will not draw leaves the rest of the symbol.
                }
            }
            if (baked > 0) stats.Stairs++;
            added += baked;
        }
        return added;
    }

    private static int AddStairDot(
        RhinoDoc doc, Layer layer, Point3d at, double radius, int pattern, double tol, string stairId,
        ref BoundingBox box, ref int index, ref int count)
    {
        if (radius <= 0) return 0;
        Hatch[] hatches;
        using (var circle = new ArcCurve(new Circle(at, radius)))
        {
            try { hatches = Hatch.Create(circle, pattern, 0.0, 1.0, Math.Max(tol, 0.01)); }
            catch (Exception) { hatches = null; }
        }
        if (hatches == null) return 0;
        var added = 0;
        foreach (var hatch in hatches)
        {
            if (hatch == null) continue;
            var attr = DrawAttr(layer, FormatStableId("d", index), "stair", "dot", stairId);
            Guid id;
            try { id = doc.Objects.AddHatch(hatch, attr); }
            catch (Exception) { id = Guid.Empty; }
            var dot = hatch.GetBoundingBox(true);
            hatch.Dispose();
            if (id == Guid.Empty) continue;
            if (dot.IsValid) box.Union(dot);
            index++;
            count++;
            added++;
        }
        return added;
    }

    private static int AddStairLabel(
        RhinoDoc doc, Layer layer, Stairs.Mark mark, Plane plane, Transform worldToHld, Vector3d delta, int scale, string stairId,
        ref BoundingBox box, ref int index, ref int count)
    {
        var at = MapPlan(mark.U0, mark.V0, plane, worldToHld, delta);
        var along = MapPlan(mark.U0 + 1000, mark.V0, plane, worldToHld, delta) - at;
        along.Z = 0;
        if (!along.Unitize()) return 0;
        // Text reads left to right, or bottom to top on a stair that climbs south.
        if (along.X < -1e-9 || Math.Abs(along.X) <= 1e-9 && along.Y < 0) along = -along;
        var textPlane = new Plane(at, along, new Vector3d(-along.Y, along.X, 0));
        var entity = PlanAnnotation(doc, mark.Text, textPlane, mark.Radius);
        if (entity == null) return 0;
        Guid id;
        try
        {
            var attr = DrawAttr(layer, FormatStableId("d", index), "stair", "label", stairId);
            id = doc.Objects.AddText(entity, attr);
        }
        catch (Exception) { id = Guid.Empty; }
        finally { entity.Dispose(); }
        if (id == Guid.Empty) return 0;
        var written = doc.Objects.FindId(id);
        if (written?.Geometry is TextEntity stored)
        {
            var model = stored.TextHeight * (stored.DimensionScale > 0 ? stored.DimensionScale : 1.0);
            var paper = OpeningTypes.PaperTextHeight(model, scale, doc.LayoutSpaceAnnotationScalingEnabled);
            written.Attributes.SetUserString("forsk:paper_height", paper.ToString("0.###", CultureInfo.InvariantCulture));
            written.CommitChanges();
            box.Union(stored.GetBoundingBox(true));
        }
        index++;
        count++;
        return 1;
    }
}
