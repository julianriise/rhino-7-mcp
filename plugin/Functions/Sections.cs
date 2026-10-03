using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Box = RhinoMCPPlugin.Functions.RoomDetect.Box;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// F5.3 cross sections. A section is a vertical cut along a line in plan,
/// looking to one side of it: a letter, the line's two points (model mm, XY)
/// and the look direction (a unit vector across the line). section_add
/// stores them in the document; layout_pack draws the marker A–A on the plan
/// and one sheet per section. Pure geometry, no RhinoCommon, so the layout,
/// the cut/beyond rule and the heights test headless; the sheet code bakes
/// what it returns.
///
/// Cut or beyond: v1 trims each solid on the cut plane before the hidden
/// line drawing, and no clipping plane reaches the HLD, so it never tags an
/// edge SilhouetteType.SectionCut. An edge is cut when it lies on the cut
/// plane: in the drawing, on a loop of the section fill (the solids'
/// contours on that plane). The plan's LiesOnLoops is the same rule.
/// </summary>
public static class Sections
{
    /// <summary>Document strings section and key that hold the sections as JSON.</summary>
    public const string MetaSection = "forsk";
    public const string MetaKey = "sections";
    public const string ViewPrefix = "section_";

    /// <summary>Paper cap height of the letter on the plan: the F5.1 marks' size.</summary>
    public const double LetterMm = Schedules.MarkMm;
    /// <summary>Paper length of the heavy stroke at each end of the cut line.</summary>
    public const double StrokeMm = 6.0;
    /// <summary>Paper length and base width of the arrow that shows the look direction.</summary>
    public const double ArrowLongMm = 2.5;
    public const double ArrowWideMm = 2.0;
    /// <summary>Paper gap between the arrow's tip and the letter.</summary>
    public const double LetterGapMm = 0.8;
    /// <summary>First paper offset of a marker past the building, then StepMm at a time out to ReachMm.</summary>
    public const double FirstMm = 4.0;
    public const double StepMm = 1.0;
    public const double ReachMm = 150.0;
    /// <summary>Paper clearance a marker keeps to tags, marks, lines and the other markers.</summary>
    public const double ClearMm = 0.8;

    /// <summary>Paper height of level and free height values on the section: the plan dimensions' 1.8 mm.</summary>
    public const double ValueMm = PlanDims.TextMm;
    /// <summary>How far the ground line runs past the building on each side, on paper.</summary>
    public const double GroundOverMm = 10.0;
    /// <summary>Paper gap between the building and the level marks, and their line length.</summary>
    public const double LevelGapMm = 6.0;
    public const double LevelLineMm = 14.0;
    /// <summary>Paper size of a level mark's triangle, its tip on the level.</summary>
    public const double LevelTriangleMm = 1.8;
    /// <summary>Two level values closer than this on paper (plus their text) step sideways.</summary>
    public const double LevelRowMm = ValueMm + 1.0;
    /// <summary>Two heights this close (mm) are one level.</summary>
    public const double SameLevelMm = 1.0;

    public sealed class Def
    {
        public string Letter;
        public Pt A;
        public Pt B;
        public Pt Look;
        public string Room = "";
        public string Axis = "";
        /// <summary>Dialog label (A1, A2, …). The marker and the sheet stay the letter.</summary>
        public string Name = "";
    }

    /// <summary>A picked line that is ready to store. Null from Decide means the user cancelled.</summary>
    public sealed class Drawn
    {
        public string Name;
        public string Letter;
        public Pt A;
        public Pt B;
        /// <summary>Set when nothing can be stored. Letter is then null.</summary>
        public string Error;
    }

    public const string TooShortMessage = "The line is under 1 mm.";
    public const string LettersFullMessage = "Every section letter A to Z is already used.";

    public static string View(string letter) => ViewPrefix + (letter ?? "").Trim().ToLowerInvariant();

    /// <summary>section_a → A. False for every other view name.</summary>
    public static bool TryLetter(string view, out string letter)
    {
        letter = null;
        var key = (view ?? "").Trim().ToLowerInvariant();
        if (!key.StartsWith(ViewPrefix, StringComparison.Ordinal)) return false;
        var rest = key.Substring(ViewPrefix.Length);
        if (rest.Length != 1 || rest[0] < 'a' || rest[0] > 'z') return false;
        letter = rest.ToUpperInvariant();
        return true;
    }

    public static string PageName(string letter) => "Forsk — Section " + letter;
    public static string LayerName(string letter) => "Section " + letter;
    public static string Title(string letter) => "Snitt " + letter + "–" + letter;

    /// <summary>The first letter A..Z no section uses.</summary>
    public static string NextLetter(IEnumerable<Def> defs)
    {
        var used = new HashSet<string>((defs ?? new List<Def>()).Select(d => d.Letter), StringComparer.OrdinalIgnoreCase);
        for (var c = 'A'; c <= 'Z'; c++)
            if (!used.Contains(c.ToString())) return c.ToString();
        return null;
    }

    /// <summary>Panel phrases that start the viewport line. A section through a room stays section_add.</summary>
    public static bool IsPickPhrase(string text)
    {
        var compact = new System.Text.StringBuilder();
        var space = false;
        foreach (var c in (text ?? "").ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                if (compact.Length > 0) space = true;
                continue;
            }
            if (space)
            {
                compact.Append(' ');
                space = false;
            }
            compact.Append(c);
        }
        var phrase = compact.ToString();
        return phrase.Contains("add cross section")
            || phrase.Contains("add a cross section")
            || phrase.Contains("new cross section")
            || phrase.Contains("draw cross section");
    }

    /// <summary>A1, then the smallest missing A&lt;n&gt;. A letter with no such name counts as none, so the first is A1.</summary>
    public static string NextSectionName(IEnumerable<Def> defs)
    {
        var used = new HashSet<int>();
        foreach (var def in defs ?? new Def[0])
            if (TrySectionNumber(def?.Name, out var number)) used.Add(number);
        var n = 1;
        while (used.Contains(n)) n++;
        return "A" + n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>True for A1, a12. False for A, A0, A1b.</summary>
    public static bool TrySectionNumber(string name, out int number)
    {
        number = 0;
        var text = (name ?? "").Trim();
        if (text.Length < 2 || (text[0] != 'A' && text[0] != 'a')) return false;
        for (var i = 1; i < text.Length; i++)
            if (text[i] < '0' || text[i] > '9') return false;
        if (text[1] == '0') return false;
        return int.TryParse(text.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }

    /// <summary>
    /// A single A–Z letter replaces that section, as section_add does.
    /// Anything else, including A1, takes the next free letter. Never strips A1 down to A.
    /// </summary>
    public static string LetterFor(string name, IEnumerable<Def> defs)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 1)
        {
            var c = char.ToUpperInvariant(trimmed[0]);
            if (c >= 'A' && c <= 'Z') return c.ToString();
        }
        return NextLetter(defs);
    }

    /// <summary>
    /// Lock the drag to X or Y in plan. The caller drops Z. Equal |dx| and |dy| lock to X.
    /// False when the locked line is under 1 mm.
    /// </summary>
    public static bool TrySnapAxis(Pt from, Pt to, out Pt snapped)
    {
        snapped = default;
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        snapped = Math.Abs(dx) >= Math.Abs(dy) ? new Pt(to.X, from.Y) : new Pt(from.X, to.Y);
        var sx = snapped.X - from.X;
        var sy = snapped.Y - from.Y;
        if (sx * sx + sy * sy < 1.0) return false;
        return true;
    }

    /// <summary>
    /// What a finished pick stores. Null when the line or the name was cancelled:
    /// the document stays as it was, including no new layer.
    /// </summary>
    public static Drawn Decide(bool haveLine, string dialogName, IEnumerable<Def> defs, Pt from, Pt rawTo)
    {
        if (!haveLine || dialogName == null) return null;
        var name = dialogName.Trim();
        if (name.Length == 0) name = NextSectionName(defs);
        if (!TrySnapAxis(from, rawTo, out var snapped))
            return new Drawn { Error = TooShortMessage };
        var letter = LetterFor(name, defs);
        if (letter == null)
            return new Drawn { Error = LettersFullMessage };
        return new Drawn { Name = name, Letter = letter, A = from, B = snapped };
    }

    public static List<Def> Read(string json)
    {
        var defs = new List<Def>();
        if (string.IsNullOrWhiteSpace(json)) return defs;
        JArray rows;
        try { rows = JArray.Parse(json); }
        catch (Exception) { return defs; }
        foreach (var row in rows.OfType<JObject>())
        {
            var letter = row["letter"]?.ToString();
            var a = ReadPt(row["a"]);
            var b = ReadPt(row["b"]);
            var look = ReadPt(row["look"]);
            if (string.IsNullOrEmpty(letter) || a == null || b == null || look == null) continue;
            defs.Add(new Def
            {
                Letter = letter,
                A = a.Value,
                B = b.Value,
                Look = look.Value,
                Room = row["room"]?.ToString() ?? "",
                Axis = row["axis"]?.ToString() ?? "",
                Name = row["name"]?.ToString() ?? ""
            });
        }
        return defs.OrderBy(d => d.Letter, StringComparer.Ordinal).ToList();
    }

    public static string Write(IEnumerable<Def> defs)
    {
        var rows = new JArray();
        foreach (var d in (defs ?? new List<Def>()).OrderBy(d => d.Letter, StringComparer.Ordinal))
            rows.Add(Record(d));
        return rows.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static JObject Record(Def d)
    {
        return new JObject
        {
            ["letter"] = d.Letter,
            ["view"] = View(d.Letter),
            ["a"] = new JArray(Math.Round(d.A.X, 3), Math.Round(d.A.Y, 3)),
            ["b"] = new JArray(Math.Round(d.B.X, 3), Math.Round(d.B.Y, 3)),
            ["look"] = new JArray(Math.Round(d.Look.X, 6), Math.Round(d.Look.Y, 6)),
            ["room"] = d.Room ?? "",
            ["axis"] = d.Axis ?? "",
            ["name"] = d.Name ?? ""
        };
    }

    static Pt? ReadPt(JToken token)
    {
        if (!(token is JArray xy) || xy.Count < 2) return null;
        try { return new Pt(xy[0].ToObject<double>(), xy[1].ToObject<double>()); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// A line through a point inside a room. long runs along the building's
    /// longer side (its footprint box), cross across it. The line spans the
    /// footprint; the look follows LookAcross.
    /// </summary>
    public static Def Through(string letter, Pt inside, Box footprint, string axis, string look)
    {
        var w = footprint.MaxX - footprint.MinX;
        var h = footprint.MaxY - footprint.MinY;
        var cross = !string.Equals(axis, "long", StringComparison.OrdinalIgnoreCase);
        var alongX = (w >= h) != cross;
        Pt a, b;
        if (alongX)
        {
            a = new Pt(footprint.MinX, inside.Y);
            b = new Pt(footprint.MaxX, inside.Y);
        }
        else
        {
            // Top to bottom: the left of that line is +X, so a plain cross
            // section of a building long in X looks east.
            a = new Pt(inside.X, footprint.MaxY);
            b = new Pt(inside.X, footprint.MinY);
        }
        return Along(letter, a, b, look, cross ? "cross" : "long");
    }

    /// <summary>A section on a given line. False for a line under 1 mm.</summary>
    public static Def Along(string letter, Pt a, Pt b, string look, string axis = "")
    {
        if (!TryLookAcross(a, b, look, out var dir)) return null;
        return new Def { Letter = letter, A = a, B = b, Look = dir, Axis = axis ?? "" };
    }

    /// <summary>
    /// The look direction: across the line, to its left (A to B) unless a
    /// compass word (north, south, east, west) picks the other side. A word
    /// along the line keeps the left.
    /// </summary>
    public static bool TryLookAcross(Pt a, Pt b, string look, out Pt dir)
    {
        dir = default;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1.0) return false;
        var left = new Pt(-dy / len, dx / len);
        dir = left;
        if (!TryCompass(look, out var want)) return true;
        var dot = want.X * left.X + want.Y * left.Y;
        if (dot < -1e-6) dir = new Pt(-left.X, -left.Y);
        return true;
    }

    public static bool TryCompass(string word, out Pt dir)
    {
        switch ((word ?? "").Trim().ToLowerInvariant())
        {
            case "north": case "nord": dir = new Pt(0, 1); return true;
            case "south": case "sør": case "sor": dir = new Pt(0, -1); return true;
            case "east": case "øst": case "ost": dir = new Pt(1, 0); return true;
            case "west": case "vest": dir = new Pt(-1, 0); return true;
            default: dir = default; return false;
        }
    }

    /// <summary>Unit vector A to B.</summary>
    public static Pt Direction(Def def)
    {
        var dx = def.B.X - def.A.X;
        var dy = def.B.Y - def.A.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-9 ? new Pt(1, 0) : new Pt(dx / len, dy / len);
    }

    /// <summary>Distance along the line from A of a plan point: the section's horizontal coordinate.</summary>
    public static double U(Def def, double x, double y)
    {
        var d = Direction(def);
        return (x - def.A.X) * d.X + (y - def.A.Y) * d.Y;
    }

    // ---- Cut or beyond ----

    /// <summary>
    /// True when every sample of an edge lies on one cut loop (within tol):
    /// that edge is where the plane cuts a solid, drawn by the poché and its
    /// heavy outline. Anything else is seen beyond the cut.
    /// </summary>
    public static bool IsCut(IList<Pt> samples, IList<List<Pt>> cutRings, double tol)
    {
        if (samples == null || samples.Count == 0 || cutRings == null) return false;
        foreach (var ring in cutRings)
        {
            if (ring == null || ring.Count < 2) continue;
            var on = true;
            foreach (var p in samples)
            {
                if (RoomDetect.Clearance(ring, p) <= tol) continue;
                on = false;
                break;
            }
            if (on) return true;
        }
        return false;
    }

    // ---- Heights ----

    public sealed class Level
    {
        /// <summary>floor, ground, gesims, mone, or gesims,mone when they are one height.</summary>
        public string Kind;
        public double Z;
        /// <summary>Whole mm above ±0, the lowest floor's top.</summary>
        public int Value;
        public string Text;
    }

    /// <summary>
    /// The section's level marks: each floor level (slab tops), the ground,
    /// gesims and møne. ±0 is the lowest floor's top; values are whole mm
    /// above it. Floors within SameLevelMm are one; gesims and møne at one
    /// height share a mark. Lowest first.
    /// </summary>
    public static List<Level> Levels(IEnumerable<double> floorTops, double? ground, double? gesims, double? mone)
    {
        var floors = new List<double>();
        foreach (var z in (floorTops ?? new double[0]).OrderBy(z => z))
            if (floors.Count == 0 || z - floors[floors.Count - 1] > SameLevelMm) floors.Add(z);
        var zero = floors.Count > 0 ? floors[0] : 0.0;
        var levels = new List<Level>();
        for (var i = 0; i < floors.Count; i++)
            levels.Add(Make("floor", floors[i], zero, (i + 1).ToString(CultureInfo.InvariantCulture) + ". etg"));
        if (ground.HasValue)
            levels.Add(Make("ground", ground.Value, zero, "Terreng"));
        if (gesims.HasValue && mone.HasValue && Math.Abs(gesims.Value - mone.Value) <= SameLevelMm)
            levels.Add(Make("gesims,mone", Math.Max(gesims.Value, mone.Value), zero, "Gesims/møne"));
        else
        {
            if (gesims.HasValue) levels.Add(Make("gesims", gesims.Value, zero, "Gesims"));
            if (mone.HasValue) levels.Add(Make("mone", mone.Value, zero, "Møne"));
        }
        return levels.OrderBy(l => l.Z).ToList();
    }

    /// <summary>A solid of the model by its kind (floor, wall, roof) and its height range.</summary>
    public sealed class Solid
    {
        public string Kind;
        public double MinZ;
        public double MaxZ;
    }

    public sealed class Heights
    {
        /// <summary>The ground: the lowest slab's underside, else the lowest wall base. Null with neither.</summary>
        public double? Ground;
        public List<Level> Levels = new List<Level>();
    }

    /// <summary>
    /// The heights a section and a facade print, from one rule so the two
    /// sheets cannot disagree. The model has no terrain yet, so the ground is
    /// the lowest slab's underside (the house stands on it); with no slab,
    /// the lowest wall base. The floors are the slab tops, or with no slab
    /// the floors of the rooms. Gesims and møne come from the roof's outline
    /// in (u, z): a section's cut loop, or a facade's roof as it is seen.
    /// </summary>
    public static Heights ModelHeights(IEnumerable<Solid> solids, IEnumerable<double> roomFloors, IList<List<Pt>> roofUz)
    {
        var slabTops = new List<double>();
        double? ground = null;
        double? lowestWall = null;
        foreach (var solid in solids ?? new Solid[0])
        {
            if (solid == null) continue;
            if (string.Equals(solid.Kind, "wall", StringComparison.OrdinalIgnoreCase))
                lowestWall = lowestWall.HasValue ? Math.Min(lowestWall.Value, solid.MinZ) : solid.MinZ;
            if (!string.Equals(solid.Kind, "floor", StringComparison.OrdinalIgnoreCase)) continue;
            slabTops.Add(solid.MaxZ);
            ground = ground.HasValue ? Math.Min(ground.Value, solid.MinZ) : solid.MinZ;
        }
        if (!ground.HasValue) ground = lowestWall;
        double? gesims = null, mone = null;
        if (RoofHeights(roofUz, out var g, out var m))
        {
            gesims = g;
            mone = m;
        }
        return new Heights { Ground = ground, Levels = Levels(FloorTops(slabTops, roomFloors), ground, gesims, mone) };
    }

    /// <summary>How far a facade's ground line runs past the facade on each side, in model mm.</summary>
    public const double FacadeGroundOverMm = 1000.0;

    /// <summary>A facade's ground line from left to right: the facade's width plus FacadeGroundOverMm each side.</summary>
    public static (double X0, double X1) FacadeGround(double left, double right)
    {
        return (left - FacadeGroundOverMm, right + FacadeGroundOverMm);
    }

    /// <summary>The ground line is drawn with the profile's cut pen, on a section and on a facade.</summary>
    public static PrintPen GroundPen(PrintProfile profile) => (profile ?? PrintProfiles.Default).Cut;

    /// <summary>The level marks are drawn with the profile's thin pen, on a section and on a facade.</summary>
    public static PrintPen LevelPen(PrintProfile profile) => (profile ?? PrintProfiles.Default).Thin;

    /// <summary>
    /// The floor levels of a section: the slab tops. A model with no slab (a
    /// garage of walls on grade) has its floor where its rooms stand, so the
    /// floors of the rooms the line crosses stand in for the slab tops.
    /// </summary>
    public static List<double> FloorTops(IEnumerable<double> slabTops, IEnumerable<double> roomFloors)
    {
        var slabs = (slabTops ?? new double[0]).ToList();
        return slabs.Count > 0 ? slabs : (roomFloors ?? new double[0]).ToList();
    }

    static Level Make(string kind, double z, double zero, string name)
    {
        var value = (int)Math.Round(z - zero, MidpointRounding.AwayFromZero);
        return new Level { Kind = kind, Z = z, Value = value, Text = name + " " + Signed(value) };
    }

    /// <summary>±0, +3000, -400.</summary>
    public static string Signed(int value)
    {
        if (value == 0) return "±0";
        return (value > 0 ? "+" : "-") + Math.Abs(value).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gesims and møne from the roof's cut loops in section coordinates (u
    /// along the line, z up). Møne is the highest point. Gesims is where the
    /// roof's top meets the eave: the top at each end of the cut roof, the
    /// lower of the two. A flat roof has both at its top.
    /// </summary>
    public static bool RoofHeights(IList<List<Pt>> roofLoops, out double gesims, out double mone)
    {
        gesims = mone = 0;
        var points = (roofLoops ?? new List<List<Pt>>()).Where(r => r != null).SelectMany(r => r).ToList();
        if (points.Count == 0) return false;
        mone = points.Max(p => p.Y);
        var uMin = points.Min(p => p.X);
        var uMax = points.Max(p => p.X);
        var left = points.Where(p => p.X <= uMin + SameLevelMm).Max(p => p.Y);
        var right = points.Where(p => p.X >= uMax - SameLevelMm).Max(p => p.Y);
        gesims = Math.Min(left, right);
        return true;
    }

    /// <summary>
    /// Where the cut line crosses a room's outline, as distances along the
    /// line from A: the longest stretch inside. False when the line misses.
    /// </summary>
    public static bool RoomSpan(IList<Pt> ring, Def def, out double u0, out double u1)
    {
        u0 = u1 = 0;
        if (ring == null || ring.Count < 3 || def == null) return false;
        var d = Direction(def);
        var hits = new List<double>();
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            var ex = q.X - p.X;
            var ey = q.Y - p.Y;
            var den = d.X * ey - d.Y * ex;
            if (Math.Abs(den) < 1e-12) continue;
            var wx = p.X - def.A.X;
            var wy = p.Y - def.A.Y;
            var t = (wx * ey - wy * ex) / den;
            var s = (wx * d.Y - wy * d.X) / den;
            if (s < 0 || s >= 1) continue;
            hits.Add(t);
        }
        hits.Sort();
        var best = 0.0;
        for (var i = 0; i + 1 < hits.Count; i++)
        {
            var mid = 0.5 * (hits[i] + hits[i + 1]);
            if (!RoomDetect.Contains(ring, new Pt(def.A.X + d.X * mid, def.A.Y + d.Y * mid))) continue;
            if (hits[i + 1] - hits[i] <= best) continue;
            best = hits[i + 1] - hits[i];
            u0 = hits[i];
            u1 = hits[i + 1];
        }
        return best > 1.0;
    }

    /// <summary>
    /// The underside of what is over a floor at u: the lowest crossing of
    /// the vertical through u with any cut loop (section coordinates) that
    /// lies above floorZ by more than tol. Null when nothing covers it.
    /// </summary>
    public static double? Ceiling(IList<List<Pt>> loops, double u, double floorZ, double tol)
    {
        double? best = null;
        foreach (var ring in loops ?? new List<List<Pt>>())
        {
            if (ring == null || ring.Count < 3) continue;
            for (var i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Count];
                if ((p.X - u) * (q.X - u) > 0 || Math.Abs(q.X - p.X) < 1e-9) continue;
                var z = p.Y + (q.Y - p.Y) * (u - p.X) / (q.X - p.X);
                if (z <= floorZ + tol) continue;
                if (!best.HasValue || z < best.Value) best = z;
            }
        }
        return best;
    }

    /// <summary>Free height: whole mm from a floor to the underside over it at u. Null when nothing covers it.</summary>
    public static int? FreeHeight(IList<List<Pt>> loops, double u, double floorZ, double tol)
    {
        var top = Ceiling(loops, u, floorZ, tol);
        if (!top.HasValue) return null;
        return (int)Math.Round(top.Value - floorZ, MidpointRounding.AwayFromZero);
    }

    // ---- The marker on the plan ----

    /// <summary>One end of a marker, in drawing mm: the heavy stroke, the arrow, the letter's centre and its box.</summary>
    public sealed class End
    {
        public Pt StrokeA;
        public Pt StrokeB;
        public Pt[] Arrow;
        public Pt LetterAt;
        public Box LetterBox;
        public List<Box> Boxes = new List<Box>();
    }

    public sealed class Marker
    {
        public List<End> Ends = new List<End>();
        /// <summary>False when an end found no spot clear of everything within ReachMm.</summary>
        public bool Clear = true;
    }

    /// <summary>
    /// The marker A–A on the plan, in drawing mm: at each end of the cut
    /// line past the building, a heavy stroke on the line, an arrow from its
    /// outer end toward the look, and the letter past the arrow's tip. Each
    /// end starts FirstMm past the building outline (projected on the line)
    /// and steps out until its stroke, arrow and letter clear every taken box
    /// (tags, marks, symbols, dimensions, the other markers) by ClearMm. The
    /// line between the ends is not drawn, so the marker never crosses a room
    /// tag or a mark inside. a and b are the line in drawing mm, look a unit
    /// vector in drawing mm; hx, hy are the letter's half extents.
    /// </summary>
    public static Marker PlaceMarker(
        Pt a, Pt b, Pt look, IList<List<Pt>> outlines, IList<Box> taken, int scale, double hx, double hy)
    {
        var marker = new Marker();
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9 || scale < 1) return marker;
        var d = new Pt(dx / len, dy / len);
        double tMin = 0, tMax = len;
        var any = false;
        foreach (var ring in outlines ?? new List<List<Pt>>())
            foreach (var p in ring ?? new List<Pt>())
            {
                var t = (p.X - a.X) * d.X + (p.Y - a.Y) * d.Y;
                if (!any) { tMin = tMax = t; any = true; }
                tMin = Math.Min(tMin, t);
                tMax = Math.Max(tMax, t);
            }
        var blocked = new List<Box>(taken ?? new List<Box>());
        var clear = ClearMm * scale;
        foreach (var sign in new[] { -1.0, 1.0 })
        {
            var edge = sign < 0 ? tMin : tMax;
            End first = null;
            End found = null;
            for (var off = FirstMm; off <= ReachMm + 1e-9; off += StepMm)
            {
                var end = MakeEnd(a, d, look, edge + sign * off * scale, sign, scale, hx, hy);
                if (first == null) first = end;
                if (end.Boxes.Any(box => blocked.Any(t => Schedules.Overlaps(box, t, clear)))) continue;
                found = end;
                break;
            }
            if (found == null)
            {
                marker.Clear = false;
                found = first;
            }
            marker.Ends.Add(found);
            blocked.AddRange(found.Boxes);
        }
        return marker;
    }

    static End MakeEnd(Pt a, Pt d, Pt look, double t, double sign, int scale, double hx, double hy)
    {
        Pt At(double s) => new Pt(a.X + d.X * s, a.Y + d.Y * s);
        Pt Add(Pt p, Pt v, double k) => new Pt(p.X + v.X * k, p.Y + v.Y * k);
        var inner = At(t);
        var outer = At(t + sign * StrokeMm * scale);
        var half = 0.5 * ArrowWideMm * scale;
        var baseA = Add(outer, d, -sign * 2 * half);
        var tip = Add(Add(outer, d, -sign * half), look, ArrowLongMm * scale);
        var reach = Math.Abs(look.X) * hx + Math.Abs(look.Y) * hy;
        var letter = Add(tip, look, LetterGapMm * scale + reach);
        var end = new End
        {
            StrokeA = inner,
            StrokeB = outer,
            Arrow = new[] { baseA, outer, tip },
            LetterAt = letter,
            LetterBox = Schedules.MarkBox(letter, hx, hy)
        };
        end.Boxes.Add(BoxOf(new[] { inner, outer }));
        end.Boxes.Add(BoxOf(end.Arrow));
        end.Boxes.Add(end.LetterBox);
        return end;
    }

    public static Box BoxOf(IEnumerable<Pt> points)
    {
        var list = points.ToList();
        return new Box(list.Min(p => p.X), list.Min(p => p.Y), list.Max(p => p.X), list.Max(p => p.Y));
    }

    // ---- Level marks on the section sheet ----

    public sealed class LevelMark
    {
        public Level Level;
        public double Y;
        public Pt LineA;
        public Pt LineB;
        public Pt[] Triangle;
        public Pt TextAt;
        public Box TextBox;
    }

    /// <summary>
    /// Level marks right of the building, in drawing mm: a line at each
    /// level from LevelGapMm past the building's right edge, a triangle with
    /// its tip on the line, the value above the line. ys maps a model height
    /// to drawing Y; width measures a value's paper width. A value that
    /// would touch the one below steps right past it.
    /// </summary>
    public static List<LevelMark> PlaceLevels(
        IList<Level> levels, Func<double, double> ys, double right, int scale, Func<string, double> width)
    {
        var marks = new List<LevelMark>();
        if (levels == null || ys == null || scale < 1) return marks;
        var x0 = right + LevelGapMm * scale;
        var tri = LevelTriangleMm * scale;
        var gap = 0.6 * scale;
        var text = ValueMm * scale;
        var placed = new List<Box>();
        foreach (var level in levels.OrderBy(l => l.Z))
        {
            var y = ys(level.Z);
            var w = Math.Max(width?.Invoke(level.Text) ?? 0, 0.6 * ValueMm * level.Text.Length) * scale;
            var column = x0 + tri + gap;
            Box box;
            while (true)
            {
                box = new Box(column, y + gap, column + w, y + gap + text);
                var hit = placed.Where(p => Schedules.Overlaps(box, p, 0.4 * scale)).ToList();
                if (hit.Count == 0) break;
                column = hit.Max(p => p.MaxX) + 2 * gap;
            }
            placed.Add(box);
            var tipX = x0 + 0.5 * tri;
            marks.Add(new LevelMark
            {
                Level = level,
                Y = y,
                LineA = new Pt(x0, y),
                LineB = new Pt(Math.Max(x0 + LevelLineMm * scale, box.MaxX), y),
                Triangle = new[] { new Pt(tipX, y), new Pt(tipX - 0.5 * tri, y + tri), new Pt(tipX + 0.5 * tri, y + tri) },
                TextAt = new Pt(0.5 * (box.MinX + box.MaxX), 0.5 * (box.MinY + box.MaxY)),
                TextBox = box
            });
        }
        return marks;
    }
}
