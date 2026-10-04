using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>One pen: its paper weight in mm and its colour.</summary>
public readonly struct PrintPen
{
    public PrintPen(double mm, Color color)
    {
        Mm = mm;
        Color = color;
    }

    public double Mm { get; }
    public Color Color { get; }
}

/// <summary>
/// F5.4 print profile: what the F5.0 weight tiers, the dashed linetype, the
/// annotation ink and the section poché look like on a sheet. Pure data, no
/// RhinoCommon, so the headless tests pin the default byte for byte (the
/// numbers F5.0 shipped with) and check an alternative is applied.
///
/// A profile is a look, not geometry: the same strokes and loops are drawn,
/// only their paper weight, colour and the poché fill change. The profile
/// drives the plan sheet, the section sheets and the schedule rules. Print
/// chrome (frame, title block, scale bar, north arrow) stays black.
/// </summary>
public sealed class PrintProfile
{
    public string Name { get; set; }
    public string Label { get; set; }
    /// <summary>Cut: the plan and section outline of what the plane cuts, and the section ground line.</summary>
    public PrintPen Cut { get; set; }
    /// <summary>Silhouette: the elevation silhouette and cut edges, and the dimension ticks.</summary>
    public PrintPen Silhouette { get; set; }
    /// <summary>Beyond: everything seen past the cut, and the elevation lines.</summary>
    public PrintPen Beyond { get; set; }
    /// <summary>Thin: opening symbols, dimension lines, level marks, marker strokes.</summary>
    public PrintPen Thin { get; set; }
    /// <summary>The colour of every dashed line: the overhead roof outline, an opening above the cut.</summary>
    public Color Dashed { get; set; }
    /// <summary>Room tags, opening marks, dimension text, section letters, leader dots and arrows.</summary>
    public Color Text { get; set; }
    /// <summary>The poché fill colour.</summary>
    public Color Poche { get; set; }
    /// <summary>The poché hatch pattern: Solid, or the Rhino pattern Hatch1 (45° lines).</summary>
    public string PochePattern { get; set; }
    /// <summary>Paper spacing of the poché hatch lines in mm. Ignored for Solid.</summary>
    public double PocheSpacingMm { get; set; }

    public bool PocheSolid => string.Equals(PochePattern, PrintProfiles.SolidPattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>The profile as the tool returns it: name, label and every pen.</summary>
    public JObject Record()
    {
        JObject Pen(PrintPen pen) => new JObject
        {
            ["mm"] = pen.Mm,
            ["rgb"] = PrintProfiles.Rgb(pen.Color)
        };
        return new JObject
        {
            ["name"] = Name,
            ["label"] = Label,
            ["cut"] = Pen(Cut),
            ["silhouette"] = Pen(Silhouette),
            ["beyond"] = Pen(Beyond),
            ["thin"] = Pen(Thin),
            ["dashed"] = PrintProfiles.Rgb(Dashed),
            ["text"] = PrintProfiles.Rgb(Text),
            ["poche"] = new JObject
            {
                ["rgb"] = PrintProfiles.Rgb(Poche),
                ["pattern"] = PochePattern,
                ["spacing_mm"] = PocheSpacingMm
            }
        };
    }
}

/// <summary>
/// The shipped profiles and the one a drawing pass uses. The choice lives in
/// the document (one string), not in the user's settings: a sheet set is a
/// property of the job, so the file reopens, and goes to a colleague, with the
/// look it was drawn with. The drawing code reads <see cref="Active"/>, which
/// the layout pass sets once from the document (see UseProfile).
/// </summary>
public static class PrintProfiles
{
    /// <summary>Document strings section and key that hold the profile name.</summary>
    public const string MetaSection = "forsk";
    public const string MetaKey = "print_profile";
    public const string SolidPattern = "Solid";
    public const string HatchPattern = "Hatch1";
    public const string DefaultName = "default";

    static PrintProfile _active = Default;

    /// <summary>The profile the current drawing pass uses. Never null.</summary>
    public static PrintProfile Active
    {
        get => _active ?? Default;
        set => _active = value ?? Default;
    }

    /// <summary>
    /// Solid black poché, black pens, the F5.0 tiers 0.50 / 0.35 / 0.18 / 0.13 mm. The same
    /// numbers the plan, the sections and the elevations were drawn with before profiles.
    /// </summary>
    public static PrintProfile Default => new PrintProfile
    {
        Name = DefaultName,
        Label = "Default: solid black poché, black lines",
        Cut = new PrintPen(0.50, Color.Black),
        Silhouette = new PrintPen(0.35, Color.Black),
        Beyond = new PrintPen(0.18, Color.Black),
        Thin = new PrintPen(0.13, Color.Black),
        Dashed = Color.Black,
        Text = Color.Black,
        Poche = Color.Black,
        PochePattern = SolidPattern,
        PocheSpacingMm = 0
    };

    /// <summary>Grey poché and grey lines. The cut is darker than what lies beyond. No saturated ink.</summary>
    public static PrintProfile Grey => new PrintProfile
    {
        Name = "grey",
        Label = "Grey poché, grey lines",
        Cut = new PrintPen(0.50, Color.FromArgb(30, 30, 30)),
        Silhouette = new PrintPen(0.35, Color.FromArgb(30, 30, 30)),
        Beyond = new PrintPen(0.18, Color.FromArgb(110, 110, 110)),
        Thin = new PrintPen(0.13, Color.FromArgb(150, 150, 150)),
        Dashed = Color.FromArgb(130, 130, 130),
        Text = Color.Black,
        Poche = Color.FromArgb(150, 150, 150),
        PochePattern = SolidPattern,
        PocheSpacingMm = 0
    };

    /// <summary>Hatched poché, a lighter cut: 45° lines 1.5 mm apart, a 0.35 mm cut outline.</summary>
    public static PrintProfile Hatched => new PrintProfile
    {
        Name = "hatch",
        Label = "Hatched poché, lighter cut line",
        Cut = new PrintPen(0.35, Color.Black),
        Silhouette = new PrintPen(0.25, Color.Black),
        Beyond = new PrintPen(0.18, Color.Black),
        Thin = new PrintPen(0.13, Color.Black),
        Dashed = Color.Black,
        Text = Color.Black,
        Poche = Color.FromArgb(60, 60, 60),
        PochePattern = HatchPattern,
        PocheSpacingMm = 1.5
    };

    /// <summary>Every shipped profile, the default first.</summary>
    public static IReadOnlyList<PrintProfile> All => new[] { Default, Grey, Hatched };

    /// <summary>
    /// A profile by name, or by what a user would say: grey / gray / grå, hatch / skravur,
    /// default / standard / black / svart. Null when nothing matches.
    /// </summary>
    public static PrintProfile Find(string name)
    {
        var key = Squash(name);
        if (key.Length == 0) return null;
        foreach (var profile in All)
            if (key == profile.Name) return profile;
        switch (key)
        {
            case "standard": case "black": case "svart": case "sort": case "solid":
                return Default;
            case "gray": case "gra": case "grey": case "colour": case "color": case "colours": case "colors":
                return Grey;
            case "hatched": case "hatching": case "skravur": case "skravert":
                return Hatched;
        }
        return null;
    }

    /// <summary>The profile the document stores. An unset or unknown name is the default.</summary>
    public static PrintProfile FromStored(string stored)
    {
        return Find(stored) ?? Default;
    }

    /// <summary>The profile after this one, wrapping: what the panel's profile button picks next.</summary>
    public static PrintProfile Next(PrintProfile current)
    {
        var all = All;
        for (var i = 0; i < all.Count; i++)
            if (string.Equals(all[i].Name, current?.Name, StringComparison.Ordinal))
                return all[(i + 1) % all.Count];
        return all[0];
    }

    /// <summary>
    /// The distance between Hatch1's lines at pattern scale 1, in model units. RhinoCommon 7 does
    /// not expose a pattern's lines, so this is an assumption until the first live look at the
    /// hatch profile's sheet pins it: if the lines sit too far or too close, correct it here.
    /// </summary>
    public const double HatchLineSpacingAtScaleOne = 1.0;

    /// <summary>
    /// The pattern scale that puts the poché hatch lines the profile's paper spacing apart on a
    /// sheet at 1:<paramref name="sheetScale"/>. 1 for Solid, so a solid fill never depends on it.
    /// </summary>
    public static double HatchScale(PrintProfile profile, int sheetScale)
    {
        if (profile == null || profile.PocheSolid || profile.PocheSpacingMm <= 0)
            return 1.0;
        return profile.PocheSpacingMm * Math.Max(sheetScale, 1) / HatchLineSpacingAtScaleOne;
    }

    /// <summary>The finest scale denominator drawn with the plan's pens. Finer, a detail's lines are heavier.</summary>
    public const int DetailPenScale = 20;

    /// <summary>
    /// The profile at 1:<paramref name="scale"/>. At 1:20 and finer the cut is
    /// at least 0.70, the silhouette 0.50 and what lies beyond 0.25, so a
    /// detail reads heavier than the plan. Thin lines, dimensions and text
    /// stay as they are: annotation does not scale. Coarser, the profile.
    /// </summary>
    public static PrintProfile AtScale(PrintProfile profile, int scale)
    {
        profile = profile ?? Default;
        if (scale < 1 || scale > DetailPenScale) return profile;
        PrintPen AtLeast(PrintPen pen, double mm) => new PrintPen(Math.Max(pen.Mm, mm), pen.Color);
        return new PrintProfile
        {
            Name = profile.Name,
            Label = profile.Label,
            Cut = AtLeast(profile.Cut, 0.70),
            Silhouette = AtLeast(profile.Silhouette, 0.50),
            Beyond = AtLeast(profile.Beyond, 0.25),
            Thin = profile.Thin,
            Dashed = profile.Dashed,
            Text = profile.Text,
            Poche = profile.Poche,
            PochePattern = profile.PochePattern,
            PocheSpacingMm = profile.PocheSpacingMm
        };
    }

    /// <summary>"r,g,b" the way the plugin logs colours.</summary>
    public static string Rgb(Color color)
    {
        return color.R.ToString(CultureInfo.InvariantCulture) + ","
            + color.G.ToString(CultureInfo.InvariantCulture) + ","
            + color.B.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The names a user may pick, as the tool lists them.</summary>
    public static JArray Available()
    {
        return new JArray(All.Select(p => new JObject { ["name"] = p.Name, ["label"] = p.Label }));
    }

    static string Squash(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Trim().ToLowerInvariant().Where(char.IsLetter).ToArray();
        return new string(chars).Replace("å", "a");
    }
}
