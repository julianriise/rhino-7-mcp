using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions
{
    /// <summary>
    /// The flat drawing Generate 3D hides, and the list it stores so Show 2D
    /// can bring the same layers back. A layer that holds the new model stays
    /// visible: A-ROOM carries the room plates and markers. Nothing is deleted.
    /// Pure: no Rhino.
    /// </summary>
    public static class PlanLayers
    {
        public const string Section = "forsk";
        public const string Entry = "hidden_plan_layers";

        /// <summary>Leaf names of the 2D layers the bake reads. A-ROOM is not here.</summary>
        public static readonly string[] Names = { "wall", "door", "window", "room", "plan", "label", "space_divider" };

        public sealed class LayerInfo
        {
            public string Path;
            public string Name;
            public bool Visible;
            /// <summary>A generated wall, floor, roof, opening, room or plate is on this layer.</summary>
            public bool HoldsModel;
        }

        /// <summary>Visible source layers that do not hold the model. These are the ones to hide.</summary>
        public static List<string> ToHide(IEnumerable<LayerInfo> layers)
        {
            var list = new List<string>();
            if (layers == null) return list;
            foreach (var layer in layers)
            {
                if (layer == null || !layer.Visible || layer.HoldsModel) continue;
                if (!IsSource(layer.Name) || string.IsNullOrWhiteSpace(layer.Path)) continue;
                list.Add(layer.Path);
            }
            return list;
        }

        public static bool IsSource(string name)
        {
            var leaf = Leaf(name);
            if (leaf.Length == 0) return false;
            foreach (var source in Names)
                if (leaf.Equals(source, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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

        static string Leaf(string name)
        {
            var text = (name ?? "").Trim();
            var cut = text.LastIndexOf("::", StringComparison.Ordinal);
            return cut >= 0 ? text.Substring(cut + 2).Trim() : text;
        }
    }
}
