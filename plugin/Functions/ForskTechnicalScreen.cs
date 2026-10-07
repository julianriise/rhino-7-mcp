using System;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    /// <summary>
    /// The plan pieces of one opening marker, generated stair or piece of furniture, from the plan
    /// sheet's own symbol code at the plan cut. Null when it draws nothing.
    /// <paramref name="host"/> is the marker's wall.
    /// </summary>
    internal List<ForskTechnical.Stroke> ScreenSymbol(RhinoDoc doc, RhinoObject obj, double cutZ, PrintProfile profile, out Guid host)
    {
        host = Guid.Empty;
        if (doc == null || obj?.Attributes == null) return null;
        var kind = GetForskKind(obj);
        var z = cutZ - ForskTechnical.BelowCutMm;
        if (string.Equals(kind, "opening_marker", StringComparison.OrdinalIgnoreCase))
        {
            Guid.TryParse(obj.Attributes.GetUserString("forsk:host"), out host);
            if (!TryOpeningMarks(doc, obj, cutZ, out _, out var plane, out _, out var marks)) return null;
            var frame = new ForskTechnical.Frame(
                plane.Origin.X, plane.Origin.Y, plane.XAxis.X, plane.XAxis.Y, plane.YAxis.X, plane.YAxis.Y);
            return ForskTechnical.FromOpening(marks, frame, z, profile);
        }
        if (string.Equals(kind, Stairs.Kind, StringComparison.OrdinalIgnoreCase)
            && IsForskGenerated(obj)
            && TryStairAsBuilt(obj, out var spec, out var flight))
            return ForskTechnical.FromStair(spec, flight, cutZ, StairDraw.PreviewScale, z, profile);
        if (string.Equals(kind, Furniture.Kind, StringComparison.OrdinalIgnoreCase)
            && IsForskGenerated(obj)
            && TryFurniture(obj, out var piece, out var placed, out _))
            // A piece hung above the cut (a wall unit) shows dashed, as on the sheet.
            return ForskTechnical.FromFurniture(piece, placed, z, profile);
        return null;
    }
}
