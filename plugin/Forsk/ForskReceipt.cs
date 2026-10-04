using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// One line per change, from structured data: done, failed or skipped, the
    /// object in bold, then what happened ("✓ D02 moved 400 mm along w01"). The
    /// object is the first Forsk id the tool's message names (w01, D02, V03,
    /// rd-01), else the step's label from ForskText. No RhinoCommon.
    /// </summary>
    public sealed class ForskReceipt
    {
        static readonly Regex ForskId = new Regex(@"\b(w\d{2,}|[DV]\d{2,}|rd-\d{2,})\b", RegexOptions.CultureInvariant);

        /// <summary>Every Forsk id a text names, once each, in the order it names them.</summary>
        public static IEnumerable<string> IdsIn(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in ForskId.Matches(text))
                if (seen.Add(match.Value)) yield return match.Value;
        }

        /// <summary>True done, false failed, null skipped or cancelled.</summary>
        public bool? Ok;
        public string Subject;
        public string Text;

        public JObject ToJson()
        {
            var item = new JObject { ["role"] = "receipt", ["text"] = Text ?? "" };
            if (Ok.HasValue) item["ok"] = Ok.Value;
            if (!string.IsNullOrWhiteSpace(Subject)) item["subject"] = Subject;
            return item;
        }

        /// <summary>A tool call's receipt from its envelope.</summary>
        public static ForskReceipt From(string tool, JObject envelope)
        {
            var label = StepLabel(tool);
            if (envelope == null) return new ForskReceipt { Ok = false, Subject = label, Text = "no result" };
            var status = envelope["status"]?.ToString();
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                var error = envelope["message"]?.ToString();
                return new ForskReceipt { Ok = false, Subject = label, Text = Sentences(string.IsNullOrWhiteSpace(error) ? "failed" : error, 1) };
            }
            var result = envelope["result"] as JObject;
            var message = result?["message"]?.ToString();
            if (!string.IsNullOrWhiteSpace(message))
            {
                // area_stats is already the short answer. Two sentences would drop the
                // uses and the rooms, and "1. etasje" would end at the ordinal point.
                var text = string.Equals(tool, "area_stats", StringComparison.Ordinal)
                    ? OneLine(message)
                    : Sentences(message, 2);
                var id = ForskId.Match(text);
                return new ForskReceipt { Ok = true, Subject = id.Success ? id.Value : label, Text = text };
            }
            var count = (result?["count"] ?? result?["opening_count"] ?? result?["cut_count"])?.ToString();
            return new ForskReceipt
            {
                Ok = true,
                Subject = label,
                Text = string.IsNullOrEmpty(count) ? ForskText.Get("receipt.ok") : count
            };
        }

        /// <summary>A "Label · ok · rest" line, as ForskPrint and ForskBake return them.</summary>
        public static ForskReceipt FromLine(string line)
        {
            if ((line ?? "").StartsWith(Done, StringComparison.Ordinal))
                return new ForskReceipt { Ok = true, Text = line.Substring(Done.Length).Trim() };
            var parts = (line ?? "").Split(new[] { " · " }, 3, StringSplitOptions.None);
            if (parts.Length < 2) return new ForskReceipt { Text = (line ?? "").Trim() };
            var state = parts[1].Trim().ToLowerInvariant();
            bool? ok = state == "error" ? false : state == "ok" ? true : (bool?)null;
            var text = parts.Length > 2 ? parts[2] : parts[1];
            return new ForskReceipt { Ok = ok, Subject = StepLabel(parts[0].Trim()), Text = text.Trim() };
        }

        /// <summary>
        /// Print's one line: "✓ Printed 7 sheets at 1:200 on A3 · Holmen.pdf".
        /// The file name only, no folder. A set revision is named, a sheet
        /// bumped up the ladder is one clause, and a blank page keeps its clause.
        /// scale 0: the sheets have none (a list alone). Detail sheets name
        /// their own scales after the set's, and details that dropped end it.
        /// </summary>
        public static string PrintLine(int sheets, int scale, string written, string revision, string bumped, string blank,
            IEnumerable<int> detailScales = null, int detailsDropped = 0)
        {
            var text = "Printed " + sheets.ToString(System.Globalization.CultureInfo.InvariantCulture) + (sheets == 1 ? " sheet" : " sheets");
            if (scale > 0) text += " at 1:" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
            text += " on A3";
            var details = (detailScales ?? Enumerable.Empty<int>())
                .Select(s => "1:" + s.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            if (details.Count > 0) text += ", details at " + string.Join(" and ", details);
            if (!string.IsNullOrWhiteSpace(revision)) text += ", rev. " + revision.Trim();
            var bump = (bumped ?? "").Trim().TrimEnd('.');
            if (bump.Length > 0) text += " (" + bump + ")";
            var dropped = Details.DroppedLine(detailsDropped);
            return Done + text + " · " + System.IO.Path.GetFileName(written ?? "") + (blank ?? "")
                + (dropped.Length > 0 ? " · " + dropped : "");
        }

        /// <summary>"✓ Exported 7 sheets as DWG · Holmen DWG/": the folder's name, never its path.</summary>
        public static string ExportLine(int sheets, string format, string folder)
        {
            var name = System.IO.Path.GetFileName((folder ?? "").TrimEnd('/', '\\'));
            return Done + "Exported " + sheets.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (sheets == 1 ? " sheet" : " sheets") + " as " + (format ?? "dwg").Trim().ToUpperInvariant()
                + " · " + name + "/";
        }

        /// <summary>A line that starts with this is a finished receipt as it stands.</summary>
        public const string Done = "✓ ";

        /// <summary>The label a step shows when its message names no object.</summary>
        public static string StepLabel(string tool)
        {
            var key = "tool." + (tool ?? "");
            return ForskText.Has(key) ? ForskText.Get(key) : tool ?? "";
        }

        /// <summary>The first sentences of a message: a receipt stays one line.</summary>
        static string Sentences(string text, int max)
        {
            var one = OneLine(text);
            var count = 0;
            for (var i = 0; i < one.Length; i++)
            {
                if (one[i] != '.' && one[i] != '!' && one[i] != '?') continue;
                if (i + 1 < one.Length && one[i + 1] != ' ') continue;
                if (++count == max) return one.Substring(0, i + 1);
            }
            return one;
        }

        static string OneLine(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
