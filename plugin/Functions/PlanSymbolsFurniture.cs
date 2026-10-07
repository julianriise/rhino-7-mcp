using System;
using System.Globalization;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// FU.2: each piece of furniture on the plan as its 2D symbol, drawn from
/// the catalogue (Furniture.Plan), never a Make2D of its block. Thin pen,
/// detail lines from 1:50, dashed where it hangs above the cut. Role
/// furniture (fixed pieces furniture_fixed), so a DWG export puts it on
/// A-FURN or A-FURN-FIXD. Each stroke also carries the piece's catalogue
/// id, frame and scale, so the export writes one named block per type.
/// </summary>
public partial class RhinoMCPFunctions
{
    public const string FurnitureFrameKey = "forsk:fu_frame";
    public const string FurnitureScaleKey = "forsk:fu_scale";

    private int BakeFurnitureSymbols(
        RhinoDoc doc,
        Layer layer,
        int scale,
        Transform worldToHld,
        Vector3d delta,
        int pattern,
        double tol,
        ref BoundingBox box,
        ref int index,
        ref int count,
        ref PlanStats stats)
    {
        var added = 0;
        foreach (var obj in FurnitureObjects(doc))
        {
            if (!TryFurniture(obj, out var piece, out var frame, out _)) continue;
            var plane = new Plane(new Point3d(frame.Ox, frame.Oy, 0), new Vector3d(frame.Ux, frame.Uy, 0), new Vector3d(-frame.Uy, frame.Ux, 0));
            var markerId = obj.Id.ToString();
            var role = Furniture.RoleFor(piece);
            var baked = 0;
            // AllObjectsSince returns the objects after this serial: one before the next keeps the first stroke.
            var since = RhinoObject.NextRuntimeSerialNumber - 1;
            foreach (var mark in Furniture.Plan(piece, scale))
            {
                try
                {
                    Curve curve;
                    if (mark.Shape == "line")
                        curve = new LineCurve(MapPlan(mark.X0, mark.Y0, plane, worldToHld, delta), MapPlan(mark.X1, mark.Y1, plane, worldToHld, delta));
                    else
                    {
                        Point3d At(double degrees) => MapPlan(
                            mark.Cx + mark.R * Math.Cos(degrees * Math.PI / 180), mark.Cy + mark.R * Math.Sin(degrees * Math.PI / 180),
                            plane, worldToHld, delta);
                        curve = new ArcCurve(new Arc(At(mark.A0), At((mark.A0 + mark.A1) / 2), At(mark.A1)));
                    }
                    using (curve)
                    {
                        if (!curve.IsValid || curve.GetLength() < 0.5) continue;
                        var pen = ForskTechnical.PenFor(role, mark.Part, PrintProfiles.Active);
                        baked += AddStroke(doc, layer, curve, pen, scale, mark.Dashed, pattern, tol,
                            role, mark.Part, markerId, null, ref box, ref index, ref count);
                    }
                }
                catch (Exception)
                {
                    // One piece that will not draw leaves the rest of the symbol.
                }
            }
            if (baked == 0) continue;
            StampFurnitureStrokes(doc, since, piece, plane, worldToHld, delta, scale);
            stats.Furniture++;
            added += baked;
        }
        return added;
    }

    /// <summary>
    /// The piece's catalogue id, its frame in the drawing's millimetres
    /// (origin, x and y) and the scale, on every stroke its symbol just added.
    /// </summary>
    private static void StampFurnitureStrokes(
        RhinoDoc doc, uint since, Furniture.Piece piece, Plane plane, Transform worldToHld, Vector3d delta, int scale)
    {
        var origin = MapPlan(0, 0, plane, worldToHld, delta);
        var x = MapPlan(1, 0, plane, worldToHld, delta) - origin;
        var y = MapPlan(0, 1, plane, worldToHld, delta) - origin;
        var frame = string.Format(CultureInfo.InvariantCulture,
            "{0:R},{1:R},{2:R},{3:R},{4:R},{5:R}", origin.X, origin.Y, x.X, x.Y, y.X, y.Y);
        foreach (var obj in doc.Objects.AllObjectsSince(since) ?? new RhinoObject[0])
        {
            if (obj?.Attributes == null) continue;
            var attr = obj.Attributes.Duplicate();
            attr.SetUserString(Furniture.CatalogKey, piece.Id);
            attr.SetUserString(FurnitureFrameKey, frame);
            attr.SetUserString(FurnitureScaleKey, scale.ToString(CultureInfo.InvariantCulture));
            doc.Objects.ModifyAttributes(obj, attr, true);
        }
    }
}
