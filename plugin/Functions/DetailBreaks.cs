using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Break lines for a long wall's plan detail, so it stays at 1:20: the
/// uniform middle stretches are cut out, each leaving a 200 mm gap, and the
/// dimensions keep their true values (a value across a break is underlined,
/// ISO 129-1). Keep zones are KeepMm around every stop (face ends, corners,
/// jambs, joins) and the crop ends, merged; a gap between them of at least
/// MinGapMm may go, the longest first (ties: lower u), until the drawing
/// fits. Along u, model mm. Pure, no RhinoCommon.
/// </summary>
public static class DetailBreaks
{
    public const double KeepMm = 600;
    public const double MinGapMm = 1000;
    /// <summary>What a break leaves: 10 paper mm at 1:20.</summary>
    public const double LeaveMm = 200;
    /// <summary>The scale a wall's plan detail keeps by breaking.</summary>
    public const int Scale = 20;

    public readonly struct Break
    {
        public Break(double u0, double u1)
        {
            U0 = u0;
            U1 = u1;
        }

        public double U0 { get; }
        public double U1 { get; }
        public double Removed => U1 - U0 - LeaveMm;
    }

    public sealed class Plan
    {
        public double Lo;
        public double Hi;
        public List<Break> Breaks = new List<Break>();
        /// <summary>Every candidate went and the drawing is still longer than it may be.</summary>
        public bool TooLong;

        /// <summary>A true u where it is drawn: shifted back by what each break before it removed.</summary>
        public double Map(double u)
        {
            var shift = 0.0;
            foreach (var b in Breaks)
            {
                if (u >= b.U1) shift += b.Removed;
                else if (u > b.U0) return b.U0 - shift + (u - b.U0) * LeaveMm / (b.U1 - b.U0);
            }
            return u - shift;
        }

        /// <summary>The span from a to b crosses a break: its value is not to scale.</summary>
        public bool Spans(double a, double b)
        {
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            return Breaks.Any(x => lo <= x.U0 + 1e-6 && hi >= x.U1 - 1e-6);
        }

        public double DrawnLength => Map(Hi) - Map(Lo);

        /// <summary>The stretches kept, lo to hi.</summary>
        public List<KeyValuePair<double, double>> Keep()
        {
            var keep = new List<KeyValuePair<double, double>>();
            var from = Lo;
            foreach (var b in Breaks)
            {
                keep.Add(new KeyValuePair<double, double>(from, b.U0));
                from = b.U1;
            }
            keep.Add(new KeyValuePair<double, double>(from, Hi));
            return keep;
        }
    }

    /// <summary>The keep zones: KeepMm around each stop, and from each crop end KeepMm inwards and out, merged.</summary>
    public static List<KeyValuePair<double, double>> Zones(IEnumerable<double> stops, double lo, double hi)
    {
        var raw = (stops ?? Enumerable.Empty<double>()).Concat(new[] { lo, hi })
            .Select(s => new KeyValuePair<double, double>(s - KeepMm, s + KeepMm))
            .OrderBy(z => z.Key)
            .ToList();
        var zones = new List<KeyValuePair<double, double>>();
        foreach (var z in raw)
        {
            if (zones.Count > 0 && z.Key <= zones[zones.Count - 1].Value)
            {
                var last = zones[zones.Count - 1];
                zones[zones.Count - 1] = new KeyValuePair<double, double>(last.Key, Math.Max(last.Value, z.Value));
            }
            else zones.Add(z);
        }
        return zones;
    }

    /// <summary>
    /// The breaks that bring lo..hi within avail: the candidate gaps between
    /// keep zones, longest first, until hi − lo − removed fits.
    /// </summary>
    public static Plan Make(IEnumerable<double> stops, double lo, double hi, double avail)
    {
        var plan = new Plan { Lo = lo, Hi = hi };
        if (hi - lo <= avail) return plan;
        var zones = Zones(stops, lo, hi);
        var gaps = new List<Break>();
        for (var i = 0; i + 1 < zones.Count; i++)
        {
            var g = new Break(zones[i].Value, zones[i + 1].Key);
            if (g.U1 - g.U0 >= MinGapMm) gaps.Add(g);
        }
        var length = hi - lo;
        foreach (var g in gaps.OrderByDescending(g => g.U1 - g.U0).ThenBy(g => g.U0))
        {
            if (length <= avail) break;
            plan.Breaks.Add(g);
            length -= g.Removed;
        }
        plan.Breaks.Sort((a, b) => a.U0.CompareTo(b.U0));
        plan.TooLong = length > avail;
        return plan;
    }
}
