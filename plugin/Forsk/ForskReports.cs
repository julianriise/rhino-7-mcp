using System;
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

        /// <summary>The editable report. Null for a question or any other sentence.</summary>
        public static CardSpec Card(string text, string file)
        {
            var kind = Of(text);
            if (kind == ReportKind.None) return null;
            var bug = kind == ReportKind.Bug;
            var said = (text ?? "").Trim();
            return new CardSpec
            {
                Kind = "support.report",
                Question = ForskText.Get("support.ask"),
                Fields = new System.Collections.Generic.List<CardField>
                {
                    new CardField { Key = "type", Label = ForskText.Get("support.type"), Value = ForskText.Get(bug ? "support.bug" : "support.feature") },
                    new CardField { Key = "happened", Label = ForskText.Get("support.happened"), Value = bug ? said : "" },
                    new CardField { Key = "expected", Label = ForskText.Get("support.expected"), Value = bug ? "" : said },
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
