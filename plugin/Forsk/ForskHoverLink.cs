using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// A viewport hover answered in the chat: the receipt rows that name the
    /// element under the pointer. The thread is read once into a dictionary
    /// (element id to the receipts that name it), so a mouse move costs one
    /// lookup, and nothing is sent until the hovered element changes. The
    /// setting ForskHoverFocus turns it off with the rest of hover to focus.
    /// No RhinoCommon, so it tests headless.
    /// </summary>
    public sealed class ForskHoverLink
    {
        /// <summary>The pointer must rest this long before the viewport is asked what is under it.</summary>
        public const int DebounceMs = 120;

        /// <summary>The class the page puts on a highlighted row (window.html).</summary>
        public const string Style = "hl";

        readonly Dictionary<string, List<string>> _byElement = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string _hovered = "";
        string _shown = "";

        /// <summary>How many elements the chat names.</summary>
        public int Elements => _byElement.Count;

        /// <summary>
        /// Reads the thread again: each receipt row, by the ids its subject and
        /// text name. Returns the rows to show now when the highlight changed
        /// under a pointer that has not moved, else null.
        /// </summary>
        public string[] Rebuild(IEnumerable<JObject> thread)
        {
            _byElement.Clear();
            foreach (var item in thread ?? Enumerable.Empty<JObject>())
            {
                if (item?["role"]?.ToString() != "receipt") continue;
                var row = item["id"]?.ToString();
                if (string.IsNullOrEmpty(row)) continue;
                foreach (var id in ForskReceipt.IdsIn(item["subject"]?.ToString() + " " + item["text"]?.ToString()))
                {
                    if (!_byElement.TryGetValue(id, out var rows)) _byElement[id] = rows = new List<string>();
                    if (!rows.Contains(row)) rows.Add(row);
                }
            }
            return Show(Rows(Split(_hovered)));
        }

        /// <summary>The receipt rows that name any of these ids, in thread order of first naming.</summary>
        public string[] Rows(IEnumerable<string> elementIds)
        {
            var rows = new List<string>();
            foreach (var id in elementIds ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(id) || !_byElement.TryGetValue(id, out var named)) continue;
                foreach (var row in named)
                    if (!rows.Contains(row)) rows.Add(row);
            }
            return rows.ToArray();
        }

        /// <summary>
        /// The pointer rests over an element, named by these ids (a wall's
        /// forsk:id; an opening's id and its mark), or over nothing. Returns the
        /// rows to highlight, empty to clear, or null when nothing changed:
        /// the same element again, or a different one that lights the same rows.
        /// </summary>
        public string[] Hover(bool enabled, IEnumerable<string> elementIds)
        {
            var ids = enabled ? (elementIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).ToList() : new List<string>();
            var key = string.Join("|", ids);
            if (string.Equals(key, _hovered, StringComparison.Ordinal)) return null;
            _hovered = key;
            return Show(Rows(ids));
        }

        /// <summary>The pointer is gone from the viewport.</summary>
        public string[] Clear() => Hover(false, null);

        string[] Show(string[] rows)
        {
            var key = string.Join("|", rows);
            if (string.Equals(key, _shown, StringComparison.Ordinal)) return null;
            _shown = key;
            return rows;
        }

        static IEnumerable<string> Split(string key) =>
            string.IsNullOrEmpty(key) ? Enumerable.Empty<string>() : key.Split('|');
    }
}
