using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Which closed curve stands for a room. Detection replaces that curve by
/// forsk:id and deletes the copies, including a second outline from an
/// earlier run. No RhinoCommon, so it tests headless.
/// </summary>
public static class RoomCurves
{
    public sealed class Curve
    {
        public string Id;
        public string ForskId;
        public string Ring;
        public bool Detected;
        public bool Marker;
    }

    public sealed class Action
    {
        public string Keep;
        public readonly List<string> Delete = new List<string>();
    }

    /// <summary>
    /// One action per room id. The keeper is the best curve with that forsk:id
    /// or that ring. The other matches are deleted. A room with no curve is an add.
    /// </summary>
    public static List<Action> Plan(IList<Curve> curves, IList<string> roomIds, IList<string> rings)
    {
        var plans = new List<Action>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var count = roomIds == null ? 0 : roomIds.Count;
        for (var i = 0; i < count; i++)
        {
            var roomId = roomIds[i] ?? "";
            var ring = rings != null && i < rings.Count ? rings[i] ?? "" : "";
            var action = new Action();
            Curve best = null;
            var bestScore = int.MinValue;
            foreach (var curve in curves ?? new Curve[0])
            {
                if (!Candidate(curve, roomId, ring) || used.Contains(curve.Id)) continue;
                var score = Score(curve, roomId);
                if (best != null && (score < bestScore || (score == bestScore && string.CompareOrdinal(curve.Id, best.Id) >= 0)))
                    continue;
                best = curve;
                bestScore = score;
            }
            if (best != null)
            {
                action.Keep = best.Id;
                used.Add(best.Id);
            }
            foreach (var curve in curves ?? new Curve[0])
            {
                if (!Candidate(curve, roomId, ring) || used.Contains(curve.Id)) continue;
                action.Delete.Add(curve.Id);
                used.Add(curve.Id);
            }
            plans.Add(action);
        }
        return plans;
    }

    /// <summary>
    /// Marker and detected curves that no room kept. A non-detected marker that
    /// is the only curve on its ring stays: it is that room's record. A detected
    /// curve nobody kept is stale and goes, even when it is the only one.
    /// </summary>
    public static List<string> Leftovers(IList<Curve> curves, IList<Action> plans)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var drop = new HashSet<string>(StringComparer.Ordinal);
        if (plans != null)
        {
            foreach (var plan in plans)
            {
                if (plan == null) continue;
                if (!string.IsNullOrEmpty(plan.Keep)) keep.Add(plan.Keep);
                foreach (var id in plan.Delete)
                    if (!string.IsNullOrEmpty(id)) drop.Add(id);
            }
        }
        var onRing = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var curve in curves ?? new Curve[0])
        {
            if (curve == null || string.IsNullOrEmpty(curve.Ring)) continue;
            onRing.TryGetValue(curve.Ring, out var n);
            onRing[curve.Ring] = n + 1;
        }
        var extra = new List<string>();
        foreach (var curve in curves ?? new Curve[0])
        {
            if (curve == null || string.IsNullOrEmpty(curve.Id)) continue;
            if (keep.Contains(curve.Id) || drop.Contains(curve.Id)) continue;
            if (!curve.Marker && !curve.Detected) continue;
            if (curve.Marker && !curve.Detected
                && onRing.TryGetValue(curve.Ring ?? "", out var n) && n == 1)
                continue;
            extra.Add(curve.Id);
        }
        return extra;
    }

    /// <summary>
    /// The ring's identity at 1 mm. The start point, the direction, and a
    /// repeated closing point do not change it.
    /// </summary>
    public static string RingKey(IList<RoomDetect.Pt> ring)
    {
        if (ring == null || ring.Count == 0) return "";
        var pts = new List<long[]>(ring.Count);
        foreach (var p in ring)
            pts.Add(new[] { (long)Math.Round(p.X), (long)Math.Round(p.Y) });
        if (pts.Count > 1 && pts[0][0] == pts[pts.Count - 1][0] && pts[0][1] == pts[pts.Count - 1][1])
            pts.RemoveAt(pts.Count - 1);
        if (pts.Count == 0) return "";
        var forward = Canon(pts);
        pts.Reverse();
        var backward = Canon(pts);
        return string.CompareOrdinal(forward, backward) <= 0 ? forward : backward;
    }

    static string Canon(List<long[]> pts)
    {
        var start = 0;
        for (var i = 1; i < pts.Count; i++)
            if (Compare(pts[i], pts[start]) < 0) start = i;
        var parts = new string[pts.Count];
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[(start + i) % pts.Count];
            parts[i] = p[0].ToString(CultureInfo.InvariantCulture) + "," + p[1].ToString(CultureInfo.InvariantCulture);
        }
        return string.Join(";", parts);
    }

    static int Compare(long[] a, long[] b)
    {
        var byX = a[0].CompareTo(b[0]);
        return byX != 0 ? byX : a[1].CompareTo(b[1]);
    }

    static bool Candidate(Curve curve, string roomId, string ring)
    {
        if (curve == null || string.IsNullOrEmpty(curve.Id)) return false;
        if (!string.IsNullOrEmpty(roomId) && string.Equals(curve.ForskId, roomId, StringComparison.Ordinal))
            return true;
        return !string.IsNullOrEmpty(ring) && string.Equals(curve.Ring, ring, StringComparison.Ordinal);
    }

    /// <summary>
    /// A curve the user drew outranks a detected outline, which outranks the
    /// marker copy. The same forsk:id outranks a ring-only match.
    /// </summary>
    static int Score(Curve curve, string roomId)
    {
        var same = !string.IsNullOrEmpty(roomId) && string.Equals(curve.ForskId, roomId, StringComparison.Ordinal);
        if (!curve.Marker && !curve.Detected) return same ? 5 : 4;
        if (!curve.Marker) return same ? 3 : 2;
        return same ? 1 : 0;
    }
}
