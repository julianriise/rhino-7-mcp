using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Area statistics for the Analyser. Each room brings the net area the plan
/// tag and the Romliste already store (mm²). This sums those figures per
/// floor, per use and for the model. It does not measure a room outline again.
/// The use is a small name map; a caller may set one.
/// BRA and BTA, when asked, come from the outer face of the outer walls
/// (the wall record's outer loop, the same ring the floor slab is extruded
/// from) inset by that floor's wall thickness. Inner walls stay inside BRA.
/// A closed courtyard is a hole in that ring, or the hole of a wall ring
/// inside it, and it is not floor. The hole that is the inner face of the
/// outer walls is the rooms, and it stays. A figure that cannot be derived
/// is left out, with one reason. Pure, no Rhino document.
/// </summary>
public static class AreaStats
{
    /// <summary>How many uses the summary names before "+N more". The rooms themselves are on the card.</summary>
    public const int SummaryUses = 4;

    public const string OtherUse = "Annet";

    /// <summary>Longest key first is not required: a word takes the longest key it matches. Keys are folded: ø is o.</summary>
    static readonly (string Key, string Use)[] NameUses =
    {
        ("konferanse", "Møterom"),
        ("kontorplass", "Kontor"),
        ("conference", "Møterom"),
        ("bottekott", "Bod"),
        ("bathroom", "Bad"),
        ("bedroom", "Soverom"),
        ("soverom", "Soverom"),
        ("moterom", "Møterom"),
        ("meeting", "Møterom"),
        ("kitchen", "Kjøkken"),
        ("kjokken", "Kjøkken"),
        ("archive", "Bod"),
        ("kontor", "Kontor"),
        ("office", "Kontor"),
        ("living", "Stue"),
        ("storage", "Bod"),
        ("toalett", "Bad"),
        ("vatrom", "Bad"),
        ("arkiv", "Bod"),
        ("entre", "Gang"),
        ("stue", "Stue"),
        ("hall", "Gang"),
        ("kott", "Bod"),
        ("gang", "Gang"),
        ("bath", "Bad"),
        ("bod", "Bod"),
        ("bad", "Bad"),
        ("wet", "Bad"),
        ("wc", "Bad")
    };

    public sealed class Room
    {
        public string Id;
        public string Name;
        public string Level;
        /// <summary>Net area in mm², the same figure as the plan tag.</summary>
        public double AreaMm2;
        /// <summary>The room outline's perimeter in mm, 0 when not known.</summary>
        public double PerimeterMm;
        /// <summary>When set, the group. Otherwise a stored room type, else the name map.</summary>
        public string Use;
        /// <summary>forsk:room_type. Missing reads as unassigned and does not change the name map.</summary>
        public string RoomType;
    }

    public sealed class RoomLine
    {
        public string Id;
        public string Name;
        public string Level;
        public string Use;
        /// <summary>The stored key, or unassigned when the file has none.</summary>
        public string RoomType;
        public double AreaMm2;
        public double PerimeterMm;
    }

    /// <summary>One floor (forsk:level) or one use. AreaMm2 is the sum of the rooms in it.</summary>
    public sealed class Group
    {
        public string Key;
        public int Count;
        public double AreaMm2;
    }

    public sealed class Result
    {
        /// <summary>Largest net area first.</summary>
        public List<RoomLine> Rooms = new List<RoomLine>();
        /// <summary>By floor, level order. An empty level is stored as "0", the ground floor.</summary>
        public List<Group> Floors = new List<Group>();
        /// <summary>By use, largest area first.</summary>
        public List<Group> Uses = new List<Group>();
        /// <summary>By forsk:room_type. Largest first, unassigned last. Key is the stored type.</summary>
        public List<Group> Types = new List<Group>();
        /// <summary>Set by ApplyGross. Null until then, so a net-only summary stays as it was.</summary>
        public List<FloorGross> Gross;
        public double NetMm2;
        public string Summary;
    }

    /// <summary>BRA and BTA for one floor, or a note and neither figure.</summary>
    public sealed class FloorGross
    {
        public string Level;
        public double? BraMm2;
        public double? BtaMm2;
        /// <summary>Why both were left out. Null when both are set.</summary>
        public string Note;
    }

    /// <summary>One wall record: forsk:path rings (outer, then holes) and forsk:thickness.</summary>
    public sealed class Wall
    {
        public string Level;
        public double? ThicknessMm;
        public List<List<Pt>> Rings;
    }

    /// <summary>
    /// The use of a room name. Norwegian and English both map, including an
    /// office, a meeting room, a wet room, a closet and an archive. "Rom" and
    /// "Room", and any name the map does not know, are Annet.
    /// </summary>
    public static string UseOf(string name)
    {
        string found = null;
        foreach (var word in Words(name))
        {
            var use = MatchWord(word);
            if (use == null) continue;
            found = use;
            break;
        }
        return found ?? OtherUse;
    }

    /// <summary>
    /// The Areas table's label for a use key. The key stays Annet so the rows
    /// still group; English prints Other. Every other use is printed as stored.
    /// </summary>
    public static string UseLabel(string key, bool norwegian = false)
    {
        if (!norwegian && string.Equals(key, OtherUse, StringComparison.Ordinal)) return "Other";
        return key ?? "";
    }

    public static Result Compute(IList<Room> rooms, bool norwegian = false)
    {
        var result = new Result();
        if (rooms != null)
        {
            foreach (var room in rooms)
            {
                if (room == null) continue;
                var type = RoomTypes.Read(room.RoomType);
                var line = new RoomLine
                {
                    Id = room.Id ?? "",
                    Name = string.IsNullOrWhiteSpace(room.Name) ? RoomDetect.UnnamedRoom(norwegian) : room.Name.Trim(),
                    Level = string.IsNullOrWhiteSpace(room.Level) ? "0" : room.Level.Trim(),
                    Use = string.IsNullOrWhiteSpace(room.Use)
                        ? (type == RoomTypes.Unassigned ? UseOf(room.Name) : RoomTypes.English(type))
                        : room.Use.Trim(),
                    RoomType = type,
                    AreaMm2 = room.AreaMm2,
                    PerimeterMm = room.PerimeterMm
                };
                result.Rooms.Add(line);
                result.NetMm2 += line.AreaMm2;
            }
        }
        if (result.Rooms.Count == 0)
        {
            result.Summary = "No rooms. Make rooms finds them from the walls.";
            return result;
        }
        result.Rooms.Sort(BySize);
        result.Floors = Groups(result.Rooms, line => line.Level, ByLevel);
        result.Uses = Groups(result.Rooms, line => line.Use, ByArea);
        result.Types = TypeGroups(result.Rooms);
        result.Summary = Summarize(result);
        return result;
    }

    static List<Group> Groups(List<RoomLine> rooms, Func<RoomLine, string> key, Comparison<Group> order)
    {
        var groups = new List<Group>();
        foreach (var room in rooms)
        {
            var name = key(room);
            var group = groups.Find(g => g.Key == name);
            if (group == null)
            {
                group = new Group { Key = name };
                groups.Add(group);
            }
            group.Count++;
            group.AreaMm2 += room.AreaMm2;
        }
        groups.Sort(order);
        return groups;
    }

    /// <summary>Room types by area, with unassigned last even when it is the largest.</summary>
    static List<Group> TypeGroups(List<RoomLine> rooms)
    {
        var groups = Groups(rooms, line => RoomTypes.Read(line.RoomType), ByArea);
        var open = groups.Find(g => g.Key == RoomTypes.Unassigned);
        if (open == null || groups.Count < 2) return groups;
        groups.Remove(open);
        groups.Add(open);
        return groups;
    }

    /// <summary>
    /// The card: each floor's BRA and BTA, then each room type in English, unassigned last, then the total.
    /// A floor without both figures shows its net. Every figure is an estimate. English, one decimal, a point.
    /// </summary>
    public static List<string> Breakdown(Result result)
    {
        var rows = new List<string>();
        if (result == null || result.Rooms.Count == 0) return rows;
        foreach (var floor in result.Floors)
        {
            var name = FloorName(floor.Key, false);
            FloorGross gross = null;
            if (result.Gross != null)
                foreach (var item in result.Gross)
                    if (item.Level == floor.Key) gross = item;
            if (gross?.BraMm2 != null && gross.BtaMm2 != null)
                rows.Add(name + " · BRA " + OpeningTypes.AreaText(gross.BraMm2.Value, false)
                    + " · BTA " + OpeningTypes.AreaText(gross.BtaMm2.Value, false));
            else
                rows.Add(name + " · net " + OpeningTypes.AreaText(floor.AreaMm2, false));
        }
        foreach (var type in result.Types)
            rows.Add(RoomTypes.English(type.Key) + " · " + OpeningTypes.AreaText(type.AreaMm2, false));
        rows.Add("Total · " + OpeningTypes.AreaText(result.NetMm2, false));
        return rows;
    }

    /// <summary>
    /// The net once. One floor does not repeat it. BRA and BTA join when the
    /// walls give them; a thickness note stays off this line. Uses are the
    /// largest few. The rooms are not listed here.
    /// </summary>
    static string Summarize(Result result)
    {
        var text = "Net " + OpeningTypes.AreaText(result.NetMm2) + ", estimate.";
        if (result.Floors.Count == 1)
        {
            var figures = GrossFigures(result, result.Floors[0].Key);
            if (figures != null) text += " " + figures + ".";
        }
        else if (result.Floors.Count > 1)
        {
            var floors = new string[result.Floors.Count];
            for (var i = 0; i < floors.Length; i++)
            {
                var floor = result.Floors[i];
                var figures = GrossFigures(result, floor.Key);
                floors[i] = FloorName(floor.Key) + ": " + OpeningTypes.AreaText(floor.AreaMm2)
                    + (figures == null ? "" : ", " + figures);
            }
            text += " " + string.Join("; ", floors) + ".";
        }
        return text + " " + UsesClause(result);
    }

    static string UsesClause(Result result)
    {
        var shown = Math.Min(SummaryUses, result.Uses.Count);
        var uses = new string[shown];
        for (var i = 0; i < shown; i++)
            uses[i] = result.Uses[i].Key + " " + OpeningTypes.AreaText(result.Uses[i].AreaMm2);
        var text = "By use: " + string.Join(", ", uses);
        var more = result.Uses.Count - shown;
        if (more > 0) text += ", +" + more.ToString(CultureInfo.InvariantCulture) + " more";
        return text + ".";
    }

    /// <summary>BRA and BTA for the floor, or null when either is missing. The note is not printed.</summary>
    static string GrossFigures(Result result, string level)
    {
        if (result.Gross == null) return null;
        FloorGross gross = null;
        foreach (var row in result.Gross)
            if (row.Level == level) gross = row;
        if (gross?.BraMm2 == null || gross.BtaMm2 == null) return null;
        return "BRA " + OpeningTypes.AreaText(gross.BraMm2.Value) + ", BTA " + OpeningTypes.AreaText(gross.BtaMm2.Value);
    }

    /// <summary>
    /// BTA is the area of the outer face. BRA is that ring inset by the wall
    /// thickness, so the inner walls stay inside it. False leaves both unset
    /// and says why.
    /// </summary>
    public static bool TryEnvelope(IList<Pt> outer, double thicknessMm, out double btaMm2, out double braMm2, out string reason)
    {
        return TryEnvelope(outer, null, thicknessMm, out btaMm2, out braMm2, out reason);
    }

    /// <summary>
    /// The same, minus enclosed open voids. Each void is a courtyard: BTA loses
    /// its area, and BRA loses it outset by the wall thickness. The caller
    /// leaves out a hole that is only the inner face of the outer walls.
    /// </summary>
    public static bool TryEnvelope(IList<Pt> outer, IEnumerable<IList<Pt>> voids, double thicknessMm, out double btaMm2, out double braMm2, out string reason)
    {
        btaMm2 = 0;
        braMm2 = 0;
        reason = null;
        var ring = Clean(outer);
        if (ring == null)
        {
            reason = "the wall outline is missing";
            return false;
        }
        if (!(thicknessMm > 0) || double.IsNaN(thicknessMm))
        {
            reason = "wall thickness is missing";
            return false;
        }
        var area = RoomDetect.Area(ring);
        if (Math.Abs(area) < 1)
        {
            reason = "the wall outline has no area";
            return false;
        }
        if (area < 0) ring.Reverse();
        btaMm2 = Math.Abs(RoomDetect.Area(ring));
        if (!TryInset(ring, thicknessMm, out var inset, out reason))
        {
            btaMm2 = 0;
            return false;
        }
        braMm2 = Math.Abs(RoomDetect.Area(inset));
        if (!SubtractVoids(ring, thicknessMm, voids, ref btaMm2, ref braMm2, out reason))
        {
            btaMm2 = 0;
            braMm2 = 0;
            return false;
        }
        var collapsed = braMm2 <= 1;
        var grew = braMm2 >= btaMm2 - 1;
        if (collapsed || grew)
        {
            btaMm2 = 0;
            braMm2 = 0;
            reason = collapsed ? "the walls are thicker than the outline" : "the wall outline does not inset cleanly";
            return false;
        }
        return true;
    }

    /// <summary>Courtyard voids come off both figures. A missing or outside void fails the whole envelope.</summary>
    static bool SubtractVoids(List<Pt> outer, double thickness, IEnumerable<IList<Pt>> voids, ref double bta, ref double bra, out string reason)
    {
        reason = null;
        if (voids == null) return true;
        foreach (var raw in voids)
        {
            var hole = Clean(raw);
            if (hole == null)
            {
                reason = "the courtyard outline is missing";
                return false;
            }
            if (RoomDetect.Area(hole) < 0) hole.Reverse();
            foreach (var p in hole)
            {
                if (RoomDetect.Contains(outer, p)) continue;
                reason = "the courtyard is not inside the outline";
                return false;
            }
            var open = Math.Abs(RoomDetect.Area(hole));
            if (open < 1)
            {
                reason = "the courtyard outline has no area";
                return false;
            }
            // Clockwise, so the inset walks outward and the void grows by the wall thickness.
            hole.Reverse();
            if (!TryInset(hole, thickness, out var expanded, out reason)) return false;
            var grown = Math.Abs(RoomDetect.Area(expanded));
            if (grown <= open + 1)
            {
                reason = "the wall outline does not inset cleanly";
                return false;
            }
            bta -= open;
            bra -= grown;
        }
        if (bta <= 1)
        {
            reason = "the courtyard is larger than the outline";
            return false;
        }
        return true;
    }

    /// <summary>
    /// BRA and BTA for each floor that has rooms, from that floor's wall
    /// records. One thickness for the floor. Several outlines are summed.
    /// A floor that cannot be derived keeps the note and neither figure.
    /// </summary>
    public static void ApplyGross(Result result, IList<Wall> walls, double tol)
    {
        if (result == null) return;
        result.Gross = new List<FloorGross>();
        if (result.Rooms.Count == 0) return;
        var byLevel = new Dictionary<string, List<Wall>>(StringComparer.Ordinal);
        if (walls != null)
        {
            foreach (var wall in walls)
            {
                if (wall?.Rings == null || wall.Rings.Count == 0 || wall.Rings[0] == null || wall.Rings[0].Count < 3) continue;
                var level = string.IsNullOrWhiteSpace(wall.Level) ? "0" : wall.Level.Trim();
                if (!byLevel.TryGetValue(level, out var list))
                {
                    list = new List<Wall>();
                    byLevel[level] = list;
                }
                list.Add(wall);
            }
        }
        foreach (var floor in result.Floors)
        {
            var gross = new FloorGross { Level = floor.Key };
            result.Gross.Add(gross);
            if (!byLevel.TryGetValue(floor.Key, out var levelWalls))
            {
                gross.Note = "no wall outline on this floor";
                continue;
            }
            if (!OneThickness(levelWalls, tol, out var thickness, out var why))
            {
                gross.Note = why;
                continue;
            }
            var records = new List<List<List<Pt>>>();
            foreach (var wall in levelWalls) records.Add(wall.Rings);
            var shapes = Shapes(records, tol <= 0 ? 1 : tol);
            if (shapes.Count == 0)
            {
                gross.Note = "no wall outline on this floor";
                continue;
            }
            double bra = 0, bta = 0;
            string reason = null;
            foreach (var shape in shapes)
            {
                if (EnclosedBy(shape, shapes)) continue;
                if (!TryEnvelope(shape[0], Courtyards(shape, shapes, thickness), thickness, out var oneBta, out var oneBra, out reason)) break;
                bra += oneBra;
                bta += oneBta;
            }
            if (reason != null)
            {
                gross.Note = reason;
                continue;
            }
            gross.BraMm2 = bra;
            gross.BtaMm2 = bta;
        }
        result.Summary = Summarize(result);
    }

    /// <summary>Compact JSON for the chat: printed areas, no ids and no coordinates. message is the summary.</summary>
    public static JObject ToJson(Result result)
    {
        var floors = new JArray();
        var uses = new JArray();
        var rooms = new JArray();
        var omitted = new JArray();
        if (result != null)
        {
            foreach (var floor in result.Floors)
            {
                var row = new JObject
                {
                    ["level"] = FloorName(floor.Key),
                    ["net"] = OpeningTypes.AreaText(floor.AreaMm2),
                    ["rooms"] = floor.Count
                };
                FloorGross gross = null;
                if (result.Gross != null)
                    foreach (var item in result.Gross)
                        if (item.Level == floor.Key) gross = item;
                if (gross != null)
                {
                    if (gross.BraMm2.HasValue) row["bra"] = OpeningTypes.AreaText(gross.BraMm2.Value);
                    if (gross.BtaMm2.HasValue) row["bta"] = OpeningTypes.AreaText(gross.BtaMm2.Value);
                    if (!string.IsNullOrEmpty(gross.Note))
                    {
                        row["note"] = gross.Note;
                        omitted.Add(FloorName(floor.Key) + ": " + gross.Note);
                    }
                }
                floors.Add(row);
            }
            foreach (var use in result.Uses)
                uses.Add(new JObject { ["use"] = use.Key, ["area"] = OpeningTypes.AreaText(use.AreaMm2), ["rooms"] = use.Count });
            foreach (var room in result.Rooms)
                rooms.Add(new JObject
                {
                    ["name"] = room.Name,
                    ["level"] = FloorName(room.Level),
                    ["use"] = room.Use,
                    ["room_type"] = room.RoomType ?? RoomTypes.Unassigned,
                    ["area"] = OpeningTypes.AreaText(room.AreaMm2)
                });
        }
        return new JObject
        {
            ["summary"] = result?.Summary ?? "",
            ["message"] = result?.Summary ?? "",
            ["floors"] = floors,
            ["uses"] = uses,
            ["rooms"] = rooms,
            ["breakdown"] = new JArray(Breakdown(result)),
            ["more"] = 0,
            ["omitted"] = omitted
        };
    }

    /// <summary>Each cluster as outer loop then holes. A union that does not read as one piece keeps the records.</summary>
    static List<List<List<Pt>>> Shapes(List<List<List<Pt>>> records, double tol)
    {
        var shapes = new List<List<List<Pt>>>();
        foreach (var cluster in WallJoins.Clusters(records, tol))
        {
            var shape = WallJoins.Shape(records, cluster, tol);
            if (shape != null && shape.Count > 0 && shape[0] != null && shape[0].Count >= 3)
            {
                shapes.Add(shape);
                continue;
            }
            foreach (var i in cluster)
                if (records[i] != null && records[i].Count > 0 && records[i][0] != null && records[i][0].Count >= 3)
                    shapes.Add(records[i]);
        }
        return shapes;
    }

    /// <summary>A wall ring inside a larger outline. Its holes are the open court, not a second building.</summary>
    static bool EnclosedBy(List<List<Pt>> shape, List<List<List<Pt>>> shapes)
    {
        var ring = shape[0];
        foreach (var other in shapes)
        {
            if (other == shape) continue;
            var outer = other[0];
            if (Math.Abs(RoomDetect.Area(outer)) > Math.Abs(RoomDetect.Area(ring)) && RoomDetect.Contains(outer, ring[0]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Open voids of this outline. The hole that matches the inner face is the
    /// rooms. A smaller hole, and the hole of a ring inside this outline, is a courtyard.
    /// </summary>
    static List<List<Pt>> Courtyards(List<List<Pt>> shape, List<List<List<Pt>>> shapes, double thickness)
    {
        var voids = new List<List<Pt>>();
        for (var i = 1; i < shape.Count; i++)
            if (shape[i] != null && shape[i].Count >= 3 && !IsInnerFace(shape[0], shape[i], thickness))
                voids.Add(shape[i]);
        foreach (var other in shapes)
        {
            if (other == shape || !EnclosedBy(other, shapes)) continue;
            if (!RoomDetect.Contains(shape[0], other[0][0])) continue;
            for (var i = 1; i < other.Count; i++)
                if (other[i] != null && other[i].Count >= 3)
                    voids.Add(other[i]);
        }
        return voids;
    }

    /// <summary>The inner face of the outer walls: the hole a uniform band leaves, within one wall-band of area.</summary>
    static bool IsInnerFace(List<Pt> outer, List<Pt> hole, double thickness)
    {
        if (!TryInset(outer, thickness, out var inset, out _)) return false;
        var slop = Math.Max(RoomDetect.Perimeter(outer) * thickness, 1);
        return Math.Abs(Math.Abs(RoomDetect.Area(inset)) - Math.Abs(RoomDetect.Area(hole))) <= slop;
    }

    static bool OneThickness(List<Wall> walls, double tol, out double thickness, out string why)
    {
        thickness = 0;
        why = null;
        var limit = Math.Max(tol, 1);
        var seen = false;
        foreach (var wall in walls)
        {
            if (!wall.ThicknessMm.HasValue || !(wall.ThicknessMm.Value > 0) || double.IsNaN(wall.ThicknessMm.Value))
            {
                why = "wall thickness is missing";
                return false;
            }
            if (!seen)
            {
                thickness = wall.ThicknessMm.Value;
                seen = true;
                continue;
            }
            if (Math.Abs(thickness - wall.ThicknessMm.Value) > limit)
            {
                why = "the outer walls do not share one thickness";
                return false;
            }
        }
        if (!seen)
        {
            why = "no wall outline on this floor";
            return false;
        }
        return true;
    }

    static bool TryInset(List<Pt> ring, double thickness, out List<Pt> inset, out string reason)
    {
        inset = new List<Pt>(ring.Count);
        reason = null;
        var n = ring.Count;
        for (var i = 0; i < n; i++)
        {
            var prev = ring[(i + n - 1) % n];
            var at = ring[i];
            var next = ring[(i + 1) % n];
            var din = Sub(at, prev);
            var dout = Sub(next, at);
            if (Len(din) < 1e-6 || Len(dout) < 1e-6)
            {
                reason = "the wall outline does not inset cleanly";
                return false;
            }
            var p = Add(prev, Mul(Left(din), thickness));
            var q = Add(at, Mul(Left(dout), thickness));
            if (!TryMeet(p, din, q, dout, out var meet))
            {
                if (Dot(din, dout) <= 0)
                {
                    reason = "the wall outline does not inset cleanly";
                    return false;
                }
                meet = Add(at, Mul(Left(dout), thickness));
            }
            inset.Add(meet);
        }
        inset = Clean(inset);
        if (inset == null)
        {
            reason = "the walls are thicker than the outline";
            return false;
        }
        if (Crosses(inset))
        {
            reason = "the wall outline does not inset cleanly";
            return false;
        }
        return true;
    }

    static bool TryMeet(Pt p, Pt d, Pt q, Pt e, out Pt at)
    {
        var den = Cross(d, e);
        var scale = Len(d) * Len(e);
        if (Math.Abs(den) <= 1e-9 * Math.Max(scale, 1))
        {
            at = default;
            return false;
        }
        var t = Cross(Sub(q, p), e) / den;
        at = new Pt(p.X + t * d.X, p.Y + t * d.Y);
        return true;
    }

    static bool Crosses(List<Pt> ring)
    {
        var n = ring.Count;
        for (var i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            for (var j = i + 1; j < n; j++)
            {
                if (j == i || j == (i + 1) % n || i == (j + 1) % n) continue;
                if (i == 0 && j == n - 1) continue;
                var c = ring[j];
                var d = ring[(j + 1) % n];
                if (ProperCross(a, b, c, d)) return true;
            }
        }
        return false;
    }

    static bool ProperCross(Pt a, Pt b, Pt c, Pt d)
    {
        var d1 = Cross(Sub(b, a), Sub(c, a));
        var d2 = Cross(Sub(b, a), Sub(d, a));
        var d3 = Cross(Sub(d, c), Sub(a, c));
        var d4 = Cross(Sub(d, c), Sub(b, c));
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    static List<Pt> Clean(IList<Pt> ring)
    {
        if (ring == null) return null;
        var clean = new List<Pt>();
        foreach (var p in ring)
        {
            if (clean.Count > 0 && Dist2(clean[clean.Count - 1], p) < 0.01) continue;
            clean.Add(p);
        }
        if (clean.Count > 1 && Dist2(clean[0], clean[clean.Count - 1]) < 0.01) clean.RemoveAt(clean.Count - 1);
        return clean.Count >= 3 ? clean : null;
    }

    static Pt Sub(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);
    static Pt Add(Pt a, Pt b) => new Pt(a.X + b.X, a.Y + b.Y);
    static Pt Mul(Pt a, double k) => new Pt(a.X * k, a.Y * k);
    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
    static double Cross(Pt a, Pt b) => a.X * b.Y - a.Y * b.X;
    static double Len(Pt a) => Math.Sqrt(a.X * a.X + a.Y * a.Y);
    static double Dist2(Pt a, Pt b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    static Pt Left(Pt d)
    {
        var len = Len(d);
        return new Pt(-d.Y / len, d.X / len);
    }

    static int BySize(RoomLine a, RoomLine b)
    {
        var byArea = b.AreaMm2.CompareTo(a.AreaMm2);
        if (byArea != 0) return byArea;
        var byName = string.CompareOrdinal(a.Name, b.Name);
        return byName != 0 ? byName : string.CompareOrdinal(a.Id, b.Id);
    }

    static int ByArea(Group a, Group b)
    {
        var byArea = b.AreaMm2.CompareTo(a.AreaMm2);
        return byArea != 0 ? byArea : string.CompareOrdinal(a.Key, b.Key);
    }

    /// <summary>
    /// The storey a forsk:level prints as, in the language of the question.
    /// 0 is the ground floor. Below it, -1 is underetasjen and -2 is the basement.
    /// </summary>
    public static string FloorName(string level, bool norwegian)
    {
        var key = string.IsNullOrWhiteSpace(level) ? "0" : level.Trim();
        if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return norwegian ? key : "Floor " + key;
        if (n >= 0)
            return norwegian
                ? (n + 1).ToString(CultureInfo.InvariantCulture) + ". etasje"
                : (n == 0 ? "Ground floor" : Ordinal(n) + " floor");
        if (n == -1) return norwegian ? "U. etasje" : "Lower ground";
        if (n == -2) return norwegian ? "Kjeller" : "Basement";
        var deep = (-n - 1).ToString(CultureInfo.InvariantCulture);
        return norwegian ? deep + ". kjeller" : "Basement " + deep;
    }

    /// <summary>FloorName in the language of the turn that is reading the areas. English when no question set it.</summary>
    public static string FloorName(string level)
    {
        return FloorName(level, ForskSpeech.Norwegian);
    }

    static string Ordinal(int n)
    {
        var mod100 = n % 100;
        var suffix = "th";
        if (mod100 < 11 || mod100 > 13)
        {
            switch (n % 10)
            {
                case 1: suffix = "st"; break;
                case 2: suffix = "nd"; break;
                case 3: suffix = "rd"; break;
            }
        }
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    static int ByLevel(Group a, Group b)
    {
        var aNum = int.TryParse(a.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var an);
        var bNum = int.TryParse(b.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bn);
        if (aNum && bNum) return an.CompareTo(bn);
        return string.CompareOrdinal(a.Key, b.Key);
    }

    /// <summary>A word matches a key when it is that key, or a longer word that starts with a key of three letters or more (baderom, hallway), or the key plus digits (soverom2).</summary>
    static string MatchWord(string word)
    {
        string use = null;
        var best = 0;
        foreach (var pair in NameUses)
        {
            if (pair.Key.Length < best || word.Length < pair.Key.Length) continue;
            if (!word.StartsWith(pair.Key, StringComparison.Ordinal)) continue;
            var rest = word.Substring(pair.Key.Length);
            if (rest.Length > 0 && pair.Key.Length < 3 && !AllDigits(rest)) continue;
            best = pair.Key.Length;
            use = pair.Use;
        }
        return use;
    }

    static List<string> Words(string name)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) return words;
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var raw in name.ToLowerInvariant())
        {
            var c = Fold(raw);
            if (c == ' ')
            {
                if (sb.Length > 0) words.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    static char Fold(char c)
    {
        if (c == 'ø' || c == 'ö') return 'o';
        if (c == 'å' || c == 'ä' || c == 'æ') return 'a';
        if (c == 'é' || c == 'è' || c == 'ê' || c == 'ë') return 'e';
        return char.IsLetterOrDigit(c) ? c : ' ';
    }

    static bool AllDigits(string text)
    {
        if (text.Length == 0) return false;
        foreach (var c in text)
            if (c < '0' || c > '9') return false;
        return true;
    }
}
