using System;
using System.Collections.Generic;
using System.Globalization;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// FU.2: in a DWG or DXF sheet every piece of furniture is one insert of a
/// named block, FORSK_FU_&lt;catalogue id&gt;, on A-FURN or A-FURN-FIXD, so a
/// consultant can swap or freeze a type. The block holds the plan symbol in
/// the piece's own millimetres, drawn from the catalogue as the plan does;
/// the insert carries the piece's place and the sheet's scale.
/// </summary>
public partial class RhinoMCPFunctions
{
    /// <summary>A dashed symbol line in a block: dashes this long, gaps this long, model mm.</summary>
    private const double FurnitureDashMm = 100;
    private const double FurnitureGapMm = 60;

    /// <summary>A furniture stroke's piece as one block insert, from the frame the plan stamped on it. Null when it does not read.</summary>
    private static FlatPiece FurnitureBlockPiece(RhinoObject stroke, Transform map, double dx, double dy)
    {
        var piece = Furniture.Find(stroke.Attributes.GetUserString(Furniture.CatalogKey));
        var parts = (stroke.Attributes.GetUserString(FurnitureFrameKey) ?? "").Split(',');
        if (piece == null || parts.Length != 6) return null;
        var v = new double[6];
        for (var i = 0; i < 6; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
        if (!int.TryParse(stroke.Attributes.GetUserString(FurnitureScaleKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale))
            scale = 100;
        // Piece frame to the drawing's millimetres, then the bake's centre move, then the page.
        var frame = Transform.Identity;
        frame.M00 = v[2];
        frame.M10 = v[3];
        frame.M01 = v[4];
        frame.M11 = v[5];
        frame.M03 = v[0];
        frame.M13 = v[1];
        var curves = new List<Curve>();
        foreach (var mark in Furniture.Plan(piece, scale))
        {
            if (mark.Shape == "line")
            {
                var a = new Point3d(mark.X0, mark.Y0, 0);
                var b = new Point3d(mark.X1, mark.Y1, 0);
                if (!mark.Dashed)
                {
                    curves.Add(new LineCurve(a, b));
                    continue;
                }
                var length = a.DistanceTo(b);
                var dir = b - a;
                if (length < 1e-6) continue;
                dir.Unitize();
                for (var s = 0.0; s < length; s += FurnitureDashMm + FurnitureGapMm)
                    curves.Add(new LineCurve(a + dir * s, a + dir * Math.Min(length, s + FurnitureDashMm)));
            }
            else
            {
                var arc = new Arc(new Circle(new Point3d(mark.Cx, mark.Cy, 0), mark.R),
                    new Interval(mark.A0 * Math.PI / 180, mark.A1 * Math.PI / 180));
                curves.Add(new ArcCurve(arc));
            }
        }
        // The plan's pen for the symbol: the PDF strokes at it; a DWG insert takes its layer's.
        var pen = ForskTechnical.PenFor(Furniture.RoleFor(piece), null, PrintProfiles.Active);
        return new FlatPiece
        {
            Layer = SheetFlat.LayerFor(Furniture.RoleFor(piece)),
            Weight = pen.Mm,
            Color = pen.Color,
            BlockName = Furniture.BlockName(piece),
            BlockCurves = curves,
            BlockXform = Flat(map * Transform.Translation(dx, dy, 0) * frame),
            BlockScale = scale
        };
    }

    /// <summary>
    /// The plan part of a transform, with z kept: a detail's world-to-page map
    /// flattens z, and an insert whose transform cannot be inverted is refused.
    /// </summary>
    private static Transform Flat(Transform t)
    {
        var flat = Transform.Identity;
        flat.M00 = t.M00;
        flat.M01 = t.M01;
        flat.M03 = t.M03;
        flat.M10 = t.M10;
        flat.M11 = t.M11;
        flat.M13 = t.M13;
        return flat;
    }

    /// <summary>
    /// The block for a piece in the target: one per name and symbol scale.
    /// A name the target already holds for something else takes a suffix.
    /// </summary>
    private static int FurnitureBlockIn(RhinoDoc target, FlatPiece piece, int layer, Dictionary<string, int> blocks, List<int> made)
    {
        var key = piece.BlockName + "|" + (piece.BlockScale <= Furniture.DetailScale ? "detail" : "plain");
        if (blocks.TryGetValue(key, out var known)) return known;
        var name = piece.BlockName;
        // The same type at both detail levels: the second is another block.
        if (blocks.ContainsKey(piece.BlockName + "|" + (piece.BlockScale <= Furniture.DetailScale ? "plain" : "detail")))
            name += "_1-" + piece.BlockScale.ToString(CultureInfo.InvariantCulture);
        for (var n = 2; target.InstanceDefinitions.Find(name) != null; n++)
            name = piece.BlockName + "_" + n.ToString(CultureInfo.InvariantCulture);
        var attrs = new List<ObjectAttributes>();
        foreach (var _ in piece.BlockCurves)
            attrs.Add(new ObjectAttributes
            {
                LayerIndex = layer,
                ColorSource = ObjectColorSource.ColorFromLayer,
                PlotColorSource = ObjectPlotColorSource.PlotColorFromLayer,
                PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromLayer
            });
        var index = target.InstanceDefinitions.Add(name, "", Point3d.Origin, piece.BlockCurves, attrs);
        if (index < 0) return -1;
        made?.Add(index);
        blocks[key] = index;
        return index;
    }
}
