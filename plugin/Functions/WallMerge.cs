using System;
using System.Collections.Generic;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Merge walls: two wall records that read as less than two walls become
/// clean. Two walls on one line, end to end (a run of each on the same two
/// face lines), merge into one record. Two walls that go into each other
/// (their union covers less than the two) are cut apart at the join as
/// WallSplit cuts a shape, so each keeps its own run and id; when that split
/// does not stand they merge into one record. The union, and so the rooms,
/// floor and roof, is the same before and after. Records only merge with a
/// record of the same kind (height and level, as the caller keys them).
/// Pure geometry, no RhinoCommon, so it tests headless.
/// </summary>
public static class WallMerge
{
    /// <summary>Two walls overlapping by more than this, mm² per tol², go into each other.</summary>
    const double OverlapMm2 = 100.0;

    public sealed class Result
    {
        /// <summary>Each record that changed, by its index in the records given, and its rings after.</summary>
        public Dictionary<int, List<List<Pt>>> Records = new Dictionary<int, List<List<Pt>>>();
        /// <summary>Each record merged away and the record it is now part of.</summary>
        public Dictionary<int, int> Into = new Dictionary<int, int>();
        /// <summary>How many pairs were cut apart at their join.</summary>
        public int Simplified;
        public bool Any => Records.Count > 0 || Into.Count > 0;
    }

    /// <summary>
    /// The merges around the seeds: each seed against every record it touches,
    /// and again for each record a merge changed, until nothing more merges.
    /// keys, when given, holds one string per record; only records with the
    /// same key merge into one.
    /// </summary>
    public static Result Plan(IList<List<List<Pt>>> records, IEnumerable<int> seeds, double tol, IList<string> keys = null)
    {
        var work = new List<List<List<Pt>>>(records);
        var result = new Result();
        var queue = new Queue<int>();
        var queued = new HashSet<int>();
        foreach (var s in seeds)
            if (s >= 0 && s < work.Count && queued.Add(s)) queue.Enqueue(s);
        // Each merge or cut takes a pair out of the overlaps, so this ends; the cap guards odd geometry.
        var steps = 0;
        while (queue.Count > 0 && steps < 4 * work.Count + 8)
        {
            var i = queue.Dequeue();
            queued.Remove(i);
            if (work[i] == null) continue;
            for (var j = 0; j < work.Count; j++)
            {
                if (j == i || work[j] == null || work[i] == null) continue;
                var a = work[i];
                var b = work[j];
                if (!WallJoins.BoxesMeet(a[0], b[0], tol)) continue;
                var union = Union(a, b, tol);
                if (union == null) continue;
                var same = keys == null || string.Equals(keys[i], keys[j], StringComparison.Ordinal);
                var overlap = Area(a) + Area(b) - Area(union);
                if (overlap > OverlapMm2 * tol * tol)
                {
                    steps++;
                    var cut = Cut(a, b, tol);
                    if (cut != null)
                    {
                        work[i] = cut[0];
                        work[j] = cut[1];
                        result.Simplified++;
                    }
                    else if (same) Merge(work, result, i, j, union);
                    else continue;
                }
                else if (same && OnOneLine(a, b, tol))
                {
                    steps++;
                    Merge(work, result, i, j, union);
                }
                else continue;
                foreach (var k in new[] { i, j })
                    if (work[k] != null && queued.Add(k)) queue.Enqueue(k);
            }
        }

        foreach (var pair in new List<int>(result.Into.Keys))
        {
            var to = result.Into[pair];
            while (result.Into.TryGetValue(to, out var next)) to = next;
            result.Into[pair] = to;
        }
        for (var k = 0; k < work.Count; k++)
            if (work[k] != null && !ReferenceEquals(work[k], records[k])) result.Records[k] = work[k];
        return result;
    }

    /// <summary>A run of each on the same two face lines, end to end: one wall drawn in two goes.</summary>
    static bool OnOneLine(List<List<Pt>> a, List<List<Pt>> b, double tol)
    {
        foreach (var ra in WallJoins.Runs(a, tol))
            foreach (var rb in WallJoins.Runs(b, tol))
            {
                // Runs face east or north, so their faces compare directly.
                if (ra.Normal.X * rb.Normal.X + ra.Normal.Y * rb.Normal.Y < 0.99) continue;
                if (Math.Abs(ra.Near - rb.Near) > tol || Math.Abs(ra.Far - rb.Far) > tol) continue;
                if (ra.Lo <= rb.Hi + tol && rb.Lo <= ra.Hi + tol) return true;
            }
        return false;
    }

    /// <summary>The larger record takes the other in.</summary>
    static void Merge(List<List<List<Pt>>> work, Result result, int i, int j, List<List<Pt>> union)
    {
        var keep = Area(work[j]) > Area(work[i]) ? j : i;
        var gone = keep == i ? j : i;
        work[keep] = union;
        work[gone] = null;
        result.Into[gone] = keep;
    }

    /// <summary>
    /// The pair cut apart where they go into each other: the union split into
    /// one piece per run (WallSplit), when it is exactly two pieces, each
    /// given to the record it overlaps most. Null when that does not stand.
    /// </summary>
    static List<List<Pt>>[] Cut(List<List<Pt>> a, List<List<Pt>> b, double tol)
    {
        if (a.Count != 1 || b.Count != 1) return null;
        var pair = new List<List<List<Pt>>> { a, b };
        var graph = WallJoins.Build(pair, new List<int> { 0, 1 }, tol);
        if (graph == null) return null;
        var pieces = WallSplit.Pieces(graph, tol, out _);
        if (pieces == null || pieces.Count != 2) return null;
        var p0 = new List<List<Pt>> { pieces[0].Ring };
        var p1 = new List<List<Pt>> { pieces[1].Ring };
        var straight = Shared(p0, a, tol) + Shared(p1, b, tol);
        var crossed = Shared(p0, b, tol) + Shared(p1, a, tol);
        return straight >= crossed ? new[] { p0, p1 } : new[] { p1, p0 };
    }

    /// <summary>The area two regions share.</summary>
    static double Shared(List<List<Pt>> a, List<List<Pt>> b, double tol)
    {
        var union = Union(a, b, tol);
        return union == null ? 0 : Area(a) + Area(b) - Area(union);
    }

    /// <summary>The two records' union when it is one piece, else null.</summary>
    static List<List<Pt>> Union(List<List<Pt>> a, List<List<Pt>> b, double tol)
    {
        var loops = new List<List<Pt>>();
        foreach (var loop in RoomDetect.Union(new[] { a, b }, tol)) loops.Add(RoomDetect.Simplify(loop, tol));
        var regions = WallEdit.Group(loops);
        return regions.Count == 1 ? regions[0] : null;
    }

    /// <summary>The area the walls cover: the outer loop less the holes.</summary>
    static double Area(List<List<Pt>> rings)
    {
        var area = Math.Abs(RoomDetect.Area(rings[0]));
        for (var k = 1; k < rings.Count; k++) area -= Math.Abs(RoomDetect.Area(rings[k]));
        return area;
    }
}
