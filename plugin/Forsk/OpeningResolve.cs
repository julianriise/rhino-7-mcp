using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// An opening is its marker. A click may land on the frame, the leaf, the
    /// glass, the block, or a group of those, and the marker may be hidden.
    /// This maps any of them to the marker id. A wall is not an opening, even
    /// in the same group. No RhinoCommon: the document walk lives with the tools.
    /// </summary>
    public static class OpeningResolve
    {
        /// <summary>One object the document walk already read. Ids are strings so tests need no Rhino.</summary>
        public sealed class Part
        {
            public string Id;
            public string Kind;
            /// <summary>forsk:part: frame, leaf, glass, sash, sill, threshold, track.</summary>
            public string Member;
            /// <summary>forsk:marker_id, or forsk:marker on a room plate.</summary>
            public string Marker;
            public string ForskId;
            /// <summary>Rhino group indexes, sorted and comma-separated.</summary>
            public string Group;
        }

        static readonly string[] NamedParts =
        {
            "frame", "leaf", "glass", "sash", "sill", "threshold", "track"
        };

        /// <summary>The marker id this part stands for, or null when it is not an opening.</summary>
        public static string MarkerOf(IReadOnlyList<Part> all, Part selected)
        {
            if (selected == null || string.IsNullOrWhiteSpace(selected.Id)) return null;
            if (IsMarker(selected)) return selected.Id;
            if (!IsOpeningPart(selected)) return null;
            if (!string.IsNullOrWhiteSpace(selected.Marker)) return selected.Marker.Trim();
            var direct = ByForskId(all, selected.ForskId);
            if (direct != null) return direct;
            if (string.IsNullOrWhiteSpace(selected.Group) || all == null) return null;
            foreach (var other in all)
            {
                if (other == null || !SharesGroup(selected.Group, other.Group)) continue;
                if (IsMarker(other)) return other.Id;
            }
            foreach (var other in all)
            {
                if (other == null || Same(other, selected)) continue;
                if (!SharesGroup(selected.Group, other.Group) || !IsOpeningPart(other)) continue;
                if (!string.IsNullOrWhiteSpace(other.Marker)) return other.Marker.Trim();
                var via = ByForskId(all, other.ForskId);
                if (via != null) return via;
            }
            return null;
        }

        /// <summary>Marker ids for the picked parts, one per opening, in pick order.</summary>
        public static List<string> Selected(IReadOnlyList<Part> all, IEnumerable<Part> picked)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (picked == null) return ids;
            foreach (var part in picked)
            {
                var id = MarkerOf(all, part);
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                ids.Add(id);
            }
            return ids;
        }

        /// <summary>True when the comma-separated group indexes share one.</summary>
        public static bool SharesGroup(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            var left = new HashSet<string>(Split(a), StringComparer.Ordinal);
            foreach (var token in Split(b))
                if (left.Contains(token)) return true;
            return false;
        }

        static string ByForskId(IReadOnlyList<Part> all, string forskId)
        {
            if (all == null || string.IsNullOrWhiteSpace(forskId)) return null;
            foreach (var other in all)
            {
                if (!IsMarker(other)) continue;
                if (SameText(other.ForskId, forskId) || SameText(other.Id, forskId))
                    return other.Id;
            }
            return null;
        }

        static bool IsMarker(Part part) =>
            part != null && SameText(part.Kind, "opening_marker");

        static bool IsOpeningPart(Part part)
        {
            if (part == null || IsMarker(part)) return false;
            if (SameText(part.Kind, "opening")) return true;
            return Named(part.Kind) || Named(part.Member);
        }

        static bool Named(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (var name in NamedParts)
                if (SameText(name, value)) return true;
            return false;
        }

        static bool Same(Part a, Part b) => a != null && b != null && SameText(a.Id, b.Id);

        static bool SameText(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        static IEnumerable<string> Split(string group)
        {
            foreach (var token in group.Split(','))
            {
                var trimmed = token.Trim();
                if (trimmed.Length > 0) yield return trimmed;
            }
        }
    }
}
