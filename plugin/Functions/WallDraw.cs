using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Draw wall, pure: clicked points become wall segments for add_wall. The
/// snapping (wall ends, midpoints, faces, then 15 degree steps and 10 mm
/// lengths), the typed length, the close and the corner clean-up all live
/// here. The command only feeds it mouse points and draws what it returns.
/// No RhinoCommon, so it tests headless.
/// </summary>
public static class WallDraw
{
    public const double AngleStepDeg = 15;
    public const double LengthStepMm = 10;
    public const double ReachMm = 150;
    public const double DefaultThicknessMm = 200;
    public const string TooShort = "The wall is no longer than it is thick: give two points further apart.";
    public static readonly double[] Presets = { 100, 150, 200, 250, 300 };

    /// <summary>Two walls with a turn under this many degrees between them meet as one straight run.</summary>
    const double StraightDeg = 1;
    /// <summary>A sharper turn than this keeps this extension, so a hairpin does not run off.</summary>
    const double SharpTurnDeg = 179;

    public enum SnapKind { None, Angle, Start, End, Mid, Face }

    public readonly struct Snapped
    {
        public readonly Pt Point;
        public readonly SnapKind Kind;

        public Snapped(Pt point, SnapKind kind)
        {
            Point = point;
            Kind = kind;
        }
    }

    /// <summary>What a point can snap to: wall corners, edge midpoints and the faces themselves.</summary>
    public sealed class Targets
    {
        public readonly List<Pt> Ends = new List<Pt>();
        public readonly List<Pt> Mids = new List<Pt>();
        public readonly List<(Pt A, Pt B)> Faces = new List<(Pt A, Pt B)>();
    }

    /// <summary>The targets of every wall record (forsk:path loops). A wall's end is the midpoint of its short edge.</summary>
    public static Targets TargetsFrom(IEnumerable<List<List<Pt>>> records)
    {
        var targets = new Targets();
        var seen = new HashSet<string>();
        foreach (var rings in records ?? Enumerable.Empty<List<List<Pt>>>())
            foreach (var ring in rings ?? new List<List<Pt>>())
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (Dist(a, b) < 1) continue;
                    if (seen.Add(Key(a))) targets.Ends.Add(a);
                    targets.Mids.Add(new Pt(Round((a.X + b.X) / 2), Round((a.Y + b.Y) / 2)));
                    targets.Faces.Add((a, b));
                }
        return targets;
    }

    /// <summary>The raw point on the nearest 15 degree ray from from, its length on the nearest 10 mm.</summary>
    public static Pt SnapAngle(Pt from, Pt raw, double stepDeg = AngleStepDeg, double lengthStep = LengthStepMm)
    {
        var dx = raw.X - from.X;
        var dy = raw.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return raw;
        var angle = AngleDir(from, raw, stepDeg);
        var snapped = Math.Round(length / lengthStep, MidpointRounding.AwayFromZero) * lengthStep;
        return new Pt(Round(from.X + angle.X * snapped), Round(from.Y + angle.Y * snapped));
    }

    /// <summary>The unit direction from from toward raw, turned to the nearest step.</summary>
    public static Pt AngleDir(Pt from, Pt raw, double stepDeg = AngleStepDeg)
    {
        var angle = Math.Atan2(raw.Y - from.Y, raw.X - from.X);
        var step = stepDeg * Math.PI / 180.0;
        var snapped = Math.Round(angle / step, MidpointRounding.AwayFromZero) * step;
        return new Pt(Clean(Math.Cos(snapped)), Clean(Math.Sin(snapped)));
    }

    /// <summary>
    /// How far each wall runs past the corner so the square ends meet on the mitre.
    /// Half the thickness at 90 degrees; h·tan(turn/2) at any other turn.
    /// </summary>
    public static double CornerExtension(Pt before, Pt corner, Pt after, double thickness)
    {
        var d1 = Unit(before, corner);
        var d2 = Unit(corner, after);
        var cos = Math.Max(-1, Math.Min(1, d1.X * d2.X + d1.Y * d2.Y));
        var turn = Math.Acos(cos) * 180.0 / Math.PI;
        if (turn < StraightDeg) return 0;
        if (turn > SharpTurnDeg) turn = SharpTurnDeg;
        var half = turn * Math.PI / 360.0;
        return thickness / 2.0 * Math.Tan(half);
    }

    /// <summary>One wall: the clicked centreline, and the outline with mitred ends where the run turns.</summary>
    public sealed class Segment
    {
        public Pt From, To;
        public Pt Start;
        public double Length;
        /// <summary>Square at a free end, cut on the mitre where the next segment of this run meets it.</summary>
        public List<Pt> Ring;
    }

    /// <summary>The walls for a point list. Fewer than two points: none.</summary>
    public static List<Segment> Plan(IList<Pt> points, bool closed, double thickness)
    {
        var list = new List<Segment>();
        var n = points?.Count ?? 0;
        if (n < 2) return list;
        var count = closed && n >= 3 ? n : n - 1;
        for (var i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % n];
            var atStart = i > 0 || count == n;
            var atEnd = i < count - 1 || count == n;
            list.Add(new Segment
            {
                Start = a,
                From = a,
                To = b,
                Length = Dist(a, b),
                Ring = SegmentRing(
                    atStart ? points[(i - 1 + n) % n] : (Pt?)null,
                    a, b,
                    atEnd ? points[(i + 2) % n] : (Pt?)null,
                    thickness)
            });
        }
        return list;
    }

    /// <summary>The add_wall calls for the segments, one wall each.</summary>
    public static List<JObject> ToolCalls(IEnumerable<Segment> segments, double thickness)
    {
        return (segments ?? Enumerable.Empty<Segment>()).Select(s => new JObject
        {
            ["from"] = new JArray(s.From.X, s.From.Y),
            ["to"] = new JArray(s.To.X, s.To.Y),
            ["thickness"] = thickness
        }).ToList();
    }

    /// <summary>
    /// One segment's outline. A free end is square. An end that meets prev or next
    /// is the mitre, the same cut on both segments, so the corner has no sliver and no spike.
    /// </summary>
    public static List<Pt> SegmentRing(Pt? prev, Pt a, Pt b, Pt? next, double thickness)
    {
        var h = thickness / 2.0;
        var n = LeftNormal(a, b);
        return new List<Pt>
        {
            prev.HasValue ? Meet(prev.Value, a, b, -h) : Cap(a, n, -h),
            next.HasValue ? Meet(a, b, next.Value, -h) : Cap(b, n, -h),
            next.HasValue ? Meet(a, b, next.Value, h) : Cap(b, n, h),
            prev.HasValue ? Meet(prev.Value, a, b, h) : Cap(a, n, h)
        };
    }

    static Pt Cap(Pt at, Pt normal, double offset) =>
        new Pt(Round(at.X + normal.X * offset), Round(at.Y + normal.Y * offset));

    static Pt LeftNormal(Pt a, Pt b)
    {
        var u = Unit(a, b);
        return new Pt(-u.Y, u.X);
    }

    /// <summary>Where the two segments' lines, each offset by h to the left, meet.</summary>
    static Pt Meet(Pt a, Pt b, Pt c, double h)
    {
        var n1 = LeftNormal(a, b);
        var n2 = LeftNormal(b, c);
        var p = new Pt(a.X + n1.X * h, a.Y + n1.Y * h);
        var q = new Pt(b.X + n2.X * h, b.Y + n2.Y * h);
        var d1 = new Pt(b.X - a.X, b.Y - a.Y);
        var d2 = new Pt(c.X - b.X, c.Y - b.Y);
        var denom = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(denom) < 1e-9) return new Pt(Round(b.X + n1.X * h), Round(b.Y + n1.Y * h));
        var t = ((q.X - p.X) * d2.Y - (q.Y - p.Y) * d2.X) / denom;
        return new Pt(Round(p.X + d1.X * t), Round(p.Y + d1.Y * t));
    }

    /// <summary>The wall outline for a segment, as the band add_wall will lay down.</summary>
    public static List<Pt> Band(Pt from, Pt to, double thickness)
    {
        var u = Unit(from, to);
        var h = new Pt(-u.Y * thickness / 2.0, u.X * thickness / 2.0);
        return new List<Pt>
        {
            new Pt(from.X - h.X, from.Y - h.Y), new Pt(to.X - h.X, to.Y - h.Y),
            new Pt(to.X + h.X, to.Y + h.Y), new Pt(from.X + h.X, from.Y + h.Y)
        };
    }

    /// <summary>
    /// The thickness to start with: the last one drawn, else the one most
    /// walls have, else 200. Zero and negative values are ignored.
    /// </summary>
    public static double DefaultThickness(IEnumerable<double> existing, double? last)
    {
        if (last.HasValue && last.Value > 0 && last.Value <= WallEdit.MaxThickMm) return last.Value;
        var common = (existing ?? Enumerable.Empty<double>())
            .Where(t => t > 0 && t <= WallEdit.MaxThickMm)
            .GroupBy(t => Math.Round(t))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => (double?)g.Key)
            .FirstOrDefault();
        return common ?? DefaultThicknessMm;
    }

    /// <summary>The wall types to pick from: the presets, plus the current thickness when it is none of them.</summary>
    public static List<double> Types(double current)
    {
        var all = new List<double>(Presets);
        if (current > 0 && !all.Any(t => Math.Abs(t - current) < 0.5)) all.Add(Math.Round(current));
        all.Sort();
        return all;
    }

    /// <summary>"200 mm".</summary>
    public static string TypeName(double thickness) => Mm(thickness) + " mm";

    /// <summary>The live dimension: "3450 mm", with the angle when the wall is not square to the axes.</summary>
    public static string Dimension(double length, Pt dir)
    {
        var text = Mm(length) + " mm";
        var degrees = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
        if (degrees < 0) degrees += 360;
        var rounded = Math.Round(degrees, 1);
        if (Math.Abs(rounded % 90) > 0.05 && Math.Abs(rounded % 90 - 90) > 0.05)
            text += " · " + rounded.ToString("0.#", CultureInfo.InvariantCulture) + "°";
        return text;
    }

    /// <summary>Where Draw wall puts a wall: a record with an id when the 3D model stands, else a closed outline on the plan's wall layer.</summary>
    public interface IWallStore
    {
        /// <summary>True when the file has 3D walls. False: only the flat plan, and the walls get ids at Generate 3D.</summary>
        bool Solid { get; }

        /// <summary>Opens the one undo record for the whole run. False when a record is already open: the caller's holds it.</summary>
        bool OpenRecord();

        void CloseRecord();

        /// <summary>One segment as one wall of its own. Id is its forsk:id in the 3D model, null in the plan. Error is set when it was refused.</summary>
        Placed Place(Segment segment, double thickness);
    }

    public readonly struct Placed
    {
        public readonly string Id;
        public readonly string Error;

        public Placed(string id, string error)
        {
            Id = id;
            Error = error;
        }

        public bool Ok => string.IsNullOrEmpty(Error);
    }

    /// <summary>What a Draw wall run did: the walls placed, their ids, how far it got.</summary>
    public sealed class Outcome
    {
        public int Drawn;
        public int Planned;
        public double Length;
        /// <summary>The new walls' ids, in drawing order. Empty in the plan.</summary>
        public readonly List<string> Ids = new List<string>();
        /// <summary>The first refusal, which ended the run, or null.</summary>
        public string Stopped;
        /// <summary>The walls went to the plan only: no 3D model stood.</summary>
        public bool Plan;
    }

    /// <summary>
    /// Every segment becomes its own wall, inside one undo record however many
    /// there are. The first refusal ends the run; the walls already placed stay.
    /// </summary>
    public static Outcome Commit(IWallStore store, IList<Segment> segments, double thickness, bool ownUndo)
    {
        var outcome = new Outcome { Planned = segments?.Count ?? 0, Plan = !store.Solid };
        if (outcome.Planned == 0) return outcome;
        var opened = ownUndo && store.OpenRecord();
        try
        {
            foreach (var segment in segments)
            {
                var placed = store.Place(segment, thickness);
                if (!placed.Ok)
                {
                    outcome.Stopped = placed.Error;
                    break;
                }
                outcome.Drawn++;
                outcome.Length += segment.Length;
                if (!string.IsNullOrEmpty(placed.Id)) outcome.Ids.Add(placed.Id);
            }
        }
        finally
        {
            if (opened) store.CloseRecord();
        }
        return outcome;
    }

    /// <summary>The closed outline a wall has on the plan's wall layer: the band add_wall would lay down.</summary>
    public static List<Pt> PlanRing(Segment segment, double thickness) =>
        segment?.Ring != null && segment.Ring.Count >= 3 ? segment.Ring : Band(segment.From, segment.To, thickness);

    /// <summary>"Drew 4 walls, 200 mm thick, 14.2 m in all, closed."</summary>
    public static string Receipt(int drawn, int planned, double thickness, double totalMm, bool closed)
    {
        return Receipt(new Outcome { Drawn = drawn, Planned = planned, Length = totalMm }, thickness, closed);
    }

    /// <summary>
    /// "Drew 3 walls (w05, w06, w07), 200 mm thick, 9.8 m in all." Each new
    /// wall's id is named. In the plan there are none yet, and the line says
    /// the walls are in the plan and Generate 3D builds them. A run a wall
    /// stopped says why, as its second sentence.
    /// </summary>
    public static string Receipt(Outcome outcome, double thickness, bool closed)
    {
        var drawn = outcome.Drawn;
        if (drawn <= 0) return "No walls drawn.";
        var metres = (outcome.Length / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " m";
        var ids = outcome.Ids.Count == 0 ? "" : " (" + string.Join(", ", outcome.Ids) + ")";
        var place = outcome.Plan ? " in the plan" : "";
        var text = drawn == 1
            ? "Drew a " + TypeName(thickness) + " wall" + (outcome.Ids.Count == 1 ? " " + outcome.Ids[0] : "") + place + ", " + metres + " long"
            : "Drew " + drawn.ToString(CultureInfo.InvariantCulture) + " walls" + ids + place + ", " + TypeName(thickness) + " thick, " + metres + " in all";
        if (closed && drawn == outcome.Planned) text += ", closed";
        text += outcome.Plan ? "; Generate 3D builds them." : ".";
        if (!string.IsNullOrWhiteSpace(outcome.Stopped)) text += " Stopped: " + outcome.Stopped;
        return text;
    }

    internal static Pt Unit(Pt a, Pt b)
    {
        var d = Dist(a, b);
        return d < 1e-12 ? new Pt(1, 0) : new Pt((b.X - a.X) / d, (b.Y - a.Y) / d);
    }

    internal static double Dist(Pt a, Pt b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static double Round(double v) => Math.Round(v, 3);

    static double Clean(double v) => Math.Abs(v) < 1e-12 ? 0 : v;

    static string Key(Pt p) => Math.Round(p.X).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(p.Y).ToString(CultureInfo.InvariantCulture);

    internal static string Mm(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
}
