using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The Analysis set's daylight map sheet (2026-10-08): the plan drawing with
/// the last daylight run's map under its lines, a legend for the DF ramp, and
/// each room's mean by its tag. Print never shows the daylight mesh (it is off
/// in every detail), so the PDF draws the mesh's cells as a picture of its
/// own, placed where the plan's detail puts the model. The ramp is the
/// tracer's (forsk_daylight.py, SKY_STOPS and DF_LOW to DF_HIGH), printed
/// lighter so black lines and tags read over the darkest cell. Pure, no
/// Rhino document, so it tests headless. Sizes are paper millimetres.
/// </summary>
public static class DaylightPrint
{
    /// <summary>The ramp's ends, as the tracer's DF_LOW and DF_HIGH: log between them.</summary>
    public const double DfLow = 0.1;
    public const double DfHigh = 10.0;
    /// <summary>How far each colour moves toward white on paper.</summary>
    public const double PrintTint = 0.4;
    /// <summary>About 150 dpi, as the page previews print.</summary>
    public const double PxPerMm = 6.0;
    /// <summary>An A1 plan stays a few megabytes.</summary>
    public const int MaxPixels = 6_000_000;
    /// <summary>The legend's column at the right of the detail.</summary>
    public const double ColumnMm = 40.0;
    public const double BarWidthMm = 6.0;
    public const double BarHeightMm = 60.0;
    public const double TextMm = 2.5;
    /// <summary>
    /// The plan bake's model-to-drawing map (doc strings, section forsk): the
    /// plan drawing sits where its bake moved it, not at the model's place.
    /// </summary>
    public const string PlanMapEntry = "plan model_to_drawing";
    /// <summary>The page object around the legend's ramp, and the paper box the PDF draws it in.</summary>
    public const string RampRole = "daylight_ramp";
    public const string RampBoxKey = "forsk:ramp_box";

    /// <summary>The DFs the legend names, low to high.</summary>
    public static readonly double[] LegendTicks = { 0.1, 0.5, 1, 2, 5, 10 };

    static readonly double[] StopT = { 0.0, 0.33, 0.66, 1.0 };
    static readonly byte[][] StopRgb =
    {
        new byte[] { 0x0B, 0x25, 0x45 },
        new byte[] { 0x2F, 0x66, 0x90 },
        new byte[] { 0x8F, 0xBC, 0xE6 },
        new byte[] { 0xF2, 0xF8, 0xFD }
    };

    /// <summary>Where a DF % sits on the ramp, 0–1: log from DfLow to DfHigh.</summary>
    public static double Shade(double df)
    {
        if (df <= DfLow) return 0;
        return Math.Min(Math.Log(df / DfLow) / Math.Log(DfHigh / DfLow), 1.0);
    }

    /// <summary>The ramp's colour at t, linear between the stops, as the tracer paints the mesh.</summary>
    public static byte[] Rgb(double t)
    {
        t = Math.Min(Math.Max(t, 0), 1);
        var i = 0;
        while (i < StopT.Length - 2 && t > StopT[i + 1]) i++;
        var u = (t - StopT[i]) / ((StopT[i + 1] - StopT[i]) == 0 ? 1 : StopT[i + 1] - StopT[i]);
        var rgb = new byte[3];
        for (var c = 0; c < 3; c++)
            rgb[c] = (byte)Math.Round(StopRgb[i][c] + (StopRgb[i + 1][c] - StopRgb[i][c]) * u, MidpointRounding.ToEven);
        return rgb;
    }

    /// <summary>A channel as it prints: PrintTint of the way to white.</summary>
    public static byte Tint(byte channel) => (byte)Math.Round(channel + (255 - channel) * PrintTint, MidpointRounding.AwayFromZero);

    /// <summary>The ramp's colour at t as it prints.</summary>
    public static byte[] PrintRgb(double t) => Rgb(t).Select(Tint).ToArray();

    /// <summary>"DF 2.1 %": a room's mean beside its tag.</summary>
    public static string RoomLabel(double df) => "DF " + df.ToString("0.0", CultureInfo.InvariantCulture) + " %";

    /// <summary>"0.5 %", "10 %": a legend tick.</summary>
    public static string TickLabel(double df) => df.ToString("0.#", CultureInfo.InvariantCulture) + " %";

    /// <summary>The legend in the column whose left edge is x0 and top is top: a title, the ramp upright, a label at each tick.</summary>
    public sealed class Legend
    {
        public double TitleX, TitleY;
        public double BarX0, BarY0, BarX1, BarY1;
        public List<(double Y, string Label)> Ticks = new List<(double, string)>();
        /// <summary>Under the bar: the run's line.</summary>
        public double NoteY;
    }

    public static Legend LegendAt(double x0, double top)
    {
        var legend = new Legend { TitleX = x0 + 4, TitleY = top - TextMm };
        legend.BarX0 = x0 + 4;
        legend.BarX1 = legend.BarX0 + BarWidthMm;
        legend.BarY1 = legend.TitleY - 2 * TextMm;
        legend.BarY0 = legend.BarY1 - BarHeightMm;
        foreach (var df in LegendTicks)
            legend.Ticks.Add((legend.BarY0 + Shade(df) * BarHeightMm, TickLabel(df)));
        legend.NoteY = legend.BarY0 - 2 * TextMm;
        return legend;
    }

    /// <summary>The ramp as it prints, one pixel wide, steps tall, 10 % on the top row.</summary>
    public static OfficeLogo.Picture RampPicture(int steps = 128)
    {
        steps = Math.Max(2, steps);
        var rgb = new byte[steps * 3];
        for (var row = 0; row < steps; row++)
        {
            var colour = PrintRgb(1.0 - (double)row / (steps - 1));
            Array.Copy(colour, 0, rgb, row * 3, 3);
        }
        return new OfficeLogo.Picture { Width = 1, Height = steps, Rgb = rgb };
    }

    /// <summary>first, then second: a model point to the drawing, then the drawing to paper.</summary>
    public static SheetFlat.Affine Then(SheetFlat.Affine first, SheetFlat.Affine second) => new SheetFlat.Affine
    {
        A = second.A * first.A + second.B * first.D,
        B = second.A * first.B + second.B * first.E,
        C = second.A * first.C + second.B * first.F + second.C,
        D = second.D * first.A + second.E * first.D,
        E = second.D * first.B + second.E * first.E,
        F = second.D * first.C + second.E * first.F + second.F
    };

    public static string FormatAffine(SheetFlat.Affine a) =>
        string.Join(",", new[] { a.A, a.B, a.C, a.D, a.E, a.F }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    public static bool TryAffine(string text, out SheetFlat.Affine a)
    {
        a = SheetFlat.Affine.Identity;
        var parts = (text ?? "").Split(',');
        if (parts.Length != 6) return false;
        var v = new double[6];
        for (var i = 0; i < 6; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
        a = new SheetFlat.Affine { A = v[0], B = v[1], C = v[2], D = v[3], E = v[4], F = v[5] };
        return true;
    }

    /// <summary>A mesh triangle on paper with its printed colour.</summary>
    public sealed class Tri
    {
        public double X0, Y0, X1, Y1, X2, Y2;
        public byte R, G, B;
    }

    /// <summary>The picture and the paper box it covers.</summary>
    public sealed class Raster
    {
        public OfficeLogo.Picture Picture;
        public double X0, Y0, X1, Y1;
    }

    /// <summary>
    /// The triangles as one picture over their paper box, pxPerMm square
    /// pixels (fewer when it would pass maxPixels). A pixel takes the colour of
    /// the triangle its centre is in; outside every triangle it is clear, so
    /// the paper shows. Null when there is nothing to draw.
    /// </summary>
    public static Raster Rasterise(IList<Tri> tris, double pxPerMm = PxPerMm, int maxPixels = MaxPixels)
    {
        if (tris == null || tris.Count == 0 || pxPerMm <= 0) return null;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var t in tris)
        {
            x0 = Math.Min(x0, Math.Min(t.X0, Math.Min(t.X1, t.X2)));
            y0 = Math.Min(y0, Math.Min(t.Y0, Math.Min(t.Y1, t.Y2)));
            x1 = Math.Max(x1, Math.Max(t.X0, Math.Max(t.X1, t.X2)));
            y1 = Math.Max(y1, Math.Max(t.Y0, Math.Max(t.Y1, t.Y2)));
        }
        if (!(x1 > x0) || !(y1 > y0)) return null;
        var px = pxPerMm;
        int w, h;
        // A whole pixel more than the box: round the pixel count, never cut a cell.
        int Cols(double p) => Math.Max(1, (int)Math.Ceiling((x1 - x0) * p - 1e-9));
        int Rows(double p) => Math.Max(1, (int)Math.Ceiling((y1 - y0) * p - 1e-9));
        w = Cols(px);
        h = Rows(px);
        if ((long)w * h > maxPixels)
        {
            px *= Math.Sqrt((double)maxPixels / ((long)w * h));
            w = Cols(px);
            h = Rows(px);
            while ((long)w * h > maxPixels && px > 1e-6)
            {
                px *= 0.995;
                w = Cols(px);
                h = Rows(px);
            }
        }
        var top = y0 + h / px;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = 255;
        var alpha = new byte[w * h];
        foreach (var t in tris)
        {
            var area = (t.X1 - t.X0) * (t.Y2 - t.Y0) - (t.X2 - t.X0) * (t.Y1 - t.Y0);
            if (Math.Abs(area) < 1e-12) continue;
            var tx0 = Math.Min(t.X0, Math.Min(t.X1, t.X2));
            var tx1 = Math.Max(t.X0, Math.Max(t.X1, t.X2));
            var ty0 = Math.Min(t.Y0, Math.Min(t.Y1, t.Y2));
            var ty1 = Math.Max(t.Y0, Math.Max(t.Y1, t.Y2));
            var c0 = Math.Max(0, (int)Math.Floor((tx0 - x0) * px - 0.5));
            var c1 = Math.Min(w - 1, (int)Math.Ceiling((tx1 - x0) * px - 0.5));
            var r0 = Math.Max(0, (int)Math.Floor((top - ty1) * px - 0.5));
            var r1 = Math.Min(h - 1, (int)Math.Ceiling((top - ty0) * px - 0.5));
            // Barycentric weights, a hair over the edges so cells that share one leave no seam.
            var eps = -1e-9 * Math.Abs(area);
            for (var r = r0; r <= r1; r++)
            {
                var y = top - (r + 0.5) / px;
                for (var c = c0; c <= c1; c++)
                {
                    var x = x0 + (c + 0.5) / px;
                    var a = ((t.X1 - x) * (t.Y2 - y) - (t.X2 - x) * (t.Y1 - y)) * Math.Sign(area);
                    var b = ((t.X2 - x) * (t.Y0 - y) - (t.X0 - x) * (t.Y2 - y)) * Math.Sign(area);
                    var g = ((t.X0 - x) * (t.Y1 - y) - (t.X1 - x) * (t.Y0 - y)) * Math.Sign(area);
                    if (a < eps || b < eps || g < eps) continue;
                    var at = r * w + c;
                    rgb[at * 3] = t.R;
                    rgb[at * 3 + 1] = t.G;
                    rgb[at * 3 + 2] = t.B;
                    alpha[at] = 255;
                }
            }
        }
        return new Raster
        {
            Picture = new OfficeLogo.Picture { Width = w, Height = h, Rgb = rgb, Alpha = alpha },
            X0 = x0,
            Y0 = y0,
            X1 = x0 + w / px,
            Y1 = top
        };
    }
}
