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
        public double Sill;
        public double Head;
        public double CutZ;
        public int YInward;
        public int XLeft;
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

    static void AddPocket(List<PlanMark> marks, PlanFrame frame, int parkSign)
    {
        if (parkSign == 0) return;
        var span = LeafSpan(frame);
        var x0 = parkSign * (frame.InnerHalf + 2.0);
        var x1 = parkSign * (frame.InnerHalf + 2.0 + span);
        marks.Add(Line(x0, 0, x1, 0, "leaf", true));
    }

    static void AddWindow(List<PlanMark> marks, PlanFrame frame, Record record, bool dashed)
    {
        var x0 = -frame.InnerHalf;
        var x1 = frame.InnerHalf;
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

    public static string ViewTitle(string view, int level, int scale, bool locked)
    {
        var key = string.IsNullOrWhiteSpace(view) ? "" : view.Trim().ToLowerInvariant();
        if (key == "plan")
        {
            var etg = (level < 0 ? 0 : level) + 1;
            var sc = locked && scale > 0
                ? "1:" + scale.ToString(CultureInfo.InvariantCulture)
                : "fit";
            return "Plan " + etg.ToString(CultureInfo.InvariantCulture) + ". etg " + sc;
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
