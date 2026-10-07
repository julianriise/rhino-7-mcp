using System;
using System.Collections.Generic;
using System.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Jump inside: a first interior shot of a room, to adjust before a render.
/// The camera stands just inside the room's door at eye height and looks
/// across the room to the far wall; with no door it stands in one corner
/// and looks along the room's longest diagonal. Reused by Forsk Render's
/// camera per room. No RhinoCommon.
/// </summary>
public static class InteriorCamera
{
    public const double EyeMm = 1200;
    public const double LensMm = 24;
    /// <summary>How far inside the door the camera stands, so the frame clears the jambs.</summary>
    public const double StepInMm = 300;
    public const string ViewPrefix = "Interior: ";

    public sealed class Shot
    {
        public Pt Eye;
        public Pt Target;
        /// <summary>"from the door" or "from the corner".</summary>
        public string From;
    }

    /// <summary>
    /// The shot for a room. Of the doors on its outline, the one with the
    /// longest view across the room wins; no door gives the longest diagonal.
    /// Null for a room with no outline.
    /// </summary>
    public static Shot For(IList<Pt> room, IList<Furnish.Opening> openings)
    {
        var ring = Furniture.Ccw(room ?? new List<Pt>());
        if (ring.Count < 3) return null;
        Shot best = null;
        var bestReach = 0.0;
        foreach (var door in (openings ?? new List<Furnish.Opening>()).Where(o => o.Door))
        {
            if (!OnEdge(ring, door.Centre, out var ux, out var uy)) continue;
            // Into the room: the edge's left normal on a counter-clockwise ring.
            double nx = -uy, ny = ux;
            var eye = new Pt(door.Centre.X + nx * StepInMm, door.Centre.Y + ny * StepInMm);
            if (!RoomDetect.Contains(ring, eye)) continue;
            var far = FarWall(ring, eye, nx, ny);
            if (far.Reach > bestReach)
            {
                bestReach = far.Reach;
                best = new Shot { Eye = eye, Target = far.Hit, From = "from the door" };
            }
        }
        if (best != null) return best;
        // No door: corner to the opposite corner, each stepped in toward the middle.
        Pt a = ring[0], b = ring[1];
        var longest = -1.0;
        for (var i = 0; i < ring.Count; i++)
            for (var j = i + 1; j < ring.Count; j++)
            {
                var d = Dist(ring[i], ring[j]);
                if (d > longest) { longest = d; a = ring[i]; b = ring[j]; }
            }
        var middle = Furniture.Middle(ring);
        return new Shot { Eye = Toward(a, middle, StepInMm), Target = b, From = "from the corner" };
    }

    /// <summary>The edge the door sits on (within 400 mm), its direction unit.</summary>
    static bool OnEdge(List<Pt> ring, Pt p, out double ux, out double uy)
    {
        ux = uy = 0;
        var best = double.MaxValue;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var len = Dist(a, b);
            if (len < 1) continue;
            var d = SegDist(p, a, b);
            if (d < best) { best = d; ux = (b.X - a.X) / len; uy = (b.Y - a.Y) / len; }
        }
        return best <= 400;
    }

    /// <summary>The nearest wall the ray from eye along (nx, ny) meets, and how far it is.</summary>
    static (Pt Hit, double Reach) FarWall(List<Pt> ring, Pt eye, double nx, double ny)
    {
        var reach = double.MaxValue;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            var den = nx * ey - ny * ex;
            if (Math.Abs(den) < 1e-9) continue;
            var t = ((a.X - eye.X) * ey - (a.Y - eye.Y) * ex) / den;
            var s = ((a.X - eye.X) * ny - (a.Y - eye.Y) * nx) / den;
            if (t > 1 && s >= -1e-9 && s <= 1 + 1e-9) reach = Math.Min(reach, t);
        }
        if (reach == double.MaxValue) return (eye, 0);
        return (new Pt(eye.X + nx * reach, eye.Y + ny * reach), reach);
    }

    static Pt Toward(Pt from, Pt to, double by)
    {
        var d = Dist(from, to);
        return d < 1e-9 ? from : new Pt(from.X + (to.X - from.X) / d * by, from.Y + (to.Y - from.Y) / d * by);
    }

    static double Dist(Pt a, Pt b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    static double SegDist(Pt p, Pt a, Pt b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
        return Dist(p, new Pt(a.X + t * dx, a.Y + t * dy));
    }
}
