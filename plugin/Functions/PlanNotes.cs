using System;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3: the user's own notes on the plan sheet. Text, leaders, dimensions and
/// lines drawn with Rhino's own tools on A-NOTE (or a layer under it), in the
/// model, are copied into the plan's drawing at every Print, at the drawing's
/// own offset, so they print on the plan sheet and survive each new Print.
/// A note put on a layout by hand would go with the layout. Pure, so it tests headless.
/// </summary>
public static class PlanNotes
{
    public const string LayerName = "A-NOTE";
    /// <summary>The forsk:role of a note's copy on the plan sheet.</summary>
    public const string Role = "note";

    /// <summary>A-NOTE itself or any layer under it.</summary>
    public static bool IsNoteLayer(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        return fullPath.Equals(LayerName, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(LayerName + "::", StringComparison.OrdinalIgnoreCase);
    }
}
