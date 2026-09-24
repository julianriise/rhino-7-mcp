using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Which document objects a select_objects filter hits.
/// A row with no attributes, or a null name, is skipped. It does not throw.
/// </summary>
public static class SelectPlan
{
    public sealed class Row
    {
        public string Id;
        public string Name;
        public bool HasAttributes;
        public int R;
        public int G;
        public int B;
        public Dictionary<string, string> Strings;
    }

    public static List<string> Match(
        IList<Row> rows,
        IList<string> names,
        bool hasColor,
        int red,
        int green,
        int blue,
        IDictionary<string, IList<string>> custom,
        bool any)
    {
        var hits = new List<string>();
        if (rows == null) return hits;
        var named = names != null;
        var hasCustom = custom != null && custom.Count > 0;
        foreach (var row in rows)
        {
            if (row == null || !row.HasAttributes || string.IsNullOrEmpty(row.Id))
                continue;
            if (Hits(row, names, named, hasColor, red, green, blue, custom, hasCustom, any))
                hits.Add(row.Id);
        }
        return hits;
    }

    private static bool Hits(
        Row row,
        IList<string> names,
        bool named,
        bool hasColor,
        int red,
        int green,
        int blue,
        IDictionary<string, IList<string>> custom,
        bool hasCustom,
        bool any)
    {
        var nameHit = named && row.Name != null && Contains(names, row.Name);
        var colorHit = hasColor && row.R == red && row.G == green && row.B == blue;
        var customHit = hasCustom && CustomHit(row, custom, any);
        if (!named && !hasColor && !hasCustom) return true;
        if (any) return nameHit || colorHit || customHit;
        if (named && !nameHit) return false;
        if (hasColor && !colorHit) return false;
        if (hasCustom && !customHit) return false;
        return true;
    }

    private static bool CustomHit(Row row, IDictionary<string, IList<string>> custom, bool any)
    {
        if (row.Strings == null) return false;
        var matched = false;
        foreach (var pair in custom)
        {
            row.Strings.TryGetValue(pair.Key, out var value);
            var hit = value != null && pair.Value != null && Contains(pair.Value, value);
            if (any)
            {
                if (hit) return true;
            }
            else if (!hit)
            {
                return false;
            }
            else
            {
                matched = true;
            }
        }
        return any ? matched : true;
    }

    private static bool Contains(IList<string> values, string candidate)
    {
        if (values == null || candidate == null) return false;
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], candidate, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
