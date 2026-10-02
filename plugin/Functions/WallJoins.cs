using System;
using System.Collections.Generic;
using System.Globalization;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The join graph over the walls as they stand. Wall records that touch are
/// one cluster, read as one shape (their union); a record that touches no
/// other is a cluster of one, read as its own rings. Runs come from that
/// shape (WallEdit's run finder), and two runs join where their bands meet:
/// a corner at an end of both, a tee at an end of one. The records stay the
/// storage. Pure geometry, no RhinoCommon, so it tests headless.
/// </summary>
public static class WallJoins
{
    /// <summary>Two runs this close to parallel (sine of the angle between them) do not join.</summary>
    const double ParallelSin = 0.05;
    /// <summary>A run is named for a compass side when its outer face looks within about 10° of it.</summary>
    const double FacingCos = 0.98;
    static readonly string[] Compass = { "north", "south", "east", "west" };

    public enum JoinKind
    {
        /// <summary>At an end of both runs.</summary>
        Corner,
        /// <summary>At an end of one run, the stem, and along the other.</summary>
        Tee,
        /// <summary>Along both.</summary>
        Cross
    }

    public sealed class Join
    {
        public int A, B;
        public JoinKind Kind;
        /// <summary>Where the two centrelines meet.</summary>
        public Pt At;
        /// <summary>The run that ends at a tee, or -1.</summary>
        public int Stem = -1;
    }

    public sealed class Graph
    {
        /// <summary>The cluster's records, as indices into the records given.</summary>
        public List<int> Records = new List<int>();
        /// <summary>The cluster's rings: outer loops, then holes.</summary>
        public List<List<Pt>> Shape;
        public List<WallEdit.Run> Runs = new List<WallEdit.Run>();
        public List<Join> Joins = new List<Join>();
        /// <summary>Each run's name: the north wall, or the wall at (x, y).</summary>
        public List<string> Names = new List<string>();

        /// <summary>
        /// The runs joined to this one that end at the join: both runs of a
        /// corner, the stem of a tee. These stretch or shorten when it moves.
        /// </summary>
        public List<int> Neighbours(int run)
        {
            var list = new List<int>();
            foreach (var join in Joins)
            {
                if (join.A != run && join.B != run) continue;
                var other = join.A == run ? join.B : join.A;
                if (join.Kind == JoinKind.Corner || (join.Kind == JoinKind.Tee && join.Stem == other))
                    if (!list.Contains(other)) list.Add(other);
            }
            return list;
        }

        /// <summary>The run on the same two faces that shares most of this one's length, or -1. Runs face east or north, so faces compare directly.</summary>
        public int Find(WallEdit.Run run, double tol)
        {
            var best = -1;
            var overlap = tol;
            for (var i = 0; i < Runs.Count; i++)
            {
                var r = Runs[i];
                if (Dot(r.Normal, run.Normal) < 0.99) continue;
                if (Math.Abs(r.Near - run.Near) > tol || Math.Abs(r.Far - run.Far) > tol) continue;
                var shared = Math.Min(run.Hi, r.Hi) - Math.Max(run.Lo, r.Lo);
                if (shared > overlap)
                {
                    overlap = shared;
                    best = i;
                }
            }
            return best;
        }
    }

    /// <summary>The records that touch, grouped. Every record is in exactly one cluster.</summary>
    public static List<List<int>> Clusters(IList<List<List<Pt>>> records, double tol)
    {
        var n = records.Count;
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;
        int Root(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if (Root(i) != Root(j) && Touch(records[i], records[j], tol))
                    parent[Root(j)] = Root(i);
        var byRoot = new Dictionary<int, List<int>>();
        var clusters = new List<List<int>>();
        for (var i = 0; i < n; i++)
        {
            if (!byRoot.TryGetValue(Root(i), out var list))
            {
                list = new List<int>();
                byRoot[Root(i)] = list;
                clusters.Add(list);
            }
            list.Add(i);
        }
        return clusters;
    }

    /// <summary>The cluster that holds this record.</summary>
    public static List<int> ClusterOf(IList<List<List<Pt>>> records, int record, double tol)
    {
        foreach (var cluster in Clusters(records, tol))
            if (cluster.Contains(record)) return cluster;
        return new List<int> { record };
    }

    /// <summary>
    /// The cluster's shape. A cluster of one is the record's own rings, so a
    /// lone record reads exactly as v2 read it. Null when the union does not
    /// come back as one piece.
    /// </summary>
    public static List<List<Pt>> Shape(IList<List<List<Pt>>> records, IList<int> cluster, double tol)
    {
        if (cluster.Count == 1) return records[cluster[0]];
        var regions = new List<List<List<Pt>>>();
        foreach (var i in cluster) regions.Add(records[i]);
        var loops = new List<List<Pt>>();
        foreach (var loop in RoomDetect.Union(regions, tol)) loops.Add(RoomDetect.Simplify(loop, tol));
        var grouped = WallEdit.Group(loops);
        return grouped.Count == 1 ? grouped[0] : null;
    }

    /// <summary>The join graph of one cluster. Null when its shape does not read as one piece.</summary>
    public static Graph Build(IList<List<List<Pt>>> records, IList<int> cluster, double tol)
    {
        var shape = Shape(records, cluster, tol);
        if (shape == null) return null;
        var graph = new Graph { Records = new List<int>(cluster), Shape = shape, Runs = Runs(shape, tol) };
        for (var a = 0; a < graph.Runs.Count; a++)
            for (var b = a + 1; b < graph.Runs.Count; b++)
                if (TryJoin(graph.Runs[a], graph.Runs[b], tol, out var join))
                {
                    join.A = a;
                    join.B = b;
                    if (join.Stem == 0) join.Stem = a;
                    else if (join.Stem == 1) join.Stem = b;
                    graph.Joins.Add(join);
                }
        graph.Names = Names(shape, graph, tol);
        return graph;
    }

    /// <summary>Every run in the shape, once. A face edge with no face across it is a free end, not a run.</summary>
    public static List<WallEdit.Run> Runs(List<List<Pt>> shape, double tol)
    {
        var runs = new List<WallEdit.Run>();
        var taken = new HashSet<(int, int)>();
        foreach (var edge in WallEdit.Edges(shape, tol))
        {
            if (taken.Contains(edge)) continue;
            if (!WallEdit.TryRunFrom(shape, edge, tol, out var run, out _)) continue;
            var seen = false;
            foreach (var e in run.Edges)
                if (taken.Contains(e)) seen = true;
            foreach (var e in run.Edges) taken.Add(e);
            if (!seen) runs.Add(run);
        }
        return runs;
    }

    /// <summary>
    /// Two runs join when their centrelines meet within each one's length,
    /// give or take the other's half thickness (more at a slant). The meeting
    /// is at an end of a run when it is within both thicknesses of that end.
    /// Stem is 0 or 1 for a tee (which of the two ends there), else -1.
    /// </summary>
    static bool TryJoin(WallEdit.Run a, WallEdit.Run b, double tol, out Join join)
    {
        join = null;
        var sin = Math.Abs(Cross(a.Dir, b.Dir));
        if (sin < ParallelSin) return false;
        var ca = Along(a.Normal, (a.Near + a.Far) / 2.0);
        var cb = Along(b.Normal, (b.Near + b.Far) / 2.0);
        // ca + a.Dir * t = cb + b.Dir * u
        var t = Cross(Sub(cb, ca), b.Dir) / Cross(a.Dir, b.Dir);
        var at = new Pt(ca.X + a.Dir.X * t, ca.Y + a.Dir.Y * t);
        var sa = Dot(at, a.Dir);
        var sb = Dot(at, b.Dir);
        var reachA = b.Thickness / (2.0 * sin) + tol;
        var reachB = a.Thickness / (2.0 * sin) + tol;
        if (sa < a.Lo - reachA || sa > a.Hi + reachA) return false;
        if (sb < b.Lo - reachB || sb > b.Hi + reachB) return false;
        var ends = (a.Thickness + b.Thickness) / sin + tol;
        var endA = Math.Min(sa - a.Lo, a.Hi - sa) <= ends;
        var endB = Math.Min(sb - b.Lo, b.Hi - sb) <= ends;
        join = new Join { At = at };
        if (endA && endB) join.Kind = JoinKind.Corner;
        else if (endA || endB)
        {
            join.Kind = JoinKind.Tee;
            join.Stem = endA ? 0 : 1;
        }
        else join.Kind = JoinKind.Cross;
        return true;
    }

    /// <summary>The outer run furthest out on a compass side is that side's wall; every other run is named by its middle.</summary>
    static List<string> Names(List<List<Pt>> shape, Graph graph, double tol)
    {
        var names = new List<string>();
        foreach (var run in graph.Runs) names.Add("the wall at " + At(Middle(run)));
        foreach (var side in Compass)
        {
            if (!WallEdit.TryPickSide(shape, side, tol, out var run, out _)) continue;
            var i = graph.Find(run, tol);
            if (i < 0 || !Sections.TryCompass(side, out var w)) continue;
            if (Math.Abs(Dot(graph.Runs[i].Normal, w)) < FacingCos) continue;
            names[i] = "the " + side + " wall";
        }
        return names;
    }

    /// <summary>The middle of the run's band.</summary>
    public static Pt Middle(WallEdit.Run run)
    {
        var s = (run.Lo + run.Hi) / 2.0;
        var c = (run.Near + run.Far) / 2.0;
        return new Pt(run.Dir.X * s + run.Normal.X * c, run.Dir.Y * s + run.Normal.Y * c);
    }

    /// <summary>Two records touch or overlap: their union is one piece.</summary>
    static bool Touch(List<List<Pt>> a, List<List<Pt>> b, double tol)
    {
        if (!BoxesMeet(a[0], b[0], tol)) return false;
        var loops = RoomDetect.Union(new[] { a, b }, tol);
        return WallEdit.Group(loops).Count == 1;
    }

    static bool BoxesMeet(List<Pt> a, List<Pt> b, double tol)
    {
        var x = Extent(a);
        var y = Extent(b);
        return x.MinX <= y.MaxX + tol && y.MinX <= x.MaxX + tol && x.MinY <= y.MaxY + tol && y.MinY <= x.MaxY + tol;
    }

    static RoomDetect.Box Extent(List<Pt> ring)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in ring)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
        }
        return new RoomDetect.Box(x0, y0, x1, y1);
    }

    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
    static double Cross(Pt a, Pt b) => a.X * b.Y - a.Y * b.X;
    static Pt Sub(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);
    static Pt Along(Pt dir, double by) => new Pt(dir.X * by, dir.Y * by);
    static string Mm(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
    static string At(Pt p) => "(" + Mm(p.X) + ", " + Mm(p.Y) + ")";
}
