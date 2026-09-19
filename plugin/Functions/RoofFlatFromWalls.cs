using System;
using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Flat roof slab from the outermost closed outline on the wall source layer
/// (same footprint path as floor_from_layer). Top at wall bbox Max.Z (or
/// elevation); thickness extrudes downward so wall tops meet the underside.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("roof_flat_from_walls")]
    public JObject RoofFlatFromWalls(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var layerName = parameters["layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(layerName)) layerName = "wall";
        var thickness = parameters["thickness"]?.ToObject<double>() ?? 200.0;
        var overhang = parameters["overhang"]?.ToObject<double>() ?? 0.0;
        var elevation = parameters["elevation"]?.ToObject<double?>();
        var targetLayerName = parameters["target_layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(targetLayerName)) targetLayerName = "A-ROOF";
        var namePrefix = parameters["name_prefix"]?.ToString();
        if (string.IsNullOrEmpty(namePrefix)) namePrefix = "roof-";
        var joinTolerance = parameters["join_tolerance"]?.ToObject<double?>() ?? 0;

        if (thickness <= 0)
            throw new ArgumentException("thickness must be positive");
        if (overhang < 0)
            throw new ArgumentException("overhang must be >= 0");

        var walls = CollectForskWallBreps(doc);
        if (walls.Count == 0)
        {
            return new JObject
            {
                ["ids"] = new JArray(),
                ["count"] = 0,
                ["kind"] = "roof",
                ["roof_type"] = "flat",
                ["thickness"] = thickness,
                ["overhang"] = overhang,
                ["warnings"] = new JArray(),
                ["message"] = "No Forsk walls to roof. Call walls_from_layer first."
            };
        }

        var outlineLayer = ResolveWallSourceLayer(walls, layerName);
        var profiles = CollectClosedPlanCurves(doc, outlineLayer, joinTolerance);
        if (profiles.SourceCount == 0)
            return EmptyRoofResult(thickness, overhang,
                $"No curves on layer '{profiles.SourceLayer.Name}'.");
        if (profiles.Closed.Count == 0)
            return EmptyRoofResult(thickness, overhang,
                $"No closed planar curves on layer '{profiles.SourceLayer.Name}' after join.");

        var skipped = profiles.Skipped;
        var warnings = profiles.Warnings;
        var footprints = OutermostClosedCurves(profiles.Closed, profiles.Tol);
        var topZ = elevation ?? MaxWallTopZ(walls);
        var targetLayer = EnsureLayer(doc, targetLayerName, Color.FromArgb(70, 72, 76));
        var ids = new JArray();
        BoundingBox? firstBbox = null;
        var index = 1;

        foreach (var outer in footprints)
        {
            try
            {
                var footprint = ApplyConstantOverhang(outer, overhang, profiles.Tol, warnings);
                MoveCurveToZ(footprint, topZ);
                var brep = ExtrudeClosedCurve(footprint, -thickness, profiles.Tol);
                if (brep == null || !brep.IsValid)
                {
                    warnings.Add("Roof extrude failed for an outer outline.");
                    skipped++;
                    continue;
                }

                var attr = new ObjectAttributes
                {
                    Name = $"{namePrefix}{index:D2}",
                    LayerIndex = targetLayer.Index,
                    MaterialSource = ObjectMaterialSource.MaterialFromLayer
                };
                StampForskTags(attr, new ForskStamp
                {
                    Kind = "roof",
                    Level = "0",
                    SourceLayer = profiles.SourceLayer.Name
                });
                attr.SetUserString("forsk:roof_type", "flat");
                attr.SetUserString("forsk:overhang", FormatMm(overhang));
                attr.SetUserString("forsk:thickness", FormatMm(thickness));

                var id = doc.Objects.AddBrep(brep, attr);
                if (id != Guid.Empty)
                {
                    ids.Add(id.ToString());
                    if (firstBbox == null)
                        firstBbox = brep.GetBoundingBox(true);
                    index++;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Roof failed: {ex.Message}");
                skipped++;
            }
        }

        var result = new JObject
        {
            ["ids"] = ids,
            ["count"] = ids.Count,
            ["kind"] = "roof",
            ["roof_type"] = "flat",
            ["source_curves"] = profiles.SourceCount,
            ["joined"] = profiles.JoinedCount,
            ["closed"] = profiles.Closed.Count,
            ["skipped"] = skipped,
            ["thickness"] = thickness,
            ["overhang"] = overhang,
            ["warnings"] = warnings,
            ["message"] = $"Created {ids.Count} roof slab(s) on {targetLayer.Name}, thickness {thickness}, overhang {overhang}, top at Z={topZ}."
        };

        if (ids.Count > 0 && firstBbox.HasValue && firstBbox.Value.IsValid)
        {
            var bb = firstBbox.Value;
            result["bbox"] = new JArray
            {
                new JArray(bb.Min.X, bb.Min.Y, bb.Min.Z),
                new JArray(bb.Max.X, bb.Max.Y, bb.Max.Z)
            };
        }

        doc.Views.Redraw();
        return result;
    }

    private List<WallSolid> CollectForskWallBreps(RhinoDoc doc)
    {
        var tagged = new List<WallSolid>();
        foreach (var obj in doc.Objects)
        {
            if (obj == null) continue;
            if (!string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase))
                continue;
            var brep = GetBrepFromObject(obj);
            if (brep == null) continue;
            tagged.Add(new WallSolid
            {
                Id = obj.Id,
                Brep = brep.DuplicateBrep(),
                Attributes = obj.Attributes.Duplicate()
            });
        }
        if (tagged.Count > 0) return tagged;
        return CollectWallSolids(doc, "A-WALL", null);
    }

    private static string ResolveWallSourceLayer(List<WallSolid> walls, string fallbackLayer)
    {
        foreach (var wall in walls)
        {
            var source = wall.Attributes?.GetUserString("forsk:source_layer");
            if (!string.IsNullOrWhiteSpace(source))
                return source.Trim();
        }
        return fallbackLayer;
    }

    private static double MaxWallTopZ(List<WallSolid> walls)
    {
        var maxZ = double.NegativeInfinity;
        foreach (var wall in walls)
        {
            var bb = wall.Brep.GetBoundingBox(true);
            if (!bb.IsValid) continue;
            if (bb.Max.Z > maxZ) maxZ = bb.Max.Z;
        }
        return double.IsNegativeInfinity(maxZ) ? 0.0 : maxZ;
    }

    private static Curve ApplyConstantOverhang(Curve footprint, double overhang, double tol, JArray warnings)
    {
        var working = footprint.DuplicateCurve();
        if (overhang <= 0) return working;

        Curve best = null;
        var bestArea = -1.0;
        foreach (var distance in new[] { overhang, -overhang })
        {
            Curve[] offsets;
            try
            {
                offsets = working.Offset(Plane.WorldXY, distance, tol, CurveOffsetCornerStyle.Sharp);
            }
            catch
            {
                continue;
            }
            if (offsets == null) continue;
            foreach (var offset in offsets)
            {
                if (offset == null || !offset.IsClosed || !offset.IsValid) continue;
                var area = CurveArea(offset);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = offset;
                }
            }
        }

        if (best == null)
        {
            warnings.Add("Overhang offset failed; keeping unoffset footprint.");
            return working;
        }
        return best;
    }

    private static void MoveCurveToZ(Curve curve, double z)
    {
        var bb = curve.GetBoundingBox(true);
        if (!bb.IsValid) return;
        var currentZ = 0.5 * (bb.Min.Z + bb.Max.Z);
        var dz = z - currentZ;
        if (Math.Abs(dz) > 1e-9)
            curve.Translate(new Vector3d(0, 0, dz));
    }

    private static JObject EmptyRoofResult(double thickness, double overhang, string message)
    {
        return new JObject
        {
            ["ids"] = new JArray(),
            ["count"] = 0,
            ["kind"] = "roof",
            ["roof_type"] = "flat",
            ["thickness"] = thickness,
            ["overhang"] = overhang,
            ["warnings"] = new JArray(),
            ["message"] = message
        };
    }
}
