using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Box = RhinoMCPPlugin.Functions.RoomDetect.Box;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// F5.2 plan dimensions, made at print from the model like the symbols. A
/// chain outside each facade that has openings: corner, opening centres (the
/// record's own position; the width is in the schedule), corner. Then per side
/// of the building, what that side sees of the outline's jogs, and the
/// overall. In each rectangular room, its width and depth. Sizes are paper mm
/// times the plan scale. Each chain takes the first offset, stepping away
/// from its face, where its lines and values clear the tags, marks, symbols,
/// wall poché and the chains placed before it (the longest facade first; a
/// facade chain not yet placed keeps its witness paths free). One that finds
/// no clear spot near its face moves out to its side's rows. Values are whole
/// mm and each chain adds up to its total. Pure geometry in drawing mm, no
/// RhinoCommon, so the layout tests headless; PlanSymbols bakes what it returns.
/// </summary>
public static class PlanDims
{
    /// <summary>Paper height of a value: ISO 3098's step between the marks' 1.25 mm and the tags' 2.5 mm.</summary>
    public const double TextMm = 1.8;
    /// <summary>Paper length of the 45° tick at each point of a chain.</summary>
    public const double TickMm = 2.0;
    /// <summary>Paper gap between a dimension line and its value.</summary>
    public const double TextGapMm = 0.6;
    /// <summary>Paper distance between two rows of values: a padded glyph box and 0.2 mm.</summary>
    public const double RowMm = TextMm + 2 * TextPadMm + 0.2;
    /// <summary>
    /// Witness lines have a fixed length ("fixed to dimension line" in Revit):
    /// this far toward the object from the dimension line, and this far past it.
    /// </summary>
    public const double WitnessInMm = 2.5;
    public const double WitnessOutMm = 1.5;
    /// <summary>
    /// First paper offset tried outside a face, then StepMm at a time: a facade
    /// chain stays within FacadeReachMm of its face (farther, it would no longer
    /// read as that face's), a side's rows go out to ReachMm.
    /// </summary>
    public const double FirstMm = 6.0;
    public const double StepMm = 1.0;
    public const double FacadeReachMm = 40.0;
    public const double ReachMm = 150.0;
    /// <summary>Paper clearance a dimension keeps to everything else.</summary>
    public const double ClearMm = 0.8;
    /// <summary>Paper margin around a value's glyphs.</summary>
    public const double TextPadMm = 0.4;
    /// <summary>A room's dimension line stays at least this far from its walls.</summary>
    public const double InsetMm = 3.0;
    /// <summary>How far past its room a room tag on a leader may go.</summary>
    public const double LeaderReachMm = 40.0;

    /// <summary>
    /// What a drawn thing is to a dimension: text that nothing may touch; a
    /// line no dimension may touch (symbols, leaders); a dimension line, which
    /// other dimension lines may cross (at an inner corner two facade chains
    /// must, and a room's width crosses its depth) but not run along; or a
    /// line only text keeps clear of (roof outline, lines beyond the cut).
    /// </summary>
    public enum Kind
    {
        Text,
        Line,
        Dim,
        Crossable
    }

    public readonly struct Obstacle
    {
        public readonly Box Box;
        public readonly Kind Kind;

        public Obstacle(Box box, Kind kind)
        {
            Box = box;
            Kind = kind;
        }
    }

    /// <summary>An opening in drawing mm: the wall centre at it, unit along the wall, the wall's half thickness.</summary>
    public sealed class Opening
    {
        public string Id;
        public Pt Centre;
        public Pt Along;
        public double HalfThick;
    }

    public sealed class Room
    {
        public string Id;
        public List<Pt> Ring;
    }

    /// <summary>
    /// The plan in drawing mm: the outer wall faces of the building, the
    /// openings, the tagged rooms, what is already drawn, and the poché.
    /// </summary>
    public sealed class Scene
    {
        public int Scale;
        public List<List<Pt>> Outlines = new List<List<Pt>>();
        public List<Opening> Openings = new List<Opening>();
        public List<Room> Rooms = new List<Room>();
        public List<Obstacle> Taken = new List<Obstacle>();
        public List<List<List<Pt>>> Walls = new List<List<List<Pt>>>();
        /// <summary>Paper width in mm of a value printed TextMm tall; 0 falls back to an estimate.</summary>
        public Func<string, double> Measure;
    }

    public readonly struct Seg
    {
        public readonly Pt A;
        public readonly Pt B;

        public Seg(Pt a, Pt b)
        {
            A = a;
            B = b;
        }
    }

    /// <summary>
    /// One value: its text, where it sits, which way it reads (left to right
    /// or bottom to top), its glyph size, and the segment it measures with
    /// the opening id at each end (null at a corner or a wall).
    /// </summary>
    public sealed class Label
    {
        public string Text;
        public int Value;
        public Pt Centre;
        public Pt Reading;
        public double Width;
        public double Height;
        public int Level;
        public Pt From;
        public Pt To;
        public string FromId;
        public string ToId;
        public Box Box;
        public bool Clear;
    }

    /// <summary>
    /// A dimension chain: points (Stops) along Dir from Origin, drawn Offset
    /// out along Out from its reference line (the face, the side's outermost
    /// face, or a room wall). Kind is facade, jog, overall or room; Side is S,
    /// E, N, W, or the room id.
    /// </summary>
    public sealed class Chain
    {
        public string Id;
        public string Kind;
        public string Side;
        public Pt Origin;
        public Pt Dir;
        public Pt Out;
        public List<double> Stops = new List<double>();
        public List<string> StopIds = new List<string>();
        public bool Witness = true;
        public double Offset;
        public int Total;
        public List<Seg> Lines = new List<Seg>();
        public List<Seg> Ticks = new List<Seg>();
        public List<Label> Texts = new List<Label>();
        public bool Placed;
        public int Collisions;
    }

    public sealed class Result
    {
        public List<Chain> Chains = new List<Chain>();
        /// <summary>Openings on an outer face, and of them those on a placed facade chain.</summary>
        public int Openings;
        public int OpeningsShown;
        /// <summary>Rectangular rooms, the ones that get a width and a depth.</summary>
        public int Rooms;
        /// <summary>Values that found no clear spot (drawn anyway, on their first choice).</summary>
        public int Collisions;
    }

    sealed class Face
    {
        public Pt A;
        public Pt Dir;
        public Pt Out;
        public double Length;
    }

    sealed class Attempt
    {
        public double Offset;
        public List<Seg> Lines = new List<Seg>();
        public List<Seg> Ticks = new List<Seg>();
        public List<Label> Texts = new List<Label>();
        public int Collisions;
    }

    public static Result Layout(Scene scene)
    {
        var result = new Result();
        if (scene == null || scene.Scale < 1) return result;
        var s = (double)scene.Scale;
        var taken = new List<Obstacle>(scene.Taken ?? new List<Obstacle>());
        var walls = scene.Walls ?? new List<List<List<Pt>>>();
        var faces = Faces(scene.Outlines);
        Frame(faces, out var u, out var v);
        var sides = new[]
        {
            (Name: "S", Out: Neg(v), Dir: u),
            (Name: "E", Out: u, Dir: v),
            (Name: "N", Out: v, Dir: u),
            (Name: "W", Out: Neg(u), Dir: v)
        };
        var outward = Offsets(FirstMm * s, ReachMm * s, StepMm * s);
        var near = Offsets(FirstMm * s, FacadeReachMm * s, StepMm * s);

        // Facade chains first, each as close to its own face as it can go; the
        // longest first, so a short chain at an inner corner works around it.
        var used = new HashSet<Opening>();
        var facades = new List<KeyValuePair<Chain, int>>();
        foreach (var face in faces)
        {
            var on = new List<KeyValuePair<double, string>>();
            foreach (var opening in scene.Openings ?? new List<Opening>())
            {
                if (opening == null || used.Contains(opening) || !OnFace(opening, face)) continue;
                used.Add(opening);
                on.Add(new KeyValuePair<double, string>(Dot(Sub(opening.Centre, face.A), face.Dir), opening.Id));
            }
            if (on.Count == 0) continue;
            result.Openings += on.Count;
            on.Sort((a, b) => a.Key.CompareTo(b.Key));
            var side = sides.OrderByDescending(item => Dot(face.Out, item.Out)).First().Name;
            var chain = NewChain("facade", side, face.A, face.Dir, face.Out, result.Chains.Count);
            AddStop(chain, 0, null);
            foreach (var item in on) AddStop(chain, item.Key, item.Value);
            AddStop(chain, face.Length, null);
            facades.Add(new KeyValuePair<Chain, int>(chain, on.Count));
            result.Chains.Add(chain);
        }
        // A chain not placed yet keeps its witness paths: from its face out to
        // the reach, no value may sit on them.
        var paths = new Dictionary<Chain, List<Obstacle>>();
        foreach (var item in facades)
            paths[item.Key] = item.Key.Stops
                .Select(t => new Obstacle(SegBox(new Seg(At(item.Key, t, 0), At(item.Key, t, FacadeReachMm * s))), Kind.Dim))
                .ToList();
        foreach (var item in facades.OrderByDescending(pair => pair.Key.Stops[pair.Key.Stops.Count - 1]))
        {
            paths.Remove(item.Key);
            var reserved = paths.Values.SelectMany(list => list).ToList();
            Place(item.Key, near, taken, walls, s, scene.Measure, false, reserved);
        }
        // One with no clear spot near its face (a crowded inner corner) goes
        // out to its side's rows, its points projected onto the side.
        for (var i = 0; i < result.Chains.Count; i++)
        {
            var chain = result.Chains[i];
            if (chain.Placed) continue;
            var side = sides.First(item => item.Name == chain.Side);
            var moved = NewChain("facade", side.Name, Mul(side.Out, Reference(faces, side.Out)), side.Dir, side.Out, i);
            moved.Id = chain.Id;
            foreach (var stop in chain.Stops
                .Select((t, k) => new KeyValuePair<double, string>(Dot(At(chain, t, 0), side.Dir), chain.StopIds[k]))
                .OrderBy(pair => pair.Key))
                AddStop(moved, stop.Key, stop.Value);
            Place(moved, outward, taken, walls, s, scene.Measure, false);
            result.Chains[i] = moved;
        }
        foreach (var item in facades)
        {
            var chain = result.Chains.First(c => c.Id == item.Key.Id);
            if (chain.Placed) result.OpeningsShown += item.Value;
        }

        // Per side: the jogs it sees of the outline, then the overall.
        if (faces.Count > 0)
        {
            foreach (var side in sides)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var face in faces)
                {
                    foreach (var p in new[] { face.A, End(face) })
                    {
                        lo = Math.Min(lo, Dot(p, side.Dir));
                        hi = Math.Max(hi, Dot(p, side.Dir));
                    }
                }
                var origin = Mul(side.Out, Reference(faces, side.Out));
                var covered = new List<KeyValuePair<double, double>>();
                var stops = new List<double>();
                foreach (var face in faces
                    .Where(f => Dot(f.Out, side.Out) >= 0.99)
                    .OrderByDescending(f => Dot(f.A, side.Out)))
                {
                    var a = Dot(face.A, side.Dir);
                    var b = Dot(End(face), side.Dir);
                    foreach (var piece in Uncovered(Math.Min(a, b), Math.Max(a, b), covered))
                    {
                        stops.Add(piece.Key);
                        stops.Add(piece.Value);
                        covered.Add(piece);
                    }
                }
                stops.Sort();
                var jog = NewChain("jog", side.Name, origin, side.Dir, side.Out, result.Chains.Count);
                foreach (var t in stops) AddStop(jog, t, null);
                if (jog.Stops.Count > 2)
                {
                    Place(jog, outward, taken, walls, s, scene.Measure, false);
                    result.Chains.Add(jog);
                }
                var overall = NewChain("overall", side.Name, origin, side.Dir, side.Out, result.Chains.Count);
                AddStop(overall, lo, null);
                AddStop(overall, hi, null);
                if (overall.Stops.Count < 2) continue;
                Place(overall, outward, taken, walls, s, scene.Measure, false);
                result.Chains.Add(overall);
            }
        }

        // Rooms: a width and a depth in each rectangle, near a wall, clear of
        // its tag and the door swings, or not at all.
        foreach (var room in scene.Rooms ?? new List<Room>())
        {
            if (!TryRectangle(room?.Ring, u, out var corner, out var along, out var width, out var depth)) continue;
            result.Rooms++;
            var across = Left(along);
            var wide = NewChain("room", room.Id, corner, along, across, result.Chains.Count);
            wide.Witness = false;
            AddStop(wide, 0, null);
            AddStop(wide, width, null);
            Place(wide, Inward(depth, s), taken, walls, s, scene.Measure, true);
            result.Chains.Add(wide);
            var deep = NewChain("room", room.Id, Add(corner, Mul(along, width)), across, Neg(along), result.Chains.Count);
            deep.Witness = false;
            AddStop(deep, 0, null);
            AddStop(deep, depth, null);
            Place(deep, Inward(width, s), taken, walls, s, scene.Measure, true);
            result.Chains.Add(deep);
        }

        foreach (var chain in result.Chains)
            result.Collisions += chain.Placed ? chain.Collisions : 0;
        return result;
    }

    /// <summary>
    /// Where a room tag that does not fit its room goes: straight out of the
    /// room from its inside point along a drawing axis, at the nearest spot
    /// where the tag (half extents hx, hy) clears everything drawn so far and
    /// the poché, with a leader to its near edge that touches no text. False
    /// when nothing within LeaderReachMm past the room is clear.
    /// </summary>
    public static bool PlaceLeader(
        Pt inside, IList<Pt> ring, double hx, double hy, int scale,
        IList<Obstacle> taken, List<List<List<Pt>>> walls, out Pt centre, out Seg leader)
    {
        centre = default;
        leader = default;
        if (ring == null || ring.Count < 3 || scale < 1) return false;
        var s = (double)scale;
        var clear = ClearMm * s;
        var best = double.MaxValue;
        foreach (var dir in new[] { new Pt(1, 0), new Pt(-1, 0), new Pt(0, 1), new Pt(0, -1) })
        {
            var exit = Exit(inside, dir, ring);
            if (double.IsInfinity(exit)) continue;
            var half = Math.Abs(dir.X) > 0 ? hx : hy;
            for (var t = exit + clear + half; t <= exit + half + LeaderReachMm * s && t < best; t += StepMm * s)
            {
                var at = Add(inside, Mul(dir, t));
                var box = new Box(at.X - hx, at.Y - hy, at.X + hx, at.Y + hy);
                if (Blocked(box, taken, clear, true) || Schedules.OnWalls(Grow(box, clear), walls)) continue;
                var lead = new Seg(inside, Add(inside, Mul(dir, t - half - TextGapMm * s)));
                if (taken != null && taken.Any(o => o.Kind == Kind.Text && Schedules.Overlaps(SegBox(lead), o.Box, clear)))
                    continue;
                best = t;
                centre = at;
                leader = lead;
                break;
            }
        }
        return best < double.MaxValue;
    }

    /// <summary>The box of a segment.</summary>
    public static Box SegBox(Seg seg)
    {
        return new Box(
            Math.Min(seg.A.X, seg.B.X), Math.Min(seg.A.Y, seg.B.Y),
            Math.Max(seg.A.X, seg.B.X), Math.Max(seg.A.Y, seg.B.Y));
    }

    static Chain NewChain(string kind, string side, Pt origin, Pt dir, Pt outward, int index)
    {
        return new Chain
        {
            Id = kind + "-" + side + "-" + (index + 1).ToString(CultureInfo.InvariantCulture),
            Kind = kind,
            Side = side,
            Origin = origin,
            Dir = dir,
            Out = outward
        };
    }

    /// <summary>Adds a point along the chain; one within 1 mm of the last is the same point.</summary>
    static void AddStop(Chain chain, double t, string id)
    {
        if (chain.Stops.Count > 0 && Math.Abs(t - chain.Stops[chain.Stops.Count - 1]) < 1.0) return;
        chain.Stops.Add(t);
        chain.StopIds.Add(id);
    }

    static List<double> Offsets(double first, double last, double step)
    {
        var list = new List<double>();
        for (var d = first; d <= last + 1e-9; d += step) list.Add(d);
        return list;
    }

    /// <summary>A room dimension's offsets from its wall: near a wall first, then toward the middle.</summary>
    static List<double> Inward(double extent, double s)
    {
        var list = new List<double>();
        var inset = InsetMm * s;
        for (var k = 0; ; k++)
        {
            var near = inset + k * StepMm * s;
            var far = extent - inset - k * StepMm * s;
            if (near > far + 1e-9) break;
            list.Add(near);
            if (far - near > 1e-9) list.Add(far);
        }
        return list;
    }

    /// <summary>
    /// The chain at the first offset where its lines are clear and every value
    /// has a clear spot. Else the offset with clear lines and the fewest values
    /// without one (an optional chain, a room's, is then left out). Else not
    /// placed. Reserved paths are kept clear like what is drawn. What it placed
    /// joins taken.
    /// </summary>
    static void Place(Chain chain, List<double> offsets, List<Obstacle> taken, List<List<List<Pt>>> walls,
        double s, Func<string, double> measure, bool optional, List<Obstacle> reserved = null)
    {
        chain.Placed = false;
        if (chain.Stops.Count < 2) return;
        var rounded = chain.Stops.Select(t => Math.Round(t - chain.Stops[0], MidpointRounding.AwayFromZero)).ToList();
        chain.Total = (int)rounded[rounded.Count - 1];
        if (offsets.Count == 0) return;
        var texts = new List<string>();
        var widths = new List<double>();
        for (var i = 0; i + 1 < rounded.Count; i++)
        {
            var text = ((int)(rounded[i + 1] - rounded[i])).ToString(CultureInfo.InvariantCulture);
            var paper = measure?.Invoke(text) ?? 0;
            if (paper <= 0) paper = 0.6 * TextMm * text.Length;
            texts.Add(text);
            widths.Add(paper * s);
        }
        // A room value must fit between its walls; the room can do without it.
        if (optional)
        {
            for (var i = 0; i < texts.Count; i++)
                if (chain.Stops[i + 1] - chain.Stops[i] < widths[i] + 2 * (TextPadMm + ClearMm) * s) return;
        }

        var margin = widths.Max() + 2 * (TextPadMm + ClearMm) * s;
        var low = offsets.Min() - (WitnessInMm + 3 * RowMm + ClearMm) * s;
        var high = offsets.Max() + (WitnessOutMm + 3 * RowMm + ClearMm) * s;
        var first = chain.Stops[0] - margin;
        var last = chain.Stops[chain.Stops.Count - 1] + margin;
        var region = BoxOf(At(chain, first, low), At(chain, last, low), At(chain, first, high), At(chain, last, high));
        var near = taken.Concat(reserved ?? new List<Obstacle>()).Where(o => Schedules.Overlaps(o.Box, region, 0)).ToList();

        Attempt best = null;
        foreach (var d in offsets)
        {
            var attempt = Try(chain, d, texts, widths, near, walls, s);
            if (attempt == null) continue;
            if (attempt.Collisions == 0)
            {
                best = attempt;
                break;
            }
            if (best == null || attempt.Collisions < best.Collisions) best = attempt;
        }
        if (best == null || (optional && best.Collisions > 0)) return;

        chain.Placed = true;
        chain.Offset = best.Offset;
        chain.Lines = best.Lines;
        chain.Ticks = best.Ticks;
        chain.Texts = best.Texts;
        chain.Collisions = best.Collisions;
        foreach (var line in chain.Lines) taken.Add(new Obstacle(SegBox(line), Kind.Dim));
        foreach (var tick in chain.Ticks) taken.Add(new Obstacle(SegBox(tick), Kind.Crossable));
        foreach (var label in chain.Texts) taken.Add(new Obstacle(label.Box, Kind.Text));
    }

    /// <summary>
    /// The chain drawn at offset d, or null when its dimension line or a
    /// witness line is blocked. Each value takes its first clear spot: on the
    /// line between its ticks when it fits there, centred, then slid along its
    /// segment; else lifted a row, then two, centred then slid; else beside
    /// its segment, past a tick.
    /// </summary>
    static Attempt Try(Chain chain, double d, List<string> texts, List<double> widths,
        List<Obstacle> near, List<List<List<Pt>>> walls, double s)
    {
        var clear = ClearMm * s;
        var first = chain.Stops[0];
        var last = chain.Stops[chain.Stops.Count - 1];
        var attempt = new Attempt { Offset = d };
        var line = new Seg(At(chain, first, d), At(chain, last, d));
        // The line may end on a wall face; only its run between must be clear.
        var run = SegBox(new Seg(At(chain, first + clear, d), At(chain, last - clear, d)));
        if (Blocked(run, near, clear, false) || Schedules.OnWalls(run, walls)) return null;
        attempt.Lines.Add(line);
        var own = new List<Box> { SegBox(line) };
        if (chain.Witness)
        {
            foreach (var t in chain.Stops)
            {
                var witness = new Seg(At(chain, t, d - WitnessInMm * s), At(chain, t, d + WitnessOutMm * s));
                var box = SegBox(witness);
                if (Blocked(box, near, clear, false)) return null;
                attempt.Lines.Add(witness);
                own.Add(box);
            }
        }

        var reading = Reading(chain.Dir);
        var up = Left(reading);
        var tilt = Unit(Add(reading, up));
        var tick = TickMm * s / 2.0;
        foreach (var t in chain.Stops)
        {
            var at = At(chain, t, d);
            attempt.Ticks.Add(new Seg(Sub(at, Mul(tilt, tick)), Add(at, Mul(tilt, tick))));
        }

        var height = TextMm * s;
        var pad = TextPadMm * s;
        for (var i = 0; i < texts.Count; i++)
        {
            var a = chain.Stops[i];
            var b = chain.Stops[i + 1];
            var w = widths[i];
            var mid = (a + b) / 2.0;
            var step = StepMm * s;
            var spots = new List<KeyValuePair<double, int>>();
            var slack = (b - a) - (w + 2 * (pad + clear));
            if (slack >= 0) Slide(spots, mid, slack / 2.0, step, 0);
            Slide(spots, mid, (b - a) / 2.0, step, 1);
            Slide(spots, mid, (b - a) / 2.0, step, 2);
            var shift = (b - a) / 2.0 + clear + w / 2.0 + pad;
            foreach (var level in new[] { 0, 1, 2 })
            {
                spots.Add(new KeyValuePair<double, int>(mid + shift, level));
                spots.Add(new KeyValuePair<double, int>(mid - shift, level));
            }
            Label pick = null;
            foreach (var spot in spots)
            {
                var label = NewLabel(chain, i, texts[i], w, height, pad, reading, up, spot.Key, spot.Value, d, s);
                if (!TextClear(label.Box, near, walls, own, attempt.Texts, clear)) continue;
                label.Clear = true;
                pick = label;
                break;
            }
            if (pick == null)
            {
                pick = NewLabel(chain, i, texts[i], w, height, pad, reading, up, spots[0].Key, spots[0].Value, d, s);
                attempt.Collisions++;
            }
            attempt.Texts.Add(pick);
        }
        return attempt;
    }

    /// <summary>Spots at level: mid, then mid ± a step at a time, then the two ends of the reach.</summary>
    static void Slide(List<KeyValuePair<double, int>> spots, double mid, double reach, double step, int level)
    {
        spots.Add(new KeyValuePair<double, int>(mid, level));
        for (var off = step; off < reach; off += step)
        {
            spots.Add(new KeyValuePair<double, int>(mid + off, level));
            spots.Add(new KeyValuePair<double, int>(mid - off, level));
        }
        if (reach <= 0) return;
        spots.Add(new KeyValuePair<double, int>(mid + reach, level));
        spots.Add(new KeyValuePair<double, int>(mid - reach, level));
    }

    static Label NewLabel(Chain chain, int i, string text, double w, double h, double pad,
        Pt reading, Pt up, double along, int level, double d, double s)
    {
        var lift = TextGapMm * s + h / 2.0 + level * RowMm * s;
        var centre = Add(At(chain, along, d), Mul(up, lift));
        var a = w / 2.0 + pad;
        var b = h / 2.0 + pad;
        var hx = Math.Abs(reading.X) * a + Math.Abs(up.X) * b;
        var hy = Math.Abs(reading.Y) * a + Math.Abs(up.Y) * b;
        return new Label
        {
            Text = text,
            Value = int.Parse(text, CultureInfo.InvariantCulture),
            Centre = centre,
            Reading = reading,
            Width = w,
            Height = h,
            Level = level,
            From = At(chain, chain.Stops[i], 0),
            To = At(chain, chain.Stops[i + 1], 0),
            FromId = chain.StopIds[i],
            ToId = chain.StopIds[i + 1],
            Box = new Box(centre.X - hx, centre.Y - hy, centre.X + hx, centre.Y + hy)
        };
    }

    /// <summary>
    /// A value's box is clear when it keeps the clearance to everything drawn,
    /// sits off the poché, and does not touch its own chain's lines or values
    /// (their padded boxes may meet: a lifted value over its neighbour).
    /// </summary>
    static bool TextClear(Box box, List<Obstacle> near, List<List<List<Pt>>> walls, List<Box> own, List<Label> placed, double clear)
    {
        if (Blocked(box, near, clear, true)) return false;
        if (Schedules.OnWalls(Grow(box, clear), walls)) return false;
        foreach (var line in own)
            if (Schedules.Overlaps(box, line, 0)) return false;
        foreach (var other in placed)
            if (Schedules.Overlaps(box, other.Box, 0)) return false;
        return true;
    }

    /// <summary>
    /// Text keeps clear of everything. A line keeps clear of text and symbols,
    /// and of any dimension line it would run along.
    /// </summary>
    static bool Blocked(Box box, IList<Obstacle> obstacles, double clear, bool text)
    {
        if (obstacles == null) return false;
        foreach (var o in obstacles)
        {
            if (!text && o.Kind == Kind.Crossable) continue;
            if (!text && o.Kind == Kind.Dim && !Along(box, o.Box)) continue;
            if (Schedules.Overlaps(box, o.Box, clear)) return true;
        }
        return false;
    }

    /// <summary>
    /// Two line boxes that both lie along drawing X or both along Y (a box at
    /// least four times as long as it is wide; a stroke ribbon has a width):
    /// overlapping, one runs along the other.
    /// </summary>
    public static bool Along(Box a, Box b)
    {
        return (Flat(a) && Flat(b)) || (Flat(Turn(a)) && Flat(Turn(b)));
    }

    static bool Flat(Box box) => 4 * (box.MaxY - box.MinY) <= box.MaxX - box.MinX;

    static Box Turn(Box box) => new Box(box.MinY, box.MinX, box.MaxY, box.MaxX);

    /// <summary>Is an opening in the wall behind this face: parallel, its centre half the wall in from the face, within the face's length.</summary>
    static bool OnFace(Opening opening, Face face)
    {
        var cross = opening.Along.X * face.Dir.Y - opening.Along.Y * face.Dir.X;
        if (Math.Abs(cross) > 0.05) return false;
        var rel = Sub(opening.Centre, face.A);
        var depth = -Dot(rel, face.Out);
        if (Math.Abs(depth - opening.HalfThick) > Math.Max(5.0, 0.25 * opening.HalfThick)) return false;
        var t = Dot(rel, face.Dir);
        return t >= 0 && t <= face.Length;
    }

    /// <summary>The outline's straight runs, counter-clockwise, each with its outward normal.</summary>
    static List<Face> Faces(List<List<Pt>> outlines)
    {
        var faces = new List<Face>();
        foreach (var raw in outlines ?? new List<List<Pt>>())
        {
            if (raw == null || raw.Count < 3) continue;
            var ring = RoomDetect.Simplify(new List<Pt>(raw), 1.0);
            if (ring.Count < 3) continue;
            if (RoomDetect.Area(ring) < 0) ring.Reverse();
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                var length = Dist(a, b);
                if (length < 1.0) continue;
                var dir = new Pt((b.X - a.X) / length, (b.Y - a.Y) / length);
                faces.Add(new Face { A = a, Dir = dir, Out = new Pt(dir.Y, -dir.X), Length = length });
            }
        }
        return faces;
    }

    /// <summary>The building's axes: its longest face, turned to within 45° of drawing X.</summary>
    static void Frame(List<Face> faces, out Pt u, out Pt v)
    {
        u = new Pt(1, 0);
        Face longest = null;
        foreach (var face in faces)
            if (longest == null || face.Length > longest.Length) longest = face;
        if (longest != null)
        {
            var quarter = Math.PI / 2.0;
            var angle = Math.Atan2(longest.Dir.Y, longest.Dir.X);
            angle -= quarter * Math.Round(angle / quarter);
            u = new Pt(Math.Cos(angle), Math.Sin(angle));
        }
        v = Left(u);
    }

    /// <summary>
    /// A room outline with four right-angled corners: the corner its width
    /// runs from (the edge nearest the building's X axis), the unit along
    /// that edge, the width and the depth.
    /// </summary>
    static bool TryRectangle(List<Pt> raw, Pt u, out Pt corner, out Pt along, out double width, out double depth)
    {
        corner = default;
        along = default;
        width = depth = 0;
        if (raw == null || raw.Count < 4) return false;
        var ring = RoomDetect.Simplify(new List<Pt>(raw), 1.0);
        if (ring.Count != 4) return false;
        if (RoomDetect.Area(ring) < 0) ring.Reverse();
        var start = 0;
        var bestDot = double.MinValue;
        for (var i = 0; i < 4; i++)
        {
            var e = Sub(ring[(i + 1) % 4], ring[i]);
            var f = Sub(ring[(i + 2) % 4], ring[(i + 1) % 4]);
            var le = Math.Sqrt(Dot(e, e));
            var lf = Math.Sqrt(Dot(f, f));
            if (le < 1 || lf < 1 || Math.Abs(Dot(e, f)) > 0.02 * le * lf) return false;
            var dot = Dot(e, u) / le;
            if (dot > bestDot + 1e-9)
            {
                bestDot = dot;
                start = i;
            }
        }
        corner = ring[start];
        var edge = Sub(ring[(start + 1) % 4], corner);
        width = Math.Sqrt(Dot(edge, edge));
        along = Mul(edge, 1.0 / width);
        var side = Sub(ring[(start + 2) % 4], ring[(start + 1) % 4]);
        depth = Math.Sqrt(Dot(side, side));
        return true;
    }

    /// <summary>The parts of [lo, hi] no covered interval holds, each over 1 mm.</summary>
    static List<KeyValuePair<double, double>> Uncovered(double lo, double hi, List<KeyValuePair<double, double>> covered)
    {
        var pieces = new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(lo, hi) };
        foreach (var cut in covered)
        {
            var next = new List<KeyValuePair<double, double>>();
            foreach (var piece in pieces)
            {
                if (cut.Value <= piece.Key || cut.Key >= piece.Value)
                {
                    next.Add(piece);
                    continue;
                }
                if (cut.Key > piece.Key) next.Add(new KeyValuePair<double, double>(piece.Key, cut.Key));
                if (cut.Value < piece.Value) next.Add(new KeyValuePair<double, double>(cut.Value, piece.Value));
            }
            pieces = next;
        }
        return pieces.Where(p => p.Value - p.Key > 1.0).ToList();
    }

    /// <summary>Distance along dir from p to where the ray leaves the ring.</summary>
    static double Exit(Pt p, Pt dir, IList<Pt> ring)
    {
        var best = double.PositiveInfinity;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var den = dir.X * ey - dir.Y * ex;
            if (Math.Abs(den) < 1e-12) continue;
            var t = ((a.X - p.X) * ey - (a.Y - p.Y) * ex) / den;
            var k = ((a.X - p.X) * dir.Y - (a.Y - p.Y) * dir.X) / den;
            if (t > 1e-9 && k >= -1e-9 && k <= 1 + 1e-9) best = Math.Min(best, t);
        }
        return best;
    }

    /// <summary>A value reads left to right, or bottom to top on a vertical chain.</summary>
    static Pt Reading(Pt dir)
    {
        return dir.X > 1e-9 || (Math.Abs(dir.X) <= 1e-9 && dir.Y > 0) ? dir : Neg(dir);
    }

    static Pt At(Chain chain, double along, double offset)
    {
        return Add(chain.Origin, Add(Mul(chain.Dir, along), Mul(chain.Out, offset)));
    }

    static Pt End(Face face) => Add(face.A, Mul(face.Dir, face.Length));

    /// <summary>How far the outline reaches toward a side: its rows are drawn from there out.</summary>
    static double Reference(List<Face> faces, Pt outward)
    {
        var reference = double.MinValue;
        foreach (var face in faces)
            reference = Math.Max(reference, Math.Max(Dot(face.A, outward), Dot(End(face), outward)));
        return reference;
    }

    static Box BoxOf(params Pt[] points)
    {
        return new Box(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    static Box Grow(Box box, double by) => new Box(box.MinX - by, box.MinY - by, box.MaxX + by, box.MaxY + by);

    static Pt Add(Pt a, Pt b) => new Pt(a.X + b.X, a.Y + b.Y);
    static Pt Sub(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);
    static Pt Mul(Pt a, double k) => new Pt(a.X * k, a.Y * k);
    static Pt Neg(Pt a) => new Pt(-a.X, -a.Y);
    static Pt Left(Pt a) => new Pt(-a.Y, a.X);
    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
    static double Dist(Pt a, Pt b) => Math.Sqrt(Dot(Sub(a, b), Sub(a, b)));

    static Pt Unit(Pt a)
    {
        var length = Math.Sqrt(Dot(a, a));
        return length < 1e-12 ? a : Mul(a, 1.0 / length);
    }
}
