using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// What follows a wall edit, as plain numbers. The floor and the flat roof
/// that came from a wall record share its outer outline (a roof with an
/// overhang sits that far outside it). The receipt names what was rebuilt.
/// No RhinoCommon, so it tests headless.
/// </summary>
public static class WallFollowPlan
{
    /// <summary>
    /// The sentence after a successful edit. Nothing rebuilt is the unchanged
    /// sentence. A shown daylight map adds that it is out of date.
    /// </summary>
    public static string Sentence(bool floor, bool roof, int rooms, bool daylight)
    {
        var parts = new List<string>();
        if (floor) parts.Add("floor");
        if (roof) parts.Add("roof");
        if (rooms == 1) parts.Add("1 room");
        else if (rooms > 1) parts.Add(rooms.ToString(CultureInfo.InvariantCulture) + " rooms");

        string text;
        if (parts.Count == 0)
            text = "Floor, roof and rooms are unchanged.";
        else if (parts.Count == 1)
            text = Capital(parts[0]) + " updated.";
        else if (parts.Count == 2)
            text = Capital(parts[0]) + " and " + parts[1] + " updated.";
        else
            text = Capital(parts[0]) + ", " + parts[1] + " and " + parts[2] + " updated.";
        if (daylight)
            text += " Daylight is out of date, run it again.";
        return text;
    }

    /// <summary>The same closed outline, vertices on each other's edges.</summary>
    public static bool SameOutline(IList<RoomDetect.Pt> a, IList<RoomDetect.Pt> b, double tol)
    {
        var slack = tol > 0 ? tol : 1.0;
        return Near(a, b, slack) && Near(b, a, slack);
    }

    /// <summary>
    /// A flat roof of this wall: every wall vertex lies inside the roof, and
    /// the typical distance out to the roof edge is the overhang. An overhang
    /// of nothing is <see cref="SameOutline"/>.
    /// </summary>
    public static bool Covers(IList<RoomDetect.Pt> wall, IList<RoomDetect.Pt> roof, double overhang, double tol)
    {
        if (wall == null || roof == null || wall.Count < 3 || roof.Count < 3) return false;
        var slack = tol > 0 ? tol : 1.0;
        if (overhang <= slack) return SameOutline(wall, roof, slack);
        var dists = new List<double>(wall.Count);
        foreach (var p in wall)
        {
            var distance = DistanceToRing(p, roof);
            if (!RoomDetect.Contains(roof, p) && distance > slack) return false;
            dists.Add(distance);
        }
        dists.Sort();
        var mid = dists[dists.Count / 2];
        return Math.Abs(mid - overhang) <= Math.Max(50.0, overhang * 0.1);
    }

    static bool Near(IList<RoomDetect.Pt> points, IList<RoomDetect.Pt> ring, double tol)
    {
        if (points == null || ring == null || points.Count < 3 || ring.Count < 3) return false;
        foreach (var p in points)
            if (DistanceToRing(p, ring) > tol) return false;
        return true;
    }

    /// <summary>Distance from a point to the nearest edge of a closed ring.</summary>
    public static double DistanceToRing(RoomDetect.Pt p, IList<RoomDetect.Pt> ring)
    {
        var best = double.MaxValue;
        if (ring == null) return best;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            best = Math.Min(best, DistanceToSegment(p, ring[j], ring[i]));
        return best;
    }

    static double DistanceToSegment(RoomDetect.Pt p, RoomDetect.Pt a, RoomDetect.Pt b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 <= 1e-12) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        if (t < 0) t = 0;
        else if (t > 1) t = 1;
        var x = a.X + t * dx - p.X;
        var y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    static string Capital(string word)
    {
        if (string.IsNullOrEmpty(word) || !char.IsLetter(word[0])) return word;
        return char.ToUpperInvariant(word[0]) + word.Substring(1);
    }
}
