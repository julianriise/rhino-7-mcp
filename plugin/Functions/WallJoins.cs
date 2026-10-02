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

        public int Find(WallEdit.Run run, double tol) => WallJoins.Find(Runs, run, tol);
    }

    /// <summary>The run on the same two faces that shares most of this one's length, or -1. Runs face east or north, so faces compare directly.</summary>
    public static int Find(IList<WallEdit.Run> runs, WallEdit.Run run, double tol)
    {
        var best = -1;
        var overlap = tol;
        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
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

    /// <summary>A wall joined to the edited run, as it followed.</summary>
    public sealed class Followed
    {
        /// <summary>Its name before the edit: the east wall, or the wall at (x, y).</summary>
        public string Wall;
        /// <summary>How much longer it got, mm. Shorter is below 0.</summary>
        public double ChangeMm;
        /// <summary>The records it stands in.</summary>
        public List<int> Records = new List<int>();
    }

    public sealed class Moved
    {
        /// <summary>The cluster's shape after the move.</summary>
        public List<List<Pt>> Shape;
        /// <summary>Each record that changed, by its index in the records given, and its rings after.</summary>
        public Dictionary<int, List<List<Pt>>> Records = new Dictionary<int, List<List<Pt>>>();
        public List<Followed> Followed = new List<Followed>();
    }

    /// <summary>
    /// The run moved by along its normal across the whole cluster. The shape
    /// moves as WallEdit moves it, with its refusals, and must keep every room
    /// closed. Each record in a cluster of more than one moves by the same
    /// rule, and together they must make the moved shape again. A cluster of
    /// one is its record, so a lone record moves exactly as WallEdit moves it.
    /// </summary>
    public static bool TryMove(IList<List<List<Pt>>> records, Graph graph, WallEdit.Run run, double by, double tol, out Moved moved, out string why)
    {
        moved = null;
        if (!WallEdit.TryMove(graph.Shape, run, by, tol, out var shape, out why)) return false;
        if (!SameRooms(graph.Shape, shape, tol))
        {
            why = "Not moved: a room would not stay closed.";
            return false;
        }
        var result = new Moved { Shape = shape };
        if (graph.Records.Count == 1)
            result.Records[graph.Records[0]] = shape;
        else
        {
            var after = new List<List<List<Pt>>>(records);
            foreach (var i in graph.Records)
            {
                var slides = WallEdit.Slides(records[i], run, by, tol, out why);
                if (slides == null) return false;
                if (slides.Count == 0) continue;
                if (!WallEdit.TryMove(records[i], run, by, tol, out var rings, out why))
                {
                    why = NotCleanly;
                    return false;
                }
                result.Records[i] = rings;
                after[i] = rings;
            }
            var union = Shape(after, graph.Records, tol);
            if (union == null || union.Count != shape.Count || Math.Abs(Area(union) - Area(shape)) > tol * Perimeter(shape))
            {
                why = NotCleanly;
                return false;
            }
        }

        result.Followed = Follow(records, graph, run, shape, tol, keepUnchanged: false);
        moved = result;
        return true;
    }

    public sealed class Cut
    {
        /// <summary>The cluster's shape after the cut, or null when nothing is left.</summary>
        public List<List<Pt>> Shape;
        /// <summary>Each record the cut changed and its rings after; null for a record that went whole.</summary>
        public Dictionary<int, List<List<Pt>>> Records = new Dictionary<int, List<List<Pt>>>();
        /// <summary>The walls that were joined to the run, as they stand after it.</summary>
        public List<Followed> Followed = new List<Followed>();
    }

    /// <summary>
    /// The run's band cut out of every record in the cluster. A record the
    /// band covers goes whole; one it does not reach stays as it is. A record
    /// left in two pieces is refused, as WallEdit refuses it. Records left
    /// standing apart are fine: they are two clusters now, and the shape after
    /// is the largest of them. A cluster of one is its record, cut exactly as
    /// WallEdit cuts it.
    /// </summary>
    public static bool TryDelete(IList<List<List<Pt>>> records, Graph graph, WallEdit.Run run, double tol, out Cut cut, out string why)
    {
        cut = null;
        var result = new Cut();
        if (graph.Records.Count == 1)
        {
            if (!WallEdit.TryDelete(graph.Shape, run, tol, out var left, out why)) return false;
            result.Records[graph.Records[0]] = left;
            result.Shape = left;
        }
        else
        {
            why = null;
            var band = WallEdit.Band(run);
            var after = new List<List<List<Pt>>>(records);
            var kept = new List<int>();
            foreach (var i in graph.Records)
            {
                var pieces = WallEdit.Group(RoomDetect.Difference(new[] { records[i] }, new[] { band }, tol));
                if (pieces.Count > 1)
                {
                    why = "Not deleted: the walls left would stand in " + pieces.Count + " separate pieces, and this version keeps one wall record.";
                    return false;
                }
                if (pieces.Count == 0)
                {
                    result.Records[i] = null;
                    continue;
                }
                kept.Add(i);
                if (Math.Abs(Area(pieces[0]) - Area(records[i])) <= tol * tol) continue;
                result.Records[i] = pieces[0];
                after[i] = pieces[0];
            }
            foreach (var piece in Pieces(after, kept, tol))
                if (result.Shape == null || Area(piece) > Area(result.Shape)) result.Shape = piece;
        }
        if (result.Shape != null) result.Followed = Follow(records, graph, run, result.Shape, tol, keepUnchanged: true);
        cut = result;
        return true;
    }

    /// <summary>
    /// The run's neighbours (WallJoins.Graph.Neighbours) as they stand in the
    /// shape after an edit: name, change in length, the records they stand in.
    /// A move lists only those whose length changed; a delete lists them all.
    /// </summary>
    static List<Followed> Follow(IList<List<List<Pt>>> records, Graph graph, WallEdit.Run run, List<List<Pt>> after, double tol, bool keepUnchanged)
    {
        var list = new List<Followed>();
        var self = graph.Find(run, tol);
        if (self < 0) return list;
        var later = Runs(after, tol);
        foreach (var n in graph.Neighbours(self))
        {
            var before = graph.Runs[n];
            var j = Find(later, before, tol);
            if (j < 0) continue;
            var change = Math.Round(later[j].Length - before.Length);
            if (!keepUnchanged && Math.Abs(change) < 1) continue;
            var followed = new Followed { Wall = graph.Names[n], ChangeMm = change };
            foreach (var i in graph.Records)
                if (WallEdit.InRegion(records[i], Middle(before))) followed.Records.Add(i);
            list.Add(followed);
        }
        return list;
    }

    const string NotCleanly = "Not moved: the walls joined to it would not follow cleanly.";

    /// <summary>As many rooms as before, and none closed up.</summary>
    static bool SameRooms(List<List<Pt>> before, List<List<Pt>> after, double tol)
    {
        if (after == null || after.Count != before.Count) return false;
        for (var k = 1; k < after.Count; k++)
            if (Math.Abs(RoomDetect.Area(after[k])) <= tol * tol) return false;
        return true;
    }

    /// <summary>The area the walls cover: the outer loop less the holes.</summary>
    static double Area(List<List<Pt>> rings)
    {
        var area = Math.Abs(RoomDetect.Area(rings[0]));
        for (var k = 1; k < rings.Count; k++) area -= Math.Abs(RoomDetect.Area(rings[k]));
        return area;
    }

    static double Perimeter(List<List<Pt>> rings)
    {
        var length = 0.0;
        foreach (var ring in rings)
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                length += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            }
        return length;
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
        var pieces = Pieces(records, cluster, tol);
        return pieces.Count == 1 ? pieces[0] : null;
    }

    /// <summary>
    /// The facades: each cluster's outer loop, except one that lies inside a
    /// larger one. A cluster whose union does not read as one piece gives its
    /// records' outer loops.
    /// </summary>
    public static List<List<Pt>> Outlines(IList<List<List<Pt>>> records, double tol)
    {
        var rings = new List<List<Pt>>();
        foreach (var cluster in Clusters(records, tol))
        {
            var shape = Shape(records, cluster, tol);
            if (shape != null) rings.Add(shape[0]);
            else foreach (var i in cluster) rings.Add(records[i][0]);
        }
        var outlines = new List<List<Pt>>();
        foreach (var ring in rings)
        {
            var inside = false;
            foreach (var other in rings)
                if (other != ring && Math.Abs(RoomDetect.Area(other)) > Math.Abs(RoomDetect.Area(ring)) && RoomDetect.Contains(other, ring[0]))
                    inside = true;
            if (!inside) outlines.Add(ring);
        }
        return outlines;
    }

    /// <summary>The records' union as separate regions, each an outer loop with its holes.</summary>
    static List<List<List<Pt>>> Pieces(IList<List<List<Pt>>> records, IList<int> cluster, double tol)
    {
        if (cluster.Count == 0) return new List<List<List<Pt>>>();
        if (cluster.Count == 1) return new List<List<List<Pt>>> { records[cluster[0]] };
        var regions = new List<List<List<Pt>>>();
        foreach (var i in cluster) regions.Add(records[i]);
        var loops = new List<List<Pt>>();
        foreach (var loop in RoomDetect.Union(regions, tol)) loops.Add(RoomDetect.Simplify(loop, tol));
        return WallEdit.Group(loops);
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

    /// <summary>
    /// A point just outside one side of a room, in the wall: the middle of the
    /// room's edge that faces that compass side, furthest out, 1 mm out. A
    /// pick there (WallEdit.TryPick) finds the run whose face is that side.
    /// </summary>
    public static bool TryRoomSide(List<Pt> room, string side, double tol, out Pt at, out string why)
    {
        at = default;
        why = null;
        if (room == null || room.Count < 3)
        {
            why = "The room has no outline.";
            return false;
        }
        if (!Sections.TryCompass(side, out var w))
        {
            why = "side is north, south, east or west.";
            return false;
        }
        var outward = RoomDetect.Area(room) > 0 ? 1.0 : -1.0;
        var best = double.MinValue;
        var found = false;
        for (var i = 0; i < room.Count; i++)
        {
            var a = room[i];
            var b = room[(i + 1) % room.Count];
            var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (len <= tol) continue;
            // A counterclockwise room's outside is to the right of each edge.
            var n = new Pt(outward * (b.Y - a.Y) / len, -outward * (b.X - a.X) / len);
            if (Dot(n, w) < 0.7) continue;
            var mid = new Pt((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            var reach = Dot(mid, w) + 1e-6 * len;
            if (reach <= best) continue;
            best = reach;
            at = new Pt(mid.X + n.X * Math.Max(tol, 1.0), mid.Y + n.Y * Math.Max(tol, 1.0));
            found = true;
        }
        if (!found) why = "The room has no side facing " + side.Trim().ToLowerInvariant() + ".";
        return found;
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
