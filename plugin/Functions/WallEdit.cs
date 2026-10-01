using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// One straight wall run inside a wall record. A baked plan is one record:
/// forsk:path is the outer loop, then the rooms it closes as hole loops. A run
/// is the wall between two parallel faces, the face picked and the face across
/// the wall, with every edge on those two lines that joins up with them along
/// the run. Moving it moves both faces; the walls that meet it stretch to
/// follow (naive: no other join logic). Pure geometry, no RhinoCommon, so it
/// tests headless.
/// </summary>
public static class WallEdit
{
    /// <summary>The face across the wall is at most this far: a wall is no thicker.</summary>
    public const double MaxThickMm = RoomDetect.MaxEndMm;
    /// <summary>A point picks the wall face nearest it, within this reach.</summary>
    public const double PickReachMm = 1000.0;
    /// <summary>Two edges this close to parallel (sine of the angle between them) can share a line.</summary>
    const double ParallelSin = 0.01;
    /// <summary>A compass word moves a run only when the run's normal is within 45° of it.</summary>
    const double CompassCos = 0.7;

    public sealed class Run
    {
        /// <summary>Unit vector along the run.</summary>
        public Pt Dir;
        /// <summary>Unit normal, pointing east or north. The faces sit at Near and Far along it, the wall between.</summary>
        public Pt Normal;
        public double Near, Far;
        /// <summary>Extent along Dir of the face edges in the run.</summary>
        public double Lo, Hi;
        /// <summary>Each face edge in the run: edge i of a loop runs from vertex i to vertex i + 1.</summary>
        public List<(int Loop, int Edge)> Edges = new List<(int Loop, int Edge)>();
        public double Thickness => Far - Near;
        public double Length => Hi - Lo;
    }

    /// <summary>forsk:path as loops: the outer loop first, then the holes. Null when it does not read.</summary>
    public static List<List<Pt>> Rings(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        JObject obj;
        try { obj = JObject.Parse(path); }
        catch (JsonException) { return null; }
        var outer = Loop(obj["outer"]);
        if (outer == null) return null;
        var rings = new List<List<Pt>> { outer };
        if (obj["holes"] is JArray holes)
            foreach (var hole in holes)
            {
                var ring = Loop(hole);
                if (ring != null) rings.Add(ring);
            }
        return rings;
    }

    /// <summary>The loops back as forsk:path, in the plugin's own form (no closing point, 3 decimals).</summary>
    public static string Path(List<List<Pt>> rings)
    {
        var obj = new JObject { ["outer"] = Json(rings[0]) };
        if (rings.Count > 1)
        {
            var holes = new JArray();
            for (var i = 1; i < rings.Count; i++) holes.Add(Json(rings[i]));
            obj["holes"] = holes;
        }
        return obj.ToString(Formatting.None);
    }

    /// <summary>The run whose face is nearest the point.</summary>
    public static bool TryPick(List<List<Pt>> rings, Pt at, double tol, out Run run, out string why)
    {
        run = null;
        var best = double.MaxValue;
        (int Loop, int Edge) pick = (-1, -1);
        foreach (var e in Edges(rings, tol))
        {
            var d = DistToSeg(at, A(rings, e), B(rings, e));
            if (d < best)
            {
                best = d;
                pick = e;
            }
        }
        if (pick.Loop < 0 || best > PickReachMm)
        {
            why = "No wall face within " + Mm(PickReachMm) + " mm of " + At(at) + ".";
            return false;
        }
        return TryRunFrom(rings, pick, tol, out run, out why);
    }

    /// <summary>How far the point is from the nearest wall face.</summary>
    public static double Distance(List<List<Pt>> rings, Pt at, double tol)
    {
        var best = double.MaxValue;
        foreach (var e in Edges(rings, tol))
            best = Math.Min(best, DistToSeg(at, A(rings, e), B(rings, e)));
        return best;
    }

    /// <summary>The outer wall on one side: the outer loop's face that faces that way, furthest out.</summary>
    public static bool TryPickSide(List<List<Pt>> rings, string compass, double tol, out Run run, out string why)
    {
        run = null;
        if (!Sections.TryCompass(compass, out var w))
        {
            why = "side is north, south, east or west.";
            return false;
        }
        var best = double.MinValue;
        (int Loop, int Edge) pick = (-1, -1);
        foreach (var e in Edges(rings, tol))
        {
            if (e.Loop != 0) continue;
            var a = A(rings, e);
            var b = B(rings, e);
            var u = Unit(a, b);
            var n = new Pt(-u.Y, u.X);
            var mid = Mid(a, b);
            // Outward: away from the wall material.
            if (InRegion(rings, Along(mid, n, 2.0 * tol))) n = new Pt(-n.X, -n.Y);
            if (Dot(n, w) < 1.0 - ParallelSin) continue;
            var reach = Dot(mid, w) + 1e-6 * Dist(a, b);
            if (reach > best)
            {
                best = reach;
                pick = e;
            }
        }
        if (pick.Loop < 0)
        {
            why = "No outer wall faces " + compass.Trim().ToLowerInvariant() + ".";
            return false;
        }
        return TryRunFrom(rings, pick, tol, out run, out why);
    }

    /// <summary>
    /// The signed distance along the run's normal for a move toward a compass
    /// side. A wall moves across itself, so a run along the way asked is refused.
    /// </summary>
    public static bool TryToward(Run run, string compass, double distance, out double by, out string why)
    {
        by = 0;
        why = null;
        if (!Sections.TryCompass(compass, out var w))
        {
            why = "toward is north, south, east or west.";
            return false;
        }
        var dot = Dot(run.Normal, w);
        if (Math.Abs(dot) < CompassCos)
        {
            why = "That wall runs " + Runs(run) + ", so it moves " + Across(run) + ".";
            return false;
        }
        by = Math.Sign(dot) * Math.Abs(distance);
        return true;
    }

    /// <summary>
    /// Both faces of the run moved by along the normal. A wall that meets the
    /// run stretches or shortens with it; one that would close up (the room
    /// or wall beyond run out of depth) or cross another wall refuses the move.
    /// </summary>
    public static bool TryMove(List<List<Pt>> rings, Run run, double by, double tol, out List<List<Pt>> moved, out string why)
    {
        moved = null;
        why = null;
        var shift = new Pt(run.Normal.X * by, run.Normal.Y * by);
        var moving = new HashSet<(int, int)>();
        foreach (var e in run.Edges)
        {
            moving.Add((e.Loop, e.Edge));
            moving.Add((e.Loop, (e.Edge + 1) % rings[e.Loop].Count));
        }

        var next = new List<List<Pt>>();
        for (var k = 0; k < rings.Count; k++)
        {
            var ring = new List<Pt>(rings[k]);
            for (var i = 0; i < ring.Count; i++)
                if (moving.Contains((k, i))) ring[i] = new Pt(Round(ring[i].X + shift.X), Round(ring[i].Y + shift.Y));
            next.Add(ring);
        }

        // A wall that meets the run: one end moves, and it must keep its direction.
        var closest = double.MaxValue;
        var changed = new List<(int Loop, int Edge)>();
        for (var k = 0; k < rings.Count; k++)
            for (var i = 0; i < rings[k].Count; i++)
            {
                var j = (i + 1) % rings[k].Count;
                var ends = (moving.Contains((k, i)) ? 1 : 0) + (moving.Contains((k, j)) ? 1 : 0);
                if (ends == 0) continue;
                changed.Add((k, i));
                if (ends == 2) continue;
                var before = Sub(rings[k][j], rings[k][i]);
                var after = Sub(next[k][j], next[k][i]);
                if (Dot(before, after) <= 0 || Len(after) < tol)
                    closest = Math.Min(closest, Math.Abs(Dot(before, run.Normal)));
            }
        if (closest < double.MaxValue)
        {
            why = "Not moved: " + Mm(Math.Abs(by)) + " mm would close the room or wall beyond it, "
                + Mm(closest) + " mm deep.";
            return false;
        }

        foreach (var c in changed)
        {
            var a = next[c.Loop][c.Edge];
            var b = next[c.Loop][(c.Edge + 1) % next[c.Loop].Count];
            for (var k = 0; k < next.Count; k++)
                for (var i = 0; i < next[k].Count; i++)
                {
                    if (k == c.Loop && Near(i, c.Edge, next[k].Count)) continue;
                    var p = next[k][i];
                    var q = next[k][(i + 1) % next[k].Count];
                    if (SegsMeet(a, b, p, q, tol))
                    {
                        why = "Not moved: the wall would cross another wall near " + At(Mid(a, b)) + ".";
                        return false;
                    }
                }
        }

        moved = new List<List<Pt>>();
        foreach (var ring in next) moved.Add(RoomDetect.Simplify(ring, tol));
        return true;
    }

    /// <summary>
    /// An opening that stays where it is (on a wall that meets the run) is
    /// clear of the move when its marker still sits in the walls and the run,
    /// in its new place, does not run across it.
    /// </summary>
    public static bool Clear(List<List<Pt>> moved, Run run, double by, RoomDetect.Box marker, double tol)
    {
        var center = new Pt((marker.MinX + marker.MaxX) / 2.0, (marker.MinY + marker.MaxY) / 2.0);
        if (!InRegion(moved, center)) return false;
        double sLo = double.MaxValue, sHi = double.MinValue, cLo = double.MaxValue, cHi = double.MinValue;
        foreach (var p in new[]
                 {
                     new Pt(marker.MinX, marker.MinY), new Pt(marker.MaxX, marker.MinY),
                     new Pt(marker.MaxX, marker.MaxY), new Pt(marker.MinX, marker.MaxY)
                 })
        {
            var s = Dot(p, run.Dir);
            var c = Dot(p, run.Normal);
            sLo = Math.Min(sLo, s);
            sHi = Math.Max(sHi, s);
            cLo = Math.Min(cLo, c);
            cHi = Math.Max(cHi, c);
        }
        var alongRun = Math.Min(sHi, run.Hi) - Math.Max(sLo, run.Lo);
        var acrossRun = Math.Min(cHi, run.Far + by) - Math.Max(cLo, run.Near + by);
        return alongRun <= tol || acrossRun <= tol;
    }

    /// <summary>A plan point in the run's wall band, ends and faces included.</summary>
    public static bool InBand(Run run, Pt p, double tol)
    {
        var s = Dot(p, run.Dir);
        var c = Dot(p, run.Normal);
        return s >= run.Lo - tol && s <= run.Hi + tol && c >= run.Near - tol && c <= run.Far + tol;
    }

    /// <summary>The way the run runs and the way it moves, as compass words.</summary>
    public static string Runs(Run run) => Math.Abs(run.Dir.X) >= Math.Abs(run.Dir.Y) ? "east–west" : "north–south";

    public static string Across(Run run) => Math.Abs(run.Normal.Y) >= Math.Abs(run.Normal.X) ? "north or south" : "east or west";

    /// <summary>The compass word for a signed move along the run's normal.</summary>
    public static string Heading(Run run, double by)
    {
        var x = run.Normal.X * by;
        var y = run.Normal.Y * by;
        if (Math.Abs(y) >= Math.Abs(x)) return y >= 0 ? "north" : "south";
        return x >= 0 ? "east" : "west";
    }

    static bool TryRunFrom(List<List<Pt>> rings, (int Loop, int Edge) pick, double tol, out Run run, out string why)
    {
        run = null;
        why = null;
        var a0 = A(rings, pick);
        var b0 = B(rings, pick);
        var u = Unit(a0, b0);
        var n = new Pt(-u.Y, u.X);
        if (n.X < -1e-9 || (Math.Abs(n.X) <= 1e-9 && n.Y < 0))
        {
            u = new Pt(-u.X, -u.Y);
            n = new Pt(-n.X, -n.Y);
        }
        var c0 = Dot(n, a0);
        // The wall material lies on side s of the picked face.
        var s = InRegion(rings, Along(Mid(a0, b0), n, 2.0 * tol)) ? 1.0 : -1.0;
        var lo0 = Math.Min(Dot(u, a0), Dot(u, b0));
        var hi0 = Math.Max(Dot(u, a0), Dot(u, b0));

        var across = double.MaxValue;
        foreach (var e in Edges(rings, tol))
        {
            if (!OnLine(rings, e, u, n, out var c)) continue;
            var d = s * (c - c0);
            if (d <= tol || d > MaxThickMm + tol || d >= across) continue;
            if (Overlap(rings, e, u, lo0, hi0) <= tol) continue;
            if (!InRegion(rings, Along(Mid(A(rings, e), B(rings, e)), n, -s * 2.0 * tol))) continue;
            across = d;
        }
        if (across == double.MaxValue)
        {
            why = "No face across the wall within " + Mm(MaxThickMm) + " mm of " + At(Mid(a0, b0)) + ".";
            return false;
        }

        var c1 = c0 + s * across;
        run = new Run
        {
            Dir = u,
            Normal = n,
            Near = Math.Min(c0, c1),
            Far = Math.Max(c0, c1),
            Lo = lo0,
            Hi = hi0
        };
        // Face edges on either line, wall between, joined up along the run.
        var candidates = new List<(int Loop, int Edge)>();
        foreach (var e in Edges(rings, tol))
        {
            if (!OnLine(rings, e, u, n, out var c)) continue;
            var onNear = Math.Abs(c - run.Near) <= tol;
            var onFar = Math.Abs(c - run.Far) <= tol;
            if (!onNear && !onFar) continue;
            var inward = onNear ? 2.0 * tol : -2.0 * tol;
            if (!InRegion(rings, Along(Mid(A(rings, e), B(rings, e)), n, inward))) continue;
            candidates.Add(e);
        }
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var e in candidates)
            {
                if (run.Edges.Contains(e)) continue;
                var lo = Math.Min(Dot(u, A(rings, e)), Dot(u, B(rings, e)));
                var hi = Math.Max(Dot(u, A(rings, e)), Dot(u, B(rings, e)));
                if (hi < run.Lo - tol || lo > run.Hi + tol) continue;
                run.Edges.Add(e);
                run.Lo = Math.Min(run.Lo, lo);
                run.Hi = Math.Max(run.Hi, hi);
                grew = true;
            }
        }
        return true;
    }

    static List<(int Loop, int Edge)> Edges(List<List<Pt>> rings, double tol)
    {
        var list = new List<(int, int)>();
        for (var k = 0; k < rings.Count; k++)
            for (var i = 0; i < rings[k].Count; i++)
                if (Dist(rings[k][i], rings[k][(i + 1) % rings[k].Count]) > tol) list.Add((k, i));
        return list;
    }

    static bool OnLine(List<List<Pt>> rings, (int Loop, int Edge) e, Pt u, Pt n, out double c)
    {
        var a = A(rings, e);
        var b = B(rings, e);
        c = Dot(n, a);
        var v = Unit(a, b);
        return Math.Abs(Cross(u, v)) < ParallelSin && Math.Abs(Dot(n, b) - c) < 1.0;
    }

    static double Overlap(List<List<Pt>> rings, (int Loop, int Edge) e, Pt u, double lo, double hi)
    {
        var a = Dot(u, A(rings, e));
        var b = Dot(u, B(rings, e));
        return Math.Min(hi, Math.Max(a, b)) - Math.Max(lo, Math.Min(a, b));
    }

    /// <summary>Inside the wall: inside an odd number of the loops.</summary>
    public static bool InRegion(List<List<Pt>> rings, Pt p)
    {
        var inside = false;
        foreach (var ring in rings)
            if (RoomDetect.Contains(ring, p)) inside = !inside;
        return inside;
    }

    static bool Near(int i, int j, int count) => i == j || (i + 1) % count == j || (j + 1) % count == i;

    /// <summary>Two segments that cross or come within tol of each other.</summary>
    static bool SegsMeet(Pt a, Pt b, Pt p, Pt q, double tol)
    {
        var d1 = Cross(Sub(b, a), Sub(p, a));
        var d2 = Cross(Sub(b, a), Sub(q, a));
        var d3 = Cross(Sub(q, p), Sub(a, p));
        var d4 = Cross(Sub(q, p), Sub(b, p));
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;
        return DistToSeg(a, p, q) < tol || DistToSeg(b, p, q) < tol
            || DistToSeg(p, a, b) < tol || DistToSeg(q, a, b) < tol;
    }

    static List<Pt> Loop(JToken token)
    {
        if (!(token is JArray arr) || arr.Count < 3) return null;
        var pts = new List<Pt>();
        foreach (var item in arr)
        {
            if (!(item is JArray xy) || xy.Count < 2) return null;
            if (xy[0].Type != JTokenType.Float && xy[0].Type != JTokenType.Integer) return null;
            if (xy[1].Type != JTokenType.Float && xy[1].Type != JTokenType.Integer) return null;
            pts.Add(new Pt(xy[0].Value<double>(), xy[1].Value<double>()));
        }
        if (pts.Count > 3 && Dist(pts[0], pts[pts.Count - 1]) <= 1e-6) pts.RemoveAt(pts.Count - 1);
        return pts.Count >= 3 ? pts : null;
    }

    static JArray Json(List<Pt> ring)
    {
        var arr = new JArray();
        foreach (var p in ring) arr.Add(new JArray(Round(p.X), Round(p.Y)));
        return arr;
    }

    static Pt A(List<List<Pt>> rings, (int Loop, int Edge) e) => rings[e.Loop][e.Edge];
    static Pt B(List<List<Pt>> rings, (int Loop, int Edge) e) => rings[e.Loop][(e.Edge + 1) % rings[e.Loop].Count];
    static double Round(double v) => Math.Round(v, 3);
    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
    static double Cross(Pt a, Pt b) => a.X * b.Y - a.Y * b.X;
    static Pt Sub(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);
    static double Len(Pt a) => Math.Sqrt(a.X * a.X + a.Y * a.Y);
    static double Dist(Pt a, Pt b) => Len(Sub(a, b));
    static Pt Mid(Pt a, Pt b) => new Pt((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
    static Pt Along(Pt p, Pt dir, double by) => new Pt(p.X + dir.X * by, p.Y + dir.Y * by);

    static Pt Unit(Pt a, Pt b)
    {
        var d = Sub(b, a);
        var len = Len(d);
        return len > 0 ? new Pt(d.X / len, d.Y / len) : new Pt(1, 0);
    }

    static double DistToSeg(Pt p, Pt a, Pt b)
    {
        var ab = Sub(b, a);
        var len2 = Dot(ab, ab);
        var t = len2 > 0 ? Math.Max(0, Math.Min(1, Dot(Sub(p, a), ab) / len2)) : 0;
        return Dist(p, new Pt(a.X + ab.X * t, a.Y + ab.Y * t));
    }

    static string Mm(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
    static string At(Pt p) => "(" + Mm(p.X) + ", " + Mm(p.Y) + ")";
}
