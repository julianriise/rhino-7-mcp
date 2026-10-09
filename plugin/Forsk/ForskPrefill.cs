using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>A sentence for the composer, with the number selected so the user types over it. It is not sent.</summary>
    public sealed class Prefill
    {
        public string Text;
        public int Start;
        public int Length;

        public JObject ToJson(long n)
        {
            return new JObject { ["n"] = n, ["text"] = Text, ["start"] = Start, ["end"] = Start + Length };
        }
    }

    /// <summary>
    /// The prefill actions' sentences. A sentence follows the language of the
    /// user's last message; labels stay English. No RhinoCommon.
    /// </summary>
    public static class ForskPrefill
    {
        static readonly string[] NorwegianWords =
        {
            "og", "ikke", "er", "på", "til", "med", "jeg", "det", "en", "et", "skriv", "ut", "flytt", "lag", "gjør",
            "vegg", "veggen", "vindu", "vinduet", "dør", "døra", "døren", "rom", "dagslys", "snitt", "målestokk", "sett", "fjern"
        };

        /// <summary>nb when the text has æ, ø or å, or a common Norwegian word; else en.</summary>
        public static string Language(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "en";
            var t = text.ToLowerInvariant();
            if (t.IndexOfAny(new[] { 'æ', 'ø', 'å' }) >= 0) return "nb";
            var words = t.Split(new[] { ' ', ',', '.', '!', '?', ':', ';', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Any(w => NorwegianWords.Contains(w)) ? "nb" : "en";
        }

        /// <summary>The sentence for a prefill action, or null when the action is not a prefill or its selection is gone.</summary>
        public static Prefill For(string actionId, FileFacts f, string lastUserText)
        {
            var nb = Language(lastUserText) == "nb";
            switch (actionId)
            {
                case "opening.move":
                    return Opening("opening.move.prefill", f, nb, "500");
                case "opening.resize":
                    return Opening("opening.resize.prefill", f, nb, f?.PickedOpeningKind == "door" ? "900" : "1200");
                case "room.set_type":
                    // The user finishes the sentence: the cursor waits at its end, nothing selected.
                    var text = ForskText.Get(nb ? "room.set_type.prefill.nb" : "room.set_type.prefill");
                    return new Prefill { Text = text, Start = text.Length, Length = 0 };
                case "room.push_pull":
                    return Fill(ForskText.Get(nb ? "room.push_pull.prefill.nb" : "room.push_pull.prefill"), "", "500");
                default:
                    return null;
            }
        }

        static Prefill Opening(string key, FileFacts f, bool nb, string number)
        {
            var kind = f?.PickedOpeningKind ?? "window";
            var word = ForskText.Get("word." + kind + (nb ? ".nb" : ""));
            return Fill(ForskText.Get(nb ? key + ".nb" : key), word, number);
        }

        static Prefill Fill(string template, string kind, string number)
        {
            var text = template.Replace("{kind}", kind);
            var at = text.IndexOf("{n}", StringComparison.Ordinal);
            return new Prefill { Text = text.Replace("{n}", number), Start = at, Length = number.Length };
        }
    }

    /// <summary>
    /// The language of the chat turn or job that is running a tool. The window
    /// sets it for that turn and clears it after. An external MCP call leaves
    /// it English. Not thread-static: the tool runs on the UI thread.
    /// </summary>
    public static class ForskSpeech
    {
        static bool _nb;

        public static bool Norwegian => _nb;

        public static void Use(string text)
        {
            _nb = ForskPrefill.Language(text) == "nb";
        }

        public static void Clear()
        {
            _nb = false;
        }
    }
}
