using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
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
        // FU.2: a tag keeps off the furniture symbols, and off the door swings
        // drawn already when its room has the space. Both are read in world space.
        var pieces = FurnitureFootprints(doc, null).Select(f => f.Corners).ToList();
        var toWorld = worldToHld.IsValid && !worldToHld.IsIdentity ? InverseOf(worldToHld) : Transform.Identity;
        var swings = PlanObstacles(doc, layer, IsDoorSwing)
            .Select(o => Furniture.Corners(o.Box).Select(p =>
            {
                var world = new Point3d(p.X - delta.X, p.Y - delta.Y, 0);
                world.Transform(toWorld);
                return new RoomDetect.Pt(world.X, world.Y);
            }).ToArray())
            .ToList();
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
            if ((pieces.Count > 0 || swings.Count > 0) && planRoom.Outline != null)
            {
                // The tag box: the name 1.15 heights over the area line, both centred.
                var line0 = OpeningTypes.RoomTag(planRoom.Area);
                var hw = Math.Max(TextWidthOf(doc, name, height), TextWidthOf(doc, line0, height)) / 2 + 0.25 * height;
                var lift = 0.575 * height;
                var spot = Furniture.RoomTagSpot(new RoomDetect.Pt(inside.X, inside.Y + lift), planRoom.Outline, hw, 1.25 * height, pieces, swings, height);
                if (spot.HasValue) inside = new RoomDetect.Pt(spot.Value.X, spot.Value.Y - lift);
                else stats.TagsOnFurniture++;
            }
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

    /// <summary>A door's leaf or swing arc as the plan draws it.</summary>
    private static bool IsDoorSwing(ObjectAttributes attr)
    {
        if (attr.GetUserString("forsk:role") != "symbol") return false;
        var part = attr.GetUserString("forsk:symbol");
        return part == "arc" || part == "leaf";
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
        ref BoundingBox box, ref int index, ref int count, string role = "room_leader")
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
            var attr = DrawAttr(layer, FormatStableId("d", index), role, "dot", null);
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

    /// <summary>A room tag's room as RoomStamp wrote it: its outline in drawing space, empty when unreadable.</summary>
    private static List<RoomDetect.Pt> RoomRing(string room)
    {
        var ring = new List<RoomDetect.Pt>();
        foreach (var pair in (room ?? "").Split(';'))
        {
            var xy = pair.Split(',');
            if (xy.Length != 2) continue;
            double x, y;
            if (!double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
            if (!double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
            ring.Add(new RoomDetect.Pt(x, y));
        }
        return ring;
    }

    private static bool RoomHolds(BoundingBox box, string room)
    {
        var ring = RoomRing(room);
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
        var ink = ForskTechnical.Ink(pen, dashed, PrintProfiles.Active);
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
