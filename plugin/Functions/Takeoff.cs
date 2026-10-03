using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 P7: the takeoff (mengdeliste) at a v3 depth. Walls are read as runs of
/// the F2 join graph, so a split and a whole record give the same figures:
/// a run is outer when one of its faces lies on its cluster's outer ring.
/// A run's length is its footprint (the band between its faces, a trapezoid
/// at the corners) ÷ its thickness, the mean of its two faces' extents; its
/// area is length × height less its openings, one side; its volume is the
/// footprint × height less the openings × thickness. Walls on X-EXIST are
/// kept apart and not counted in the totals. Slabs and roofs each, doors and
/// windows by type and size from the schedules' own records, and the areas
/// straight from area_stats. No layers, insulation, cladding, NS 3451 codes,
/// cost or waste. Every figure is ca. Pure, no Rhino document.
/// </summary>
public static class Takeoff
{
    public const string Outer = "Exterior walls";
    public const string Inner = "Interior walls";
    public const string Existing = "Existing";
    public const string Slabs = "Slabs";
    public const string Roofs = "Roof";
    public const string Doors = "Doors";
    public const string Windows = "Windows";
    public const string StairsGroup = "Stairs";
    public const string Areas = "Areas";
    public const string Note = "Quantities are approximate, from the model. Wall area is net of openings, one side.";
    public const string NoteNb = "Mengder er ca.-tall fra modellen. Veggareal er netto av åpninger, én side.";

    /// <summary>One wall record: forsk:path rings (outer, then holes), forsk:thickness, forsk:height, X-EXIST or not.</summary>
    public sealed class Wall
    {
        public List<List<Pt>> Rings;
        public double ThicknessMm;
        public double HeightMm;
        public bool Existing;
    }

    /// <summary>A floor slab or a roof: its footprint (with the overhang) and its thickness.</summary>
    public sealed class Slab
    {
        public double AreaMm2;
        public double ThicknessMm;
    }

    /// <summary>One line of the takeoff. A figure that does not apply is null.</summary>
    public sealed class Line
    {
        public string Group;
        public string Label;
        /// <summary>How the card names it: Yttervegg 200 mm, Dekke 400 mm.</summary>
        public string Name;
        public double? LengthM;
        public double? AreaM2;
        public double? VolumeM3;
        public int? Count;
    }

    public sealed class Result
    {
        public List<Line> Lines = new List<Line>();
        public string Summary;
    }

    sealed class RunFigures
    {
        public bool Outer;
        public double Thickness;
        public double Length;
        public double Area;
        public double Volume;
    }

    public static Result Compute(
        IList<Wall> walls, IList<Schedules.Opening> openings, IList<Slab> slabs, IList<Slab> roofs, AreaStats.Result area, double tol, bool norwegian = false,
        IList<Stairs.Flight> stairs = null)
    {
        walls = walls ?? new Wall[0];
        openings = openings ?? new Schedules.Opening[0];
        var result = new Result();
        var built = WallRuns(walls.Where(w => !w.Existing).ToList(), openings, tol);
        AddWalls(result, built.Where(r => r.Outer), Group(Outer, "Yttervegger", norwegian), SheetLang.Pick(norwegian, "Exterior wall", "Yttervegg"));
        AddWalls(result, built.Where(r => !r.Outer), Group(Inner, "Innervegger", norwegian), SheetLang.Pick(norwegian, "Interior wall", "Innervegg"));
        var standing = WallRuns(walls.Where(w => w.Existing).ToList(), new Schedules.Opening[0], tol);
        var existing = Group(Existing, "Eksisterende", norwegian);
        AddWalls(result, standing.Where(r => r.Outer), existing, SheetLang.Pick(norwegian, "Existing exterior wall", "Eksisterende yttervegg"));
        AddWalls(result, standing.Where(r => !r.Outer), existing, SheetLang.Pick(norwegian, "Existing interior wall", "Eksisterende innervegg"));
        foreach (var slab in slabs ?? new Slab[0]) AddSlab(result, Group(Slabs, "Dekker", norwegian), SheetLang.Pick(norwegian, "Slab", "Dekke"), slab);
        foreach (var roof in roofs ?? new Slab[0]) AddSlab(result, Group(Roofs, "Tak", norwegian), SheetLang.Pick(norwegian, "Roof", "Tak"), roof);
        AddOpenings(result, openings.Where(o => o.Record?.Kind == "door"), Group(Doors, "Dører", norwegian), norwegian);
        AddOpenings(result, openings.Where(o => o.Record?.Kind == "window"), Group(Windows, "Vinduer", norwegian), norwegian);
        AddStairs(result, stairs, norwegian);
        AddAreas(result, area, norwegian);
        result.Summary = Summarize(result, norwegian);
        return result;
    }

    static string Group(string english, string bokmal, bool norwegian) => norwegian ? bokmal : english;

    /// <summary>The card's row: "Yttervegg 200 mm · 23,2 m · 52,1 m² · 10,4 m³".</summary>
    public static string Row(Line line, bool norwegian = false)
    {
        var parts = new List<string> { line.Name };
        if (line.Count.HasValue) parts.Add(line.Count.Value.ToString(CultureInfo.InvariantCulture) + SheetLang.Pick(norwegian, " no.", " stk"));
        if (line.LengthM.HasValue) parts.Add(Number(line.LengthM.Value) + " m");
        if (line.AreaM2.HasValue) parts.Add(Number(line.AreaM2.Value) + " m²");
        if (line.VolumeM3.HasValue) parts.Add(Number(line.VolumeM3.Value) + " m³");
        return string.Join(" · ", parts);
    }

    /// <summary>The Mengdeliste on the lists' path: a heading row per group, then its lines, and the note.</summary>
    public static Schedules.Table Table(Result result, bool norwegian = false)
    {
        var table = new Schedules.Table
        {
            Kind = "takeoff",
            Title = SheetLang.Pick(norwegian, "Quantities", "Mengdeliste"),
            Heads = norwegian
                ? new[] { "Post", "Lengde (m)", "Areal (m²)", "Volum (m³)", "Antall" }
                : new[] { "Item", "Length (m)", "Area (m²)", "Volume (m³)", "Count" },
            Right = new[] { false, true, true, true, true },
            ContinuedSuffix = norwegian ? " (forts.)" : " (cont.)"
        };
        var existing = Group(Existing, "Eksisterende", norwegian);
        string group = null;
        var n = 0;
        foreach (var line in result?.Lines ?? new List<Line>())
        {
            if (line.Group != group)
            {
                group = line.Group;
                table.Ids.Add("group-" + group);
                table.Rows.Add(new[] { group, "", "", "", "" });
            }
            table.Ids.Add("line-" + (++n).ToString(CultureInfo.InvariantCulture));
            table.Rows.Add(new[]
            {
                line.Group == existing ? line.Name : line.Label,
                line.LengthM.HasValue ? Number(line.LengthM.Value, norwegian) : "",
                line.AreaM2.HasValue ? Number(line.AreaM2.Value, norwegian) : "",
                line.VolumeM3.HasValue ? Number(line.VolumeM3.Value, norwegian) : "",
                line.Count.HasValue ? line.Count.Value.ToString(CultureInfo.InvariantCulture) : ""
            });
        }
        table.Note = table.Rows.Count == 0 ? null : (norwegian ? NoteNb : Note);
        return Schedules.Fit(table, null);
    }

    /// <summary>One decimal as the chat prints it, with a comma: 23,2.</summary>
    public static string Number(double value) => Number(value, true);

    /// <summary>One decimal. English sheets use a point (32.0); Bokmål keeps the comma.</summary>
    public static string Number(double value, bool norwegian)
    {
        var text = Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);
        return norwegian ? text.Replace('.', ',') : text;
    }

    /// <summary>Every run of every cluster of these walls, with its openings taken out.</summary>
    static List<RunFigures> WallRuns(IList<Wall> walls, IList<Schedules.Opening> openings, double tol)
    {
        var figures = new List<RunFigures>();
        var records = walls.Select(w => w.Rings).ToList();
        if (records.Count == 0) return figures;
        var taken = new HashSet<Schedules.Opening>();
        foreach (var cluster in WallJoins.Clusters(records, tol))
        {
            var graph = WallJoins.Build(records, cluster, tol);
            // A cluster whose union is not one piece is read record by record.
            var graphs = graph != null
                ? new List<(WallJoins.Graph Graph, List<int> Records)> { (graph, cluster) }
                : cluster.Select(i => (WallJoins.Build(records, new List<int> { i }, tol), new List<int> { i })).Where(g => g.Item1 != null).ToList();
            foreach (var (g, members) in graphs)
            {
                foreach (var run in g.Runs)
                {
                    var length = MeanFaceExtent(g.Shape, run);
                    if (length <= 0) continue;
                    var height = HeightOf(walls, records, members, run);
                    var open = 0.0;
                    foreach (var opening in openings)
                    {
                        if (taken.Contains(opening) || !OnRun(run, opening, tol)) continue;
                        taken.Add(opening);
                        open += opening.Width * Math.Max(0, opening.Head - opening.Sill);
                    }
                    figures.Add(new RunFigures
                    {
                        Outer = run.Edges.Any(e => e.Loop == 0),
                        Thickness = Math.Round(run.Thickness, MidpointRounding.AwayFromZero),
                        Length = length,
                        Area = length * height - open,
                        Volume = (length * height - open) * run.Thickness
                    });
                }
            }
        }
        return figures;
    }

    /// <summary>The mean of the run's two faces' extents along it: the band's footprint ÷ its thickness.</summary>
    static double MeanFaceExtent(List<List<Pt>> shape, WallEdit.Run run)
    {
        double nearLo = double.MaxValue, nearHi = double.MinValue, farLo = double.MaxValue, farHi = double.MinValue;
        foreach (var (loop, edge) in run.Edges)
        {
            var ring = shape[loop];
            var a = ring[edge];
            var b = ring[(edge + 1) % ring.Count];
            var across = 0.5 * (Dot(a, run.Normal) + Dot(b, run.Normal));
            var u0 = Math.Min(Dot(a, run.Dir), Dot(b, run.Dir));
            var u1 = Math.Max(Dot(a, run.Dir), Dot(b, run.Dir));
            if (Math.Abs(across - run.Near) <= Math.Abs(across - run.Far))
            {
                nearLo = Math.Min(nearLo, u0);
                nearHi = Math.Max(nearHi, u1);
            }
            else
            {
                farLo = Math.Min(farLo, u0);
                farHi = Math.Max(farHi, u1);
            }
        }
        var near = nearHi > nearLo ? nearHi - nearLo : 0;
        var far = farHi > farLo ? farHi - farLo : 0;
        if (near <= 0) return far;
        if (far <= 0) return near;
        return 0.5 * (near + far);
    }

    /// <summary>The height of the record that holds the run's middle; the cluster's tallest when none does.</summary>
    static double HeightOf(IList<Wall> walls, IList<List<List<Pt>>> records, IList<int> members, WallEdit.Run run)
    {
        var middle = WallJoins.Middle(run);
        foreach (var i in members)
            if (WallEdit.InRegion(records[i], middle)) return walls[i].HeightMm;
        return members.Max(i => walls[i].HeightMm);
    }

    /// <summary>An opening's centre inside the run's band: between its faces and along its extent.</summary>
    static bool OnRun(WallEdit.Run run, Schedules.Opening opening, double tol)
    {
        var p = new Pt(opening.X, opening.Y);
        var across = Dot(p, run.Normal);
        var along = Dot(p, run.Dir);
        return across >= run.Near - tol && across <= run.Far + tol && along >= run.Lo - tol && along <= run.Hi + tol;
    }

    static double Dot(Pt p, Pt v) => p.X * v.X + p.Y * v.Y;

    static void AddWalls(Result result, IEnumerable<RunFigures> runs, string group, string name)
    {
        foreach (var byThickness in runs.GroupBy(r => r.Thickness).OrderByDescending(g => g.Key))
        {
            var label = byThickness.Key.ToString("0", CultureInfo.InvariantCulture) + " mm";
            result.Lines.Add(new Line
            {
                Group = group,
                Label = label,
                Name = name + " " + label,
                LengthM = byThickness.Sum(r => r.Length) / 1000.0,
                AreaM2 = byThickness.Sum(r => r.Area) / 1e6,
                VolumeM3 = byThickness.Sum(r => r.Volume) / 1e9
            });
        }
    }

    static void AddSlab(Result result, string group, string name, Slab slab)
    {
        if (slab == null || slab.AreaMm2 <= 0) return;
        var label = slab.ThicknessMm.ToString("0", CultureInfo.InvariantCulture) + " mm";
        result.Lines.Add(new Line
        {
            Group = group,
            Label = label,
            Name = name + " " + label,
            AreaM2 = slab.AreaMm2 / 1e6,
            VolumeM3 = slab.AreaMm2 * slab.ThicknessMm / 1e9
        });
    }

    static void AddOpenings(Result result, IEnumerable<Schedules.Opening> openings, string group, bool norwegian)
    {
        var bySize = openings
            .GroupBy(o => Schedules.TypeText(o.Record, norwegian) + " " + Mm(o.Width) + " × " + Mm(o.Head - o.Sill))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var size in bySize)
            result.Lines.Add(new Line
            {
                Group = group,
                Label = size.Key,
                Name = size.Key,
                Count = size.Count(),
                AreaM2 = size.Sum(o => o.Width * Math.Max(0, o.Head - o.Sill)) / 1e6
            });
    }

    /// <summary>R5: the stairs by size: "Straight stair 16 × 180/260, 900 wide", and how many.</summary>
    static void AddStairs(Result result, IList<Stairs.Flight> stairs, bool norwegian)
    {
        if (stairs == null) return;
        var group = Group(StairsGroup, "Trapper", norwegian);
        foreach (var size in stairs.Where(s => s != null)
                     .GroupBy(s => SheetLang.Pick(norwegian, "Straight stair ", "Rett trapp ") + Stairs.Sizes(s) + ", "
                         + Mm(s.Width) + SheetLang.Pick(norwegian, " wide", " bred"))
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
            result.Lines.Add(new Line { Group = group, Label = size.Key, Name = size.Key, Count = size.Count() });
    }

    /// <summary>BTA, BRA and Netto per floor, as area_stats gives them. A figure it left out stays out.</summary>
    static void AddAreas(Result result, AreaStats.Result area, bool norwegian)
    {
        if (area == null) return;
        var areas = Group(Areas, "Arealer", norwegian);
        foreach (var floor in area.Floors)
        {
            var level = string.IsNullOrWhiteSpace(floor.Key) ? "0" : floor.Key.Trim();
            var name = AreaStats.FloorName(level, norwegian);
            var gross = area.Gross?.Find(g => g != null && (string.IsNullOrWhiteSpace(g.Level) ? "0" : g.Level.Trim()) == level);
            void Add(string what, double mm2) =>
                result.Lines.Add(new Line { Group = areas, Label = name + " " + what, Name = name + " " + what, AreaM2 = mm2 / 1e6 });
            if (gross?.BtaMm2 != null) Add(SheetLang.Pick(norwegian, "Gross area (BTA)", "BTA"), gross.BtaMm2.Value);
            if (gross?.BraMm2 != null) Add(SheetLang.Pick(norwegian, "Usable area (BRA)", "BRA"), gross.BraMm2.Value);
            Add(SheetLang.Pick(norwegian, "Net area", "Netto"), floor.AreaMm2);
        }
    }

    /// <summary>One line for the chat: the new walls, the slabs and roof, the doors and windows. Not the existing.</summary>
    static string Summarize(Result result, bool norwegian)
    {
        var parts = new List<string>();
        string Named(string english, string bokmal) => Group(english, bokmal, norwegian);
        double Sum(string group, Func<Line, double?> figure) => result.Lines.Where(l => l.Group == group).Sum(l => figure(l) ?? 0);
        var outer = Named(Outer, "Yttervegger");
        var inner = Named(Inner, "Innervegger");
        var slabs = Named(Slabs, "Dekker");
        var roofs = Named(Roofs, "Tak");
        var doorsGroup = Named(Doors, "Dører");
        var windowsGroup = Named(Windows, "Vinduer");
        if (result.Lines.Any(l => l.Group == outer)) parts.Add(SheetLang.Pick(norwegian, "exterior walls ", "yttervegger ") + Number(Sum(outer, l => l.LengthM)) + " m");
        if (result.Lines.Any(l => l.Group == inner)) parts.Add(SheetLang.Pick(norwegian, "interior walls ", "innervegger ") + Number(Sum(inner, l => l.LengthM)) + " m");
        if (result.Lines.Any(l => l.Group == slabs)) parts.Add(SheetLang.Pick(norwegian, "slabs ", "dekker ") + Number(Sum(slabs, l => l.AreaM2)) + " m²");
        if (result.Lines.Any(l => l.Group == roofs)) parts.Add(SheetLang.Pick(norwegian, "roof ", "tak ") + Number(Sum(roofs, l => l.AreaM2)) + " m²");
        var doors = (int)Sum(doorsGroup, l => l.Count);
        var windows = (int)Sum(windowsGroup, l => l.Count);
        var stairs = (int)Sum(Named(StairsGroup, "Trapper"), l => l.Count);
        if (doors > 0) parts.Add(doors.ToString(CultureInfo.InvariantCulture) + (doors == 1 ? SheetLang.Pick(norwegian, " door", " dør") : SheetLang.Pick(norwegian, " doors", " dører")));
        if (windows > 0) parts.Add(windows.ToString(CultureInfo.InvariantCulture) + (windows == 1 ? SheetLang.Pick(norwegian, " window", " vindu") : SheetLang.Pick(norwegian, " windows", " vinduer")));
        if (stairs > 0) parts.Add(stairs.ToString(CultureInfo.InvariantCulture) + (stairs == 1 ? SheetLang.Pick(norwegian, " stair", " trapp") : SheetLang.Pick(norwegian, " stairs", " trapper")));
        if (parts.Count == 0) return SheetLang.Pick(norwegian, "No quantities: the model has no walls.", "Ingen mengder: modellen har ingen vegger.");
        return SheetLang.Pick(norwegian, "Quantities, approx.: ", "Mengder, ca.: ") + string.Join(", ", parts) + ".";
    }

    static string Mm(double value)
    {
        return Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }
}
