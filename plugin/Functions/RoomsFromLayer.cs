using System;
using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Room markers from closed curves on A-ROOM (alias layer room): each the
/// room's boundary curve with its tag data, no surface. A planar surface on
/// the plan Z is the floor slab's top face and z-fights in Rendered mode.
/// Source curves stay. clear_generated removes the markers. Then the floor
/// plates are rebuilt from the markers (RoomPlates), and a plated curve locks
/// so the plate is the click. rooms_detect later keeps one curve per room.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("rooms_from_layer", ModelView = true)]
    public JObject RoomsFromLayer(JObject parameters)
    {
        var result = BakeRoomMarkers(parameters);
        AddRoomPlates(RhinoDoc.ActiveDoc, result);
        return result;
    }

    private JObject BakeRoomMarkers(JObject parameters)
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
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var labels = RoomLabels(doc);
        var doors = OpeningBoxes(doc, false);
        var windows = OpeningBoxes(doc, true);
        var targetLayer = EnsureLayer(doc, targetLayerName, Color.FromArgb(200, 180, 120));
        var ids = new JArray();
        var forskIds = new JArray();
        var index = 1;
        var curves = new List<Curve>();
        var attrs = new List<ObjectAttributes>();
        var queuedIds = new List<string>();
        // A room follows the walls' inner faces, never their centreline.
        var walls = RoomScene(doc, tol, new JArray(), out _).Walls;

        foreach (var curve in profiles.Closed)
        {
            try
            {
                var marker = RoomMarkerFromCurve(curve);
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
                var area = CurveArea(curve);
                var flat = FlattenToWorldXY(curve, tol);
                var pts = flat == null ? null : LoopPoints(flat, tol);
                var plan = pts == null || pts.Count < 3 ? null : PlanPoints(pts);
                var inside = plan == null || walls.Count == 0 ? null : RoomDetect.InsideWalls(plan, walls, Math.Max(tol, 1.0));
                if (inside != null && Math.Abs(RoomDetect.Area(inside)) >= RoomDetect.MinAreaMm2)
                {
                    marker = RoomOutline(inside, curve.GetBoundingBox(true).Min.Z);
                    plan = inside;
                    area = Math.Abs(RoomDetect.Area(inside));
                }
                StampForskTags(attr, new ForskStamp
                {
                    Kind = "room",
                    Level = "0",
                    Id = forskId,
                    Area = area,
                    SourceLayer = profiles.SourceLayer.Name
                });
                WriteRoomType(attr, plan, area, plan == null ? "" : RoomDetect.Name(labels, plan), doors, windows);
                curves.Add(marker);
                attrs.Add(attr);
                queuedIds.Add(forskId);
                index++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Room failed: {ex.Message}");
                skipped++;
            }
        }

        var added = BakePace.AddCurves(doc, curves, attrs, null);
        for (var i = 0; i < added.Count; i++)
        {
            if (added[i] == Guid.Empty)
            {
                warnings.Add("Room marker was not added.");
                skipped++;
                continue;
            }
            ids.Add(added[i].ToString());
            forskIds.Add(queuedIds[i]);
        }

        BakePace.Redraw(doc);
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

    /// <summary>The marker: a copy of the closed outline, counter-clockwise.</summary>
    private static Curve RoomMarkerFromCurve(Curve curve)
    {
        var dup = curve?.DuplicateCurve();
        if (dup == null || !dup.IsClosed) return null;
        if (dup.ClosedCurveOrientation(Plane.WorldXY) == CurveOrientation.Clockwise)
            dup.Reverse();
        return dup;
    }
}
