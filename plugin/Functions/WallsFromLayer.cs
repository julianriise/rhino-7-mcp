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
/// Extrude closed wall-layer curves to solids. Nested room outlines become
/// wall bands (outer minus inner). A slender outline standing free, in a room
/// or outside, is a wall run of its own. No roof/ceiling. Source DXF is never
/// modified.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("walls_from_layer", ModelView = true)]
    public JObject WallsFromLayer(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var layerName = parameters["layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(layerName)) layerName = "wall";
        var height = parameters["height"]?.ToObject<double>() ?? ForskDefaults.WallHeight;
        var targetLayerName = parameters["target_layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(targetLayerName)) targetLayerName = "A-WALL";
        var namePrefix = parameters["name_prefix"]?.ToString();
        if (string.IsNullOrEmpty(namePrefix)) namePrefix = "wall-";
        var joinTolerance = parameters["join_tolerance"]?.ToObject<double?>() ?? 0;
        var applyDefaultMaterials = parameters["apply_default_materials"]?.ToObject<bool?>() ?? true;

        if (height <= 0)
            throw new ArgumentException("height must be positive");

        if (IsExistingLayerName(layerName))
            return ExistingBakeRefusal();

        if (IsRoofOrCeilingLayerName(layerName))
        {
            return new JObject
            {
                ["ids"] = new JArray(),
                ["count"] = 0,
                ["source_curves"] = 0,
                ["joined"] = 0,
                ["skipped"] = 0,
                ["warnings"] = new JArray(),
                ["message"] = $"Layer '{layerName}' is roof/ceiling/slab — ignored in 2D to 3D (walls and openings only)."
            };
        }

        var profiles = CollectClosedPlanCurves(doc, layerName, joinTolerance, true);
        if (profiles.SourceCount == 0)
            return profiles.EmptyResult($"No curves on layer '{profiles.SourceLayer.Name}'.");
        if (profiles.Closed.Count == 0)
            return profiles.EmptyResult(
                $"No closed planar curves on layer '{profiles.SourceLayer.Name}' after join.");

        var skipped = profiles.Skipped;
        var warnings = profiles.Warnings;
        var targetLayer = EnsureLayer(doc, targetLayerName, Color.FromArgb(180, 180, 180));
        var bakes = BuildWallBakes(profiles.Closed, height, profiles.Tol, warnings);
        var ids = new JArray();
        var forskIds = new JArray();
        var index = 1;
        string thicknessNote = null;
        var solids = new List<Brep>();
        var solidAttrs = new List<ObjectAttributes>();
        var solidIds = new List<string>();

        foreach (var bake in bakes)
        {
            BakePace.Breathe(BakePace.BuildingWalls);
            try
            {
                var breps = bake.Breps;
                if (breps == null || breps.Count == 0) continue;
                var solePath = breps.Count == 1
                    ? EncodeWallPath(bake.Outer, bake.Holes, profiles.Tol)
                    : null;
                foreach (var brep in breps)
                {
                    if (brep == null || !brep.IsValid) continue;
                    if (LooksLikeRoofOrRoomFill(brep, height))
                    {
                        warnings.Add("Skipped a capped room/roof solid; 2D to 3D keeps walls open at the top.");
                        skipped++;
                        continue;
                    }
                    var path = !string.IsNullOrEmpty(solePath)
                        ? solePath
                        : EncodePathFromBrep(brep, profiles.Tol);
                    if (string.IsNullOrEmpty(path))
                        warnings.Add("Could not store a param path on " + namePrefix + index.ToString("D2") + ".");
                    foreach (var record in SplitWallBake(brep, path, height, profiles.Tol, warnings))
                    {
                        var read = MeasureWallThickness(record.Path, record.Brep, profiles.Tol);
                        if (!read.Measured && string.IsNullOrEmpty(thicknessNote))
                            thicknessNote = read.Receipt;
                        var thickness = read.Millimetres;
                        var attr = new ObjectAttributes
                        {
                            Name = $"{namePrefix}{index:D2}",
                            LayerIndex = targetLayer.Index,
                            MaterialSource = ObjectMaterialSource.MaterialFromLayer
                        };
                        var forskId = FormatStableId("w", index);
                        StampForskTags(attr, new ForskStamp
                        {
                            Kind = "wall",
                            Level = "0",
                            Id = forskId,
                            Height = height,
                            Thickness = thickness > 0 ? (double?)thickness : null,
                            Path = record.Path,
                            SourceLayer = profiles.SourceLayer.Name
                        });
                        // Queued, then added together with redraw held. The name stays on a failed add.
                        solids.Add(record.Brep);
                        solidAttrs.Add(attr);
                        solidIds.Add(forskId);
                        index++;
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Wall profile failed: {ex.Message}");
            }
        }

        var added = BakePace.AddBreps(doc, solids, solidAttrs, BakePace.BuildingWalls);
        for (var i = 0; i < added.Count; i++)
        {
            if (added[i] == Guid.Empty) continue;
            ids.Add(added[i].ToString());
            forskIds.Add(solidIds[i]);
        }

        var message = $"Created {ids.Count} wall solid(s) on {targetLayer.Name} from layer '{profiles.SourceLayer.Name}'.";
        if (ids.Count > 0 && !string.IsNullOrEmpty(thicknessNote))
        {
            warnings.Add(thicknessNote);
            message = message + " " + thicknessNote;
        }
        var result = new JObject
        {
            ["ids"] = ids,
            ["forsk_ids"] = forskIds,
            ["count"] = ids.Count,
            ["source_curves"] = profiles.SourceCount,
            ["joined"] = profiles.JoinedCount,
            ["closed"] = profiles.Closed.Count,
            ["skipped"] = skipped,
            ["warnings"] = warnings,
            ["message"] = message
        };

        if (applyDefaultMaterials && ids.Count > 0)
            TryApplyDefaultMaterial(doc, targetLayer.Name, "plaster", warnings, result);

        // R5: a stair whose rise is auto follows the new wall height.
        var stairs = ids.Count > 0 ? ReplanAutoStairs(doc) : null;
        if (stairs != null)
        {
            result["stairs"] = stairs;
            result["message"] = message + " " + stairs;
        }

        BakePace.Redraw(doc);
        return result;
    }

    /// <summary>
    /// Selection S1: one record per straight run, so a click picks one wall.
    /// The bake's outline is cut into pieces (WallSplit) and each piece is
    /// extruded as its own solid at the bake's base. A bake the split refuses,
    /// or whose piece will not extrude, stays one record, and the warnings say why.
    /// </summary>
    private List<(Brep Brep, string Path)> SplitWallBake(Brep brep, string path, double height, double tol, JArray warnings)
    {
        var whole = new List<(Brep Brep, string Path)> { (brep, path) };
        var rings = WallEdit.Rings(path);
        if (rings == null) return whole;
        var splitTol = Math.Max(tol, 1.0);
        var graph = WallJoins.Build(new List<List<List<RoomDetect.Pt>>> { rings }, new List<int> { 0 }, splitTol);
        string why = null;
        var pieces = graph == null ? null : WallSplit.Pieces(graph, splitTol, out why);
        if (pieces == null)
        {
            if (!string.IsNullOrEmpty(why)) warnings.Add("A wall stays one record. " + why);
            return whole;
        }
        if (pieces.Count < 2) return whole;
        var z = brep.GetBoundingBox(true).Min.Z;
        var split = new List<(Brep Brep, string Path)>();
        foreach (var piece in pieces)
        {
            BakePace.Breathe(BakePace.BuildingWalls);
            var piecePath = WallEdit.Path(new List<List<RoomDetect.Pt>> { piece.Ring });
            var solid = PrepareFreshSolid(ExtrudeFromPath(piecePath, height, tol, warnings), tol, out var diagnostic);
            if (solid == null)
            {
                warnings.Add("A wall stays one record. Not split: a piece did not extrude. " + diagnostic);
                return whole;
            }
            if (Math.Abs(z) > 1e-9) solid.Translate(new Vector3d(0, 0, z));
            split.Add((solid, piecePath));
        }
        return split;
    }

    private sealed class WallBake
    {
        public List<Brep> Breps = new List<Brep>();
        public Curve Outer;
        public List<Curve> Holes = new List<Curve>();
    }

    private List<WallBake> BuildWallBakes(
        List<Curve> closed,
        double height,
        double tol,
        JArray warnings)
    {
        var n = closed.Count;
        var parentOf = new int[n];
        for (var i = 0; i < n; i++) parentOf[i] = -1;

        var areas = new double[n];
        for (var i = 0; i < n; i++)
            areas[i] = CurveArea(closed[i]);

        var containTol = Math.Max(tol, 1.0);
        for (var i = 0; i < n; i++)
        {
            var best = -1;
            var bestArea = double.MaxValue;
            for (var j = 0; j < n; j++)
            {
                if (i == j) continue;
                if (!CurveContainsPointOf(closed[j], closed[i], containTol)) continue;
                if (areas[j] < bestArea)
                {
                    bestArea = areas[j];
                    best = j;
                }
            }
            parentOf[i] = best;
        }

        var children = new List<int>[n];
        for (var i = 0; i < n; i++) children[i] = new List<int>();
        var hasNesting = false;
        for (var i = 0; i < n; i++)
        {
            if (parentOf[i] < 0) continue;
            children[parentOf[i]].Add(i);
            hasNesting = true;
        }

        var result = new List<WallBake>();

        if (!hasNesting)
        {
            foreach (var curve in closed)
            {
                var brep = ExtrudeClosedCurve(curve, height, tol);
                if (brep == null)
                {
                    warnings.Add("Extrude failed for a closed curve.");
                    continue;
                }
                if (LooksLikeRoofOrRoomFill(brep, height))
                {
                    warnings.Add("Skipped a capped room outline (would become a roof/ceiling).");
                    continue;
                }
                result.Add(new WallBake
                {
                    Breps = new List<Brep> { brep },
                    Outer = curve,
                    Holes = new List<Curve>()
                });
            }
            return result;
        }

        // Wall, the space inside it, wall again: an outline standing free in a
        // room is a wall run of its own, not a hole in that room.
        var space = new bool[n];
        for (var i = 0; i < n; i++)
        {
            // At most n steps up: two copies of one outline hold each other.
            var parent = parentOf[i];
            for (var step = 0; parent >= 0 && step < n; step++, parent = parentOf[parent]) space[i] = !space[i];
        }

        for (var i = 0; i < n; i++)
        {
            if (space[i]) continue;
            if (children[i].Count == 0)
            {
                var brep = ExtrudeClosedCurve(closed[i], height, tol);
                if (brep == null)
                    warnings.Add("Extrude failed for a disjoint closed curve.");
                else if (LooksLikeRoofOrRoomFill(brep, height))
                    warnings.Add("Skipped a disjoint capped room outline (would become a roof/ceiling).");
                else
                    result.Add(new WallBake
                    {
                        Breps = new List<Brep> { brep },
                        Outer = closed[i],
                        Holes = new List<Curve>()
                    });
                continue;
            }

            var holes = children[i].Select(idx => closed[idx]).ToList();
            var band = WallBandFromParentAndHoles(closed[i], holes, height, tol, warnings);
            if (band.Count > 0)
                result.Add(new WallBake
                {
                    Breps = band,
                    Outer = closed[i],
                    Holes = holes
                });
        }

        return result;
    }

    private List<Brep> WallBandFromParentAndHoles(
        Curve parent,
        List<Curve> holes,
        double height,
        double tol,
        JArray warnings)
    {
        var booleanTol = Math.Max(tol, 0.1);
        var planar = ExtrudeParentWithPlanarHoles(parent, holes, height, booleanTol);
        if (planar.Count > 0) return planar;

        var sequential = SequentialDifference(parent, holes, height, booleanTol);
        if (sequential.Count > 0) return sequential;

        warnings.Add($"Wall band failed for outline with {holes.Count} hole(s).");
        return new List<Brep>();
    }

    private List<Brep> ExtrudeParentWithPlanarHoles(
        Curve parent,
        List<Curve> holes,
        double height,
        double tol)
    {
        var inputs = new List<Curve>();
        var outer = parent.DuplicateCurve();
        if (outer.ClosedCurveOrientation(Plane.WorldXY) == CurveOrientation.Clockwise)
            outer.Reverse();
        inputs.Add(outer);
        foreach (var hole in holes)
        {
            var h = hole.DuplicateCurve();
            if (h.ClosedCurveOrientation(Plane.WorldXY) == CurveOrientation.CounterClockwise)
                h.Reverse();
            inputs.Add(h);
        }

        Brep[] planar;
        try
        {
            planar = Brep.CreatePlanarBreps(inputs, tol);
        }
        catch
        {
            return new List<Brep>();
        }

        if (planar == null || planar.Length == 0)
            return new List<Brep>();

        var expectedArea = CurveArea(parent) - holes.Sum(CurveArea);
        if (expectedArea < 0) expectedArea = 0;
        var extruded = ExtrudePlanarFaces(planar, height, tol);
        if (extruded.Count == 0) return extruded;

        if (expectedArea > 0)
        {
            var kept = new List<Brep>();
            foreach (var brep in extruded)
            {
                var vmp = VolumeMassProperties.Compute(brep);
                if (vmp == null)
                {
                    kept.Add(brep);
                    continue;
                }
                var faceArea = Math.Abs(vmp.Volume) / Math.Max(height, 1e-6);
                if (faceArea <= expectedArea * 1.35 + 1.0)
                    kept.Add(brep);
            }
            if (kept.Count > 0) return kept;
        }

        return extruded;
    }

    private List<Brep> ExtrudePlanarFaces(IEnumerable<Brep> planar, double height, double tol)
    {
        var result = new List<Brep>();
        var delta = new Vector3d(0, 0, height);
        foreach (var pb in planar)
        {
            if (pb == null) continue;
            for (var i = 0; i < pb.Faces.Count; i++)
            {
                var face = pb.Faces[i];
                var domainU = face.Domain(0);
                var domainV = face.Domain(1);
                var origin = face.PointAt(domainU.Mid, domainV.Mid);
                var path = new LineCurve(origin, origin + delta);
                Brep extruded = null;
                try
                {
                    extruded = face.CreateExtrusion(path, true);
                }
                catch
                {
                    extruded = null;
                }

                if (extruded == null || !extruded.IsValid)
                {
                    var outer = face.OuterLoop?.To3dCurve();
                    if (outer != null)
                        extruded = ExtrudeClosedCurve(outer, height, tol);
                }

                if (extruded != null && extruded.IsValid)
                    result.Add(extruded);
            }
        }
        return result;
    }

    private List<Brep> SequentialDifference(
        Curve parent,
        List<Curve> holes,
        double height,
        double tol)
    {
        var current = new List<Brep>();
        var parentBrep = ExtrudeClosedCurve(parent, height, tol);
        if (parentBrep == null) return current;
        current.Add(parentBrep);

        foreach (var hole in holes)
        {
            var cutter = ExtrudeClosedCurve(hole, height, tol);
            if (cutter == null) continue;
            var next = new List<Brep>();
            foreach (var piece in current)
            {
                Brep[] diff = null;
                try
                {
                    diff = Brep.CreateBooleanDifference(new[] { piece }, new[] { cutter }, tol);
                }
                catch
                {
                    diff = null;
                }

                if (diff != null && diff.Length > 0)
                    next.AddRange(diff.Where(b => b != null && b.IsValid));
                else
                    next.Add(piece);
            }
            if (next.Count > 0)
                current = next;
        }

        var parentArea = CurveArea(parent);
        if (parentArea <= 0) return current;
        var kept = new List<Brep>();
        foreach (var brep in current)
        {
            var vmp = VolumeMassProperties.Compute(brep);
            if (vmp == null)
            {
                kept.Add(brep);
                continue;
            }
            var faceArea = Math.Abs(vmp.Volume) / Math.Max(height, 1e-6);
            if (faceArea <= parentArea * 0.95)
                kept.Add(brep);
        }
        return kept.Count > 0 ? kept : new List<Brep>();
    }

    /// <summary>
    /// A solid that fills most of its bounding box is a room or roof outline
    /// drawn on the wall layer, not a wall. A slender one is a wall run
    /// standing free: it stays a wall, never a room.
    /// </summary>
    private static bool LooksLikeRoofOrRoomFill(Brep brep, double height)
    {
        if (brep == null || !brep.IsValid) return false;
        var bbox = brep.GetBoundingBox(true);
        if (!bbox.IsValid) return false;
        var xy = (bbox.Max.X - bbox.Min.X) * (bbox.Max.Y - bbox.Min.Y);
        var vmp = VolumeMassProperties.Compute(brep);
        if (vmp == null) return false;
        var tall = Math.Max(Math.Abs(height), 1e-6);
        var footprint = Math.Abs(vmp.Volume) / tall;
        if (!RoomDetect.IsBlock(footprint, xy)) return false;
        // The sides' area over the height is the length of the outline's edge.
        var amp = AreaMassProperties.Compute(brep);
        return amp == null || !RoomDetect.IsWallRun(footprint, (amp.Area - 2.0 * footprint) / tall);
    }
}
