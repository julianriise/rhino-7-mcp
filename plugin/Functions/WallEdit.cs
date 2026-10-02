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
    /// <summary>A wall that meets a moving run at under 15° (sine) would slide too far along itself.</summary>
    const double MinMeetSin = 0.26;

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
    /// Both faces of the run moved by along the normal. Every vertex on one of
    /// the run's face lines, within its length, moves (Slides), so a wall that
    /// meets the run stretches or shortens with it and keeps its direction.
    /// One that would close up (the room or wall beyond run out of depth) or
    /// cross another wall refuses the move.
    /// </summary>
    public static bool TryMove(List<List<Pt>> rings, Run run, double by, double tol, out List<List<Pt>> moved, out string why)
    {
        moved = null;
        var slides = Slides(rings, run, by, tol, out why);
        if (slides == null) return false;

        var next = new List<List<Pt>>();
        for (var k = 0; k < rings.Count; k++)
        {
            var ring = new List<Pt>(rings[k]);
            for (var i = 0; i < ring.Count; i++)
                if (slides.TryGetValue((k, i), out var to)) ring[i] = to;
            next.Add(ring);
        }

        // An edge with a moving end must keep its direction and some length.
        var closest = double.MaxValue;
        var changed = new List<(int Loop, int Edge)>();
        for (var k = 0; k < rings.Count; k++)
            for (var i = 0; i < rings[k].Count; i++)
            {
                var j = (i + 1) % rings[k].Count;
                var ends = (slides.ContainsKey((k, i)) ? 1 : 0) + (slides.ContainsKey((k, j)) ? 1 : 0);
                if (ends == 0) continue;
                changed.Add((k, i));
                var before = Sub(rings[k][j], rings[k][i]);
                var after = Sub(next[k][j], next[k][i]);
                if (ends == 2 && Len(before) < tol) continue;
                if (Dot(before, after) <= 0 || Len(after) < tol)
                    closest = Math.Min(closest, ends == 1 ? Math.Abs(Dot(before, run.Normal)) : Len(before));
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
    /// Where each vertex on one of the run's face lines, within its length,
    /// goes. It slides along the one edge of its ring that leaves that line,
    /// so a wall meeting the run at any angle keeps its direction and
    /// thickness; at 90° that is a move along the normal. A vertex with no
    /// such edge, or two, moves along the normal. Null, with why, when a wall
    /// meets the run at under 15° and would slide too far along itself.
    /// </summary>
    internal static Dictionary<(int Loop, int Index), Pt> Slides(List<List<Pt>> rings, Run run, double by, double tol, out string why)
    {
        why = null;
        var slides = new Dictionary<(int, int), Pt>();
        for (var k = 0; k < rings.Count; k++)
        {
            var ring = rings[k];
            for (var i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                var s = Dot(p, run.Dir);
                if (s < run.Lo - tol || s > run.Hi + tol) continue;
                var c = Dot(p, run.Normal);
                double line;
                if (Math.Abs(c - run.Near) <= tol) line = run.Near;
                else if (Math.Abs(c - run.Far) <= tol) line = run.Far;
                else continue;

                var leaving = 0;
                var leave = default(Pt);
                foreach (var q in new[] { ring[(i - 1 + ring.Count) % ring.Count], ring[(i + 1) % ring.Count] })
                {
                    if (Math.Abs(Dot(q, run.Normal) - line) <= tol) continue;
                    leaving++;
                    leave = Unit(p, q);
                }
                var to = Along(p, run.Normal, by);
                if (leaving == 1)
                {
                    var dn = Dot(leave, run.Normal);
                    if (Math.Abs(dn) < MinMeetSin)
                    {
                        why = "Not moved: a wall meets it at under 15° near " + At(p) + ".";
                        return null;
                    }
                    to = Along(p, leave, by / dn);
                }
                slides[(k, i)] = new Pt(Round(to.X), Round(to.Y));
            }
        }
        return slides;
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
        return !Overlaps(run.Dir, run.Normal, run.Lo, run.Hi, run.Near + by, run.Far + by, marker, tol);
    }

    /// <summary>The run's wall as a closed loop: its length between its two faces.</summary>
    public static List<Pt> Band(Run run) => new List<Pt>
    {
        At(run, run.Lo, run.Near), At(run, run.Hi, run.Near), At(run, run.Hi, run.Far), At(run, run.Lo, run.Far)
    };

    /// <summary>
    /// The record with the run taken out. Left is null when the run was the
    /// whole record (a wall standing free). A delete that would leave the
    /// record in two or more pieces is refused: this version keeps one record.
    /// </summary>
    public static bool TryDelete(List<List<Pt>> rings, Run run, double tol, out List<List<Pt>> left, out string why)
    {
        left = null;
        why = null;
        var regions = Group(RoomDetect.Difference(new[] { rings }, new[] { Band(run) }, tol));
        if (regions.Count > 1)
        {
            why = "Not deleted: the walls left would stand in " + regions.Count + " separate pieces, and this version keeps one wall record.";
            return false;
        }
        if (regions.Count == 1) left = regions[0];
        return true;
    }

    /// <summary>An opening that stays is still held: both ends of its marker, along its longer side, sit in the walls.</summary>
    public static bool Holds(List<List<Pt>> rings, RoomDetect.Box marker)
    {
        var cx = (marker.MinX + marker.MaxX) / 2.0;
        var cy = (marker.MinY + marker.MaxY) / 2.0;
        var alongX = marker.MaxX - marker.MinX >= marker.MaxY - marker.MinY;
        var a = alongX ? new Pt(marker.MinX, cy) : new Pt(cx, marker.MinY);
        var b = alongX ? new Pt(marker.MaxX, cy) : new Pt(cx, marker.MaxY);
        return InRegion(rings, a) && InRegion(rings, b);
    }

    public sealed class Added
    {
        /// <summary>The record the new wall joins (its index in the records given), or -1 for a record of its own.</summary>
        public int Joined = -1;
        /// <summary>The records the new wall touches. Two or more: it stands as a record of its own, joined at both ends (F2).</summary>
        public List<int> Touches = new List<int>();
        /// <summary>The joined record's loops with the wall in, or the new wall's own loop.</summary>
        public List<List<Pt>> Rings;
        public List<Pt> Band;
        /// <summary>The centreline's ends after each has reached the wall face it stops short of.</summary>
        public Pt From, To;
    }

    /// <summary>
    /// A straight wall of the thickness on the centreline from to to. Each end
    /// that stops short of a wall face by at most RoomDetect.ReachMm runs on to
    /// it. The wall joins the one record it touches. One that touches none,
    /// or two or more, stands as a record of its own; with two or more it
    /// shares its ends with them, and the join graph reads them together.
    /// </summary>
    public static bool TryAdd(IList<List<List<Pt>>> records, Pt from, Pt to, double thickness, double tol, out Added added, out string why)
    {
        added = null;
        why = null;
        if (thickness <= 0 || thickness > MaxThickMm)
        {
            why = "thickness is above 0 and at most " + Mm(MaxThickMm) + " mm.";
            return false;
        }
        if (Dist(from, to) <= thickness)
        {
            why = "The wall is no longer than it is thick: give two points further apart.";
            return false;
        }
        var dir = Unit(from, to);
        from = Reach(records, from, new Pt(-dir.X, -dir.Y), tol);
        to = Reach(records, to, dir, tol);
        var band = Strip(from, to, thickness);
        var joined = new List<int>();
        for (var i = 0; i < records.Count; i++)
            if (Group(RoomDetect.Union(new[] { records[i], new List<List<Pt>> { band } }, tol)).Count == 1)
                joined.Add(i);
        added = new Added { Band = band, From = from, To = to, Touches = joined };
        if (joined.Count != 1)
        {
            added.Rings = new List<List<Pt>> { band };
            return true;
        }
        added.Joined = joined[0];
        added.Rings = Group(RoomDetect.Union(new[] { records[joined[0]], new List<List<Pt>> { band } }, tol))[0];
        return true;
    }

    /// <summary>An opening's marker the new wall would run across.</summary>
    public static bool InTheWay(Added added, double thickness, RoomDetect.Box marker, double tol)
    {
        var u = Unit(added.From, added.To);
        var n = new Pt(-u.Y, u.X);
        var c = Dot(added.From, n);
        return Overlaps(u, n, Dot(added.From, u), Dot(added.To, u), c - thickness / 2.0, c + thickness / 2.0, marker, tol);
    }

    /// <summary>A loop of the thickness around the centreline a to b.</summary>
    static List<Pt> Strip(Pt a, Pt b, double thickness)
    {
        var u = Unit(a, b);
        var h = new Pt(-u.Y * thickness / 2.0, u.X * thickness / 2.0);
        return new List<Pt>
        {
            new Pt(Round(a.X - h.X), Round(a.Y - h.Y)), new Pt(Round(b.X - h.X), Round(b.Y - h.Y)),
            new Pt(Round(b.X + h.X), Round(b.Y + h.Y)), new Pt(Round(a.X + h.X), Round(a.Y + h.Y))
        };
    }

    /// <summary>An end outside the walls runs on along dir to the first wall face within reach.</summary>
    static Pt Reach(IList<List<List<Pt>>> records, Pt end, Pt dir, double tol)
    {
        foreach (var rings in records)
            if (InRegion(rings, end) || Distance(rings, end, tol) <= tol) return end;
        var reach = RoomDetect.ReachMm + tol;
        var best = double.MaxValue;
        foreach (var rings in records)
            foreach (var e in Edges(rings, tol))
            {
                var a = A(rings, e);
                var ab = Sub(B(rings, e), a);
                var denom = Cross(dir, ab);
                if (Math.Abs(denom) < 1e-9) continue;
                var ae = Sub(a, end);
                var t = Cross(ae, ab) / denom;
                var s = Cross(ae, dir) / denom;
                if (t > tol && t <= reach && t < best && s >= 0 && s <= 1) best = t;
            }
        return best < double.MaxValue ? Along(end, dir, best) : end;
    }

    /// <summary>Loops from Union or Difference as records: each outline with the holes it closes.</summary>
    internal static List<List<List<Pt>>> Group(List<List<Pt>> loops)
    {
        var outlines = new List<List<Pt>>();
        var holes = new List<List<Pt>>();
        foreach (var loop in loops)
            (RoomDetect.Area(loop) > 0 ? outlines : holes).Add(loop);
        return RoomDetect.Regions(outlines, holes);
    }

    /// <summary>A marker box overlaps the strip between sLo..sHi along u and cLo..cHi along n, by more than tol both ways.</summary>
    static bool Overlaps(Pt u, Pt n, double sLo, double sHi, double cLo, double cHi, RoomDetect.Box marker, double tol)
    {
        double bsLo = double.MaxValue, bsHi = double.MinValue, bcLo = double.MaxValue, bcHi = double.MinValue;
        foreach (var p in new[]
                 {
                     new Pt(marker.MinX, marker.MinY), new Pt(marker.MaxX, marker.MinY),
                     new Pt(marker.MaxX, marker.MaxY), new Pt(marker.MinX, marker.MaxY)
                 })
        {
            bsLo = Math.Min(bsLo, Dot(p, u));
            bsHi = Math.Max(bsHi, Dot(p, u));
            bcLo = Math.Min(bcLo, Dot(p, n));
            bcHi = Math.Max(bcHi, Dot(p, n));
        }
        var along = Math.Min(bsHi, Math.Max(sLo, sHi)) - Math.Max(bsLo, Math.Min(sLo, sHi));
        var across = Math.Min(bcHi, cHi) - Math.Max(bcLo, cLo);
        return along > tol && across > tol;
    }

    static Pt At(Run run, double s, double c) =>
        new Pt(Round(run.Dir.X * s + run.Normal.X * c), Round(run.Dir.Y * s + run.Normal.Y * c));

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

    internal static bool TryRunFrom(List<List<Pt>> rings, (int Loop, int Edge) pick, double tol, out Run run, out string why)
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

    internal static List<(int Loop, int Edge)> Edges(List<List<Pt>> rings, double tol)
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
