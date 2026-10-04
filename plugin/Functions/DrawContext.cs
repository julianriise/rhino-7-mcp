using System.Collections.Generic;
using System.Globalization;
using Rhino;
using Rhino.Geometry;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>What the Draw wall and Draw stair commands read off the model before the first click.</summary>
public sealed class DrawContext
{
    /// <summary>Each host wall's forsk:path loops.</summary>
    public readonly List<List<List<Pt>>> Records = new List<List<List<Pt>>>();
    public readonly List<double> Thicknesses = new List<double>();
    /// <summary>True when the 3D model stands: at least one wall record. False: a flat plan, or nothing yet. Records then hold the plan's wall outlines.</summary>
    public bool Solid;
    /// <summary>Floor to floor, mm: what add_stair uses when no rise is given.</summary>
    public double Rise;
}

public partial class RhinoMCPFunctions
{
    /// <summary>The walls the new ones snap to and the numbers add_stair would take. UI thread.</summary>
    public DrawContext ReadDrawContext(RhinoDoc doc)
    {
        var context = new DrawContext();
        if (doc == null) return context;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsHostWall(doc, obj)) continue;
            var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (rings == null) continue;
            context.Records.Add(rings);
            var thickness = ParseMm(obj.Attributes.GetUserString("forsk:thickness"));
            if (thickness.HasValue) context.Thicknesses.Add(thickness.Value);
        }
        context.Solid = context.Records.Count > 0;
        if (!context.Solid) ReadPlanWallOutlines(doc, context);
        context.Rise = StairAutoRise(doc);
        return context;
    }

    /// <summary>A flat plan has no records: its closed wall-layer outlines are what a new wall snaps to.</summary>
    private static void ReadPlanWallOutlines(RhinoDoc doc, DrawContext context)
    {
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!(obj.Geometry is Curve curve) || !curve.IsClosed) continue;
            if (!doc.Layers[obj.Attributes.LayerIndex].Name.Equals(PlanWallLayerName, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!curve.TryGetPolyline(out Polyline polyline) || polyline == null || polyline.Count < 4) continue;
            var ring = new List<Pt>(polyline.Count - 1);
            for (var i = 0; i < polyline.Count - 1; i++) ring.Add(new Pt(polyline[i].X, polyline[i].Y));
            context.Records.Add(new List<List<Pt>> { ring });
        }
    }

    private const string DrawSection = "forsk_draw";
    private const string DrawWallThicknessKey = "wall_thickness";

    /// <summary>The wall thickness the last Draw wall used in this file, else null.</summary>
    public static double? LastDrawnThickness(RhinoDoc doc)
    {
        var text = doc?.Strings.GetValue(DrawSection, DrawWallThicknessKey);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : (double?)null;
    }

    public static void RememberDrawnThickness(RhinoDoc doc, double thickness)
    {
        doc?.Strings.SetString(DrawSection, DrawWallThicknessKey, thickness.ToString("0.###", CultureInfo.InvariantCulture));
    }
}
