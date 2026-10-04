using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Detail drawings of picked walls, doors and windows. Each detail is a
/// reference stored in the document string forsk/details as
/// [{id:"DET01", wall:"w03"}, {id:"DET02", opening:"…"}], one per element,
/// and drawn fresh at every Print from the records as they are then. Numbers
/// on paper are computed, never stored. A record whose element is gone
/// drops at Print, and the receipt says so. Pure: the model as IfcExport
/// reads it in, the element's facts out.
/// </summary>
public static class Details
{
    public const string Section = "forsk";
    public const string Entry = "details";
    /// <summary>The removed Add dimensions' chains. A file that still has them loses them at its next Print.</summary>
    public const string RetiredEntry = "user_dims";
    public const string NeedsPick = "Pick walls, doors or windows, then Add detail.";

    public const string Plan = "plan";
    public const string Elevation = "elevation";
    public const string Cut = "section";

    public sealed class Ref
    {
        public string Wall;
        public string Opening;
    }

    public sealed class Record
    {
        public string Id;
        public string Wall;
        public string Opening;
    }

    /// <summary>An opening on the detailed run: the record and its centre along the run's Dir.</summary>
    public sealed class Hosted
    {
        public IfcExport.Opening Opening;
        public double U;
    }

    /// <summary>
    /// What a detail draws, read from the live records. Run is the wall's
    /// main run, or the opening's host run, in its cluster's shape (Shape),
    /// so its faces end where the joined walls' faces do. Outer is +1 when
    /// the face at Far (the Normal side) is the outside, -1 when Near is; an
    /// inner or lone wall takes its Normal side (Exterior false).
    /// </summary>
    public sealed class Facts
    {
        public Record Record;
        /// <summary>wall, door or window.</summary>
        public string Kind;
        public IfcExport.Wall Wall;
        public List<List<Pt>> Shape;
        public WallEdit.Run Run;
        /// <summary>The run's name in the join graph: the north wall, the wall at (x, y).</summary>
        public string RunName;
        public double Thickness;
        public double Height;
        public double Base;
        public double FloorTop;
        /// <summary>The underside of the slab under the run's middle; the floor top when there is none.</summary>
        public double SlabBottom;
        /// <summary>The top of a roof that sits on the wall, over the run's middle. Null when none does.</summary>
        public double? RoofTop;
        public double WallTop => Base + Height;
        public int Outer = 1;
        public bool Exterior;
        /// <summary>The openings on the run, by U.</summary>
        public List<Hosted> Openings = new List<Hosted>();
        /// <summary>The detailed opening, for a door or window.</summary>
        public Hosted Opening;
        public bool IsWall => Kind == "wall";
    }

    public static List<Record> Read(string json)
    {
        var records = new List<Record>();
        if (string.IsNullOrWhiteSpace(json)) return records;
        JArray array;
        try { array = JArray.Parse(json); }
        catch (JsonException) { return records; }
        foreach (var item in array.OfType<JObject>())
        {
            var id = item["id"]?.ToString();
            var wall = item["wall"]?.ToString();
            var opening = item["opening"]?.ToString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!string.IsNullOrWhiteSpace(wall)) records.Add(new Record { Id = id.Trim(), Wall = wall.Trim() });
            else if (!string.IsNullOrWhiteSpace(opening)) records.Add(new Record { Id = id.Trim(), Opening = opening.Trim() });
        }
        return records;
    }

    public static string Write(IEnumerable<Record> records)
    {
        var array = new JArray();
        foreach (var record in records ?? Enumerable.Empty<Record>())
        {
            var item = new JObject { ["id"] = record.Id };
            if (record.Wall != null) item["wall"] = record.Wall;
            else item["opening"] = record.Opening;
            array.Add(item);
        }
        return array.ToString(Formatting.None);
    }

    /// <summary>DET01, DET02, …: one past the highest stored.</summary>
    public static string NextId(IEnumerable<Record> records)
    {
        var top = 0;
        foreach (var record in records ?? Enumerable.Empty<Record>())
            if (record.Id != null && record.Id.StartsWith("DET", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(record.Id.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                top = Math.Max(top, n);
        return "DET" + (top + 1).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>The records with one more per element not detailed yet; an element that has one counts as already there.</summary>
    public static List<Record> Add(IEnumerable<Record> records, IEnumerable<Ref> refs, out int added, out int already)
    {
        var list = new List<Record>(records ?? Enumerable.Empty<Record>());
        added = 0;
        already = 0;
        foreach (var r in refs ?? Enumerable.Empty<Ref>())
        {
            if (r == null || (r.Wall == null && r.Opening == null)) continue;
            if (list.Any(d => Same(d, r)))
            {
                already++;
                continue;
            }
            list.Add(new Record { Id = NextId(list), Wall = r.Wall, Opening = r.Wall == null ? r.Opening : null });
            added++;
        }
        return list;
    }

    /// <summary>The records left once the ids given go; null ids remove every one.</summary>
    public static List<Record> Remove(IEnumerable<Record> records, IEnumerable<string> ids)
    {
        if (ids == null) return new List<Record>();
        var wanted = new HashSet<string>(ids.Where(id => id != null).Select(id => id.Trim()), StringComparer.OrdinalIgnoreCase);
        return (records ?? Enumerable.Empty<Record>()).Where(d => !wanted.Contains(d.Id)).ToList();
    }

    /// <summary>The drawings of a detail: a wall's plan and section; an opening's plan, elevation and section.</summary>
    public static List<string> Views(Record record) =>
        record?.Wall != null ? new List<string> { Plan, Cut } : new List<string> { Plan, Elevation, Cut };

    /// <summary>
    /// The element's facts from the model as it is now, or null when the
    /// element is gone (a deleted wall, a split one whose old id is gone, a
    /// removed opening or its host).
    /// </summary>
    public static Facts Resolve(Record record, IfcExport.Model model, double tol)
    {
        if (record == null || model == null) return null;
        var walls = model.Walls.Where(w => !w.Existing && w.Rings != null && w.Rings.Count > 0).ToList();
        if (record.Wall != null)
        {
            var wall = walls.FirstOrDefault(w => SameId(w.Id, record.Wall));
            if (wall == null) return null;
            var facts = Host(record, "wall", wall, walls, null, model, tol);
            if (facts == null) return null;
            foreach (var opening in model.Openings.Where(o => SameId(o.Host, wall.Id)))
            {
                if (!OnRun(facts.Run, opening.Centre, tol)) continue;
                facts.Openings.Add(new Hosted { Opening = opening, U = Dot(opening.Centre, facts.Run.Dir) });
            }
            facts.Openings.Sort((a, b) => a.U.CompareTo(b.U));
            return facts;
        }
        var detailed = model.Openings.FirstOrDefault(o => SameId(o.Id, record.Opening));
        if (detailed == null) return null;
        var host = walls.FirstOrDefault(w => SameId(w.Id, detailed.Host));
        if (host == null) return null;
        var kind = detailed.Kind == "door" ? "door" : "window";
        var result = Host(record, kind, host, walls, detailed.Centre, model, tol);
        if (result == null) return null;
        foreach (var opening in model.Openings.Where(o => SameId(o.Host, host.Id)))
        {
            if (!OnRun(result.Run, opening.Centre, tol)) continue;
            var hosted = new Hosted { Opening = opening, U = Dot(opening.Centre, result.Run.Dir) };
            result.Openings.Add(hosted);
            if (opening == detailed) result.Opening = hosted;
        }
        result.Openings.Sort((a, b) => a.U.CompareTo(b.U));
        return result.Opening == null ? null : result;
    }

    /// <summary>
    /// A detail's name, as the pick line names its element: "North wall",
    /// "Wall at (4000, 2000)", "Door D01", "Window W01".
    /// </summary>
    public static string Name(Facts facts)
    {
        if (facts == null) return "";
        if (facts.IsWall)
        {
            var run = facts.RunName ?? "";
            const string the = "the ";
            if (run.StartsWith(the, StringComparison.Ordinal) && run.Length > the.Length)
                return char.ToUpperInvariant(run[the.Length]) + run.Substring(the.Length + 1);
            return "Wall " + (facts.Wall?.Id ?? "").ToUpperInvariant();
        }
        var mark = facts.Opening?.Opening?.Mark;
        var noun = facts.Kind == "door" ? "Door" : "Window";
        return string.IsNullOrWhiteSpace(mark) ? noun : noun + " " + mark.Trim();
    }

    /// <summary>Every detail's name, a wall's with its id in brackets when another shares the name: "North wall (W03)".</summary>
    public static List<string> Names(IList<Facts> facts)
    {
        var plain = (facts ?? new List<Facts>()).Select(Name).ToList();
        var names = new List<string>(plain);
        for (var i = 0; i < names.Count; i++)
            if (facts[i].IsWall && plain.Count(n => n == plain[i]) > 1)
                names[i] += " (" + (facts[i].Wall?.Id ?? "").ToUpperInvariant() + ")";
        return names;
    }

    /// <summary>A companion mark inside a drawing: which drawing it points to, where it sits and which way it looks, in frame mm.</summary>
    public sealed class Mark
    {
        public string View;
        public Pt At;
        public Pt Look;
    }

    /// <summary>
    /// One detail drawing in frame coordinates (model mm): u along X (the
    /// paper's x), and v along Left(X) on a plan or up (z) on a vertical
    /// drawing. A plan is cut at CutZ and seen from above. A vertical
    /// drawing looks along Look = Left(X); a section keeps what lies at or
    /// beyond Dot(p, Look) = Depth, an elevation is not cut. The crop is
    /// U0..U1 by V0..V1.
    /// </summary>
    public sealed class Drawing
    {
        public Facts Facts;
        public string View;
        public Pt X;
        public bool Vertical;
        public double U0, U1, V0, V1;
        public double CutZ;
        public double Depth;
        /// <summary>An elevation draws only what meets this band along its look (its host wall). Null: everything in the crop.</summary>
        public double? DepthLo, DepthHi;
        public bool Clipped;
        public string Title;
        public List<Mark> Marks = new List<Mark>();
        /// <summary>A long wall's plan breaks (D2b). Null when the drawing is whole.</summary>
        public DetailBreaks.Plan Breaks;
        public Pt Y => Vertical ? new Pt(0, 0) : new Pt(-X.Y, X.X);
        public Pt Look => Vertical ? new Pt(-X.Y, X.X) : new Pt(0, 0);
        /// <summary>A true u where it is drawn: past the breaks before it.</summary>
        public double Map(double u) => Breaks == null ? u : Breaks.Map(u);
        public double Width => Breaks == null ? U1 - U0 : Breaks.DrawnLength;
        public double Height => V1 - V0;
    }

    /// <summary>Crop margin around a wall's plan detail, past its ends and both faces.</summary>
    public const double WallMarginMm = 300;
    /// <summary>Crop margin along a wall past an opening's jambs.</summary>
    public const double OpeningAlongMm = 600;
    /// <summary>Crop margin past both faces of an opening's wall, and of every section.</summary>
    public const double AcrossMm = 400;
    /// <summary>Paper band kept for dimensions on every side of a drawing, and for its title below.</summary>
    public const double BandMm = 15;
    public const double TitleBandMm = 12;
    /// <summary>The sheet's detail area (A3 inside the margins and above the footer).</summary>
    public const double AreaWidthMm = 400;
    public const double AreaHeightMm = 254;
    /// <summary>The scales a detail takes, finest first: a subset of SheetScale.Ladder.</summary>
    public static readonly IReadOnlyList<int> Ladder = new[] { 5, 10, 20, 25, 50 };

    /// <summary>The paper a drawing may take: the detail area less the bands.</summary>
    public static double DrawWidthMm => AreaWidthMm - 2 * BandMm;
    public static double DrawHeightMm => AreaHeightMm - 2 * BandMm - TitleBandMm;

    /// <summary>
    /// The finest detail scale at which every span (model mm) fits the paper
    /// a drawing may take. The coarsest step when none does.
    /// </summary>
    public static int Scale(IEnumerable<SheetScale.Span> spans)
    {
        var list = (spans ?? Enumerable.Empty<SheetScale.Span>()).ToList();
        foreach (var step in Ladder)
            if (list.All(s => s.Width / step <= DrawWidthMm + 1e-9 && s.Height / step <= DrawHeightMm + 1e-9))
                return step;
        return Ladder[Ladder.Count - 1];
    }

    /// <summary>The paper x of a plan detail: the run's Normal turned a quarter clockwise, so the Normal is up.</summary>
    public static Pt PlanX(WallEdit.Run run) => new Pt(run.Normal.Y, -run.Normal.X);

    /// <summary>A distance along the run as u along X.</summary>
    public static double U(Facts facts, double along) => Dot(PlanX(facts.Run), facts.Run.Dir) >= 0 ? along : -along;

    /// <summary>The outer face's v (along the Normal), and the inner one's.</summary>
    public static double OuterV(Facts facts) => facts.Outer > 0 ? facts.Run.Far : facts.Run.Near;
    public static double InnerV(Facts facts) => facts.Outer > 0 ? facts.Run.Near : facts.Run.Far;

    /// <summary>
    /// Each face's extent along the run: from its own edges in the cluster's
    /// shape, so the inner face stops where a joined wall's face starts.
    /// </summary>
    public static void FaceExtent(Facts facts, bool outer, out double lo, out double hi)
    {
        var run = facts.Run;
        var v = outer ? OuterV(facts) : InnerV(facts);
        lo = double.MaxValue;
        hi = double.MinValue;
        foreach (var (loop, edge) in run.Edges)
        {
            if (facts.Shape == null || loop >= facts.Shape.Count) continue;
            var ring = facts.Shape[loop];
            var a = ring[edge];
            var b = ring[(edge + 1) % ring.Count];
            if (Math.Abs(Dot(a, run.Normal) - v) > 1.0 || Math.Abs(Dot(b, run.Normal) - v) > 1.0) continue;
            lo = Math.Min(lo, Math.Min(Dot(a, run.Dir), Dot(b, run.Dir)));
            hi = Math.Max(hi, Math.Max(Dot(a, run.Dir), Dot(b, run.Dir)));
        }
        if (lo > hi)
        {
            lo = run.Lo;
            hi = run.Hi;
        }
    }

    /// <summary>
    /// Where a wall is cut for its section: the middle of its longest stretch
    /// clear of openings, along the run.
    /// </summary>
    public static double SectionAlong(Facts facts)
    {
        var run = facts.Run;
        var cuts = new List<KeyValuePair<double, double>>();
        foreach (var hosted in facts.Openings)
            cuts.Add(new KeyValuePair<double, double>(hosted.U - hosted.Opening.Width / 2.0, hosted.U + hosted.Opening.Width / 2.0));
        cuts.Sort((a, b) => a.Key.CompareTo(b.Key));
        double from = run.Lo, bestLo = run.Lo, bestHi = run.Lo;
        foreach (var cut in cuts.Concat(new[] { new KeyValuePair<double, double>(run.Hi, run.Hi) }))
        {
            if (cut.Key - from > bestHi - bestLo)
            {
                bestLo = from;
                bestHi = cut.Key;
            }
            from = Math.Max(from, cut.Value);
        }
        return (bestLo + bestHi) / 2.0;
    }

    /// <summary>
    /// A detail drawing's frame: its crop, cut, look, companion marks and
    /// title. A wall's plan crop runs WallMarginMm past its ends and faces;
    /// an opening's is its width plus OpeningAlongMm either side, by the wall
    /// plus AcrossMm past each face. Null for a view the detail has not.
    /// </summary>
    public static Drawing Frame(Facts facts, string view, string name = null)
    {
        if (facts?.Run == null || !Views(facts.Record).Contains(view)) return null;
        var run = facts.Run;
        var title = (name ?? Name(facts)) + " — " + char.ToUpperInvariant(view[0]) + view.Substring(1);
        if (view != Plan) return Vertical(facts, view, title);
        var x = PlanX(run);
        var drawing = new Drawing
        {
            Facts = facts,
            View = Plan,
            X = x,
            CutZ = facts.FloorTop + ForskPlanCut.AboveFloorMm,
            Clipped = true,
            Title = title
        };
        double a, b, across;
        if (facts.IsWall)
        {
            a = run.Lo - WallMarginMm;
            b = run.Hi + WallMarginMm;
            across = WallMarginMm;
            drawing.Breaks = WallBreaks(facts, Math.Min(U(facts, a), U(facts, b)), Math.Max(U(facts, a), U(facts, b)));
            drawing.Marks.Add(new Mark
            {
                View = Cut,
                At = new Pt(CutU(facts, drawing.Breaks), OuterV(facts) + facts.Outer * WallMarginMm / 2.0),
                Look = new Pt(1, 0)
            });
        }
        else
        {
            var half = facts.Opening.Opening.Width / 2.0 + OpeningAlongMm;
            a = facts.Opening.U - half;
            b = facts.Opening.U + half;
            across = AcrossMm;
            var u = U(facts, facts.Opening.U);
            var outside = OuterV(facts) + facts.Outer * AcrossMm / 2.0;
            drawing.Marks.Add(new Mark { View = Elevation, At = new Pt(u, outside), Look = new Pt(0, -facts.Outer) });
            drawing.Marks.Add(new Mark { View = Cut, At = new Pt(u, InnerV(facts) - facts.Outer * AcrossMm / 2.0), Look = new Pt(1, 0) });
        }
        drawing.U0 = Math.Min(U(facts, a), U(facts, b));
        drawing.U1 = Math.Max(U(facts, a), U(facts, b));
        drawing.V0 = run.Near - across;
        drawing.V1 = run.Far + across;
        return drawing;
    }

    /// <summary>Every drawing a detail has, in view order: plan, elevation, section.</summary>
    public static List<Drawing> Drawings(Facts facts, string name = null) =>
        facts == null
            ? new List<Drawing>()
            : Views(facts.Record).Select(v => Frame(facts, v, name)).Where(d => d != null).ToList();

    /// <summary>A detail's scale: the finest at which every one of its drawings fits.</summary>
    public static int ScaleOf(IEnumerable<Drawing> drawings) =>
        Scale((drawings ?? Enumerable.Empty<Drawing>()).Select(d => new SheetScale.Span(d.Width, d.Height)));

    public const string SheetPrefix = "detail_";

    /// <summary>A detail sheet's view id: detail_20_1 is the first sheet at 1:20.</summary>
    public static string SheetId(int scale, int n) =>
        SheetPrefix + scale.ToString(CultureInfo.InvariantCulture) + "_" + n.ToString(CultureInfo.InvariantCulture);

    public static bool TrySheetId(string view, out int scale, out int n)
    {
        scale = 0;
        n = 0;
        var key = (view ?? "").Trim().ToLowerInvariant();
        if (!key.StartsWith(SheetPrefix, StringComparison.Ordinal)) return false;
        var parts = key.Substring(SheetPrefix.Length).Split('_');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out scale)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out n)
            && Ladder.Contains(scale) && n >= 1;
    }

    /// <summary>"Details 1:20", "Detaljer 1:20".</summary>
    public static string SheetTitle(int scale, bool norwegian = false) =>
        SheetLang.Pick(norwegian, "Details 1:", "Detaljer 1:") + scale.ToString(CultureInfo.InvariantCulture);

    /// <summary>The page: "Forsk — Details 1:20", then " (2)" for the next sheet at that scale.</summary>
    public static string PageName(int scale, int n) =>
        "Forsk — " + SheetTitle(scale) + (n > 1 ? " (" + n.ToString(CultureInfo.InvariantCulture) + ")" : "");

    /// <summary>The detail sheet a page name is, the other way from PageName.</summary>
    public static bool TryPage(string pageName, out int scale, out int n)
    {
        scale = 0;
        n = 1;
        var prefix = "Forsk — " + SheetTitle(0).TrimEnd('0');
        var name = (pageName ?? "").Trim();
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = name.Substring(prefix.Length);
        var space = rest.IndexOf(' ');
        var number = space < 0 ? rest : rest.Substring(0, space);
        if (space >= 0)
        {
            var tail = rest.Substring(space + 1);
            if (tail.Length < 3 || tail[0] != '(' || tail[tail.Length - 1] != ')'
                || !int.TryParse(tail.Substring(1, tail.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out n))
                return false;
        }
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out scale)
            && Ladder.Contains(scale) && n >= 1 && PageName(scale, n) == name;
    }

    /// <summary>The S-DRAW child a detail sheet draws on: "Details 20-1".</summary>
    public static string LayerName(int scale, int n) =>
        "Details " + scale.ToString(CultureInfo.InvariantCulture) + "-" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Where a detail sheet's drawings live in the model: each sheet its own
    /// region, wide enough for the detail area at 1:50.
    /// </summary>
    public static Pt SheetOrigin(int scale, int n)
    {
        var k = Math.Max(0, IndexOfScale(scale)) * 20 + Math.Max(0, n - 1);
        return new Pt(25000.0 * k, -60000.0);
    }

    static int IndexOfScale(int scale)
    {
        for (var i = 0; i < Ladder.Count; i++)
            if (Ladder[i] == scale) return i;
        return -1;
    }

    /// <summary>A drawing on its sheet: its crop's lower left at paper X, Y (mm) in the detail area.</summary>
    public sealed class Placed
    {
        public Drawing Drawing;
        public int Number;
        public double X;
        public double Y;
    }

    /// <summary>
    /// A wall plan's breaks, when its crop lo..hi (u) is longer than 1:20
    /// allows: kept around both faces' ends and every jamb. Null when none.
    /// </summary>
    public static DetailBreaks.Plan WallBreaks(Facts facts, double lo, double hi)
    {
        var stops = new List<double>();
        foreach (var outer in new[] { true, false })
        {
            FaceExtent(facts, outer, out var a, out var b);
            stops.Add(U(facts, a));
            stops.Add(U(facts, b));
        }
        foreach (var hosted in facts.Openings)
        {
            stops.Add(U(facts, hosted.U - hosted.Opening.Width / 2.0));
            stops.Add(U(facts, hosted.U + hosted.Opening.Width / 2.0));
        }
        var plan = DetailBreaks.Make(stops, lo, hi, DrawWidthMm * DetailBreaks.Scale);
        return plan.Breaks.Count > 0 ? plan : null;
    }

    /// <summary>
    /// Where a wall's section cuts it, as u in its plan: beside the largest
    /// break (its u0) when the plan breaks, else the middle of its longest
    /// stretch clear of openings.
    /// </summary>
    public static double CutU(Facts facts, DetailBreaks.Plan breaks)
    {
        if (breaks == null || breaks.Breaks.Count == 0) return U(facts, SectionAlong(facts));
        return breaks.Breaks.OrderByDescending(b => b.U1 - b.U0).ThenBy(b => b.U0).First().U0;
    }

    /// <summary>Paper below the slab and above the top a section's crop keeps, and below the floor in an elevation.</summary>
    public const double BelowMm = 200;
    /// <summary>An elevation's crop above the head.</summary>
    public const double AboveHeadMm = 400;

    /// <summary>
    /// A vertical drawing. A section cuts across the run (its plane's normal
    /// the run's Dir, looking along it): a wall's beside its largest plan
    /// break or in its longest plain stretch, an opening's through its
    /// centre; it crops thickness + 2 × AcrossMm by the slab's underside −
    /// BelowMm to the wall or roof top + BelowMm. An elevation looks at the
    /// host's outer face (the Normal side for an inner wall), is not cut,
    /// crops the opening ± (width/2 + OpeningAlongMm) by the floor − BelowMm
    /// to the head + AboveHeadMm, and draws only the host wall's band.
    /// </summary>
    static Drawing Vertical(Facts facts, string view, string title)
    {
        var run = facts.Run;
        var elevation = view == Elevation;
        if (elevation && facts.Opening == null) return null;
        var look = elevation ? new Pt(-facts.Outer * run.Normal.X, -facts.Outer * run.Normal.Y) : run.Dir;
        var x = new Pt(look.Y, -look.X);
        var drawing = new Drawing { Facts = facts, View = view, X = x, Vertical = true, Clipped = !elevation, Title = title };
        double UOf(double along, double across) => Dot(At(run, along, across), x);
        double DepthOf(double along, double across) => Dot(At(run, along, across), look);
        if (elevation)
        {
            var o = facts.Opening.Opening;
            var half = o.Width / 2.0 + OpeningAlongMm;
            var a = UOf(facts.Opening.U - half, OuterV(facts));
            var b = UOf(facts.Opening.U + half, OuterV(facts));
            drawing.U0 = Math.Min(a, b);
            drawing.U1 = Math.Max(a, b);
            drawing.V0 = facts.FloorTop - BelowMm;
            drawing.V1 = o.Head + AboveHeadMm;
            var near = DepthOf(facts.Opening.U, run.Near);
            var far = DepthOf(facts.Opening.U, run.Far);
            drawing.DepthLo = Math.Min(near, far) - 1.0;
            drawing.DepthHi = Math.Max(near, far) + 1.0;
            return drawing;
        }
        var along = facts.IsWall ? U(facts, CutU(facts, Frame(facts, Plan)?.Breaks)) : facts.Opening.U;
        drawing.Depth = DepthOf(along, 0);
        var u0 = UOf(along, run.Near - AcrossMm);
        var u1 = UOf(along, run.Far + AcrossMm);
        drawing.U0 = Math.Min(u0, u1);
        drawing.U1 = Math.Max(u0, u1);
        drawing.V0 = facts.SlabBottom - BelowMm;
        drawing.V1 = (facts.RoofTop ?? facts.WallTop) + BelowMm;
        return drawing;
    }

    /// <summary>A face's u in a vertical drawing across the run: across × the Normal's share of X.</summary>
    public static double FaceU(Facts facts, Drawing drawing, double across) =>
        across * Dot(facts.Run.Normal, drawing.X);

    /// <summary>A point along the run (at its outer face) as u in a vertical drawing along it.</summary>
    public static double AlongU(Facts facts, Drawing drawing, double along) =>
        Dot(At(facts.Run, along, OuterV(facts)), drawing.X);

    /// <summary>The Print receipt's clause for details that dropped. Empty for none.</summary>
    public static string DroppedLine(int count)
    {
        if (count <= 0) return "";
        return count == 1
            ? "1 detail dropped: its wall or opening is gone."
            : count.ToString(CultureInfo.InvariantCulture) + " details dropped: their walls or openings are gone.";
    }

    /// <summary>
    /// The run a detail is about, in its cluster's shape: for a wall, the run
    /// whose middle lies in the record (else the record's longest); for an
    /// opening, the run whose band holds its centre.
    /// </summary>
    static Facts Host(Record record, string kind, IfcExport.Wall wall, List<IfcExport.Wall> walls, Pt? at, IfcExport.Model model, double tol)
    {
        var records = walls.Select(w => w.Rings).ToList();
        var index = walls.IndexOf(wall);
        var graph = WallJoins.Build(records, WallJoins.ClusterOf(records, index, tol), tol);
        var shape = graph?.Shape ?? wall.Rings;
        var runs = graph?.Runs ?? WallJoins.Runs(wall.Rings, tol);
        var names = graph?.Names;
        var pick = -1;
        if (at.HasValue)
        {
            for (var i = 0; i < runs.Count && pick < 0; i++)
                if (OnRun(runs[i], at.Value, tol)) pick = i;
        }
        else
        {
            pick = graph != null ? WallJoins.RunIn(graph, wall.Rings) : -1;
            if (pick < 0)
            {
                var best = -1.0;
                for (var i = 0; i < runs.Count; i++)
                {
                    if (!WallEdit.InRegion(wall.Rings, WallJoins.Middle(runs[i])) || runs[i].Length <= best) continue;
                    best = runs[i].Length;
                    pick = i;
                }
            }
        }
        WallEdit.Run run;
        if (pick >= 0) run = runs[pick];
        else
        {
            run = WallJoins.MainRun(wall.Rings, tol);
            if (run == null) return null;
        }
        var facts = new Facts
        {
            Record = record,
            Kind = kind,
            Wall = wall,
            Shape = shape,
            Run = run,
            RunName = pick >= 0 && names != null && pick < names.Count ? names[pick] : null,
            Thickness = run.Thickness,
            Height = wall.Height,
            Base = wall.Base,
            FloorTop = model.FloorTop
        };
        OuterSide(facts, WallJoins.Outlines(records, tol), tol);
        var middle = At(run, (run.Lo + run.Hi) / 2.0, (run.Near + run.Far) / 2.0);
        var under = model.Slabs.Where(sl => sl.Rings != null && sl.Rings.Any(r => RoomDetect.Contains(r, middle))).ToList();
        facts.SlabBottom = under.Count > 0 ? under.Min(sl => sl.Base) : model.FloorTop;
        var roofs = model.Roofs.Where(r => r.Rings != null && Math.Abs(r.Base - facts.WallTop) <= Math.Max(tol, 1.0)
            && r.Rings.Any(ring => RoomDetect.Contains(ring, middle))).ToList();
        facts.RoofTop = roofs.Count > 0 ? roofs.Max(r => r.Base + r.Thickness) : (double?)null;
        return facts;
    }

    /// <summary>A step past each face at the run's middle: the one outside every facade outline is the outer face.</summary>
    static void OuterSide(Facts facts, List<List<Pt>> outlines, double tol)
    {
        var run = facts.Run;
        var s = (run.Lo + run.Hi) / 2.0;
        var step = Math.Max(tol, 1.0);
        bool Outside(double across) => !outlines.Any(ring => RoomDetect.Contains(ring, At(run, s, across)));
        var far = Outside(run.Far + step);
        var near = Outside(run.Near - step);
        facts.Exterior = far != near;
        facts.Outer = near && !far ? -1 : 1;
    }

    /// <summary>The point lies in the run's band: between its faces, within its length.</summary>
    static bool OnRun(WallEdit.Run run, Pt p, double tol)
    {
        var across = Dot(p, run.Normal);
        var along = Dot(p, run.Dir);
        return across >= run.Near - tol && across <= run.Far + tol && along >= run.Lo - tol && along <= run.Hi + tol;
    }

    static Pt At(WallEdit.Run run, double along, double across) =>
        new Pt(run.Dir.X * along + run.Normal.X * across, run.Dir.Y * along + run.Normal.Y * across);

    static bool Same(Record record, Ref r) =>
        r.Wall != null ? SameId(record.Wall, r.Wall) : SameId(record.Opening, r.Opening);

    static bool SameId(string a, string b) =>
        a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
}
