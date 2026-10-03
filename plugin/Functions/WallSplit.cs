using System;
using System.Collections.Generic;
using System.Globalization;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// A cluster's shape cut into one piece per straight run, so each run can be
/// its own wall record. Each run keeps its band, end to end. Where two bands
/// overlap, the square goes to one run: at a tee the through run, at a corner
/// or a cross the longer run, then the one facing north or south. Whatever no
/// band covers joins the piece it shares the longest edge with; a square that
/// lies between two runs on one line joins them into one. The split stands
/// only when every piece is one simple ring, no two overlap and together they
/// are the shape. Pure geometry, no RhinoCommon, so it tests headless.
/// </summary>
public static class WallSplit
{
    /// <summary>Two runs that meet at under 15° (sine) read as a curve, not a corner.</summary>
    const double MinMeetSin = 0.26;

    public sealed class Piece
    {
        /// <summary>The run the piece belongs to, as an index into the graph's runs.</summary>
        public int Run;
        /// <summary>One simple counterclockwise ring.</summary>
        public List<Pt> Ring;
    }

    /// <summary>
    /// The shape cut into pieces, in run order and along each run. A shape of
    /// one run is one piece. Null, with one sentence, when the split does not
    /// stand; the caller keeps the record whole.
    /// </summary>
    public static List<Piece> Pieces(WallJoins.Graph graph, double tol, out string why)
    {
        why = null;
        var shape = graph?.Shape;
        if (shape == null || shape.Count == 0 || graph.Runs.Count == 0)
        {
            why = "Not split: the wall has no straight runs.";
            return null;
        }
        if (graph.Runs.Count == 1)
        {
            if (shape.Count != 1)
            {
                why = "Not split: one run that closes a room is one wall already.";
                return null;
            }
            return new List<Piece> { new Piece { Run = 0, Ring = shape[0] } };
        }
        if (!Straight(graph, tol, out why)) return null;

        // Where two bands overlap, the loser gives up the winner's band.
        var bands = new List<List<Pt>>();
        foreach (var run in graph.Runs) bands.Add(WallEdit.Band(run));
        var beaten = new List<int>[graph.Runs.Count];
        for (var i = 0; i < beaten.Length; i++) beaten[i] = new List<int>();
        for (var a = 0; a < bands.Count; a++)
            for (var b = a + 1; b < bands.Count; b++)
            {
                if (!Overlap(bands[a], bands[b], tol)) continue;
                var winner = Winner(graph, a, b, tol);
                beaten[winner == a ? b : a].Add(winner);
            }

        var pieces = new List<Piece>();
        for (var i = 0; i < graph.Runs.Count; i++)
        {
            var band = bands[i];
            var cuts = new List<List<Pt>>();
            cuts.AddRange(RoomDetect.Difference(new[] { new List<List<Pt>> { band } }, new[] { shape[0] }, tol));
            for (var k = 1; k < shape.Count; k++) cuts.Add(shape[k]);
            foreach (var w in beaten[i]) cuts.Add(bands[w]);
            foreach (var region in WallEdit.Group(RoomDetect.Difference(new[] { new List<List<Pt>> { band } }, cuts, tol)))
            {
                if (region.Count != 1 || Math.Abs(RoomDetect.Area(region[0])) <= tol * tol) continue;
                pieces.Add(new Piece { Run = i, Ring = RoomDetect.Simplify(region[0], tol) });
            }
        }

        var rings = new List<List<Pt>>();
        foreach (var piece in pieces) rings.Add(piece.Ring);
        foreach (var left in WallEdit.Group(RoomDetect.Difference(new[] { shape }, rings, tol)))
        {
            if (left.Count != 1)
            {
                why = "Not split: part of the wall near " + At(left[0][0]) + " belongs to no run.";
                return null;
            }
            if (Math.Abs(RoomDetect.Area(left[0])) <= tol * Perimeter(left[0])) continue;
            if (!Absorb(graph, pieces, left[0], tol, out why)) return null;
        }

        if (!Whole(shape, pieces, tol, out why)) return null;
        pieces.Sort((a, b) =>
        {
            if (a.Run != b.Run) return a.Run.CompareTo(b.Run);
            var dir = graph.Runs[a.Run].Dir;
            return Dot(Centre(a.Ring), dir).CompareTo(Dot(Centre(b.Ring), dir));
        });
        return pieces;
    }

    /// <summary>
    /// Runs meet at 15° or more, and no three edges outside the runs bend on
    /// one after another by less. Otherwise the wall is a curve drawn in
    /// short straight pieces, and it stays whole.
    /// </summary>
    internal static bool Straight(WallJoins.Graph graph, double tol, out string why)
    {
        why = null;
        var shape = graph.Shape;
        var inRun = new HashSet<(int, int)>();
        foreach (var run in graph.Runs)
            foreach (var e in run.Edges) inRun.Add(e);
        var edges = WallEdit.Edges(shape, tol);
        foreach (var e in edges)
        {
            var bends = 0;
            var at = e;
            while (bends < 2 && !inRun.Contains(at))
            {
                var next = Next(shape, at, tol);
                if (inRun.Contains(next) || !Bends(shape, at, next)) break;
                bends++;
                at = next;
            }
            if (bends < 2) continue;
            why = Curved(shape[e.Loop][e.Edge]);
            return false;
        }
        foreach (var join in graph.Joins)
            if (Math.Abs(Cross(graph.Runs[join.A].Dir, graph.Runs[join.B].Dir)) < MinMeetSin)
            {
                why = Curved(join.At);
                return false;
            }
        return true;
    }

    static string Curved(Pt at) => "Not split: the wall bends in a curve near " + At(at) + ", and a curved wall stays one record.";

    /// <summary>The second edge goes on from the first, turning by under 15°.</summary>
    static bool Bends(List<List<Pt>> shape, (int Loop, int Edge) a, (int Loop, int Edge) b)
    {
        var ra = shape[a.Loop];
        var rb = shape[b.Loop];
        var u = Sub(ra[(a.Edge + 1) % ra.Count], ra[a.Edge]);
        var v = Sub(rb[(b.Edge + 1) % rb.Count], rb[b.Edge]);
        var lu = Math.Sqrt(Dot(u, u));
        var lv = Math.Sqrt(Dot(v, v));
        if (lu <= 0 || lv <= 0 || Dot(u, v) <= 0) return false;
        var sin = Math.Abs(Cross(u, v)) / (lu * lv);
        return sin > 1e-6 && sin < MinMeetSin;
    }

    /// <summary>The next edge longer than tol after this one in its ring.</summary>
    static (int, int) Next(List<List<Pt>> shape, (int Loop, int Edge) e, double tol)
    {
        var ring = shape[e.Loop];
        var n = ring.Count;
        for (var k = 1; k < n; k++)
        {
            var i = (e.Edge + k) % n;
            if (Dist(ring[i], ring[(i + 1) % n]) > tol) return (e.Loop, i);
        }
        return e;
    }

    /// <summary>The run that keeps the part two bands share: at a tee the through run, else the longer, then the one facing north or south.</summary>
    static int Winner(WallJoins.Graph graph, int a, int b, double tol)
    {
        foreach (var join in graph.Joins)
            if (join.Kind == WallJoins.JoinKind.Tee && ((join.A == a && join.B == b) || (join.A == b && join.B == a)))
                return join.Stem == a ? b : a;
        return Prefer(graph.Runs[a].Length, graph.Runs[a], a, graph.Runs[b].Length, graph.Runs[b], b, tol);
    }

    /// <summary>Two bands share more than a sliver.</summary>
    static bool Overlap(List<Pt> a, List<Pt> b, double tol)
    {
        double ax0 = double.MaxValue, ay0 = double.MaxValue, ax1 = double.MinValue, ay1 = double.MinValue;
        double bx0 = double.MaxValue, by0 = double.MaxValue, bx1 = double.MinValue, by1 = double.MinValue;
        foreach (var p in a) { ax0 = Math.Min(ax0, p.X); ay0 = Math.Min(ay0, p.Y); ax1 = Math.Max(ax1, p.X); ay1 = Math.Max(ay1, p.Y); }
        foreach (var p in b) { bx0 = Math.Min(bx0, p.X); by0 = Math.Min(by0, p.Y); bx1 = Math.Max(bx1, p.X); by1 = Math.Max(by1, p.Y); }
        if (ax0 >= bx1 - tol || bx0 >= ax1 - tol || ay0 >= by1 - tol || by0 >= ay1 - tol) return false;
        var left = 0.0;
        foreach (var loop in RoomDetect.Difference(new[] { new List<List<Pt>> { a } }, new[] { b }, tol)) left += RoomDetect.Area(loop);
        return RoomDetect.Area(a) - left > tol * Perimeter(a);
    }

    static int Prefer(double lengthA, WallEdit.Run a, int ia, double lengthB, WallEdit.Run b, int ib, double tol)
    {
        if (Math.Abs(lengthA - lengthB) > tol) return lengthA > lengthB ? ia : ib;
        var na = FacesNorthOrSouth(a);
        if (na != FacesNorthOrSouth(b)) return na ? ia : ib;
        return Math.Min(ia, ib);
    }

    static bool FacesNorthOrSouth(WallEdit.Run run) => Math.Abs(run.Normal.Y) >= Math.Abs(run.Normal.X);

    /// <summary>
    /// A part no band covers joins a piece. One that lies between pieces of two
    /// runs on one line joins them into one piece through it; else it joins the
    /// piece it shares the longest edge with.
    /// </summary>
    static bool Absorb(WallJoins.Graph graph, List<Piece> pieces, List<Pt> left, double tol, out string why)
    {
        why = null;
        var touching = new List<int>();
        var shared = new List<double>();
        for (var i = 0; i < pieces.Count; i++)
        {
            var length = Shared(pieces[i].Ring, left, tol);
            if (length <= tol) continue;
            touching.Add(i);
            shared.Add(length);
        }
        if (touching.Count == 0)
        {
            why = "Not split: part of the wall near " + At(left[0]) + " touches no run.";
            return false;
        }

        int keep = -1, other = -1;
        var best = -1.0;
        for (var x = 0; x < touching.Count; x++)
            for (var y = x + 1; y < touching.Count; y++)
            {
                var a = pieces[touching[x]];
                var b = pieces[touching[y]];
                if (a.Run == b.Run || !OneLine(graph.Runs[a.Run], graph.Runs[b.Run], tol)) continue;
                var length = graph.Runs[a.Run].Length + graph.Runs[b.Run].Length;
                if (keep >= 0 && Prefer(best, graph.Runs[pieces[keep].Run], 0, length, graph.Runs[a.Run], 1, tol) == 0)
                    continue;
                best = length;
                keep = touching[x];
                other = touching[y];
            }
        if (keep < 0)
        {
            var longest = 0;
            for (var x = 1; x < touching.Count; x++)
                if (shared[x] > shared[longest]) longest = x;
            keep = touching[longest];
        }

        var parts = new List<List<Pt>> { pieces[keep].Ring, left };
        if (other >= 0) parts.Add(pieces[other].Ring);
        var merged = WallEdit.Group(RoomDetect.Union(parts, tol));
        if (merged.Count != 1 || merged[0].Count != 1)
        {
            why = "Not split: the wall near " + At(left[0]) + " does not part cleanly.";
            return false;
        }
        pieces[keep].Ring = RoomDetect.Simplify(merged[0][0], tol);
        if (other >= 0) pieces.RemoveAt(other);
        return true;
    }

    /// <summary>Two runs on the same two face lines.</summary>
    static bool OneLine(WallEdit.Run a, WallEdit.Run b, double tol) =>
        Math.Abs(Dot(a.Normal, b.Normal)) > 0.99
        && Math.Abs(a.Near - b.Near) <= tol && Math.Abs(a.Far - b.Far) <= tol;

    /// <summary>The pieces are simple rings that do not overlap and together make the shape.</summary>
    static bool Whole(List<List<Pt>> shape, List<Piece> pieces, double tol, out string why)
    {
        why = null;
        var area = 0.0;
        var rings = new List<List<Pt>>();
        foreach (var piece in pieces)
        {
            if (piece.Ring.Count < 3 || RoomDetect.Area(piece.Ring) <= 0 || !Simple(piece.Ring, tol))
            {
                why = "Not split: a piece near " + At(piece.Ring[0]) + " is not one simple outline.";
                return false;
            }
            area += RoomDetect.Area(piece.Ring);
            rings.Add(piece.Ring);
        }
        var slack = tol * Perimeter(shape);
        if (Math.Abs(area - Area(shape)) > slack)
        {
            why = "Not split: the pieces would overlap.";
            return false;
        }
        var union = WallEdit.Group(RoomDetect.Union(rings, tol));
        if (union.Count != 1 || union[0].Count != shape.Count || Math.Abs(Area(union[0]) - Area(shape)) > slack)
        {
            why = "Not split: the pieces would not make the same wall.";
            return false;
        }
        return true;
    }

    /// <summary>No two edges that are not neighbours meet.</summary>
    static bool Simple(List<Pt> ring, double tol)
    {
        var n = ring.Count;
        for (var i = 0; i < n; i++)
            for (var j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;
                if (Crosses(ring[i], ring[(i + 1) % n], ring[j], ring[(j + 1) % n])) return false;
            }
        return true;
    }

    static bool Crosses(Pt a, Pt b, Pt p, Pt q)
    {
        var d1 = Cross(Sub(b, a), Sub(p, a));
        var d2 = Cross(Sub(b, a), Sub(q, a));
        var d3 = Cross(Sub(q, p), Sub(a, p));
        var d4 = Cross(Sub(q, p), Sub(b, p));
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    /// <summary>How much of two rings' boundaries lie on each other.</summary>
    static double Shared(List<Pt> a, List<Pt> b, double tol)
    {
        var length = 0.0;
        for (var i = 0; i < a.Count; i++)
        {
            var p = a[i];
            var q = a[(i + 1) % a.Count];
            var len = Dist(p, q);
            if (len <= tol) continue;
            var u = new Pt((q.X - p.X) / len, (q.Y - p.Y) / len);
            for (var j = 0; j < b.Count; j++)
            {
                var r = b[j];
                var s = b[(j + 1) % b.Count];
                if (Math.Abs(Cross(u, Sub(r, p))) > tol || Math.Abs(Cross(u, Sub(s, p))) > tol) continue;
                var lo = Math.Max(0, Math.Min(Dot(Sub(r, p), u), Dot(Sub(s, p), u)));
                var hi = Math.Min(len, Math.Max(Dot(Sub(r, p), u), Dot(Sub(s, p), u)));
                if (hi > lo) length += hi - lo;
            }
        }
        return length;
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
        foreach (var ring in rings) length += Perimeter(ring);
        return length;
    }

    static double Perimeter(List<Pt> ring)
    {
        var length = 0.0;
        for (var i = 0; i < ring.Count; i++) length += Dist(ring[i], ring[(i + 1) % ring.Count]);
        return length;
    }

    static Pt Centre(List<Pt> ring)
    {
        double x = 0, y = 0;
        foreach (var p in ring)
        {
            x += p.X;
            y += p.Y;
        }
        return new Pt(x / ring.Count, y / ring.Count);
    }

    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
    static double Cross(Pt a, Pt b) => a.X * b.Y - a.Y * b.X;
    static Pt Sub(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);
    static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static string Mm(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
    static string At(Pt p) => "(" + Mm(p.X) + ", " + Mm(p.Y) + ")";
}
