using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The view picker (top left of the Forsk window): Perspective, Plan and the
/// four elevations. Each is a camera the active viewport takes; show_view
/// adds the look (Forsk White or Forsk Technical) and the zoom. Elevations
/// look as the sheets do: North looks south at the north facade. No
/// RhinoCommon.
/// </summary>
public static class ViewPicker
{
    public const string Perspective = "perspective";
    public const string Plan = "plan";
    public static readonly IReadOnlyList<string> Ids = new[] { Perspective, Plan, "north", "east", "south", "west" };

    public sealed class Camera
    {
        public bool Parallel;
        public double Dx, Dy, Dz;
        public double Ux, Uy, Uz;
    }

    public static bool Known(string id) => Ids.Contains((id ?? "").Trim().ToLowerInvariant());

    /// <summary>"Perspective", "Plan", "South elevation".</summary>
    public static string Label(string id)
    {
        var key = (id ?? "").Trim().ToLowerInvariant();
        if (key == Perspective) return "Perspective";
        if (key == Plan) return "Plan";
        if (!Known(key)) return "View";
        return char.ToUpperInvariant(key[0]) + key.Substring(1) + " elevation";
    }

    /// <summary>The camera for a view: its direction and up. Null for an unknown id.</summary>
    public static Camera For(string id)
    {
        switch ((id ?? "").Trim().ToLowerInvariant())
        {
            // From the south-west corner, above, looking in: the building's two street sides.
            case Perspective: return new Camera { Parallel = false, Dx = 1, Dy = 1, Dz = -0.7, Uz = 1 };
            case Plan: return new Camera { Parallel = true, Dz = -1, Uy = 1 };
            case "north": return new Camera { Parallel = true, Dy = -1, Uz = 1 };
            case "east": return new Camera { Parallel = true, Dx = -1, Uz = 1 };
            case "south": return new Camera { Parallel = true, Dy = 1, Uz = 1 };
            case "west": return new Camera { Parallel = true, Dx = 1, Uz = 1 };
            default: return null;
        }
    }

    public const string InteriorPrefix = "interior:";
    public const string ExteriorPrefix = "exterior:";

    /// <summary>
    /// The window's picker: the view shown now (null: none of them), its label,
    /// every view, and the saved Interior and Exterior render views, one list
    /// each when there are any (Julian, 2026-10-08).
    /// </summary>
    public static JObject Control(string current, IList<string> interior = null, IList<string> exterior = null)
    {
        var options = new JArray();
        foreach (var id in Ids)
            options.Add(new JObject { ["id"] = id, ["label"] = Label(id) });
        var control = new JObject
        {
            ["value"] = Known(current) ? current : "",
            ["label"] = Label(current),
            ["options"] = options
        };
        var groups = new JArray();
        AddGroup(groups, "Interior", InteriorPrefix, interior);
        AddGroup(groups, "Exterior", ExteriorPrefix, exterior);
        if (groups.Count > 0) control["groups"] = groups;
        return control;
    }

    static void AddGroup(JArray groups, string label, string prefix, IList<string> names)
    {
        if (names == null || names.Count == 0) return;
        var items = new JArray();
        foreach (var name in names)
            items.Add(new JObject { ["id"] = prefix + name, ["label"] = name });
        groups.Add(new JObject { ["label"] = label, ["items"] = items });
    }

    /// <summary>A picked saved render view ("interior:Living"): its name and whether it is exterior.</summary>
    public static bool TryRender(string id, out string name, out bool exterior)
    {
        name = null;
        exterior = false;
        if (string.IsNullOrEmpty(id)) return false;
        if (id.StartsWith(InteriorPrefix, StringComparison.Ordinal)) name = id.Substring(InteriorPrefix.Length);
        else if (id.StartsWith(ExteriorPrefix, StringComparison.Ordinal)) { name = id.Substring(ExteriorPrefix.Length); exterior = true; }
        if (!string.IsNullOrWhiteSpace(name)) return true;
        name = null;
        exterior = false;
        return false;
    }

    /// <summary>
    /// Which picker view a viewport shows now: any perspective is
    /// Perspective, a parallel view straight down is Plan, a level one the
    /// elevation it faces. Anything else (a tilted parallel view) is null.
    /// </summary>
    public static string Current(bool parallel, double dx, double dy, double dz)
    {
        if (!parallel) return Perspective;
        switch (ForskTechnical.Classify(true, dx, dy, dz))
        {
            case ForskTechnical.Look.Plan:
                return Plan;
            case ForskTechnical.Look.Elevation:
                if (Math.Abs(dx) > Math.Abs(dy)) return dx < 0 ? "east" : "west";
                return dy < 0 ? "north" : "south";
            default:
                return null;
        }
    }
}
