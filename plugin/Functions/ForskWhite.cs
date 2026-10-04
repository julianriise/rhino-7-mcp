using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Forsk White, the demo display mode. The patch rewrites an exported Shaded
/// ini and adds nothing: a key ExportToFile did not write is left absent, and
/// a colour keeps the component count it arrived with. Import the result and
/// do not call UpdateDisplayMode afterwards; that drops the ini-only keys.
/// An earlier import is replaced by a new import on load, not retuned in place.
/// Technical lines stay off (TechnicalMask is not raised). Cast shadows stay
/// off. A small x-ray transparency is not applied: glass and the daylight ramp
/// are unproven until the live check, so materials stay opaque.
/// </summary>
public static class ForskWhite
{
    public const string ModeName = "Forsk White";
    public const string ShadedModeName = "Shaded";
    public const string PageViewType = "RhinoPageView";
    public const string DetailType = "DetailViewObject";
    /// <summary>
    /// 1 is this import: cast shadows off. A missing plugin setting is 0,
    /// so a Forsk White from an earlier build is replaced on load.
    /// </summary>
    public const int ModeRevision = 1;

    public const int GroundR = 245;
    public const int GroundG = 245;
    public const int GroundB = 247;
    public const int EdgePx = 1;
    /// <summary>Surface-edge usage. 2 is single colour, the value Pen learned.</summary>
    public const int SurfaceEdgeUsage = 2;
    /// <summary>Clipping fill usage. 3 is solid colour (0 object, 1 backface, 2 plane).</summary>
    public const int ClipFillUsage = 3;
    /// <summary>Clipping edge usage. 1 is solid colour. This is not the surface-edge enum.</summary>
    public const int ClipEdgeUsage = 1;
    public const int Ambient = 200;
    public const int SpecularNearBlack = 8;
    public const int ShadowMap = 1024;
    public const int ShadowSamples = 2;
    public const int ShadowBlur = 4;

    /// <summary>
    /// Model views take the mode. A layout page and a detail do not: a shaded
    /// detail blacks the PDF framebuffer.
    /// </summary>
    public static bool AssignsDisplayMode(string rhinoTypeName)
    {
        if (string.IsNullOrEmpty(rhinoTypeName)) return false;
        if (rhinoTypeName.Equals(PageViewType, StringComparison.Ordinal)) return false;
        if (rhinoTypeName.Equals(DetailType, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Forsk White or Forsk Technical: a mode Forsk put on the view and may change.</summary>
    public static bool IsForskMode(string modeName)
    {
        return string.Equals(modeName, ModeName, StringComparison.Ordinal)
            || string.Equals(modeName, ForskTechnical.ModeName, StringComparison.Ordinal);
    }

    /// <summary>
    /// On: a model view that is not already on its target (Forsk White, or
    /// Forsk Technical for a plan or an elevation). Off: only a model view
    /// still on a Forsk mode, so a Rendered view is left where it was.
    /// </summary>
    public static bool NeedsAssign(bool polishOn, string currentModeName, string targetModeName, string rhinoTypeName)
    {
        if (!AssignsDisplayMode(rhinoTypeName)) return false;
        if (!polishOn) return IsForskMode(currentModeName);
        return !string.Equals(currentModeName, targetModeName, StringComparison.Ordinal);
    }

    public static string TargetMode(bool polishOn)
    {
        return polishOn ? ModeName : ShadedModeName;
    }

    /// <summary>
    /// A stored revision below <see cref="ModeRevision"/> is an older import.
    /// </summary>
    public static bool NeedsReimport(int storedRevision)
    {
        return storedRevision < ModeRevision;
    }

    /// <summary>
    /// A model view still named for its target, but not on the current mode, is
    /// the one that was just replaced. Assign the new one. A layout is skipped.
    /// Off does not pull a view back onto a Forsk mode.
    /// </summary>
    public static bool NeedsReassign(bool polishOn, string currentModeName, string targetModeName, string rhinoTypeName, bool sameMode)
    {
        if (!polishOn || sameMode) return false;
        if (!AssignsDisplayMode(rhinoTypeName)) return false;
        return string.Equals(currentModeName, targetModeName, StringComparison.Ordinal);
    }

    /// <summary>4View, a new view, a split, and Open put the file's modes back.</summary>
    public static bool ReassignsAfterCommand(string englishName)
    {
        if (string.IsNullOrEmpty(englishName)) return false;
        return englishName.Equals("4View", StringComparison.OrdinalIgnoreCase)
            || englishName.Equals("3View", StringComparison.OrdinalIgnoreCase)
            || englishName.Equals("NewViewport", StringComparison.OrdinalIgnoreCase)
            || englishName.Equals("SplitViewportHorizontal", StringComparison.OrdinalIgnoreCase)
            || englishName.Equals("SplitViewportVertical", StringComparison.OrdinalIgnoreCase)
            || englishName.Equals("Open", StringComparison.OrdinalIgnoreCase)
            || englishName.Equals("New", StringComparison.OrdinalIgnoreCase);
    }

    public static string Patch(string exported)
    {
        return PatchWith(exported, Rules);
    }

    /// <summary>Rewrite the keys <paramref name="rules"/> names. A key the export lacks stays absent.</summary>
    internal static string PatchWith(string exported, IReadOnlyDictionary<string, Rule> rules)
    {
        var lines = Lines(exported);
        var section = "";
        for (var i = 0; i < lines.Length; i++)
        {
            string header;
            if (TrySection(lines[i], out header))
            {
                section = header;
                continue;
            }
            string key, value;
            if (!TryKey(lines[i], out key, out value)) continue;
            string next;
            if (!TryRewrite(rules, section, key, value, out next)) continue;
            if (string.Equals(value, next, StringComparison.Ordinal)) continue;
            lines[i] = key + "=" + next;
        }
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }

    public static string Read(string ini, string section, string key)
    {
        var lines = Lines(ini);
        var current = "";
        foreach (var line in lines)
        {
            string header;
            if (TrySection(line, out header))
            {
                current = header;
                continue;
            }
            if (!string.Equals(current, section ?? "", StringComparison.OrdinalIgnoreCase)) continue;
            string found, value;
            if (!TryKey(line, out found, out value)) continue;
            if (found.Equals(key, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    static bool TryRewrite(IReadOnlyDictionary<string, Rule> rules, string section, string key, string value, out string next)
    {
        next = null;
        Rule rule;
        if (rules.TryGetValue(RuleKey(section, key), out rule))
        {
            next = rule.Apply(value);
            return next != null;
        }
        // No curve-piping key is invented. An exported one is turned off.
        if (key.IndexOf("piping", StringComparison.OrdinalIgnoreCase) >= 0 && IsBool(value))
        {
            next = WriteBool(value, false);
            return true;
        }
        return false;
    }

    static string RuleKey(string section, string key)
    {
        return (section ?? "") + "\n" + (key ?? "");
    }

    internal sealed class Rule
    {
        public string Section;
        public string Key;
        public Func<string, string> Apply;
    }

    static readonly Dictionary<string, Rule> Rules = BuildRules();

    static Dictionary<string, Rule> BuildRules()
    {
        return RuleMap(new[]
        {
            Text("", "Name", ModeName),
            Rgb("View settings", "SolidColor", GroundR, GroundG, GroundB),
            Int("View settings", "FillMode", 2),
            Bool("View settings", "UseDocumentGrid", false),
            Bool("View settings", "DrawGrid", false),
            Bool("View settings", "DrawAxes", false),
            Bool("View settings", "DrawWorldAxes", false),
            Bool("View settings", "DrawZAxis", false),
            Bool("View settings", "ShowClippingPlanes", false),
            Bool("View settings", "ClippingShowXSurface", true),
            Bool("View settings", "ClippingShowXEdges", true),
            Int("View settings", "ClippingSurfaceUsage", ClipFillUsage),
            Int("View settings", "ClippingEdgesUsage", ClipEdgeUsage),
            Rgb("View settings", "ClippingSurfaceColor", 255, 255, 255),
            Rgb("View settings", "ClippingEdgeColor", 0, 0, 0),
            Int("View settings", "ClippingEdgeThickness", EdgePx),
            Bool("Shading", "ShadeVertexColors", true),
            Bool("Shading", "ShadeSurface", true),
            Bool("Shading", "UseObjectMaterial", true),
            Bool("Shading\\Material\\Front Material", "FlatShaded", false),
            Bool("Shading\\Material\\Front Material", "OverrideObjectColor", false),
            Bool("Shading\\Material\\Front Material", "OverrideObjectTransparency", false),
            Rgb("Shading\\Material\\Front Material", "Diffuse", 255, 255, 255),
            Int("Shading\\Material\\Front Material", "Shine", 0),
            Rgb("Shading\\Material\\Front Material", "Specular", SpecularNearBlack, SpecularNearBlack, SpecularNearBlack),
            Int("Shading\\Material\\Front Material", "ShineIntensity", 0),
            Rgb("Lighting", "AmbientColor", Ambient, Ambient, Ambient),
            Bool("Lighting", "CastShadows", false),
            Int("Lighting", "ShadowMapSize", ShadowMap),
            Int("Lighting", "NumSamples", ShadowSamples),
            Int("Lighting", "ShadowBlur", ShadowBlur),
            Bool("Lighting", "PerPixelLighting", false),
            Bool("Objects\\Surfaces", "ShowIsocurves", false),
            Bool("Objects\\Surfaces", "ShowTangentEdges", false),
            Bool("Objects\\Surfaces", "ShowTangentSeams", false),
            Bool("Objects\\Surfaces", "ShowEdges", true),
            Int("Objects\\Surfaces", "EdgeThickness", EdgePx),
            Int("Objects\\Surfaces", "NakedEdgeThickness", EdgePx),
            Int("Objects\\Surfaces", "EdgeColorUsage", SurfaceEdgeUsage),
            Int("Objects\\Surfaces", "NakedEdgeColorUsage", SurfaceEdgeUsage),
            Rgb("Objects\\Surfaces", "EdgeColor", 0, 0, 0),
            Rgb("Objects\\Surfaces", "NakedEdgeColor", 0, 0, 0),
            Bool("Objects\\Meshes", "ShowMeshWires", false),
            Rgb("Objects\\Technical", "TSiColor", 0, 0, 0),
            Int("Objects\\Technical", "TSiThickness", EdgePx)
        });
    }

    internal static Dictionary<string, Rule> RuleMap(IEnumerable<Rule> list)
    {
        var map = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in list)
            map[RuleKey(rule.Section, rule.Key)] = rule;
        return map;
    }

    internal static Rule Text(string section, string key, string text)
    {
        return new Rule { Section = section, Key = key, Apply = _ => text };
    }

    internal static Rule Bool(string section, string key, bool flag)
    {
        return new Rule
        {
            Section = section,
            Key = key,
            Apply = existing => IsBool(existing) ? WriteBool(existing, flag) : null
        };
    }

    internal static Rule Int(string section, string key, int number)
    {
        return new Rule
        {
            Section = section,
            Key = key,
            Apply = existing => IsInt(existing) ? number.ToString(CultureInfo.InvariantCulture) : null
        };
    }

    internal static Rule Rgb(string section, string key, int r, int g, int b)
    {
        return new Rule
        {
            Section = section,
            Key = key,
            Apply = existing => WriteRgb(existing, r, g, b)
        };
    }

    /// <summary>
    /// Three components stay three. A fourth component is kept as it was
    /// written, so an alpha field is not added and an existing one is not rewritten.
    /// </summary>
    static string WriteRgb(string existing, int r, int g, int b)
    {
        var parts = (existing ?? "").Split(',');
        if (parts.Length < 3 || parts.Length > 4) return null;
        foreach (var part in parts)
        {
            int number;
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return null;
        }
        var rgb = r.ToString(CultureInfo.InvariantCulture) + ","
            + g.ToString(CultureInfo.InvariantCulture) + ","
            + b.ToString(CultureInfo.InvariantCulture);
        if (parts.Length == 3) return rgb;
        return rgb + "," + parts[3].Trim();
    }

    static string WriteBool(string existing, bool flag)
    {
        var text = (existing ?? "").Trim();
        if (text.Equals("y", StringComparison.OrdinalIgnoreCase) || text.Equals("n", StringComparison.OrdinalIgnoreCase))
            return flag ? "y" : "n";
        if (text.Equals("yes", StringComparison.OrdinalIgnoreCase) || text.Equals("no", StringComparison.OrdinalIgnoreCase))
            return flag ? "yes" : "no";
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase))
            return flag ? "true" : "false";
        if (text == "1" || text == "0")
            return flag ? "1" : "0";
        return flag ? "y" : "n";
    }

    static bool IsBool(string value)
    {
        var text = (value ?? "").Trim();
        return text.Equals("y", StringComparison.OrdinalIgnoreCase)
            || text.Equals("n", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("no", StringComparison.OrdinalIgnoreCase)
            || text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("false", StringComparison.OrdinalIgnoreCase)
            || text == "1" || text == "0";
    }

    static bool IsInt(string value)
    {
        int number;
        return int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    static bool TrySection(string line, out string section)
    {
        section = "";
        var trimmed = (line ?? "").Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[trimmed.Length - 1] != ']') return false;
        var inner = trimmed.Substring(1, trimmed.Length - 2);
        var parts = inner.Split('\\');
        if (parts.Length < 2 || !parts[0].Equals("DisplayMode", StringComparison.OrdinalIgnoreCase))
            return false;
        if (parts.Length == 2) return true;
        section = string.Join("\\", parts, 2, parts.Length - 2);
        return true;
    }

    static bool TryKey(string line, out string key, out string value)
    {
        key = null;
        value = null;
        if (string.IsNullOrEmpty(line)) return false;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '[' || trimmed[0] == ';' || trimmed[0] == '#')
            return false;
        var eq = trimmed.IndexOf('=');
        if (eq <= 0) return false;
        key = trimmed.Substring(0, eq).Trim();
        value = trimmed.Substring(eq + 1).Trim();
        return key.Length > 0;
    }

    static string[] Lines(string text)
    {
        return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
}
