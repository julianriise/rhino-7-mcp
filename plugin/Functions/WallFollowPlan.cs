using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
        return Sentence(floor, roof, rooms, daylight, false);
    }

    /// <summary>The same sentence in Norwegian when <paramref name="nb"/> is set.</summary>
    public static string Sentence(bool floor, bool roof, int rooms, bool daylight, bool nb)
    {
        var parts = new List<string>();
        if (floor) parts.Add(nb ? "gulv" : "floor");
        if (roof) parts.Add(nb ? "tak" : "roof");
        if (rooms == 1) parts.Add(nb ? "1 rom" : "1 room");
        else if (rooms > 1) parts.Add(rooms.ToString(CultureInfo.InvariantCulture) + (nb ? " rom" : " rooms"));

        string text;
        if (parts.Count == 0)
            text = nb ? "Gulv, tak og rom er uendret." : "Floor, roof and rooms are unchanged.";
        else if (parts.Count == 1)
            text = Capital(parts[0]) + (nb ? " oppdatert." : " updated.");
        else if (parts.Count == 2)
            text = Capital(parts[0]) + (nb ? " og " : " and ") + parts[1] + (nb ? " oppdatert." : " updated.");
        else
            text = Capital(parts[0]) + ", " + parts[1] + (nb ? " og " : " and ") + parts[2] + (nb ? " oppdatert." : " updated.");
        if (daylight)
            text += nb ? " Dagslyset er utdatert. Kjør det igjen." : " Daylight is out of date, run it again.";
        return text;
    }

    /// <summary>A room outline the receipt can name a wall by.</summary>
    public sealed class NamedRoom
    {
        public string Name;
        public List<RoomDetect.Pt> Ring;
    }

    /// <summary>
    /// The walls that followed, as one phrase. Outer walls are named by side.
    /// Everything else is a count: "the west and east walls and ten inner walls".
    /// A coordinate name is never printed. Empty when there are none.
    /// </summary>
    public static string Walls(IList<string> names)
    {
        return Walls(names, false);
    }

    /// <summary>The same phrase in Norwegian when <paramref name="nb"/> is set.</summary>
    public static string Walls(IList<string> names, bool nb)
    {
        var sides = new List<string>();
        var inner = 0;
        foreach (var name in names ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (IsSide(name, out var side)) { if (!sides.Contains(side)) sides.Add(side); }
            else inner++;
        }
        var parts = new List<string>();
        if (sides.Count == 1) parts.Add(Side(sides[0], nb));
        else if (sides.Count > 1) parts.Add(SideList(sides, nb));
        if (inner > 0) parts.Add(InnerCount(inner, nb));
        return And(parts, nb);
    }

    /// <summary>"the east and west walls followed", or the Norwegian sentence.</summary>
    public static string FollowedClause(IList<string> names, bool nb)
    {
        var walls = Walls(names, nb);
        if (walls.Length == 0) return "";
        return walls + (nb ? " fulgte" : " followed");
    }

    /// <summary>True when the graph named this run for a compass side.</summary>
    public static bool IsSide(string name, out string side)
    {
        side = null;
        foreach (var candidate in new[] { "north", "south", "east", "west" })
        {
            if (name != "the " + candidate + " wall") continue;
            side = candidate;
            return true;
        }
        return false;
    }

    /// <summary>"the north wall", or "nordveggen".</summary>
    public static string Side(string side, bool nb)
    {
        if (!nb) return "the " + side + " wall";
        switch (side)
        {
            case "north": return "nordveggen";
            case "south": return "sørveggen";
            case "east": return "østveggen";
            case "west": return "vestveggen";
            default: return side ?? "";
        }
    }

    /// <summary>
    /// An inner wall for the review card: the rooms it bounds, else "inner wall 3".
    /// Two or more rooms read "wall between Kitchen and Bedroom".
    /// </summary>
    public static string InnerName(IList<string> rooms, int index, bool nb)
    {
        var named = new List<string>();
        foreach (var room in rooms ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(room) && !named.Contains(room.Trim())) named.Add(room.Trim());
        if (named.Count >= 2)
            return (nb ? "vegg mellom " : "wall between ") + And(named, nb);
        if (named.Count == 1)
            return nb ? "vegg mot " + named[0] : "wall of " + named[0];
        var n = index > 0 ? index : 1;
        return (nb ? "innervegg " : "inner wall ") + n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>1-based place of this run among the inner runs, in graph order.</summary>
    public static int InnerOrdinal(IList<string> names, int index)
    {
        var n = 0;
        for (var i = 0; i < (names?.Count ?? 0); i++)
        {
            if (IsSide(names[i], out _)) continue;
            n++;
            if (i == index) return n;
        }
        return 1;
    }

    /// <summary>
    /// The rooms just outside the two faces, western (then southern) first,
    /// so a partition reads "Kitchen and Bedroom" from left to right.
    /// </summary>
    public static List<string> Beside(WallEdit.Run run, IList<NamedRoom> rooms)
    {
        var found = new List<NamedHit>();
        if (run == null || rooms == null) return new List<string>();
        var mid = WallJoins.Middle(run);
        var gap = run.Thickness / 2.0 + 30.0;
        var probes = new[]
        {
            new RoomDetect.Pt(mid.X + run.Normal.X * gap, mid.Y + run.Normal.Y * gap),
            new RoomDetect.Pt(mid.X - run.Normal.X * gap, mid.Y - run.Normal.Y * gap)
        };
        foreach (var probe in probes)
        {
            foreach (var room in rooms)
            {
                if (room?.Ring == null || room.Ring.Count < 3 || string.IsNullOrWhiteSpace(room.Name)) continue;
                if (!RoomDetect.Contains(room.Ring, probe)) continue;
                var name = room.Name.Trim();
                if (found.Exists(hit => hit.Name == name)) break;
                found.Add(new NamedHit { Name = name, X = probe.X, Y = probe.Y });
                break;
            }
        }
        found.Sort((a, b) =>
        {
            var byX = a.X.CompareTo(b.X);
            return byX != 0 ? byX : a.Y.CompareTo(b.Y);
        });
        var names = new List<string>();
        foreach (var hit in found) names.Add(hit.Name);
        return names;
    }

    sealed class NamedHit
    {
        public string Name;
        public double X, Y;
    }

    static string SideList(IList<string> sides, bool nb)
    {
        if (!nb) return "the " + And(sides, false) + " walls";
        if (sides.Count == 1) return Side(sides[0], true);
        var stems = new List<string>();
        foreach (var side in sides) stems.Add(Stem(side));
        var last = stems[stems.Count - 1] + "veggen";
        if (stems.Count == 2) return stems[0] + "- og " + last;
        return string.Join("-, ", stems.Take(stems.Count - 1)) + "- og " + last;
    }

    static string Stem(string side)
    {
        switch (side)
        {
            case "north": return "nord";
            case "south": return "sør";
            case "east": return "øst";
            case "west": return "vest";
            default: return side ?? "";
        }
    }

    static string InnerCount(int n, bool nb)
    {
        var word = n >= 1 && n <= 10 ? (nb ? NbNumber : EnNumber)[n] : n.ToString(CultureInfo.InvariantCulture);
        if (nb) return word + (n == 1 ? " innervegg" : " innervegger");
        return word + (n == 1 ? " inner wall" : " inner walls");
    }

    static readonly string[] EnNumber = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };
    static readonly string[] NbNumber = { "null", "én", "to", "tre", "fire", "fem", "seks", "sju", "åtte", "ni", "ti" };

    static string And(IList<string> items, bool nb)
    {
        var and = nb ? " og " : " and ";
        if (items.Count == 0) return "";
        if (items.Count == 1) return items[0];
        return string.Join(", ", items.Take(items.Count - 1)) + and + items[items.Count - 1];
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
