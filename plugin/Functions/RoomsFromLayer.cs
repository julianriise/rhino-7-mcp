using System;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Planar room markers from closed curves on A-ROOM (alias layer room).
/// Source curves stay. clear_generated removes the markers.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("rooms_from_layer")]
    public JObject RoomsFromLayer(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var layerName = parameters["layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(layerName)) layerName = "A-ROOM";
        var targetLayerName = parameters["target_layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(targetLayerName)) targetLayerName = "A-ROOM";
        var namePrefix = parameters["name_prefix"]?.ToString();
        if (string.IsNullOrEmpty(namePrefix)) namePrefix = "room-";
        var joinTolerance = parameters["join_tolerance"]?.ToObject<double?>() ?? 0;

        if (IsExistingLayerName(layerName))
            return ExistingBakeRefusal();

        var sourceLayer = ResolveRoomSourceLayer(doc, layerName);
        if (sourceLayer == null)
        {
            return new JObject
            {
                ["ids"] = new JArray(),
                ["forsk_ids"] = new JArray(),
                ["count"] = 0,
                ["source_curves"] = 0,
                ["joined"] = 0,
                ["closed"] = 0,
                ["skipped"] = 0,
                ["warnings"] = new JArray(),
                ["message"] = $"No closed room curves. Layer '{layerName}' not found."
            };
        }

        var profiles = CollectClosedPlanCurves(doc, sourceLayer.Name, joinTolerance);
        if (profiles.SourceCount == 0)
        {
            var empty = profiles.EmptyResult($"No curves on layer '{profiles.SourceLayer.Name}'.");
            empty["forsk_ids"] = new JArray();
            empty["closed"] = 0;
            return empty;
        }

        if (profiles.Closed.Count == 0)
        {
            var empty = profiles.EmptyResult(
                $"No closed planar curves on layer '{profiles.SourceLayer.Name}' after join.");
            empty["forsk_ids"] = new JArray();
            return empty;
        }

        var skipped = profiles.Skipped;
        var warnings = profiles.Warnings;
        var targetLayer = EnsureLayer(doc, targetLayerName, Color.FromArgb(200, 180, 120));
        var ids = new JArray();
        var forskIds = new JArray();
        var index = 1;

        foreach (var curve in profiles.Closed)
        {
            try
            {
                var marker = RoomMarkerFromCurve(curve, profiles.Tol);
                if (marker == null || !marker.IsValid)
                {
                    warnings.Add("Room marker failed for a closed curve.");
                    skipped++;
                    continue;
                }

                var forskId = FormatStableId("r", index);
                var attr = new ObjectAttributes
                {
                    Name = $"{namePrefix}{index:D2}",
                    LayerIndex = targetLayer.Index,
                    MaterialSource = ObjectMaterialSource.MaterialFromLayer
                };
                StampForskTags(attr, new ForskStamp
                {
                    Kind = "room",
                    Level = "0",
                    Id = forskId,
                    Area = CurveArea(curve),
                    SourceLayer = profiles.SourceLayer.Name
                });
                var id = doc.Objects.AddBrep(marker, attr);
                if (id == Guid.Empty)
                {
                    warnings.Add("Room marker was not added.");
                    skipped++;
                    continue;
                }

                ids.Add(id.ToString());
                forskIds.Add(forskId);
                index++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Room failed: {ex.Message}");
                skipped++;
            }
        }

        doc.Views.Redraw();
        return new JObject
        {
            ["ids"] = ids,
            ["forsk_ids"] = forskIds,
            ["count"] = ids.Count,
            ["source_curves"] = profiles.SourceCount,
            ["joined"] = profiles.JoinedCount,
            ["closed"] = profiles.Closed.Count,
            ["skipped"] = skipped,
            ["warnings"] = warnings,
            ["message"] = $"Created {ids.Count} room marker(s) on {targetLayer.Name} from layer '{profiles.SourceLayer.Name}'."
        };
    }

    private Layer ResolveRoomSourceLayer(RhinoDoc doc, string layerName)
    {
        var found = FindLayerCaseInsensitive(doc, layerName);
        if (found != null) return found;
        if (layerName.Equals("room", StringComparison.OrdinalIgnoreCase))
            return FindLayerCaseInsensitive(doc, "A-ROOM");
        if (layerName.Equals("A-ROOM", StringComparison.OrdinalIgnoreCase))
            return FindLayerCaseInsensitive(doc, "room");
        return null;
    }

    private static Brep RoomMarkerFromCurve(Curve curve, double tol)
    {
        if (curve == null) return null;
        var dup = curve.DuplicateCurve();
        if (dup != null &&
            dup.IsClosed &&
            dup.ClosedCurveOrientation(Plane.WorldXY) == CurveOrientation.Clockwise)
        {
            dup.Reverse();
        }

        Brep[] planar;
        try
        {
            planar = Brep.CreatePlanarBreps(dup, Math.Max(tol, 1e-6));
        }
        catch
        {
            return null;
        }

        if (planar == null) return null;
        foreach (var brep in planar)
        {
            if (brep != null && brep.IsValid)
                return brep;
        }

        return null;
    }
}
