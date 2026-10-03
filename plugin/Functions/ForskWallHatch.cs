using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// A selected wall is drawn with this blue. The same blue is
/// AppearanceSettings.SelectedObjectColor. IgnoreHighlights is left false.
/// The hatch is not baked.
/// </summary>
public static class ForskWallHatch
{
    public const int Red = 41;
    public const int Green = 72;
    public const int Blue = 245;
    public const string WallKind = "wall";

    public readonly struct Candidate
    {
        public Candidate(bool selected, string kind)
        {
            Selected = selected;
            Kind = kind;
        }

        public bool Selected { get; }
        public string Kind { get; }
    }

    /// <summary>True only for a selected object whose forsk:kind is wall.</summary>
    public static bool Draws(bool selected, string kind)
    {
        return selected && string.Equals(kind, WallKind, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<int> DrawIndexes(IReadOnlyList<Candidate> objects)
    {
        var hits = new List<int>();
        if (objects == null) return hits;
        for (var i = 0; i < objects.Count; i++)
            if (Draws(objects[i].Selected, objects[i].Kind)) hits.Add(i);
        return hits;
    }
}
