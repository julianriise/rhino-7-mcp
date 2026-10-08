using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Forsk Interior and Forsk Exterior, the looks of an Interior render and an
/// Exterior render (Julian, 2026-10-08): Rhino's Rendered mode with the
/// materials on, subtle shadows, and no curves, points, text, lights, grid or
/// edges. The room-type pastel stays off them. The hidden roof is drawn: as a
/// plaster ceiling inside, as the roof outside. The look comes back when the
/// saved named view is restored (Rhino 7 named views keep no display mode).
/// No RhinoCommon.
/// </summary>
public static class ForskInterior
{
    public const string ModeName = "Forsk Interior";
    public const string ExteriorModeName = "Forsk Exterior";
    public const string RenderedName = "Rendered";
    public const string RaytracedName = "Raytraced";

    /// <summary>Document string: the named views Interior render saved, one per line.</summary>
    public const string ViewsKey = "forsk:interior_views";
    /// <summary>Document string: the named views Exterior render saved, one per line.</summary>
    public const string ExteriorViewsKey = "forsk:exterior_views";

    /// <summary>A restored camera is the saved one within this distance, in mm.</summary>
    public const double SameCameraMm = 1.0;

    /// <summary>
    /// The layers a render gives a material when they have none, and the
    /// preset: loose furniture is wood, fixed fittings white, stairs wood, the roof plaster.
    /// Walls (plaster), floors (concrete), doors and windows (wood, glass)
    /// already get theirs when they are made.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> LayerMaterials = new[]
    {
        new KeyValuePair<string, string>(Furniture.LayerName, "wood"),
        new KeyValuePair<string, string>(Furniture.FixedLayerName, "white"),
        new KeyValuePair<string, string>(Stairs.LayerName, "wood"),
        // The roof shows in renders now: its underside is the ceiling inside, and from eye height outside only its edge shows.
        new KeyValuePair<string, string>("A-ROOF", "plaster"),
    };

    /// <summary>The ceiling drawn from the hidden roof: a warm white plaster.</summary>
    public static readonly (int R, int G, int B) Ceiling = (238, 235, 230);
    /// <summary>The hidden roof seen from outside: A-ROOF's own dark grey.</summary>
    public static readonly (int R, int G, int B) Roof = (70, 72, 76);

    /// <summary>
    /// 1: the import from a patched Rendered export, with subtle shadows
    /// (Julian, 2026-10-08). The first cut was a copy changed through
    /// RhinoCommon, which cannot reach the shadow settings; a missing plugin
    /// setting is 0, so that copy is replaced.
    /// </summary>
    public const int ModeRevision = 1;

    /// <summary>Shadows on and subtle, as Julian tuned them: 25 % black, soft-edged, glass casts none.</summary>
    public const int ShadowIntensity = 25;
    public const int ShadowBlur = 5;
    public const int ShadowSamples = 1;
    /// <summary>Julian tried 576; 1024 keeps the contact shadow under furniture crisper and stays quick on an M1.</summary>
    public const int ShadowMapSize = 1024;
    /// <summary>0: a transparent object (glass) casts no shadow.</summary>
    public const int ShadowTransparency = 0;

    /// <summary>A stored revision below <see cref="ModeRevision"/> is an older mode.</summary>
    public static bool NeedsReimport(int storedRevision) => storedRevision < ModeRevision;

    /// <summary>
    /// The export under a new id. The export is of a copy Forsk deletes before the
    /// import; imported under that same id, Rhino keeps drawing its cached entry
    /// for it, plain Rendered with lines across every surface and no shadows,
    /// until it restarts (Julian's 1.3.0 test, 2026-10-08). A fresh id draws right.
    /// </summary>
    public static string WithFreshId(string ini, Guid id)
    {
        var match = System.Text.RegularExpressions.Regex.Match(ini ?? "", @"\[DisplayMode\\([0-9A-Fa-f-]{36})");
        return match.Success ? ini.Replace(match.Groups[1].Value, id.ToString()) : ini;
    }

    /// <summary>A Rendered export as Forsk Interior. A key the export lacks stays absent.</summary>
    public static string Patch(string exported) => ForskWhite.PatchWith(exported, Rules(ModeName));

    /// <summary>The same look as Forsk Exterior, its own mode so a view can say which it is.</summary>
    public static string PatchExterior(string exported) => ForskWhite.PatchWith(exported, Rules(ExteriorModeName));

    static Dictionary<string, ForskWhite.Rule> Rules(string name) => ForskWhite.RuleMap(new[]
    {
        ForskWhite.Text("", "Name", name),
        ForskWhite.Bool("Objects\\Curves", "ShowCurves", false),
        ForskWhite.Bool("Objects\\Points", "ShowPoints", false),
        ForskWhite.Bool("Objects\\Annotations", "ShowText", false),
        ForskWhite.Bool("Objects\\Annotations", "ShowAnnotations", false),
        ForskWhite.Bool("Objects\\Surfaces", "ShowIsocurves", false),
        ForskWhite.Bool("Objects\\Surfaces", "ShowEdges", false),
        ForskWhite.Bool("Objects\\Surfaces", "ShowTangentEdges", false),
        ForskWhite.Bool("Objects\\Surfaces", "ShowTangentSeams", false),
        ForskWhite.Bool("View settings", "DrawGrid", false),
        ForskWhite.Bool("View settings", "DrawAxes", false),
        ForskWhite.Bool("View settings", "DrawWorldAxes", false),
        ForskWhite.Bool("View settings", "DrawZAxis", false),
        ForskWhite.Bool("View settings", "ShowClippingPlanes", false),
        ForskWhite.Bool("Lighting", "ShowLights", false),
        ForskWhite.Bool("Lighting", "CastShadows", true),
        ForskWhite.Int("Lighting", "ShadowIntensity", ShadowIntensity),
        ForskWhite.Rgb("Lighting", "ShadowColor", 0, 0, 0),
        ForskWhite.Int("Lighting", "ShadowBlur", ShadowBlur),
        ForskWhite.Int("Lighting", "NumSamples", ShadowSamples),
        ForskWhite.Int("Lighting", "ShadowMapSize", ShadowMapSize),
        ForskWhite.Int("Lighting", "TransparencyTolerance", ShadowTransparency),
    });

    /// <summary>A mode that shows materials as they are: Forsk Interior, Forsk Exterior, Rendered, Raytraced.</summary>
    public static bool IsRealistic(string modeName)
    {
        return IsRenderMode(modeName)
            || string.Equals(modeName, RenderedName, StringComparison.Ordinal)
            || string.Equals(modeName, RaytracedName, StringComparison.Ordinal);
    }

    public static bool IsMode(string modeName) => string.Equals(modeName, ModeName, StringComparison.Ordinal);

    public static bool IsExteriorMode(string modeName) => string.Equals(modeName, ExteriorModeName, StringComparison.Ordinal);

    /// <summary>Forsk Interior or Forsk Exterior: a render's look, which Forsk White leaves alone.</summary>
    public static bool IsRenderMode(string modeName) => IsMode(modeName) || IsExteriorMode(modeName);

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
