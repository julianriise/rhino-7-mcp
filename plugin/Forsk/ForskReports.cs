using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// A JSON line on disk. The card posts to forsk.app. This writer stays
    /// for that file shape. No network here.
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

        /// <summary>
        /// The editable report. Null for a question or any other sentence.
        /// The file name is not a field. The description is the user's own
        /// sentence. replyEmail is the remembered address; an empty or invalid
        /// one leaves the email field blank so the card never asks in silence.
        /// </summary>
        public static CardSpec Card(string text, string file, string replyEmail = null)
        {
            var kind = Of(text);
            if (kind == ReportKind.None) return null;
            var said = (text ?? "").Trim();
            var email = ForskSupport.EmailOk(replyEmail) ? replyEmail.Trim() : "";
            return new CardSpec
            {
                Kind = "support.report",
                Question = ForskText.Get("support.ask"),
                Fields = new List<CardField>
                {
                    new CardField
                    {
                        Key = "type",
                        Label = ForskText.Get("support.type"),
                        Value = ForskText.Get(kind == ReportKind.Bug ? "support.bug" : "support.feature"),
                        Options = new List<string>
                        {
                            ForskText.Get("support.bug"),
                            ForskText.Get("support.question"),
                            ForskText.Get("support.feature")
                        }
                    },
                    new CardField
                    {
                        Key = "description",
                        Label = ForskText.Get("support.description"),
                        Value = said,
                        Long = true
                    },
                    new CardField
                    {
                        Key = "email",
                        Label = ForskText.Get("support.email"),
                        Value = email
                    },
                    new CardField { Key = "attach", Label = ForskText.Get("support.attach"), Value = "1", Check = true }
                },
                Data = new JObject { ["file"] = file ?? "" },
                Pills =
                {
                    new CardPill("send", ForskText.Get("support.send")),
                    new CardPill("cancel", ForskText.Get("word.cancel"))
                }
            };
        }

        /// <summary>Puts a blank email field in front of the debug tick when a card has none.</summary>
        public static bool EnsureEmailField(JObject card)
        {
            if (card == null) return false;
            var fields = card["fields"] as JArray;
            if (fields == null)
            {
                fields = new JArray();
                card["fields"] = fields;
            }
            foreach (var field in fields)
                if (field["key"]?.ToString() == "email") return false;
            var email = new JObject
            {
                ["key"] = "email",
                ["label"] = ForskText.Get("support.email"),
                ["value"] = ""
            };
            var at = fields.Count;
            for (var i = 0; i < fields.Count; i++)
                if (fields[i]["key"]?.ToString() == "attach") { at = i; break; }
            fields.Insert(at, email);
            return true;
        }

        /// <summary>The checkbox defaults to ticked. "0" and "false" leave the debug report off.</summary>
        public static bool WantsDebug(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            return value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>One stored report. The body is one description. debug is omitted when the box was unticked.</summary>
        public static JObject Record(JObject fields, string debug, DateTimeOffset at)
        {
            string Value(string key) => fields?[key]?.ToString() ?? "";
            var record = new JObject
            {
                ["type"] = Value("type"),
                ["description"] = Description(fields),
                ["file"] = Value("file"),
                ["at"] = at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            };
            if (debug != null) record["debug"] = debug;
            return record;
        }

        /// <summary>
        /// A stored line as one description. A new line already has it. An old
        /// line still has happened, expected and steps, joined in that order.
        /// </summary>
        public static JObject Read(JObject line)
        {
            var copy = line == null ? new JObject() : (JObject)line.DeepClone();
            if (string.IsNullOrEmpty(copy["description"]?.ToString()))
                copy["description"] = Description(copy);
            return copy;
        }

        /// <summary>description, or the old three fields joined. Empty parts are skipped.</summary>
        public static string Description(JObject fields)
        {
            var description = fields?["description"]?.ToString() ?? "";
            if (description.Length > 0) return description;
            var parts = new List<string>();
            foreach (var key in new[] { "happened", "expected", "steps" })
            {
                var part = (fields?[key]?.ToString() ?? "").Trim();
                if (part.Length > 0) parts.Add(part);
            }
            return string.Join("\n\n", parts);
        }
    }
}
