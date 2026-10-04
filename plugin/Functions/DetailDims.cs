using System;
using System.Collections.Generic;
using System.Globalization;
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
        if (drawing.Breaks != null)
            foreach (var chain in chains) Break(chain, drawing.Breaks);
        return chains;
    }

    /// <summary>
    /// A chain drawn across a plan's breaks: its stops where they are drawn,
    /// its values true, and a value whose span crosses a break underlined.
    /// </summary>
    public static void Break(PlanDims.FixedChain chain, DetailBreaks.Plan breaks)
    {
        if (chain == null || breaks == null) return;
        if (Math.Abs(chain.Dir.Y) > 0.5)
        {
            chain.Origin = new Pt(breaks.Map(chain.Origin.X), chain.Origin.Y);
            return;
        }
        chain.Values = Values(chain);
        chain.Underline = new List<bool>();
        for (var i = 0; i + 1 < chain.Stops.Count; i++)
            chain.Underline.Add(breaks.Spans(chain.Stops[i], chain.Stops[i + 1]));
        chain.Stops = chain.Stops.Select(breaks.Map).ToList();
    }

    /// <summary>Stops closer than this are one: a frame set 1 mm into the wall shows only the thickness.</summary>
    public const double MergeMm = 5;

    /// <summary>A drawing's chains, by its view.</summary>
    public static List<PlanDims.FixedChain> Chains(Details.Facts facts, Details.Drawing drawing)
    {
        if (facts?.Run == null || drawing == null) return new List<PlanDims.FixedChain>();
        if (drawing.View == Details.Plan) return PlanChains(facts, drawing);
        if (drawing.View == Details.Elevation) return ElevationChains(facts, drawing);
        return facts.IsWall ? SectionChains(facts, drawing) : OpeningSectionChains(facts, drawing);
    }

    /// <summary>
    /// A wall section's chains: its thickness across, below the slab; up its
    /// outer face, row 1 slab underside → floor top → wall top (→ roof top),
    /// row 2 the overall.
    /// </summary>
    public static List<PlanDims.FixedChain> SectionChains(Details.Facts facts, Details.Drawing drawing)
    {
        var heights = new List<double> { facts.SlabBottom, facts.FloorTop, facts.WallTop };
        if (facts.RoofTop.HasValue) heights.Add(facts.RoofTop.Value);
        return Vertical(facts, drawing, "section", Across(facts, drawing, false), heights, new[] { facts.SlabBottom, heights.Max() });
    }

    /// <summary>
    /// An opening section's chains: across, below the slab, outer face →
    /// frame outer → frame inner → inner face; up its outer face, row 1 slab
    /// underside → floor top → sill → head → wall top (→ roof top), row 2
    /// slab underside → wall top.
    /// </summary>
    public static List<PlanDims.FixedChain> OpeningSectionChains(Details.Facts facts, Details.Drawing drawing)
    {
        var o = facts.Opening.Opening;
        var heights = new List<double> { facts.SlabBottom, facts.FloorTop, o.Sill, o.Head, facts.WallTop };
        if (facts.RoofTop.HasValue) heights.Add(facts.RoofTop.Value);
        return Vertical(facts, drawing, "section", Across(facts, drawing, true), heights, new[] { facts.SlabBottom, facts.WallTop });
    }

    /// <summary>
    /// An opening elevation's chains: its width below the floor line; beside
    /// it, row 1 floor top → sill → head, row 2 floor top → head when row 1
    /// has a sill stop.
    /// </summary>
    public static List<PlanDims.FixedChain> ElevationChains(Details.Facts facts, Details.Drawing drawing)
    {
        var o = facts.Opening.Opening;
        var a = Details.AlongU(facts, drawing, facts.Opening.U - o.Width / 2.0);
        var b = Details.AlongU(facts, drawing, facts.Opening.U + o.Width / 2.0);
        var chains = new List<PlanDims.FixedChain>
        {
            Line(facts, "width", new Pt(0, facts.FloorTop), new Pt(1, 0), new Pt(0, -1), new[] { a, b }, 1)
        };
        var side = new Pt(Math.Max(a, b), 0);
        var row1 = Line(facts, "height", side, new Pt(0, 1), new Pt(1, 0), new[] { facts.FloorTop, o.Sill, o.Head }, 1);
        chains.Add(row1);
        if (row1.Stops.Count > 2)
            chains.Add(Line(facts, "height_overall", side, new Pt(0, 1), new Pt(1, 0), new[] { facts.FloorTop, o.Head }, 2));
        return chains;
    }

    /// <summary>A level mark on a vertical drawing: "±0", "+2100", at z, on the side away from the dimensions.</summary>
    public sealed class Level
    {
        public string Text;
        public double Z;
        public double U;
        /// <summary>+1 when the mark points to +u from U, −1 to −u.</summary>
        public int Side;
    }

    /// <summary>
    /// A vertical drawing's level marks: ±0 and the wall top on a wall
    /// section; ±0, the sill (when above the floor) and the head on an
    /// opening section; ±0 on an elevation. They stand at the crop's edge
    /// away from the height chains, pointing out of it.
    /// </summary>
    public static List<Level> Levels(Details.Facts facts, Details.Drawing drawing)
    {
        var levels = new List<Level>();
        if (facts?.Run == null || drawing == null || !drawing.Vertical) return levels;
        var heights = new List<double> { facts.FloorTop };
        if (drawing.View == Details.Cut && facts.IsWall) heights.Add(facts.WallTop);
        else if (drawing.View == Details.Cut)
        {
            var o = facts.Opening.Opening;
            if (o.Sill - facts.FloorTop >= MergeMm) heights.Add(o.Sill);
            heights.Add(o.Head);
        }
        var dims = Chains(facts, drawing).First(c => Math.Abs(c.Dir.Y) > 0.5);
        var side = dims.Out.X > 0 ? -1 : 1;
        var u = side > 0 ? drawing.U1 : drawing.U0;
        foreach (var z in heights)
            levels.Add(new Level { Text = LevelText(z - facts.FloorTop), Z = z, U = u, Side = side });
        return levels;
    }

    /// <summary>"±0", "+2100", "-400": mm from the floor top.</summary>
    public static string LevelText(double mm)
    {
        var n = (int)Math.Round(mm, MidpointRounding.AwayFromZero);
        if (n == 0) return "±0";
        return (n > 0 ? "+" : "-") + Math.Abs(n).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The faces' u across a vertical drawing, outer first; an opening's frame faces between them.</summary>
    static List<double> Across(Details.Facts facts, Details.Drawing drawing, bool frame)
    {
        var outer = Details.FaceU(facts, drawing, Details.OuterV(facts));
        var inner = Details.FaceU(facts, drawing, Details.InnerV(facts));
        var list = new List<double> { outer };
        if (frame && OpeningElement.FrameInsetMm >= MergeMm)
        {
            var step = Math.Sign(inner - outer) * OpeningElement.FrameInsetMm;
            list.Add(outer + step);
            list.Add(inner - step);
        }
        list.Add(inner);
        return list;
    }

    /// <summary>A section's chains: across below the slab, and up the outer face in two rows.</summary>
    static List<PlanDims.FixedChain> Vertical(Details.Facts facts, Details.Drawing drawing, string kind,
        List<double> across, List<double> heights, IList<double> overall)
    {
        var outer = across[0];
        var outward = outer >= across[across.Count - 1] ? 1 : -1;
        var side = new Pt(outer, 0);
        var row1 = Line(facts, "height", side, new Pt(0, 1), new Pt(outward, 0), heights, 1);
        var chains = new List<PlanDims.FixedChain>
        {
            Line(facts, "thickness", new Pt(0, facts.SlabBottom), new Pt(1, 0), new Pt(0, -1), across, 1),
            row1
        };
        if (row1.Stops.Count > 2)
            chains.Add(Line(facts, "height_overall", side, new Pt(0, 1), new Pt(outward, 0), overall, 2));
        return chains;
    }

    /// <summary>A chain along dir from origin, its stops sorted and those closer than MergeMm made one.</summary>
    static PlanDims.FixedChain Line(Details.Facts facts, string kind, Pt origin, Pt dir, Pt outward, IEnumerable<double> stops, int row)
    {
        var chain = new PlanDims.FixedChain
        {
            Kind = kind,
            Id = facts.Record?.Id + "." + kind,
            Origin = origin,
            Dir = dir,
            Out = outward,
            Row = row
        };
        foreach (var t in stops.OrderBy(t => t))
        {
            if (chain.Stops.Count > 0 && t - chain.Stops[chain.Stops.Count - 1] < MergeMm) continue;
            chain.Stops.Add(t);
            chain.StopIds.Add(null);
        }
        return chain;
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
        if (chain.Values != null) return new List<int>(chain.Values);
        var values = new List<int>();
        for (var i = 0; i + 1 < chain.Stops.Count; i++)
            values.Add((int)Math.Round(chain.Stops[i + 1] - chain.Stops[i], MidpointRounding.AwayFromZero));
        return values;
    }
}
