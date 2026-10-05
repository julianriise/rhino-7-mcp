using System;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions
{
    /// <summary>
    /// Hides everything but the model after Generate 3D and shows it again. The
    /// layer paths are document user text, so the Show 2D chip knows which ones.
    /// Layer visibility changes inside the open undo record, so Undo restores them.
    /// </summary>
    public static class PlanLayerHost
    {
        /// <summary>Hide the visible layers that hold no Forsk object. True when the stored layers are now hidden.</summary>
        public static bool Hide(RhinoDoc doc)
        {
            if (doc == null) return false;
            var layers = Describe(doc);
            var chosen = PlanLayers.ToHide(layers);
            if (chosen.Count > 0)
            {
                // Rhino does not hide the current layer: the model's first layer becomes current.
                var current = doc.Layers.CurrentLayer;
                var currentPath = current == null ? null : PathOf(current);
                var model = layers.Find(layer => layer.HoldsModel);
                if (currentPath != null && model != null && chosen.Exists(path => path.Equals(currentPath, StringComparison.OrdinalIgnoreCase)))
                {
                    var next = Find(doc, model.Path);
                    if (next != null) doc.Layers.SetCurrentLayerIndex(next.Index, true);
                }
                foreach (var path in chosen)
                    SetVisible(doc, path, false);
                var stored = doc.Strings.GetValue(PlanLayers.Section, PlanLayers.Entry);
                doc.Strings.SetString(PlanLayers.Section, PlanLayers.Entry, PlanLayers.Store(PlanLayers.Merge(stored, chosen)));
            }
            return AreHidden(doc);
        }

        /// <summary>Show the remembered layers when they are hidden, otherwise hide them again.</summary>
        public static string Toggle(RhinoDoc doc)
        {
            if (doc == null) return ForskReceipt.Done + "No file open.";
            var stored = doc.Strings.GetValue(PlanLayers.Section, PlanLayers.Entry);
            if (PlanLayers.Stored(stored).Count == 0)
                return ForskReceipt.Done + "No 2D layers were hidden.";
            var show = PlanLayers.AreHidden(stored, path => Visible(doc, path));
            foreach (var path in PlanLayers.Stored(stored))
                SetVisible(doc, path, show);
            doc.Views.Redraw();
            return ForskReceipt.Done + (show ? "2D layers shown." : "2D layers hidden.");
        }

        public static bool AreHidden(RhinoDoc doc)
        {
            if (doc == null) return false;
            var stored = doc.Strings.GetValue(PlanLayers.Section, PlanLayers.Entry);
            return PlanLayers.AreHidden(stored, path => Visible(doc, path));
        }

        public static string Record(RhinoDoc doc)
        {
            if (doc == null) return "";
            return doc.Strings.GetValue(PlanLayers.Section, PlanLayers.Entry) ?? "";
        }

        static List<PlanLayers.LayerInfo> Describe(RhinoDoc doc)
        {
            var list = new List<PlanLayers.LayerInfo>();
            foreach (var layer in doc.Layers)
            {
                if (layer == null || layer.IsDeleted) continue;
                RhinoObject[] found = null;
                try { found = doc.Objects.FindByLayer(layer); }
                catch (Exception) { found = null; }
                list.Add(new PlanLayers.LayerInfo
                {
                    Path = PathOf(layer),
                    Visible = layer.IsVisible,
                    HasObjects = found != null && found.Length > 0,
                    HoldsModel = found != null && Array.Exists(found, obj => obj?.Attributes?.GetUserString("forsk:generated") == "1")
                });
            }
            return list;
        }

        static string PathOf(Layer layer)
        {
            return string.IsNullOrEmpty(layer.FullPath) ? layer.Name : layer.FullPath;
        }

        static bool? Visible(RhinoDoc doc, string path)
        {
            var layer = Find(doc, path);
            return layer == null ? (bool?)null : layer.IsVisible;
        }

        static void SetVisible(RhinoDoc doc, string path, bool visible)
        {
            var layer = Find(doc, path);
            if (layer == null || layer.IsVisible == visible) return;
            layer.IsVisible = visible;
            doc.Layers.Modify(layer, layer.Index, true);
        }

        static Layer Find(RhinoDoc doc, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            foreach (var layer in doc.Layers)
            {
                if (layer == null || layer.IsDeleted) continue;
                var full = PathOf(layer);
                if (full.Equals(path, StringComparison.OrdinalIgnoreCase)) return layer;
                if (layer.Name.Equals(path, StringComparison.OrdinalIgnoreCase)) return layer;
            }
            return null;
        }
    }
}
