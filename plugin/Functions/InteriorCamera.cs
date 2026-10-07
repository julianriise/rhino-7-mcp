using System;
using System.Collections.Generic;
using System.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Jump inside: a level, straight-on interior shot of a room, like interior
/// photography (Julian, 2026-10-07). The camera looks the chosen way (north,
/// east, south or west; north by default), snapped to the room's own axis
/// nearest that way, so it stands square to the walls. It stands just inside
/// the wall behind it, on the line through the room's middle, and looks at
/// the far wall. Reused by Forsk Render's camera per room. No RhinoCommon.
/// </summary>
public static class InteriorCamera
{
    public const double EyeMm = 1200;
    public const double LensMm = 24;
    /// <summary>How far in from the wall behind it the camera stands, so the frame clears the wall.</summary>
    public const double StepInMm = 300;
    public static readonly IReadOnlyList<string> Directions = new[] { "north", "east", "south", "west" };

    public sealed class Shot
    {
        public Pt Eye;
        public Pt Target;
        /// <summary>"looking north".</summary>
        public string From;
    }

    /// <summary>The shot looking direction (north when null or unknown). Null for a room with no outline.</summary>
    public static Shot For(IList<Pt> room, string direction)
    {
        var ring = Furniture.Ccw(room ?? new List<Pt>());
        if (ring.Count < 3) return null;
        var way = Directions.Contains((direction ?? "").Trim().ToLowerInvariant()) ? direction.Trim().ToLowerInvariant() : "north";
        double wx = way == "east" ? 1 : way == "west" ? -1 : 0;
        double wy = way == "north" ? 1 : way == "south" ? -1 : 0;
        // The room's own axes: its longest edge and the normal to it. The one nearest the way wins.
        Pt a = ring[0], b = ring[1];
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            if (Dist(p, q) > Dist(a, b)) { a = p; b = q; }
        }
        var len = Dist(a, b);
        double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
        double nx = ux, ny = uy, best = double.MinValue;
        foreach (var (cx, cy) in new[] { (ux, uy), (-ux, -uy), (-uy, ux), (uy, -ux) })
        {
            var dot = cx * wx + cy * wy;
            if (dot > best) { best = dot; nx = cx; ny = cy; }
        }
        var middle = RoomDetect.TryInside(new List<List<Pt>> { ring }, out var inside) ? inside : Furniture.Middle(ring);
        var back = FarWall(ring, middle, -nx, -ny);
        var front = FarWall(ring, middle, nx, ny);
        var depth = back.Reach + front.Reach;
        var step = Math.Min(StepInMm, depth / 3);
        var eye = new Pt(back.Hit.X + nx * step, back.Hit.Y + ny * step);
        return new Shot { Eye = eye, Target = front.Hit, From = "looking " + way };
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

    static double Dist(Pt a, Pt b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

}
