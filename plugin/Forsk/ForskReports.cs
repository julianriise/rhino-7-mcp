using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Where a report goes. The file delivery appends one JSON line.
    /// Mail, a GitHub issue, or a web form can implement this later. No network here.
    /// </summary>
    public interface IReportDelivery
    {
        void Deliver(JObject report);
    }

    /// <summary>Appends one JSON object per line. Creates the folder when it is missing.</summary>
    public sealed class JsonlReportDelivery : IReportDelivery
    {
        public readonly string Path;

        public JsonlReportDelivery(string path)
        {
            Path = path;
        }

        public void Deliver(JObject report)
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(Path, (report ?? new JObject()).ToString(Formatting.None) + "\n");
        }
    }

    /// <summary>
    /// A bug report or a feature request as a card the user can edit, and the
    /// line that delivery stores. Questions are Support too, and they are not a card.
    /// When the turn's role is Support, the answer comes first and the card follows.
    /// No RhinoCommon.
    /// </summary>
    public static class ForskReports
    {
        public enum ReportKind
        {
            None,
            Bug,
            Feature
        }

        /// <summary>~/Library/Application Support/Forsk/reports.jsonl</summary>
        public static string DefaultPath
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, "Library", "Application Support", "Forsk", "reports.jsonl");
            }
        }

        public static IReportDelivery FileDelivery()
        {
            return new JsonlReportDelivery(DefaultPath);
        }

        /// <summary>A bug or a feature request. A question is None: Support answers it in the thread.</summary>
        public static ReportKind Of(string text)
        {
            if (ForskIntentRouter.IsQuestion(text)) return ReportKind.None;
            if (ForskIntentRouter.IsBug(text)) return ReportKind.Bug;
            if (ForskIntentRouter.IsFeature(text)) return ReportKind.Feature;
            return ReportKind.None;
        }

        /// <summary>
        /// Support, picked or routed, ends a bug or a feature request on the card.
        /// A question does not. Another role keeps the card without an answer.
        /// </summary>
        public static bool EndsWithReport(ForskRole overrideRole, string text)
        {
            if (Of(text) == ReportKind.None) return false;
            var role = overrideRole != ForskRole.None
                ? overrideRole
                : ForskRoles.Of(ForskIntentRouter.Classify(text));
            return role == ForskRole.Support;
        }

        /// <summary>What the turn read, after the user's line: assistant text and receipts.</summary>
        public static string Findings(IList<JObject> items)
        {
            if (items == null || items.Count == 0) return "";
            var start = 0;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (items[i]?["role"]?.ToString() == "user")
                {
                    start = i + 1;
                    break;
                }
            }
            var parts = new List<string>();
            for (var i = start; i < items.Count; i++)
            {
                var role = items[i]?["role"]?.ToString();
                if (role != "assistant" && role != "receipt") continue;
                var line = (items[i]["text"]?.ToString() ?? "").Trim();
                if (line.Length > 0) parts.Add(line);
            }
            return string.Join("\n", parts);
        }

        /// <summary>The editable report. Null for a question or any other sentence. Findings prefill a bug's happened, or a feature's expected.</summary>
        public static CardSpec Card(string text, string file, string findings = null)
        {
            var kind = Of(text);
            if (kind == ReportKind.None) return null;
            var bug = kind == ReportKind.Bug;
            var body = WithFindings((text ?? "").Trim(), findings);
            return new CardSpec
            {
                Kind = "support.report",
                Question = ForskText.Get("support.ask"),
                Fields = new List<CardField>
                {
                    new CardField { Key = "type", Label = ForskText.Get("support.type"), Value = ForskText.Get(bug ? "support.bug" : "support.feature") },
                    new CardField { Key = "happened", Label = ForskText.Get("support.happened"), Value = bug ? body : "" },
                    new CardField { Key = "expected", Label = ForskText.Get("support.expected"), Value = bug ? "" : body },
                    new CardField { Key = "steps", Label = ForskText.Get("support.steps"), Value = "" },
                    new CardField { Key = "file", Label = ForskText.Get("support.file"), Value = file ?? "" },
                    new CardField { Key = "attach", Label = ForskText.Get("support.attach"), Value = "1", Check = true }
                },
                Pills =
                {
                    new CardPill("send", ForskText.Get("support.send")),
                    new CardPill("cancel", ForskText.Get("word.cancel"))
                }
            };
        }

        /// <summary>The sentence, then a blank line, then what was found. Findings that already include the sentence stand alone.</summary>
        static string WithFindings(string said, string findings)
        {
            findings = (findings ?? "").Trim();
            if (findings.Length == 0) return said;
            if (said.Length == 0 || findings.IndexOf(said, StringComparison.Ordinal) >= 0) return findings;
            return said + "\n\n" + findings;
        }

        /// <summary>The checkbox defaults to ticked. "0" and "false" leave the debug report off.</summary>
        public static bool WantsDebug(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            return value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>One stored report. debug is omitted when the box was unticked.</summary>
        public static JObject Record(JObject fields, string debug, DateTimeOffset at)
        {
            string Value(string key) => fields?[key]?.ToString() ?? "";
            var record = new JObject
            {
                ["type"] = Value("type"),
                ["happened"] = Value("happened"),
                ["expected"] = Value("expected"),
                ["steps"] = Value("steps"),
                ["file"] = Value("file"),
                ["at"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            };
            if (debug != null) record["debug"] = debug;
            return record;
        }
    }
}
