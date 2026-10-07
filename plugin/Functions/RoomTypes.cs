using System;
using System.Collections.Generic;
using System.Text;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The type of a room, stored as <c>forsk:room_type</c>, and where it came from
/// (<c>forsk:room_type_source</c>: label, guessed, or user). A missing or unknown
/// value reads as unassigned. <see cref="FromLabel"/> maps a name.
/// <see cref="Guess"/> is a separate first pass for a room with no label.
/// <see cref="Choose"/> never replaces a user or label type with a guess.
/// Pure: no Rhino document.
/// </summary>
public static class RoomTypes
{
    public const string Unassigned = "unassigned";
    public const string Living = "living";
    public const string Kitchen = "kitchen";
    public const string Dining = "dining";
    public const string Bedroom = "bedroom";
    public const string Bathroom = "bathroom";
    public const string Wc = "wc";
    public const string Hall = "hall";
    public const string Storage = "storage";
    public const string Laundry = "laundry";
    public const string Technical = "technical";
    public const string Office = "office";
    public const string Garage = "garage";
    public const string Balcony = "balcony";
    public const string Stair = "stair";

    public const string User = "user";
    public const string Label = "label";
    public const string Guessed = "guessed";

    public const string Key = "forsk:room_type";
    public const string SourceKey = "forsk:room_type_source";

    /// <summary>A door or window this close to the room outline counts for that room.</summary>
    public const double TouchMm = 400;
    /// <summary>No window, one door, and smaller than this is a storage guess.</summary>
    public const double StorageMaxM2 = 4;
    public const double BathMinM2 = 2.5;
    public const double BathMaxM2 = 8;
    /// <summary>Long side over short side. With several doors, a hall guess.</summary>
    public const double HallAspect = 2.5;
    public const int HallDoors = 2;

    public static readonly IReadOnlyList<string> All = new[]
    {
        Unassigned, Living, Kitchen, Dining, Bedroom, Bathroom, Wc, Hall,
        Storage, Laundry, Technical, Office, Garage, Balcony, Stair
    };

    public readonly struct Rgb
    {
        public readonly int R;
        public readonly int G;
        public readonly int B;

        public Rgb(int r, int g, int b)
        {
            R = r;
            G = g;
            B = b;
        }
    }

    /// <summary>Doors, windows, net area and the bounding-box aspect (long / short, at least 1).</summary>
    public readonly struct GuessInput
    {
        public readonly int Doors;
        public readonly int Windows;
        public readonly double AreaM2;
        public readonly double Aspect;

        public GuessInput(int doors, int windows, double areaM2, double aspect)
        {
            Doors = doors;
            Windows = windows;
            AreaM2 = areaM2;
            Aspect = aspect;
        }
    }

    /// <summary>The type to store, and its source. Source is empty when the type is unassigned and nobody set it.</summary>
    public readonly struct Decision
    {
        public readonly string Type;
        public readonly string Source;

        public Decision(string type, string source)
        {
            Type = type ?? Unassigned;
            Source = source ?? "";
        }
    }

    static readonly Dictionary<string, string> Words = BuildWords();
    static readonly Dictionary<string, Rgb> Colours = BuildColours();
    static readonly HashSet<string> Known = new HashSet<string>(All, StringComparer.Ordinal);

    /// <summary>A stored key, or unassigned when the file has none or the text is not a key.</summary>
    public static string Read(string stored)
    {
        var key = (stored ?? "").Trim().ToLowerInvariant();
        return Known.Contains(key) ? key : Unassigned;
    }

    /// <summary>label, guessed, user, or empty when the file has no source.</summary>
    public static string ReadSource(string stored)
    {
        var source = (stored ?? "").Trim().ToLowerInvariant();
        if (source == User || source == Label || source == Guessed) return source;
        return "";
    }

    /// <summary>True when <paramref name="text"/> is one of the keys, including unassigned. English words fold.</summary>
    public static bool TryParse(string text, out string key)
    {
        key = Unassigned;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var folded = text.Trim().ToLowerInvariant();
        if (!Known.Contains(folded)) return false;
        key = folded;
        return true;
    }

    /// <summary>
    /// The type of a room name or label. Whole words only, case-insensitive,
    /// Norwegian and English. Room and Rom, and a name with no keyword, are unassigned.
    /// The first matching word wins.
    /// </summary>
    public static string FromLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Unassigned;
        var tokens = Tokens(text);
        foreach (var token in tokens)
        {
            if (token == "room" || token == "rom") continue;
            string type;
            if (Words.TryGetValue(token, out type)) return type;
        }
        return Unassigned;
    }

    /// <summary>
    /// The room a user asked for by a type word, English or Norwegian
    /// ("the toilet", "soverom", "bath"): the room's stored type, else the
    /// type its name says. A WC named Bathroom is the toilet. False when the
    /// words name no type.
    /// </summary>
    public static bool Matches(string asked, string roomType, string roomName)
    {
        var key = TryParse(asked, out var parsed) && parsed != Unassigned ? parsed : FromLabel(asked);
        if (key == Unassigned) return false;
        var stored = Read(roomType);
        return stored != Unassigned ? stored == key : FromLabel(roomName) == key;
    }

    /// <summary>
    /// A first guess from the room's openings and shape. No door stays unassigned.
    /// No window, one door, under <see cref="StorageMaxM2"/> is storage, even inside the bathroom band.
    /// Several doors and a long narrow plan is a hall.
    /// No window and <see cref="BathMinM2"/>–<see cref="BathMaxM2"/> is a bathroom, including a small room with more than one door.
    /// Anything else stays unassigned, for a later guesser to replace.
    /// </summary>
    public static string Guess(GuessInput facts)
    {
        if (facts.Doors <= 0) return Unassigned;
        if (facts.Windows <= 0 && facts.Doors == 1 && facts.AreaM2 > 0 && facts.AreaM2 < StorageMaxM2)
            return Storage;
        if (facts.Doors >= HallDoors && facts.Aspect >= HallAspect)
            return Hall;
        if (facts.Windows <= 0 && facts.AreaM2 >= BathMinM2 && facts.AreaM2 <= BathMaxM2)
            return Bathroom;
        return Unassigned;
    }

    /// <summary>
    /// Label beats a guess. A stored user type is kept. A stored label type is kept
    /// when the name no longer maps, so a guess cannot replace it. A new label
    /// may replace a guess, or an earlier label.
    /// </summary>
    public static Decision Choose(string storedType, string storedSource, string labelText, GuessInput facts)
    {
        var source = ReadSource(storedSource);
        if (source == User)
            return new Decision(Read(storedType), User);
        var fromLabel = FromLabel(labelText);
        if (fromLabel != Unassigned)
            return new Decision(fromLabel, Label);
        if (source == Label)
            return new Decision(Read(storedType), Label);
        var guessed = Guess(facts);
        if (guessed != Unassigned)
            return new Decision(guessed, Guessed);
        return new Decision(Unassigned, "");
    }

    /// <summary>Doors and windows whose boxes meet the outline, the net area, and the plan aspect.</summary>
    public static GuessInput Measure(IList<RoomDetect.Pt> ring, double areaMm2,
        IList<RoomDetect.Box> doors, IList<RoomDetect.Box> windows)
    {
        return new GuessInput(
            CountTouching(ring, doors, TouchMm),
            CountTouching(ring, windows, TouchMm),
            areaMm2 / 1000000.0,
            Aspect(ring));
    }

    /// <summary>Long side over short side of the outline's box. A square is 1.</summary>
    public static double Aspect(IList<RoomDetect.Pt> ring)
    {
        if (ring == null || ring.Count == 0) return 1;
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var p in ring)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        var w = maxX - minX;
        var h = maxY - minY;
        var shortSide = Math.Min(w, h);
        if (shortSide < 1) return 1;
        return Math.Max(w, h) / shortSide;
    }

    /// <summary>How many boxes meet the outline, expanded by <paramref name="margin"/>.</summary>
    public static int CountTouching(IList<RoomDetect.Pt> ring, IList<RoomDetect.Box> boxes, double margin)
    {
        if (ring == null || boxes == null) return 0;
        var count = 0;
        foreach (var box in boxes)
            if (Overlaps(ring, box, margin)) count++;
        return count;
    }

    /// <summary>Appended to a room line when a type is stored. Empty when the file has none.</summary>
    public static string LineSuffix(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        return " · " + English(stored);
    }

    /// <summary>The English name on a schedule. Unassigned stays Unassigned.</summary>
    public static string English(string key)
    {
        switch (Read(key))
        {
            case Living: return "Living";
            case Kitchen: return "Kitchen";
            case Dining: return "Dining";
            case Bedroom: return "Bedroom";
            case Bathroom: return "Bathroom";
            case Wc: return "WC";
            case Hall: return "Hall";
            case Storage: return "Storage";
            case Laundry: return "Laundry";
            case Technical: return "Technical";
            case Office: return "Office";
            case Garage: return "Garage";
            case Balcony: return "Balcony";
            case Stair: return "Stair";
            default: return "Unassigned";
        }
    }

    /// <summary>Flat pastel for a perspective floor. Unassigned is light grey.</summary>
    public static Rgb Colour(string key)
    {
        Rgb colour;
        if (Colours.TryGetValue(Read(key), out colour)) return colour;
        return Colours[Unassigned];
    }

    /// <summary>
    /// Perspective only. A parallel plan (looking down) or elevation (looking level)
    /// stays a line drawing. Off means no colour in any view.
    /// </summary>
    public static bool ShowInView(bool enabled, bool parallel, double dx, double dy, double dz)
    {
        if (!enabled) return false;
        return ForskTechnical.Classify(parallel, dx, dy, dz) == ForskTechnical.Look.Model;
    }

    static bool Overlaps(IList<RoomDetect.Pt> ring, RoomDetect.Box box, double margin)
    {
        if (ring == null || ring.Count < 3) return false;
        var minX = box.MinX - margin;
        var minY = box.MinY - margin;
        var maxX = box.MaxX + margin;
        var maxY = box.MaxY + margin;
        foreach (var p in ring)
            if (p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY) return true;
        var corners = new[]
        {
            new RoomDetect.Pt(minX, minY), new RoomDetect.Pt(maxX, minY),
            new RoomDetect.Pt(maxX, maxY), new RoomDetect.Pt(minX, maxY)
        };
        foreach (var corner in corners)
            if (RoomDetect.Contains(ring, corner)) return true;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (SegmentsCross(a, b, minX, minY, maxX, minY)
                || SegmentsCross(a, b, maxX, minY, maxX, maxY)
                || SegmentsCross(a, b, maxX, maxY, minX, maxY)
                || SegmentsCross(a, b, minX, maxY, minX, minY))
                return true;
        }
        return false;
    }

    static bool SegmentsCross(RoomDetect.Pt a, RoomDetect.Pt b, double x1, double y1, double x2, double y2)
    {
        var c = new RoomDetect.Pt(x1, y1);
        var d = new RoomDetect.Pt(x2, y2);
        var d1 = Side(c, d, a);
        var d2 = Side(c, d, b);
        var d3 = Side(a, b, c);
        var d4 = Side(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    static double Side(RoomDetect.Pt a, RoomDetect.Pt b, RoomDetect.Pt p)
    {
        return (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
    }

    static List<string> Tokens(string text)
    {
        var folded = Fold(text);
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= folded.Length; i++)
        {
            var letter = i < folded.Length && folded[i] != ' ';
            if (letter && start < 0) start = i;
            if (!letter && start >= 0)
            {
                tokens.Add(folded.Substring(start, i - start));
                start = -1;
            }
        }
        return tokens;
    }

    /// <summary>Lower case, Norwegian letters folded, everything else a word break.</summary>
    static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.Trim().ToLowerInvariant())
        {
            switch (raw)
            {
                case 'ø':
                case 'ö':
                    sb.Append('o');
                    break;
                case 'æ':
                    sb.Append("ae");
                    break;
                case 'å':
                case 'á':
                case 'à':
                    sb.Append('a');
                    break;
                case 'é':
                case 'è':
                case 'ê':
                    sb.Append('e');
                    break;
                default:
                    sb.Append(char.IsLetter(raw) ? raw : ' ');
                    break;
            }
        }
        return sb.ToString();
    }

    static Dictionary<string, string> BuildWords()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string word, string type) => map[word] = type;
        Add("soverom", Bedroom);
        Add("vaskerom", Laundry);
        Add("baderom", Bathroom);
        Add("spisestue", Dining);
        Add("kjokken", Kitchen);
        Add("garasje", Garage);
        Add("balkong", Balcony);
        Add("terrasse", Balcony);
        Add("terrace", Balcony);
        Add("toalett", Wc);
        Add("toilet", Wc);
        Add("teknisk", Technical);
        Add("technical", Technical);
        Add("korridor", Hall);
        Add("corridor", Hall);
        Add("hallway", Hall);
        Add("entrance", Hall);
        Add("bathroom", Bathroom);
        Add("bedroom", Bedroom);
        Add("balcony", Balcony);
        Add("storage", Storage);
        Add("laundry", Laundry);
        Add("kitchen", Kitchen);
        Add("dining", Dining);
        Add("living", Living);
        Add("office", Office);
        Add("garage", Garage);
        Add("stairs", Stair);
        Add("stair", Stair);
        Add("kontor", Office);
        Add("entre", Hall);
        Add("stue", Living);
        Add("gang", Hall);
        Add("hall", Hall);
        Add("bath", Bathroom);
        Add("boder", Storage);
        Add("trapp", Stair);
        Add("bod", Storage);
        Add("sov", Bedroom);
        Add("bad", Bathroom);
        Add("wc", Wc);
        return map;
    }

    static Dictionary<string, Rgb> BuildColours()
    {
        return new Dictionary<string, Rgb>(StringComparer.Ordinal)
        {
            [Unassigned] = new Rgb(236, 236, 236),
            [Living] = new Rgb(255, 214, 170),
            [Kitchen] = new Rgb(255, 236, 153),
            [Dining] = new Rgb(255, 204, 170),
            [Bedroom] = new Rgb(186, 210, 245),
            [Bathroom] = new Rgb(170, 224, 224),
            [Wc] = new Rgb(204, 228, 242),
            [Hall] = new Rgb(224, 216, 200),
            [Storage] = new Rgb(210, 198, 184),
            [Laundry] = new Rgb(190, 224, 204),
            [Technical] = new Rgb(196, 200, 214),
            [Office] = new Rgb(214, 196, 230),
            [Garage] = new Rgb(196, 204, 214),
            [Balcony] = new Rgb(196, 224, 186),
            [Stair] = new Rgb(224, 206, 186)
        };
    }
}
