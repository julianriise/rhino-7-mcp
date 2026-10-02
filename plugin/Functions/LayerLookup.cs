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
    /// A-ROOF stays off so clay shows walls and floor. A-OPEN stays on: frames,
    /// leaves and glass live on A-OPEN::Block, and a hidden parent hides that
    /// child. Opening markers are object-hidden instead.
    /// </summary>
    private static bool LayerHiddenByDefault(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.Equals("A-ROOF", StringComparison.OrdinalIgnoreCase);
    }

    private Layer EnsureLayer(RhinoDoc doc, string name, Color color)
    {
        var existing = FindLayerCaseInsensitive(doc, name);
        if (existing != null)
        {
            ApplyDefaultLayerVisibility(doc, existing, name);
            if (StampPrintInk(existing))
                doc.Layers.Modify(existing, existing.Index, true);
            return existing;
        }

        var layer = new Layer
        {
            Name = name,
            Color = color,
            IsVisible = !LayerHiddenByDefault(name)
        };
        var index = doc.Layers.Add(layer);
        var created = doc.Layers.FindIndex(index);
        if (created != null && StampPrintInk(created))
            doc.Layers.Modify(created, created.Index, true);
        return created;
    }

    /// <summary>
    /// Plot colour and weight from <see cref="PrintInk"/>. Display colour is left as it is.
    /// </summary>
    internal static bool StampPrintInk(Layer layer)
    {
        if (layer == null) return false;
        if (!PrintInk.TryResolve(layer.FullPath, layer.Name, out var spec)) return false;
        layer.PlotColor = Color.FromArgb(spec.R, spec.G, spec.B);
        layer.PlotWeight = spec.WeightMm;
        return true;
    }

    /// <summary>
    /// Every Forsk layer gets its print colour. An object that would print in its
    /// display colour prints by layer instead. Sheet objects keep their own pen.
    /// </summary>
    internal static void ApplyDocumentPrintInk(RhinoDoc doc)
    {
        if (doc == null) return;
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer == null || layer.IsDeleted) continue;
            if (!StampPrintInk(layer)) continue;
            doc.Layers.Modify(layer, layer.Index, true);
        }
        foreach (var obj in EnumerateDocObjects(doc))
        {
            var attrs = obj?.Attributes;
            if (attrs == null) continue;
            if (attrs.PlotColorSource != ObjectPlotColorSource.PlotColorFromDisplay) continue;
            var layer = attrs.LayerIndex >= 0 && attrs.LayerIndex < doc.Layers.Count
                ? doc.Layers[attrs.LayerIndex]
                : null;
            if (layer == null || layer.IsDeleted) continue;
            if (!PrintInk.TryResolve(layer.FullPath, layer.Name, out _)) continue;
            attrs.PlotColorSource = ObjectPlotColorSource.PlotColorFromLayer;
            obj.CommitChanges();
        }
    }

    private static void ApplyDefaultLayerVisibility(RhinoDoc doc, Layer layer, string name)
    {
        if (!LayerHiddenByDefault(name)) return;
        if (!layer.IsVisible) return;
        layer.IsVisible = false;
        doc.Layers.Modify(layer, layer.Index, true);
    }
}
