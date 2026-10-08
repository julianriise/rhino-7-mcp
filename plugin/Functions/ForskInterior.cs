using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Forsk Interior, the look of a Jump inside view (Julian, 2026-10-08): Rhino's
/// Rendered mode with the materials on, and no curves, points, text, lights,
/// grid or edges. The room-type pastel stays off it, the hidden roof is drawn
/// as a ceiling, and the look comes back when its named view is restored
/// (Rhino 7 named views keep no display mode). No RhinoCommon.
/// </summary>
public static class ForskInterior
{
    public const string ModeName = "Forsk Interior";
    public const string RenderedName = "Rendered";
    public const string RaytracedName = "Raytraced";

    /// <summary>Document string: the named views Jump inside saved, one per line.</summary>
    public const string ViewsKey = "forsk:interior_views";

    /// <summary>A restored camera is the saved one within this distance, in mm.</summary>
    public const double SameCameraMm = 1.0;

    /// <summary>
    /// The layers Jump inside gives a material when they have none, and the
    /// preset: loose furniture is wood, fixed fittings white, stairs wood.
    /// Walls (plaster), floors (concrete), doors and windows (wood, glass)
    /// already get theirs when they are made.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> LayerMaterials = new[]
    {
        new KeyValuePair<string, string>(Furniture.LayerName, "wood"),
        new KeyValuePair<string, string>(Furniture.FixedLayerName, "white"),
        new KeyValuePair<string, string>(Stairs.LayerName, "wood"),
    };

    /// <summary>The ceiling drawn from the hidden roof: a warm white plaster.</summary>
    public static readonly (int R, int G, int B) Ceiling = (238, 235, 230);

    /// <summary>A mode that shows materials as they are: Forsk Interior, Rendered, Raytraced.</summary>
    public static bool IsRealistic(string modeName)
    {
        return string.Equals(modeName, ModeName, StringComparison.Ordinal)
            || string.Equals(modeName, RenderedName, StringComparison.Ordinal)
            || string.Equals(modeName, RaytracedName, StringComparison.Ordinal);
    }

    public static bool IsMode(string modeName) => string.Equals(modeName, ModeName, StringComparison.Ordinal);

    /// <summary>The stored view names, in order, without blanks or repeats (case ignored).</summary>
    public static List<string> Views(string stored)
    {
        return (stored ?? "").Split('\n')
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The list with <paramref name="name"/> added once.</summary>
    public static string AddView(string stored, string name)
    {
        var views = Views(stored);
        if (!string.IsNullOrWhiteSpace(name) && !views.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
            views.Add(name.Trim());
        return string.Join("\n", views);
    }

    /// <summary>The same camera: eye and target each within <see cref="SameCameraMm"/>.</summary>
    public static bool SameCamera(double[] eye, double[] target, double[] savedEye, double[] savedTarget)
    {
        return Near(eye, savedEye) && Near(target, savedTarget);
    }

    static bool Near(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length < 3 || b.Length < 3) return false;
        var dx = a[0] - b[0];
        var dy = a[1] - b[1];
        var dz = a[2] - b[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= SameCameraMm;
    }
}
