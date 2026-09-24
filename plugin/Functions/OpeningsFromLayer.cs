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
/// Cut door/window openings through wall solids, leave a selectable
/// opening_marker on A-OPEN, and place a simple frame on A-OPEN::Block.
/// Source DXF geometry is never modified.
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
        double defaultSill = ForskDefaults.DoorSill;
        double defaultHead = ForskDefaults.DoorHead;
        if (layerKey.Equals("window", StringComparison.OrdinalIgnoreCase))
        {
            defaultSill = ForskDefaults.WindowSill;
            defaultHead = ForskDefaults.WindowHead;
        }

        var sill = parameters["sill"]?.ToObject<double?>() ?? defaultSill;
        var head = parameters["head"]?.ToObject<double?>() ?? defaultHead;
        if (head <= sill)
            throw new ArgumentException("head must be greater than sill");

        if (IsExistingLayerName(layerName))
            return ExistingOpeningsRefusal(sill, head);

        if (IsRoofOrCeilingLayerName(layerName))
        {
            return new JObject
            {
                ["cut_count"] = 0,
                ["failed_count"] = 0,
                ["failures"] = new JArray(),
                ["wall_ids"] = new JArray(),
                ["opening_count"] = 0,
                ["marker_ids"] = new JArray(),
                ["sill"] = sill,
                ["head"] = head,
                ["message"] = $"Layer '{layerName}' is roof/ceiling/slab - ignored in 2D to 3D."
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
                ["marker_ids"] = new JArray(),
                ["message"] = $"No wall solids on '{targetLayerName}' to cut."
            };
        }

        var footprints = CollectOpeningFootprints(doc, sourceLayer, tol);
        if (limit.HasValue && limit.Value > 0 && footprints.Count > limit.Value)
            footprints = footprints.Take(limit.Value).ToList();

        var openingKind = ResolveOpeningKind(layerKey);
        var markerPrefix = openingKind == "window" ? "window-" : "door-";
        var markerIndex = NextNameIndex(doc, markerPrefix);
        var openLayer = EnsureLayer(doc, "A-OPEN", Color.FromArgb(120, 160, 200));

        var failures = new JArray();
        var markerIds = new JArray();
        var blockIds = new JArray();
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
                var hostId = Guid.Empty;
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

                    if (valid.Count == 1 && TryReplaceWallBrep(doc, wall, valid[0]))
                    {
                        walls[i] = wall;
                        if (hostId == Guid.Empty) hostId = wall.Id;
                        hit = true;
                        continue;
                    }

                    var oldId = wall.Id;
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
                    {
                        RetargetOpeningMarkers(doc, oldId, first.Id);
                        walls[i] = first;
                        if (hostId == Guid.Empty) hostId = first.Id;
                    }

                    hit = true;
                }

                if (splitPieces.Count > 0)
                    walls.AddRange(splitPieces);

                if (hit)
                {
                    cutCount++;
                    if (hostId != Guid.Empty)
                    {
                        var markerId = AddOpeningMarker(
                            doc,
                            openLayer,
                            foot,
                            hostId,
                            openingKind,
                            markerPrefix,
                            markerIndex,
                            sill,
                            head,
                            sourceLayer.Name);
                        if (markerId != Guid.Empty)
                        {
                            markerIds.Add(markerId.ToString());
                            Brep hostBrep = null;
                            WallSolid hostSolid = null;
                            for (var w = 0; w < walls.Count; w++)
                            {
                                if (walls[w].Id != hostId) continue;
                                hostBrep = walls[w].Brep;
                                hostSolid = walls[w];
                                break;
                            }
                            var blockId = AddOpeningBlock(
                                doc,
                                markerId,
                                $"{markerPrefix}{markerIndex:D2}",
                                hostId,
                                openingKind,
                                foot,
                                sill,
                                head,
                                LongerXySide(foot.Bbox),
                                pad,
                                sourceLayer.Name,
                                hostBrep,
                                null);
                            if (blockId != Guid.Empty)
                                blockIds.Add(blockId.ToString());
                            if (hostSolid != null)
                            {
                                StampOpeningOnHost(
                                    doc,
                                    hostSolid,
                                    markerId,
                                    blockId,
                                    foot.Bbox.Center,
                                    null,
                                    tol);
                            }
                            markerIndex++;
                        }
                    }
                }
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
            ["marker_ids"] = markerIds,
            ["block_ids"] = blockIds,
            ["sill"] = sill,
            ["head"] = head,
            ["message"] = $"Cut {cutCount} opening(s) from layer '{sourceLayer.Name}' " +
                          $"({failures.Count} failure(s), {markerIds.Count} marker(s), {blockIds.Count} block(s))."
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

    private static string ResolveOpeningKind(string layerKey)
    {
        if (layerKey.Equals("window", StringComparison.OrdinalIgnoreCase))
            return "window";
        if (layerKey.Equals("door", StringComparison.OrdinalIgnoreCase))
            return "door";
        return layerKey.ToLowerInvariant();
    }

    private static bool TryReplaceWallBrep(RhinoDoc doc, WallSolid wall, Brep brep)
    {
        try
        {
            if (!doc.Objects.Replace(wall.Id, brep))
                return false;
            wall.Brep = brep.DuplicateBrep() ?? brep;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RetargetOpeningMarkers(RhinoDoc doc, Guid oldHost, Guid newHost)
    {
        if (oldHost == Guid.Empty || newHost == Guid.Empty || oldHost == newHost)
            return;
        var oldStr = oldHost.ToString();
        var newStr = newHost.ToString();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            var kind = GetForskKind(obj);
            if (!string.Equals(kind, "opening_marker", StringComparison.Ordinal)
                && !string.Equals(kind, "opening", StringComparison.OrdinalIgnoreCase))
                continue;
            if (obj.Attributes.GetUserString("forsk:host") != oldStr)
                continue;
            obj.Attributes.SetUserString("forsk:host", newStr);
            var stable = ReadForskUserString(doc, newHost, "forsk:id");
            if (!string.IsNullOrEmpty(stable))
                obj.Attributes.SetUserString("forsk:host_id", stable);
            obj.CommitChanges();
        }
    }

    private Guid AddOpeningMarker(
        RhinoDoc doc,
        Layer openLayer,
        OpeningFootprint foot,
        Guid hostId,
        string openingKind,
        string namePrefix,
        int index,
        double sill,
        double head,
        string sourceLayerName)
    {
        var width = LongerXySide(foot.Bbox);
        var markerBrep = BuildOpeningMarkerBox(foot, sill, head, 50.0);
        if (markerBrep == null || !markerBrep.IsValid)
            return Guid.Empty;

        var attr = new ObjectAttributes
        {
            Name = $"{namePrefix}{index:D2}",
            LayerIndex = openLayer.Index,
            MaterialSource = ObjectMaterialSource.MaterialFromLayer
        };
        StampForskTags(attr, new ForskStamp
        {
            Kind = "opening_marker",
            Level = "0",
            Host = hostId.ToString(),
            HostId = ReadForskUserString(doc, hostId, "forsk:id"),
            OpeningKind = openingKind,
            Sill = sill,
            Head = head,
            Width = width,
            SourceLayer = sourceLayerName
        });
        return doc.Objects.AddBrep(markerBrep, attr);
    }

    private static double LongerXySide(BoundingBox bbox)
    {
        if (!bbox.IsValid) return 0;
        var dx = bbox.Max.X - bbox.Min.X;
        var dy = bbox.Max.Y - bbox.Min.Y;
        return Math.Max(dx, dy);
    }

    private static Brep BuildOpeningMarkerBox(
        OpeningFootprint foot,
        double sill,
        double head,
        double minSelectDepth)
    {
        var bbox = foot.Bbox;
        if (!bbox.IsValid) return null;

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
            if (dy < minSelectDepth)
            {
                minY = cy - minSelectDepth * 0.5;
                maxY = cy + minSelectDepth * 0.5;
            }
        }
        else
        {
            if (dx < minSelectDepth)
            {
                minX = cx - minSelectDepth * 0.5;
                maxX = cx + minSelectDepth * 0.5;
            }
        }

        if (maxX - minX < 1e-9 || maxY - minY < 1e-9 || head - sill < 1e-9)
            return null;

        var box = new Box(
            Plane.WorldXY,
            new Interval(minX, maxX),
            new Interval(minY, maxY),
            new Interval(sill, head));
        return Brep.CreateFromBox(box);
    }

    private static int NextNameIndex(RhinoDoc doc, string prefix)
    {
        var max = 0;
        foreach (var obj in doc.Objects)
        {
            var name = obj?.Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = name.Substring(prefix.Length);
            if (int.TryParse(suffix, out var n) && n > max)
                max = n;
        }
        return max + 1;
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
