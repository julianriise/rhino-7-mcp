using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoMCPPlugin.Functions
{
    /// <summary>
    /// One block definition for every opening of the same size. The key is the
    /// type, width, height, sill, frame thickness and style, each millimetre
    /// rounded to 1. Placement and a hand mirror are the instance transform,
    /// not another definition. No RhinoCommon.
    /// </summary>
    public static class OpeningBlockShare
    {
        public const string TokenPrefix = "forsk opening|";

        /// <summary>What makes two openings the same block. Hand is not here: it mirrors.</summary>
        public sealed class Key : IEquatable<Key>
        {
            public readonly string Kind;
            public readonly int WidthMm;
            public readonly int HeightMm;
            public readonly int SillMm;
            public readonly int FrameMm;
            public readonly string Style;
            public readonly string Swing;
            public readonly int PadMm;

            Key(string kind, int width, int height, int sill, int frame, string style, string swing, int pad)
            {
                Kind = kind;
                WidthMm = width;
                HeightMm = height;
                SillMm = sill;
                FrameMm = frame;
                Style = style ?? "";
                Swing = swing ?? "";
                PadMm = pad;
            }

            /// <summary>Width, sill, head, frame and pad round to 1 mm. Height is the rounded head minus the rounded sill.</summary>
            public static Key From(
                string kind, double width, double sill, double head, double frame, double pad,
                string style, string swing)
            {
                var sillMm = RoundMm(sill);
                var headMm = RoundMm(head);
                var tag = string.Equals(kind, "door", StringComparison.OrdinalIgnoreCase) ? "door" : "window";
                return new Key(
                    tag,
                    Math.Max(0, RoundMm(width)),
                    headMm - sillMm,
                    sillMm,
                    Math.Max(0, RoundMm(frame)),
                    style ?? "",
                    swing ?? "",
                    Math.Max(0, RoundMm(pad)));
            }

            public string Token =>
                TokenPrefix + Kind
                + "|" + WidthMm.ToString(CultureInfo.InvariantCulture)
                + "|" + HeightMm.ToString(CultureInfo.InvariantCulture)
                + "|" + SillMm.ToString(CultureInfo.InvariantCulture)
                + "|" + FrameMm.ToString(CultureInfo.InvariantCulture)
                + "|" + Style
                + "|" + Swing
                + "|" + PadMm.ToString(CultureInfo.InvariantCulture);

            public bool Equals(Key other)
            {
                if (other == null) return false;
                return WidthMm == other.WidthMm
                    && HeightMm == other.HeightMm
                    && SillMm == other.SillMm
                    && FrameMm == other.FrameMm
                    && PadMm == other.PadMm
                    && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
                    && string.Equals(Style, other.Style, StringComparison.Ordinal)
                    && string.Equals(Swing, other.Swing, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => Equals(obj as Key);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = hash * 31 + (Kind ?? "").GetHashCode();
                    hash = hash * 31 + WidthMm;
                    hash = hash * 31 + HeightMm;
                    hash = hash * 31 + SillMm;
                    hash = hash * 31 + FrameMm;
                    hash = hash * 31 + (Style ?? "").GetHashCode();
                    hash = hash * 31 + (Swing ?? "").GetHashCode();
                    hash = hash * 31 + PadMm;
                    return hash;
                }
            }
        }

        public static int RoundMm(double mm) =>
            (int)Math.Round(mm, MidpointRounding.AwayFromZero);

        /// <summary>"Window 1200x1400" or "Door 900x2100".</summary>
        public static string Readable(Key key)
        {
            var noun = key != null && key.Kind == "door" ? "Door" : "Window";
            var width = key == null ? 0 : key.WidthMm;
            var height = key == null ? 0 : key.HeightMm;
            return noun + " " + width.ToString(CultureInfo.InvariantCulture)
                + "x" + height.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>What to append when two different keys want the same readable name.</summary>
        public static string Suffix(Key key)
        {
            if (key == null) return "";
            return "s" + key.SillMm.ToString(CultureInfo.InvariantCulture)
                + " f" + key.FrameMm.ToString(CultureInfo.InvariantCulture)
                + " " + key.Style
                + (key.Swing.Length == 0 ? "" : " " + key.Swing);
        }

        /// <summary>
        /// Canonical opening point to world. X runs along the wall, Y across it,
        /// Z stays. mirrorX sends +X the other way along the wall: that is the hand.
        /// </summary>
        public readonly struct Placement
        {
            public readonly double Xx, Xy, Yx, Yy, Ox, Oy;

            Placement(double xx, double xy, double yx, double yy, double ox, double oy)
            {
                Xx = xx;
                Xy = xy;
                Yx = yx;
                Yy = yy;
                Ox = ox;
                Oy = oy;
            }

            public static Placement On(
                double originX, double originY,
                double widthX, double widthY,
                double thickX, double thickY,
                bool mirrorX)
            {
                if (mirrorX)
                {
                    widthX = -widthX;
                    widthY = -widthY;
                }
                return new Placement(widthX, widthY, thickX, thickY, originX, originY);
            }

            public void Map(double x, double y, double z, out double worldX, out double worldY, out double worldZ)
            {
                worldX = Xx * x + Yx * y + Ox;
                worldY = Xy * x + Yy * y + Oy;
                worldZ = z;
            }
        }

        /// <summary>
        /// The definitions a set of instances would keep. Place and retarget
        /// are copy-on-write: an edit moves one instance, and purge drops a
        /// definition nothing points at.
        /// </summary>
        public sealed class Catalog
        {
            readonly Dictionary<Key, string> _names = new Dictionary<Key, string>();
            readonly Dictionary<string, Key> _byName = new Dictionary<string, Key>(StringComparer.Ordinal);
            readonly Dictionary<string, Key> _instance = new Dictionary<string, Key>(StringComparer.Ordinal);
            readonly Dictionary<Key, int> _refs = new Dictionary<Key, int>();

            public int Definitions => _names.Count;

            public string NameOf(Key key)
            {
                string name;
                return key != null && _names.TryGetValue(key, out name) ? name : null;
            }

            public Key KeyOf(string instance)
            {
                Key key;
                return instance != null && _instance.TryGetValue(instance, out key) ? key : null;
            }

            public string Place(string instance, Key key)
            {
                if (string.IsNullOrEmpty(instance) || key == null) return null;
                if (_instance.ContainsKey(instance)) return Retarget(instance, key);
                return Add(instance, key);
            }

            public string Retarget(string instance, Key key)
            {
                if (string.IsNullOrEmpty(instance) || key == null) return null;
                Key old;
                if (_instance.TryGetValue(instance, out old))
                {
                    if (old.Equals(key)) return _names[old];
                    _refs[old] = _refs[old] - 1;
                    _instance.Remove(instance);
                }
                return Add(instance, key);
            }

            public void Purge()
            {
                var dead = new List<Key>();
                foreach (var pair in _refs)
                    if (pair.Value <= 0) dead.Add(pair.Key);
                foreach (var key in dead)
                {
                    _refs.Remove(key);
                    string name;
                    if (!_names.TryGetValue(key, out name)) continue;
                    _names.Remove(key);
                    _byName.Remove(name);
                }
            }

            string Add(string instance, Key key)
            {
                string name;
                if (!_names.TryGetValue(key, out name))
                {
                    name = Readable(key);
                    if (_byName.ContainsKey(name))
                    {
                        var suffixed = name + " · " + Suffix(key);
                        name = suffixed;
                        var n = 2;
                        while (_byName.ContainsKey(name))
                        {
                            name = suffixed + " " + n.ToString(CultureInfo.InvariantCulture);
                            n++;
                        }
                    }
                    _names[key] = name;
                    _byName[name] = key;
                    _refs[key] = 0;
                }
                _refs[key] = _refs[key] + 1;
                _instance[instance] = key;
                return name;
            }
        }
    }
}
