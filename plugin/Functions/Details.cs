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
