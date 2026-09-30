using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Text as a DXF file holds it, decoded once, for Forsk's DXF import. Rhino's
/// import parses a DXF string as RTF and keeps only the result: \U is read as a
/// control word and its + eaten, so B\U+00F8ttekott comes in as B00F8ttekott
/// and the escape is gone. dxf_import reads the same file here and rewrites
/// each imported text with the decoded string. Pure, tested headless.
/// </summary>
public static class DxfText
{
    /// <summary>A TEXT or MTEXT entity of the ENTITIES section.</summary>
    public sealed class Entity
    {
        public string Handle = "";
        public string Kind = "";
        public string Layer = "";
        public double X, Y;
        /// <summary>A justified TEXT's alignment point (11/21), where Rhino may anchor it.</summary>
        public double AlignX, AlignY;
        public bool Aligned;
        public string Raw = "";
        public string Text = "";
    }

    /// <summary>A text object as Rhino's import made it.</summary>
    public readonly struct Placed
    {
        public readonly string Layer;
        public readonly double X, Y;
        public readonly string Text;

        public Placed(string layer, double x, double y, string text)
        {
            Layer = layer;
            X = x;
            Y = y;
            Text = text;
        }
    }

    /// <summary>What dxf_import does to one placed text: its source entity (or none) and its text.</summary>
    public readonly struct Rewritten
    {
        public readonly int Source;
        public readonly string Text;

        public Rewritten(int source, string text)
        {
            Source = source;
            Text = text;
        }
    }

    public static List<Entity> Read(string path) => Read(File.ReadAllBytes(path));

    /// <summary>
    /// TEXT and MTEXT of an ASCII DXF, decoded. From AC1021 (2007) a DXF is
    /// UTF-8; before that its strings are in $DWGCODEPAGE (ANSI_1252, …).
    /// </summary>
    public static List<Entity> Read(byte[] bytes)
    {
        var header = Pairs(Encoding.ASCII.GetString(bytes));
        var version = HeaderValue(header, "$ACADVER");
        var codepage = CodePage(HeaderValue(header, "$DWGCODEPAGE"));
        var utf8 = string.CompareOrdinal(version, "AC1021") >= 0;
        Encoding encoding;
        try
        {
            encoding = utf8 ? new UTF8Encoding(false) : Encoding.GetEncoding(codepage);
        }
        catch (Exception)
        {
            throw new NotSupportedException("The DXF's code page " + codepage + " ($DWGCODEPAGE) is not available here.");
        }
        var pairs = Pairs(encoding.GetString(bytes));

        var list = new List<Entity>();
        Entity current = null;
        var mtext = new StringBuilder();
        string section = null;
        for (var i = 0; i < pairs.Count; i++)
        {
            var code = pairs[i].Key;
            var value = pairs[i].Value;
            if (code == "0")
            {
                Finish(current, mtext, codepage, list);
                current = null;
                if (value == "SECTION" && i + 1 < pairs.Count && pairs[i + 1].Key == "2")
                    section = pairs[i + 1].Value.Trim();
                else if (section == "ENTITIES" && (value == "TEXT" || value == "MTEXT"))
                    current = new Entity { Kind = value };
                continue;
            }
            if (current == null) continue;
            switch (code)
            {
                case "5": current.Handle = value.Trim(); break;
                case "8": current.Layer = value.Trim(); break;
                case "10": current.X = Number(value); break;
                case "20": current.Y = Number(value); break;
                case "11": if (current.Kind == "TEXT") { current.AlignX = Number(value); current.Aligned = true; } break;
                case "21": if (current.Kind == "TEXT") current.AlignY = Number(value); break;
                case "3": mtext.Append(value); break;
                case "1": mtext.Append(value); break;
            }
        }
        Finish(current, mtext, codepage, list);
        return list;
    }

    static void Finish(Entity entity, StringBuilder raw, int codepage, List<Entity> list)
    {
        if (entity != null)
        {
            // MTEXT carries its string in 250-char 3 chunks, then the rest in 1.
            entity.Raw = raw.ToString();
            entity.Text = entity.Kind == "MTEXT" ? DecodeMText(entity.Raw, codepage) : DecodeText(entity.Raw, codepage);
            list.Add(entity);
        }
        raw.Clear();
    }

    /// <summary>A TEXT string: \U+XXXX and \M+nXXXX escapes, %% codes. Other backslashes are text.</summary>
    public static string DecodeText(string raw, int codepage) => Decode(raw, codepage, false);

    /// <summary>An MTEXT string: the TEXT escapes, then MTEXT formatting dropped (\P and \~ are spaces).</summary>
    public static string DecodeMText(string raw, int codepage) => Decode(raw, codepage, true);

    // MTEXT codes whose argument runs to ';': font, height, width, oblique,
    // tracking, alignment, colour, paragraph, stacked fraction.
    static readonly Regex MtextArgument = new Regex(@"\G\\([fFHWQTACcpS])([^;]*);");
    static readonly Regex Spaces = new Regex(@"\s+");

    static string Decode(string raw, int codepage, bool mtext)
    {
        var s = raw ?? "";
        var text = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '%' && i + 2 < s.Length && s[i + 1] == '%')
            {
                i = Percent(s, i, text);
                continue;
            }
            if (mtext && (c == '{' || c == '}'))
            {
                i++;
                continue;
            }
            if (c != '\\' || i + 1 >= s.Length)
            {
                text.Append(c);
                i++;
                continue;
            }
            var n = s[i + 1];
            if ((n == 'U' || n == 'u') && i + 6 < s.Length && s[i + 2] == '+' && Hex(s, i + 3, 4, out var code))
            {
                text.Append((char)code);
                i += 7;
            }
            else if ((n == 'M' || n == 'm') && i + 7 < s.Length && s[i + 2] == '+' && s[i + 3] >= '1' && s[i + 3] <= '5'
                && Hex(s, i + 4, 4, out var pair) && TryMbcs(s[i + 3] - '1', pair, out var glyph))
            {
                text.Append(glyph);
                i += 8;
            }
            else if (!mtext)
            {
                text.Append(c);
                i++;
            }
            else if (n == '\\' || n == '{' || n == '}')
            {
                text.Append(n);
                i += 2;
            }
            else if (n == 'P' || n == 'X' || n == '~')
            {
                text.Append(' ');
                i += 2;
            }
            else if ("LlOoKk".IndexOf(n) >= 0)
            {
                i += 2;
            }
            else if (MtextArgument.Match(s, i) is Match arg && arg.Success)
            {
                // A stacked fraction keeps its numbers: \S1^2; reads 1/2.
                if (arg.Groups[1].Value == "S") text.Append(Regex.Replace(arg.Groups[2].Value, @"[\^#]", "/"));
                i += arg.Length;
            }
            else
            {
                text.Append(c);
                i++;
            }
        }
        return Spaces.Replace(text.ToString(), " ").Trim();
    }

    // \M+n: 1 Japanese, 2 Traditional Chinese, 3 Korean (Wansung), 4 Korean (Johab), 5 Simplified Chinese.
    static readonly int[] MbcsCodePages = { 932, 950, 949, 1361, 936 };

    /// <summary>A \M+n double-byte character. No such code page here: false, and the escape stays as written.</summary>
    static bool TryMbcs(int n, int pair, out string glyph)
    {
        try
        {
            glyph = Encoding.GetEncoding(MbcsCodePages[n]).GetString(new[] { (byte)(pair >> 8), (byte)(pair & 0xFF) });
            return true;
        }
        catch (Exception)
        {
            glyph = null;
            return false;
        }
    }

    /// <summary>%%d degree, %%c diameter, %%p plus/minus, %%% percent, %%nnn a code; %%u and %%o toggles drop.</summary>
    static int Percent(string s, int i, StringBuilder text)
    {
        var k = char.ToLowerInvariant(s[i + 2]);
        switch (k)
        {
            case 'd': text.Append('°'); return i + 3;
            case 'c': text.Append('⌀'); return i + 3;
            case 'p': text.Append('±'); return i + 3;
            case '%': text.Append('%'); return i + 3;
            case 'u':
            case 'o': return i + 3;
        }
        if (i + 4 < s.Length && char.IsDigit(s[i + 2]) && char.IsDigit(s[i + 3]) && char.IsDigit(s[i + 4]))
        {
            text.Append((char)int.Parse(s.Substring(i + 2, 3), CultureInfo.InvariantCulture));
            return i + 5;
        }
        text.Append('%');
        return i + 1;
    }

    static bool Hex(string s, int start, int length, out int value)
    {
        value = 0;
        return start + length <= s.Length
            && int.TryParse(s.Substring(start, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    internal static double Number(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>ANSI_1252 → 1252. No or an unknown header value: 1252, the DXF default.</summary>
    static int CodePage(string value)
    {
        var digits = Regex.Match(value ?? "", @"\d+");
        return digits.Success ? int.Parse(digits.Value, CultureInfo.InvariantCulture) : 1252;
    }

    internal static string HeaderValue(List<KeyValuePair<string, string>> pairs, string name)
    {
        for (var i = 0; i + 1 < pairs.Count; i++)
        {
            if (pairs[i].Key == "0" && pairs[i].Value == "ENDSEC") return "";
            if (pairs[i].Key == "9" && pairs[i].Value.Trim() == name) return pairs[i + 1].Value.Trim();
        }
        return "";
    }

    /// <summary>Group code / value pairs. Codes are trimmed; values keep their spaces, not the line end.</summary>
    internal static List<KeyValuePair<string, string>> Pairs(string text)
    {
        var lines = text.Split('\n');
        var pairs = new List<KeyValuePair<string, string>>(lines.Length / 2);
        for (var i = 0; i + 1 < lines.Length; i += 2)
            pairs.Add(new KeyValuePair<string, string>(lines[i].Trim(), lines[i + 1].TrimEnd('\r')));
        return pairs;
    }

    /// <summary>
    /// The decoded text for each placed text: its DXF entity is the one on the
    /// same layer whose insertion (or TEXT alignment) point is within tol,
    /// nearest first, each entity used once. Rhino's import keeps no DXF
    /// handle, so position and layer are the key. No entity within tol, or two
    /// equally near with different text: the text stays as Rhino made it.
    /// </summary>
    public static Rewritten[] Rewrite(IList<Entity> source, IList<Placed> placed, double tol)
    {
        var used = new bool[source.Count];
        var result = new Rewritten[placed.Count];
        for (var p = 0; p < placed.Count; p++)
        {
            int best = -1, second = -1;
            double bestD = double.MaxValue, secondD = double.MaxValue;
            for (var s = 0; s < source.Count; s++)
            {
                var e = source[s];
                if (used[s] || !string.Equals(e.Layer, placed[p].Layer, StringComparison.OrdinalIgnoreCase)) continue;
                var d = Dist(e.X, e.Y, placed[p]);
                if (e.Aligned) d = Math.Min(d, Dist(e.AlignX, e.AlignY, placed[p]));
                if (d > tol) continue;
                if (d < bestD)
                {
                    second = best;
                    secondD = bestD;
                    best = s;
                    bestD = d;
                }
                else if (d < secondD)
                {
                    second = s;
                    secondD = d;
                }
            }
            var tie = second >= 0 && secondD - bestD <= 1e-6 && source[second].Text != source[best].Text;
            if (best < 0 || tie)
            {
                result[p] = new Rewritten(-1, placed[p].Text);
                continue;
            }
            used[best] = true;
            result[p] = new Rewritten(best, source[best].Text);
        }
        return result;
    }

    static double Dist(double x, double y, Placed p) => Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y));

    /// <summary>For an unmatched text: how far the nearest DXF text on its layer is, or -1 when there is none.</summary>
    public static double Nearest(IList<Entity> source, Placed placed)
    {
        var best = -1.0;
        foreach (var e in source)
        {
            if (!string.Equals(e.Layer, placed.Layer, StringComparison.OrdinalIgnoreCase)) continue;
            var d = Dist(e.X, e.Y, placed);
            if (e.Aligned) d = Math.Min(d, Dist(e.AlignX, e.AlignY, placed));
            if (best < 0 || d < best) best = d;
        }
        return best;
    }

    // What Rhino leaves of \U+XXXX: the four hex digits of a letter, right
    // next to a letter (B00F8ttekott).
    static readonly Regex Remnant = new Regex(@"(?<=\p{L})0[0-9A-F]{3}|0[0-9A-F]{3}(?=\p{L})");

    /// <summary>
    /// True when a text looks like Rhino's import mangled a \U+XXXX escape in
    /// it. Only reported, never decoded: the original is not in the model.
    /// </summary>
    public static bool LooksMangled(string text)
    {
        foreach (Match m in Remnant.Matches(text ?? ""))
            if (char.IsLetter((char)int.Parse(m.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)))
                return true;
        return false;
    }
}
