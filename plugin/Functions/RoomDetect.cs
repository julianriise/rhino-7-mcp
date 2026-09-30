using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Rooms from a plan in mm: the closed regions left between wall footprints,
/// split by space dividers. Pure geometry, no RhinoCommon, so it tests headless.
/// Two facing wall ends with a gap between them close when a door sits in the
/// gap. Without a door the region stays open: it is reported, not made a room.
/// </summary>
public static class RoomDetect
{
    /// <summary>Smaller free regions are slivers (cavities, door reveals), not rooms.</summary>
    public const double MinAreaMm2 = 1000000.0;
    /// <summary>Widest gap between two wall ends that a door can close.</summary>
    public const double MaxGapMm = 2000.0;
    /// <summary>Longest wall end (jamb face). Thicker is a wall side, not an end.</summary>
    public const double MaxEndMm = 600.0;
    /// <summary>A divider may stop this short of a wall face and still split the room.</summary>
    public const double ReachMm = 300.0;

    public readonly struct Pt
    {
        public readonly double X;
        public readonly double Y;

        public Pt(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    public readonly struct Box
    {
        public readonly double MinX, MinY, MaxX, MaxY;

        public Box(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }
    }

    public sealed class Scene
    {
        /// <summary>Per wall: its footprint rings (outer, then holes), read even-odd.</summary>
        public List<List<List<Pt>>> Walls = new List<List<List<Pt>>>();
        /// <summary>Door and opening marker boxes in plan. Windows do not close gaps.</summary>
        public List<Box> Doors = new List<Box>();
        /// <summary>space_divider polylines, open or closed.</summary>
        public List<List<Pt>> Dividers = new List<List<Pt>>();
        /// <summary>Closed room outlines already drawn. They win over detection.</summary>
        public List<List<Pt>> Keep = new List<List<Pt>>();
        public double MinArea = MinAreaMm2;
        public double Tol = 1.0;
    }

    public sealed class Room
    {
        public List<Pt> Ring;
        public int Holes;
        public double Area;
        public Pt Inside;
    }

    public sealed class Open
    {
        public string Reason;
        public Pt At;
    }

    public sealed class Result
    {
        public List<Room> Rooms = new List<Room>();
        public List<Open> Open = new List<Open>();
        public int Slivers;
        public int Kept;
    }

    public static Result Detect(Scene scene)
    {
        var tol = scene.Tol > 0 ? scene.Tol : 1.0;
        var segs = new List<Seg>();
        foreach (var wall in scene.Walls)
            foreach (var ring in wall)
                AddPath(segs, ring, true, Kind.Wall, -1, 0);
        for (var i = 0; i < scene.Dividers.Count; i++)
            AddPath(segs, scene.Dividers[i], false, Kind.Divider, i, ReachMm);

        var plan = Plan.Build(segs, tol);
        plan.Classify(scene);
        var bridges = plan.Bridges(scene);
        if (bridges.Count > 0)
        {
            segs.AddRange(bridges);
            plan = Plan.Build(segs, tol);
            plan.Classify(scene);
        }

        var result = new Result();
        for (var i = 0; i < scene.Dividers.Count; i++)
        {
            if (plan.HasEdge(Kind.Divider, i)) continue;
            var line = scene.Dividers[i];
            if (line == null || line.Count < 2) continue;
            result.Open.Add(new Open
            {
                Reason = "space divider does not meet a wall",
                At = Mid(line[0], line[line.Count - 1])
            });
        }

        foreach (var face in plan.Faces)
        {
            if (!face.Free) continue;
            var area = face.Area;
            foreach (var hole in face.Holes) area -= Math.Abs(hole.Area);
            var gap = plan.GapOn(face);
            if (gap > 0)
            {
                if (area >= scene.MinArea)
                    result.Open.Add(new Open { Reason = "gap " + Metres(gap) + " m without a door", At = face.Inside });
                continue;
            }
            if (area < scene.MinArea)
            {
                result.Slivers++;
                continue;
            }
            var ring = Simplify(plan.Points(face), tol);
            if (Covered(scene.Keep, ring, face.Inside))
            {
                result.Kept++;
                continue;
            }
            result.Rooms.Add(new Room { Ring = ring, Holes = face.Holes.Count, Area = area, Inside = face.Inside });
        }
        return result;
    }

    /// <summary>
    /// Closed outlines read as one shape: the boundary loops of everything
    /// any of them covers. Separate wall rectangles that overlap or touch give
    /// the outline they make together: outer loops counterclockwise (area
    /// above 0) and the holes they close clockwise (area below 0). An outline
    /// that touches nothing comes back as drawn.
    /// </summary>
    public static List<List<Pt>> Union(IList<List<Pt>> rings, double tol)
    {
        var scene = new Scene { Tol = tol > 0 ? tol : 1.0 };
        var segs = new List<Seg>();
        foreach (var ring in rings)
        {
            scene.Walls.Add(new List<List<Pt>> { ring });
            AddPath(segs, ring, true, Kind.Wall, -1, 0);
        }
        var plan = Plan.Build(segs, scene.Tol);
        plan.Classify(scene);
        return plan.Boundary();
    }

    /// <summary>
    /// Stable ids across runs. A room keeps the id of the earlier outline it
    /// overlaps; a new room takes the next free number after prefix.
    /// </summary>
    public static string[] Match(IList<Room> rooms, IList<KeyValuePair<string, List<Pt>>> earlier, string prefix)
    {
        var ids = new string[rooms.Count];
        var used = new bool[earlier.Count];
        var next = 1;
        foreach (var old in earlier)
        {
            var id = old.Key ?? "";
            if (id.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(id.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                next = Math.Max(next, n + 1);
        }
        for (var r = 0; r < rooms.Count; r++)
        {
            for (var o = 0; o < earlier.Count && ids[r] == null; o++)
            {
                if (used[o] || earlier[o].Value == null || earlier[o].Value.Count < 3) continue;
                var overlap = Contains(earlier[o].Value, rooms[r].Inside)
                    || (TryInside(new List<List<Pt>> { earlier[o].Value }, out var at) && Contains(rooms[r].Ring, at));
                if (!overlap) continue;
                used[o] = true;
                ids[r] = earlier[o].Key;
            }
            if (ids[r] == null)
                ids[r] = prefix + (next++).ToString("00", CultureInfo.InvariantCulture);
        }
        return ids;
    }

    /// <summary>Name for a room with no label inside it.</summary>
    public const string DefaultRoomName = "Rom";
    /// <summary>Fixture labels (a cupboard, a drain) name a room only up to this size.</summary>
    public const double FixtureRoomMaxMm2 = 20000000.0;

    static readonly string[] FixtureWords =
    {
        "brannskap", "sluk", "skap", "elskap", "el-skap", "sikringsskap", "brannslukker", "sprinkler"
    };

    public readonly struct Label
    {
        public readonly string Text;
        public readonly double Height;
        public readonly Pt At;

        public Label(string text, double height, Pt at)
        {
            Text = text;
            Height = height;
            At = at;
        }
    }

    /// <summary>
    /// The one place a room gets its name: the labels inside its ring, picked
    /// by <see cref="PickLabel"/> with the ring's area and inside point. The
    /// name depends on the ring alone. rooms_detect names each detected room
    /// here once and stamps it on the marker; the plan tag shows that stamp.
    /// </summary>
    public static string Name(IList<Label> labels, List<Pt> ring)
    {
        var inside = new List<Label>();
        foreach (var label in labels)
            if (Contains(ring, label.At)) inside.Add(label);
        TryInside(new List<List<Pt>> { ring }, out var at);
        return PickLabel(inside, Math.Abs(Area(ring)), at);
    }

    /// <summary>
    /// What the plan tag shows for one room marker, from rooms_detect's result.
    /// A detected room is bounded by construction (detection only closes a
    /// region that walls, doors or a space divider bound) and shows the name
    /// rooms_detect stamped on it. An outline drawn by hand is the user's word,
    /// named by <see cref="Name"/>. Under <see cref="MinAreaMm2"/> neither is
    /// tagged: <paramref name="untagged"/> says why, else it is null.
    /// </summary>
    public static string TagName(string detectedName, double areaMm2, IList<Label> labels, List<Pt> ring, out string untagged)
    {
        var name = string.IsNullOrEmpty(detectedName) ? Name(labels, ring) : detectedName;
        untagged = areaMm2 < MinAreaMm2
            ? (areaMm2 / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + " m² under 1 m²"
            : null;
        return name;
    }

    /// <summary>
    /// The rule <see cref="Name"/> applies to the labels inside a room. Fixture
    /// labels do not name a room over 20 m². Of the rest the tallest text wins,
    /// then the one nearest the room's inside point. No label left: Rom.
    /// </summary>
    public static string PickLabel(IList<Label> labels, double areaMm2, Pt inside)
    {
        string best = null;
        var bestHeight = 0.0;
        var bestDist = 0.0;
        foreach (var label in labels)
        {
            var text = (label.Text ?? "").Trim();
            if (text.Length == 0) continue;
            if (areaMm2 > FixtureRoomMaxMm2 && Array.IndexOf(FixtureWords, text.ToLowerInvariant()) >= 0) continue;
            var dist = Dist(label.At, inside);
            var taller = label.Height > bestHeight + 1e-6;
            var tie = Math.Abs(label.Height - bestHeight) <= 1e-6 && dist < bestDist;
            if (best != null && !taller && !tie) continue;
            best = text;
            bestHeight = label.Height;
            bestDist = dist;
        }
        return best ?? DefaultRoomName;
    }

    /// <summary>
    /// Total area of the rooms in mm², holes subtracted as each room's own area
    /// is, plus the outlines drawn by hand.
    /// </summary>
    public static double TotalArea(IList<Room> rooms, IEnumerable<double> drawnMm2)
    {
        var total = 0.0;
        foreach (var room in rooms) total += room.Area;
        foreach (var area in drawnMm2) total += area;
        return total;
    }

    public static double Area(IList<Pt> ring)
    {
        if (ring == null || ring.Count < 3) return 0;
        var sum = 0.0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            sum += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
        return sum / 2.0;
    }

    public static bool Contains(IList<Pt> ring, Pt p)
    {
        if (ring == null || ring.Count < 3) return false;
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Y > p.Y) != (b.Y > p.Y)
                && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>A centroid this close to the edge, next to the roomiest point, gives way to it.</summary>
    public const double Cramped = 0.5;

    /// <summary>
    /// A point well inside the even-odd region of rings, and where a room's
    /// tag goes: the centroid, unless it lies outside or has under half the
    /// edge clearance of the roomiest point (the inside point farthest from
    /// every edge). Then that roomiest point. A rectangle keeps its centre;
    /// an L-shaped room gets its point in the open part, not at the inner
    /// corner its centroid falls next to.
    /// </summary>
    public static bool TryInside(List<List<Pt>> rings, out Pt at)
    {
        at = default;
        double area2 = 0, cx = 0, cy = 0, sx = 0, sy = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var n = 0;
        foreach (var ring in rings)
        {
            if (ring == null) continue;
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                var cross = a.X * b.Y - b.X * a.Y;
                area2 += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
                sx += a.X;
                sy += a.Y;
                n++;
                minX = Math.Min(minX, a.X);
                minY = Math.Min(minY, a.Y);
                maxX = Math.Max(maxX, a.X);
                maxY = Math.Max(maxY, a.Y);
            }
        }
        if (n < 3) return false;
        var centroid = Math.Abs(area2) < 1e-6
            ? new Pt(sx / n, sy / n)
            : new Pt(cx / (3.0 * area2), cy / (3.0 * area2));

        // The roomiest point: the best clearance on a 32-step grid, refined by
        // halving the step around it. A band too thin for the grid (a wall
        // footprint) gets a finer one.
        var span = Math.Max(maxX - minX, maxY - minY);
        if (span <= 0) return false;
        var best = 0.0;
        var roomiest = default(Pt);
        var step = 0.0;
        for (var cells = 32; cells <= 256 && best <= 0; cells *= 2)
        {
            step = span / cells;
            for (var x = minX + step / 2; x < maxX; x += step)
                for (var y = minY + step / 2; y < maxY; y += step)
                    Roomier(rings, new Pt(x, y), ref roomiest, ref best);
        }
        for (var k = 0; k < 12 && best > 0; k++)
        {
            step /= 2;
            var around = roomiest;
            for (var i = -2; i <= 2; i++)
                for (var j = -2; j <= 2; j++)
                    Roomier(rings, new Pt(around.X + i * step, around.Y + j * step), ref roomiest, ref best);
        }
        if (best <= 0) return false;
        var keep = InRings(rings, centroid) && Clearance(rings, centroid) >= Cramped * best;
        at = keep ? centroid : roomiest;
        return true;
    }

    static void Roomier(List<List<Pt>> rings, Pt p, ref Pt roomiest, ref double best)
    {
        if (!InRings(rings, p)) return;
        var d = Clearance(rings, p);
        if (d <= best) return;
        best = d;
        roomiest = p;
    }

    static bool InRings(List<List<Pt>> rings, Pt p)
    {
        var inside = false;
        foreach (var ring in rings)
            if (Contains(ring, p)) inside = !inside;
        return inside;
    }

    static double Clearance(List<List<Pt>> rings, Pt p)
    {
        var best = double.MaxValue;
        foreach (var ring in rings)
            best = Math.Min(best, Clearance(ring, p));
        return best;
    }

    /// <summary>Distance from p to the nearest edge of ring.</summary>
    public static double Clearance(IList<Pt> ring, Pt p)
    {
        var best = double.MaxValue;
        if (ring == null) return best;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len2 = dx * dx + dy * dy;
            var t = len2 <= 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
            var ex = a.X + dx * t - p.X;
            var ey = a.Y + dy * t - p.Y;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    static bool Covered(List<List<Pt>> keep, List<Pt> ring, Pt inside)
    {
        foreach (var drawn in keep)
        {
            if (drawn == null || drawn.Count < 3) continue;
            if (Contains(drawn, inside)) return true;
            if (TryInside(new List<List<Pt>> { drawn }, out var at) && Contains(ring, at)) return true;
        }
        return false;
    }

    /// <summary>Drops repeated and straight-through vertices.</summary>
    public static List<Pt> Simplify(List<Pt> ring, double tol)
    {
        var pts = new List<Pt>(ring);
        var changed = true;
        while (changed && pts.Count > 3)
        {
            changed = false;
            for (var i = 0; i < pts.Count && pts.Count > 3; i++)
            {
                var a = pts[(i - 1 + pts.Count) % pts.Count];
                var b = pts[i];
                var c = pts[(i + 1) % pts.Count];
                var ab = Dist(a, b);
                var bc = Dist(b, c);
                var straight = ab <= tol
                    || (Math.Abs(Cross(a, b, c)) <= tol * Math.Max(Dist(a, c), tol) && Dot(b.X - a.X, b.Y - a.Y, c.X - b.X, c.Y - b.Y) > 0);
                if (!straight) continue;
                pts.RemoveAt(i);
                changed = true;
                i--;
            }
        }
        return pts;
    }

    static string Metres(double mm)
    {
        return (mm / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
    }

    enum Kind
    {
        Wall,
        Divider,
        Door,
        Gap
    }

    sealed class Seg
    {
        public Pt A;
        public Pt B;
        public Kind Kind;
        public int Tag;
        public double Gap;
    }

    static void AddPath(List<Seg> segs, List<Pt> path, bool closed, Kind kind, int tag, double reach)
    {
        if (path == null || path.Count < 2) return;
        var pts = new List<Pt>(path);
        if (!closed && reach > 0 && Dist(pts[0], pts[pts.Count - 1]) > 1e-6)
        {
            pts[0] = Extend(pts[1], pts[0], reach);
            pts[pts.Count - 1] = Extend(pts[pts.Count - 2], pts[pts.Count - 1], reach);
        }
        var count = closed ? pts.Count : pts.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (Dist(a, b) <= 1e-9) continue;
            segs.Add(new Seg { A = a, B = b, Kind = kind, Tag = tag });
        }
    }

    static Pt Extend(Pt from, Pt end, double by)
    {
        var len = Dist(from, end);
        if (len <= 1e-9) return end;
        return new Pt(end.X + (end.X - from.X) / len * by, end.Y + (end.Y - from.Y) / len * by);
    }

    sealed class Cycle
    {
        public List<int> Verts = new List<int>();
        public List<int> Edges = new List<int>();
        public double Area;
        public int Comp;
        public List<Cycle> Holes = new List<Cycle>();
        public Cycle Owner;
        public bool Free;
        public Pt Inside;
    }

    /// <summary>
    /// Planar arrangement of the segments: noded at every crossing and touch,
    /// dangling ends pruned, faces traced with the face on the left.
    /// </summary>
    sealed class Plan
    {
        readonly double _tol;
        readonly List<Pt> _v = new List<Pt>();
        readonly Dictionary<(long, long), List<int>> _grid = new Dictionary<(long, long), List<int>>();
        readonly List<int> _ea = new List<int>();
        readonly List<int> _eb = new List<int>();
        readonly List<Seg> _es = new List<Seg>();
        readonly List<bool> _alive = new List<bool>();
        readonly List<Cycle> _cycles = new List<Cycle>();
        public readonly List<Cycle> Faces = new List<Cycle>();
        List<int>[] _outgoing;
        int[] _slot;
        int[] _cycleOf;

        Plan(double tol)
        {
            _tol = tol;
        }

        public static Plan Build(List<Seg> segs, double tol)
        {
            var plan = new Plan(tol);
            var ts = Node(segs, tol);
            var seen = new HashSet<(int, int)>();
            for (var i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                var list = ts[i];
                list.Sort();
                var prev = plan.Vertex(s.A);
                for (var k = 1; k < list.Count; k++)
                {
                    var t = list[k];
                    var cur = t >= 1.0 ? plan.Vertex(s.B) : plan.Vertex(Lerp(s.A, s.B, t));
                    if (cur != prev && seen.Add((Math.Min(prev, cur), Math.Max(prev, cur))))
                    {
                        plan._ea.Add(prev);
                        plan._eb.Add(cur);
                        plan._es.Add(s);
                        plan._alive.Add(true);
                    }
                    prev = cur;
                }
            }
            plan.Prune();
            plan.Trace();
            return plan;
        }

        /// <summary>Split parameters per segment: ends, touches, and crossings.</summary>
        static List<double>[] Node(List<Seg> segs, double tol)
        {
            var ts = new List<double>[segs.Count];
            for (var i = 0; i < segs.Count; i++) ts[i] = new List<double> { 0.0, 1.0 };
            for (var i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                for (var j = i + 1; j < segs.Count; j++)
                {
                    var o = segs[j];
                    if (Math.Max(s.A.X, s.B.X) + tol < Math.Min(o.A.X, o.B.X)
                        || Math.Max(o.A.X, o.B.X) + tol < Math.Min(s.A.X, s.B.X)
                        || Math.Max(s.A.Y, s.B.Y) + tol < Math.Min(o.A.Y, o.B.Y)
                        || Math.Max(o.A.Y, o.B.Y) + tol < Math.Min(s.A.Y, s.B.Y))
                        continue;
                    AddTouch(ts[i], s, o.A, tol);
                    AddTouch(ts[i], s, o.B, tol);
                    AddTouch(ts[j], o, s.A, tol);
                    AddTouch(ts[j], o, s.B, tol);
                    if (TryCross(s, o, tol, out var t, out var u))
                    {
                        ts[i].Add(t);
                        ts[j].Add(u);
                    }
                }
            }
            return ts;
        }

        static void AddTouch(List<double> ts, Seg s, Pt p, double tol)
        {
            if (TryOn(s.A, s.B, p, tol, out var t)) ts.Add(t);
        }

        int Vertex(Pt p)
        {
            var kx = (long)Math.Floor(p.X / _tol);
            var ky = (long)Math.Floor(p.Y / _tol);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!_grid.TryGetValue((kx + dx, ky + dy), out var list)) continue;
                    foreach (var index in list)
                        if (Dist(_v[index], p) <= _tol) return index;
                }
            }
            _v.Add(p);
            if (!_grid.TryGetValue((kx, ky), out var cell))
                _grid[(kx, ky)] = cell = new List<int>();
            cell.Add(_v.Count - 1);
            return _v.Count - 1;
        }

        void Prune()
        {
            var degree = new int[_v.Count];
            var incident = new List<int>[_v.Count];
            for (var i = 0; i < _v.Count; i++) incident[i] = new List<int>();
            for (var e = 0; e < _ea.Count; e++)
            {
                degree[_ea[e]]++;
                degree[_eb[e]]++;
                incident[_ea[e]].Add(e);
                incident[_eb[e]].Add(e);
            }
            var queue = new Queue<int>();
            for (var i = 0; i < _v.Count; i++)
                if (degree[i] == 1) queue.Enqueue(i);
            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                if (degree[v] != 1) continue;
                foreach (var e in incident[v])
                {
                    if (!_alive[e]) continue;
                    _alive[e] = false;
                    var other = _ea[e] == v ? _eb[e] : _ea[e];
                    degree[v]--;
                    degree[other]--;
                    if (degree[other] == 1) queue.Enqueue(other);
                }
            }
        }

        int From(int h) => (h & 1) == 0 ? _ea[h >> 1] : _eb[h >> 1];
        int To(int h) => (h & 1) == 0 ? _eb[h >> 1] : _ea[h >> 1];

        void Trace()
        {
            var outgoing = _outgoing = new List<int>[_v.Count];
            for (var i = 0; i < _v.Count; i++) outgoing[i] = new List<int>();
            for (var e = 0; e < _ea.Count; e++)
            {
                if (!_alive[e]) continue;
                outgoing[_ea[e]].Add(2 * e);
                outgoing[_eb[e]].Add(2 * e + 1);
            }
            var slot = _slot = new int[2 * _ea.Count];
            _cycleOf = new int[2 * _ea.Count];
            for (var v = 0; v < _v.Count; v++)
            {
                var from = _v[v];
                outgoing[v].Sort((a, b) => Angle(from, _v[To(a)]).CompareTo(Angle(from, _v[To(b)])));
                for (var k = 0; k < outgoing[v].Count; k++) slot[outgoing[v][k]] = k;
            }

            var comp = new int[_v.Count];
            for (var i = 0; i < comp.Length; i++) comp[i] = i;
            for (var e = 0; e < _ea.Count; e++)
                if (_alive[e]) Union(comp, _ea[e], _eb[e]);

            var visited = new bool[2 * _ea.Count];
            for (var start = 0; start < visited.Length; start++)
            {
                if (visited[start] || !_alive[start >> 1]) continue;
                var cycle = new Cycle();
                var h = start;
                while (!visited[h])
                {
                    visited[h] = true;
                    _cycleOf[h] = _cycles.Count;
                    cycle.Verts.Add(From(h));
                    cycle.Edges.Add(h >> 1);
                    var at = To(h);
                    var around = outgoing[at];
                    // Next edge: clockwise from the way back, so the face stays on the left.
                    h = around[(slot[h ^ 1] - 1 + around.Count) % around.Count];
                }
                cycle.Area = Area(Points(cycle));
                cycle.Comp = Find(comp, cycle.Verts[0]);
                _cycles.Add(cycle);
                if (cycle.Area > _tol * _tol) Faces.Add(cycle);
            }

            // A component's outer boundary runs clockwise. It is a hole of the
            // smallest face of another component around it, or of the outside.
            foreach (var cycle in _cycles)
            {
                if (cycle.Area >= -_tol * _tol) continue;
                var probe = _v[cycle.Verts[0]];
                foreach (var face in Faces)
                {
                    if (face.Comp == cycle.Comp || !Contains(Points(face), probe)) continue;
                    if (cycle.Owner == null || face.Area < cycle.Owner.Area) cycle.Owner = face;
                }
                cycle.Owner?.Holes.Add(cycle);
            }
        }

        public List<Pt> Points(Cycle cycle)
        {
            var pts = new List<Pt>(cycle.Verts.Count);
            foreach (var v in cycle.Verts) pts.Add(_v[v]);
            return pts;
        }

        /// <summary>
        /// Boundary loops of the wall region after Classify: the half-edges
        /// with wall on their left and free space on their right, chained so
        /// the wall stays on the left. Edges between two wall faces are inside
        /// the region and are passed over.
        /// </summary>
        public List<List<Pt>> Boundary()
        {
            var loops = new List<List<Pt>>();
            var visited = new bool[2 * _ea.Count];
            for (var start = 0; start < visited.Length; start++)
            {
                if (visited[start] || !OnBoundary(start)) continue;
                var pts = new List<Pt>();
                var h = start;
                while (!visited[h])
                {
                    visited[h] = true;
                    pts.Add(_v[From(h)]);
                    var around = _outgoing[To(h)];
                    // Clockwise from the way back, as a face is traced, to the first edge that is boundary too.
                    var back = _slot[h ^ 1];
                    var next = -1;
                    for (var k = 1; k <= around.Count && next < 0; k++)
                    {
                        var candidate = around[((back - k) % around.Count + around.Count) % around.Count];
                        if (OnBoundary(candidate)) next = candidate;
                    }
                    if (next < 0) break;
                    h = next;
                }
                var loop = Simplify(pts, _tol);
                if (loop.Count >= 3 && Math.Abs(Area(loop)) > _tol * _tol) loops.Add(loop);
            }
            return loops;
        }

        bool OnBoundary(int h)
        {
            return _alive[h >> 1] && IsWall(h) && !IsWall(h ^ 1);
        }

        /// <summary>The face on the half-edge's left is wall. A sliver too thin to classify counts as wall.</summary>
        bool IsWall(int h)
        {
            var cycle = _cycles[_cycleOf[h]];
            if (Math.Abs(cycle.Area) <= _tol * _tol) return true;
            return !cycle.Free;
        }

        /// <summary>A face is free (not wall) when a point inside it is outside every wall.</summary>
        public void Classify(Scene scene)
        {
            foreach (var face in Faces)
            {
                var rings = new List<List<Pt>> { Points(face) };
                foreach (var hole in face.Holes) rings.Add(Points(hole));
                if (!TryInside(rings, out var at))
                    continue;
                face.Inside = at;
                face.Free = !InWall(scene, at);
            }
            foreach (var cycle in _cycles)
            {
                if (cycle.Area >= -_tol * _tol) continue;
                cycle.Free = cycle.Owner == null || cycle.Owner.Free;
            }
        }

        static bool InWall(Scene scene, Pt p)
        {
            foreach (var wall in scene.Walls)
                if (InRings(wall, p)) return true;
            return false;
        }

        public bool HasEdge(Kind kind, int tag)
        {
            for (var e = 0; e < _es.Count; e++)
                if (_alive[e] && _es[e].Kind == kind && _es[e].Tag == tag) return true;
            return false;
        }

        /// <summary>Width of an undoored gap on the face's boundary, or 0.</summary>
        public double GapOn(Cycle face)
        {
            foreach (var e in face.Edges)
                if (_es[e].Kind == Kind.Gap) return _es[e].Gap;
            foreach (var hole in face.Holes)
                foreach (var e in hole.Edges)
                    if (_es[e].Kind == Kind.Gap) return _es[e].Gap;
            return 0;
        }

        sealed class End
        {
            public int P;
            public int Q;
        }

        /// <summary>
        /// Wall ends are short free-side edges with both corners reflex. Two
        /// ends facing each other across a gap get two parallel bridges, jamb
        /// corner to jamb corner: Door when a door box overlaps the gap, else Gap.
        /// </summary>
        public List<Seg> Bridges(Scene scene)
        {
            var ends = new List<End>();
            foreach (var cycle in _cycles)
            {
                if (!cycle.Free || cycle.Verts.Count < 4) continue;
                var n = cycle.Verts.Count;
                for (var i = 0; i < n; i++)
                {
                    var e = cycle.Edges[i];
                    if (_es[e].Kind != Kind.Wall) continue;
                    var p = cycle.Verts[i];
                    var q = cycle.Verts[(i + 1) % n];
                    if (Dist(_v[p], _v[q]) > MaxEndMm) continue;
                    if (!Reflex(cycle, i) || !Reflex(cycle, (i + 1) % n)) continue;
                    ends.Add(new End { P = p, Q = q });
                }
            }

            var pairs = new List<(double len, int a, int b)>();
            for (var a = 0; a < ends.Count; a++)
                for (var b = 0; b < ends.Count; b++)
                    if (a != b && Facing(ends[a], ends[b], out var len))
                        pairs.Add((len, a, b));
            pairs.Sort((x, y) => x.len.CompareTo(y.len));

            var used = new bool[ends.Count];
            var bridges = new List<Seg>();
            foreach (var (len, a, b) in pairs)
            {
                if (used[a] || used[b]) continue;
                used[a] = used[b] = true;
                var e = ends[a];
                var f = ends[b];
                var door = DoorIn(scene, _v[e.P], _v[e.Q], _v[f.P], _v[f.Q]);
                var kind = door ? Kind.Door : Kind.Gap;
                bridges.Add(new Seg { A = _v[e.P], B = _v[f.Q], Kind = kind, Gap = len });
                bridges.Add(new Seg { A = _v[e.Q], B = _v[f.P], Kind = kind, Gap = len });
            }
            return bridges;
        }

        bool Reflex(Cycle cycle, int i)
        {
            var n = cycle.Verts.Count;
            var a = _v[cycle.Verts[(i - 1 + n) % n]];
            var b = _v[cycle.Verts[i]];
            var c = _v[cycle.Verts[(i + 1) % n]];
            return Cross(a, b, c) < -_tol * Math.Max(Dist(a, c), _tol);
        }

        /// <summary>
        /// End e runs P to Q along its cycle; f faces it when bridges e.P to f.Q
        /// and e.Q to f.P are about equal, parallel, square to the end, and
        /// cross nothing.
        /// </summary>
        bool Facing(End e, End f, out double len)
        {
            len = 0;
            var p1 = _v[e.P];
            var p2 = _v[e.Q];
            var q1 = _v[f.P];
            var q2 = _v[f.Q];
            var l1 = Dist(p1, q2);
            var l2 = Dist(p2, q1);
            if (l1 <= _tol || l2 <= _tol || l1 > MaxGapMm || l2 > MaxGapMm) return false;
            if (Math.Abs(l1 - l2) > Math.Max(50.0, 0.05 * l1)) return false;
            var bx = (q2.X - p1.X) / l1;
            var by = (q2.Y - p1.Y) / l1;
            var cx = (q1.X - p2.X) / l2;
            var cy = (q1.Y - p2.Y) / l2;
            if (Dot(bx, by, cx, cy) < 0.99) return false;
            var jamb = Dist(p1, p2);
            if (Math.Abs(Dot(bx, by, (p2.X - p1.X) / jamb, (p2.Y - p1.Y) / jamb)) > 0.2) return false;
            if (!Clear(e.P, f.Q) || !Clear(e.Q, f.P)) return false;
            len = (l1 + l2) / 2.0;
            return true;
        }

        /// <summary>A bridge between two vertices that touches no edge on the way.</summary>
        bool Clear(int a, int b)
        {
            var pa = _v[a];
            var pb = _v[b];
            for (var e = 0; e < _ea.Count; e++)
            {
                if (!_alive[e]) continue;
                var u = _ea[e];
                var w = _eb[e];
                if ((u == a && w == b) || (u == b && w == a)) return false;
                var pu = _v[u];
                var pw = _v[w];
                if (u != a && u != b && TryOn(pa, pb, pu, _tol, out _)) return false;
                if (w != a && w != b && TryOn(pa, pb, pw, _tol, out _)) return false;
                if (u == a || u == b || w == a || w == b) continue;
                var s = new Seg { A = pa, B = pb };
                var o = new Seg { A = pu, B = pw };
                if (TryCross(s, o, _tol, out _, out _)) return false;
            }
            return true;
        }

        static bool DoorIn(Scene scene, Pt a, Pt b, Pt c, Pt d)
        {
            var minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
            var maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            var minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
            var maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
            foreach (var box in scene.Doors)
            {
                if (box.MaxX > minX && box.MinX < maxX && box.MaxY > minY && box.MinY < maxY)
                    return true;
            }
            return false;
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        static void Union(int[] parent, int a, int b)
        {
            parent[Find(parent, a)] = Find(parent, b);
        }
    }

    /// <summary>Parameter of p on segment a-b, strictly between its ends, within tol.</summary>
    static bool TryOn(Pt a, Pt b, Pt p, double tol, out double t)
    {
        t = 0;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 <= 1e-12) return false;
        t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        var len = Math.Sqrt(len2);
        if (t * len <= tol || (1 - t) * len <= tol) return false;
        return Dist(p, Lerp(a, b, t)) <= tol;
    }

    /// <summary>Proper crossing, away from both segments' ends.</summary>
    static bool TryCross(Seg s, Seg o, double tol, out double t, out double u)
    {
        t = u = 0;
        var dx1 = s.B.X - s.A.X;
        var dy1 = s.B.Y - s.A.Y;
        var dx2 = o.B.X - o.A.X;
        var dy2 = o.B.Y - o.A.Y;
        var den = dx1 * dy2 - dy1 * dx2;
        var l1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
        var l2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
        if (Math.Abs(den) <= 1e-9 * l1 * l2) return false;
        var ex = o.A.X - s.A.X;
        var ey = o.A.Y - s.A.Y;
        t = (ex * dy2 - ey * dx2) / den;
        u = (ex * dy1 - ey * dx1) / den;
        return t * l1 > tol && (1 - t) * l1 > tol && u * l2 > tol && (1 - u) * l2 > tol;
    }

    static Pt Lerp(Pt a, Pt b, double t) => new Pt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    static Pt Mid(Pt a, Pt b) => Lerp(a, b, 0.5);
    static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static double Cross(Pt a, Pt b, Pt c) => (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
    static double Dot(double ax, double ay, double bx, double by) => ax * bx + ay * by;
    static double Angle(Pt from, Pt to) => Math.Atan2(to.Y - from.Y, to.X - from.X);
}
