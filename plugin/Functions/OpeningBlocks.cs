using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Simple door and window frames in the opening void.
/// forsk:kind=opening so clear, sheets, and print already treat them as clay
/// and skip them when hatching wall poché. Markers stay opening_marker on A-OPEN.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string OpeningBlockLayerPath = "A-OPEN::Block";
    private const string OpeningBlockDefDescription = "forsk opening";
    private const double OpeningFrameFaceMm = 50.0;
    private const double OpeningFrameInsetMm = 1.0;
    private const double OpeningLeafMm = 40.0;
    private const double OpeningGlazeMm = 8.0;
    private const double OpeningSillNoseMm = 16.0;

    /// <summary>
    /// Visible child of hidden A-OPEN. Parent off does not hide a child in Rhino 7,
    /// so frames show in the clay while markers stay off.
    /// </summary>
    private Layer EnsureOpeningBlockLayer(RhinoDoc doc)
    {
        var parent = EnsureLayer(doc, "A-OPEN", Color.FromArgb(120, 160, 200));
        var existing = FindLayerCaseInsensitive(doc, OpeningBlockLayerPath);
        if (existing != null)
        {
            if (!existing.IsVisible)
            {
                existing.IsVisible = true;
                doc.Layers.Modify(existing, existing.Index, true);
            }
            return existing;
        }

        var created = new Layer
        {
            Name = "Block",
            Color = Color.FromArgb(186, 164, 140),
            IsVisible = true,
            ParentLayerId = parent.Id
        };
        var index = doc.Layers.Add(created);
        if (index < 0) return parent;
        var layer = doc.Layers.FindIndex(index);
        if (layer != null && !layer.IsVisible)
        {
            layer.IsVisible = true;
            doc.Layers.Modify(layer, layer.Index, true);
        }
        return layer ?? parent;
    }

    private Guid AddOpeningBlock(
        RhinoDoc doc,
        Guid markerId,
        string markerName,
        Guid hostId,
        string openingKind,
        OpeningFootprint foot,
        double sill,
        double head,
        double width,
        double pad,
        string sourceLayer,
        Brep hostBrep,
        WallSegment segment)
    {
        if (doc == null || markerId == Guid.Empty || foot == null) return Guid.Empty;
        if (width <= 1 || head <= sill) return Guid.Empty;

        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        if (!ResolveOpeningAxes(
                hostBrep, foot, segment, sill, head, width, pad, tol,
                out var center, out var widthDir, out var thickDir, out var thickness))
            return Guid.Empty;

        var window = string.Equals(openingKind, "window", StringComparison.OrdinalIgnoreCase);
        var parts = BuildOpeningBlockParts(
            center, widthDir, thickDir, width, thickness, sill, head, pad, window, tol);
        if (parts.Count == 0) return Guid.Empty;

        var layer = EnsureOpeningBlockLayer(doc);
        var name = string.IsNullOrWhiteSpace(markerName) ? "opening-block" : markerName + "-block";
        var attr = new ObjectAttributes
        {
            Name = name,
            LayerIndex = layer.Index,
            ColorSource = ObjectColorSource.ColorFromLayer,
            MaterialSource = ObjectMaterialSource.MaterialFromLayer
        };
        StampForskTags(attr, new ForskStamp
        {
            Kind = "opening",
            Level = "0",
            Host = hostId.ToString(),
            HostId = ReadForskUserString(doc, hostId, "forsk:id"),
            OpeningKind = window ? "window" : "door",
            Sill = sill,
            Head = head,
            Width = width,
            SourceLayer = sourceLayer,
            MarkerId = markerId.ToString()
        });

        var boolTol = Math.Max(tol, 0.1);
        var id = CommitOpeningBlock(doc, name, attr, parts, boolTol);
        return id;
    }

    private void DeleteOpeningBlocks(RhinoDoc doc, Guid markerId)
    {
        if (doc == null || markerId == Guid.Empty) return;
        var key = markerId.ToString();
        var doomed = new List<RhinoObject>();
        foreach (var obj in doc.Objects)
        {
            if (obj == null) continue;
            if (!string.Equals(GetForskKind(obj), "opening", StringComparison.OrdinalIgnoreCase))
                continue;
            var mid = obj.Attributes?.GetUserString("forsk:marker_id");
            if (!string.Equals(mid, key, StringComparison.OrdinalIgnoreCase))
                continue;
            doomed.Add(obj);
        }

        foreach (var obj in doomed)
        {
            var defIndex = OpeningBlockDefIndex(obj);
            doc.Objects.Delete(obj.Id, true);
            if (defIndex >= 0)
                doc.InstanceDefinitions.Delete(defIndex, true, true);
        }
    }

    private static void PurgeOpeningBlockDefinitions(RhinoDoc doc)
    {
        if (doc == null) return;
        for (var i = doc.InstanceDefinitions.Count - 1; i >= 0; i--)
        {
            var idef = doc.InstanceDefinitions[i];
            if (idef == null || idef.IsDeleted) continue;
            if (!string.Equals(idef.Description, OpeningBlockDefDescription, StringComparison.Ordinal))
                continue;
            InstanceObject[] refs = null;
            try { refs = idef.GetReferences(-1); }
            catch (Exception) { refs = null; }
            if (refs != null && refs.Length > 0) continue;
            doc.InstanceDefinitions.Delete(i, true, true);
        }
    }

    private static RhinoObject ResolveOpeningHandle(RhinoObject obj)
    {
        if (obj == null) return null;
        if (string.Equals(GetForskKind(obj), "opening_marker", StringComparison.OrdinalIgnoreCase))
            return obj;
        if (!string.Equals(GetForskKind(obj), "opening", StringComparison.OrdinalIgnoreCase))
            return obj;
        var raw = obj.Attributes?.GetUserString("forsk:marker_id");
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out var markerId))
            return obj;
        var marker = RhinoDoc.ActiveDoc?.Objects.Find(markerId);
        return marker ?? obj;
    }

    private static int OpeningBlockDefIndex(RhinoObject obj)
    {
        if (!(obj is InstanceObject inst) || inst.InstanceDefinition == null)
            return -1;
        if (!string.Equals(inst.InstanceDefinition.Description, OpeningBlockDefDescription, StringComparison.Ordinal))
            return -1;
        return inst.InstanceDefinition.Index;
    }

    private static Guid CommitOpeningBlock(
        RhinoDoc doc, string name, ObjectAttributes attr, List<Brep> parts, double tol)
    {
        var solids = parts.Where(p => p != null && p.IsValid).ToList();
        if (solids.Count == 0) return Guid.Empty;

        Brep one = null;
        if (solids.Count == 1)
        {
            one = solids[0];
        }
        else
        {
            Brep[] united = null;
            try { united = Brep.CreateBooleanUnion(solids, tol); }
            catch (Exception) { united = null; }
            if (united != null)
            {
                var valid = united.Where(b => b != null && b.IsValid).ToList();
                if (valid.Count == 1)
                    one = valid[0];
                else if (valid.Count > 1)
                    return AddOpeningInstance(doc, name, attr, valid);
            }
        }

        if (one != null && one.IsValid)
        {
            var id = doc.Objects.AddBrep(one, attr);
            if (id != Guid.Empty) return id;
        }
        return AddOpeningInstance(doc, name, attr, solids);
    }

    private static Guid AddOpeningInstance(
        RhinoDoc doc, string name, ObjectAttributes attr, IList<Brep> parts)
    {
        var geom = new List<GeometryBase>();
        foreach (var part in parts)
        {
            if (part != null && part.IsValid)
                geom.Add(part);
        }
        if (geom.Count == 0) return Guid.Empty;

        var index = doc.InstanceDefinitions.Add(name, OpeningBlockDefDescription, Point3d.Origin, geom);
        if (index < 0)
        {
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
            index = doc.InstanceDefinitions.Add(
                name + "-" + suffix, OpeningBlockDefDescription, Point3d.Origin, geom);
        }
        if (index < 0) return Guid.Empty;
        return doc.Objects.AddInstanceObject(index, Transform.Identity, attr);
    }

    private static List<Brep> BuildOpeningBlockParts(
        Point3d center,
        Vector3d widthDir,
        Vector3d thickDir,
        double width,
        double thickness,
        double sill,
        double head,
        double pad,
        bool window,
        double tol)
    {
        var parts = new List<Brep>();
        if (!TryOpeningPlane(center, widthDir, thickDir, out var plane))
            return parts;

        var z0 = sill + OpeningFrameInsetMm;
        var z1 = head - OpeningFrameInsetMm;
        var outerHalf = width * 0.5 + Math.Max(pad, 0) - OpeningFrameInsetMm;
        var halfThick = thickness * 0.5 - OpeningFrameInsetMm;
        if (z1 - z0 < 80 || outerHalf < 30 || halfThick < 8)
            return parts;

        var face = Math.Min(OpeningFrameFaceMm, Math.Min(outerHalf * 0.4, (z1 - z0) * 0.22));
        if (face < 12) face = 12;
        var innerHalf = outerHalf - face;
        var minClear = window ? face * 2 + 30 : face + 30;
        if (innerHalf < 15 || z1 - z0 < minClear)
            return parts;

        var boolTol = Math.Max(tol, 0.1);
        var frame = BuildFrameSolid(plane, outerHalf, innerHalf, halfThick, face, z0, z1, window, boolTol);
        if (frame == null)
            frame = BuildFrameFromMembers(plane, outerHalf, innerHalf, halfThick, face, z0, z1, window, boolTol);
        if (frame == null) return parts;
        parts.Add(frame);

        var panel = BuildPanel(plane, innerHalf, halfThick, face, z0, z1, window);
        if (panel != null) parts.Add(panel);
        if (window)
        {
            var nose = BuildSillNose(plane, outerHalf, halfThick, face, z0);
            if (nose != null) parts.Add(nose);
        }
        return parts;
    }

    private static Brep BuildFrameSolid(
        Plane plane,
        double outerHalf,
        double innerHalf,
        double halfThick,
        double face,
        double z0,
        double z1,
        bool window,
        double tol)
    {
        var outer = FrameBox(plane, -outerHalf, outerHalf, -halfThick, halfThick, z0, z1);
        if (outer == null) return null;

        var innerZ0 = window ? z0 + face : z0 - 8;
        var innerZ1 = z1 - face;
        var inner = FrameBox(
            plane, -innerHalf, innerHalf, -(halfThick + 8), halfThick + 8, innerZ0, innerZ1);
        if (inner == null) return null;

        try
        {
            var diff = Brep.CreateBooleanDifference(outer, inner, tol);
            if (diff == null) return null;
            var valid = diff.Where(b => b != null && b.IsValid).ToList();
            if (valid.Count == 1) return valid[0];
            if (valid.Count > 1)
            {
                var joined = Brep.JoinBreps(valid, tol);
                if (joined != null && joined.Length == 1 && joined[0] != null && joined[0].IsValid)
                    return joined[0];
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// Jambs and head overlap in volume. The head is slightly shallower so the
    /// shared faces are not coplanar, which is where box unions fail.
    /// </summary>
    private static Brep BuildFrameFromMembers(
        Plane plane,
        double outerHalf,
        double innerHalf,
        double halfThick,
        double face,
        double z0,
        double z1,
        bool window,
        double tol)
    {
        var members = new List<Brep>();
        var jambY0 = -halfThick;
        var jambY1 = halfThick;
        var railY0 = -(halfThick - 0.4);
        var railY1 = halfThick - 0.4;

        var left = FrameBox(plane, -outerHalf, -innerHalf, jambY0, jambY1, z0, z1);
        var right = FrameBox(plane, innerHalf, outerHalf, jambY0, jambY1, z0, z1);
        var head = FrameBox(plane, -outerHalf, outerHalf, railY0, railY1, z1 - face, z1);
        if (left != null) members.Add(left);
        if (right != null) members.Add(right);
        if (head != null) members.Add(head);
        if (window)
        {
            var sill = FrameBox(plane, -outerHalf, outerHalf, railY0, railY1, z0, z0 + face);
            if (sill != null) members.Add(sill);
        }
        if (members.Count == 0) return null;
        if (members.Count == 1) return members[0];
        try
        {
            var united = Brep.CreateBooleanUnion(members, tol);
            if (united == null) return members[0];
            var valid = united.Where(b => b != null && b.IsValid).ToList();
            if (valid.Count == 1) return valid[0];
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Brep BuildPanel(
        Plane plane, double innerHalf, double halfThick, double face, double z0, double z1, bool window)
    {
        var thick = window ? OpeningGlazeMm : OpeningLeafMm;
        var half = Math.Min(thick * 0.5, Math.Max(3, halfThick * 0.45));
        var bite = 2.0;
        var panelZ0 = window ? z0 + face - bite : z0 + 0.5;
        var panelZ1 = z1 - face + bite;
        if (panelZ1 - panelZ0 < 10) return null;
        return FrameBox(
            plane,
            -(innerHalf + bite),
            innerHalf + bite,
            -half,
            half,
            panelZ0,
            panelZ1);
    }

    private static Brep BuildSillNose(Plane plane, double outerHalf, double halfThick, double face, double z0)
    {
        var y0 = halfThick - 2;
        var y1 = halfThick + OpeningSillNoseMm;
        var zNose0 = z0 + 1;
        var zNose1 = z0 + face - 1;
        if (zNose1 - zNose0 < 4) return null;
        return FrameBox(plane, -(outerHalf - 1), outerHalf - 1, y0, y1, zNose0, zNose1);
    }

    private static Brep FrameBox(
        Plane plane, double x0, double x1, double y0, double y1, double z0, double z1)
    {
        if (x1 - x0 < 0.5 || y1 - y0 < 0.5 || z1 - z0 < 0.5) return null;
        var box = new Box(
            plane,
            new Interval(x0, x1),
            new Interval(y0, y1),
            new Interval(z0, z1));
        if (!box.IsValid) return null;
        var brep = Brep.CreateFromBox(box);
        if (brep == null || !brep.IsValid) return null;
        return brep;
    }

    private static bool TryOpeningPlane(
        Point3d center, Vector3d widthDir, Vector3d thickDir, out Plane plane)
    {
        plane = Plane.Unset;
        widthDir.Z = 0;
        if (!widthDir.Unitize()) return false;
        thickDir.Z = 0;
        thickDir -= widthDir * (thickDir * widthDir);
        if (!thickDir.Unitize()) return false;
        plane = new Plane(new Point3d(center.X, center.Y, 0), widthDir, thickDir);
        return plane.IsValid;
    }

    private static bool ResolveOpeningAxes(
        Brep host,
        OpeningFootprint foot,
        WallSegment segment,
        double sill,
        double head,
        double width,
        double pad,
        double tol,
        out Point3d center,
        out Vector3d widthDir,
        out Vector3d thickDir,
        out double thickness)
    {
        var bb = foot.Bbox;
        center = bb.IsValid ? bb.Center : Point3d.Origin;
        center.Z = 0;
        widthDir = Vector3d.XAxis;
        thickDir = Vector3d.YAxis;
        thickness = 200;

        if (segment != null && segment.Length > 1 && segment.Thickness > 40)
        {
            widthDir = segment.Tangent;
            widthDir.Z = 0;
            if (!widthDir.Unitize()) widthDir = Vector3d.XAxis;
            thickDir = segment.Inward;
            thickDir.Z = 0;
            thickDir -= widthDir * (thickDir * widthDir);
            if (!thickDir.Unitize())
                thickDir = new Vector3d(-widthDir.Y, widthDir.X, 0);
            thickness = segment.Thickness;
            return true;
        }

        if (!TryFootprintWidthDir(foot, tol, out widthDir))
            widthDir = Vector3d.XAxis;
        thickDir = new Vector3d(-widthDir.Y, widthDir.X, 0);
        if (!thickDir.Unitize()) thickDir = Vector3d.YAxis;

        if (host != null && TryMeasureWallThickness(
                host, center, widthDir, thickDir, sill, head, width, pad, tol,
                out var measured, out var axis))
        {
            thickness = measured;
            var shift = (axis - center) * thickDir;
            center += thickDir * shift;
            center.Z = 0;
            return true;
        }

        if (bb.IsValid)
        {
            var thin = Math.Min(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y);
            if (thin >= 80 && thin <= 600)
                thickness = thin;
        }
        return thickness >= 40;
    }

    private static bool TryFootprintWidthDir(OpeningFootprint foot, double tol, out Vector3d dir)
    {
        dir = Vector3d.XAxis;
        if (foot == null) return false;
        var bb = foot.Bbox;
        if (foot.Curve != null)
        {
            var best = 0.0;
            Line? longest = null;
            foreach (var line in ExplodeLineSegments(foot.Curve, tol))
            {
                var len = line.From.DistanceTo(line.To);
                if (len <= best) continue;
                best = len;
                longest = line;
            }
            if (longest.HasValue && best > 1)
            {
                dir = longest.Value.To - longest.Value.From;
                dir.Z = 0;
                if (dir.Unitize()) return true;
            }
        }

        if (!bb.IsValid) return false;
        var dx = bb.Max.X - bb.Min.X;
        var dy = bb.Max.Y - bb.Min.Y;
        dir = dx >= dy ? Vector3d.XAxis : Vector3d.YAxis;
        return true;
    }

    private static bool TryMeasureWallThickness(
        Brep host,
        Point3d center,
        Vector3d widthDir,
        Vector3d thickDir,
        double sill,
        double head,
        double width,
        double pad,
        double tol,
        out double thickness,
        out Point3d axisPoint)
    {
        thickness = 0;
        axisPoint = center;
        var wallBox = host.GetBoundingBox(true);
        if (!wallBox.IsValid) return false;

        var probes = new List<Point3d>();
        void Add(double z, double along)
        {
            if (z < wallBox.Min.Z + 5 || z > wallBox.Max.Z - 5) return;
            probes.Add(new Point3d(center.X, center.Y, 0) + widthDir * along + Vector3d.ZAxis * z);
        }

        var above = Math.Min(head + 40, wallBox.Max.Z - 20);
        var beside = width * 0.5 + Math.Max(pad, 0) + 80;
        Add(above, 0);
        Add(wallBox.Max.Z - 20, 0);
        Add(above, beside);
        Add(above, -beside);
        Add(Math.Min(Math.Max(sill - 30, wallBox.Min.Z + 20), wallBox.Max.Z - 20), beside);

        foreach (var probe in probes)
        {
            if (!TryWallSpan(host, probe, thickDir, tol, out var span, out var mid))
                continue;
            if (span < 60 || span > 900) continue;
            thickness = span;
            axisPoint = mid;
            axisPoint.Z = 0;
            return true;
        }
        return false;
    }

    private static bool TryWallSpan(
        Brep host, Point3d probe, Vector3d thickDir, double tol,
        out double thickness, out Point3d mid)
    {
        thickness = 0;
        mid = probe;
        var a = probe - thickDir * 8000;
        var b = probe + thickDir * 8000;
        var curve = new LineCurve(new Point3d(a.X, a.Y, probe.Z), new Point3d(b.X, b.Y, probe.Z));
        Point3d[] pts = null;
        try
        {
            if (!Intersection.CurveBrep(curve, host, Math.Max(tol, 0.1), out _, out pts))
                return false;
        }
        catch (Exception)
        {
            return false;
        }
        if (pts == null || pts.Length < 2) return false;

        var ts = new List<double>();
        foreach (var p in pts)
            ts.Add((p - probe) * thickDir);
        ts.Sort();
        var uniq = new List<double>();
        foreach (var t in ts)
        {
            if (uniq.Count == 0 || Math.Abs(t - uniq[uniq.Count - 1]) > 1.0)
                uniq.Add(t);
        }
        if (uniq.Count < 2) return false;

        var bestDist = double.MaxValue;
        var bestThick = 0.0;
        var bestMid = 0.0;
        for (var i = 0; i + 1 < uniq.Count; i += 2)
        {
            var t0 = uniq[i];
            var t1 = uniq[i + 1];
            var span = Math.Abs(t1 - t0);
            if (span < 60) continue;
            double dist;
            if (t0 <= 0 && t1 >= 0) dist = 0;
            else dist = Math.Min(Math.Abs(t0), Math.Abs(t1));
            if (dist > 500) continue;
            if (dist < bestDist - 1 || (Math.Abs(dist - bestDist) <= 1 && span < bestThick))
            {
                bestDist = dist;
                bestThick = span;
                bestMid = 0.5 * (t0 + t1);
            }
        }
        if (bestThick < 60) return false;
        thickness = bestThick;
        mid = new Point3d(probe.X, probe.Y, 0) + thickDir * bestMid;
        return true;
    }
}
