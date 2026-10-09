using System;
using System.Collections.Generic;
using System.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Room areas drawn by hand, and drawn areas a new wall splits, as plain
/// points. AI detection misses rooms, so the user clicks a room's corners; a
/// wall drawn across a drawn room cuts that room into the rooms it makes.
/// No RhinoCommon, so it tests headless.
/// </summary>
public static class RoomAreaPlan
{
    public const string TooFew = "An area needs three corners or more.";
    public const string TooSmall = "That area is under 1 m².";
    public const string InWall = "That area lies inside a wall. Click the corners on the walls' inner faces.";
    public const string Crossing = "The outline crosses itself. Click the corners in order around the room.";

    /// <summary>
    /// The clicked corners as a closed ring, counterclockwise. A corner
    /// clicked twice in a row, or the first clicked again to close, counts
    /// once, and so does a corner on a straight line between its neighbours.
    /// </summary>
    public static bool TryRing(IList<Pt> clicks, double tol, out List<Pt> ring, out string why)
    {
        ring = null;
        why = null;
        tol = tol > 0 ? tol : 1.0;
        var pts = new List<Pt>();
        foreach (var p in clicks ?? new List<Pt>())
        {
            if (pts.Count > 0 && Dist(pts[pts.Count - 1], p) <= tol) continue;
            pts.Add(p);
        }
        while (pts.Count > 1 && Dist(pts[0], pts[pts.Count - 1]) <= tol) pts.RemoveAt(pts.Count - 1);
        pts = DropStraight(pts, tol);
        if (pts.Count < 3)
        {
            why = TooFew;
            return false;
        }
        if (SelfCrossing(pts, tol))
        {
            why = Crossing;
            return false;
        }
        var area = RoomDetect.Area(pts);
        if (Math.Abs(area) < RoomDetect.MinAreaMm2)
        {
            why = TooSmall;
            return false;
        }
        if (area < 0) pts.Reverse();
        ring = pts;
        return true;
    }

    /// <summary>
    /// The pieces a wall band cuts a room into, largest first, each an outer
    /// loop of 1 m² or more. Null when the band leaves the room in one piece.
    /// The band runs on past both ends by its thickness here, so a wall that
    /// stops on the face of a wall the outline was drawn a little inside of
    /// still cuts the room through.
    /// </summary>
    public static List<List<Pt>> Split(List<Pt> room, Pt from, Pt to, double thickness, double tol)
    {
        if (room == null || room.Count < 3 || thickness <= 0) return null;
        var length = Dist(from, to);
        if (length <= 0) return null;
        var ux = (to.X - from.X) / length;
        var uy = (to.Y - from.Y) / length;
        var a = new Pt(from.X - ux * thickness, from.Y - uy * thickness);
        var b = new Pt(to.X + ux * thickness, to.Y + uy * thickness);
        var nx = -uy * thickness / 2.0;
        var ny = ux * thickness / 2.0;
        var band = new List<Pt>
        {
            new Pt(a.X + nx, a.Y + ny),
            new Pt(a.X - nx, a.Y - ny),
            new Pt(b.X - nx, b.Y - ny),
            new Pt(b.X + nx, b.Y + ny)
        };
        var left = RoomDetect.Difference(new[] { new List<List<Pt>> { room } }, new[] { band }, tol);
        var pieces = left.Where(r => RoomDetect.Area(r) >= RoomDetect.MinAreaMm2).OrderByDescending(RoomDetect.Area).ToList();
        return pieces.Count >= 2 ? pieces : null;
    }

    /// <summary>
    /// Draw area shows the walls only (Julian 2026-10-09): the user follows
    /// the walls' inner faces and should not think about doors, windows,
    /// furniture, roof or floor. Every Forsk object but a wall (or an existing
    /// wall underlay) is left out of the view while the tool runs. The user's
    /// own curves and the imported plan are not Forsk's and stay.
    /// </summary>
    public static bool HiddenWhileDrawing(string generated, string kind)
    {
        if (generated != "1") return false;
        return !string.Equals(kind, "wall", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kind, "existing", StringComparison.OrdinalIgnoreCase);
    }

    public static string SplitClause(int rooms)
    {
        if (rooms <= 0) return "";
        return rooms == 1 ? " Split 1 room in two." : " Split " + rooms + " rooms.";
    }

    static List<Pt> DropStraight(List<Pt> pts, double tol)
    {
        var changed = true;
        while (changed && pts.Count > 3)
        {
            changed = false;
            for (var i = 0; i < pts.Count && pts.Count > 3; i++)
            {
                var prev = pts[(i + pts.Count - 1) % pts.Count];
                var next = pts[(i + 1) % pts.Count];
                var span = Dist(prev, next);
                if (span <= tol) continue;
                var cross = (next.X - prev.X) * (pts[i].Y - prev.Y) - (next.Y - prev.Y) * (pts[i].X - prev.X);
                var along = (pts[i].X - prev.X) * (next.X - prev.X) + (pts[i].Y - prev.Y) * (next.Y - prev.Y);
                if (Math.Abs(cross) / span > tol || along < 0 || along > span * span) continue;
                pts.RemoveAt(i);
                changed = true;
                break;
            }
        }
        return pts;
    }

    static bool SelfCrossing(List<Pt> pts, double tol)
    {
        var n = pts.Count;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            for (var j = i + 1; j < n; j++)
            {
                // Neighbouring edges share a corner; that is not a crossing.
                if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
                if (Cross(a, b, pts[j], pts[(j + 1) % n], tol)) return true;
            }
        }
        return false;
    }

    static bool Cross(Pt a, Pt b, Pt c, Pt d, double tol)
    {
        var d1 = Side(c, d, a);
        var d2 = Side(c, d, b);
        var d3 = Side(a, b, c);
        var d4 = Side(a, b, d);
        var eps = tol * Math.Max(Dist(a, b), Dist(c, d));
        return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps))
            && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
    }

    static double Side(Pt a, Pt b, Pt p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
