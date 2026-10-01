using System;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// After a wall edit succeeds, the floor slab and flat roof that were built
/// from that record's outer outline are rebuilt the same way (downward
/// extrude, the roof's thickness and overhang kept), and rooms are detected
/// again. A daylight map that was shown is hidden. A failure here rolls the
/// slabs back; the caller rolls the wall back.
/// </summary>
public partial class RhinoMCPFunctions
{
    private sealed class SlabUndo
    {
        public RhinoObject Object;
        public Brep Brep;
        public ObjectAttributes Attr;
        public bool Deleted;
    }

    /// <summary>
    /// <paramref name="before"/> is the record's rings before the edit.
    /// <paramref name="after"/> is the rings now, or null when the record was deleted.
    /// </summary>
    private string FollowNeighbours(RhinoDoc doc, string sourceLayer, List<List<RoomDetect.Pt>> before, List<List<RoomDetect.Pt>> after)
    {
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var outlineTol = Math.Max(tol, 5.0);
        var outer = before != null && before.Count > 0 ? before[0] : null;
        var floor = outer == null ? null : ChooseSlab(doc, sourceLayer, outer, roof: false, outlineTol);
        var roof = outer == null ? null : ChooseSlab(doc, sourceLayer, outer, roof: true, outlineTol);
        var daylight = DaylightShown(doc);
        var snaps = new List<SlabUndo>();
        try
        {
            var floorDone = false;
            var roofDone = false;
            if (after == null)
            {
                floorDone = DeleteSlab(doc, floor, snaps);
                roofDone = DeleteSlab(doc, roof, snaps);
            }
            else if (after.Count > 0 && after[0] != null && after[0].Count >= 3)
            {
                floorDone = RebuildFloor(doc, floor, after[0], tol, snaps);
                roofDone = RebuildRoof(doc, roof, after[0], tol, snaps);
            }
            var rooms = RoomsDetect(new JObject());
            var count = rooms?["count"]?.ToObject<int>() ?? 0;
            if (daylight)
                SetAnalysisVisible(doc, false);
            return WallFollowPlan.Sentence(floorDone, roofDone, count, daylight);
        }
        catch (Exception ex)
        {
            RestoreSlabs(doc, snaps);
            if (ex is InvalidOperationException) throw;
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private static bool DaylightShown(RhinoDoc doc)
    {
        foreach (var mesh in AnalysisOverlays(doc))
            if (mesh != null && mesh.Visible) return true;
        return false;
    }

    private RhinoObject ChooseSlab(RhinoDoc doc, string sourceLayer, List<RoomDetect.Pt> outer, bool roof, double tol)
    {
        RhinoObject best = null;
        var bestArea = double.MaxValue;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj == null || IsExistingUnderlay(doc, obj)) continue;
            var kind = GetForskKind(obj);
            var isRoof = string.Equals(kind, "roof", StringComparison.OrdinalIgnoreCase);
            var isFloor = string.Equals(kind, "floor", StringComparison.OrdinalIgnoreCase);
            if (roof ? !isRoof : !isFloor) continue;
            if (isRoof)
            {
                var type = obj.Attributes?.GetUserString("forsk:roof_type");
                if (!string.IsNullOrEmpty(type) && !string.Equals(type, "flat", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (!SourceAgrees(sourceLayer, obj)) continue;
            var ring = PlanRingOf(GetBrepFromObject(obj));
            if (ring == null) continue;
            var overhang = isRoof ? ParseMm(obj.Attributes?.GetUserString("forsk:overhang")) ?? 0 : 0;
            var owns = overhang > tol
                ? WallFollowPlan.Covers(outer, ring, overhang, tol)
                : WallFollowPlan.SameOutline(outer, ring, tol);
            if (!owns) continue;
            var area = Math.Abs(RoomDetect.Area(ring));
            if (area < bestArea)
            {
                bestArea = area;
                best = obj;
            }
        }
        return best;
    }

    private static bool SourceAgrees(string wallLayer, RhinoObject slab)
    {
        if (string.IsNullOrWhiteSpace(wallLayer)) return true;
        var slabLayer = slab.Attributes?.GetUserString("forsk:source_layer");
        if (string.IsNullOrWhiteSpace(slabLayer)) return true;
        return string.Equals(wallLayer.Trim(), slabLayer.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private bool RebuildFloor(RhinoDoc doc, RhinoObject slab, List<RoomDetect.Pt> outer, double tol, List<SlabUndo> snaps)
    {
        if (slab == null) return false;
        var brep = GetBrepFromObject(slab);
        var box = brep?.GetBoundingBox(true) ?? BoundingBox.Unset;
        var thickness = box.IsValid ? box.Max.Z - box.Min.Z : 0;
        if (thickness <= 1) thickness = ForskDefaults.FloorThickness;
        var top = box.IsValid ? box.Max.Z : 0;
        var fresh = ExtrudeClosedCurve(RingCurve(outer, top), -thickness, tol);
        if (fresh == null || !fresh.IsValid)
            throw new InvalidOperationException("The floor slab did not rebuild.");
        ReplaceSlab(doc, slab, fresh, snaps);
        return true;
    }

    private bool RebuildRoof(RhinoDoc doc, RhinoObject slab, List<RoomDetect.Pt> outer, double tol, List<SlabUndo> snaps)
    {
        if (slab == null) return false;
        var brep = GetBrepFromObject(slab);
        var box = brep?.GetBoundingBox(true) ?? BoundingBox.Unset;
        var stored = ParseMm(slab.Attributes?.GetUserString("forsk:thickness"));
        var thickness = stored ?? (box.IsValid ? box.Max.Z - box.Min.Z : 0);
        if (thickness <= 1) thickness = ForskDefaults.RoofThickness;
        var top = box.IsValid ? box.Max.Z : 0;
        var overhang = ParseMm(slab.Attributes?.GetUserString("forsk:overhang")) ?? 0;
        var footprint = ApplyConstantOverhang(RingCurve(outer, 0), overhang, tol, new JArray());
        MoveCurveToZ(footprint, top);
        var fresh = ExtrudeClosedCurve(footprint, -thickness, tol);
        if (fresh == null || !fresh.IsValid)
            throw new InvalidOperationException("The flat roof did not rebuild.");
        ReplaceSlab(doc, slab, fresh, snaps);
        return true;
    }

    private void ReplaceSlab(RhinoDoc doc, RhinoObject slab, Brep fresh, List<SlabUndo> snaps)
    {
        var undo = new SlabUndo
        {
            Object = slab,
            Brep = GetBrepFromObject(slab)?.DuplicateBrep(),
            Attr = slab.Attributes?.Duplicate()
        };
        if (!doc.Objects.Replace(slab.Id, fresh))
            throw new InvalidOperationException("The slab did not replace.");
        snaps.Add(undo);
    }

    private static bool DeleteSlab(RhinoDoc doc, RhinoObject slab, List<SlabUndo> snaps)
    {
        if (slab == null) return false;
        var undo = new SlabUndo { Object = slab, Deleted = true };
        if (!doc.Objects.Delete(slab.Id, true))
            throw new InvalidOperationException("The slab did not delete.");
        snaps.Add(undo);
        return true;
    }

    private static void RestoreSlabs(RhinoDoc doc, List<SlabUndo> snaps)
    {
        if (doc == null || snaps == null) return;
        for (var i = snaps.Count - 1; i >= 0; i--)
        {
            var snap = snaps[i];
            if (snap?.Object == null) continue;
            try
            {
                if (snap.Deleted)
                {
                    if (doc.Objects.FindId(snap.Object.Id) == null)
                        doc.Objects.Undelete(snap.Object);
                }
                else if (snap.Brep != null && doc.Objects.FindId(snap.Object.Id) != null)
                {
                    doc.Objects.Replace(snap.Object.Id, snap.Brep.DuplicateBrep());
                    if (snap.Attr != null)
                        doc.Objects.ModifyAttributes(snap.Object.Id, snap.Attr.Duplicate(), true);
                }
            }
            catch (Exception)
            {
                // The wall rollback still runs. A slab that will not go back is named by the error above.
            }
        }
    }

    private static Curve RingCurve(List<RoomDetect.Pt> ring, double z)
    {
        var points = new List<Point3d>(ring.Count + 1);
        foreach (var p in ring) points.Add(new Point3d(p.X, p.Y, z));
        if (points.Count > 0 && points[0].DistanceTo(points[points.Count - 1]) > 0.1)
            points.Add(points[0]);
        return new PolylineCurve(points);
    }

    private static List<RoomDetect.Pt> PlanRingOf(Brep brep)
    {
        if (brep == null) return null;
        Curve best = null;
        var bestArea = -1.0;
        foreach (var face in brep.Faces)
        {
            Vector3d normal;
            try { normal = face.NormalAt(face.Domain(0).Mid, face.Domain(1).Mid); }
            catch (Exception) { continue; }
            if (Math.Abs(normal.Z) < 0.9) continue;
            Curve loop;
            try { loop = face.OuterLoop?.To3dCurve(); }
            catch (Exception) { continue; }
            if (loop == null || !loop.IsClosed) continue;
            var area = Math.Abs(CurveArea(loop));
            if (area > bestArea)
            {
                bestArea = area;
                best = loop;
            }
        }
        if (best == null) return null;
        var ring = new List<RoomDetect.Pt>();
        if (best.TryGetPolyline(out Polyline poly) && poly != null && poly.Count >= 4)
        {
            var last = poly.Count - 1;
            if (poly[0].DistanceTo(poly[last]) < 0.1) last--;
            for (var i = 0; i <= last; i++)
                ring.Add(new RoomDetect.Pt(poly[i].X, poly[i].Y));
        }
        else
        {
            var count = Math.Min(Math.Max((int)Math.Ceiling(best.GetLength() / 50.0), 4), 400);
            var ts = best.DivideByCount(count, true);
            if (ts == null) return null;
            foreach (var t in ts)
            {
                var p = best.PointAt(t);
                ring.Add(new RoomDetect.Pt(p.X, p.Y));
            }
            if (ring.Count > 1)
            {
                var a = ring[0];
                var b = ring[ring.Count - 1];
                if (Math.Abs(a.X - b.X) < 0.1 && Math.Abs(a.Y - b.Y) < 0.1)
                    ring.RemoveAt(ring.Count - 1);
            }
        }
        return ring.Count >= 3 ? ring : null;
    }
}
