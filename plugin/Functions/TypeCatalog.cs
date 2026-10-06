using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// AG.1: the wall, floor, roof and opening type catalog (forsk.types.v1,
/// plugin/Types/forsk.types.v1.json). No Rhino document. Parse reads and
/// validates it and fails closed with every reason. Nothing reads it in
/// Rhino yet: walls keep their thickness, and old walls map to the default
/// type (Generic 200).
/// </summary>
public static class TypeCatalog
{
    public const string Schema = "forsk.types.v1";
    public const string WallTypeKey = "forsk:wall_type";

    public static readonly string[] Functions = { "structure", "insulation", "membrane", "cladding", "cavity", "service", "finish" };
    public static readonly string[] DoorOperations = { "hinged", "double", "sliding", "pocket", "folding" };
    public static readonly string[] WindowOperations = { "fixed", "side_hung", "top_hung", "tilt_turn", "pivot" };
    public static readonly string[] Glazings = { "none", "double", "triple" };

    public sealed class Material
    {
        public string Id;
        public string Name;
        public string Hatch;
        public double CutPen;
        public int[] Colour;
        /// <summary>The render preset in forsk templates/materials.json, or null.</summary>
        public string Render;
    }

    public sealed class Layer
    {
        public string Name;
        public string Material;
        public double Thickness;
        public string Function;
        public bool Core;
    }

    /// <summary>A wall, floor or roof type. Layers run exterior to interior, or top to bottom.</summary>
    public sealed class Assembly
    {
        public string Id;
        public string Name;
        public double Thickness;
        /// <summary>The build-up is a placeholder for Julian to confirm.</summary>
        public bool Placeholder;
        public List<Layer> Layers = new List<Layer>();

        public Layer Core => Layers.FirstOrDefault(l => l.Core);
    }

    public sealed class OpeningType
    {
        public string Id;
        public string Kind;
        public string Operation;
        public double FrameDepth;
        public double FrameWidth;
        public double LeafThickness;
        public string Glazing;
        public bool Threshold;
        public double DefaultWidth;
        public double DefaultHeight;
        public bool Placeholder;
    }

    public sealed class Catalog
    {
        public string DefaultWall;
        public List<Material> Materials = new List<Material>();
        public List<Assembly> Walls = new List<Assembly>();
        public List<Assembly> Floors = new List<Assembly>();
        public List<Assembly> Roofs = new List<Assembly>();
        public List<OpeningType> Openings = new List<OpeningType>();

        public Assembly Wall(string id) => Walls.FirstOrDefault(w => w.Id == id);
        public OpeningType Opening(string id) => Openings.FirstOrDefault(o => o.Id == id);

        /// <summary>
        /// The type of a wall record: its forsk:wall_type when the catalog has
        /// it, otherwise the default. A wall from before the catalog is Generic 200.
        /// </summary>
        public Assembly WallFor(string wallTypeRaw)
        {
            var id = (wallTypeRaw ?? "").Trim();
            return (id.Length > 0 ? Wall(id) : null) ?? Wall(DefaultWall);
        }
    }

    /// <summary>Reads and validates the catalog. Null with every reason when it is not valid.</summary>
    public static Catalog Parse(string json, out List<string> errors)
    {
        errors = new List<string>();
        JObject root;
        try
        {
            using (var reader = new JsonTextReader(new System.IO.StringReader(json ?? "")) { DateParseHandling = DateParseHandling.None })
                root = JObject.Load(reader);
        }
        catch (Exception e)
        {
            errors.Add("Not JSON: " + e.Message);
            return null;
        }
        var schema = (string)root["schema"];
        if (schema != Schema)
        {
            errors.Add("Schema is " + (schema ?? "missing") + ", expected " + Schema + ".");
            return null;
        }

        var catalog = new Catalog { DefaultWall = (string)root["defaults"]?["wall"] };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var problems = errors;
        void Fail(string message) => problems.Add(message);
        bool NewId(string id, string group)
        {
            if (string.IsNullOrWhiteSpace(id)) { Fail(group + ": an entry has no id."); return false; }
            if (!ids.Add(group + "/" + id)) { Fail(group + ": " + id + " is listed twice."); return false; }
            return true;
        }

        foreach (var m in Array(root, "materials", Fail))
        {
            var material = new Material
            {
                Id = (string)m["id"],
                Name = (string)m["name"],
                Hatch = (string)m["hatch"],
                CutPen = Number(m["cut_pen"]),
                Colour = (m["colour"] as JArray)?.Select(c => (int)c).ToArray(),
                Render = (string)m["render"],
            };
            if (!NewId(material.Id, "materials")) continue;
            if (string.IsNullOrWhiteSpace(material.Name)) Fail("Material " + material.Id + " has no name.");
            if (string.IsNullOrWhiteSpace(material.Hatch)) Fail("Material " + material.Id + " has no hatch.");
            if (!(material.CutPen > 0)) Fail("Material " + material.Id + " has no cut pen.");
            if (material.Colour == null || material.Colour.Length != 3 || material.Colour.Any(c => c < 0 || c > 255))
                Fail("Material " + material.Id + " colour is not three values from 0 to 255.");
            catalog.Materials.Add(material);
        }
        var known = new HashSet<string>(catalog.Materials.Select(m => m.Id), StringComparer.Ordinal);

        catalog.Walls = Assemblies(root, "walls", "wall.", known, NewId, Fail);
        catalog.Floors = Assemblies(root, "floors", "floor.", known, NewId, Fail);
        catalog.Roofs = Assemblies(root, "roofs", "roof.", known, NewId, Fail);

        foreach (var o in Array(root, "openings", Fail))
        {
            var opening = new OpeningType
            {
                Id = (string)o["id"],
                Kind = (string)o["kind"],
                Operation = (string)o["operation"],
                FrameDepth = Number(o["frame_depth"]),
                FrameWidth = Number(o["frame_width"]),
                LeafThickness = Number(o["leaf_thickness"]),
                Glazing = (string)o["glazing"],
                Threshold = (bool?)o["threshold"] ?? false,
                DefaultWidth = Number(o["default_width"]),
                DefaultHeight = Number(o["default_height"]),
                Placeholder = (bool?)o["placeholder"] ?? false,
            };
            if (!NewId(opening.Id, "openings")) continue;
            var operations = opening.Kind == "door" ? DoorOperations : opening.Kind == "window" ? WindowOperations : null;
            if (operations == null) Fail("Opening " + opening.Id + " kind is " + (opening.Kind ?? "missing") + ", not door or window.");
            else if (!operations.Contains(opening.Operation)) Fail("Opening " + opening.Id + " operation " + (opening.Operation ?? "missing") + " is not a " + opening.Kind + " operation.");
            if (!opening.Id.StartsWith((opening.Kind ?? "") + ".", StringComparison.Ordinal)) Fail("Opening " + opening.Id + " id does not start with its kind.");
            if (!Glazings.Contains(opening.Glazing)) Fail("Opening " + opening.Id + " glazing " + (opening.Glazing ?? "missing") + " is not none, double or triple.");
            if (opening.Kind == "window" && opening.Glazing == "none") Fail("Opening " + opening.Id + " is a window with no glass.");
            if (!(opening.FrameDepth > 0) || !(opening.FrameWidth > 0) || opening.LeafThickness < 0)
                Fail("Opening " + opening.Id + " frame or leaf size is not positive.");
            if (!(opening.DefaultWidth > 2 * opening.FrameWidth) || !(opening.DefaultHeight > 2 * opening.FrameWidth))
                Fail("Opening " + opening.Id + " default size leaves no room inside the frame.");
            catalog.Openings.Add(opening);
        }

        if (string.IsNullOrWhiteSpace(catalog.DefaultWall)) Fail("defaults.wall is missing.");
        else if (catalog.Wall(catalog.DefaultWall) == null) Fail("defaults.wall " + catalog.DefaultWall + " is not a wall type.");

        return errors.Count == 0 ? catalog : null;
    }

    static List<Assembly> Assemblies(JObject root, string group, string prefix, HashSet<string> known,
        Func<string, string, bool> newId, Action<string> fail)
    {
        var list = new List<Assembly>();
        foreach (var t in Array(root, group, fail))
        {
            var type = new Assembly
            {
                Id = (string)t["id"],
                Name = (string)t["name"],
                Thickness = Number(t["thickness"]),
                Placeholder = (bool?)t["placeholder"] ?? false,
            };
            if (!newId(type.Id, group)) continue;
            if (!type.Id.StartsWith(prefix, StringComparison.Ordinal)) fail(type.Id + " id does not start with " + prefix);
            if (string.IsNullOrWhiteSpace(type.Name)) fail(type.Id + " has no name.");
            foreach (var l in (t["layers"] as JArray ?? new JArray()).OfType<JObject>())
            {
                type.Layers.Add(new Layer
                {
                    Name = (string)l["name"],
                    Material = (string)l["material"],
                    Thickness = Number(l["thickness"]),
                    Function = (string)l["function"],
                    Core = (bool?)l["core"] ?? false,
                });
            }
            if (type.Layers.Count == 0) { fail(type.Id + " has no layers."); list.Add(type); continue; }
            for (int i = 0; i < type.Layers.Count; i++)
            {
                var layer = type.Layers[i];
                var at = type.Id + " layer " + (i + 1) + " (" + (layer.Name ?? "no name") + ")";
                if (!known.Contains(layer.Material ?? "")) fail(at + ": material " + (layer.Material ?? "missing") + " is not in the catalog.");
                if (!Functions.Contains(layer.Function)) fail(at + ": function " + (layer.Function ?? "missing") + " is not one of " + string.Join(", ", Functions) + ".");
                if (double.IsNaN(layer.Thickness) || layer.Thickness < 0) fail(at + ": thickness is not a number of 0 or more.");
                else if (layer.Function != "membrane" && layer.Thickness <= 0) fail(at + ": only a membrane may be 0 thick.");
            }
            var cores = type.Layers.Count(l => l.Core);
            if (cores != 1) fail(type.Id + " has " + cores + " core layers, expected 1.");
            else if (type.Core.Function != "structure") fail(type.Id + " core layer is " + type.Core.Function + ", not structure.");
            var sum = type.Layers.Sum(l => double.IsNaN(l.Thickness) ? 0 : l.Thickness);
            if (Math.Abs(sum - type.Thickness) > 0.01)
                fail(type.Id + " thickness is " + Mm(type.Thickness) + " but its layers add up to " + Mm(sum) + ".");
            list.Add(type);
        }
        return list;
    }

    static IEnumerable<JObject> Array(JObject root, string key, Action<string> fail)
    {
        if (root[key] is JArray array) return array.OfType<JObject>();
        fail(key + " is missing.");
        return Enumerable.Empty<JObject>();
    }

    static double Number(JToken token) =>
        token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? (double)token : double.NaN;

    static string Mm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + " mm";
}
