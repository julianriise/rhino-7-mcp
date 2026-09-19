using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Closed, WorldXY-flattened plan curves shared by walls and floor.
/// Openings do not use this pipeline (bbox / instance footprints).
/// </summary>
public partial class RhinoMCPFunctions
{
    private sealed class ClosedPlanCurves
    {
        public Layer SourceLayer;
        public int SourceCount;
        public int JoinedCount;
        public List<Curve> Closed;
        public int Skipped;
        public JArray Warnings;
        public double Tol;
        public double JoinTol;

        public JObject EmptyResult(string message)
        {
            return new JObject
            {
                ["ids"] = new JArray(),
                ["count"] = 0,
                ["source_curves"] = SourceCount,
                ["joined"] = JoinedCount,
                ["skipped"] = Skipped,
                ["warnings"] = Warnings,
                ["message"] = message
            };
        }
    }

    private ClosedPlanCurves CollectClosedPlanCurves(
        RhinoDoc doc,
        string layerName,
        double joinToleranceParam)
    {
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var joinTol = joinToleranceParam > 0
            ? joinToleranceParam
            : Math.Max(tol * 10.0, 1.0);

        var sourceLayer = FindLayerCaseInsensitive(doc, layerName);
        if (sourceLayer == null)
            throw new InvalidOperationException($"Layer '{layerName}' not found.");

        var rawCurves = new List<Curve>();
        foreach (var obj in doc.Objects)
        {
            if (!ObjectOnLayer(doc, obj, sourceLayer)) continue;
            if (obj.Geometry is Curve curve)
                rawCurves.Add(curve.DuplicateCurve());
        }

        var warnings = new JArray();
        var profiles = new ClosedPlanCurves
        {
            SourceLayer = sourceLayer,
            SourceCount = rawCurves.Count,
            JoinedCount = 0,
            Closed = new List<Curve>(),
            Skipped = 0,
            Warnings = warnings,
            Tol = tol,
            JoinTol = joinTol
        };

        if (rawCurves.Count == 0)
            return profiles;

        var joined = JoinAndCloseCurves(rawCurves, joinTol);
        profiles.JoinedCount = joined.Length;

        foreach (var curve in joined)
        {
            var flat = FlattenToWorldXY(curve, tol);
            if (flat == null)
            {
                profiles.Skipped++;
                continue;
            }

            if (!flat.IsClosed)
            {
                var gap = flat.PointAtStart.DistanceTo(flat.PointAtEnd);
                if (gap <= joinTol)
                    flat.MakeClosed(joinTol);
            }

            if (!flat.IsClosed)
            {
                profiles.Skipped++;
                continue;
            }

            if (!flat.IsValid)
            {
                profiles.Skipped++;
                continue;
            }

            profiles.Closed.Add(flat);
        }

        return profiles;
    }

    private static bool IsRoofOrCeilingLayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        return n.Equals("roof", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("ceiling", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("slab", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("floor", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("soffit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CurveContainsPointOf(Curve outer, Curve inner, double tol)
    {
        try
        {
            var rel = Curve.PlanarClosedCurveRelationship(inner, outer, Plane.WorldXY, tol);
            if (rel == RegionContainment.AInsideB) return true;
            if (rel == RegionContainment.BInsideA) return false;
            if (rel == RegionContainment.MutualIntersection) return false;
        }
        catch
        {
            // Fall through to point-in-curve.
        }

        Point3d pt;
        var amp = AreaMassProperties.Compute(inner);
        if (amp != null) pt = amp.Centroid;
        else pt = inner.PointAtNormalizedLength(0.5);

        try
        {
            return outer.Contains(pt, Plane.WorldXY, tol) == PointContainment.Inside;
        }
        catch
        {
            return false;
        }
    }

    private Brep ExtrudeClosedCurve(Curve curve, double height, double tol)
    {
        var oriented = curve.DuplicateCurve();
        if (oriented.IsClosed &&
            oriented.ClosedCurveOrientation(Plane.WorldXY) == CurveOrientation.Clockwise)
        {
            oriented.Reverse();
        }

        try
        {
            var extrusion = Extrusion.Create(oriented, height, true);
            if (extrusion != null)
            {
                var fromExtrusion = extrusion.ToBrep();
                if (fromExtrusion != null && fromExtrusion.IsValid)
                    return fromExtrusion;
            }
        }
        catch
        {
            // Fall through to surface extrusion.
        }

        var direction = new Vector3d(0, 0, height);
        var surface = Surface.CreateExtrusion(oriented, direction);
        if (surface == null) return null;
        var brep = surface.ToBrep();
        if (brep == null) return null;
        if (oriented.IsClosed)
        {
            var capped = brep.CapPlanarHoles(tol);
            if (capped != null) return capped;
        }
        return brep;
    }

    private static Curve[] JoinAndCloseCurves(List<Curve> raw, double joinTol)
    {
        if (raw.Count == 0) return Array.Empty<Curve>();
        var joined = Curve.JoinCurves(raw, joinTol, false);
        if (joined == null || joined.Length == 0) return raw.ToArray();
        foreach (var curve in joined)
        {
            if (curve.IsClosed) continue;
            if (curve.PointAtStart.DistanceTo(curve.PointAtEnd) <= joinTol)
                curve.MakeClosed(joinTol);
        }
        return joined;
    }

    private static Curve FlattenToWorldXY(Curve curve, double tol)
    {
        if (curve == null) return null;
        var dup = curve.DuplicateCurve();
        var bbox = dup.GetBoundingBox(true);
        if (!bbox.IsValid) return dup;
        var planZ = bbox.Min.Z;
        var plane = new Plane(new Point3d(0, 0, planZ), Vector3d.ZAxis);
        if (Math.Abs(bbox.Max.Z - bbox.Min.Z) > Math.Max(tol * 10.0, 1.0))
        {
            var projected = Curve.ProjectToPlane(dup, plane);
            return projected ?? dup;
        }
        return dup;
    }

    private static double CurveArea(Curve curve)
    {
        var amp = AreaMassProperties.Compute(curve);
        if (amp != null) return Math.Abs(amp.Area);
        var bbox = curve.GetBoundingBox(true);
        return Math.Abs((bbox.Max.X - bbox.Min.X) * (bbox.Max.Y - bbox.Min.Y));
    }
}
