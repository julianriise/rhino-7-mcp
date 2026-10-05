using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// An opening's plan footprint as a rectangle on its wall, not a world box:
/// on a 45° wall it is a turned rectangle. The cutter, the marker and the
/// width read it. No Rhino document.
/// </summary>
public sealed class OpeningFoot
{
    /// <summary>Centre in plan.</summary>
    public double X;
    public double Y;
    /// <summary>Unit direction along the wall.</summary>
    public double DirX;
    public double DirY;
    /// <summary>Along the wall.</summary>
    public double Width;
    /// <summary>Across the wall.</summary>
    public double Depth;

    /// <summary>Across the wall: the direction turned a quarter left.</summary>
    public double AcrossX => -DirY;
    public double AcrossY => DirX;

    public readonly struct Edge
    {
        public readonly double X0;
        public readonly double Y0;
        public readonly double X1;
        public readonly double Y1;

        public Edge(double x0, double y0, double x1, double y1)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
        }
    }

    /// <summary>A world-aligned box: the longer side runs along the wall, X on a tie.</summary>
    public static OpeningFoot FromBox(double minX, double minY, double maxX, double maxY)
    {
        var dx = maxX - minX;
        var dy = maxY - minY;
        var alongX = dx >= dy;
        return new OpeningFoot
        {
            X = 0.5 * (minX + maxX),
            Y = 0.5 * (minY + maxY),
            DirX = alongX ? 1 : 0,
            DirY = alongX ? 0 : 1,
            Width = alongX ? dx : dy,
            Depth = alongX ? dy : dx
        };
    }

    /// <summary>On a wall run at (x, y): the run's direction, pointing +X (or +Y) first.</summary>
    public static OpeningFoot Along(double x, double y, double tx, double ty, double width, double depth)
    {
        var len = Math.Sqrt(tx * tx + ty * ty);
        var foot = new OpeningFoot
        {
            X = x,
            Y = y,
            DirX = len > 1e-12 ? tx / len : 1,
            DirY = len > 1e-12 ? ty / len : 0,
            Width = width,
            Depth = depth
        };
        return foot.Canonical();
    }

    /// <summary>
    /// Drawn or modelled edges in plan: the longest one, as walked, runs along
    /// the wall (the first on a tie). Width and depth are the ends' extent
    /// along and across it. False when no edge is longer than 1 mm.
    /// </summary>
    public static bool TryFromEdges(IList<Edge> edges, out OpeningFoot foot)
    {
        foot = null;
        if (edges == null || edges.Count == 0) return false;
        var best = 1.0;
        var found = false;
        double dirX = 0, dirY = 0;
        foreach (var e in edges)
        {
            var dx = e.X1 - e.X0;
            var dy = e.Y1 - e.Y0;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= best) continue;
            best = len;
            dirX = dx / len;
            dirY = dy / len;
            found = true;
        }
        if (!found) return false;

        double loA = double.MaxValue, hiA = double.MinValue;
        double loC = double.MaxValue, hiC = double.MinValue;
        foreach (var e in edges)
        {
            Reach(e.X0, e.Y0, dirX, dirY, ref loA, ref hiA, ref loC, ref hiC);
            Reach(e.X1, e.Y1, dirX, dirY, ref loA, ref hiA, ref loC, ref hiC);
        }
        var midA = 0.5 * (loA + hiA);
        var midC = 0.5 * (loC + hiC);
        foot = new OpeningFoot
        {
            X = dirX * midA + -dirY * midC,
            Y = dirY * midA + dirX * midC,
            DirX = dirX,
            DirY = dirY,
            Width = hiA - loA,
            Depth = hiC - loC
        };
        return true;
    }

    /// <summary>The same rectangle, its direction pointing +X (or +Y on a wall along Y).</summary>
    public OpeningFoot Canonical()
    {
        if (DirX < 0 || (DirX == 0 && DirY < 0))
        {
            DirX = -DirX;
            DirY = -DirY;
        }
        return this;
    }

    /// <summary>The wall cutter: the width plus pad at each end, at least minDepth across.</summary>
    public void CutterHalves(double pad, double minDepth, out double halfAlong, out double halfAcross)
    {
        halfAlong = 0.5 * Width + pad;
        halfAcross = 0.5 * Math.Max(Depth, minDepth);
    }

    /// <summary>The marker: the width, at least selectDepth across so it can be picked.</summary>
    public void MarkerHalves(double selectDepth, out double halfAlong, out double halfAcross)
    {
        halfAlong = 0.5 * Width;
        halfAcross = 0.5 * Math.Max(Depth, selectDepth);
    }

    /// <summary>World extent of the rectangle grown to these half sizes.</summary>
    public void WorldBox(
        double halfAlong, double halfAcross,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        var ex = Math.Abs(DirX) * halfAlong + Math.Abs(DirY) * halfAcross;
        var ey = Math.Abs(DirY) * halfAlong + Math.Abs(DirX) * halfAcross;
        minX = X - ex;
        maxX = X + ex;
        minY = Y - ey;
        maxY = Y + ey;
    }

    private static void Reach(
        double x, double y, double dirX, double dirY,
        ref double loA, ref double hiA, ref double loC, ref double hiC)
    {
        var a = x * dirX + y * dirY;
        var c = x * -dirY + y * dirX;
        if (a < loA) loA = a;
        if (a > hiA) hiA = a;
        if (c < loC) loC = c;
        if (c > hiC) hiC = c;
    }
}
