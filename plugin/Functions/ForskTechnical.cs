using System;
using System.Collections.Generic;
using System.Drawing;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Forsk Technical, the plan and elevation look. A parallel view looking
/// straight down is a plan; a parallel view looking level is an elevation;
/// every other view keeps Forsk White. The mode is a patched Wireframe: lines
/// only, no surfaces and no shading. The cut is a heavy outline, not a filled
/// cap. The door and window symbols, the stair and the elevation ground line
/// are drawn over it from the pieces the plan and facade sheets print
/// (ForskTechnicalHost). Pens come from the document's print profile, so
/// screen and paper agree.
/// </summary>
public static class ForskTechnical
{
    public const string ModeName = "Forsk Technical";
    /// <summary>Plugin setting. Missing is on.</summary>
    public const string SettingKey = "ForskTechnical";
    /// <summary>Plugin setting that holds <see cref="Signature"/> of the last import.</summary>
    public const string SignatureKey = "ForskTechnicalSignature";
    public const int ModeRevision = 2;
    /// <summary>The paper weight one screen pixel stands for: the default beyond pen.</summary>
    public const double MmPerPx = 0.18;
    public const int MaxPx = 6;
    /// <summary>A camera whose sideways (plan) or vertical (elevation) share is under this, about 1°, is square on.</summary>
    public const double Tilt = 0.02;
    /// <summary>The share of ink a hatched poché shows on screen, where its lines are not drawn.</summary>
    public const double HatchInk = 0.25;
    /// <summary>Plan pieces sit this far under the cut, on the side the plan clipping plane keeps.</summary>
    public const double BelowCutMm = 10;

    public enum Look
    {
        Model,
        Plan,
        Elevation
    }

    /// <summary>Plan, elevation or model from the projection and the camera direction. Looking up is not a plan.</summary>
    public static Look Classify(bool parallel, double dx, double dy, double dz)
    {
        if (!parallel) return Look.Model;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(length > 1e-12)) return Look.Model;
        var down = -dz / length;
        var level = Math.Sqrt(dx * dx + dy * dy) / length;
        if (down > 0 && level < Tilt) return Look.Plan;
        if (Math.Abs(dz / length) < Tilt) return Look.Elevation;
        return Look.Model;
    }

    /// <summary>The mode a model view should be on: Shaded when Forsk White is off.</summary>
    public static string Wanted(bool whiteOn, bool technicalOn, Look look)
    {
        if (!whiteOn) return ForskWhite.ShadedModeName;
        return technicalOn && look != Look.Model ? ModeName : ForskWhite.ModeName;
    }

    /// <summary>
    /// A view Forsk put on one of its modes follows its projection, and a view
    /// on a replaced import takes the new one. A view the user put on another
    /// mode is left.
    /// </summary>
    public static bool Retunes(string currentModeName, string wantedModeName, bool sameMode)
    {
        if (!ForskWhite.IsForskMode(currentModeName) || !ForskWhite.IsForskMode(wantedModeName)) return false;
        return !string.Equals(currentModeName, wantedModeName, StringComparison.Ordinal) || !sameMode;
    }

    /// <summary>Screen pixels for a paper pen: 0.13 and 0.18 are 1, 0.35 is 2, 0.50 is 3, 0.70 is 4.</summary>
    public static int Px(double mm)
    {
        if (double.IsNaN(mm) || mm <= 0) return 1;
        var px = (int)Math.Round(mm / MmPerPx, MidpointRounding.AwayFromZero);
        return Math.Max(1, Math.Min(MaxPx, px));
    }

    /// <summary>
    /// The pen a plan or facade piece prints with, by the forsk:role the sheet
    /// stamps on it: cut outlines at the cut pen, the stair outline at the
    /// beyond pen and its other parts thin, opening symbols thin, the ground
    /// line at the section's ground pen, other lines at the beyond pen.
    /// </summary>
    public static PrintPen PenFor(string role, string part, PrintProfile profile)
    {
        profile = profile ?? PrintProfiles.Default;
        switch (role)
        {
            case "cut":
                return profile.Cut;
            case "ground_line":
                return Sections.GroundPen(profile);
            case "stair":
                return part == "outline" ? profile.Beyond : profile.Thin;
            case "symbol":
                return profile.Thin;
            case "greyscale":
            case "beyond":
                return profile.Beyond;
            default:
                return profile.Thin;
        }
    }

    /// <summary>A dashed line takes the profile's linetype colour, whatever tier it is drawn at.</summary>
    public static Color Ink(PrintPen pen, bool dashed, PrintProfile profile)
    {
        return dashed ? (profile ?? PrintProfiles.Default).Dashed : pen.Color;
    }

    /// <summary>The poché on screen: the solid fill, or a tint of a hatch's ink (its lines are not drawn).</summary>
    public static Color ScreenPoche(PrintProfile profile)
    {
        profile = profile ?? PrintProfiles.Default;
        if (profile.PocheSolid) return profile.Poche;
        int Tint(int ink) => 255 - (int)Math.Round((255 - ink) * HatchInk, MidpointRounding.AwayFromZero);
        return Color.FromArgb(Tint(profile.Poche.R), Tint(profile.Poche.G), Tint(profile.Poche.B));
    }

    /// <summary>The import's revision and profile. Another profile, or a newer revision, is a new import.</summary>
    public static string Signature(PrintProfile profile)
    {
        return ModeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (profile ?? PrintProfiles.Default).Name;
    }

    public static bool NeedsReimport(string storedSignature, PrintProfile profile)
    {
        return !string.Equals(storedSignature ?? "", Signature(profile), StringComparison.Ordinal);
    }

    public static string Patch(string exported, PrintProfile profile)
    {
        return ForskWhite.PatchWith(exported, Rules(profile));
    }

    static Dictionary<string, ForskWhite.Rule> Rules(PrintProfile profile)
    {
        profile = profile ?? PrintProfiles.Default;
        var poche = ScreenPoche(profile);
        var cut = profile.Cut;
        var beyond = profile.Beyond;
        var edge = Px(beyond.Mm);
        return ForskWhite.RuleMap(new[]
        {
            ForskWhite.Text("", "Name", ModeName),
            ForskWhite.Rgb("View settings", "SolidColor", 255, 255, 255),
            ForskWhite.Int("View settings", "FillMode", 2),
            ForskWhite.Bool("View settings", "UseDocumentGrid", false),
            ForskWhite.Bool("View settings", "DrawGrid", false),
            ForskWhite.Bool("View settings", "DrawAxes", false),
            ForskWhite.Bool("View settings", "DrawWorldAxes", false),
            ForskWhite.Bool("View settings", "DrawZAxis", false),
            ForskWhite.Bool("View settings", "ShowClippingPlanes", false),
            ForskWhite.Bool("View settings", "ClippingShowXSurface", false),
            ForskWhite.Bool("View settings", "ClippingShowXEdges", true),
            ForskWhite.Int("View settings", "ClippingSurfaceUsage", ForskWhite.ClipFillUsage),
            ForskWhite.Int("View settings", "ClippingEdgesUsage", ForskWhite.ClipEdgeUsage),
            ForskWhite.Rgb("View settings", "ClippingSurfaceColor", poche.R, poche.G, poche.B),
            ForskWhite.Rgb("View settings", "ClippingEdgeColor", cut.Color.R, cut.Color.G, cut.Color.B),
            ForskWhite.Int("View settings", "ClippingEdgeThickness", Px(cut.Mm)),
            ForskWhite.Bool("Shading", "ShadeVertexColors", false),
            ForskWhite.Bool("Shading", "ShadeSurface", false),
            ForskWhite.Bool("Shading", "UseObjectMaterial", false),
            ForskWhite.Bool("Shading\\Material\\Front Material", "FlatShaded", false),
            ForskWhite.Bool("Shading\\Material\\Front Material", "OverrideObjectColor", true),
            ForskWhite.Bool("Shading\\Material\\Front Material", "OverrideObjectTransparency", true),
            ForskWhite.Rgb("Shading\\Material\\Front Material", "Diffuse", 255, 255, 255),
            ForskWhite.Int("Shading\\Material\\Front Material", "Shine", 0),
            ForskWhite.Rgb("Shading\\Material\\Front Material", "Specular", 0, 0, 0),
            ForskWhite.Int("Shading\\Material\\Front Material", "ShineIntensity", 0),
            ForskWhite.Int("Shading\\Material\\Front Material", "Transparency", 0),
            ForskWhite.Bool("Shading\\Material\\Back Material", "FlatShaded", false),
            ForskWhite.Bool("Shading\\Material\\Back Material", "OverrideObjectColor", true),
            ForskWhite.Rgb("Shading\\Material\\Back Material", "Diffuse", 255, 255, 255),
            ForskWhite.Rgb("Shading\\Material\\Back Material", "Specular", 0, 0, 0),
            ForskWhite.Int("Shading\\Material\\Back Material", "ShineIntensity", 0),
            // Full white ambient keeps every face paper white, whatever the lights do.
            ForskWhite.Rgb("Lighting", "AmbientColor", 255, 255, 255),
            ForskWhite.Bool("Lighting", "CastShadows", false),
            ForskWhite.Bool("Lighting", "PerPixelLighting", false),
            ForskWhite.Bool("Objects\\Surfaces", "ShowIsocurves", false),
            ForskWhite.Bool("Objects\\Surfaces", "ShowTangentEdges", false),
            ForskWhite.Bool("Objects\\Surfaces", "ShowTangentSeams", false),
            ForskWhite.Bool("Objects\\Surfaces", "ShowEdges", true),
            ForskWhite.Int("Objects\\Surfaces", "EdgeThickness", edge),
            ForskWhite.Int("Objects\\Surfaces", "NakedEdgeThickness", edge),
            ForskWhite.Int("Objects\\Surfaces", "EdgeColorUsage", ForskWhite.SurfaceEdgeUsage),
            ForskWhite.Int("Objects\\Surfaces", "NakedEdgeColorUsage", ForskWhite.SurfaceEdgeUsage),
            ForskWhite.Rgb("Objects\\Surfaces", "EdgeColor", beyond.Color.R, beyond.Color.G, beyond.Color.B),
            ForskWhite.Rgb("Objects\\Surfaces", "NakedEdgeColor", beyond.Color.R, beyond.Color.G, beyond.Color.B),
            ForskWhite.Bool("Objects\\Meshes", "ShowMeshWires", false)
        });
    }

    /// <summary>
    /// One screen piece, in world millimetres: a line, an arc (start, a point
    /// on it, end), a filled dot, or a label. Role and part are what the sheet
    /// stamps on the same piece (forsk:role, forsk:part), so its export layer
    /// is SheetFlat.LayerFor(Role).
    /// </summary>
    public sealed class Stroke
    {
        public string Shape;
        public string Role;
        public string Part;
        public double X0, Y0, X1, Y1, Xm, Ym, Z;
        public double Radius;
        public string Text;
        public bool Dashed;
        /// <summary>The paper weight of the pen it prints with.</summary>
        public double Mm;
        public Color Ink;
        public int Px => ForskTechnical.Px(Mm);
    }

    /// <summary>A plan frame: origin, along (x) and across (y) axes, as the sheet's MapPlan reads a symbol plane.</summary>
    public readonly struct Frame
    {
        public Frame(double ox, double oy, double xx, double xy, double yx, double yy)
        {
            Ox = ox;
            Oy = oy;
            Xx = xx;
            Xy = xy;
            Yx = yx;
            Yy = yy;
        }

        public double Ox { get; }
        public double Oy { get; }
        public double Xx { get; }
        public double Xy { get; }
        public double Yx { get; }
        public double Yy { get; }

        public void Map(double x, double y, out double wx, out double wy)
        {
            wx = Ox + Xx * x + Yx * y;
            wy = Oy + Xy * x + Yy * y;
        }
    }

    /// <summary>The point halfway round a symbol arc, in its own frame. False when the arc has no direction.</summary>
    public static bool ArcMid(OpeningTypes.PlanMark mark, out double x, out double y)
    {
        x = y = 0;
        if (mark == null) return false;
        var mx = (mark.X0 - mark.Cx) + (mark.X1 - mark.Cx);
        var my = (mark.Y0 - mark.Cy) + (mark.Y1 - mark.Cy);
        var length = Math.Sqrt(mx * mx + my * my);
        if (!(length > 1e-9)) return false;
        x = mark.Cx + mx / length * mark.Radius;
        y = mark.Cy + my / length * mark.Radius;
        return true;
    }

    /// <summary>A door or window symbol as the plan sheet draws it (role symbol, thin pen).</summary>
    public static List<Stroke> FromOpening(IEnumerable<OpeningTypes.PlanMark> marks, Frame frame, double z, PrintProfile profile)
    {
        var strokes = new List<Stroke>();
        foreach (var mark in marks ?? new OpeningTypes.PlanMark[0])
        {
            if (mark == null) continue;
            var pen = PenFor("symbol", mark.Part, profile);
            var stroke = new Stroke
            {
                Role = "symbol",
                Part = mark.Part,
                Z = z,
                Dashed = mark.Dashed,
                Mm = pen.Mm,
                Ink = Ink(pen, mark.Dashed, profile)
            };
            frame.Map(mark.X0, mark.Y0, out stroke.X0, out stroke.Y0);
            frame.Map(mark.X1, mark.Y1, out stroke.X1, out stroke.Y1);
            if (mark.Shape == "arc" && mark.Radius > 1 && ArcMid(mark, out var ax, out var ay))
            {
                stroke.Shape = "arc";
                frame.Map(ax, ay, out stroke.Xm, out stroke.Ym);
            }
            else
            {
                stroke.Shape = "line";
                var dx = stroke.X1 - stroke.X0;
                var dy = stroke.Y1 - stroke.Y0;
                if (dx * dx + dy * dy < 0.25) continue;
            }
            strokes.Add(stroke);
        }
        return strokes;
    }

    /// <summary>The stair plan frame: the foot of the first riser, u along the climb, v to its left.</summary>
    public static Frame StairFrame(Stairs.Spec spec)
    {
        return new Frame(spec.X, spec.Y, spec.Dx, spec.Dy, -spec.Dy, spec.Dx);
    }

    /// <summary>A stair as its plan symbol (Stairs.PlanSymbol), role stair, the outline at the beyond pen.</summary>
    public static List<Stroke> FromStair(Stairs.Spec spec, Stairs.Flight flight, double cutZ, int scale, double z, PrintProfile profile)
    {
        var strokes = new List<Stroke>();
        if (spec == null || flight == null) return strokes;
        profile = profile ?? PrintProfiles.Default;
        var frame = StairFrame(spec);
        foreach (var mark in Stairs.PlanSymbol(flight, cutZ - spec.Z, scale, Stairs.LabelHeight(flight, scale)))
        {
            var pen = PenFor("stair", mark.Part, profile);
            var stroke = new Stroke
            {
                Shape = mark.Shape,
                Role = "stair",
                Part = mark.Part,
                Z = z,
                Radius = mark.Radius,
                Text = mark.Text,
                Dashed = mark.Dashed,
                Mm = pen.Mm,
                Ink = mark.Shape == "text" ? profile.Text : Ink(pen, mark.Dashed, profile)
            };
            frame.Map(mark.U0, mark.V0, out stroke.X0, out stroke.Y0);
            frame.Map(mark.U1, mark.V1, out stroke.X1, out stroke.Y1);
            strokes.Add(stroke);
        }
        return strokes;
    }

    /// <summary>
    /// The elevation's ground line: level at the ground, across the model as
    /// the view sees it and FacadeGroundOverMm past each side, at the ground
    /// pen. <paramref name="rightX"/>, <paramref name="rightY"/> is the
    /// camera's right in plan. Null without a ground or a sideways view.
    /// </summary>
    public static Stroke GroundLine(double minX, double minY, double maxX, double maxY, double? groundZ,
        double rightX, double rightY, PrintProfile profile)
    {
        if (!groundZ.HasValue || maxX < minX || maxY < minY) return null;
        var length = Math.Sqrt(rightX * rightX + rightY * rightY);
        if (!(length > 1e-9)) return null;
        var rx = rightX / length;
        var ry = rightY / length;
        var lo = double.PositiveInfinity;
        var hi = double.NegativeInfinity;
        foreach (var x in new[] { minX, maxX })
        foreach (var y in new[] { minY, maxY })
        {
            var along = x * rx + y * ry;
            lo = Math.Min(lo, along);
            hi = Math.Max(hi, along);
        }
        var (from, to) = Sections.FacadeGround(lo, hi);
        var cx = (minX + maxX) / 2.0;
        var cy = (minY + maxY) / 2.0;
        var mid = cx * rx + cy * ry;
        var pen = PenFor("ground_line", null, profile);
        return new Stroke
        {
            Shape = "line",
            Role = "ground_line",
            X0 = cx + rx * (from - mid),
            Y0 = cy + ry * (from - mid),
            X1 = cx + rx * (to - mid),
            Y1 = cy + ry * (to - mid),
            Z = groundZ.Value,
            Mm = pen.Mm,
            Ink = pen.Color
        };
    }
}
