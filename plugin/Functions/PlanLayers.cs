using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions
{
    /// <summary>
    /// Everything Generate 3D hides, and the list it stores so Show 2D can
    /// bring the same layers back. After the bake only the model shows: a layer
    /// that holds a Forsk object stays visible (A-ROOM carries the room plates
    /// and markers), and so does its parent. A layer already off stays off and
    /// is not stored, so Show 2D leaves it off. Nothing is deleted. Pure: no Rhino.
    /// </summary>
    public static class PlanLayers
    {
        public const string Section = "forsk";
        public const string Entry = "hidden_plan_layers";

        public sealed class LayerInfo
        {
            public string Path;
            public bool Visible;
            /// <summary>At least one object is on this layer.</summary>
            public bool HasObjects;
            /// <summary>A Forsk object (forsk:generated) is on this layer.</summary>
            public bool HoldsModel;
        }

        /// <summary>Visible layers with objects and no Forsk object, and not the parent of a layer that has one.</summary>
        public static List<string> ToHide(IEnumerable<LayerInfo> layers)
        {
            var list = new List<string>();
            if (layers == null) return list;
            var all = new List<LayerInfo>();
            foreach (var layer in layers)
                if (layer != null && !string.IsNullOrWhiteSpace(layer.Path)) all.Add(layer);
            foreach (var layer in all)
            {
                if (!layer.Visible || !layer.HasObjects || layer.HoldsModel) continue;
                if (all.Exists(other => other.HoldsModel && IsParentOf(layer.Path, other.Path))) continue;
                list.Add(layer.Path);
            }
            return list;
        }

        /// <summary>The stored paths, then the new ones not already stored: a second bake keeps the first one's list.</summary>
        public static List<string> Merge(string stored, IEnumerable<string> hidden)
        {
            var list = Stored(stored);
            if (hidden != null)
                foreach (var path in hidden)
                    if (!string.IsNullOrWhiteSpace(path) && !list.Exists(p => p.Equals(path.Trim(), StringComparison.OrdinalIgnoreCase)))
                        list.Add(path.Trim());
            return list;
        }

        /// <summary>One full path per line. Empty when nothing was hidden.</summary>
        public static string Store(IEnumerable<string> paths)
        {
            var list = new List<string>();
            if (paths != null)
                foreach (var path in paths)
                    if (!string.IsNullOrWhiteSpace(path)) list.Add(path.Trim());
            return string.Join("\n", list);
        }

        public static List<string> Stored(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var part in text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = part.Trim();
                if (path.Length > 0) list.Add(path);
            }
            return list;
        }

        /// <summary>
        /// True when at least one remembered layer still exists and every one
        /// that exists is off. A missing layer is ignored. <paramref name="visible"/>
        /// returns null when the layer is gone.
        /// </summary>
        public static bool AreHidden(string text, Func<string, bool?> visible)
        {
            var any = false;
            foreach (var path in Stored(text))
            {
                var shown = visible == null ? (bool?)null : visible(path);
                if (!shown.HasValue) continue;
                any = true;
                if (shown.Value) return false;
            }
            return any;
        }

        static bool IsParentOf(string parent, string child)
        {
            return child.StartsWith(parent + "::", StringComparison.OrdinalIgnoreCase);
        }
    }
}
