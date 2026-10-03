using System;
using System.Collections.Generic;
using System.Globalization;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Area statistics for the Analyser. Each room brings the net area the plan
/// tag and the Romliste already store (mm²). This sums those figures per
/// floor, per use and for the model. It does not measure an outline again.
/// The use is a small name map; a caller may set one. Pure, no Rhino document.
/// </summary>
public static class AreaStats
{
    /// <summary>How many rooms the summary names before "+N more".</summary>
    public const int SummaryRooms = 8;

    public const string OtherUse = "Annet";

    /// <summary>Longest key first is not required: a word takes the longest key it matches.</summary>
    static readonly (string Key, string Use)[] NameUses =
    {
        ("bathroom", "Bad"),
        ("bedroom", "Soverom"),
        ("soverom", "Soverom"),
        ("kitchen", "Kjøkken"),
        ("kjokken", "Kjøkken"),
        ("living", "Stue"),
        ("storage", "Bod"),
        ("toalett", "Bad"),
        ("entre", "Gang"),
        ("stue", "Stue"),
        ("hall", "Gang"),
        ("gang", "Gang"),
        ("bath", "Bad"),
        ("bod", "Bod"),
        ("bad", "Bad"),
        ("wc", "Bad")
    };

    public sealed class Room
    {
        public string Id;
        public string Name;
        public string Level;
        /// <summary>Net area in mm², the same figure as the plan tag.</summary>
        public double AreaMm2;
        /// <summary>When set, the group. Otherwise the name map.</summary>
        public string Use;
    }

    public sealed class RoomLine
    {
        public string Id;
        public string Name;
        public string Level;
        public string Use;
        public double AreaMm2;
    }

    /// <summary>One floor (forsk:level) or one use. AreaMm2 is the sum of the rooms in it.</summary>
    public sealed class Group
    {
        public string Key;
        public int Count;
        public double AreaMm2;
    }

    public sealed class Result
    {
        /// <summary>Largest net area first.</summary>
        public List<RoomLine> Rooms = new List<RoomLine>();
        /// <summary>By floor, level order. An empty level is "0".</summary>
        public List<Group> Floors = new List<Group>();
        /// <summary>By use, largest area first.</summary>
        public List<Group> Uses = new List<Group>();
        public double NetMm2;
        public string Summary;
    }

    /// <summary>
    /// The use of a room name. Norwegian and English both map. "Rom" and
    /// "Room", and any name the map does not know, are Annet.
    /// </summary>
    public static string UseOf(string name)
    {
        string found = null;
        foreach (var word in Words(name))
        {
            var use = MatchWord(word);
            if (use == null) continue;
            found = use;
            break;
        }
        return found ?? OtherUse;
    }

    public static Result Compute(IList<Room> rooms)
    {
        var result = new Result();
        if (rooms != null)
        {
            foreach (var room in rooms)
            {
                if (room == null) continue;
                var line = new RoomLine
                {
                    Id = room.Id ?? "",
                    Name = string.IsNullOrWhiteSpace(room.Name) ? "Rom" : room.Name.Trim(),
                    Level = string.IsNullOrWhiteSpace(room.Level) ? "0" : room.Level.Trim(),
                    Use = string.IsNullOrWhiteSpace(room.Use) ? UseOf(room.Name) : room.Use.Trim(),
                    AreaMm2 = room.AreaMm2
                };
                result.Rooms.Add(line);
                result.NetMm2 += line.AreaMm2;
            }
        }
        if (result.Rooms.Count == 0)
        {
            result.Summary = "No rooms. Make rooms finds them from the walls.";
            return result;
        }
        result.Rooms.Sort(BySize);
        result.Floors = Groups(result.Rooms, line => line.Level, ByLevel);
        result.Uses = Groups(result.Rooms, line => line.Use, ByArea);
        result.Summary = Summarize(result);
        return result;
    }

    static List<Group> Groups(List<RoomLine> rooms, Func<RoomLine, string> key, Comparison<Group> order)
    {
        var groups = new List<Group>();
        foreach (var room in rooms)
        {
            var name = key(room);
            var group = groups.Find(g => g.Key == name);
            if (group == null)
            {
                group = new Group { Key = name };
                groups.Add(group);
            }
            group.Count++;
            group.AreaMm2 += room.AreaMm2;
        }
        groups.Sort(order);
        return groups;
    }

    static string Summarize(Result result)
    {
        var floors = new string[result.Floors.Count];
        for (var i = 0; i < floors.Length; i++)
        {
            var floor = result.Floors[i];
            floors[i] = "Floor " + floor.Key + ": " + OpeningTypes.AreaText(floor.AreaMm2);
        }
        var uses = new string[result.Uses.Count];
        for (var i = 0; i < uses.Length; i++)
            uses[i] = result.Uses[i].Key + " " + OpeningTypes.AreaText(result.Uses[i].AreaMm2);
        var shown = Math.Min(SummaryRooms, result.Rooms.Count);
        var names = new string[shown];
        for (var i = 0; i < shown; i++)
            names[i] = result.Rooms[i].Name + " " + OpeningTypes.AreaText(result.Rooms[i].AreaMm2);
        var text = "Net " + OpeningTypes.AreaText(result.NetMm2) + ", estimate. "
            + string.Join("; ", floors) + ". By use: " + string.Join(", ", uses) + ". "
            + string.Join(", ", names);
        var more = result.Rooms.Count - shown;
        if (more > 0) text += ", +" + more.ToString(CultureInfo.InvariantCulture) + " more";
        return text + ".";
    }

    static int BySize(RoomLine a, RoomLine b)
    {
        var byArea = b.AreaMm2.CompareTo(a.AreaMm2);
        if (byArea != 0) return byArea;
        var byName = string.CompareOrdinal(a.Name, b.Name);
        return byName != 0 ? byName : string.CompareOrdinal(a.Id, b.Id);
    }

    static int ByArea(Group a, Group b)
    {
        var byArea = b.AreaMm2.CompareTo(a.AreaMm2);
        return byArea != 0 ? byArea : string.CompareOrdinal(a.Key, b.Key);
    }

    static int ByLevel(Group a, Group b)
    {
        var aNum = int.TryParse(a.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var an);
        var bNum = int.TryParse(b.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bn);
        if (aNum && bNum) return an.CompareTo(bn);
        return string.CompareOrdinal(a.Key, b.Key);
    }

    /// <summary>A word matches a key when it is that key, or a longer word that starts with a key of three letters or more (baderom, hallway), or the key plus digits (soverom2).</summary>
    static string MatchWord(string word)
    {
        string use = null;
        var best = 0;
        foreach (var pair in NameUses)
        {
            if (pair.Key.Length < best || word.Length < pair.Key.Length) continue;
            if (!word.StartsWith(pair.Key, StringComparison.Ordinal)) continue;
            var rest = word.Substring(pair.Key.Length);
            if (rest.Length > 0 && pair.Key.Length < 3 && !AllDigits(rest)) continue;
            best = pair.Key.Length;
            use = pair.Use;
        }
        return use;
    }

    static List<string> Words(string name)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) return words;
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var raw in name.ToLowerInvariant())
        {
            var c = Fold(raw);
            if (c == ' ')
            {
                if (sb.Length > 0) words.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    static char Fold(char c)
    {
        if (c == 'ø' || c == 'ö') return 'o';
        if (c == 'å' || c == 'ä' || c == 'æ') return 'a';
        if (c == 'é' || c == 'è' || c == 'ê' || c == 'ë') return 'e';
        return char.IsLetterOrDigit(c) ? c : ' ';
    }

    static bool AllDigits(string text)
    {
        if (text.Length == 0) return false;
        foreach (var c in text)
            if (c < '0' || c > '9') return false;
        return true;
    }
}
