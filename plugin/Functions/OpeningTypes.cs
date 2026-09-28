using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Opening type registry. No Rhino document. Lookup is by string id so a
/// later kit can add an entry. Plan symbols are generated from the record.
/// </summary>
public static class OpeningTypes
{
    public const string TypeKey = "forsk:opening_type";
    public const string HandKey = "forsk:hand";
    public const string SwingKey = "forsk:swing";

    public sealed class TypeDef
    {
        public string Id;
        public string Kind;
        public string Label;
        public string ShortName;
        public bool HasHand;
        public bool HasSwing;
        public string DefaultSwing;
        public string NoKeyName;
    }

    public sealed class Record
    {
        public string TypeId;
        public string Kind;
        public string Hand;
        public string Swing;
        public TypeDef Def;
    }

    public sealed class Edit
    {
        public Record Before;
        public Record After;
        public bool TypeChanged;
        public bool HandChanged;
        public bool SwingChanged;

        public bool Changed
        {
            get { return TypeChanged || HandChanged || SwingChanged; }
        }
    }

    public sealed class ReceiptRow
    {
        public string Kind;
        public string ShortName;
        public string Host;
        public bool TypeChanged;
        public bool HandChanged;
        public bool SwingChanged;
    }

    static readonly TypeDef[] Catalog = new TypeDef[]
    {
        Def("door.hinged_single", "door", "Hinged door", "hinged", true, true, "in", "Hinged doors"),
        Def("door.hinged_double", "door", "Double door", "double", false, true, "in", "Double doors"),
        Def("door.sliding", "door", "Sliding door", "sliding", true, false, null, "Sliding doors"),
        Def("door.pocket", "door", "Pocket door", "pocket", true, false, null, "Pocket doors"),
        Def("window.fixed", "window", "Fixed window", "fixed", false, false, null, "Fixed windows"),
        Def("window.side_hung", "window", "Side-hung window", "side-hung", true, true, "in", "Side-hung windows"),
        Def("window.top_hung", "window", "Top-hung window", "top-hung", false, true, "out", "Top-hung windows")
    };

    static readonly Dictionary<string, TypeDef> ById = Index(Catalog);

    public static IList<TypeDef> All
    {
        get { return Catalog; }
    }

    public static bool TryGet(string id, out TypeDef def)
    {
        def = null;
        if (string.IsNullOrWhiteSpace(id)) return false;
        return ById.TryGetValue(id.Trim(), out def);
    }

    public static Record DefaultRecord(string kind)
    {
        if (TryRead(kind, null, null, null, out var record, out _))
            return record;
        return null;
    }

    /// <summary>
    /// Missing type, hand, and swing become the defaults for the kind.
    /// A key the type does not use is dropped.
    /// </summary>
    public static bool TryRead(
        string kind,
        string typeRaw,
        string handRaw,
        string swingRaw,
        out Record record,
        out string error)
    {
        record = null;
        error = "";
        if (!IsKind(kind))
        {
            error = "opening_kind must be door or window.";
            return false;
        }

        var tag = NormKind(kind);
        TypeDef def;
        if (string.IsNullOrWhiteSpace(typeRaw))
            def = DefaultDef(tag);
        else if (!TryGet(typeRaw, out def))
        {
            error = "Unknown opening type.";
            return false;
        }
        else if (!string.Equals(def.Kind, tag, StringComparison.Ordinal))
        {
            error = KindMismatch(tag);
            return false;
        }

        string hand = null;
        if (def.HasHand)
        {
            if (string.IsNullOrWhiteSpace(handRaw))
                hand = "L";
            else if (!TryStoredHand(handRaw, out hand))
            {
                error = "hand must be L or R.";
                return false;
            }
        }

        string swing = null;
        if (def.HasSwing)
        {
            if (string.IsNullOrWhiteSpace(swingRaw))
                swing = string.IsNullOrEmpty(def.DefaultSwing) ? "in" : def.DefaultSwing;
            else if (!TryStoredSwing(swingRaw, out swing))
            {
                error = "swing must be in or out.";
                return false;
            }
        }

        record = Make(def, hand, swing);
        return true;
    }

    public static bool TryReadKeys(
        IDictionary<string, string> keys,
        string kind,
        out Record record,
        out string error)
    {
        string type = null;
        string hand = null;
        string swing = null;
        if (keys != null)
        {
            keys.TryGetValue(TypeKey, out type);
            keys.TryGetValue(HandKey, out hand);
            keys.TryGetValue(SwingKey, out swing);
        }
        return TryRead(kind, type, hand, swing, out record, out error);
    }

    public static Dictionary<string, string> ToKeys(Record record)
    {
        var keys = new Dictionary<string, string>();
        if (record == null || string.IsNullOrEmpty(record.TypeId))
            return keys;
        keys[TypeKey] = record.TypeId;
        if (!string.IsNullOrEmpty(record.Hand))
            keys[HandKey] = record.Hand;
        if (!string.IsNullOrEmpty(record.Swing))
            keys[SwingKey] = record.Swing;
        return keys;
    }

    /// <summary>
    /// Apply a type, hand, or swing edit. Null fields stay. flip toggles.
    /// A key the resulting type does not use is refused before a record is returned.
    /// </summary>
    public static bool TryApply(
        Record current,
        string typeRaw,
        string handRaw,
        string swingRaw,
        out Edit edit,
        out string error)
    {
        edit = null;
        error = "";
        if (current == null || current.Def == null)
        {
            error = "Unknown opening type.";
            return false;
        }

        TypeDef def = current.Def;
        if (!string.IsNullOrWhiteSpace(typeRaw))
        {
            if (!TryGet(typeRaw, out def))
            {
                error = "Unknown opening type.";
                return false;
            }
            if (!string.Equals(def.Kind, current.Kind, StringComparison.Ordinal))
            {
                error = KindMismatch(current.Kind);
                return false;
            }
        }

        var hand = def.HasHand ? (string.IsNullOrEmpty(current.Hand) ? "L" : current.Hand) : null;
        var swing = def.HasSwing
            ? (string.IsNullOrEmpty(current.Swing)
                ? (string.IsNullOrEmpty(def.DefaultSwing) ? "in" : def.DefaultSwing)
                : current.Swing)
            : null;

        if (!string.IsNullOrWhiteSpace(handRaw))
        {
            if (!def.HasHand)
            {
                error = MissingKey(def, "hand");
                return false;
            }
            if (!TryEditHand(handRaw, hand, out hand))
            {
                error = "hand must be L, R, or flip.";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(swingRaw))
        {
            if (!def.HasSwing)
            {
                error = MissingKey(def, "swing");
                return false;
            }
            if (!TryEditSwing(swingRaw, swing, out swing))
            {
                error = "swing must be in, out, or flip.";
                return false;
            }
        }

        var after = Make(def, hand, swing);
        edit = new Edit
        {
            Before = current,
            After = after,
            TypeChanged = !string.Equals(current.TypeId, after.TypeId, StringComparison.Ordinal),
            HandChanged = !string.Equals(current.Hand ?? "", after.Hand ?? "", StringComparison.Ordinal),
            SwingChanged = !string.Equals(current.Swing ?? "", after.Swing ?? "", StringComparison.Ordinal)
        };
        return true;
    }

    public static string KindMismatch(string kind)
    {
        if (string.Equals(NormKind(kind), "window", StringComparison.Ordinal))
            return "That is a window. Pick fixed, side_hung, or top_hung.";
        return "That is a door. Pick hinged_single, hinged_double, sliding, or pocket.";
    }

    public static string MissingKey(TypeDef def, string key)
    {
        var name = def == null || string.IsNullOrEmpty(def.NoKeyName) ? "Openings" : def.NoKeyName;
        var which = string.Equals(key, "hand", StringComparison.Ordinal) ? "hand" : "swing";
        return name + " have no " + which + ".";
    }

    public static string AlreadyLine(string shortName)
    {
        return "Opening already " + (string.IsNullOrEmpty(shortName) ? "hinged" : shortName) + ".";
    }

    public static string AlreadyHand(string hand)
    {
        return "Opening hand already " + (string.IsNullOrEmpty(hand) ? "L" : hand) + ".";
    }

    public static string AlreadySwing(string swing)
    {
        return "Opening swing already " + (string.IsNullOrEmpty(swing) ? "in" : swing) + ".";
    }

    public static string ChangedLine(int count, string kind, string shortName, IList<string> hosts)
    {
        return "Changed " + Count(count) + " " + Noun(kind, count)
            + " to " + shortName + " on " + HostList(hosts);
    }

    public static string SwingLine(int count, string kind, IList<string> hosts)
    {
        return "Flipped swing on " + Count(count) + " " + Noun(kind, count) + " on " + HostList(hosts);
    }

    public static string HandLine(int count, string kind, IList<string> hosts)
    {
        return "Changed hand on " + Count(count) + " " + Noun(kind, count) + " on " + HostList(hosts);
    }

    public static string HandAndSwingLine(int count, string kind, IList<string> hosts)
    {
        return "Changed hand and flipped swing on " + Count(count) + " " + Noun(kind, count)
            + " on " + HostList(hosts);
    }

    public static string Receipt(IList<ReceiptRow> rows)
    {
        var lines = new List<string>();
        AppendGrouped(lines, rows, "type");
        AppendGrouped(lines, rows, "swing");
        AppendGrouped(lines, rows, "hand");
        AppendGrouped(lines, rows, "both");
        if (lines.Count == 0) return AlreadyLine("hinged");
        return string.Join(" ", lines.ToArray());
    }

    /// <summary>
    /// +1 when hand is L and xLeft is +1. R mirrors it. Park side uses the same sign.
    /// </summary>
    public static int HandSign(string hand, int xLeft)
    {
        var left = xLeft < 0 ? -1 : 1;
        return string.Equals(hand, "R", StringComparison.OrdinalIgnoreCase) ? -left : left;
    }

    public static int HandSign(Record record, int xLeft)
    {
        return HandSign(record == null ? null : record.Hand, xLeft);
    }

    /// <summary>In swings toward +yInward. Out mirrors it.</summary>
    public static int SwingSign(Record record, int yInward)
    {
        var inward = yInward < 0 ? -1 : 1;
        var outward = record != null && string.Equals(record.Swing, "out", StringComparison.OrdinalIgnoreCase);
        return (outward ? -1 : 1) * inward;
    }

    /// <summary>Sliding track sits on the face opposite Inward.</summary>
    public static int TrackSign(int yInward)
    {
        return yInward < 0 ? 1 : -1;
    }

    /// <summary>
    /// Sill &lt; cut &lt; head is solid. Fully above the cut is dashed.
    /// Fully below the cut is solid. Doors are always solid.
    /// </summary>
    public static bool AboveCut(Record record, double sill, double head, double cutZ)
    {
        if (record == null) return false;
        if (string.Equals(record.Kind, "door", StringComparison.OrdinalIgnoreCase)) return false;
        if (sill < cutZ && cutZ < head) return false;
        if (head <= cutZ) return false;
        return sill >= cutZ;
    }

    public sealed class PlanFrame
    {
        public double OuterHalf;
        public double InnerHalf;
        public double HalfThick;
        public double VoidHalf;
        public double Sill;
        public double Head;
        public double CutZ;
        public int YInward;
        public int XLeft;
    }

    /// <summary>Paper cap height 2.5 mm times the plan scale, in model mm.</summary>
    public static double PlanAnnotationHeight(int scale)
    {
        if (scale < 1) return 0;
        return 2.5 * scale;
    }

    /// <summary>
    /// Smallest standard scale denominator at or above <paramref name="need"/>:
    /// step 5 up to 50, 10 up to 100, 25 up to 500, then 50. 34.55 → 35,
    /// 137 → 150. Rounding up keeps the drawing inside the detail.
    /// </summary>
    public static int RoundScaleUp(double need)
    {
        if (double.IsNaN(need) || double.IsInfinity(need) || need <= 0) return 0;
        var step = need <= 50 ? 5.0 : need <= 100 ? 10.0 : need <= 500 ? 25.0 : 50.0;
        // Float noise just above a step (125.0000000001) stays on it.
        return (int)(Math.Ceiling(need / step - 1e-9) * step);
    }

    public static readonly int[] ScaleBarLengthsM = { 1, 2, 3, 5, 10, 20, 30, 50, 100 };
    public const double ScaleBarMinMm = 40.0;
    public const double ScaleBarMaxMm = 80.0;

    /// <summary>Paper length in mm of <paramref name="meters"/> at 1:scale.</summary>
    public static double ScaleBarPaperMm(int meters, int scale)
    {
        if (scale < 1 || meters < 1) return 0;
        return meters * 1000.0 / scale;
    }

    /// <summary>
    /// Scale bar total length in metres: the listed length whose paper
    /// length lies in 40..80 mm and is nearest 60 mm. Every scale from 1:13
    /// to 1:2500 has one; outside that, the one nearest the band.
    /// </summary>
    public static int ScaleBarMeters(int scale)
    {
        if (scale < 1) return 0;
        var best = ScaleBarLengthsM[0];
        var bestCost = double.MaxValue;
        foreach (var meters in ScaleBarLengthsM)
        {
            var paper = ScaleBarPaperMm(meters, scale);
            var outside = paper < ScaleBarMinMm ? ScaleBarMinMm - paper
                : paper > ScaleBarMaxMm ? paper - ScaleBarMaxMm
                : 0.0;
            // Outside the band always loses to inside it.
            var cost = outside * 1000.0 + Math.Abs(paper - 60.0);
            if (cost < bestCost - 1e-9)
            {
                best = meters;
                bestCost = cost;
            }
        }
        return best;
    }

    /// <summary>Segments of round length: 4 for 2 and 20 m (0.5, 5 m each), else 5 (3 m: 0.6 m).</summary>
    public static int ScaleBarSegments(int meters)
    {
        return meters == 2 || meters == 20 ? 4 : 5;
    }

    /// <summary>Scale bar end label: "5 m".</summary>
    public static string ScaleBarLabel(int meters)
    {
        return meters.ToString(CultureInfo.InvariantCulture) + " m";
    }

    /// <summary>Title block cells in order, blank values dropped.</summary>
    public static List<KeyValuePair<string, string>> TitleCells(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var cells = new List<KeyValuePair<string, string>>();
        if (fields == null) return cells;
        foreach (var field in fields)
        {
            var value = field.Value?.Trim();
            if (string.IsNullOrEmpty(value) || value == "—" || value == "-") continue;
            cells.Add(new KeyValuePair<string, string>(field.Key, value));
        }
        return cells;
    }

    /// <summary>
    /// Cell widths that sum to <paramref name="total"/>, in proportion to
    /// the longer of caption and value (at least 6 characters).
    /// </summary>
    public static double[] TitleCellWidths(IList<KeyValuePair<string, string>> cells, double total)
    {
        if (cells == null || cells.Count == 0 || total <= 0) return new double[0];
        var widths = new double[cells.Count];
        var sum = 0.0;
        for (var i = 0; i < cells.Count; i++)
        {
            var chars = Math.Max(cells[i].Key?.Length ?? 0, cells[i].Value?.Length ?? 0);
            widths[i] = Math.Max(6, chars);
            sum += widths[i];
        }
        for (var i = 0; i < widths.Length; i++)
            widths[i] = total * widths[i] / sum;
        return widths;
    }

    /// <summary>
    /// Printed height in paper mm of model text seen through a 1:scale
    /// detail. Layout-space annotation scaling draws the text at its own
    /// height on paper, so plan text needs it off.
    /// </summary>
    public static double PaperTextHeight(double modelHeight, int scale, bool layoutScaling)
    {
        if (scale < 1 || modelHeight <= 0) return 0;
        return layoutScaling ? modelHeight : modelHeight / scale;
    }

    /// <summary>
    /// Move the opening onto the measured wall centreline. (dx, dy) is the
    /// thickness direction. (ax, ay) is a point on that centreline.
    /// </summary>
    public static void WallCenter(
        double ox, double oy, double ax, double ay, double dx, double dy,
        out double x, out double y)
    {
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9)
        {
            x = ox;
            y = oy;
            return;
        }
        dx /= len;
        dy /= len;
        var shift = (ax - ox) * dx + (ay - oy) * dy;
        x = ox + dx * shift;
        y = oy + dy * shift;
    }

    /// <summary>
    /// Centroid when it lies in the polygon, otherwise the inside sample
    /// nearest the centroid.
    /// </summary>
    public static bool TryInteriorPoint(double[] xs, double[] ys, out double x, out double y)
    {
        x = 0;
        y = 0;
        var ring = CleanRing(xs, ys);
        if (ring.Count < 3) return false;
        double area2 = 0, cx = 0, cy = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var cross = a[0] * b[1] - b[0] * a[1];
            area2 += cross;
            cx += (a[0] + b[0]) * cross;
            cy += (a[1] + b[1]) * cross;
        }
        double gx, gy;
        if (Math.Abs(area2) < 1e-6)
        {
            double sx = 0, sy = 0;
            foreach (var p in ring) { sx += p[0]; sy += p[1]; }
            gx = sx / ring.Count;
            gy = sy / ring.Count;
        }
        else
        {
            gx = cx / (3.0 * area2);
            gy = cy / (3.0 * area2);
        }
        if (PointInPolygon(gx, gy, ring))
        {
            x = gx;
            y = gy;
            return true;
        }

        double minX = ring[0][0], maxX = ring[0][0], minY = ring[0][1], maxY = ring[0][1];
        foreach (var p in ring)
        {
            if (p[0] < minX) minX = p[0];
            if (p[0] > maxX) maxX = p[0];
            if (p[1] < minY) minY = p[1];
            if (p[1] > maxY) maxY = p[1];
        }
        var bestD = double.MaxValue;
        var found = false;
        const int steps = 8;
        for (var ix = 0; ix <= steps; ix++)
        {
            for (var iy = 0; iy <= steps; iy++)
            {
                var sx = minX + (maxX - minX) * ix / steps;
                var sy = minY + (maxY - minY) * iy / steps;
                if (!PointInPolygon(sx, sy, ring)) continue;
                var d = (sx - gx) * (sx - gx) + (sy - gy) * (sy - gy);
                if (d < bestD)
                {
                    bestD = d;
                    x = sx;
                    y = sy;
                    found = true;
                }
            }
        }
        return found;
    }

    /// <summary>Plan section of one solid: its rings, read even-odd.</summary>
    public sealed class PlanRegion
    {
        public List<double[]> Xs = new List<double[]>();
        public List<double[]> Ys = new List<double[]>();
    }

    /// <summary>True when (x, y) is inside any region (even-odd within each).</summary>
    public static bool InRegions(double x, double y, IList<PlanRegion> regions)
    {
        if (regions == null) return false;
        foreach (var region in regions)
        {
            if (region == null) continue;
            var inside = false;
            for (var i = 0; i < region.Xs.Count; i++)
                if (PointInPolygon(x, y, region.Xs[i], region.Ys[i])) inside = !inside;
            if (inside) return true;
        }
        return false;
    }

    /// <summary>
    /// Share of probes just outside the room edges that land in a wall.
    /// Probes sit at a quarter, half, and three quarters of each edge,
    /// <paramref name="probe"/> mm outward. A drawn room scores 1; a
    /// rectangle in open space scores 0. An edge that runs along a space
    /// divider (segments x0, y0, x1, y1) is bounded too: that is where a
    /// detected room meets its open-plan neighbour.
    /// </summary>
    public static double BoundedFraction(double[] xs, double[] ys, IList<PlanRegion> walls, double probe,
        IList<double[]> dividers = null)
    {
        var ring = CleanRing(xs, ys);
        if (ring.Count < 3 || walls == null || walls.Count == 0 || probe <= 0) return 0;
        double area2 = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            area2 += a[0] * b[1] - b[0] * a[1];
        }
        var sign = area2 >= 0 ? 1.0 : -1.0;
        int hits = 0, total = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var dx = b[0] - a[0];
            var dy = b[1] - a[1];
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;
            // Outward normal of a counter-clockwise ring is (dy, -dx).
            var nx = sign * dy / len;
            var ny = -sign * dx / len;
            foreach (var t in new[] { 0.25, 0.5, 0.75 })
            {
                total++;
                if (InRegions(a[0] + dx * t + nx * probe, a[1] + dy * t + ny * probe, walls)
                    || OnDivider(a[0] + dx * t, a[1] + dy * t, dividers, probe))
                    hits++;
            }
        }
        return total == 0 ? 0 : (double)hits / total;
    }

    static bool OnDivider(double x, double y, IList<double[]> dividers, double within)
    {
        if (dividers == null) return false;
        foreach (var s in dividers)
        {
            var dx = s[2] - s[0];
            var dy = s[3] - s[1];
            var len2 = dx * dx + dy * dy;
            var t = len2 <= 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - s[0]) * dx + (y - s[1]) * dy) / len2));
            var ex = s[0] + dx * t - x;
            var ey = s[1] + dy * t - y;
            if (ex * ex + ey * ey <= within * within) return true;
        }
        return false;
    }

    public static bool PointInPolygon(double px, double py, double[] xs, double[] ys)
    {
        return PointInPolygon(px, py, CleanRing(xs, ys));
    }

    static bool PointInPolygon(double px, double py, List<double[]> ring)
    {
        if (ring == null || ring.Count < 3) return false;
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var ax = ring[i][0];
            var ay = ring[i][1];
            var bx = ring[j][0];
            var by = ring[j][1];
            if (OnSegment(px, py, ax, ay, bx, by)) return true;
            var crosses = (ay > py) != (by > py);
            if (!crosses) continue;
            var xhit = (bx - ax) * (py - ay) / (by - ay) + ax;
            if (px < xhit) inside = !inside;
        }
        return inside;
    }

    static List<double[]> CleanRing(double[] xs, double[] ys)
    {
        var ring = new List<double[]>();
        if (xs == null || ys == null) return ring;
        var n = Math.Min(xs.Length, ys.Length);
        for (var i = 0; i < n; i++)
        {
            if (ring.Count > 0 && Math.Abs(ring[ring.Count - 1][0] - xs[i]) <= 1e-6
                && Math.Abs(ring[ring.Count - 1][1] - ys[i]) <= 1e-6)
                continue;
            ring.Add(new[] { xs[i], ys[i] });
        }
        if (ring.Count > 1
            && Math.Abs(ring[0][0] - ring[ring.Count - 1][0]) <= 1e-6
            && Math.Abs(ring[0][1] - ring[ring.Count - 1][1]) <= 1e-6)
            ring.RemoveAt(ring.Count - 1);
        return ring;
    }

    static bool OnSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var abx = bx - ax;
        var aby = by - ay;
        var apx = px - ax;
        var apy = py - ay;
        var cross = abx * apy - aby * apx;
        if (Math.Abs(cross) > 1e-4) return false;
        var dot = apx * abx + apy * aby;
        if (dot < -1e-4) return false;
        var len2 = abx * abx + aby * aby;
        return dot <= len2 + 1e-4;
    }

    /// <summary>Face lines and jambs on the wall faces and the void edges.</summary>
    public static void AddWallFrame(List<PlanMark> marks, PlanFrame frame)
    {
        if (marks == null || frame == null) return;
        var x = frame.VoidHalf > 1 ? frame.VoidHalf : frame.InnerHalf;
        var y = frame.HalfThick;
        if (x < 1 || y <= 0) return;
        marks.Add(Line(-x, y, x, y, "frame", false));
        marks.Add(Line(-x, -y, x, -y, "frame", false));
        marks.Add(Line(-x, -y, -x, y, "jamb", false));
        marks.Add(Line(x, -y, x, y, "jamb", false));
    }

    public static void AddJambs(List<PlanMark> marks, PlanFrame frame)
    {
        if (marks == null || frame == null) return;
        var x = frame.VoidHalf > 1 ? frame.VoidHalf : frame.InnerHalf;
        var y = frame.HalfThick;
        if (x < 1 || y <= 0) return;
        marks.Add(Line(-x, -y, -x, y, "jamb", false));
        marks.Add(Line(x, -y, x, y, "jamb", false));
    }

    public sealed class PlanMark
    {
        public string Shape;
        public string Part;
        public double X0;
        public double Y0;
        public double X1;
        public double Y1;
        public double Cx;
        public double Cy;
        public double Radius;
        public bool Dashed;
        public string Tier;
    }

    /// <summary>
    /// Plan symbol in the opening frame. X runs along the wall, Y along the
    /// frame plane. detail other than 1:100 still returns this 1:100 set.
    /// </summary>
    public static List<PlanMark> PlanSymbol(Record record, string detail, PlanFrame frame)
    {
        var marks = new List<PlanMark>();
        if (record == null || frame == null || string.IsNullOrEmpty(record.TypeId))
            return marks;
        if (frame.InnerHalf < 2 || frame.HalfThick <= 0)
            return marks;

        var dashed = AboveCut(record, frame.Sill, frame.Head, frame.CutZ);
        var id = record.TypeId;
        if (id == "door.hinged_single")
            AddHingedLeaf(marks, frame, HandSign(record, frame.XLeft), SwingSign(record, frame.YInward), LeafSpan(frame), false);
        else if (id == "door.hinged_double")
        {
            var span = DoubleLeafSpan(frame);
            AddHingedLeaf(marks, frame, -1, SwingSign(record, frame.YInward), span, false);
            AddHingedLeaf(marks, frame, 1, SwingSign(record, frame.YInward), span, false);
        }
        else if (id == "door.sliding")
            AddSliding(marks, frame, HandSign(record, frame.XLeft));
        else if (id == "door.pocket")
            AddPocket(marks, frame, HandSign(record, frame.XLeft));
        else if (id == "window.fixed" || id == "window.side_hung" || id == "window.top_hung")
            AddWindow(marks, frame, record, dashed);
        return marks;
    }

    static double LeafSpan(PlanFrame frame)
    {
        return 2.0 * (frame.InnerHalf - 1.0);
    }

    static double DoubleLeafSpan(PlanFrame frame)
    {
        return (frame.InnerHalf - 1.0) - 3.0;
    }

    static void AddHingedLeaf(List<PlanMark> marks, PlanFrame frame, int hingeSign, int swingSign, double radius, bool dashed)
    {
        if (radius <= 1 || hingeSign == 0 || swingSign == 0) return;
        var hingeX = hingeSign * (frame.InnerHalf - 1.0);
        var openY = swingSign * radius;
        marks.Add(Line(hingeX, 0, hingeX, openY, "leaf", dashed));
        marks.Add(new PlanMark
        {
            Shape = "arc",
            Part = "arc",
            X0 = hingeX - (hingeSign * radius),
            Y0 = 0,
            X1 = hingeX,
            Y1 = openY,
            Cx = hingeX,
            Cy = 0,
            Radius = radius,
            Dashed = dashed,
            Tier = "thin"
        });
    }

    static void AddSliding(List<PlanMark> marks, PlanFrame frame, int parkSign)
    {
        if (parkSign == 0) return;
        var y = TrackSign(frame.YInward) * frame.HalfThick * 0.55;
        var jamb = frame.InnerHalf - 1.0;
        var past = jamb + (jamb * 0.45);
        var x0 = -parkSign * jamb;
        var x1 = parkSign * past;
        marks.Add(Line(x0, y, x1, y, "leaf", false));
        var arrow = Math.Max(40.0, jamb * 0.18);
        var back = x1 - (parkSign * arrow);
        marks.Add(Line(x1, y, back, y + arrow * 0.55, "arrow", false));
        marks.Add(Line(x1, y, back, y - arrow * 0.55, "arrow", false));
    }

    /// <summary>How far the pocket cavity reaches from the opening centre.</summary>
    public static double PocketReach(PlanFrame frame)
    {
        return frame == null ? 0 : frame.InnerHalf + 2.0 + LeafSpan(frame);
    }

    /// <summary>
    /// Hand for a pocket door given clear wall on each side of the centre
    /// (frame -X and +X). Keeps the hand when its side holds the pocket,
    /// otherwise parks the other way. False when neither side has room.
    /// </summary>
    public static bool PocketHand(string hand, int xLeft, double roomNeg, double roomPos, double reach, out string chosen)
    {
        chosen = string.IsNullOrEmpty(hand) ? "L" : hand;
        var sign = HandSign(chosen, xLeft);
        var here = sign < 0 ? roomNeg : roomPos;
        var there = sign < 0 ? roomPos : roomNeg;
        if (here >= reach) return true;
        if (there < reach) return false;
        chosen = string.Equals(chosen, "R", StringComparison.OrdinalIgnoreCase) ? "L" : "R";
        return true;
    }

    /// <summary>
    /// Opening centre along a wall run, clamped so the opening stays
    /// <paramref name="margin"/> clear of each end after <paramref name="reserve"/>
    /// (the cross wall at a corner). Returns NaN when it cannot fit.
    /// </summary>
    public static double ClampAlong(double length, double width, double rawT, double margin, double reserve)
    {
        if (length <= 0) return double.NaN;
        var minT = (reserve + margin + width * 0.5) / length;
        var maxT = 1.0 - minT;
        if (maxT < minT) return double.NaN;
        if (rawT < minT) return minT;
        if (rawT > maxT) return maxT;
        return rawT;
    }

    static void AddPocket(List<PlanMark> marks, PlanFrame frame, int parkSign)
    {
        if (parkSign == 0) return;
        // Leaf parked in the wall, pocket drawn as a cavity inside the faces.
        var span = LeafSpan(frame);
        var mouth = frame.InnerHalf;
        var far = PocketReach(frame);
        var inset = Math.Min(12.0, frame.HalfThick * 0.18);
        if (inset < 1.0) inset = 1.0;
        var y = frame.HalfThick - inset;
        if (y < 1.0) y = frame.HalfThick * 0.5;
        var x0 = parkSign * mouth;
        var x1 = parkSign * far;
        marks.Add(Line(x0, y, x1, y, "pocket", false));
        marks.Add(Line(x0, -y, x1, -y, "pocket", false));
        marks.Add(Line(x1, -y, x1, y, "pocket", false));
        marks.Add(Line(x0, -y, x0, y, "pocket", false));
        marks.Add(Line(parkSign * (mouth + 2.0), 0, parkSign * (far - 2.0), 0, "leaf", true));
    }

    static void AddWindow(List<PlanMark> marks, PlanFrame frame, Record record, bool dashed)
    {
        var half = frame.VoidHalf > 1 ? frame.VoidHalf : frame.InnerHalf;
        var x0 = -half;
        var x1 = half;
        marks.Add(Line(x0, frame.HalfThick, x1, frame.HalfThick, "sill", dashed));
        marks.Add(Line(x0, -frame.HalfThick, x1, -frame.HalfThick, "sill", dashed));
        marks.Add(Line(x0, 0, x1, 0, "glass", dashed));
        if (record.TypeId == "window.fixed") return;
        var y = SwingSign(record, frame.YInward) * frame.HalfThick * 0.45;
        marks.Add(Line(x0, y, x1, y, "glass", dashed));
    }

    static PlanMark Line(double x0, double y0, double x1, double y1, string part, bool dashed)
    {
        return new PlanMark
        {
            Shape = "line",
            Part = part,
            X0 = x0,
            Y0 = y0,
            X1 = x1,
            Y1 = y1,
            Dashed = dashed,
            Tier = "thin"
        };
    }

    public static string RoomTag(double areaMm2)
    {
        var m2 = Math.Round(areaMm2 / 1000000.0, 1, MidpointRounding.AwayFromZero);
        var text = m2.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
        return "ca. " + text + " m²";
    }

    /// <summary>Drawing title for the title block. The scale has its own cell.</summary>
    public static string ViewTitle(string view, int level)
    {
        var key = string.IsNullOrWhiteSpace(view) ? "" : view.Trim().ToLowerInvariant();
        if (key == "plan")
        {
            var etg = (level < 0 ? 0 : level) + 1;
            return "Plan " + etg.ToString(CultureInfo.InvariantCulture) + ". etg";
        }
        if (key == "north") return "Fasade mot nord";
        if (key == "east") return "Fasade mot øst";
        if (key == "south") return "Fasade mot sør";
        if (key == "west") return "Fasade mot vest";
        return "";
    }

    static void AppendGrouped(List<string> lines, IList<ReceiptRow> rows, string mode)
    {
        if (rows == null) return;
        var buckets = new List<List<ReceiptRow>>();
        foreach (var row in rows)
        {
            if (row == null || !Matches(row, mode)) continue;
            List<ReceiptRow> found = null;
            foreach (var bucket in buckets)
            {
                if (string.Equals(bucket[0].Kind, row.Kind, StringComparison.Ordinal)
                    && string.Equals(bucket[0].ShortName, row.ShortName, StringComparison.Ordinal))
                {
                    found = bucket;
                    break;
                }
            }
            if (found == null)
            {
                found = new List<ReceiptRow>();
                buckets.Add(found);
            }
            found.Add(row);
        }

        foreach (var bucket in buckets)
        {
            var hosts = new List<string>();
            foreach (var row in bucket)
                hosts.Add(row.Host);
            var kind = bucket[0].Kind;
            var count = bucket.Count;
            if (mode == "type")
                lines.Add(ChangedLine(count, kind, bucket[0].ShortName, hosts));
            else if (mode == "swing")
                lines.Add(SwingLine(count, kind, hosts));
            else if (mode == "hand")
                lines.Add(HandLine(count, kind, hosts));
            else
                lines.Add(HandAndSwingLine(count, kind, hosts));
        }
    }

    static bool Matches(ReceiptRow row, string mode)
    {
        if (mode == "type") return row.TypeChanged;
        if (row.TypeChanged) return false;
        if (mode == "both") return row.HandChanged && row.SwingChanged;
        if (mode == "swing") return row.SwingChanged && !row.HandChanged;
        if (mode == "hand") return row.HandChanged && !row.SwingChanged;
        return false;
    }

    static string Noun(string kind, int count)
    {
        if (count < 0) count = 0;
        if (string.Equals(kind, "window", StringComparison.Ordinal))
            return count == 1 ? "window" : "windows";
        if (string.Equals(kind, "door", StringComparison.Ordinal))
            return count == 1 ? "door" : "doors";
        return count == 1 ? "opening" : "openings";
    }

    static string Count(int count)
    {
        if (count < 0) count = 0;
        return count.ToString(CultureInfo.InvariantCulture);
    }

    static string HostList(IList<string> hosts)
    {
        var labels = new List<string>();
        if (hosts != null)
        {
            for (var i = 0; i < hosts.Count; i++)
            {
                var label = hosts[i];
                if (string.IsNullOrWhiteSpace(label)) continue;
                label = label.Trim();
                var seen = false;
                for (var j = 0; j < labels.Count; j++)
                {
                    if (string.Equals(labels[j], label, StringComparison.Ordinal))
                    {
                        seen = true;
                        break;
                    }
                }
                if (!seen) labels.Add(label);
            }
        }
        if (labels.Count == 0) return "the wall";
        return string.Join(", ", labels.ToArray());
    }

    static bool TryEditHand(string raw, string current, out string hand)
    {
        hand = current;
        var value = raw.Trim();
        if (value.Equals("flip", StringComparison.OrdinalIgnoreCase))
        {
            hand = string.Equals(current, "R", StringComparison.OrdinalIgnoreCase) ? "L" : "R";
            return true;
        }
        return TryStoredHand(value, out hand);
    }

    static bool TryEditSwing(string raw, string current, out string swing)
    {
        swing = current;
        var value = raw.Trim();
        if (value.Equals("flip", StringComparison.OrdinalIgnoreCase))
        {
            swing = string.Equals(current, "out", StringComparison.OrdinalIgnoreCase) ? "in" : "out";
            return true;
        }
        return TryStoredSwing(value, out swing);
    }

    static bool TryStoredHand(string raw, out string hand)
    {
        hand = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (raw.Trim().Equals("L", StringComparison.OrdinalIgnoreCase))
        {
            hand = "L";
            return true;
        }
        if (raw.Trim().Equals("R", StringComparison.OrdinalIgnoreCase))
        {
            hand = "R";
            return true;
        }
        return false;
    }

    static bool TryStoredSwing(string raw, out string swing)
    {
        swing = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (raw.Trim().Equals("in", StringComparison.OrdinalIgnoreCase))
        {
            swing = "in";
            return true;
        }
        if (raw.Trim().Equals("out", StringComparison.OrdinalIgnoreCase))
        {
            swing = "out";
            return true;
        }
        return false;
    }

    static Record Make(TypeDef def, string hand, string swing)
    {
        return new Record
        {
            TypeId = def.Id,
            Kind = def.Kind,
            Hand = def.HasHand ? hand : null,
            Swing = def.HasSwing ? swing : null,
            Def = def
        };
    }

    static TypeDef DefaultDef(string kind)
    {
        return string.Equals(kind, "window", StringComparison.Ordinal)
            ? ById["window.side_hung"]
            : ById["door.hinged_single"];
    }

    static bool IsKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return false;
        var tag = NormKind(kind);
        return tag == "door" || tag == "window";
    }

    static string NormKind(string kind)
    {
        return string.IsNullOrWhiteSpace(kind) ? "" : kind.Trim().ToLowerInvariant();
    }

    static TypeDef Def(
        string id, string kind, string label, string shortName,
        bool hand, bool swing, string defaultSwing, string noKeyName)
    {
        return new TypeDef
        {
            Id = id,
            Kind = kind,
            Label = label,
            ShortName = shortName,
            HasHand = hand,
            HasSwing = swing,
            DefaultSwing = defaultSwing,
            NoKeyName = noKeyName
        };
    }

    static Dictionary<string, TypeDef> Index(TypeDef[] catalog)
    {
        var map = new Dictionary<string, TypeDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in catalog)
            map[def.Id] = def;
        return map;
    }
}
