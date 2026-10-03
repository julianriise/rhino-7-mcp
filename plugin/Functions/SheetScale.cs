using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// One scale for the whole set, from the standard list. With no scale asked,
/// the set takes the largest scale (the smallest denominator) at which every
/// drawing fits. A named scale is one of that list: it is the finest the set
/// may use, and the whole set steps up the list when a sheet does not fit.
/// An empty set stays at <see cref="FirstStep"/>. Spans are model mm, the
/// detail paper mm. Pure, no Rhino document, so it tests headless.
/// </summary>
public static class SheetScale
{
    /// <summary>The one list of scales a set prints at, finest first.</summary>
    public static readonly IReadOnlyList<int> Ladder = new[]
    {
        5, 10, 20, 25, 50, 75, 100, 125, 150, 175, 200, 250, 300, 350, 400, 500, 750, 1000
    };

    /// <summary>
    /// The scale when nothing is drawn, and the first bake before a pick.
    /// Not the finest step: an empty drawing must not print at 1:5.
    /// </summary>
    public const int FirstStep = 100;

    /// <summary>A drawing fills at most this share of the detail on its tighter side.</summary>
    public const double FillShare = 0.9;

    /// <summary>"1:5, 1:10, …", the list a prompt or a tool can quote.</summary>
    public static string LadderText =>
        string.Join(", ", Ladder.Select(step => "1:" + step.ToString(CultureInfo.InvariantCulture)));

    /// <summary>"5, 10, …", the denominators a scale parameter takes.</summary>
    public static string Denominators =>
        string.Join(", ", Ladder.Select(step => step.ToString(CultureInfo.InvariantCulture)));

    public readonly struct Span
    {
        public Span(double width, double height)
        {
            Width = width;
            Height = height;
        }

        public double Width { get; }
        public double Height { get; }
    }

    public sealed class Result
    {
        /// <summary>The set's scale: the one the receipt names, and the one every sheet prints at.</summary>
        public int Scale;
        /// <summary>Each drawing's scale, in the order given. All equal <see cref="Scale"/>.</summary>
        public int[] Scales;

        /// <summary>Always empty: a set no longer prints one sheet at another scale.</summary>
        public int[] Bumped
        {
            get { return Enumerable.Range(0, Scales.Length).Where(i => Scales[i] != Scale).ToArray(); }
        }
    }

    /// <summary>The scale denominator at which the span fills FillShare of the detail on its tighter side. 0 for an empty span.</summary>
    public static double Need(Span span, Span detail)
    {
        if (span.Width <= 0 || span.Height <= 0 || detail.Width <= 0 || detail.Height <= 0) return 0;
        return Math.Max(span.Width / (detail.Width * FillShare), span.Height / (detail.Height * FillShare));
    }

    public static bool Fits(Span span, Span detail, int scale)
    {
        // Float noise just above a step (100.0000000001) still fits it.
        return Need(span, detail) <= scale + 1e-9;
    }

    /// <summary>The smallest listed step at or above <paramref name="scale"/>, or the last step when it is coarser than the list.</summary>
    public static int Listed(int scale)
    {
        foreach (var step in Ladder)
            if (step >= scale) return step;
        return Ladder[Ladder.Count - 1];
    }

    /// <summary>"Fit" and "0" are 0. "1:200" and "200" are 200. Anything else is 0.</summary>
    public static int Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var t = text.Trim();
        if (t.Equals("Fit", StringComparison.OrdinalIgnoreCase) || t == "0") return 0;
        if (t.StartsWith("1:", StringComparison.Ordinal)) t = t.Substring(2).Trim();
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;
    }

    /// <summary>
    /// The set's one scale. floor holds the scales the drawings were last
    /// baked at: the set never steps back down from the coarsest of those,
    /// so a second pick only steps up.
    /// </summary>
    public static Result Pick(IList<Span> drawings, Span detail, int? asked, IList<int> floor = null)
    {
        drawings = drawings ?? new Span[0];
        var floorMax = 0;
        var any = false;
        for (var i = 0; i < drawings.Count; i++)
        {
            if (floor != null && i < floor.Count && floor[i] > floorMax) floorMax = floor[i];
            if (Need(drawings[i], detail) > 0) any = true;
        }
        int from;
        if (asked.HasValue && asked.Value >= 1)
            from = Listed(asked.Value);
        else if (!any)
            from = FirstStep;
        else
            from = Ladder[0];
        if (floorMax > 0) from = Math.Max(from, Listed(floorMax));
        var scale = any || (asked.HasValue && asked.Value >= 1) ? FitAll(drawings, detail, from) : from;
        var result = new Result { Scale = scale, Scales = new int[drawings.Count] };
        for (var i = 0; i < drawings.Count; i++)
            result.Scales[i] = scale;
        return result;
    }

    /// <summary>
    /// The receipt's one clause for the sheets that print at another scale:
    /// " Section A–A at 1:500 to fit." Empty when there are none.
    /// </summary>
    public static string Clause(IList<string> titles, IList<int> scales)
    {
        if (titles == null || titles.Count == 0) return "";
        var parts = new List<string>();
        for (var i = 0; i < titles.Count; i++)
        {
            var sameAsNext = i + 1 < titles.Count && scales[i + 1] == scales[i];
            parts.Add(titles[i] + (sameAsNext ? "" : " at 1:" + scales[i].ToString(CultureInfo.InvariantCulture)));
        }
        var text = parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
        return " " + text + " to fit.";
    }

    /// <summary>The smallest listed step at or above <paramref name="from"/> where every drawing fits, else the last step.</summary>
    static int FitAll(IList<Span> drawings, Span detail, int from)
    {
        foreach (var step in Ladder)
        {
            if (step < from) continue;
            var fits = true;
            for (var i = 0; i < drawings.Count; i++)
                if (!Fits(drawings[i], detail, step)) fits = false;
            if (fits) return step;
        }
        return Ladder[Ladder.Count - 1];
    }
}
