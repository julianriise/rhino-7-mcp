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
        // Every room marker ends in exactly one of three counts: Rooms (tagged:
        // its name is on the sheet), RoomsTooSmall (under the 1 m² room cutoff),
        // RoomsNoOutline (no outline to read). Of the
        // tagged rooms, RoomAreasDropped show the name alone (the ≈ X m² line
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
            SectionMarkersBlocked = new List<string>()
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
                added += AddStroke(doc, layer, curve, PenBeyond, scale, false, pattern, tol,
                    "greyscale", null, null, null, ref box, ref index, ref count);
            }
        }

        foreach (var loop in loops)
        {
            added += AddStroke(doc, layer, loop, PenCut, scale, false, pattern, tol,
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
            List<OpeningTypes.PlanMark> marks = null;
            Plane plane = Plane.Unset;
            OpeningTypes.PlanFrame frame = null;
            OpeningTypes.Record record = null;
            try
            {
                if (!TrySymbolFrame(doc, marker, out record, out plane, out frame))
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
            if (string.Equals(record.Kind, "window", StringComparison.OrdinalIgnoreCase))
                OpeningTypes.AddJambs(marks, frame);
            else
                OpeningTypes.AddWallFrame(marks, frame);

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
                    var n = AddStroke(doc, layer, curve, PenThin, scale, mark.Dashed, pattern, tol,
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
            var plane = new Plane(new Point3d(spec.X, spec.Y, 0), new Vector3d(spec.Dx, spec.Dy, 0), new Vector3d(-spec.Dy, spec.Dx, 0));
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
                            var pen = mark.Part == "outline" ? PenBeyond : PenThin;
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

    private int BakeRoomTags(
        RhinoDoc doc,
        Layer layer,
        int scale,
        Transform worldToHld,
        Vector3d delta,
        List<PlanRoom> rooms,
        List<List<List<RoomDetect.Pt>>> poche,
        int pattern,
        double tol,
        List<RoomDetect.Box> tagBoxes,
        List<RoomDetect.Box> leaders,
        ref BoundingBox box,
        ref int index,
        ref int count,
        ref PlanStats stats)
    {
        var added = 0;
        var texts = new List<string>();
        var height = OpeningTypes.PlanAnnotationHeight(scale);
        var loose = new List<PlanRoom>();
        foreach (var planRoom in rooms)
        {
            var roomId = planRoom.RoomId;
            var who = planRoom.Who;
            // A marker with no outline to read gets no tag.
            if (planRoom.Ring == null)
            {
                stats.RoomsNoOutline++;
                stats.RoomsUntagged.Add(who + ": no outline to tag");
                continue;
            }
            var name = planRoom.Name;
            if (planRoom.Untagged != null)
            {
                stats.RoomsTooSmall++;
                stats.RoomsUntagged.Add(who + " " + name + ": " + planRoom.Untagged);
                continue;
            }
            var inside = planRoom.Inside;
            var stamps = RoomStamps(roomId);
            var at = new Point3d(inside.X, inside.Y, 0);
            var room = RoomStamp(planRoom.Ring.Select(p => ToDrawing(p, worldToHld, delta)).ToList());
            var origin = ToDrawing(at, worldToHld, delta);

            // The tag is the name over ca. X m². A room too small for both shows
            // its name alone, centred. A name wider than its room goes outside
            // on a leader once every tag that fits is down. Text stays 2.5 mm
            // on paper.
            var line = OpeningTypes.RoomTag(planRoom.Area);
            var areaId = AddPlanText(doc, layer, line, origin, scale, "room_tag", "area", room, stamps, ref box, ref index, ref count, false, out _);
            var nameId = Guid.Empty;
            if (areaId != Guid.Empty)
            {
                var nameAt = ToDrawing(at + new Vector3d(0, height * 1.15, 0), worldToHld, delta);
                nameId = AddPlanText(doc, layer, name, nameAt, scale, "room_tag", "name", room, stamps, ref box, ref index, ref count, false, out _);
                if (nameId == Guid.Empty && doc.Objects.Delete(areaId, true))
                {
                    areaId = Guid.Empty;
                    index--;
                    count--;
                }
            }
            if (nameId == Guid.Empty)
                nameId = AddPlanText(doc, layer, name, origin, scale, "room_tag", "name", room, stamps, ref box, ref index, ref count, false, out _);
            if (nameId == Guid.Empty)
            {
                loose.Add(planRoom);
                continue;
            }
            added += TagDown(doc, planRoom, nameId, areaId, line, tagBoxes, texts, ref stats);
        }
        foreach (var planRoom in loose)
            added += BakeLeaderTag(doc, layer, scale, worldToHld, delta, planRoom, poche, pattern, tol,
                tagBoxes, leaders, texts, ref box, ref index, ref count, ref stats);
        if (texts.Count > 0)
            stats.RoomText = string.Join(" | ", texts.ToArray());
        return added;
    }

    private static Dictionary<string, string> RoomStamps(string roomId)
    {
        var stamps = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(roomId)) stamps[RoomIdKey] = roomId;
        return stamps;
    }

    /// <summary>Counts a placed tag and keeps its boxes for the marks and dimensions.</summary>
    private static int TagDown(
        RhinoDoc doc, PlanRoom planRoom, Guid nameId, Guid areaId, string line,
        List<RoomDetect.Box> tagBoxes, List<string> texts, ref PlanStats stats)
    {
        stats.Rooms++;
        foreach (var tagId in new[] { nameId, areaId })
        {
            var tagBox = tagId == Guid.Empty
                ? BoundingBox.Empty
                : doc.Objects.FindId(tagId)?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (tagBox.IsValid)
                tagBoxes.Add(new RoomDetect.Box(tagBox.Min.X, tagBox.Min.Y, tagBox.Max.X, tagBox.Max.Y));
        }
        if (areaId == Guid.Empty)
        {
            stats.RoomAreasDropped++;
            return 1;
        }
        texts.Add(line);
        return 2;
    }

    /// <summary>
    /// A room whose name does not fit it: the whole tag (name over ca. X m²)
    /// outside, at the nearest spot PlanDims.PlaceLeader finds clear of what
    /// is drawn, with a leader from a dot at the room's tag point. With no
    /// clear spot the name is still placed, centred, and runs past the room
    /// edge (overflow).
    /// </summary>
    private int BakeLeaderTag(
        RhinoDoc doc, Layer layer, int scale, Transform worldToHld, Vector3d delta, PlanRoom planRoom,
        List<List<List<RoomDetect.Pt>>> poche, int pattern, double tol,
        List<RoomDetect.Box> tagBoxes, List<RoomDetect.Box> leaders, List<string> texts,
        ref BoundingBox box, ref int index, ref int count, ref PlanStats stats)
    {
        var height = OpeningTypes.PlanAnnotationHeight(scale);
        var name = planRoom.Name;
        var line = OpeningTypes.RoomTag(planRoom.Area);
        var ring = planRoom.Ring.Select(p => ToDrawing(p, worldToHld, delta)).ToList();
        var room = RoomStamp(ring);
        var inside = ToDrawing(new Point3d(planRoom.Inside.X, planRoom.Inside.Y, 0), worldToHld, delta);
        var span = new BoundingBox(ring);
        var sizes = (TextWidthOf(doc, name, height) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " m wide, room "
            + ((span.Max.X - span.Min.X) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " x "
            + ((span.Max.Y - span.Min.Y) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " m";
        var who = planRoom.Who + " " + name + ": name " + sizes;
        var stamps = RoomStamps(planRoom.RoomId);

        // The block as Rhino sets it: the name 1.15 heights over the area line.
        var block = TagBlock(doc, name, line, height);
        if (block.IsValid && PlanDims.PlaceLeader(
                new RoomDetect.Pt(inside.X, inside.Y), ring.Select(p => new RoomDetect.Pt(p.X, p.Y)).ToList(),
                (block.Max.X - block.Min.X) / 2.0, (block.Max.Y - block.Min.Y) / 2.0, scale,
                PlanObstacles(doc, layer), poche, out var centre, out var lead))
        {
            var origin = new Point3d(centre.X - block.Center.X, centre.Y - block.Center.Y, 0);
            var onLeader = new Dictionary<string, string>(stamps) { ["forsk:leader"] = "1" };
            var areaId = AddPlanText(doc, layer, line, origin, scale, "room_tag", "area", room, onLeader, ref box, ref index, ref count, true, out _);
            var nameId = AddPlanText(doc, layer, name, origin + new Vector3d(0, height * 1.15, 0), scale, "room_tag", "name", room, onLeader,
                ref box, ref index, ref count, true, out _);
            if (nameId != Guid.Empty)
            {
                var ends = new Dictionary<string, string>(stamps)
                {
                    ["forsk:line"] = FaceStamp(DrawingPoint(lead.A), DrawingPoint(lead.B))
                };
                var added = TagDown(doc, planRoom, nameId, areaId, line, tagBoxes, texts, ref stats);
                using (var curve = new LineCurve(DrawingPoint(lead.A), DrawingPoint(lead.B)))
                    added += AddStroke(doc, layer, curve, PenThin, scale, false, pattern, tol,
                        "room_leader", "line", null, null, ref box, ref index, ref count, new SymbolStamp { Extra = ends });
                added += AddLeaderDot(doc, layer, DrawingPoint(lead.A), scale, pattern, tol, ends, ref box, ref index, ref count);
                leaders.Add(PlanDims.SegBox(lead));
                stats.RoomsLeader++;
                stats.RoomsLeading.Add(who);
                return added;
            }
            if (areaId != Guid.Empty && doc.Objects.Delete(areaId, true))
            {
                index--;
                count--;
            }
        }

        var overflow = new Dictionary<string, string>(stamps) { ["forsk:overflow"] = "1" };
        var id = AddPlanText(doc, layer, name, inside, scale, "room_tag", "name", room, overflow, ref box, ref index, ref count, true, out _);
        if (id == Guid.Empty)
        {
            // Rhino made no text at all. It is in no count, so the sheet's counts fail.
            stats.RoomsUntagged.Add(planRoom.Who + " " + name + ": text not placed");
            return 0;
        }
        stats.RoomsOverflow++;
        stats.RoomsOverflowing.Add(who);
        return TagDown(doc, planRoom, id, Guid.Empty, line, tagBoxes, texts, ref stats);
    }

    /// <summary>The name over the area line, as Rhino sets them, about the area line's origin.</summary>
    private static BoundingBox TagBlock(RhinoDoc doc, string name, string line, double height)
    {
        var block = BoundingBox.Empty;
        foreach (var (text, y) in new[] { (line, 0.0), (name, height * 1.15) })
        {
            var plane = Plane.WorldXY;
            plane.Origin = new Point3d(0, y, 0);
            using (var entity = PlanAnnotation(doc, text, plane, height))
            {
                var part = entity?.GetBoundingBox(true) ?? BoundingBox.Empty;
                if (!part.IsValid) return BoundingBox.Empty;
                block.Union(part);
            }
        }
        return block;
    }

    private static double TextWidthOf(RhinoDoc doc, string text, double height)
    {
        using (var entity = PlanAnnotation(doc, text, Plane.WorldXY, height))
        {
            var bbox = entity?.GetBoundingBox(true) ?? BoundingBox.Empty;
            return bbox.IsValid ? bbox.Max.X - bbox.Min.X : 0;
        }
    }

    /// <summary>The filled dot a leader starts from.</summary>
    private static int AddLeaderDot(
        RhinoDoc doc, Layer layer, Point3d at, int scale, int pattern, double tol, IDictionary<string, string> stamps,
        ref BoundingBox box, ref int index, ref int count)
    {
        Hatch[] hatches;
        using (var circle = new ArcCurve(new Circle(at, LeaderDotMm * scale / 2.0)))
        {
            try { hatches = Hatch.Create(circle, pattern, 0.0, 1.0, Math.Max(tol, 0.01)); }
            catch (Exception) { hatches = null; }
        }
        if (hatches == null) return 0;
        var added = 0;
        foreach (var hatch in hatches)
        {
            if (hatch == null) continue;
            var attr = DrawAttr(layer, FormatStableId("d", index), "room_leader", "dot", null);
            foreach (var pair in stamps)
                attr.SetUserString(pair.Key, pair.Value);
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

    private static bool TryRoomPolygon(RhinoObject room, out List<Point3d> polygon)
    {
        polygon = new List<Point3d>();
        var curve = RoomMarkerOutline(room);
        if (curve == null) return false;
        try
        {
            Polyline poly;
            if (curve.TryGetPolyline(out poly) && poly != null && poly.Count >= 3)
            {
                foreach (var point in poly)
                    polygon.Add(new Point3d(point.X, point.Y, 0));
            }
            else
            {
                var count = Math.Max(curve.SpanCount * 2, 8);
                if (count > 48) count = 48;
                var ts = curve.DivideByCount(count, true);
                if (ts == null) return false;
                foreach (var t in ts)
                    polygon.Add(new Point3d(curve.PointAt(t).X, curve.PointAt(t).Y, 0));
            }
        }
        finally
        {
            curve.Dispose();
        }
        return polygon.Count >= 3;
    }

    private static Point3d ToDrawing(Point3d point, Transform worldToHld, Vector3d delta)
    {
        point.Z = 0;
        if (worldToHld.IsValid && !worldToHld.IsIdentity)
            point.Transform(worldToHld);
        point += delta;
        point.Z = 0;
        return point;
    }

    private static string RoomStamp(List<Point3d> ring)
    {
        var parts = new List<string>();
        var limit = ring.Count > 32 ? 32 : ring.Count;
        for (var i = 0; i < limit; i++)
        {
            parts.Add(ring[i].X.ToString("0.###", CultureInfo.InvariantCulture)
                + "," + ring[i].Y.ToString("0.###", CultureInfo.InvariantCulture));
        }
        return string.Join(";", parts.ToArray());
    }

    /// <summary>
    /// Plan text inside <paramref name="room"/>, or anywhere when
    /// <paramref name="overflow"/> (running past its edge, or outside on a
    /// leader). Returns its id, or Guid.Empty when it was not placed;
    /// <paramref name="width"/> is the width Rhino measured for it (0 when it
    /// never got that far).
    /// </summary>
    private static Guid AddPlanText(
        RhinoDoc doc,
        Layer layer,
        string text,
        Point3d origin,
        int scale,
        string role,
        string part,
        string room,
        IDictionary<string, string> stamps,
        ref BoundingBox box,
        ref int index,
        ref int count,
        bool overflow,
        out double width)
    {
        width = 0;
        var height = OpeningTypes.PlanAnnotationHeight(scale);
        if (string.IsNullOrWhiteSpace(text) || height <= 0) return Guid.Empty;
        var plane = Plane.WorldXY;
        plane.Origin = origin;
        var stableId = FormatStableId("d", index);
        var attr = DrawAttr(layer, stableId, role, null, null);
        attr.SetUserString("forsk:tag", part);
        if (stamps != null)
            foreach (var pair in stamps)
                attr.SetUserString(pair.Key, pair.Value);
        if (!string.IsNullOrEmpty(room))
            attr.SetUserString("forsk:room", room);
        TextEntity entity = null;
        Guid id = Guid.Empty;
        try
        {
            entity = PlanAnnotation(doc, text, plane, height);
            if (entity == null) return Guid.Empty;
            id = doc.Objects.AddText(entity, attr);
        }
        catch (Exception)
        {
            entity?.Dispose();
            return Guid.Empty;
        }
        if (id == Guid.Empty)
        {
            entity?.Dispose();
            return Guid.Empty;
        }
        var written = doc.Objects.FindId(id);
        var textBox = written?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
        entity?.Dispose();
        // Stamp what Rhino stored and what the detail will print, not the request.
        if (written?.Geometry is TextEntity stored)
        {
            var dimScale = stored.DimensionScale > 0 ? stored.DimensionScale : 1.0;
            var model = stored.TextHeight * dimScale;
            var paper = OpeningTypes.PaperTextHeight(model, scale, doc.LayoutSpaceAnnotationScalingEnabled);
            written.Attributes.SetUserString("forsk:text_height", model.ToString("0.###", CultureInfo.InvariantCulture));
            written.Attributes.SetUserString("forsk:paper_height", paper.ToString("0.###", CultureInfo.InvariantCulture));
            written.CommitChanges();
        }
        if (textBox.IsValid)
        {
            width = textBox.Max.X - textBox.Min.X;
            var shortSide = Math.Min(width, textBox.Max.Y - textBox.Min.Y);
            if (shortSide > height * 2.0 || (!overflow && !RoomHolds(textBox, room)))
            {
                try { doc.Objects.Delete(id, true); } catch (Exception) { }
                return Guid.Empty;
            }
            box.Union(textBox);
        }
        index++;
        count++;
        return id;
    }

    /// <summary>
    /// Model height is the paper cap height times the plan scale, and the
    /// detail's 1:scale shrinks it to 2.5 mm. Layout-space annotation scaling
    /// would draw the text at its model height on paper (250 mm at 1:100),
    /// so plan text turns it off. Dimension scale stays 1 in model space.
    /// </summary>
    private static TextEntity PlanAnnotation(RhinoDoc doc, string text, Plane plane, double height)
    {
        if (doc.LayoutSpaceAnnotationScalingEnabled)
            doc.LayoutSpaceAnnotationScalingEnabled = false;
        var found = OneToOneTextStyle(doc, "Forsk plan " + height.ToString("0", CultureInfo.InvariantCulture), height);
        if (found == null) return null;
        var entity = TextEntity.Create(text, plane, found, false, 0, 0);
        if (entity == null) return null;
        entity.TextHorizontalAlignment = TextHorizontalAlignment.Center;
        entity.TextVerticalAlignment = TextVerticalAlignment.Middle;
        return entity;
    }

    /// <summary>
    /// Text style of <paramref name="height"/> with dimension scale 1. With
    /// layout-space scaling off, text on the document's own style takes its
    /// model dimension scale (x100 in the mm templates), on a page too. The
    /// style is written only when it differs: every plan text and schedules
    /// cell asks for it, and a write regenerates every text on the style.
    /// </summary>
    private static DimensionStyle OneToOneTextStyle(RhinoDoc doc, string name, double height)
    {
        var found = doc.DimStyles.FindName(name);
        if (found == null)
        {
            var style = doc.DimStyles.Current != null
                ? doc.DimStyles.Current.Duplicate()
                : new DimensionStyle();
            style.Name = name;
            style.TextHeight = height;
            style.DimensionScaleValue = ScaleValue.OneToOne();
            var index = doc.DimStyles.Add(style, false);
            if (index < 0) return null;
            return doc.DimStyles.FindName(name);
        }
        if (Math.Abs(found.TextHeight - height) < 1e-9 && Math.Abs(found.DimensionScale - 1.0) < 1e-9)
            return found;
        found.TextHeight = height;
        found.DimensionScaleValue = ScaleValue.OneToOne();
        doc.DimStyles.Modify(found, found.Index, false);
        return found;
    }

    private static bool RoomHolds(BoundingBox box, string room)
    {
        if (string.IsNullOrEmpty(room)) return false;
        var ring = new List<RoomDetect.Pt>();
        foreach (var pair in room.Split(';'))
        {
            var xy = pair.Split(',');
            if (xy.Length != 2) continue;
            double x, y;
            if (!double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
            if (!double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
            ring.Add(new RoomDetect.Pt(x, y));
        }
        if (ring.Count < 3) return false;
        var corners = new[]
        {
            new RoomDetect.Pt(box.Min.X, box.Min.Y),
            new RoomDetect.Pt(box.Max.X, box.Min.Y),
            new RoomDetect.Pt(box.Max.X, box.Max.Y),
            new RoomDetect.Pt(box.Min.X, box.Max.Y)
        };
        // A corner on the room edge still counts as inside.
        foreach (var corner in corners)
            if (!RoomDetect.Contains(ring, corner) && RoomDetect.Clearance(ring, corner) > 1e-4) return false;
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
        PrintPen pen,
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
        ref int count,
        SymbolStamp faces = null)
    {
        var paperMm = pen.Mm;
        // A dashed line takes the profile's linetype colour, whatever tier it is drawn at.
        var ink = dashed ? PrintProfiles.Active.Dashed : pen.Color;
        if (curve == null || paperMm <= 0 || scale < 1) return 0;
        // A swing arc ribbon fills the sector. Draw the arc as a thin curve.
        if (part == "arc")
        {
            var stableId = FormatStableId("d", index);
            var attr = DrawAttr(layer, stableId, role, part, markerId, openY, dashed, ink);
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
            added += AddRibbon(doc, layer, curve, width, paperMm, ink, pattern, tol, role, part, markerId, openY, dashed, ref box, ref index, ref count, faces);
            if (added == 0)
                added += AddPlainCurve(doc, layer, curve, paperMm, ink, role, part, markerId, openY, dashed, ref box, ref index, ref count, faces);
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
                        var n = AddRibbon(doc, layer, piece, width, paperMm, ink, pattern, tol, role, part, markerId, openY, true, ref box, ref index, ref count, faces);
                        if (n == 0)
                            n = AddPlainCurve(doc, layer, piece, paperMm, ink, role, part, markerId, openY, true, ref box, ref index, ref count, faces);
                        added += n;
                        piece.Dispose();
                    }
                }
            }
            on = !on;
            cursor = next;
        }
        return added;
    }

    /// <param name="penMm">The pen in paper mm. Every hatch of the ribbon carries it, and the first one kept
    /// carries the stroke itself, so a DWG export draws the centreline once at that weight.</param>
    private static int AddRibbon(
        RhinoDoc doc,
        Layer layer,
        Curve curve,
        double width,
        double penMm,
        Color ink,
        int pattern,
        double tol,
        string role,
        string part,
        string markerId,
        double? openY,
        bool dashed,
        ref BoundingBox box,
        ref int index,
        ref int count,
        SymbolStamp faces = null)
    {
        var half = width * 0.5;
        if (half <= 0 || curve == null) return 0;
        // A closed loop offset as one open ribbon self-intersects and
        // Hatch.Create fills a circle the stroke bbox does not contain.
        // Offset both sides. An open stroke stays a strip. A strip whose
        // boundary arc is far larger than the stroke is that circle.
        var boundaries = curve.IsClosed
            ? ClosedBand(curve, half, tol)
            : OpenRibbon(curve, half, tol);
        if (boundaries == null || boundaries.Count == 0) return 0;
        Hatch[] hatches = null;
        try
        {
            hatches = Hatch.Create(boundaries, pattern, 0.0, 1.0, Math.Max(tol, 0.01));
        }
        catch (Exception)
        {
            hatches = null;
        }
        foreach (var boundary in boundaries)
            boundary?.Dispose();
        if (hatches == null) return 0;
        var limit = curve.GetBoundingBox(true);
        var pad = Math.Max(half * 3.0, 1.0);
        if (limit.IsValid) limit.Inflate(pad, pad, Math.Max(pad, 1.0));
        var added = 0;
        var stroke = StrokeText(curve, tol);
        var stamped = false;
        foreach (var hatch in hatches)
        {
            if (hatch == null) continue;
            var stableId = FormatStableId("d", index);
            var attr = DrawAttr(layer, stableId, role, part, markerId, openY, dashed, ink);
            StampSymbolLine(attr, curve, part, faces);
            attr.SetUserString(SheetFlat.PenKey, penMm.ToString("0.###", CultureInfo.InvariantCulture));
            if (!stamped && stroke.Length > 0)
                attr.SetUserString(SheetFlat.StrokeKey, stroke);
            Guid id;
            try { id = doc.Objects.AddHatch(hatch, attr); }
            catch (Exception) { id = Guid.Empty; }
            var hatchBox = hatch.GetBoundingBox(true);
            var sourceBox = curve.GetBoundingBox(true);
            if (id != Guid.Empty && !curve.IsClosed && HatchHasWildArc(hatch, sourceBox))
            {
                try { doc.Objects.Delete(id, true); } catch (Exception) { }
                id = Guid.Empty;
            }
            if (id != Guid.Empty && limit.IsValid && hatchBox.IsValid && !BoxHolds(limit, hatchBox))
            {
                try { doc.Objects.Delete(id, true); } catch (Exception) { }
                id = Guid.Empty;
            }
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
            stamped = true;
            index++;
            count++;
            if (hatchBox.IsValid) box.Union(hatchBox);
            added++;
        }
        return added;
    }

    /// <summary>
    /// A stroke's centreline for SheetFlat: lines, arcs, and anything else as
    /// a polyline within tol. Empty when the curve gives nothing.
    /// </summary>
    private static string StrokeText(Curve curve, double tol)
    {
        var segs = new List<SheetFlat.Seg>();
        if (curve == null) return "";
        var pieces = curve.DuplicateSegments();
        if (pieces == null || pieces.Length == 0) pieces = new[] { curve.DuplicateCurve() };
        foreach (var piece in pieces)
        {
            if (piece == null) continue;
            if (piece.TryGetPolyline(out var polyline))
            {
                for (var i = 1; i < polyline.Count; i++)
                    segs.Add(SheetFlat.Seg.Line(polyline[i - 1].X, polyline[i - 1].Y, polyline[i].X, polyline[i].Y));
            }
            else if (piece.TryGetArc(out var arc, Math.Max(tol, 0.01)))
            {
                var mid = arc.MidPoint;
                segs.Add(SheetFlat.Seg.ArcOf(arc.StartPoint.X, arc.StartPoint.Y, mid.X, mid.Y, arc.EndPoint.X, arc.EndPoint.Y));
            }
            else
            {
                var approx = piece.ToPolyline(0, 0, 0.1, 0, 0, Math.Max(tol, 0.01), 0, 0, true);
                if (approx != null && approx.TryGetPolyline(out var points))
                    for (var i = 1; i < points.Count; i++)
                        segs.Add(SheetFlat.Seg.Line(points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y));
                approx?.Dispose();
            }
            piece.Dispose();
        }
        return SheetFlat.Encode(segs);
    }

    private static bool BoxHolds(BoundingBox limit, BoundingBox inner)
    {
        return limit.Min.X <= inner.Min.X + 0.5
            && limit.Min.Y <= inner.Min.Y + 0.5
            && limit.Max.X >= inner.Max.X - 0.5
            && limit.Max.Y >= inner.Max.Y - 0.5;
    }

    /// <summary>
    /// Band between the outward and inward offsets. One closed curve in,
    /// outer loop then the hole.
    /// </summary>
    private static List<Curve> ClosedBand(Curve curve, double half, double tol)
    {
        if (curve == null || !curve.IsClosed || half <= 0) return null;
        Curve[] grown = null;
        Curve[] shrunk = null;
        var gap = Math.Max(tol, 0.01);
        try
        {
            grown = curve.Offset(Plane.WorldXY, half, gap, CurveOffsetCornerStyle.Sharp);
            shrunk = curve.Offset(Plane.WorldXY, -half, gap, CurveOffsetCornerStyle.Sharp);
        }
        catch (Exception)
        {
            grown = null;
            shrunk = null;
        }
        if (grown == null || shrunk == null || grown.Length != 1 || shrunk.Length != 1
            || grown[0] == null || shrunk[0] == null
            || !grown[0].IsClosed || !shrunk[0].IsClosed)
        {
            DisposeCurves(grown);
            DisposeCurves(shrunk);
            return null;
        }
        var big = AreaMassProperties.Compute(grown[0]);
        var small = AreaMassProperties.Compute(shrunk[0]);
        if (big == null || small == null || big.Area < 1.0 || small.Area < 1.0)
        {
            grown[0].Dispose();
            shrunk[0].Dispose();
            return null;
        }
        var outer = big.Area >= small.Area ? grown[0] : shrunk[0];
        var hole = ReferenceEquals(outer, grown[0]) ? shrunk[0] : grown[0];
        var centre = AreaMassProperties.Compute(hole);
        if (centre == null
            || outer.Contains(centre.Centroid, Plane.WorldXY, gap) != PointContainment.Inside)
        {
            outer.Dispose();
            hole.Dispose();
            return null;
        }
        return new List<Curve> { outer, hole };
    }

    private static List<Curve> OpenRibbon(Curve curve, double half, double tol)
    {
        var samples = RibbonSamples(curve, tol);
        if (samples.Count < 2) return null;
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
        if (left.Count < 2) return null;
        var pts = new List<Point3d>();
        pts.AddRange(left);
        for (var i = right.Count - 1; i >= 0; i--)
            pts.Add(right[i]);
        pts.Add(pts[0]);
        return new List<Curve> { new PolylineCurve(pts) };
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

    private static int AddPlainCurve(
        RhinoDoc doc,
        Layer layer,
        Curve curve,
        double plotMm,
        Color ink,
        string role,
        string part,
        string markerId,
        double? openY,
        bool dashed,
        ref BoundingBox box,
        ref int index,
        ref int count,
        SymbolStamp faces = null)
    {
        var stableId = FormatStableId("d", index);
        var attr = DrawAttr(layer, stableId, role, part, markerId, openY, dashed, ink);
        StampSymbolLine(attr, curve, part, faces);
        if (plotMm > 0) attr.PlotWeight = plotMm;
        Guid id;
        try { id = doc.Objects.AddCurve(curve, attr); }
        catch (Exception) { id = Guid.Empty; }
        if (id == Guid.Empty) return 0;
        index++;
        count++;
        var curveBox = curve.GetBoundingBox(true);
        if (curveBox.IsValid) box.Union(curveBox);
        return 1;
    }

    private static void DisposeCurves(Curve[] curves)
    {
        if (curves == null) return;
        foreach (var curve in curves)
            curve?.Dispose();
    }

    private static bool HatchHasWildArc(Hatch hatch, BoundingBox source)
    {
        if (hatch == null) return false;
        var span = 1.0;
        if (source.IsValid)
        {
            var dx = source.Max.X - source.Min.X;
            var dy = source.Max.Y - source.Min.Y;
            var edge = Math.Max(dx, dy);
            if (edge > span) span = edge;
        }
        var cap = span * 1.25;
        var wild = false;
        foreach (var outer in new[] { true, false })
        {
            Curve[] curves = null;
            try { curves = hatch.Get3dCurves(outer); }
            catch (Exception) { curves = null; }
            if (curves == null) continue;
            foreach (var curve in curves)
            {
                if (curve is ArcCurve arc && arc.Arc.IsValid && arc.Arc.Radius > cap)
                    wild = true;
                curve?.Dispose();
            }
        }
        return wild;
    }

    private static ObjectAttributes DrawAttr(
        Layer layer, string stableId, string role, string part, string markerId, double? openY = null, bool dashed = false,
        Color? ink = null)
    {
        // Text, dots and arrows take the profile's annotation ink; strokes pass their pen's.
        var color = ink ?? PrintProfiles.Active.Text;
        var attr = new ObjectAttributes
        {
            LayerIndex = layer.Index,
            Name = stableId,
            Space = ActiveSpace.ModelSpace,
            ViewportId = Guid.Empty,
            ColorSource = ObjectColorSource.ColorFromObject,
            ObjectColor = color,
            PlotColorSource = ObjectPlotColorSource.PlotColorFromObject,
            PlotColor = color,
            PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject,
            PlotWeight = PrintProfiles.Active.Thin.Mm,
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
