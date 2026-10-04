using System;
using System.Collections.Generic;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// A detail drawing's crop in 2D: poché rings clipped as polygons (so no
/// slivers are left), lines clipped as segments, the edges that lie on the
/// crop found (they are not cut lines) and the zigzag a break line draws
/// there. Frame coordinates, model mm. Pure, no RhinoCommon.
/// </summary>
public static class DetailClip
{
    public readonly struct Rect
    {
        public Rect(double u0, double v0, double u1, double v1)
        {
            U0 = Math.Min(u0, u1);
            V0 = Math.Min(v0, v1);
            U1 = Math.Max(u0, u1);
            V1 = Math.Max(v0, v1);
        }

        public double U0 { get; }
        public double V0 { get; }
        public double U1 { get; }
        public double V1 { get; }
    }

    /// <summary>The ring inside the crop (Sutherland–Hodgman). Empty when nothing is left.</summary>
    public static List<Pt> Ring(IList<Pt> ring, Rect rect)
    {
        var list = new List<Pt>(ring ?? new List<Pt>());
        list = Side(list, p => p.X - rect.U0, (a, b) => Cross(a, b, a.X - rect.U0, b.X - rect.U0));
        list = Side(list, p => rect.U1 - p.X, (a, b) => Cross(a, b, rect.U1 - a.X, rect.U1 - b.X));
        list = Side(list, p => p.Y - rect.V0, (a, b) => Cross(a, b, a.Y - rect.V0, b.Y - rect.V0));
        list = Side(list, p => rect.V1 - p.Y, (a, b) => Cross(a, b, rect.V1 - a.Y, rect.V1 - b.Y));
        return list.Count >= 3 ? list : new List<Pt>();
    }

    /// <summary>The part of a segment inside the crop (Liang–Barsky), or false when none is.</summary>
    public static bool Segment(Pt a, Pt b, Rect rect, out Pt from, out Pt to)
    {
        from = a;
        to = b;
        double t0 = 0, t1 = 1;
        var du = b.X - a.X;
        var dv = b.Y - a.Y;
        if (!Edge(-du, a.X - rect.U0, ref t0, ref t1)) return false;
        if (!Edge(du, rect.U1 - a.X, ref t0, ref t1)) return false;
        if (!Edge(-dv, a.Y - rect.V0, ref t0, ref t1)) return false;
        if (!Edge(dv, rect.V1 - a.Y, ref t0, ref t1)) return false;
        from = new Pt(a.X + t0 * du, a.Y + t0 * dv);
        to = new Pt(a.X + t1 * du, a.Y + t1 * dv);
        return t1 - t0 > 1e-12;
    }

    /// <summary>The segment lies along one side of the crop: where the crop cuts the model, not a line of it.</summary>
    public static bool OnCrop(Pt a, Pt b, Rect rect, double tol)
    {
        bool Near(double x, double edge) => Math.Abs(x - edge) <= tol;
        return (Near(a.X, rect.U0) && Near(b.X, rect.U0))
            || (Near(a.X, rect.U1) && Near(b.X, rect.U1))
            || (Near(a.Y, rect.V0) && Near(b.Y, rect.V0))
            || (Near(a.Y, rect.V1) && Near(b.Y, rect.V1));
    }

    /// <summary>The edges of a clipped ring that lie on the crop: each gets a break line.</summary>
    public static List<KeyValuePair<Pt, Pt>> CropEdges(IList<Pt> ring, Rect rect, double tol)
    {
        var edges = new List<KeyValuePair<Pt, Pt>>();
        if (ring == null) return edges;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (Length(a, b) > tol && OnCrop(a, b, rect, tol)) edges.Add(new KeyValuePair<Pt, Pt>(a, b));
        }
        return edges;
    }

    /// <summary>
    /// A clipped ring's cut outline: its edges less those on the crop, as
    /// open runs, each starting where a crop edge ends. A ring the crop does
    /// not touch is one closed run.
    /// </summary>
    public static List<List<Pt>> CutRuns(IList<Pt> ring, Rect rect, double tol)
    {
        var runs = new List<List<Pt>>();
        if (ring == null || ring.Count < 3) return runs;
        var n = ring.Count;
        bool Crop(int i) => OnCrop(ring[i], ring[(i + 1) % n], rect, tol);
        var start = -1;
        for (var i = 0; i < n && start < 0; i++)
            if (Crop(i)) start = (i + 1) % n;
        if (start < 0)
        {
            var closed = new List<Pt>(ring) { ring[0] };
            runs.Add(closed);
            return runs;
        }
        List<Pt> run = null;
        for (var k = 0; k < n; k++)
        {
            var i = (start + k) % n;
            if (Crop(i))
            {
                run = null;
                continue;
            }
            if (Length(ring[i], ring[(i + 1) % n]) <= tol) continue;
            if (run == null)
            {
                run = new List<Pt> { ring[i] };
                runs.Add(run);
            }
            run.Add(ring[(i + 1) % n]);
        }
        return runs;
    }

    /// <summary>Paper mm a break line runs past the wall it breaks, at each end.</summary>
    public const double BreakOverMm = 2.0;
    /// <summary>The break's zigzag: its height and width, paper mm.</summary>
    public const double ZigMm = 2.0;

    /// <summary>
    /// A break line from a to b at 1:scale: straight, BreakOverMm past each
    /// end, with one zigzag in the middle across the line.
    /// </summary>
    public static List<Pt> BreakLine(Pt a, Pt b, double scale)
    {
        var length = Length(a, b);
        if (length <= 0) return new List<Pt> { a, b };
        var d = new Pt((b.X - a.X) / length, (b.Y - a.Y) / length);
        var n = new Pt(-d.Y, d.X);
        var over = BreakOverMm * scale;
        var half = ZigMm * scale / 2.0;
        var mid = length / 2.0;
        Pt At(double t, double h) => new Pt(a.X + d.X * t + n.X * h, a.Y + d.Y * t + n.Y * h);
        return new List<Pt>
        {
            At(-over, 0),
            At(mid - half, 0),
            At(mid - half / 2.0, half),
            At(mid + half / 2.0, -half),
            At(mid + half, 0),
            At(length + over, 0)
        };
    }

    static List<Pt> Side(List<Pt> ring, Func<Pt, double> inside, Func<Pt, Pt, Pt> cross)
    {
        var output = new List<Pt>();
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var ina = inside(a) >= 0;
            var inb = inside(b) >= 0;
            if (ina) output.Add(a);
            if (ina != inb) output.Add(cross(a, b));
        }
        return output;
    }

    static Pt Cross(Pt a, Pt b, double da, double db)
    {
        var t = da / (da - db);
        return new Pt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    static bool Edge(double p, double q, ref double t0, ref double t1)
    {
        if (Math.Abs(p) < 1e-12) return q >= 0;
        var r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    static double Length(Pt a, Pt b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
