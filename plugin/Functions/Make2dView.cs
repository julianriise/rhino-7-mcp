using System;
using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Model-space Make2D via Rhino.Geometry.HiddenLineDrawing.
/// Offsets match forsk templates/sheets.json (millimetres). Each pack's
/// bounding-box minimum is moved onto that offset so views do not overlap.
/// </summary>
public partial class RhinoMCPFunctions
{
    private struct SheetViewSpec
    {
        public string View;
        public string Layer;
        public Vector3d Offset;
        public Vector3d Look;
        public Vector3d Up;
    }

    private static readonly Color SheetLayerColor = Color.FromArgb(40, 40, 40);

    private const string NothingToDrawMessage =
        "Nothing to draw. Bake walls, floor, or roof first.";

    private const string UnknownViewMessage =
        "Unknown view. Use plan, north, east, south, or west.";

    private static bool TryGetSheetView(string view, out SheetViewSpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(view)) return false;
        switch (view.Trim().ToLowerInvariant())
        {
            case "plan":
                spec = new SheetViewSpec
                {
                    View = "plan",
                    Layer = "S-PLAN",
                    Offset = new Vector3d(0, 0, 0),
                    Look = -Vector3d.ZAxis,
                    Up = Vector3d.YAxis
                };
                return true;
            case "north":
                spec = new SheetViewSpec
                {
                    View = "north",
                    Layer = "S-ELEV-N",
                    Offset = new Vector3d(0, -15000, 0),
                    Look = -Vector3d.YAxis,
                    Up = Vector3d.ZAxis
                };
                return true;
            case "east":
                spec = new SheetViewSpec
                {
                    View = "east",
                    Layer = "S-ELEV-E",
                    Offset = new Vector3d(15000, -15000, 0),
                    Look = -Vector3d.XAxis,
                    Up = Vector3d.ZAxis
                };
                return true;
            case "south":
                spec = new SheetViewSpec
                {
                    View = "south",
                    Layer = "S-ELEV-S",
                    Offset = new Vector3d(30000, -15000, 0),
                    Look = Vector3d.YAxis,
                    Up = Vector3d.ZAxis
                };
                return true;
            case "west":
                spec = new SheetViewSpec
                {
                    View = "west",
                    Layer = "S-ELEV-W",
                    Offset = new Vector3d(45000, -15000, 0),
                    Look = Vector3d.XAxis,
                    Up = Vector3d.ZAxis
                };
                return true;
            default:
                return false;
        }
    }

    [McpCommand("make2d_view")]
    public JObject Make2dView(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");

        var requested = parameters?["view"]?.ToString();
        if (!TryGetSheetView(requested, out var spec))
        {
            return SheetViewResult("", "", new JArray(), UnknownViewMessage);
        }

        var includeExisting = ReadBoolParam(parameters, "include_existing", true);
        var replace = ReadBoolParam(parameters, "replace", true);
        var sources = ResolveDrawSources(doc, parameters, includeExisting, out var hadIds);
        if (sources.Count == 0)
        {
            return SheetViewResult(
                spec.View,
                spec.Layer,
                new JArray(),
                hadIds ? "No objects found to draw." : NothingToDrawMessage);
        }

        var geometries = new List<GeometryBase>();
        foreach (var obj in sources)
            AppendDrawable(obj, Transform.Identity, geometries, 0);
        if (geometries.Count == 0)
        {
            return SheetViewResult(spec.View, spec.Layer, new JArray(), NothingToDrawMessage);
        }

        var visible = new List<Curve>();
        string fail = null;
        HiddenLineDrawing hld = null;
        try
        {
            var bbox = BoundingBox.Empty;
            foreach (var geom in geometries)
                bbox.Union(geom.GetBoundingBox(true));
            if (!bbox.IsValid)
            {
                fail = "Hidden line drawing failed.";
            }
            else
            {
                var tolerance = doc.ModelAbsoluteTolerance;
                if (tolerance <= 0) tolerance = 0.01;

                var hldParams = new HiddenLineDrawingParameters
                {
                    AbsoluteTolerance = tolerance,
                    Flatten = true,
                    IncludeHiddenCurves = false
                };
                var viewport = BuildParallelViewport(bbox, spec.Look, spec.Up);
                if (viewport == null || !viewport.IsValidCamera || !viewport.IsValidFrustum)
                {
                    fail = "Hidden line drawing failed.";
                }
                else
                {
                    hldParams.SetViewport(viewport);
                    foreach (var geom in geometries)
                        hldParams.AddGeometry(geom, Transform.Identity, null);
                    hld = HiddenLineDrawing.Compute(hldParams, true);
                    if (hld == null)
                        fail = "Hidden line drawing failed.";
                    else if (hld.Segments != null)
                    {
                        foreach (var seg in hld.Segments)
                        {
                            if (seg == null) continue;
                            if (seg.SegmentVisibility != HiddenLineDrawingSegment.Visibility.Visible)
                                continue;
                            var dup = seg.CurveGeometry?.DuplicateCurve();
                            if (dup != null) visible.Add(dup);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            fail = "Hidden line drawing failed.";
        }
        finally
        {
            hld?.Dispose();
            foreach (var geom in geometries)
                geom?.Dispose();
        }

        if (fail != null)
        {
            foreach (var curve in visible)
                curve?.Dispose();
            return SheetViewResult(spec.View, spec.Layer, new JArray(), fail);
        }

        var layer = EnsureLayer(doc, spec.Layer, SheetLayerColor);
        if (replace)
            DeleteViewDrawings(doc, spec.View, layer);

        PlaceCurvePack(visible, spec.Offset, doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.01);

        var ids = new JArray();
        var index = 1;
        foreach (var curve in visible)
        {
            if (curve == null) continue;
            if (!curve.IsValid)
            {
                curve.Dispose();
                continue;
            }
            var stableId = FormatStableId("d", index);
            var attr = new ObjectAttributes
            {
                LayerIndex = layer.Index,
                Name = stableId
            };
            StampForskTags(attr, new ForskStamp
            {
                Kind = "drawing",
                Level = "0",
                Id = stableId,
                View = spec.View
            });
            var id = doc.Objects.AddCurve(curve, attr);
            curve.Dispose();
            if (id == Guid.Empty) continue;
            ids.Add(id.ToString());
            index++;
        }

        if (ids.Count > 0 || replace)
            doc.Views.Redraw();

        var message = ids.Count == 0
            ? $"No visible curves for {spec.View}."
            : $"Drew {ids.Count} curve(s) on {layer.Name} ({spec.View}).";
        return SheetViewResult(spec.View, layer.Name, ids, message);
    }

    [McpCommand("sheet_pack")]
    public JObject SheetPack(JObject parameters)
    {
        var includeExisting = ReadBoolParam(parameters, "include_existing", true);
        var replace = ReadBoolParam(parameters, "replace", true);
        var views = new List<string>();
        if (parameters?["views"] is JArray requested)
        {
            foreach (var token in requested)
            {
                var text = token?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    views.Add(text.Trim());
            }
        }
        else
        {
            views.Add("plan");
            views.Add("north");
            views.Add("east");
            views.Add("south");
            views.Add("west");
        }

        var perView = new JArray();
        var total = 0;
        foreach (var view in views)
        {
            var one = Make2dView(new JObject
            {
                ["view"] = view,
                ["include_existing"] = includeExisting,
                ["replace"] = replace
            });
            perView.Add(one);
            total += one["count"]?.Value<int>() ?? 0;
        }

        return new JObject
        {
            ["views"] = perView,
            ["count"] = total,
            ["message"] = $"Drew {total} curve(s) across {views.Count} view(s)."
        };
    }

    [McpCommand("clear_drawings")]
    public JObject ClearDrawings(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");

        HashSet<string> viewSet = null;
        if (parameters?["views"] is JArray requested && requested.Count > 0)
        {
            viewSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in requested)
            {
                var text = token?.ToString();
                if (!TryGetSheetView(text, out var spec))
                    throw new InvalidOperationException(UnknownViewMessage);
                viewSet.Add(spec.View);
            }
        }

        var dryRun = ReadBoolParam(parameters, "dry_run", false);
        var matched = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj == null) continue;
            if (!string.Equals(GetForskKind(obj), "drawing", StringComparison.OrdinalIgnoreCase))
                continue;
            if (viewSet != null)
            {
                var objView = obj.Attributes.GetUserString("forsk:view") ?? "";
                if (!viewSet.Contains(objView)) continue;
            }
            matched.Add(obj.Id);
        }

        var deleted = new JArray();
        if (dryRun)
        {
            foreach (var id in matched)
                deleted.Add(id.ToString());
        }
        else
        {
            foreach (var id in matched)
            {
                if (doc.Objects.Delete(id, true))
                    deleted.Add(id.ToString());
            }
            if (deleted.Count > 0)
                doc.Views.Redraw();
        }

        return new JObject
        {
            ["deleted"] = deleted,
            ["count"] = deleted.Count,
            ["dry_run"] = dryRun
        };
    }

    private static JObject SheetViewResult(string view, string layer, JArray ids, string message)
    {
        return new JObject
        {
            ["count"] = ids?.Count ?? 0,
            ["ids"] = ids ?? new JArray(),
            ["layer"] = layer ?? "",
            ["view"] = view ?? "",
            ["message"] = message ?? ""
        };
    }

    private static bool ReadBoolParam(JObject parameters, string key, bool fallback)
    {
        var token = parameters?[key];
        if (token == null || token.Type == JTokenType.Null) return fallback;
        return token.ToObject<bool?>() ?? fallback;
    }

    private static List<RhinoObject> ResolveDrawSources(
        RhinoDoc doc, JObject parameters, bool includeExisting, out bool hadIds)
    {
        hadIds = false;
        var result = new List<RhinoObject>();
        var seen = new HashSet<Guid>();
        if (parameters?["ids"] is JArray ids && ids.Count > 0)
        {
            hadIds = true;
            foreach (var token in ids)
            {
                var text = token?.ToString();
                if (string.IsNullOrWhiteSpace(text) || !Guid.TryParse(text, out var guid))
                    continue;
                if (!seen.Add(guid)) continue;
                var obj = doc.Objects.Find(guid);
                if (obj != null) result.Add(obj);
            }
            return result;
        }

        foreach (var obj in doc.Objects)
        {
            if (obj == null || !seen.Add(obj.Id)) continue;
            if (!IsSheetSource(doc, obj, includeExisting)) continue;
            result.Add(obj);
        }
        return result;
    }

    /// <summary>
    /// Generated wall, floor, roof, and opening solids. Skips markers, rooms, and drawings.
    /// Existing underlay is included only when includeExisting is true.
    /// </summary>
    private static bool IsSheetSource(RhinoDoc doc, RhinoObject obj, bool includeExisting)
    {
        if (obj?.Attributes == null) return false;
        if (string.Equals(GetForskKind(obj), "drawing", StringComparison.OrdinalIgnoreCase))
            return false;
        if (includeExisting && IsExistingUnderlay(doc, obj))
            return true;
        if (!IsForskGenerated(obj)) return false;
        var kind = GetForskKind(obj);
        if (string.IsNullOrEmpty(kind)) return false;
        return kind.Equals("wall", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("floor", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("roof", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("opening", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendDrawable(RhinoObject obj, Transform xform, List<GeometryBase> sink, int depth)
    {
        if (obj == null || depth > 6) return;
        if (obj is InstanceObject iref)
        {
            var idef = iref.InstanceDefinition;
            if (idef == null) return;
            var next = xform * iref.InstanceXform;
            var members = idef.GetObjects();
            if (members == null) return;
            foreach (var member in members)
                AppendDrawable(member, next, sink, depth + 1);
            return;
        }

        var geom = obj.Geometry?.Duplicate();
        if (geom == null) return;
        if (!xform.IsIdentity && !geom.Transform(xform))
        {
            geom.Dispose();
            return;
        }
        if (geom is Curve || geom is Brep || geom is Mesh)
        {
            sink.Add(geom);
            return;
        }
        if (geom is Surface surface)
        {
            var brep = surface.ToBrep();
            geom.Dispose();
            if (brep != null) sink.Add(brep);
            return;
        }
        geom.Dispose();
    }

    private static ViewportInfo BuildParallelViewport(BoundingBox bbox, Vector3d look, Vector3d upHint)
    {
        var lookU = look;
        if (!lookU.Unitize()) return null;
        var up = upHint;
        up -= lookU * (up * lookU);
        if (!up.Unitize()) return null;

        var center = bbox.Center;
        var radius = bbox.Diagonal.Length * 0.5;
        if (radius < 1.0) radius = 1000.0;
        var distance = Math.Max(radius * 4.0, 1000.0);

        var vp = new ViewportInfo();
        vp.SetCameraLocation(center - lookU * distance);
        vp.SetCameraDirection(lookU);
        vp.SetCameraUp(up);
        vp.SetScreenPort(0, 1000, 0, 1000, 0, 1000);
        SetWideFrustum(vp, radius, distance);
        vp.ChangeToParallelProjection(true);
        vp.DollyExtents(bbox, 1.1);
        if (!vp.IsValidFrustum)
            SetWideFrustum(vp, radius, distance);
        return vp;
    }

    /// <summary>
    /// Visible segments stay. A section cut is the wall line at the plan
    /// plane, including when its visibility is not Visible.
    /// </summary>
    private static bool KeepGreyscaleSegment(HiddenLineDrawingSegment seg)
    {
        if (seg == null) return false;
        if (seg.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Visible)
            return true;
        return IsSectionCut(seg);
    }

    private static bool IsSectionCut(HiddenLineDrawingSegment seg)
    {
        var parent = seg?.ParentCurve;
        if (parent == null) return false;
        return parent.SilhouetteType == SilhouetteType.SectionCut;
    }

    /// <summary>
    /// Keep the half opposite the HLD normal (the side a document clipping
    /// plane would show). Fully removed solids are dropped. A solid that
    /// crosses the plane is trimmed. Brep.Trim keeps the half opposite the
    /// normal, which is the side a document clipping plane would show.
    /// Openings the plane passes through stay gaps in that cut.
    /// </summary>
    private static List<GeometryBase> KeepSectionSide(GeometryBase geom, Plane hldPlane, double tolerance)
    {
        var kept = new List<GeometryBase>();
        if (geom == null) return kept;
        var tol = tolerance > 0 ? tolerance : 0.01;

        Brep brep = geom as Brep;
        Brep owned = null;
        if (brep == null && geom is Extrusion extrusion)
            owned = brep = extrusion.ToBrep();
        else if (brep == null && geom is Surface surface)
            owned = brep = surface.ToBrep();

        if (brep == null)
        {
            if (SectionSide(geom.GetBoundingBox(true), hldPlane, tol) > 0)
            {
                var dup = geom.Duplicate();
                if (dup != null) kept.Add(dup);
            }
            return kept;
        }

        try
        {
            var side = SectionSide(brep.GetBoundingBox(true), hldPlane, tol);
            if (side < 0) return kept;
            if (side > 0)
            {
                var dup = brep.Duplicate();
                if (dup != null) kept.Add(dup);
                return kept;
            }

            Brep[] pieces = null;
            try { pieces = brep.Trim(hldPlane, tol); }
            catch (Exception) { pieces = null; }

            if (pieces != null && pieces.Length > 0)
            {
                foreach (var piece in pieces)
                {
                    if (piece == null) continue;
                    // Trim keeps the half opposite the normal. Drop a piece
                    // whose center is still on the removed side.
                    if (SectionCenterDot(piece.GetBoundingBox(true), hldPlane) <= tol)
                        kept.Add(piece);
                    else
                        piece.Dispose();
                }
                if (kept.Count > 0) return kept;
            }

            // Trim produced nothing. The intersection curves are still the
            // wall section. Do not put the uncut solid back in: that draws
            // the floor outline and the roof.
            Curve[] contours = null;
            try { contours = Brep.CreateContourCurves(brep, hldPlane); }
            catch (Exception) { contours = null; }
            if (contours == null) return kept;
            foreach (var curve in contours)
            {
                if (curve != null) kept.Add(curve);
            }
            return kept;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// +1 kept, -1 removed, 0 crosses. The HLD normal points at the removed side.
    /// </summary>
    private static int SectionSide(BoundingBox box, Plane hldPlane, double tol)
    {
        if (!box.IsValid) return -1;
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (var corner in box.GetCorners())
        {
            var dot = (corner - hldPlane.Origin) * hldPlane.Normal;
            if (dot < min) min = dot;
            if (dot > max) max = dot;
        }
        if (max <= tol) return 1;
        if (min >= -tol) return -1;
        return 0;
    }

    private static double SectionCenterDot(BoundingBox box, Plane hldPlane)
    {
        if (!box.IsValid) return double.MaxValue;
        return (box.Center - hldPlane.Origin) * hldPlane.Normal;
    }

    private static void SetWideFrustum(ViewportInfo vp, double radius, double distance)
    {
        vp.SetFrustum(
            -radius * 2.0,
            radius * 2.0,
            -radius * 2.0,
            radius * 2.0,
            Math.Max(distance * 0.05, 0.1),
            distance + radius * 6.0);
    }

    private void DeleteViewDrawings(RhinoDoc doc, string view, Layer layer)
    {
        var doomed = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj == null) continue;
            if (!string.Equals(GetForskKind(obj), "drawing", StringComparison.OrdinalIgnoreCase))
                continue;
            var objView = obj.Attributes.GetUserString("forsk:view");
            var sameView = string.Equals(objView, view, StringComparison.OrdinalIgnoreCase);
            var onLayer = layer != null && ObjectOnLayer(doc, obj, layer);
            if (sameView || onLayer)
                doomed.Add(obj.Id);
        }
        foreach (var id in doomed)
            doc.Objects.Delete(id, true);
    }

    private static void PlaceCurvePack(List<Curve> curves, Vector3d offset, double tolerance)
    {
        if (curves == null || curves.Count == 0) return;
        var bbox = BoundingBox.Empty;
        foreach (var curve in curves)
        {
            if (curve == null) continue;
            bbox.Union(curve.GetBoundingBox(true));
        }
        if (!bbox.IsValid) return;
        var delta = new Vector3d(
            offset.X - bbox.Min.X,
            offset.Y - bbox.Min.Y,
            offset.Z - bbox.Min.Z);
        if (delta.Length <= tolerance) return;
        var move = Transform.Translation(delta);
        foreach (var curve in curves)
            curve?.Transform(move);
    }

    /// <summary>
    /// Print linework. Rhino 7 Mac has no ClippingDrawings, so this is
    /// HiddenLineDrawing.Compute. Plan passes the horizontal cut. Curves are
    /// black hairlines on S-DRAW children, hidden in the model viewport.
    /// </summary>
    private struct GreyscaleDrawing
    {
        public string View;
        public string Layer;
        public int Count;
        public BoundingBox Box;
        public string Error;
    }

    private struct WeightedCurve
    {
        public Curve Curve;
        public double Weight;
    }

    private const string DrawParentName = "S-DRAW";
    private const double DrawHairlineMm = 0.18;
    private const double DrawSilhouetteMm = 0.35;

    private static string DrawChildName(string view)
    {
        if (string.IsNullOrEmpty(view)) return null;
        switch (view.Trim().ToLowerInvariant())
        {
            case "plan": return "Plan";
            case "north": return "North";
            case "east": return "East";
            case "south": return "South";
            case "west": return "West";
            default: return null;
        }
    }

    private GreyscaleDrawing BakeGreyscaleDrawing(
        RhinoDoc doc, string view, bool includeExisting, Plane? clip)
    {
        var result = new GreyscaleDrawing
        {
            View = view ?? "",
            Layer = "",
            Count = 0,
            Box = BoundingBox.Empty,
            Error = null
        };
        if (doc == null || !TryGetSheetView(view, out var spec))
        {
            result.Error = UnknownViewMessage;
            return result;
        }

        var child = DrawChildName(spec.View);
        var sources = ResolveDrawSources(doc, new JObject(), includeExisting, out _);
        if (sources.Count == 0)
        {
            result.Error = NothingToDrawMessage;
            return result;
        }

        var geometries = new List<GeometryBase>();
        foreach (var obj in sources)
            AppendDrawable(obj, Transform.Identity, geometries, 0);
        if (geometries.Count == 0)
        {
            result.Error = NothingToDrawMessage;
            return result;
        }

        var visible = new List<WeightedCurve>();
        string fail = null;
        HiddenLineDrawing hld = null;
        List<GeometryBase> sectioned = null;
        try
        {
            var tolerance = doc.ModelAbsoluteTolerance;
            if (tolerance <= 0) tolerance = 0.01;
            var draw = geometries;
            if (clip.HasValue)
            {
                // Document clipping keeps the side the normal points at.
                // HLD keeps the opposite side, so flip before the cut
                // (https://discourse.mcneel.com/t/hiddenlinedrawing-clipping-planes-are-ignored-in-make2d-calculation-c/215626).
                // Rhino 7.34 net48 has AddGeometry and AddClippingPlane only.
                // AddGeometryAndPlanes(geom, xform, tag, occluding, planeList) is not
                // on this RhinoCommon. AddClippingPlane alone drops solids that cross
                // the plane and the plan is the floor outline. Trim each solid on
                // the flipped plane and draw the kept half. The cut edge is the section.
                var hldPlane = clip.Value;
                hldPlane.Flip();
                sectioned = new List<GeometryBase>();
                foreach (var geom in geometries)
                {
                    foreach (var piece in KeepSectionSide(geom, hldPlane, tolerance))
                        sectioned.Add(piece);
                }
                draw = sectioned;
            }

            var bbox = BoundingBox.Empty;
            foreach (var geom in draw)
                bbox.Union(geom.GetBoundingBox(true));
            if (!bbox.IsValid)
            {
                fail = "Hidden line drawing failed.";
            }
            else
            {
                var hldParams = new HiddenLineDrawingParameters
                {
                    AbsoluteTolerance = tolerance,
                    Flatten = true,
                    IncludeHiddenCurves = false,
                    IncludeTangentEdges = false,
                    IncludeTangentSeams = false
                };
                var viewport = BuildParallelViewport(bbox, spec.Look, spec.Up);
                if (viewport == null || !viewport.IsValidCamera || !viewport.IsValidFrustum)
                {
                    fail = "Hidden line drawing failed.";
                }
                else
                {
                    hldParams.SetViewport(viewport);
                    foreach (var geom in draw)
                        hldParams.AddGeometry(geom, Transform.Identity, null);
                    hld = HiddenLineDrawing.Compute(hldParams, true);
                    if (hld == null)
                        fail = "Hidden line drawing failed.";
                    else if (hld.Segments != null)
                    {
                        foreach (var seg in hld.Segments)
                        {
                            if (!KeepGreyscaleSegment(seg)) continue;
                            var dup = seg.CurveGeometry?.DuplicateCurve();
                            if (dup == null) continue;
                            var section = IsSectionCut(seg);
                            visible.Add(new WeightedCurve
                            {
                                Curve = dup,
                                Weight = seg.IsSceneSilhouette || section
                                    ? DrawSilhouetteMm
                                    : DrawHairlineMm
                            });
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            fail = "Hidden line drawing failed.";
        }
        finally
        {
            hld?.Dispose();
            foreach (var geom in geometries)
                geom?.Dispose();
            if (sectioned != null)
            {
                foreach (var geom in sectioned)
                    geom?.Dispose();
            }
        }

        if (fail != null || visible.Count == 0)
        {
            foreach (var item in visible)
                item.Curve?.Dispose();
            result.Error = fail ?? ("No visible curves for " + spec.View + ".");
            return result;
        }

        var layer = EnsureDrawLayer(doc, child);
        if (layer == null)
        {
            foreach (var item in visible)
                item.Curve?.Dispose();
            result.Error = "Hidden line drawing failed.";
            return result;
        }

        DeletePrintDrawings(doc, layer);
        var curves = new List<Curve>();
        foreach (var item in visible)
            if (item.Curve != null) curves.Add(item.Curve);
        PlaceCurvePack(curves, spec.Offset, doc.ModelAbsoluteTolerance > 0 ? doc.ModelAbsoluteTolerance : 0.01);

        var box = BoundingBox.Empty;
        var count = 0;
        var index = 1;
        foreach (var item in visible)
        {
            var curve = item.Curve;
            if (curve == null) continue;
            if (!curve.IsValid)
            {
                curve.Dispose();
                continue;
            }
            box.Union(curve.GetBoundingBox(true));
            var stableId = FormatStableId("d", index);
            var attr = new ObjectAttributes
            {
                LayerIndex = layer.Index,
                Name = stableId,
                ColorSource = ObjectColorSource.ColorFromObject,
                ObjectColor = Color.Black,
                PlotColorSource = ObjectPlotColorSource.PlotColorFromObject,
                PlotColor = Color.Black,
                PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject,
                PlotWeight = item.Weight < 0 ? DrawHairlineMm : item.Weight
            };
            StampForskTags(attr, new ForskStamp
            {
                Kind = "drawing",
                Level = "0",
                Id = stableId,
                View = spec.View
            });
            attr.SetUserString("forsk:role", "greyscale");
            var id = doc.Objects.AddCurve(curve, attr);
            curve.Dispose();
            if (id == Guid.Empty) continue;
            count++;
            index++;
        }

        result.View = spec.View;
        result.Layer = layer.FullPath ?? layer.Name;
        result.Count = count;
        result.Box = box;
        if (count == 0)
            result.Error = "No visible curves for " + spec.View + ".";
        return result;
    }

    private Layer EnsureDrawLayer(RhinoDoc doc, string childName)
    {
        if (doc == null || string.IsNullOrEmpty(childName)) return null;
        var parent = FindLayerCaseInsensitive(doc, DrawParentName);
        if (parent == null || parent.IsDeleted)
        {
            var created = new Layer
            {
                Name = DrawParentName,
                Color = Color.Black,
                IsVisible = true
            };
            var parentIndex = doc.Layers.Add(created);
            if (parentIndex < 0) return null;
            parent = doc.Layers.FindIndex(parentIndex);
        }
        if (parent == null) return null;
        StyleDrawLayer(doc, parent, DrawHairlineMm);

        Layer child = null;
        var kids = parent.GetChildren();
        if (kids != null)
        {
            foreach (var kid in kids)
            {
                if (kid == null || kid.IsDeleted) continue;
                if (!kid.Name.Equals(childName, StringComparison.OrdinalIgnoreCase)) continue;
                child = kid;
                break;
            }
        }
        if (child == null)
        {
            var created = new Layer
            {
                Name = childName,
                Color = Color.Black,
                IsVisible = true,
                ParentLayerId = parent.Id
            };
            var childIndex = doc.Layers.Add(created);
            if (childIndex < 0) return null;
            child = doc.Layers.FindIndex(childIndex);
        }
        if (child == null) return null;
        StyleDrawLayer(doc, child, DrawHairlineMm);
        HideDrawLayerInModel(doc, parent);
        HideDrawLayerInModel(doc, child);
        return child;
    }

    private static void StyleDrawLayer(RhinoDoc doc, Layer layer, double plotMm)
    {
        if (doc == null || layer == null) return;
        layer.Color = Color.Black;
        layer.PlotColor = Color.Black;
        if (plotMm < 0) plotMm = DrawHairlineMm;
        layer.PlotWeight = plotMm;
        // Global on, then model viewports off. A globally off layer stays off
        // in a detail even when that detail asks for it.
        layer.IsVisible = true;
        doc.Layers.Modify(layer, layer.Index, true);
    }

    private static void HideDrawLayerInModel(RhinoDoc doc, Layer layer)
    {
        if (doc == null || layer == null) return;
        var views = doc.Views.GetViewList(true, false);
        if (views == null) return;
        foreach (var view in views)
        {
            var id = view?.MainViewport?.Id ?? Guid.Empty;
            if (id == Guid.Empty) continue;
            layer.SetPerViewportVisible(id, false);
        }
        doc.Layers.Modify(layer, layer.Index, true);
    }

    private static void DeletePrintDrawings(RhinoDoc doc, Layer layer)
    {
        if (doc == null || layer == null) return;
        var doomed = new List<Guid>();
        foreach (var obj in doc.Objects)
        {
            if (obj == null) continue;
            if (!string.Equals(GetForskKind(obj), "drawing", StringComparison.OrdinalIgnoreCase))
                continue;
            if (obj.Attributes.LayerIndex != layer.Index) continue;
            doomed.Add(obj.Id);
        }
        foreach (var id in doomed)
            doc.Objects.Delete(id, true);
    }

    private static Layer FindDrawLayer(RhinoDoc doc, string view)
    {
        var child = DrawChildName(view);
        if (doc == null || child == null) return null;
        var want = DrawParentName + "::" + child;
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer == null || layer.IsDeleted) continue;
            if ((layer.FullPath ?? "").Equals(want, StringComparison.OrdinalIgnoreCase))
                return layer;
        }
        return null;
    }

    private static int CountPrintDrawings(RhinoDoc doc, string view)
    {
        if (doc == null) return 0;
        var count = 0;
        foreach (var obj in doc.Objects)
        {
            if (!IsPrintDrawing(doc, obj)) continue;
            if (!string.IsNullOrEmpty(view))
            {
                var objView = obj.Attributes.GetUserString("forsk:view") ?? "";
                if (!objView.Equals(view, StringComparison.OrdinalIgnoreCase)) continue;
            }
            count++;
        }
        return count;
    }

    private static BoundingBox PrintDrawingBounds(RhinoDoc doc, string view)
    {
        var box = BoundingBox.Empty;
        if (doc == null) return box;
        foreach (var obj in doc.Objects)
        {
            if (!IsPrintDrawing(doc, obj)) continue;
            if (!string.IsNullOrEmpty(view))
            {
                var objView = obj.Attributes.GetUserString("forsk:view") ?? "";
                if (!objView.Equals(view, StringComparison.OrdinalIgnoreCase)) continue;
            }
            var geom = obj.Geometry;
            if (geom == null) continue;
            var one = geom.GetBoundingBox(true);
            if (one.IsValid) box.Union(one);
        }
        return box;
    }

    private static bool IsPrintDrawing(RhinoDoc doc, RhinoObject obj)
    {
        if (doc == null || obj?.Attributes == null) return false;
        if (!string.Equals(GetForskKind(obj), "drawing", StringComparison.OrdinalIgnoreCase))
            return false;
        var index = obj.Attributes.LayerIndex;
        if (index < 0 || index >= doc.Layers.Count) return false;
        return IsUnderDrawLayer(doc.Layers[index]);
    }

    private static bool IsUnderDrawLayer(Layer layer)
    {
        if (layer == null || layer.IsDeleted) return false;
        var path = layer.FullPath ?? "";
        if (path.Equals(DrawParentName, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(DrawParentName + "::", StringComparison.OrdinalIgnoreCase);
    }

    private static string GreyscaleMake2dNote(RhinoDoc doc)
    {
        var parts = new List<string>();
        foreach (var view in new[] { "plan", "north", "east", "south", "west" })
        {
            var count = CountPrintDrawings(doc, view);
            if (count <= 0) continue;
            parts.Add(view + "=" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (parts.Count == 0) return "greyscale make2d";
        return "greyscale make2d " + string.Join(" ", parts.ToArray());
    }
}
