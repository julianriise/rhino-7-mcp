using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Placement and fail-closed rebuild decisions with no Rhino document.
/// A footprint comes from a world point on the nearest wall segment.
/// A stored offset is not a placement key: segment order can change.
/// </summary>
public static class SoftParamPlan
{
    public sealed class Seg
    {
        public double X0;
        public double Y0;
        public double X1;
        public double Y1;
        public bool Outer;

        public Seg()
        {
        }

        public Seg(double x0, double y0, double x1, double y1, bool outer)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
            Outer = outer;
        }

        public double Length
        {
            get
            {
                var dx = X1 - X0;
                var dy = Y1 - Y0;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }
    }

    public readonly struct CutStep<TState>
    {
        public readonly bool Ok;
        public readonly TState Next;

        public CutStep(bool ok, TState next)
        {
            Ok = ok;
            Next = next;
        }
    }

    /// <summary>
    /// Nearest segment to a world point, clamped to the segment.
    /// Returns false when every segment is farther than maxDist.
    /// </summary>
    public static bool TryPlace(
        IList<Seg> segs,
        double x,
        double y,
        double maxDist,
        out int index,
        out double t,
        out double distance)
    {
        index = -1;
        t = 0;
        distance = double.PositiveInfinity;
        if (segs == null) return false;
        for (var i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            if (seg == null) continue;
            if (seg.Length <= 1e-6) continue;
            var d = DistanceToSegment(seg, x, y, out var local);
            if (d < distance)
            {
                distance = d;
                index = i;
                t = local;
            }
        }

        if (index < 0) return false;
        return distance <= maxDist;
    }

    /// <summary>
    /// Walks a cumulative offset. Reordering segs changes which edge this hits.
    /// </summary>
    public static bool TryPlaceByOffset(
        IList<Seg> segs,
        double offset,
        out int index,
        out double t)
    {
        index = -1;
        t = 0;
        if (segs == null || segs.Count == 0) return false;
        if (offset < 0) offset = 0;
        double acc = 0;
        var last = -1;
        var lastAcc = 0.0;
        for (var i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            if (seg == null) continue;
            var len = seg.Length;
            if (len <= 1e-6) continue;
            last = i;
            lastAcc = acc;
            if (offset <= acc + len)
            {
                index = i;
                t = (offset - acc) / len;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
                return true;
            }

            acc += len;
        }

        if (last < 0) return false;
        index = last;
        var lastLen = segs[last].Length;
        t = lastLen > 1e-6 ? (offset - lastAcc) / lastLen : 0;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        return true;
    }

    public static double OffsetOf(IList<Seg> segs, int index, double t)
    {
        if (segs == null || index < 0 || index >= segs.Count) return 0;
        double acc = 0;
        for (var i = 0; i < index; i++)
        {
            if (segs[i] != null) acc += segs[i].Length;
        }

        var len = segs[index] == null ? 0 : segs[index].Length;
        return acc + t * len;
    }

    public static bool SameEdge(Seg a, Seg b, double tol)
    {
        if (a == null || b == null) return false;
        var forward = Math.Abs(a.X0 - b.X0) <= tol && Math.Abs(a.Y0 - b.Y0) <= tol
            && Math.Abs(a.X1 - b.X1) <= tol && Math.Abs(a.Y1 - b.Y1) <= tol;
        var back = Math.Abs(a.X0 - b.X1) <= tol && Math.Abs(a.Y0 - b.Y1) <= tol
            && Math.Abs(a.X1 - b.X0) <= tol && Math.Abs(a.Y1 - b.Y0) <= tol;
        return forward || back;
    }

    /// <summary>
    /// A cut is real when the boolean returned pieces and either the volume
    /// fell or the cutter meets the solid. An empty boolean is a miss.
    /// </summary>
    public static bool AcceptCut(
        int pieceCount,
        double volumeBefore,
        double volumeAfter,
        bool cutterIntersects)
    {
        if (pieceCount <= 0) return false;
        var dropped = volumeBefore > 0 && volumeAfter < volumeBefore;
        return dropped || cutterIntersects;
    }

    /// <summary>
    /// Cuts every item against a copy of the seed. On the first failure the
    /// result is the seed and nothing from the working copy is returned.
    /// </summary>
    public static bool ApplyAtomic<TState, TItem>(
        TState seed,
        IList<TItem> items,
        Func<TState, TItem, CutStep<TState>> cut,
        out TState result,
        out TItem failed)
        where TState : class
    {
        result = seed;
        failed = default;
        if (cut == null) return false;
        if (items == null || items.Count == 0) return true;
        var working = seed;
        for (var i = 0; i < items.Count; i++)
        {
            var step = cut(working, items[i]);
            if (!step.Ok || step.Next == null)
            {
                result = seed;
                failed = items[i];
                return false;
            }

            working = step.Next;
        }

        result = working;
        return true;
    }

    private static double DistanceToSegment(Seg seg, double x, double y, out double t)
    {
        var dx = seg.X1 - seg.X0;
        var dy = seg.Y1 - seg.Y0;
        var len2 = dx * dx + dy * dy;
        t = 0;
        if (len2 <= 1e-12)
        {
            var ex0 = x - seg.X0;
            var ey0 = y - seg.Y0;
            return Math.Sqrt(ex0 * ex0 + ey0 * ey0);
        }

        t = ((x - seg.X0) * dx + (y - seg.Y0) * dy) / len2;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        var px = seg.X0 + t * dx;
        var py = seg.Y0 + t * dy;
        var ex = x - px;
        var ey = y - py;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}
