using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Floor slab from the outermost closed wall outline, extruded down so the
/// slab top stays on the 2D plan Z.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("floor_from_layer")]
    public JObject FloorFromLayer(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var layerName = parameters["layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(layerName)) layerName = "wall";
        var thickness = parameters["thickness"]?.ToObject<double>() ?? ForskDefaults.FloorThickness;
        var targetLayerName = parameters["target_layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(targetLayerName)) targetLayerName = "A-FLOR";
        var namePrefix = parameters["name_prefix"]?.ToString();
        if (string.IsNullOrEmpty(namePrefix)) namePrefix = "floor-";
        var joinTolerance = parameters["join_tolerance"]?.ToObject<double?>() ?? 0;
        var applyDefaultMaterials = parameters["apply_default_materials"]?.ToObject<bool?>() ?? true;

        if (thickness <= 0)
            throw new ArgumentException("thickness must be positive");

        var profiles = CollectClosedPlanCurves(doc, layerName, joinTolerance);
        if (profiles.SourceCount == 0)
            return profiles.EmptyResult($"No curves on layer '{profiles.SourceLayer.Name}'.");
        if (profiles.Closed.Count == 0)
            return profiles.EmptyResult(
                $"No closed planar curves on layer '{profiles.SourceLayer.Name}' after join.");

        var skipped = profiles.Skipped;
        var warnings = profiles.Warnings;
        var footprints = OutermostClosedCurves(profiles.Closed, profiles.Tol);
        var targetLayer = EnsureLayer(doc, targetLayerName, Color.FromArgb(150, 145, 138));
        var ids = new JArray();
        var index = 1;

        foreach (var footprint in footprints)
        {
            try
            {
                // Downward so the slab top stays on the 2D plan Z.
                var brep = ExtrudeClosedCurve(footprint, -thickness, profiles.Tol);
                if (brep == null || !brep.IsValid)
                {
                    warnings.Add("Floor extrude failed for an outer outline.");
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
                    Kind = "floor",
                    Level = "0",
                    SourceLayer = profiles.SourceLayer.Name
                });
                var id = doc.Objects.AddBrep(brep, attr);
                if (id != Guid.Empty)
                {
                    ids.Add(id.ToString());
                    index++;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Floor failed: {ex.Message}");
                skipped++;
            }
        }

        var result = new JObject
        {
            ["ids"] = ids,
            ["count"] = ids.Count,
            ["source_curves"] = profiles.SourceCount,
            ["joined"] = profiles.JoinedCount,
            ["closed"] = profiles.Closed.Count,
            ["skipped"] = skipped,
            ["thickness"] = thickness,
            ["warnings"] = warnings,
            ["message"] = $"Created {ids.Count} floor slab(s) on {targetLayer.Name}, thickness {thickness}, top at plan Z."
        };

        if (applyDefaultMaterials && ids.Count > 0)
            TryApplyDefaultMaterial(doc, targetLayer.Name, "concrete", warnings, result);

        doc.Views.Redraw();
        return result;
    }

    private static List<Curve> OutermostClosedCurves(List<Curve> closed, double tol)
    {
        var n = closed.Count;
        if (n == 0) return new List<Curve>();
        var containTol = Math.Max(tol, 1.0);
        var outermost = new List<Curve>();
        for (var i = 0; i < n; i++)
        {
            var insideAnother = false;
            for (var j = 0; j < n; j++)
            {
                if (i == j) continue;
                if (CurveContainsPointOf(closed[j], closed[i], containTol))
                {
                    insideAnother = true;
                    break;
                }
            }
            if (!insideAnother)
                outermost.Add(closed[i]);
        }
        if (outermost.Count == 0)
            outermost.Add(closed.OrderByDescending(CurveArea).First());
        return outermost;
    }
}
