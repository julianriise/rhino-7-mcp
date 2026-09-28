using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The text of a label read from the model, decoded once for every reader.
/// Rhino keeps an imported DXF string as the text's RichText and parses it as
/// RTF for PlainText. DXF codes are not RTF: ON_RtfParser takes \U as a control
/// word and eats the + after it, so B\U+00F8ttekott reads B00F8ttekott. When
/// the rich text carries DXF codes it is decoded here: \U+XXXX to its
/// character, MTEXT formatting (\P, \~, \f…| and the other ;-codes, \L \O \K,
/// braces) dropped, RTF control words and tables dropped. Otherwise Rhino's
/// PlainText stands.
/// </summary>
public static class LabelText
{
    static readonly Regex DxfCodes = new Regex(
        @"\\[Uu]\+[0-9A-Fa-f]{4}|\\[PLOK]|\\[fF][^\\{};]*\|[^\\{};]*;|\\[HWQTACc][0-9.\-]+x?;|\\p[xqilrcjt0-9.,\-*]+;|\\S[^\\{};]*;");

    // An MTEXT code with an argument that runs to ';', at the backslash.
    static readonly Regex MtextArgument = new Regex(
        @"\G\\(?:[fF][^\\{};]*\|[^\\{};]*|[HWQTACc][0-9.\-]+x?|p[xqilrcjt0-9.,\-*]+|S[^\\{};]*);");

    static readonly Regex Spaces = new Regex(@"\s+");

    public static string Plain(string rich, string plain)
    {
        if (string.IsNullOrEmpty(rich) || !DxfCodes.IsMatch(rich))
            return Spaces.Replace(plain ?? "", " ").Trim();
        return Spaces.Replace(Decode(rich), " ").Trim();
    }

    static string Decode(string s)
    {
        var text = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '{')
            {
                i = TableGroup(s, i) ? SkipGroup(s, i) : i + 1;
                continue;
            }
            if (c == '}')
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
            if (n == '\\' || n == '{' || n == '}')
            {
                text.Append(n);
                i += 2;
            }
            else if ((n == 'U' || n == 'u') && i + 6 < s.Length && s[i + 2] == '+'
                && int.TryParse(s.Substring(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                text.Append((char)code);
                i += 7;
            }
            else if (n == '\'' && i + 3 < s.Length
                && int.TryParse(s.Substring(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                text.Append((char)b);
                i += 4;
            }
            else if (n == 'P' || n == '~')
            {
                text.Append(' ');
                i += 2;
            }
            else if (n == 'L' || n == 'O' || n == 'K')
            {
                i += 2;
            }
            else if (MtextArgument.Match(s, i) is Match arg && arg.Success)
            {
                i += arg.Length;
            }
            else if (char.IsLetter(n))
            {
                i = ControlWord(s, i + 1, text);
            }
            else
            {
                if (n == '_') text.Append('-');
                i += 2;
            }
        }
        return text.ToString();
    }

    /// <summary>An RTF control word at start (after the backslash): \par and \line are spaces, \uN a character.</summary>
    static int ControlWord(string s, int start, StringBuilder text)
    {
        var i = start;
        while (i < s.Length && char.IsLetter(s[i])) i++;
        var word = s.Substring(start, i - start);
        var numberStart = i;
        if (i < s.Length && s[i] == '-') i++;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        int.TryParse(s.Substring(numberStart, i - numberStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number);
        if (i < s.Length && s[i] == ' ') i++;
        if (word == "par" || word == "line" || word == "tab")
        {
            text.Append(' ');
        }
        else if (word == "u" && i > numberStart)
        {
            text.Append((char)(number < 0 ? number + 65536 : number));
            // The ANSI stand-in that follows \uN.
            if (i + 3 < s.Length && s[i] == '\\' && s[i + 1] == '\'') i += 4;
            else if (i < s.Length && s[i] != '\\' && s[i] != '{' && s[i] != '}') i++;
        }
        return i;
    }

    /// <summary>A group that holds no text: the font or colour table, or a \* destination.</summary>
    static bool TableGroup(string s, int i)
    {
        var rest = s.Substring(i + 1);
        return rest.StartsWith("\\fonttbl", StringComparison.Ordinal)
            || rest.StartsWith("\\colortbl", StringComparison.Ordinal)
            || rest.StartsWith("\\stylesheet", StringComparison.Ordinal)
            || rest.StartsWith("\\*", StringComparison.Ordinal);
    }

    static int SkipGroup(string s, int i)
    {
        var depth = 0;
        for (; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i + 1;
        }
        return s.Length;
    }
}
