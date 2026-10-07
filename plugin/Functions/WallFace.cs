using System;
using System.Collections.Generic;
using System.Globalization;
using RhinoMCPPlugin.Forsk;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// WF: a wall edited by the face clicked. A click resolves to an end face, a
/// side face or the top face from the run's own lines (WallEdit.Run), not
/// from a Brep face index. An end face changes the length, a side face moves
/// the wall (move_wall) or, with Thickness, moves that face alone. Both edits
/// go through WallJoins.TryMove with a run of no thickness on the one face
/// line, so the walls that meet that face slide along their own lines, as
/// they do for a move. Pure geometry, no RhinoCommon, so it tests headless.
/// </summary>
public static class WallFace
{
    /// <summary>A click picks the nearest face within this reach, mm.</summary>
    public const double ReachMm = 300.0;
    /// <summary>A click this close below the wall's top, mm, is on the top face.</summary>
    const double TopMm = 5.0;

    public enum Kind
    {
        /// <summary>The square end of a wall that stands free there. It changes the length.</summary>
        End,
        /// <summary>One of the two long faces. It moves the wall, or with Thickness moves alone.</summary>
        Side,
        /// <summary>The top. It drags as the nearer side face does.</summary>
        Top
    }

    public sealed class Hit
    {
        public Kind Kind;
        /// <summary>The run's index in the graph.</summary>
        public int Index;
        public WallEdit.Run Run;
        /// <summary>+1 for the end at Hi, -1 for the end at Lo. 0 for a side or the top.</summary>
        public int End;
        /// <summary>+1 for the face at Far, -1 for the face at Near: the face picked, or the top's nearer side.</summary>
        public int Side;
        /// <summary>Unit plan vector, out of the wall material through the face.</summary>
        public Pt Out;
        /// <summary>The middle of the face in plan. Given back as at, it picks the same face.</summary>
        public Pt Middle;
        public double Distance;
    }

    /// <summary>
    /// The face nearest the plan point. z, when given (Perspective), above
    /// top less 5 mm is the top face. In a plan view z is left out and the
    /// click picks by plan position only. An end face is offered only where
    /// the wall stands free; at a corner or a tee the end is another wall's
    /// side. A tie goes to the side. only limits the pick to one kind.
    /// </summary>
    public static bool TryPick(WallJoins.Graph graph, Pt at, double? z, double top, double tol, out Hit hit, out string why, Kind? only = null)
    {
        hit = null;
        why = null;
        if (graph?.Runs == null || graph.Runs.Count == 0)
        {
            why = "No straight wall to edit.";
            return false;
        }
        var onTop = z.HasValue && z.Value >= top - Math.Max(TopMm, tol);
        Hit best = null;
        for (var i = 0; i < graph.Runs.Count; i++)
        {
            var run = graph.Runs[i];
            if (only != Kind.End)
                foreach (var e in run.Edges)
                {
                    var a = graph.Shape[e.Loop][e.Edge];
                    var b = graph.Shape[e.Loop][(e.Edge + 1) % graph.Shape[e.Loop].Count];
                    var c = Dot(a, run.Normal);
                    var side = Math.Abs(c - run.Far) <= Math.Abs(c - run.Near) ? 1 : -1;
                    Offer(ref best, new Hit
                    {
                        Kind = Kind.Side,
                        Index = i,
                        Run = run,
                        Side = side,
                        Out = Scale(run.Normal, side),
                        Middle = Mid(a, b),
                        Distance = DistToSeg(at, a, b)
                    }, tol);
                }
            if (only == Kind.Side || onTop) continue;
            foreach (var end in new[] { -1, 1 })
            {
                if (!FreeEnd(graph.Shape, run, end, tol, out var a, out var b)) continue;
                Offer(ref best, new Hit
                {
                    Kind = Kind.End,
                    Index = i,
                    Run = run,
                    End = end,
                    Out = Scale(run.Dir, end),
                    Middle = Mid(a, b),
                    Distance = DistToSeg(at, a, b)
                }, tol);
            }
        }
        if (best == null || best.Distance > ReachMm + tol)
        {
            why = "No wall face within " + Mm(ReachMm) + " mm of " + At(at) + ".";
            return false;
        }
        if (onTop) best.Kind = Kind.Top;
        hit = best;
        return true;
    }

    /// <summary>
    /// The end face moved out by by (below 0 shortens). Only the two corners
    /// of the end move, along the wall's faces, so the other end and every
    /// opening keep their place. A wall left no longer than it is thick is
    /// refused, and so is one that would cross another wall.
    /// </summary>
    public static bool TryStretch(IList<List<List<Pt>>> records, WallJoins.Graph graph, Hit hit, double by, double tol, out WallJoins.Moved moved, out string why)
    {
        moved = null;
        why = null;
        if (hit?.Run == null || hit.Kind != Kind.End)
        {
            why = "Only an end face changes the length.";
            return false;
        }
        var run = hit.Run;
        if (Math.Abs(by) < tol)
        {
            why = "Not changed: the length is the same.";
            return false;
        }
        if (run.Length + by <= run.Thickness + tol)
        {
            why = "Not shortened: " + Mm(-by) + " mm would leave the wall no longer than it is thick ("
                + Mm(run.Length) + " mm long, " + Mm(run.Thickness) + " mm thick).";
            return false;
        }
        var line = hit.End > 0 ? run.Hi : run.Lo;
        // The end as a run of no thickness: its line is across the wall, its length the wall's thickness.
        var face = new WallEdit.Run
        {
            Dir = run.Normal,
            Normal = run.Dir,
            Near = line,
            Far = line,
            Lo = run.Near,
            Hi = run.Far
        };
        if (!WallJoins.TryMove(records, graph, face, hit.End * by, tol, out moved, out why))
        {
            why = Reword(why, by > 0 ? "Not lengthened:" : "Not shortened:");
            return false;
        }
        return true;
    }

    /// <summary>
    /// The side face moved alone, so the wall is thickness thick and the face
    /// across stays put. The walls that meet the moved face (a corner, a tee)
    /// slide along their own lines, as they do for a move.
    /// </summary>
    public static bool TryThicken(IList<List<List<Pt>>> records, WallJoins.Graph graph, Hit hit, double thickness, double tol, out WallJoins.Moved moved, out double by, out string why)
    {
        moved = null;
        by = 0;
        why = null;
        if (hit?.Run == null || hit.Kind == Kind.End)
        {
            why = "Only a side face changes the thickness.";
            return false;
        }
        var run = hit.Run;
        if (thickness <= 0 || thickness > WallEdit.MaxThickMm)
        {
            why = "thickness is above 0 and at most " + Mm(WallEdit.MaxThickMm) + " mm.";
            return false;
        }
        var change = thickness - run.Thickness;
        if (Math.Abs(change) < tol)
        {
            why = "Not changed: the wall is already " + Mm(run.Thickness) + " mm thick.";
            return false;
        }
        var line = hit.Side > 0 ? run.Far : run.Near;
        var face = new WallEdit.Run
        {
            Dir = run.Dir,
            Normal = run.Normal,
            Near = line,
            Far = line,
            Lo = run.Lo,
            Hi = run.Hi
        };
        by = hit.Side * change;
        if (!WallJoins.TryMove(records, graph, face, by, tol, out moved, out why))
        {
            why = Reword(why, "Thickness not changed:");
            return false;
        }
        return true;
    }

    /// <summary>The signed move along the run's normal for a side face dragged out by distance (below 0: in).</summary>
    public static double Across(Hit hit, double distance) => hit == null ? 0 : hit.Side * distance;

    /// <summary>
    /// The end at Lo or Hi is free when one edge of the shape lies across the
    /// wall there, from one face to the other. a and b are its ends.
    /// </summary>
    internal static bool FreeEnd(List<List<Pt>> shape, WallEdit.Run run, int end, double tol, out Pt a, out Pt b)
    {
        a = default;
        b = default;
        var line = end > 0 ? run.Hi : run.Lo;
        foreach (var e in WallEdit.Edges(shape, tol))
        {
            var p = shape[e.Loop][e.Edge];
            var q = shape[e.Loop][(e.Edge + 1) % shape[e.Loop].Count];
            if (Math.Abs(Dot(p, run.Dir) - line) > tol || Math.Abs(Dot(q, run.Dir) - line) > tol) continue;
            var cp = Dot(p, run.Normal);
            var cq = Dot(q, run.Normal);
            if (Math.Min(cp, cq) > run.Near + tol || Math.Max(cp, cq) < run.Far - tol) continue;
            a = p;
            b = q;
            return true;
        }
        return false;
    }

    /// <summary>The wall's length after an end edit, or its thickness after a side edit, for the live dimension.</summary>
    public static double After(Hit hit, double by)
    {
        if (hit?.Run == null) return 0;
        return hit.Kind == Kind.End ? hit.Run.Length + by : hit.Run.Thickness + by;
    }

    /// <summary>
    /// The live dimension. by is the snapped millimetres out through the face.
    /// An end reads its change and the new length, Thickness the new
    /// thickness, a move out or in from the face clicked.
    /// </summary>
    public static string Dimension(Hit hit, double by, bool thickness, bool nb)
    {
        if (hit?.Run == null) return "";
        var n = (long)Math.Round(by, MidpointRounding.AwayFromZero);
        if (hit.Kind == Kind.End)
        {
            if (n == 0) return Text("wall.drag.dim.zero", nb);
            return ForskText.Format(Key(n > 0 ? "wall.face.dim.longer" : "wall.face.dim.shorter", nb),
                "n", Mm(n), "len", Mm(hit.Run.Length + n));
        }
        if (thickness)
            return ForskText.Format(Key("wall.face.dim.thick", nb), "n", Mm(hit.Run.Thickness + n));
        return WallDrag.FormatDimension(n, 1, nb);
    }

    /// <summary>
    /// A typed number as the signed millimetres out through the face: a
    /// change for an end or a move, the new thickness for Thickness.
    /// </summary>
    public static double Typed(Hit hit, double number, bool thickness) =>
        thickness && hit?.Run != null ? number - hit.Run.Thickness : number;

    /// <summary>What the face is, for the prompt: Thickness is offered on a side and on the top.</summary>
    public static string Prompt(Hit hit, bool nb) =>
        Text(hit?.Kind == Kind.End ? "wall.face.prompt.end" : "wall.face.prompt.side", nb);

    static string Key(string key, bool nb) => nb && ForskText.Has(key + ".nb") ? key + ".nb" : key;
    static string Text(string key, bool nb) => ForskText.Get(Key(key, nb));

    static void Offer(ref Hit best, Hit next, double tol)
    {
        if (best == null) { best = next; return; }
        if (next.Distance < best.Distance - tol) { best = next; return; }
        // A tie goes to the side: at a corner the end of one wall is the other's side.
        if (next.Distance <= best.Distance + tol && best.Kind == Kind.End && next.Kind == Kind.Side) best = next;
    }

    static string Reword(string why, string lead)
    {
        if (string.IsNullOrEmpty(why)) return lead.TrimEnd(':') + ".";
        return why.StartsWith("Not moved:", StringComparison.Ordinal) ? lead + why.Substring("Not moved:".Length) : why;
    }

    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
    static Pt Scale(Pt a, double s) => new Pt(a.X * s, a.Y * s);
    static Pt Mid(Pt a, Pt b) => new Pt((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

    static double DistToSeg(Pt p, Pt a, Pt b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var len2 = abx * abx + aby * aby;
        var t = len2 > 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2)) : 0;
        var dx = p.X - (a.X + abx * t);
        var dy = p.Y - (a.Y + aby * t);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static string Mm(double v) => Math.Round(Math.Abs(v)).ToString("0", CultureInfo.InvariantCulture);
    static string At(Pt p) => "(" + Math.Round(p.X).ToString("0", CultureInfo.InvariantCulture) + ", " + Math.Round(p.Y).ToString("0", CultureInfo.InvariantCulture) + ")";
}
