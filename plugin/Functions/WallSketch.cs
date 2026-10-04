using System;
using System.Collections.Generic;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The state of one Draw wall command: the clicked points and what the next
/// mouse point snaps to. Hover answers what the preview shows, Add takes a
/// click, AddTyped a typed length, Close the loop, Back a step back (Esc).
/// Pure, so the tests drive it with plain points.
/// </summary>
public sealed class WallSketch
{
    readonly WallDraw.Targets _targets;
    readonly List<Pt> _points = new List<Pt>();
    Pt _dir = new Pt(1, 0);

    public WallSketch(WallDraw.Targets targets, double thickness, double reach = WallDraw.ReachMm)
    {
        _targets = targets ?? new WallDraw.Targets();
        Thickness = thickness;
        Reach = reach;
    }

    public double Thickness { get; }
    public double Reach { get; set; }
    public bool Closed { get; private set; }
    public IReadOnlyList<Pt> Points => _points;

    /// <summary>What the preview draws for a mouse point.</summary>
    public sealed class Preview
    {
        public Pt Point;
        public WallDraw.SnapKind Kind;
        public bool HasFrom;
        public Pt From;
        public double Length;
        public Pt Dir;
        public string Label = "";
        public bool Valid = true;
        public string Why;
        /// <summary>The rubber-band wall as it will be laid down, corner included. Null before the first click.</summary>
        public List<Pt> Band;
    }

    public Preview Hover(Pt raw, bool shift)
    {
        var snapped = Resolve(raw, shift);
        var preview = new Preview { Point = snapped.Point, Kind = snapped.Kind };
        if (_points.Count == 0) return preview;
        var last = _points[_points.Count - 1];
        var length = WallDraw.Dist(last, snapped.Point);
        if (length > 1e-6) _dir = WallDraw.Unit(last, snapped.Point);
        preview.HasFrom = true;
        preview.From = last;
        preview.Length = length;
        preview.Dir = _dir;
        preview.Label = WallDraw.Dimension(length, _dir);
        preview.Valid = length > Thickness;
        if (!preview.Valid) preview.Why = WallDraw.TooShort;
        var extension = _points.Count >= 2 ? WallDraw.CornerExtension(_points[_points.Count - 2], last, snapped.Point, Thickness) : 0;
        var from = new Pt(last.X - _dir.X * extension, last.Y - _dir.Y * extension);
        preview.Band = WallDraw.Band(from, snapped.Point, Thickness);
        return preview;
    }

    /// <summary>A click. Clicking the first point again with three or more placed closes the loop.</summary>
    public bool Add(Pt raw, bool shift, out string why)
    {
        var snapped = Resolve(raw, shift);
        return Place(snapped.Point, snapped.Kind == WallDraw.SnapKind.Start && _points.Count >= 3, out why);
    }

    /// <summary>A typed length along the direction the mouse last showed.</summary>
    public bool AddTyped(double length, out string why)
    {
        why = null;
        if (_points.Count == 0)
        {
            why = "Click the first point before typing a length.";
            return false;
        }
        if (!(length > 0))
        {
            why = "A length is above 0.";
            return false;
        }
        var last = _points[_points.Count - 1];
        var point = new Pt(WallDraw.Round(last.X + _dir.X * length), WallDraw.Round(last.Y + _dir.Y * length));
        var first = _points[0];
        return Place(point, _points.Count >= 3 && WallDraw.Dist(point, first) < 1, out why);
    }

    public bool Close(out string why)
    {
        why = null;
        if (Closed) return true;
        if (_points.Count < 3)
        {
            why = "A closed loop needs three points or more.";
            return false;
        }
        if (WallDraw.Dist(_points[_points.Count - 1], _points[0]) <= Thickness)
        {
            why = WallDraw.TooShort;
            return false;
        }
        Closed = true;
        return true;
    }

    /// <summary>Esc: takes the last step back. False when nothing is left to draw, so the command ends.</summary>
    public bool Back()
    {
        if (Closed)
        {
            Closed = false;
            return true;
        }
        if (_points.Count > 0) _points.RemoveAt(_points.Count - 1);
        return _points.Count > 0;
    }

    public List<WallDraw.Segment> Segments() => WallDraw.Plan(_points, Closed, Thickness);

    bool Place(Pt point, bool closes, out string why)
    {
        why = null;
        if (Closed)
        {
            why = "The loop is closed.";
            return false;
        }
        if (_points.Count == 0)
        {
            _points.Add(point);
            return true;
        }
        if (WallDraw.Dist(_points[_points.Count - 1], point) <= Thickness)
        {
            why = WallDraw.TooShort;
            return false;
        }
        if (closes)
        {
            Closed = true;
            return true;
        }
        _points.Add(point);
        return true;
    }

    /// <summary>The nearest of the wall corners, the wall ends' and edges' midpoints and the loop's own points; else a face; else the 15 degree and 10 mm steps.</summary>
    WallDraw.Snapped Resolve(Pt raw, bool shift)
    {
        var ortho = !shift;
        var hasLast = _points.Count > 0;
        var last = hasLast ? _points[_points.Count - 1] : default(Pt);

        var rank = 0;
        var best = raw;
        var kind = WallDraw.SnapKind.None;
        var bestDist = Reach;

        void Try(Pt point, WallDraw.SnapKind k, int r)
        {
            if (hasLast && WallDraw.Dist(point, last) < 1) return;
            var d = WallDraw.Dist(point, raw);
            if (d > Reach || r < rank || r == rank && d >= bestDist && kind != WallDraw.SnapKind.None) return;
            rank = r;
            best = point;
            kind = k;
            bestDist = d;
        }

        for (var i = 0; i < _points.Count - 1; i++)
            Try(_points[i], i == 0 && _points.Count >= 3 ? WallDraw.SnapKind.Start : WallDraw.SnapKind.End, 3);
        foreach (var p in _targets.Ends) Try(p, WallDraw.SnapKind.End, 3);
        foreach (var p in _targets.Mids) Try(p, WallDraw.SnapKind.Mid, 3);

        if (kind == WallDraw.SnapKind.None)
        {
            var ray = hasLast && ortho ? WallDraw.AngleDir(last, raw) : default(Pt?);
            foreach (var (a, b) in _targets.Faces)
            {
                Pt? hit = ray.HasValue ? RayHit(last, ray.Value, a, b) : Nearest(a, b, raw);
                if (hit.HasValue) Try(hit.Value, WallDraw.SnapKind.Face, 1);
            }
        }

        if (kind != WallDraw.SnapKind.None)
            return new WallDraw.Snapped(new Pt(WallDraw.Round(best.X), WallDraw.Round(best.Y)), kind);
        if (hasLast && ortho)
            return new WallDraw.Snapped(WallDraw.SnapAngle(last, raw), WallDraw.SnapKind.Angle);
        if (!hasLast && ortho)
            return new WallDraw.Snapped(new Pt(Step(raw.X), Step(raw.Y)), WallDraw.SnapKind.None);
        return new WallDraw.Snapped(new Pt(Math.Round(raw.X), Math.Round(raw.Y)), WallDraw.SnapKind.None);
    }

    static double Step(double v) => Math.Round(v / WallDraw.LengthStepMm, MidpointRounding.AwayFromZero) * WallDraw.LengthStepMm;

    /// <summary>Where the ray from from along dir crosses the face edge a-b ahead of from.</summary>
    static Pt? RayHit(Pt from, Pt dir, Pt a, Pt b)
    {
        var ab = new Pt(b.X - a.X, b.Y - a.Y);
        var denom = dir.X * ab.Y - dir.Y * ab.X;
        if (Math.Abs(denom) < 1e-9) return null;
        var af = new Pt(a.X - from.X, a.Y - from.Y);
        var t = (af.X * ab.Y - af.Y * ab.X) / denom;
        var s = (af.X * dir.Y - af.Y * dir.X) / denom;
        if (t <= 0 || s < 0 || s > 1) return null;
        return new Pt(from.X + dir.X * t, from.Y + dir.Y * t);
    }

    static Pt? Nearest(Pt a, Pt b, Pt p)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var len2 = abx * abx + aby * aby;
        if (len2 < 1e-12) return null;
        var s = Math.Max(0, Math.Min(1, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
        return new Pt(a.X + abx * s, a.Y + aby * s);
    }
}
