using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// A correction of a turn the router just named. A rephrase is the next
    /// sentence when it routes to a different role. A hand switch is the role
    /// the user picks for the turn they just sent. Both append one JSON line:
    /// sentence, routed role, corrected role, time. No RhinoCommon.
    /// </summary>
    public static class ForskMisroutes
    {
        /// <summary>~/Library/Application Support/Forsk/misroutes.jsonl</summary>
        public static string DefaultPath
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, "Library", "Application Support", "Forsk", "misroutes.jsonl");
            }
        }

        /// <summary>The role the router would put on this sentence. General when it names none.</summary>
        public static string RoleName(ForskIntent intent)
        {
            return RoleName(ForskRoles.Of(intent));
        }

        /// <summary>Auto is General: the router names the turn.</summary>
        public static string RoleName(ForskRole role)
        {
            return role == ForskRole.None ? "General" : ForskRoles.Label(role);
        }

        /// <summary>
        /// The sentence the thread just answered. Null when there is no user
        /// line, or the last user line has no answer after it yet.
        /// </summary>
        public static string AnsweredSentence(IList<JObject> items)
        {
            if (items == null || items.Count == 0) return null;
            var user = -1;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (items[i]?["role"]?.ToString() == "user")
                {
                    user = i;
                    break;
                }
            }
            if (user < 0) return null;
            var answered = false;
            for (var i = user + 1; i < items.Count; i++)
            {
                var role = items[i]?["role"]?.ToString();
                if (role == "user") return null;
                if (role == "assistant" || role == "receipt" || role == "card") answered = true;
            }
            if (!answered) return null;
            var text = items[user]["text"]?.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        public static JObject Entry(string sentence, string routedRole, string correctedRole, DateTimeOffset time)
        {
            return new JObject
            {
                ["sentence"] = sentence ?? "",
                ["routed_role"] = routedRole ?? "",
                ["corrected_role"] = correctedRole ?? "",
                ["time"] = time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            };
        }

        /// <summary>The next sentence, when its role differs. The same role is not a correction.</summary>
        public static JObject Rephrase(string previous, Picked picked, string next, DateTimeOffset time)
        {
            if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(next)) return null;
            var routed = RoleName(ForskIntentRouter.Classify(previous, picked));
            var corrected = RoleName(ForskIntentRouter.Classify(next, picked));
            if (string.Equals(routed, corrected, StringComparison.Ordinal)) return null;
            return Entry(previous.Trim(), routed, corrected, time);
        }

        /// <summary>The role the user picked for that sentence. The same role is not a correction.</summary>
        public static JObject Hand(string sentence, Picked picked, ForskRole pickedRole, DateTimeOffset time)
        {
            if (string.IsNullOrWhiteSpace(sentence)) return null;
            var routed = RoleName(ForskIntentRouter.Classify(sentence, picked));
            var corrected = RoleName(pickedRole);
            if (string.Equals(routed, corrected, StringComparison.Ordinal)) return null;
            return Entry(sentence.Trim(), routed, corrected, time);
        }

        public static void Append(JObject entry, string path)
        {
            if (entry == null || string.IsNullOrWhiteSpace(path)) return;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, entry.ToString(Formatting.None) + "\n");
        }

        /// <summary>
        /// Suite B cases from the log. Ids start at B31. A role that names one
        /// intent (Analyser, Support, General) fills router. Planner, Modeller
        /// and Plotter keep expect_role and leave router unset, so a later
        /// score does not pretend the intent.
        /// </summary>
        public static JArray ToFixtures(IEnumerable<JObject> entries, int firstId = 31)
        {
            var cases = new JArray();
            var n = firstId;
            foreach (var entry in entries ?? new JObject[0])
            {
                var sentence = entry?["sentence"]?.ToString();
                if (string.IsNullOrWhiteSpace(sentence)) continue;
                var corrected = entry["corrected_role"]?.ToString() ?? "";
                var row = new JObject
                {
                    ["id"] = "B" + n.ToString("00", CultureInfo.InvariantCulture),
                    ["utterance"] = sentence,
                    ["selection"] = "empty",
                    ["routed_role"] = entry["routed_role"]?.ToString() ?? "",
                    ["corrected_role"] = corrected
                };
                var intent = IntentForRole(corrected);
                if (intent != null) row["router"] = intent;
                else row["expect_role"] = corrected;
                cases.Add(row);
                n++;
            }
            return cases;
        }

        /// <summary>The same cases from a jsonl file. A blank or broken line is skipped.</summary>
        public static JArray ToFixtures(string path, int firstId = 31)
        {
            var entries = new List<JObject>();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { entries.Add(JObject.Parse(line)); }
                    catch (JsonException) { }
                }
            }
            return ToFixtures(entries, firstId);
        }

        /// <summary>The intent a corrected role names, or null when the role covers more than one.</summary>
        public static string IntentForRole(string role)
        {
            switch ((role ?? "").Trim().ToLowerInvariant())
            {
                case "analyser": return "daylight";
                case "support": return "support";
                case "general":
                case "auto":
                case "render": return "general";
                default: return null;
            }
        }

        static readonly Dictionary<string, ForskIntent> SuiteIntent = new Dictionary<string, ForskIntent>(StringComparer.Ordinal)
        {
            ["daylight"] = ForskIntent.Daylight,
            ["build"] = ForskIntent.Build,
            ["print"] = ForskIntent.Print,
            ["sheets"] = ForskIntent.Sheets,
            ["import_dxf"] = ForskIntent.Dxf,
            ["import_plan"] = ForskIntent.Import,
            ["edit"] = ForskIntent.Edit,
            ["support"] = ForskIntent.Support,
            ["general"] = ForskIntent.General
        };

        /// <summary>How many cases the router still gets. A case with no known router intent is not counted.</summary>
        public static int Score(IEnumerable<JObject> cases, out int counted)
        {
            var hit = 0;
            counted = 0;
            if (cases == null) return 0;
            foreach (var c in cases)
            {
                if (c == null || !SuiteIntent.TryGetValue(c["router"]?.ToString() ?? "", out var expected)) continue;
                counted++;
                var selection = c["selection"]?.ToString() ?? "";
                var picked = selection == "empty" || selection.Length == 0
                    ? Picked.None
                    : selection.IndexOf("forsk:opening", StringComparison.Ordinal) >= 0 ? Picked.Opening : Picked.Other;
                if (ForskIntentRouter.Classify(c["utterance"]?.ToString(), picked) == expected) hit++;
            }
            return hit;
        }
    }
}
