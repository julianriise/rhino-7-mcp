using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 P2: one scale for the whole set, from the scales a Norwegian drawing
/// set uses. With no scale asked, the set takes the first of 1:100, 1:200
/// and 1:500 at which every drawing fits; 1:50 only when asked. An asked
/// scale holds for every sheet that fits it, and a sheet that does not goes
/// up the ladder on its own. Spans are model mm, the detail paper mm. Pure,
/// no Rhino document, so it tests headless.
/// </summary>
public static class SheetScale
{
    /// <summary>The one list of scales a set prints at. 50 only when asked.</summary>
    public static readonly IReadOnlyList<int> Ladder = new[] { 50, 100, 200, 500 };

    /// <summary>The first step without an ask.</summary>
    public const int FirstStep = 100;

    /// <summary>A drawing fills at most this share of the detail on its tighter side.</summary>
    public const double FillShare = 0.9;

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
        /// <summary>The set's scale: the one the receipt names.</summary>
        public int Scale;
        /// <summary>Each drawing's scale, in the order given.</summary>
        public int[] Scales;

        /// <summary>The drawings that print at another scale than the set's.</summary>
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

    /// <summary>
    /// The set's scale and each drawing's. floor holds the scales the
    /// drawings were last baked at: a drawing whose tags grew at a larger
    /// scale never steps back down, so a second pick only steps up.
    /// </summary>
    public static Result Pick(IList<Span> drawings, Span detail, int? asked, IList<int> floor = null)
    {
        drawings = drawings ?? new Span[0];
        int Floor(int i) => floor != null && i < floor.Count ? floor[i] : 0;
        var result = new Result { Scales = new int[drawings.Count] };
        if (asked.HasValue && asked.Value >= 1)
        {
            result.Scale = asked.Value;
            for (var i = 0; i < drawings.Count; i++)
                result.Scales[i] = Math.Max(StepAt(drawings[i], detail, asked.Value), Floor(i));
            return result;
        }
        var scale = FirstStep;
        for (var i = 0; i < drawings.Count; i++)
            scale = Math.Max(scale, Math.Max(StepAt(drawings[i], detail, FirstStep), Floor(i)));
        result.Scale = scale;
        for (var i = 0; i < drawings.Count; i++)
            result.Scales[i] = scale;
        return result;
    }

    /// <summary>
    /// The receipt's one clause for the sheets that print at another scale:
    /// " Snitt A–A at 1:500 to fit." Empty when there are none.
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

    /// <summary>
    /// The scale for one drawing: from, when it fits there, else the first
    /// ladder step above it that fits, else the last step.
    /// </summary>
    static int StepAt(Span span, Span detail, int from)
    {
        if (Fits(span, detail, from)) return from;
        foreach (var step in Ladder)
            if (step > from && Fits(span, detail, step)) return step;
        return Math.Max(from, Ladder[Ladder.Count - 1]);
    }
}
