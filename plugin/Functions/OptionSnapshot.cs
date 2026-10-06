using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// AN.5 core: a saved option is a versioned JSON snapshot (forsk.option.v1)
/// of the model records: walls, openings, rooms, the project info and the
/// analysis settings. Write, Read and Diff need no Rhino document; saving
/// from and restoring to a file come in a later, live-checked slice. Option
/// files live beside the 3dm: &lt;project&gt;.forsk/options/A.json.
/// </summary>
public static class OptionSnapshot
{
    public const string Schema = "forsk.option.v1";
    /// <summary>Coordinates and sizes closer than this, in mm, are the same.</summary>
    public const double ToleranceMm = 0.5;

    public sealed class Wall
    {
        public string Id;
        /// <summary>forsk:level, or null.</summary>
        public string Level;
        /// <summary>The centreline, plan mm.</summary>
        public List<Pt> Path = new List<Pt>();
        public double Thickness;
        public double Height;
        /// <summary>forsk:wall_type, or null (Generic 200 in the type catalog).</summary>
        public string Type;
        public bool Existing;
    }

    public sealed class Opening
    {
        public string Id;
        /// <summary>The host wall's forsk:id.</summary>
        public string Host;
        public string Kind;
        public string Type;
        public string Hand;
        public string Swing;
        public string Mark;
        /// <summary>The middle of the opening on the host's centreline, plan mm.</summary>
        public Pt Centre;
        public double Width;
        public double Sill;
        public double Head;
    }

    public sealed class Room
    {
        public string Id;
        public string Name;
        /// <summary>forsk:room_type, or null.</summary>
        public string Type;
        public List<Pt> Outline = new List<Pt>();
    }

    public sealed class Snapshot
    {
        /// <summary>The option's name: A, B, …</summary>
        public string Name;
        /// <summary>When it was saved, ISO 8601 UTC, given by the caller.</summary>
        public string Saved;
        public SortedDictionary<string, string> Project = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public SortedDictionary<string, string> Analysis = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public List<Wall> Walls = new List<Wall>();
        public List<Opening> Openings = new List<Opening>();
        public List<Room> Rooms = new List<Room>();
    }

    static readonly Regex OptionName = new Regex("^[A-Za-z0-9][A-Za-z0-9 _-]{0,31}$");

    /// <summary>A name the option folder can hold: letters, digits, space, _ and -, at most 32.</summary>
    public static bool IsName(string name) => name != null && OptionName.IsMatch(name) && name.Trim() == name;

    /// <summary>&lt;folder&gt;/&lt;project&gt;.forsk/options/&lt;name&gt;.json for the 3dm at projectFile.</summary>
    public static string PathFor(string projectFile, string name)
    {
        if (string.IsNullOrWhiteSpace(projectFile)) throw new ArgumentException("The file has not been saved yet.", nameof(projectFile));
        if (!IsName(name)) throw new ArgumentException("An option name is letters, digits, space, _ or -, at most 32.", nameof(name));
        var folder = Path.GetDirectoryName(projectFile) ?? "";
        var stem = Path.GetFileNameWithoutExtension(projectFile);
        return Path.Combine(folder, stem + ".forsk", "options", name + ".json");
    }

    /// <summary>The snapshot as JSON. Records are sorted by id, so the same model writes the same bytes.</summary>
    public static string Write(Snapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var root = new JObject
        {
            ["schema"] = Schema,
            ["name"] = snapshot.Name ?? "",
            ["saved"] = snapshot.Saved ?? "",
            ["project"] = Map(snapshot.Project),
            ["analysis"] = Map(snapshot.Analysis),
            ["walls"] = new JArray(snapshot.Walls.OrderBy(w => w.Id, StringComparer.Ordinal).Select(w => Drop(new JObject
            {
                ["id"] = w.Id,
                ["level"] = w.Level,
                ["path"] = Points(w.Path),
                ["thickness"] = w.Thickness,
                ["height"] = w.Height,
                ["type"] = w.Type,
                ["existing"] = w.Existing ? (JToken)true : null,
            }))),
            ["openings"] = new JArray(snapshot.Openings.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => Drop(new JObject
            {
                ["id"] = o.Id,
                ["host"] = o.Host,
                ["kind"] = o.Kind,
                ["type"] = o.Type,
                ["hand"] = o.Hand,
                ["swing"] = o.Swing,
                ["mark"] = o.Mark,
                ["centre"] = Point(o.Centre),
                ["width"] = o.Width,
                ["sill"] = o.Sill,
                ["head"] = o.Head,
            }))),
            ["rooms"] = new JArray(snapshot.Rooms.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => Drop(new JObject
            {
                ["id"] = r.Id,
                ["name"] = r.Name,
                ["type"] = r.Type,
                ["outline"] = Points(r.Outline),
            }))),
        };
        return root.ToString(Formatting.Indented) + "\n";
    }

    /// <summary>Reads an option file. Null with the reason when it is not forsk.option.v1.</summary>
    public static Snapshot Read(string json, out string error)
    {
        error = null;
        JObject root;
        try
        {
            using (var reader = new JsonTextReader(new StringReader(json ?? "")) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
                root = JObject.Load(reader);
        }
        catch (Exception e)
        {
            error = "The option file is not JSON: " + e.Message;
            return null;
        }
        var schema = (string)root["schema"];
        if (schema != Schema)
        {
            error = "The option file is " + (schema ?? "unversioned") + "; this Forsk reads " + Schema + ".";
            return null;
        }
        try
        {
            var snapshot = new Snapshot { Name = (string)root["name"], Saved = (string)root["saved"] };
            foreach (var p in (root["project"] as JObject ?? new JObject()).Properties()) snapshot.Project[p.Name] = (string)p.Value;
            foreach (var p in (root["analysis"] as JObject ?? new JObject()).Properties()) snapshot.Analysis[p.Name] = (string)p.Value;
            foreach (var w in Items(root, "walls"))
                snapshot.Walls.Add(new Wall
                {
                    Id = Id(w, "wall"), Level = (string)w["level"], Path = Points(w["path"]),
                    Thickness = (double)w["thickness"], Height = (double)w["height"],
                    Type = (string)w["type"], Existing = (bool?)w["existing"] ?? false,
                });
            foreach (var o in Items(root, "openings"))
                snapshot.Openings.Add(new Opening
                {
                    Id = Id(o, "opening"), Host = (string)o["host"], Kind = (string)o["kind"], Type = (string)o["type"],
                    Hand = (string)o["hand"], Swing = (string)o["swing"], Mark = (string)o["mark"],
                    Centre = Point(o["centre"]), Width = (double)o["width"], Sill = (double)o["sill"], Head = (double)o["head"],
                });
            foreach (var r in Items(root, "rooms"))
                snapshot.Rooms.Add(new Room { Id = Id(r, "room"), Name = (string)r["name"], Type = (string)r["type"], Outline = Points(r["outline"]) });
            return snapshot;
        }
        catch (Exception e) when (e is FormatException || e is ArgumentException || e is InvalidCastException || e is NullReferenceException)
        {
            error = "The option file is damaged: " + e.Message;
            return null;
        }
    }

    public sealed class Change
    {
        /// <summary>wall, door, window, room, or the kind an opening names.</summary>
        public string Noun;
        /// <summary>added, removed, moved, changed or renamed.</summary>
        public string Verb;
        public string Id;
    }

    /// <summary>What changed from one option to another, matched by id: "3 walls moved, 1 window added".</summary>
    public sealed class Diff
    {
        public List<Change> Changes = new List<Change>();
        public bool ProjectChanged;
        public bool AnalysisChanged;
        public bool Same => Changes.Count == 0 && !ProjectChanged && !AnalysisChanged;

        public string Summary()
        {
            if (Same) return "No changes.";
            var parts = new List<string>();
            foreach (var noun in Nouns(Changes))
                foreach (var verb in Verbs)
                {
                    var n = Changes.Count(c => c.Noun == noun && c.Verb == verb);
                    if (n > 0) parts.Add(n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? noun : Plural(noun)) + " " + verb);
                }
            if (ProjectChanged) parts.Add("project info changed");
            if (AnalysisChanged) parts.Add("analysis settings changed");
            return string.Join(", ", parts);
        }
    }

    static readonly string[] Verbs = { "added", "removed", "moved", "changed", "renamed" };
    static readonly string[] NounOrder = { "wall", "door", "window", "room" };

    static IEnumerable<string> Nouns(List<Change> changes) =>
        NounOrder.Concat(changes.Select(c => c.Noun).Where(n => !NounOrder.Contains(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal));

    static string Plural(string noun) => noun.EndsWith("s", StringComparison.Ordinal) ? noun + "es" : noun + "s";

    public static Diff Compare(Snapshot from, Snapshot to)
    {
        if (from == null) throw new ArgumentNullException(nameof(from));
        if (to == null) throw new ArgumentNullException(nameof(to));
        var diff = new Diff
        {
            ProjectChanged = !SameMap(from.Project, to.Project),
            AnalysisChanged = !SameMap(from.Analysis, to.Analysis),
        };
        Match(diff, from.Walls, to.Walls, w => w.Id, w => "wall",
            (a, b) => SamePath(a.Path, b.Path),
            (a, b) => Near(a.Thickness, b.Thickness) && Near(a.Height, b.Height) && a.Type == b.Type && a.Level == b.Level && a.Existing == b.Existing,
            (a, b) => true);
        Match(diff, from.Openings, to.Openings, o => o.Id, o => string.IsNullOrEmpty(o.Kind) ? "opening" : o.Kind,
            (a, b) => a.Host == b.Host && Near(a.Centre, b.Centre),
            (a, b) => Near(a.Width, b.Width) && Near(a.Sill, b.Sill) && Near(a.Head, b.Head) && a.Kind == b.Kind && a.Type == b.Type && a.Hand == b.Hand && a.Swing == b.Swing,
            (a, b) => a.Mark == b.Mark);
        Match(diff, from.Rooms, to.Rooms, r => r.Id, r => "room",
            (a, b) => true,
            (a, b) => SamePath(a.Outline, b.Outline),
            (a, b) => a.Name == b.Name && a.Type == b.Type);
        return diff;
    }

    /// <summary>
    /// One change per record: added or removed by id; otherwise moved before
    /// changed before renamed, so a record counts once, under its biggest change.
    /// </summary>
    static void Match<T>(Diff diff, List<T> from, List<T> to, Func<T, string> id, Func<T, string> noun,
        Func<T, T, bool> samePlace, Func<T, T, bool> sameShape, Func<T, T, bool> sameName)
    {
        var before = from.GroupBy(id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var after = to.GroupBy(id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var pair in after.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(pair.Key, out var old)) { diff.Changes.Add(new Change { Noun = noun(pair.Value), Verb = "added", Id = pair.Key }); continue; }
            var verb = !samePlace(old, pair.Value) ? "moved" : !sameShape(old, pair.Value) ? "changed" : !sameName(old, pair.Value) ? "renamed" : null;
            if (verb != null) diff.Changes.Add(new Change { Noun = noun(pair.Value), Verb = verb, Id = pair.Key });
        }
        foreach (var pair in before.Where(p => !after.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            diff.Changes.Add(new Change { Noun = noun(pair.Value), Verb = "removed", Id = pair.Key });
    }

    static bool Near(double a, double b) => Math.Abs(a - b) <= ToleranceMm;
    static bool Near(Pt a, Pt b) => Near(a.X, b.X) && Near(a.Y, b.Y);

    static bool SamePath(List<Pt> a, List<Pt> b) =>
        a.Count == b.Count && a.Zip(b, (p, q) => Near(p, q)).All(x => x);

    static bool SameMap(IDictionary<string, string> a, IDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    static JObject Map(IDictionary<string, string> map)
    {
        var o = new JObject();
        foreach (var p in map.OrderBy(p => p.Key, StringComparer.Ordinal)) o[p.Key] = p.Value;
        return o;
    }

    static JObject Drop(JObject o)
    {
        foreach (var p in o.Properties().Where(p => p.Value.Type == JTokenType.Null).ToList()) p.Remove();
        return o;
    }

    static JArray Point(Pt p) => new JArray(p.X, p.Y);
    static JArray Points(List<Pt> points) => new JArray((points ?? new List<Pt>()).Select(Point));

    static Pt Point(JToken token)
    {
        if (!(token is JArray a) || a.Count != 2) throw new FormatException("a point is not [x, y]");
        return new Pt((double)a[0], (double)a[1]);
    }

    static List<Pt> Points(JToken token) =>
        token is JArray a ? a.Select(Point).ToList() : throw new FormatException("a path is not a list of points");

    static IEnumerable<JObject> Items(JObject root, string key) => (root[key] as JArray ?? new JArray()).OfType<JObject>();

    static string Id(JObject o, string what)
    {
        var id = (string)o["id"];
        if (string.IsNullOrWhiteSpace(id)) throw new FormatException("a " + what + " has no id");
        return id;
    }
}
