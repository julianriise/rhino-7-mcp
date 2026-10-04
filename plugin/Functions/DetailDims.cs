using System;
using System.Collections.Generic;
using System.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// A detail drawing's dimensions, as PlanDims.FixedChains in the drawing's
/// frame (model mm): the stops come from the records, never from the drawn
/// lines, and every value is a whole mm. PlanDims.LayoutFixed places them.
/// Pure, no RhinoCommon.
/// </summary>
public static class DetailDims
{
    /// <summary>The thickness chain sits this far in from the plain end of its face, clear of the joint.</summary>
    public const double ThicknessInsetMm = 300;

    /// <summary>
    /// A plan detail's chains. A wall: on each face, row 1 from the face's
    /// start through each opening's jambs to its end, and row 2 the face's
    /// length when row 1 has more than two stops; then its thickness across,
    /// at the end farther from any opening. An opening: on each face, its
    /// width from jamb to jamb, with the wall's end when that lies inside the
    /// crop; then the thickness beside one jamb.
    /// </summary>
    public static List<PlanDims.FixedChain> PlanChains(Details.Facts facts, Details.Drawing drawing)
    {
        var chains = new List<PlanDims.FixedChain>();
        if (facts?.Run == null || drawing == null) return chains;
        foreach (var outer in new[] { true, false })
        {
            Details.FaceExtent(facts, outer, out var lo, out var hi);
            var stops = new List<double>();
            if (facts.IsWall)
            {
                stops.Add(lo);
                foreach (var hosted in facts.Openings)
                {
                    stops.Add(hosted.U - hosted.Opening.Width / 2.0);
                    stops.Add(hosted.U + hosted.Opening.Width / 2.0);
                }
                stops.Add(hi);
            }
            else
            {
                var a = facts.Opening.U - facts.Opening.Opening.Width / 2.0;
                var b = facts.Opening.U + facts.Opening.Opening.Width / 2.0;
                if (Inside(drawing, Details.U(facts, lo))) stops.Add(lo);
                stops.Add(a);
                stops.Add(b);
                if (Inside(drawing, Details.U(facts, hi))) stops.Add(hi);
            }
            var face = outer ? "outer" : "inner";
            var row1 = Face(facts, outer, face, stops.Where(t => t >= lo - 0.5 && t <= hi + 0.5), 1);
            chains.Add(row1);
            if (facts.IsWall && row1.Stops.Count > 2)
                chains.Add(Face(facts, outer, face + "_overall", new[] { lo, hi }, 2));
        }
        chains.Add(Thickness(facts, ThicknessAlong(facts)));
        return chains;
    }

    /// <summary>
    /// Where the thickness is measured, along the run: a wall's at the end
    /// farther from its openings, ThicknessInsetMm in from where its inner
    /// face ends; an opening's just past one jamb.
    /// </summary>
    public static double ThicknessAlong(Details.Facts facts)
    {
        if (!facts.IsWall)
            return facts.Opening.U - facts.Opening.Opening.Width / 2.0 - ThicknessInsetMm;
        Details.FaceExtent(facts, false, out var lo, out var hi);
        if (facts.Openings.Count == 0) return hi - ThicknessInsetMm;
        var first = facts.Openings.Min(o => o.U - o.Opening.Width / 2.0);
        var last = facts.Openings.Max(o => o.U + o.Opening.Width / 2.0);
        return hi - last >= first - lo ? hi - ThicknessInsetMm : lo + ThicknessInsetMm;
    }

    static PlanDims.FixedChain Face(Details.Facts facts, bool outer, string kind, IEnumerable<double> along, int row)
    {
        var v = outer ? Details.OuterV(facts) : Details.InnerV(facts);
        var outward = outer ? facts.Outer : -facts.Outer;
        var chain = new PlanDims.FixedChain
        {
            Kind = kind,
            Id = facts.Record?.Id + "." + kind,
            Origin = new Pt(0, v),
            Dir = new Pt(1, 0),
            Out = new Pt(0, outward),
            Row = row
        };
        foreach (var u in along.Select(t => Details.U(facts, t)).OrderBy(u => u))
        {
            if (chain.Stops.Count > 0 && Math.Abs(u - chain.Stops[chain.Stops.Count - 1]) < 1.0) continue;
            chain.Stops.Add(u);
            chain.StopIds.Add(null);
        }
        return chain;
    }

    static PlanDims.FixedChain Thickness(Details.Facts facts, double along)
    {
        var u = Details.U(facts, along);
        return new PlanDims.FixedChain
        {
            Kind = "thickness",
            Id = facts.Record?.Id + ".thickness",
            Origin = new Pt(u, 0),
            Dir = new Pt(0, 1),
            Out = new Pt(1, 0),
            Stops = { facts.Run.Near, facts.Run.Far },
            StopIds = { null, null },
            Reach = ThicknessInsetMm,
            Row = 1
        };
    }

    static bool Inside(Details.Drawing drawing, double u) => u > drawing.U0 + 1.0 && u < drawing.U1 - 1.0;

    /// <summary>The values a chain prints, from stop to stop.</summary>
    public static List<int> Values(PlanDims.FixedChain chain)
    {
        var values = new List<int>();
        for (var i = 0; i + 1 < chain.Stops.Count; i++)
            values.Add((int)Math.Round(chain.Stops[i + 1] - chain.Stops[i], MidpointRounding.AwayFromZero));
        return values;
    }
}
