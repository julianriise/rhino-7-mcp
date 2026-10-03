using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 R2: dimensions the user asked for. Each is stored by Forsk ids only, in
/// the document string forsk/user_dims as [{id:"U01", refs:[{wall:"w03"},
/// {opening:"…"}]}], and resolved at every Print from the live walls and
/// openings, so a moved wall carries its dimension. The chain runs across
/// parallel walls, along their normal, with a stop at both faces of each
/// wall, and at the centre of a picked opening whose host runs along the
/// chain. A ref that is gone drops its stops; with fewer than two refs or two
/// stops left, the dimension drops. Pure: wall rings and opening centres in,
/// a PlanDims.UserChain out.
/// </summary>
public static class UserDims
{
    public const string Section = "forsk";
    public const string Entry = "user_dims";
    /// <summary>Under 15° (sine) apart, two runs are one direction, as in F2.</summary>
    public const double ParallelSin = 0.26;
    public const string NotParallel = "Pick parallel walls to dimension across them.";

    public sealed class Ref
    {
        public string Wall;
        public string Opening;
    }

    public sealed class Dim
    {
        public string Id;
        public List<Ref> Refs = new List<Ref>();
    }

    /// <summary>An opening as the plan has it: its centre on the wall's centreline and the unit along its wall.</summary>
    public sealed class OpeningAt
    {
        public Pt Centre;
        public Pt Along;
    }

    /// <summary>What a resolve could not use: refs that are gone, and openings whose host crosses the chain.</summary>
    public sealed class Gone
    {
        public int Walls;
        public int Openings;
        public int Skipped;
        public bool Dropped;
    }

    public static List<Dim> Read(string json)
    {
        var dims = new List<Dim>();
        if (string.IsNullOrWhiteSpace(json)) return dims;
        JArray array;
        try { array = JArray.Parse(json); }
        catch (JsonException) { return dims; }
        foreach (var item in array.OfType<JObject>())
        {
            var id = item["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var dim = new Dim { Id = id.Trim() };
            foreach (var r in item["refs"] as JArray ?? new JArray())
            {
                var wall = r["wall"]?.ToString();
                var opening = r["opening"]?.ToString();
                if (!string.IsNullOrWhiteSpace(wall)) dim.Refs.Add(new Ref { Wall = wall.Trim() });
                else if (!string.IsNullOrWhiteSpace(opening)) dim.Refs.Add(new Ref { Opening = opening.Trim() });
            }
            dims.Add(dim);
        }
        return dims;
    }

    public static string Write(IEnumerable<Dim> dims)
    {
        var array = new JArray();
        foreach (var dim in dims ?? Enumerable.Empty<Dim>())
        {
            var refs = new JArray();
            foreach (var r in dim.Refs)
                refs.Add(r.Wall != null ? new JObject { ["wall"] = r.Wall } : new JObject { ["opening"] = r.Opening });
            array.Add(new JObject { ["id"] = dim.Id, ["refs"] = refs });
        }
        return array.ToString(Formatting.None);
    }

    /// <summary>U01, U02, …: one past the highest stored.</summary>
    public static string NextId(IEnumerable<Dim> dims)
    {
        var top = 0;
        foreach (var dim in dims ?? Enumerable.Empty<Dim>())
            if (dim.Id != null && dim.Id.Length > 1 && dim.Id[0] == 'U'
                && int.TryParse(dim.Id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                top = Math.Max(top, n);
        return "U" + (top + 1).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Null when the walls run one way; else the sentence that refuses.</summary>
    public static string Check(IEnumerable<List<List<Pt>>> walls, double tol)
    {
        var runs = (walls ?? Enumerable.Empty<List<List<Pt>>>()).Select(w => MainRun(w, tol)).ToList();
        if (runs.Count == 0 || runs.Any(r => r == null)) return NotParallel;
        return runs.All(r => Math.Abs(Cross(r.Dir, runs[0].Dir)) < ParallelSin) ? null : NotParallel;
    }

    /// <summary>
    /// The chain for one stored dimension, from the walls (forsk:id to rings)
    /// and openings (marker id to centre) as they are now. Null when it drops.
    /// </summary>
    public static PlanDims.UserChain Resolve(
        Dim dim, IDictionary<string, List<List<Pt>>> walls, IDictionary<string, OpeningAt> openings, double tol, out Gone gone)
    {
        gone = new Gone();
        var runs = new List<WallEdit.Run>();
        var refs = 0;
        foreach (var r in dim?.Refs ?? new List<Ref>())
        {
            if (r.Wall == null) continue;
            var run = walls != null && walls.TryGetValue(r.Wall, out var rings) ? MainRun(rings, tol) : null;
            if (run == null)
            {
                gone.Walls++;
                continue;
            }
            // A wall turned off the others' line since it was picked no longer belongs on this chain.
            if (runs.Count > 0 && Math.Abs(Cross(run.Dir, runs[0].Dir)) >= ParallelSin)
            {
                gone.Walls++;
                continue;
            }
            runs.Add(run);
            refs++;
        }
        if (runs.Count == 0)
        {
            gone.Dropped = true;
            return null;
        }

        var n = runs[0].Normal;
        var w = runs[0].Dir;
        var stops = new List<KeyValuePair<double, string>>();
        double lo = double.MinValue, hi = double.MaxValue, min = double.MaxValue, max = double.MinValue;
        foreach (var run in runs)
        {
            var flip = Dot(run.Normal, n) < 0 ? -1.0 : 1.0;
            stops.Add(new KeyValuePair<double, string>(flip * run.Near, null));
            stops.Add(new KeyValuePair<double, string>(flip * run.Far, null));
            var turn = Dot(run.Dir, w) < 0 ? -1.0 : 1.0;
            var a = Math.Min(turn * run.Lo, turn * run.Hi);
            var b = Math.Max(turn * run.Lo, turn * run.Hi);
            lo = Math.Max(lo, a);
            hi = Math.Min(hi, b);
            min = Math.Min(min, a);
            max = Math.Max(max, b);
        }
        foreach (var r in dim.Refs)
        {
            if (r.Opening == null) continue;
            if (openings == null || !openings.TryGetValue(r.Opening, out var opening) || opening == null)
            {
                gone.Openings++;
                continue;
            }
            // Only an opening in a wall that runs along the chain has a centre on it.
            if (Math.Abs(Cross(opening.Along, n)) >= ParallelSin)
            {
                gone.Skipped++;
                continue;
            }
            stops.Add(new KeyValuePair<double, string>(Dot(opening.Centre, n), r.Opening));
            refs++;
        }

        stops.Sort((a, b) => a.Key.CompareTo(b.Key));
        var chain = new PlanDims.UserChain { Id = dim.Id, Dir = n, Out = w };
        foreach (var stop in stops)
        {
            if (chain.Stops.Count > 0 && Math.Abs(stop.Key - chain.Stops[chain.Stops.Count - 1]) < 1.0)
            {
                if (stop.Value != null) chain.StopIds[chain.StopIds.Count - 1] = stop.Value;
                continue;
            }
            chain.Stops.Add(stop.Key);
            chain.StopIds.Add(stop.Value);
        }
        if (refs < 2 || chain.Stops.Count < 2)
        {
            gone.Dropped = true;
            return null;
        }
        // The walls' shared span, or all of them when they share none: the line starts at its middle.
        if (lo > hi)
        {
            lo = min;
            hi = max;
        }
        var mid = (lo + hi) / 2.0;
        chain.Origin = new Pt(w.X * mid, w.Y * mid);
        chain.Reach = (hi - lo) / 2.0;
        chain.Walls = runs.Count;
        return chain;
    }

    /// <summary>The Print receipt's clause for dimensions that dropped. Empty for none.</summary>
    public static string DroppedLine(int count)
    {
        if (count <= 0) return "";
        return count == 1
            ? "1 dimension dropped: a wall it measured is gone."
            : count.ToString(CultureInfo.InvariantCulture) + " dimensions dropped: a wall they measured is gone.";
    }

    /// <summary>The wall's longest straight run, or null when its path gives none.</summary>
    public static WallEdit.Run MainRun(List<List<Pt>> rings, double tol)
    {
        if (rings == null || rings.Count == 0) return null;
        List<WallEdit.Run> runs;
        try { runs = WallJoins.Runs(rings, tol); }
        catch (Exception) { return null; }
        return runs?.OrderByDescending(r => r.Length).FirstOrDefault();
    }

    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
    static double Cross(Pt a, Pt b) => a.X * b.Y - a.Y * b.X;
}
