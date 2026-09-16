using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Cut door/window openings through wall solids. No door or window objects
/// are created. Source DXF geometry is never modified.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("openings_from_layer")]
    public JObject OpeningsFromLayer(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var layerName = parameters["layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(layerName))
            throw new ArgumentException("layer is required (door or window)");

        var targetLayerName = parameters["target_layer"]?.ToString();
        if (string.IsNullOrWhiteSpace(targetLayerName)) targetLayerName = "A-WALL";
        var pad = parameters["pad"]?.ToObject<double?>() ?? 50.0;
        var minDepth = parameters["min_depth"]?.ToObject<double?>() ?? 250.0;
        var limit = parameters["limit"]?.ToObject<int?>();
        var wallIdTokens = parameters["wall_ids"]?.ToObject<List<string>>();

        var layerKey = layerName.Trim();
        double defaultSill = 0;
        double defaultHead = 2100;
        if (layerKey.Equals("window", StringComparison.OrdinalIgnoreCase))
        {
            defaultSill = 900;
            defaultHead = 2100;
        }
        else if (layerKey.Equals("door", StringComparison.OrdinalIgnoreCase))
        {
            defaultSill = 0;
            defaultHead = 2100;
        }

        var sill = parameters["sill"]?.ToObject<double?>() ?? defaultSill;
        var head = parameters["head"]?.ToObject<double?>() ?? defaultHead;
        if (head <= sill)
            throw new ArgumentException("head must be greater than sill");

        if (IsRoofOrCeilingLayerName(layerName))
        {
            return new JObject
            {
                ["cut_count"] = 0,
                ["failed_count"] = 0,
                ["failures"] = new JArray(),
                ["wall_ids"] = new JArray(),
                ["opening_count"] = 0,
                ["sill"] = sill,
                ["head"] = head,
                ["message"] = $"Layer '{layerName}' is roof/ceiling/slab — ignored in 2D to 3D."
            };
        }

        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var sourceLayer = FindLayerCaseInsensitive(doc, layerName);
        if (sourceLayer == null)
            throw new InvalidOperationException($"Layer '{layerName}' not found.");

        var walls = CollectWallSolids(doc, targetLayerName, wallIdTokens);
        if (walls.Count == 0)
        {
            return new JObject
            {
                ["cut_count"] = 0,
                ["failed_count"] = 0,
                ["failures"] = new JArray(),
                ["wall_ids"] = new JArray(),
                ["opening_count"] = 0,
                ["message"] = $"No wall solids on '{targetLayerName}' to cut."
            };
        }

        var footprints = CollectOpeningFootprints(doc, sourceLayer, tol);
        if (limit.HasValue && limit.Value > 0 && footprints.Count > limit.Value)
            footprints = footprints.Take(limit.Value).ToList();

        var failures = new JArray();
        var cutCount = 0;

        foreach (var foot in footprints)
        {
            Brep cutter = null;
            try
            {
                cutter = BuildOpeningCutter(foot, sill, head, pad, minDepth, tol);
                if (cutter == null || !cutter.IsValid)
                {
                    failures.Add(new JObject
                    {
                        ["source_id"] = foot.SourceId,
                        ["reason"] = "Could not build opening cutter"
                    });
                    continue;
                }

                var cutterBox = cutter.GetBoundingBox(true);
                cutterBox.Inflate(tol);
                var hit = false;
                var splitPieces = new List<WallSolid>();

                for (var i = 0; i < walls.Count; i++)
                {
                    var wall = walls[i];
                    var wallBox = wall.Brep.GetBoundingBox(true);
                    if (!BboxesOverlapXY(wallBox, cutterBox)) continue;

                    Brep[] results = null;
                    try
                    {
                        results = Brep.CreateBooleanDifference(
                            new[] { wall.Brep },
                            new[] { cutter },
                            tol);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new JObject
                        {
                            ["source_id"] = foot.SourceId,
                            ["wall_id"] = wall.Id.ToString(),
                            ["reason"] = $"Boolean threw: {ex.Message}"
                        });
                        continue;
                    }

                    if (results == null || results.Length == 0)
                        continue;

                    var valid = results.Where(b => b != null && b.IsValid).ToList();
                    if (valid.Count == 0) continue;

                    if (!doc.Objects.Delete(wall.Id, true))
                    {
                        failures.Add(new JObject
                        {
                            ["source_id"] = foot.SourceId,
                            ["wall_id"] = wall.Id.ToString(),
                            ["reason"] = "Failed to replace wall after boolean"
                        });
                        continue;
                    }

                    WallSolid first = null;
                    for (var p = 0; p < valid.Count; p++)
                    {
                        var attr = wall.Attributes.Duplicate();
                        if (p > 0 && !string.IsNullOrEmpty(attr.Name))
                            attr.Name = $"{attr.Name}-{p + 1}";
                        attr.MaterialSource = ObjectMaterialSource.MaterialFromLayer;
                        attr.MaterialIndex = -1;
                        var newId = doc.Objects.AddBrep(valid[p], attr);
                        var replacement = new WallSolid
                        {
                            Id = newId,
                            Brep = valid[p],
                            Attributes = attr
                        };
                        if (p == 0) first = replacement;
                        else splitPieces.Add(replacement);
                    }

                    if (first != null)
                        walls[i] = first;
                    hit = true;
                }

                if (splitPieces.Count > 0)
                    walls.AddRange(splitPieces);

                if (hit) cutCount++;
                else
                {
                    failures.Add(new JObject
                    {
                        ["source_id"] = foot.SourceId,
                        ["reason"] = "Cutter did not intersect any wall solid"
                    });
                }
            }
            catch (Exception ex)
            {
                failures.Add(new JObject
                {
                    ["source_id"] = foot.SourceId,
                    ["reason"] = ex.Message
                });
            }
        }

        doc.Views.Redraw();

        var wallIds = new JArray(walls.Select(w => w.Id.ToString()));
        return new JObject
        {
            ["cut_count"] = cutCount,
            ["failed_count"] = failures.Count,
            ["failures"] = failures,
            ["wall_ids"] = wallIds,
            ["opening_count"] = footprints.Count,
            ["sill"] = sill,
            ["head"] = head,
            ["message"] = $"Cut {cutCount} opening(s) from layer '{sourceLayer.Name}' ({failures.Count} failure(s))."
        };
    }

    private sealed class WallSolid
    {
        public Guid Id;
        public Brep Brep;
        public ObjectAttributes Attributes;
    }

    private sealed class OpeningFootprint
    {
        public string SourceId;
        public BoundingBox Bbox;
        public Curve Curve;
    }

    private List<WallSolid> CollectWallSolids(RhinoDoc doc, string targetLayerName, List<string> wallIdTokens)
    {
        var result = new List<WallSolid>();
        if (wallIdTokens != null && wallIdTokens.Count > 0)
        {
            foreach (var token in wallIdTokens)
            {
                if (!Guid.TryParse(token, out var guid)) continue;
                var obj = doc.Objects.Find(guid);
                if (obj == null) continue;
                var brep = GetBrepFromObject(obj);
                if (brep == null) continue;
                result.Add(new WallSolid
                {
                    Id = obj.Id,
                    Brep = brep.DuplicateBrep(),
                    Attributes = obj.Attributes.Duplicate()
                });
            }
            return result;
        }

        var layer = FindLayerCaseInsensitive(doc, targetLayerName);
        if (layer == null) return result;

        foreach (var obj in doc.Objects)
        {
            if (!ObjectOnLayer(doc, obj, layer)) continue;
            var brep = GetBrepFromObject(obj);
            if (brep == null) continue;
            result.Add(new WallSolid
            {
                Id = obj.Id,
                Brep = brep.DuplicateBrep(),
                Attributes = obj.Attributes.Duplicate()
            });
        }
        return result;
    }

    private List<OpeningFootprint> CollectOpeningFootprints(RhinoDoc doc, Layer sourceLayer, double tol)
    {
        var curves = new List<OpeningFootprint>();
        var instances = new List<OpeningFootprint>();

        foreach (var obj in doc.Objects)
        {
            if (!ObjectOnLayer(doc, obj, sourceLayer)) continue;

            if (obj is InstanceObject inst)
            {
                var bbox = inst.Geometry.GetBoundingBox(true);
                if (!bbox.IsValid) continue;
                instances.Add(new OpeningFootprint
                {
                    SourceId = obj.Id.ToString(),
                    Bbox = bbox
                });
                continue;
            }

            if (obj.Geometry is Curve curve)
            {
                if (curve is ArcCurve) continue;
                var flat = FlattenToWorldXY(curve.DuplicateCurve(), tol);
                if (flat == null) continue;
                if (!flat.IsClosed)
                {
                    var gap = flat.PointAtStart.DistanceTo(flat.PointAtEnd);
                    if (gap <= Math.Max(tol * 10.0, 1.0))
                        flat.MakeClosed(Math.Max(tol * 10.0, 1.0));
                }
                if (!flat.IsClosed) continue;
                var bbox = flat.GetBoundingBox(true);
                if (!bbox.IsValid) continue;
                curves.Add(new OpeningFootprint
                {
                    SourceId = obj.Id.ToString(),
                    Bbox = bbox,
                    Curve = flat
                });
            }
        }

        // Prefer closed plan gaps (door rectangles) over swing-arc blocks.
        var chosen = curves.Count > 0 ? curves : instances;
        return chosen
            .OrderBy(f => f.Bbox.Min.X)
            .ThenBy(f => f.Bbox.Min.Y)
            .ToList();
    }

    private Brep BuildOpeningCutter(
        OpeningFootprint foot,
        double sill,
        double head,
        double pad,
        double minDepth,
        double tol)
    {
        var bbox = foot.Bbox;
        var minX = bbox.Min.X;
        var maxX = bbox.Max.X;
        var minY = bbox.Min.Y;
        var maxY = bbox.Max.Y;
        var dx = maxX - minX;
        var dy = maxY - minY;
        var cx = 0.5 * (minX + maxX);
        var cy = 0.5 * (minY + maxY);

        if (dx >= dy)
        {
            if (dy < minDepth)
            {
                minY = cy - minDepth * 0.5;
                maxY = cy + minDepth * 0.5;
            }
            minX -= pad;
            maxX += pad;
        }
        else
        {
            if (dx < minDepth)
            {
                minX = cx - minDepth * 0.5;
                maxX = cx + minDepth * 0.5;
            }
            minY -= pad;
            maxY += pad;
        }

        if (maxX - minX < tol || maxY - minY < tol || head - sill < tol)
            return null;

        var box = new Box(
            Plane.WorldXY,
            new Interval(minX, maxX),
            new Interval(minY, maxY),
            new Interval(sill, head));
        return Brep.CreateFromBox(box);
    }

    private static bool BboxesOverlapXY(BoundingBox a, BoundingBox b)
    {
        return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
               a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y;
    }
}
