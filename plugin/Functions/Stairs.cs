using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 R5: a straight stair, pure. The flight climbs a total rise in n equal
/// risers, n = ceil(rise / riser max), so the last riser lands exactly on the
/// upper floor. Treads = n − 1 (the top step is the upper floor) and the run
/// is (n − 1) × going. The 3D body is one closed sawtooth from the floor up,
/// the plan symbol is the Nordic one: outline and step lines, a walking line
/// from a start dot to an arrow, a diagonal break where the climb passes the
/// plan cut, the steps above it dashed, and "UP" with "n × riser/going".
/// The record keys live here too. No RhinoCommon.
/// </summary>
public static class Stairs
{
    public const string Kind = "stair";
    public const string LayerName = "A-STAIR";
    public const string Straight = "straight";

    public const double RiserMaxDefault = 180;
    public const double GoingDefault = 260;
    public const double WidthDefault = 900;
    /// <summary>Residential guidance only. TEK17 §12-16 must be checked before release.</summary>
    public const double RuleLo = 600;
    public const double RuleHi = 640;
    public const double WidthMin = 800;
    /// <summary>Sizes outside these are refused, not warned about.</summary>
    public const double SizeLo = 100;
    public const double SizeHi = 5000;

    public const string ShapeKey = "forsk:stair_shape";
    public const string StartKey = "forsk:stair_start";
    public const string DirectionKey = "forsk:stair_direction";
    public const string WidthKey = "forsk:width";
    /// <summary>A number, or "auto": floor to floor from the walls, re-planned when they change.</summary>
    public const string RiseKey = "forsk:stair_rise";
    public const string RisersKey = "forsk:stair_risers";
    public const string RiserKey = "forsk:stair_riser";
    public const string GoingKey = "forsk:stair_going";
    public const string RiserMaxKey = "forsk:stair_riser_max";
    /// <summary>left or right: the side that stands against a wall, looking up the flight. Empty when free.</summary>
    public const string AgainstKey = "forsk:stair_against";
    public const string Auto = "auto";

    /// <summary>What the user set. Rise null is auto.</summary>
    public sealed class Spec
    {
        public string Shape = Straight;
        /// <summary>The foot of the first riser, on the flight's centre line. Z is the floor it stands on.</summary>
        public double X, Y, Z;
        /// <summary>The climb, a unit vector in plan.</summary>
        public double Dx = 1, Dy;
        public double Width = WidthDefault;
        public double? Rise;
        public double RiserMax = RiserMaxDefault;
        public double Going = GoingDefault;
        /// <summary>left, right, or null: the side against a wall, so a wider stair grows away from it.</summary>
        public string Against;
    }

    /// <summary>The planned flight.</summary>
    public sealed class Flight
    {
        public double Rise;
        public int Risers;
        public double Riser;
        public double Going;
        public double Width;
        public double RiserMax;
        public int Treads => Risers - 1;
        public double Run => Treads * Going;
        /// <summary>2R + G, mm.</summary>
        public double Rule => 2 * Riser + Going;
        public bool Steep => Riser > RiserMaxDefault + 1e-9 || Rule > RuleHi + 1e-9;
        public bool Shallow => Rule < RuleLo - 1e-9;
        public bool Narrow => Width < WidthMin - 1e-9;
    }

    /// <summary>n = ceil(rise / riser max), every riser rise / n. Throws when the sizes cannot make a stair.</summary>
    public static Flight Plan(double rise, double riserMax, double going, double width)
    {
        if (!(riserMax >= SizeLo && riserMax <= 400)) throw new ArgumentException("Step height must be between 100 and 400 mm.");
        if (!(going >= 150 && going <= 600)) throw new ArgumentException("Going must be between 150 and 600 mm.");
        if (!(width >= 500 && width <= SizeHi)) throw new ArgumentException("Stair width must be between 500 and 5000 mm.");
        if (!(rise > riserMax && rise <= 10000)) throw new ArgumentException("The rise is too small for a stair: it needs two steps or more.");
        // A rise that is a whole number of riser max (2880 / 180) is that many, not one more from float noise.
        var n = (int)Math.Ceiling(rise / riserMax - 1e-9);
        return new Flight { Rise = rise, Risers = n, Riser = rise / n, Going = going, Width = width, RiserMax = riserMax };
    }

    public static Flight Plan(Spec spec, double autoRise)
    {
        return Plan(spec.Rise ?? autoRise, spec.RiserMax, spec.Going, spec.Width);
    }

    /// <summary>The top of riser i (1..n) above the foot. The last is the rise itself, with no drift.</summary>
    public static double StepTop(Flight f, int i)
    {
        if (i >= f.Risers) return f.Rise;
        return f.Rise * i / f.Risers;
    }

    /// <summary>
    /// Floor to floor: the walls' most common height (to the mm) plus the floor
    /// build-up. No walls: the default wall height.
    /// </summary>
    public static double AutoRise(IEnumerable<double> wallHeights, double floorThickness, double fallbackHeight)
    {
        var common = (wallHeights ?? Enumerable.Empty<double>())
            .Where(h => h > 0)
            .GroupBy(h => Math.Round(h))
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .Select(g => (double?)g.Key)
            .FirstOrDefault();
        return (common ?? fallbackHeight) + Math.Max(0, floorThickness);
    }

    /// <summary>
    /// The side profile as a closed loop in (u along the climb, z up): from the
    /// foot up each riser and along each tread, then straight down the back to
    /// the floor. The top riser is the upper floor's edge, so the body stops at
    /// the last tread.
    /// </summary>
    public static List<Pt> Profile(Flight f)
    {
        var loop = new List<Pt> { new Pt(0, 0) };
        for (var t = 1; t <= f.Treads; t++)
        {
            var u0 = (t - 1) * f.Going;
            var z = StepTop(f, t);
            loop.Add(new Pt(u0, z));
            loop.Add(new Pt(t * f.Going, z));
        }
        loop.Add(new Pt(f.Run, 0));
        return loop;
    }

    /// <summary>
    /// The tread the plan cut passes through: the first k (1..treads) whose top
    /// is above the cut. 0 when the whole flight is below it.
    /// </summary>
    public static int CutTread(Flight f, double cutAbove)
    {
        for (var k = 1; k <= f.Treads; k++)
            if (StepTop(f, k) > cutAbove + 1e-9) return k;
        return 0;
    }

    /// <summary>One piece of the plan symbol, in the flight frame: u along the climb from the first riser, v across (left positive).</summary>
    public sealed class Mark
    {
        /// <summary>line, dot or text.</summary>
        public string Shape;
        /// <summary>outline, step, walk, arrow, cut, label.</summary>
        public string Part;
        public double U0, V0, U1, V1;
        public double Radius;
        public string Text;
        public bool Dashed;
    }

    /// <summary>
    /// The plan symbol. cutAbove is the plan cut over the stair's floor;
    /// scale sizes the dot and the arrow (paper mm × scale); textHeight is the
    /// label's model height.
    /// </summary>
    public static List<Mark> PlanSymbol(Flight f, double cutAbove, int scale, double textHeight)
    {
        var marks = new List<Mark>();
        var h = f.Width / 2.0;
        var k = CutTread(f, cutAbove);
        // The break crosses tread k diagonally: from its foot on the right side to its head on the left.
        var breakR = k > 0 ? (k - 1) * f.Going : f.Run;
        var breakL = k > 0 ? k * f.Going : f.Run;
        var breakC = (breakR + breakL) / 2.0;

        void Line(string part, double u0, double v0, double u1, double v1, bool dashed)
        {
            if (Math.Abs(u1 - u0) < 1e-6 && Math.Abs(v1 - v0) < 1e-6) return;
            marks.Add(new Mark { Shape = "line", Part = part, U0 = u0, V0 = v0, U1 = u1, V1 = v1, Dashed = dashed });
        }
        void Split(string part, double v, double at)
        {
            Line(part, 0, v, Math.Min(at, f.Run), v, false);
            if (at < f.Run) Line(part, at, v, f.Run, v, true);
        }

        // Step lines: the first riser and the top edge close the outline.
        for (var i = 0; i <= f.Treads; i++)
        {
            var u = i * f.Going;
            Line(i == 0 || i == f.Treads ? "outline" : "step", u, -h, u, h, k > 0 && i >= k);
        }
        Split("outline", -h, breakR);
        Split("outline", h, breakL);
        if (k > 0) Line("cut", breakR, -h, breakL, h, false);

        var scaleMm = Math.Max(1, scale);
        var dot = Math.Min(0.75 * scaleMm, Math.Min(f.Going, h) / 3.0);
        marks.Add(new Mark { Shape = "dot", Part = "walk", U0 = dot, V0 = 0, Radius = dot });
        Split("walk", 0, k > 0 ? breakC : f.Run);
        // Split drew the walk from 0; start it at the dot's edge instead.
        var walk = marks.FindIndex(m => m.Part == "walk" && m.Shape == "line");
        if (walk >= 0) marks[walk].U0 = 2 * dot;
        var arrow = Math.Min(2.5 * scaleMm, Math.Min(f.Going, h));
        Line("arrow", f.Run, 0, f.Run - arrow, arrow * 0.4, false);
        Line("arrow", f.Run, 0, f.Run - arrow, -arrow * 0.4, false);

        // The label sits in the left half, below the break, along the climb.
        var labelU = (k > 0 ? breakR : f.Run) / 2.0;
        var lift = 0.7 * Math.Max(0, textHeight);
        marks.Add(new Mark { Shape = "text", Part = "label", U0 = labelU, V0 = h / 2 + lift, Text = "UP" });
        marks.Add(new Mark { Shape = "text", Part = "label", U0 = labelU, V0 = h / 2 - lift, Text = Sizes(f) });
        return marks;
    }

    /// <summary>"16 × 180/260": risers × riser/going, rounded to the mm for display only.</summary>
    public static string Sizes(Flight f)
    {
        return f.Risers.ToString(CultureInfo.InvariantCulture) + " × " + Mm(f.Riser) + "/" + Mm(f.Going);
    }

    /// <summary>Null when the flight is within the guidance, else one short clause: "steep: 2R+G = 668", "narrow: 750 wide".</summary>
    public static string Comfort(Flight f)
    {
        var parts = new List<string>();
        if (f.Steep)
            parts.Add(f.Rule > RuleHi + 1e-9 ? "steep: 2R+G = " + Mm(f.Rule) : "steep: steps of " + Mm(f.Riser));
        else if (f.Shallow)
            parts.Add("shallow: 2R+G = " + Mm(f.Rule));
        if (f.Narrow) parts.Add("narrow: " + Mm(f.Width) + " wide");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>"Added a straight stair, 16 steps of 180." with a comfort clause when it breaks the guidance.</summary>
    public static string Receipt(string verb, Flight f, string where = null)
    {
        var text = verb + " a straight stair" + (string.IsNullOrWhiteSpace(where) ? "" : " " + where.Trim())
            + ", " + f.Risers.ToString(CultureInfo.InvariantCulture) + " steps of " + Mm(f.Riser);
        var comfort = Comfort(f);
        return text + (comfort == null ? "" : " (" + comfort + ")") + ".";
    }

    /// <summary>"Changed the stair: 17 steps of 169, 1000 wide." or "Flipped the stair, 16 steps of 180."</summary>
    public static string EditReceipt(Flight f, bool flipped, bool widthChanged, bool sizesChanged)
    {
        var steps = f.Risers.ToString(CultureInfo.InvariantCulture) + " steps of " + Mm(f.Riser);
        var comfort = Comfort(f);
        var tail = comfort == null ? "." : " (" + comfort + ").";
        if (flipped && !widthChanged && !sizesChanged) return "Flipped the stair, " + steps + tail;
        return "Changed the stair: " + steps + (widthChanged ? ", " + Mm(f.Width) + " wide" : "")
            + (flipped ? ", flipped" : "") + tail;
    }

    /// <summary>The pick line: "Stair · 16 risers".</summary>
    public static string PickLine(string risers, bool nb)
    {
        var head = nb ? "Trapp" : "Stair";
        if (!int.TryParse(risers, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return head;
        return head + " · " + n.ToString(CultureInfo.InvariantCulture) + (nb ? " opptrinn" : " risers");
    }

    /// <summary>Flip: the same footprint, climbed the other way.</summary>
    public static Spec Flipped(Spec spec, Flight f)
    {
        return new Spec
        {
            Shape = spec.Shape,
            X = spec.X + spec.Dx * f.Run,
            Y = spec.Y + spec.Dy * f.Run,
            Z = spec.Z,
            Dx = -spec.Dx,
            Dy = -spec.Dy,
            Width = spec.Width,
            Rise = spec.Rise,
            RiserMax = spec.RiserMax,
            Going = spec.Going,
            Against = spec.Against == "left" ? "right" : spec.Against == "right" ? "left" : spec.Against
        };
    }

    /// <summary>A new width. The side against a wall stays where it is; a free stair keeps its centre line.</summary>
    public static Spec Resized(Spec spec, double width)
    {
        var shift = (width - spec.Width) / 2.0;
        var side = spec.Against == "right" ? 1.0 : spec.Against == "left" ? -1.0 : 0.0;
        return new Spec
        {
            Shape = spec.Shape,
            X = spec.X - spec.Dy * shift * side,
            Y = spec.Y + spec.Dx * shift * side,
            Z = spec.Z,
            Dx = spec.Dx,
            Dy = spec.Dy,
            Width = width,
            Rise = spec.Rise,
            RiserMax = spec.RiserMax,
            Going = spec.Going,
            Against = spec.Against
        };
    }

    /// <summary>The plan footprint corners, counter-clockwise from the right foot.</summary>
    public static List<Pt> Footprint(Spec spec, Flight f)
    {
        var h = f.Width / 2.0;
        var lx = -spec.Dy;
        var ly = spec.Dx;
        Pt At(double u, double v) => new Pt(spec.X + spec.Dx * u + lx * v, spec.Y + spec.Dy * u + ly * v);
        return new List<Pt> { At(0, -h), At(f.Run, -h), At(f.Run, h), At(0, h) };
    }

    // ---- the record ----

    /// <summary>The user strings a stair object carries, besides forsk:kind, forsk:id and the stamp.</summary>
    public static Dictionary<string, string> Write(Spec spec, Flight f)
    {
        return new Dictionary<string, string>
        {
            [ShapeKey] = spec.Shape ?? Straight,
            [StartKey] = Num(spec.X) + "," + Num(spec.Y) + "," + Num(spec.Z),
            [DirectionKey] = Num(spec.Dx, "0.######") + "," + Num(spec.Dy, "0.######"),
            [WidthKey] = Num(spec.Width),
            [RiseKey] = spec.Rise.HasValue ? Num(spec.Rise.Value) : Auto,
            [RisersKey] = f.Risers.ToString(CultureInfo.InvariantCulture),
            [RiserKey] = Num(f.Riser, "0.######"),
            [GoingKey] = Num(f.Going),
            [RiserMaxKey] = Num(spec.RiserMax),
            [AgainstKey] = spec.Against ?? ""
        };
    }

    /// <summary>The spec back from its user strings. Null when the start or the direction does not read.</summary>
    public static Spec Read(Func<string, string> get)
    {
        if (get == null) return null;
        var start = Numbers(get(StartKey));
        var dir = Numbers(get(DirectionKey));
        if (start == null || start.Length < 2 || dir == null || dir.Length != 2) return null;
        var length = Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1]);
        if (length < 1e-9) return null;
        var rise = (get(RiseKey) ?? "").Trim();
        return new Spec
        {
            Shape = string.IsNullOrWhiteSpace(get(ShapeKey)) ? Straight : get(ShapeKey).Trim(),
            X = start[0],
            Y = start[1],
            Z = start.Length > 2 ? start[2] : 0,
            Dx = dir[0] / length,
            Dy = dir[1] / length,
            Width = Number(get(WidthKey)) ?? WidthDefault,
            Rise = rise.Length == 0 || rise.Equals(Auto, StringComparison.OrdinalIgnoreCase) ? (double?)null : Number(rise),
            RiserMax = Number(get(RiserMaxKey)) ?? RiserMaxDefault,
            Going = Number(get(GoingKey)) ?? GoingDefault,
            Against = get(AgainstKey) == "left" || get(AgainstKey) == "right" ? get(AgainstKey) : null
        };
    }

    /// <summary>The next free id: S01, S02, …</summary>
    public static string NextId(IEnumerable<string> taken)
    {
        var used = new HashSet<int>();
        foreach (var id in taken ?? Enumerable.Empty<string>())
            if (id != null && id.Length > 1 && (id[0] == 'S' || id[0] == 's')
                && int.TryParse(id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                used.Add(n);
        var next = 1;
        while (used.Contains(next)) next++;
        return "S" + next.ToString("D2", CultureInfo.InvariantCulture);
    }

    // ---- placement ----

    /// <summary>One face of a wall run that looks into a room, as one straight edge.</summary>
    public sealed class Face
    {
        public Pt A, B;
        /// <summary>Unit, from the face into the room.</summary>
        public Pt Inward;
        public double Length;
        /// <summary>The run's name in the join graph: "the north wall", "the wall at (x, y)".</summary>
        public string Name;
        /// <summary>The run's index in its graph.</summary>
        public int Run;
    }

    /// <summary>
    /// The faces that look into a room: each edge of a hole in the cluster's
    /// shape that lies in a run. An edge on the outer loop faces outside.
    /// </summary>
    public static List<Face> InteriorFaces(WallJoins.Graph graph)
    {
        var faces = new List<Face>();
        if (graph?.Shape == null) return faces;
        for (var r = 0; r < graph.Runs.Count; r++)
        {
            var run = graph.Runs[r];
            foreach (var (loop, edge) in run.Edges)
            {
                if (loop == 0) continue;
                var ring = graph.Shape[loop];
                var a = ring[edge];
                var b = ring[(edge + 1) % ring.Count];
                var across = 0.5 * (Dot(a, run.Normal) + Dot(b, run.Normal));
                var near = Math.Abs(across - run.Near) <= Math.Abs(across - run.Far);
                var sign = near ? -1.0 : 1.0;
                var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (length < 1) continue;
                faces.Add(new Face
                {
                    A = a,
                    B = b,
                    Inward = new Pt(run.Normal.X * sign, run.Normal.Y * sign),
                    Length = length,
                    Name = r < graph.Names.Count ? graph.Names[r] : null,
                    Run = r
                });
            }
        }
        return faces;
    }

    /// <summary>
    /// The stair along a face: its side against the face, its foot at the end
    /// nearer to near (A when near is null), climbing toward the other end.
    /// </summary>
    public static void AlongFace(Face face, Pt? near, double width, out Pt start, out Pt dir, out string against)
    {
        var fromB = near.HasValue && Dist(near.Value, face.B) < Dist(near.Value, face.A);
        var foot = fromB ? face.B : face.A;
        var head = fromB ? face.A : face.B;
        var length = Math.Max(Dist(foot, head), 1e-9);
        dir = new Pt((head.X - foot.X) / length, (head.Y - foot.Y) / length);
        start = new Pt(foot.X + face.Inward.X * width / 2.0, foot.Y + face.Inward.Y * width / 2.0);
        // The room is on the left of the climb when inward points left: the wall is then on the right.
        against = face.Inward.X * -dir.Y + face.Inward.Y * dir.X > 0 ? "right" : "left";
    }

    /// <summary>The longest face that holds the run, else the longest face at all. Null when there is none.</summary>
    public static Face LongestFace(IEnumerable<Face> faces, double run)
    {
        var list = (faces ?? Enumerable.Empty<Face>()).Where(x => x != null).OrderByDescending(x => x.Length).ToList();
        return list.FirstOrDefault(x => x.Length >= run - 1) ?? list.FirstOrDefault();
    }

    /// <summary>"along the north wall", or a plain "along the longest free wall" when the name holds coordinates.</summary>
    public static string Where(Face face)
    {
        var name = face?.Name ?? "";
        if (name.StartsWith("the ", StringComparison.Ordinal) && name.EndsWith(" wall", StringComparison.Ordinal) && name.IndexOf('(') < 0)
            return "along " + name;
        return "along the longest free wall";
    }

    // ---- helpers ----

    static double Dot(Pt p, Pt v) => p.X * v.X + p.Y * v.Y;

    static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static string Mm(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    static string Num(double value, string format = "0.###") => value.ToString(format, CultureInfo.InvariantCulture);

    static double? Number(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
    }

    static double[] Numbers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(',');
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var v = Number(parts[i]);
            if (!v.HasValue) return null;
            values[i] = v.Value;
        }
        return values;
    }
}
