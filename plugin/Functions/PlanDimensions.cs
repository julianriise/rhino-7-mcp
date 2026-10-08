using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// F5.2 on the plan sheet. PlanDims lays the dimensions out from the model
/// (the outer wall faces from each wall's path, the opening records, the
/// tagged rooms) around everything already drawn; this bakes them on the plan
/// layer like the symbols: strokes as ribbons, values as 1:1 text. Every
/// piece is stamped with its chain, and each value with what it measures in
/// model coordinates, so the smoke reads them back against the model.
/// </summary>
public partial class RhinoMCPFunctions
{
    // A dimension tick is drawn at the profile's silhouette tier: heavier than the thin line, as drafters draw them.

    /// <summary>Paper diameter of the dot a room tag's leader starts from, inside the room.</summary>
    private const double LeaderDotMm = 0.8;

    /// <summary>
    /// The dimensions around what is drawn, from the openings the symbols drew
    /// (their frames, queued with their marks), the outer wall faces and the
    /// tagged rooms.
    /// </summary>
    private int BakeDimensions(
        RhinoDoc doc, Layer layer, int scale, Transform worldToHld, Vector3d delta,
        List<PendingMark> openings, List<PlanRoom> rooms, List<List<List<RoomDetect.Pt>>> poche, int pattern, double tol,
        ref BoundingBox box, ref int index, ref int count, ref PlanStats stats)
    {
        var scene = new PlanDims.Scene
        {
            Scale = scale,
            Walls = poche ?? new List<List<List<RoomDetect.Pt>>>(),
            Taken = PlanObstacles(doc, layer),
            Outlines = PlanOutlines(doc, worldToHld, delta, tol)
        };
        foreach (var item in openings ?? new List<PendingMark>())
        {
            var at = MapPlan(0, 0, item.Plane, worldToHld, delta);
            var along = MapPlan(1, 0, item.Plane, worldToHld, delta) - at;
            along.Z = 0;
            if (!along.Unitize()) continue;
            scene.Openings.Add(new PlanDims.Opening
            {
                Id = item.MarkerId,
                Centre = new RoomDetect.Pt(at.X, at.Y),
                Along = new RoomDetect.Pt(along.X, along.Y),
                HalfThick = item.HalfThick
            });
        }
        foreach (var room in rooms ?? new List<PlanRoom>())
        {
            if (!room.Tagged) continue;
            scene.Rooms.Add(new PlanDims.Room
            {
                Id = room.ScheduleId,
                Ring = room.Ring.Select(p => ToDrawing(p, worldToHld, delta)).Select(p => new RoomDetect.Pt(p.X, p.Y)).ToList()
            });
        }
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        scene.Measure = text =>
        {
            if (!widths.TryGetValue(text, out var paper))
            {
                paper = ModelTextWidth(doc, text, PlanDims.TextMm * scale) / scale;
                widths[text] = paper;
            }
            return paper;
        };

        var result = PlanDims.Layout(scene);
        var toModel = DrawingToModel(worldToHld, delta);
        var added = 0;
        foreach (var chain in result.Chains)
        {
            if (!chain.Placed)
            {
                stats.DimsSkipped++;
                continue;
            }
            var stamps = new Dictionary<string, string>
            {
                ["forsk:dim_chain"] = chain.Id,
                ["forsk:dim_kind"] = chain.Kind,
                ["forsk:dim_side"] = chain.Side ?? ""
            };
            var stamp = new SymbolStamp { Extra = stamps };
            for (var i = 0; i < chain.Lines.Count; i++)
            {
                using (var curve = new LineCurve(DrawingPoint(chain.Lines[i].A), DrawingPoint(chain.Lines[i].B)))
                    added += AddStroke(doc, layer, curve, PenThin, scale, false, pattern, tol,
                        "dimension", i == 0 ? "line" : "witness", null, null, ref box, ref index, ref count, stamp);
            }
            foreach (var tick in chain.Ticks)
            {
                using (var curve = new LineCurve(DrawingPoint(tick.A), DrawingPoint(tick.B)))
                    added += AddStroke(doc, layer, curve, PenSilhouette, scale, false, pattern, tol,
                        "dimension", "tick", null, null, ref box, ref index, ref count, stamp);
            }
            foreach (var label in chain.Texts)
            {
                var values = new Dictionary<string, string>(stamps)
                {
                    ["forsk:dim_value"] = label.Value.ToString(CultureInfo.InvariantCulture),
                    ["forsk:dim_total"] = chain.Total.ToString(CultureInfo.InvariantCulture),
                    ["forsk:dim_from"] = label.FromId ?? "",
                    ["forsk:dim_to"] = label.ToId ?? ""
                };
                if (toModel != null)
                    values["forsk:dim_span"] = FaceStamp(toModel(label.From), toModel(label.To));
                if (!AddDimensionText(doc, layer, label, scale, values, ref box, ref index, ref count)) continue;
                added++;
                stats.DimTexts++;
            }
            stats.Dims++;
            if (chain.Kind == "room") stats.DimsRoom++;
            else stats.DimsExterior++;
        }
        stats.DimsCollisions += result.Collisions;
        stats.DimOpenings = result.Openings;
        stats.DimOpeningsShown = result.OpeningsShown;
        return added;
    }

    /// <summary>
    /// Chains fixed by the model (a detail's, a facade's or a section's
    /// heights) laid out by PlanDims.LayoutFixed around taken, and baked:
    /// thin dimension lines, ticks at tickPen, values as 1:1 text, a line
    /// under a value that is not to scale. Every piece carries stamp plus its
    /// chain and kind. What it placed joins taken. Returns every chain as
    /// laid out; one that found no room is not Placed and not drawn.
    /// </summary>
    private List<PlanDims.Chain> BakeFixedChains(
        RhinoDoc doc, Layer layer, IEnumerable<PlanDims.FixedChain> chains, int scale, PrintPen tickPen,
        List<PlanDims.Obstacle> taken, List<List<List<RoomDetect.Pt>>> walls, IDictionary<string, string> stamp,
        int pattern, double tol, ref BoundingBox box, ref int index, ref int count, ref int added)
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        Func<string, double> measure = text =>
        {
            if (!widths.TryGetValue(text, out var paper))
                widths[text] = paper = ModelTextWidth(doc, text, PlanDims.TextMm * scale) / scale;
            return paper;
        };
        var laid = PlanDims.LayoutFixed(chains, scale, taken, walls, measure);
        foreach (var chain in laid)
        {
            if (!chain.Placed) continue;
            var stamps = new Dictionary<string, string>(stamp ?? new Dictionary<string, string>())
            {
                ["forsk:dim_chain"] = chain.Id,
                ["forsk:dim_kind"] = chain.Kind
            };
            var dimStamp = new SymbolStamp { Extra = stamps };
            foreach (var line in chain.Lines)
            {
                using (var curve = new LineCurve(DrawingPoint(line.A), DrawingPoint(line.B)))
                    added += AddStroke(doc, layer, curve, PenThin, scale, false, pattern, tol,
                        "dimension", "line", null, null, ref box, ref index, ref count, dimStamp);
            }
            foreach (var tick in chain.Ticks)
            {
                using (var curve = new LineCurve(DrawingPoint(tick.A), DrawingPoint(tick.B)))
                    added += AddStroke(doc, layer, curve, tickPen, scale, false, pattern, tol,
                        "dimension", "tick", null, null, ref box, ref index, ref count, dimStamp);
            }
            foreach (var label in chain.Texts)
            {
                var values = new Dictionary<string, string>(stamps)
                {
                    ["forsk:dim_value"] = label.Value.ToString(CultureInfo.InvariantCulture),
                    ["forsk:dim_total"] = chain.Total.ToString(CultureInfo.InvariantCulture)
                };
                if (AddDimensionText(doc, layer, label, scale, values, ref box, ref index, ref count)) added++;
                if (!label.Underline) continue;
                // Not to scale (ISO 129-1): a line under the value.
                var reading = new Vector3d(label.Reading.X, label.Reading.Y, 0);
                var under = DrawingPoint(label.Centre) - new Vector3d(-reading.Y, reading.X, 0) * (label.Height / 2.0 + PlanDims.TextPadMm * scale / 2.0);
                using (var curve = new LineCurve(under - reading * (label.Width / 2.0), under + reading * (label.Width / 2.0)))
                    added += AddStroke(doc, layer, curve, PenThin, scale, false, pattern, tol,
                        "dimension", "underline", null, null, ref box, ref index, ref count, dimStamp);
            }
        }
        return laid;
    }

    /// <summary>
    /// The building's outer wall faces in drawing mm: the outline of each
    /// cluster of walls that touch (WallJoins), from their paths (the
    /// footprint before openings are cut), except one inside another's.
    /// </summary>
    private static List<List<RoomDetect.Pt>> PlanOutlines(RhinoDoc doc, Transform worldToHld, Vector3d delta, double tol)
    {
        var records = new List<List<List<RoomDetect.Pt>>>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj) || !string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_drawIncludeExisting && IsExistingUnderlay(doc, obj)) continue;
            var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (rings != null) records.Add(rings);
        }
        return WallJoins.Outlines(records, Math.Max(tol, 1.0))
            .Select(ring => ring.Select(p => ToDrawing(new Point3d(p.X, p.Y, 0), worldToHld, delta)).Select(p => new RoomDetect.Pt(p.X, p.Y)).ToList())
            .ToList();
    }

    /// <summary>
    /// What is already on the plan layer, as PlanDims sees it: tags and marks
    /// are text, symbols and leaders lines nothing may touch, the roof outline
    /// and lines beyond the cut lines only text keeps off. The poché and its
    /// cut outline are the wall rings instead. only, when given, keeps just
    /// the objects it passes.
    /// </summary>
    private static List<PlanDims.Obstacle> PlanObstacles(RhinoDoc doc, Layer layer, Func<ObjectAttributes, bool> only = null)
    {
        var list = new List<PlanDims.Obstacle>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null || obj.Attributes.LayerIndex != layer.Index) continue;
            if (only != null && !only(obj.Attributes)) continue;
            var role = obj.Attributes.GetUserString("forsk:role") ?? "";
            if (role == "section_fill" || role == "cut") continue;
            var bbox = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!bbox.IsValid) continue;
            PlanDims.Kind kind;
            switch (role)
            {
                case "room_tag":
                case "opening_mark":
                    kind = PlanDims.Kind.Text;
                    break;
                case "symbol":
                case "room_leader":
                    kind = PlanDims.Kind.Line;
                    break;
                case "dimension":
                    kind = obj.Attributes.GetUserString("forsk:symbol") == "text" ? PlanDims.Kind.Text : PlanDims.Kind.Dim;
                    break;
                default:
                    kind = PlanDims.Kind.Dim;
                    break;
            }
            list.Add(new PlanDims.Obstacle(new RoomDetect.Box(bbox.Min.X, bbox.Min.Y, bbox.Max.X, bbox.Max.Y), kind));
        }
        return list;
    }

    /// <summary>A value as 1:1 plan text along its line, stamped with what it measures.</summary>
    private static bool AddDimensionText(
        RhinoDoc doc, Layer layer, PlanDims.Label label, int scale, IDictionary<string, string> stamps,
        ref BoundingBox box, ref int index, ref int count)
    {
        var reading = new Vector3d(label.Reading.X, label.Reading.Y, 0);
        var up = new Vector3d(-label.Reading.Y, label.Reading.X, 0);
        var plane = new Plane(DrawingPoint(label.Centre), reading, up);
        var entity = PlanAnnotation(doc, label.Text, plane, PlanDims.TextMm * scale);
        if (entity == null) return false;
        var attr = DrawAttr(layer, FormatStableId("d", index), "dimension", "text", null);
        foreach (var pair in stamps)
            attr.SetUserString(pair.Key, pair.Value);
        Guid id;
        try { id = doc.Objects.AddText(entity, attr); }
        catch (Exception) { id = Guid.Empty; }
        finally { entity.Dispose(); }
        if (id == Guid.Empty) return false;
        var written = doc.Objects.FindId(id);
        if (written?.Geometry is TextEntity stored)
        {
            var model = stored.TextHeight * (stored.DimensionScale > 0 ? stored.DimensionScale : 1.0);
            var paper = OpeningTypes.PaperTextHeight(model, scale, doc.LayoutSpaceAnnotationScalingEnabled);
            written.Attributes.SetUserString("forsk:text_height", model.ToString("0.###", CultureInfo.InvariantCulture));
            written.Attributes.SetUserString("forsk:paper_height", paper.ToString("0.###", CultureInfo.InvariantCulture));
            written.CommitChanges();
            var stamp = stored.GetBoundingBox(true);
            if (stamp.IsValid) box.Union(stamp);
        }
        index++;
        count++;
        return true;
    }

    /// <summary>Model width of text at a model height on the plan's 1:1 style; 0 when Rhino cannot say.</summary>
    private static double ModelTextWidth(RhinoDoc doc, string text, double height)
    {
        TextEntity entity = null;
        try
        {
            entity = PlanAnnotation(doc, text, Plane.WorldXY, height);
            var bbox = entity?.GetBoundingBox(true) ?? BoundingBox.Empty;
            return bbox.IsValid ? bbox.Max.X - bbox.Min.X : 0;
        }
        catch (Exception)
        {
            return 0;
        }
        finally
        {
            entity?.Dispose();
        }
    }

    private static Point3d DrawingPoint(RoomDetect.Pt p) => new Point3d(p.X, p.Y, 0);

    /// <summary>
    /// Drawing back to model XY: the plan maps model XY to drawing XY by an
    /// affine map (the HLD transform, flattened, then the pack's shift), read
    /// off three points and inverted in 2D. Null when it does not invert.
    /// </summary>
    private static Func<RoomDetect.Pt, Point3d> DrawingToModel(Transform worldToHld, Vector3d delta)
    {
        var o = ToDrawing(Point3d.Origin, worldToHld, delta);
        var x = ToDrawing(new Point3d(1000, 0, 0), worldToHld, delta) - o;
        var y = ToDrawing(new Point3d(0, 1000, 0), worldToHld, delta) - o;
        var det = x.X * y.Y - x.Y * y.X;
        if (Math.Abs(det) < 1e-9) return null;
        return p =>
        {
            var dx = p.X - o.X;
            var dy = p.Y - o.Y;
            return new Point3d(1000 * (dx * y.Y - dy * y.X) / det, 1000 * (x.X * dy - x.Y * dx) / det, 0);
        };
    }
}
