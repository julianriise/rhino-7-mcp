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
    [McpCommand("floor_from_layer", ModelView = true)]
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

        if (IsExistingLayerName(layerName))
            return ExistingBakeRefusal();

        // The slab is locked when it exists. Unlock the layer before the add, then lock again.
        UnlockFloors(doc);
        try
        {
            return AddFloorSlabs(doc, layerName, thickness, targetLayerName, namePrefix, joinTolerance, applyDefaultMaterials);
        }
        finally
        {
            LockFloors(doc);
        }
    }

    JObject AddFloorSlabs(RhinoDoc doc, string layerName, double thickness, string targetLayerName,
        string namePrefix, double joinTolerance, bool applyDefaultMaterials)
    {
        var profiles = CollectClosedPlanCurves(doc, layerName, joinTolerance, true);
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
                var id = BakePace.AddBreps(doc, new[] { brep }, new[] { attr }, null)[0];
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

        BakePace.Redraw(doc);
        return result;
    }

    /// <summary>
    /// The floor slab stays visible and is not a click, and so is the roof
    /// when its layer is shown (Julian, 2026-10-07). The layers and each
    /// slab stay locked, the same way as the daylight map, so a click falls
    /// through to the room plate. Unlock the layer first, then the slabs,
    /// before a replace or a delete. An object reports locked when either it
    /// or its layer is. Room plates stay unlocked: they are the room pick.
    /// </summary>
    private static void UnlockFloors(RhinoDoc doc)
    {
        if (doc == null) return;
        SetFloorLayerLocked(doc, false);
        foreach (var obj in FloorSlabs(doc))
            if (obj != null && obj.IsLocked) doc.Objects.Unlock(obj.Id, false);
    }

    /// <summary>Lock each floor slab, then the layer, so a click misses the slab.</summary>
    private static void LockFloors(RhinoDoc doc)
    {
        if (doc == null) return;
        foreach (var obj in FloorSlabs(doc))
            if (obj != null && !obj.IsLocked) doc.Objects.Lock(obj.Id, false);
        SetFloorLayerLocked(doc, true);
    }

    private static IEnumerable<RhinoObject> FloorSlabs(RhinoDoc doc)
    {
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (IsForskGenerated(obj) && string.Equals(GetForskKind(obj), "floor", StringComparison.OrdinalIgnoreCase))
                yield return obj;
        }
    }

    private static void SetFloorLayerLocked(RhinoDoc doc, bool locked)
    {
        foreach (var candidate in doc.Layers)
        {
            if (candidate == null || candidate.IsDeleted) continue;
            if (!candidate.Name.Equals("A-FLOR", StringComparison.OrdinalIgnoreCase)
                && !candidate.Name.Equals("A-ROOF", StringComparison.OrdinalIgnoreCase)) continue;
            if (candidate.IsLocked == locked) continue;
            candidate.IsLocked = locked;
            doc.Layers.Modify(candidate, candidate.Index, true);
        }
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
