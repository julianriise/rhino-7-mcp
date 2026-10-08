using System;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Extrusion.Create extrudes along the normal of the plane Rhino fits to the
/// profile, and on some outlines that normal points down although the curve
/// runs counter-clockwise. A floor slab rebuilt after Undo and Redo then went
/// up (0 to 400) instead of down (-400 to 0). The extrude is checked against
/// the asked side and moved by the shift this returns.
/// </summary>
public static class ExtrudeSide
{
    /// <summary>
    /// The Z shift that puts a solid spanning <paramref name="gotMin"/> to
    /// <paramref name="gotMax"/> on the asked side of the profile at
    /// <paramref name="profileZ"/>: up for a positive height, down for a
    /// negative one. 0 when it is already there.
    /// </summary>
    public static double Shift(double profileZ, double height, double gotMin, double gotMax, double tol)
    {
        var wantMin = profileZ + Math.Min(0, height);
        var wantMax = profileZ + Math.Max(0, height);
        var slack = Math.Max(tol, 1e-6);
        if (Math.Abs(gotMin - wantMin) <= slack && Math.Abs(gotMax - wantMax) <= slack) return 0;
        return wantMin - gotMin;
    }
}
