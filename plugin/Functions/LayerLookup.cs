using System;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Layer identity for plan interpret and layer materials.
/// </summary>
public partial class RhinoMCPFunctions
{
    private static bool ObjectOnLayer(RhinoDoc doc, RhinoObject obj, Layer layer)
    {
        if (obj == null || layer == null) return false;
        if (obj.Attributes.LayerIndex == layer.Index) return true;
        var objLayer = doc.Layers[obj.Attributes.LayerIndex];
        if (objLayer == null) return false;
        return objLayer.Name.Equals(layer.Name, StringComparison.OrdinalIgnoreCase) ||
               objLayer.FullPath.Equals(layer.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    private Layer FindLayerCaseInsensitive(RhinoDoc doc, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var layer = FindLayerByNameOrFullPath(doc, name);
        if (layer != null && !layer.IsDeleted) return layer;
        return doc.Layers.FirstOrDefault(candidate =>
            !candidate.IsDeleted &&
            (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             candidate.FullPath.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A-OPEN (markers) and A-ROOF stay off by default so clay shows walls/floor with holes only.
    /// </summary>
    private static bool LayerHiddenByDefault(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.Equals("A-OPEN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("A-ROOF", StringComparison.OrdinalIgnoreCase);
    }

    private Layer EnsureLayer(RhinoDoc doc, string name, Color color)
    {
        var existing = FindLayerCaseInsensitive(doc, name);
        if (existing != null)
        {
            ApplyDefaultLayerVisibility(doc, existing, name);
            return existing;
        }

        var layer = new Layer
        {
            Name = name,
            Color = color,
            IsVisible = !LayerHiddenByDefault(name)
        };
        var index = doc.Layers.Add(layer);
        return doc.Layers.FindIndex(index);
    }

    private static void ApplyDefaultLayerVisibility(RhinoDoc doc, Layer layer, string name)
    {
        if (!LayerHiddenByDefault(name)) return;
        if (!layer.IsVisible) return;
        layer.IsVisible = false;
        doc.Layers.Modify(layer, layer.Index, true);
    }
}
