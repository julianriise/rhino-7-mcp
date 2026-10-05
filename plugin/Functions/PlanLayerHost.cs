using System;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions
{
    /// <summary>
    /// Hides the flat drawing after Generate 3D and shows it again. The layer
    /// paths are document user text, so the Show 2D chip knows which ones.
    /// Layer visibility changes inside the open undo record, so Undo restores them.
    /// </summary>
    public static class PlanLayerHost
    {
        static readonly string[] ModelKinds =
        {
            "wall", "floor", "roof", "opening", "opening_marker", "room", "room_plate", "stair", "analysis"
        };

        /// <summary>Hide the visible source layers. True when those layers are now hidden.</summary>
        public static bool Hide(RhinoDoc doc)
        {
            if (doc == null) return false;
            var chosen = PlanLayers.ToHide(Describe(doc));
            if (chosen.Count > 0)
            {
                foreach (var path in chosen)
                    SetVisible(doc, path, false);
                doc.Strings.SetString(PlanLayers.Section, PlanLayers.Entry, PlanLayers.Store(chosen));
                return true;
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
                list.Add(new PlanLayers.LayerInfo
                {
                    Path = string.IsNullOrEmpty(layer.FullPath) ? layer.Name : layer.FullPath,
                    Name = layer.Name,
                    Visible = layer.IsVisible,
                    HoldsModel = HoldsModel(doc, layer)
                });
            }
            return list;
        }

        static bool HoldsModel(RhinoDoc doc, Layer layer)
        {
            foreach (var obj in Enumerate(doc))
            {
                if (obj?.Attributes == null || obj.Attributes.LayerIndex != layer.Index) continue;
                if (obj.Attributes.GetUserString("forsk:generated") != "1") continue;
                var kind = obj.Attributes.GetUserString("forsk:kind") ?? "";
                foreach (var model in ModelKinds)
                    if (kind.Equals(model, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static IEnumerable<RhinoObject> Enumerate(RhinoDoc doc)
        {
            var settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = true,
                ActiveObjects = true,
                ReferenceObjects = false,
                DeletedObjects = false,
                IncludeLights = false,
                IncludeGrips = false
            };
            return doc.Objects.GetObjectList(settings);
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
                var full = string.IsNullOrEmpty(layer.FullPath) ? layer.Name : layer.FullPath;
                if (full.Equals(path, StringComparison.OrdinalIgnoreCase)) return layer;
                if (layer.Name.Equals(path, StringComparison.OrdinalIgnoreCase)) return layer;
            }
            return null;
        }
    }
}
