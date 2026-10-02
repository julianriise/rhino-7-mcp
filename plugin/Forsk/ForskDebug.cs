using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>One room in the debug report: its name, stored area in mm², and whether the outline is closed.</summary>
    public sealed class DebugRoom
    {
        public string Name;
        public string Area;
        public bool Closed;
        public int Cells;
    }

    /// <summary>One tool call in a turn, arguments already shortened.</summary>
    public sealed class DebugCall
    {
        public string Name;
        public string Args;
    }

    /// <summary>One user message and the reply that followed it.</summary>
    public sealed class DebugTurn
    {
        public string Intent;
        public string Role;
        public string Receipt;
        /// <summary>The user text, for matching the model history. The report does not print it again.</summary>
        public string User;
        public readonly List<DebugCall> Calls = new List<DebugCall>();
    }

    /// <summary>Everything the report prints. The Rhino walk fills it; the text is pure.</summary>
    public sealed class DebugSnapshot
    {
        public string Commit;
        public string Rhino;
        public string File;
        public string Units;
        public int WallRecords;
        public int WallRuns;
        public int Openings;
        public readonly List<DebugRoom> Rooms = new List<DebugRoom>();
        public int Floors;
        public int Roofs;
        /// <summary>A daylight mesh is in the file. Cell counts are per room.</summary>
        public bool Daylight;
        public int UnassignedCells;
        public readonly List<DebugTurn> Turns = new List<DebugTurn>();
        /// <summary>The import review notes, when the underlay stored them. Null when it did not.</summary>
        public string Review;
        public string WindowLog;
        public string PluginLog;
        /// <summary>The model walk failed. Printed under Model.</summary>
        public string ModelError;
    }

    /// <summary>
    /// The debug report as plain text, and the redaction that keeps keys out of it.
    /// No RhinoCommon, so the sections and the redaction test headless.
    /// </summary>
    public static class ForskDebug
    {
        public const string MenuId = "debug.copy";
        public const string MenuLabel = "Copy debug report";
        public const string FileName = "forsk-debug.txt";
        public const int TurnLimit = 20;
        public const int ArgLimit = 240;
        public const int LogLines = 100;

        public static readonly string[] Sections =
        {
            "Plugin", "Rhino", "File", "Model", "Turns", "Import review", "Window log", "Plugin log"
        };

        /// <summary>The report, then redacted. The plugin commit is kept when it looks like a hash.</summary>
        public static string Publish(DebugSnapshot snap)
        {
            return Redact(Format(snap), snap?.Commit);
        }

        public static string Format(DebugSnapshot snap)
        {
            snap = snap ?? new DebugSnapshot();
            var text = new StringBuilder();
            Section(text, "Plugin", Blank(snap.Commit, "unknown"));
            Section(text, "Rhino", Blank(snap.Rhino, "unknown"));
            Section(text, "File", Blank(snap.File, "Untitled") + " · " + Blank(snap.Units, "millimetres"));
            Section(text, "Model", Model(snap));
            Section(text, "Turns", Turns(snap.Turns));
            Section(text, "Import review", string.IsNullOrWhiteSpace(snap.Review) ? "none" : snap.Review.Trim());
            Section(text, "Window log", string.IsNullOrWhiteSpace(snap.WindowLog) ? "none" : snap.WindowLog.TrimEnd());
            Section(text, "Plugin log", string.IsNullOrWhiteSpace(snap.PluginLog) ? "none" : snap.PluginLog.TrimEnd());
            return text.ToString().TrimEnd() + "\n";
        }

        /// <summary>The part after '+' in the assembly informational version, or "unknown".</summary>
        public static string CommitOf(string informationalVersion)
        {
            if (string.IsNullOrWhiteSpace(informationalVersion)) return "unknown";
            var plus = informationalVersion.LastIndexOf('+');
            if (plus < 0 || plus >= informationalVersion.Length - 1) return "unknown";
            var rev = informationalVersion.Substring(plus + 1).Trim();
            return rev.Length == 0 ? "unknown" : rev;
        }

        /// <summary>"Kitchen · 24.5 m² · closed". Area is mm² as stored. No area leaves the name and the closed flag.</summary>
        public static string RoomLine(string name, string areaMm2, bool closed)
        {
            var who = string.IsNullOrWhiteSpace(name) ? "Room" : DxfText.RepairRemnant(name).Trim();
            var state = closed ? "closed" : "open";
            if (!double.TryParse(areaMm2, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm2) || mm2 <= 0)
                return who + " · " + state;
            return who + " · " + (mm2 / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " m² · " + state;
        }

        /// <summary>How many cells sit in each ring, in ring order. A cell in none of them is unassigned.</summary>
        public static List<int> AssignCells(IList<RoomDetect.Pt> cells, IEnumerable<IList<RoomDetect.Pt>> rings, out int unassigned)
        {
            var ringList = new List<IList<RoomDetect.Pt>>();
            if (rings != null)
                foreach (var ring in rings) ringList.Add(ring);
            var counts = new List<int>();
            for (var i = 0; i < ringList.Count; i++) counts.Add(0);
            unassigned = 0;
            if (cells == null) return counts;
            foreach (var cell in cells)
            {
                var hit = -1;
                for (var i = 0; i < ringList.Count; i++)
                {
                    var ring = ringList[i];
                    if (ring == null || ring.Count < 3 || !RoomDetect.Contains(ring, cell)) continue;
                    hit = i;
                    break;
                }
                if (hit < 0) unassigned++;
                else counts[hit]++;
            }
            return counts;
        }

        /// <summary>The last turns: intent from the user text, the reply's role mark, tool calls, receipt.</summary>
        public static List<DebugTurn> LastTurns(IList<JObject> items, IList<JObject> history)
        {
            var turns = new List<DebugTurn>();
            DebugTurn current = null;
            foreach (var item in items ?? new List<JObject>())
            {
                if (item == null) continue;
                var role = item["role"]?.ToString();
                if (role == "user")
                {
                    if (current != null) turns.Add(current);
                    var text = item["text"]?.ToString() ?? "";
                    current = new DebugTurn
                    {
                        User = text.Trim(),
                        Intent = ForskIntentRouter.Classify(text, Picked.None).ToString(),
                        Role = "auto"
                    };
                    continue;
                }
                if (current == null) continue;
                var mark = item["mark"]?.ToString();
                if (!string.IsNullOrWhiteSpace(mark) && current.Role == "auto") current.Role = mark.Trim();
                if (role == "receipt")
                {
                    var line = item["text"]?.ToString() ?? "";
                    if (line.Length == 0) continue;
                    current.Receipt = current.Receipt == null ? line : current.Receipt + "\n" + line;
                }
            }
            if (current != null) turns.Add(current);
            AttachCalls(turns, history);
            if (turns.Count > TurnLimit) return turns.GetRange(turns.Count - TurnLimit, TurnLimit);
            return turns;
        }

        /// <summary>One line, cut at <paramref name="max"/> characters.</summary>
        public static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (max < 1 || flat.Length <= max) return flat;
            return flat.Substring(0, max) + "...";
        }

        /// <summary>The last <paramref name="lines"/> lines. Blank when there is nothing.</summary>
        public static string Tail(string text, int lines)
        {
            if (string.IsNullOrEmpty(text) || lines <= 0) return "";
            var all = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var start = all.Length > lines ? all.Length - lines : 0;
            return string.Join("\n", all.Skip(start)).TrimEnd();
        }

        /// <summary>
        /// Keys, bearer tokens, key prefixes, JWTs, and long hex runs become [redacted].
        /// <paramref name="keep"/> is put back, so the plugin commit survives. A hyphenated
        /// GUID is not a hex run.
        /// </summary>
        public static string Redact(string text, string keep = null)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            const string hole = "\u0001KEEP\u0001";
            var src = text;
            var held = !string.IsNullOrEmpty(keep) && HexRun.IsMatch(keep) && src.Contains(keep);
            if (held) src = src.Replace(keep, hole);
            src = Jwt.Replace(src, "[redacted]");
            src = Bearer.Replace(src, "Bearer [redacted]");
            src = KeyPrefix.Replace(src, "[redacted]");
            src = JsonSecret.Replace(src, "$1[redacted]$2");
            src = AssignedSecret.Replace(src, m =>
            {
                var raw = m.Value;
                var at = raw.IndexOfAny(new[] { ':', '=' });
                return at < 0 ? "[redacted]" : raw.Substring(0, at + 1) + " [redacted]";
            });
            src = HexRun.Replace(src, "[redacted]");
            if (held) src = src.Replace(hole, keep);
            return src;
        }

        static readonly Regex Jwt = new Regex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.Compiled);
        static readonly Regex Bearer = new Regex(@"(?i)\bBearer\s+\S+", RegexOptions.Compiled);
        static readonly Regex KeyPrefix = new Regex(@"(?i)\b(?:sk|xai)-[A-Za-z0-9_\-]{12,}\b|ghp_[A-Za-z0-9]{20,}\b|github_pat_[A-Za-z0-9_]{20,}\b", RegexOptions.Compiled);
        static readonly Regex JsonSecret = new Regex(
            @"(?i)(""(?:token|api_key|apikey|authorization|secret|password|bearer)""\s*:\s*"")[^""]*("")",
            RegexOptions.Compiled);
        static readonly Regex AssignedSecret = new Regex(
            @"(?i)\b(?:token|api_key|apikey|authorization|secret|password|bearer)\s*[:=]\s*\S+",
            RegexOptions.Compiled);
        static readonly Regex HexRun = new Regex(@"\b[0-9a-fA-F]{32,}\b", RegexOptions.Compiled);

        static void Section(StringBuilder text, string title, string body)
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(title).Append('\n').Append(body).Append('\n');
        }

        static string Model(DebugSnapshot snap)
        {
            var text = new StringBuilder();
            text.Append("walls: ").Append(Count(snap.WallRecords, "record", "records"))
                .Append(", ").Append(Count(snap.WallRuns, "run", "runs")).Append('\n');
            text.Append("openings: ").Append(snap.Openings).Append('\n');
            if (snap.Rooms.Count == 0) text.Append("rooms: 0\n");
            else
            {
                text.Append("rooms:\n");
                foreach (var room in snap.Rooms)
                    text.Append(RoomLine(room?.Name, room?.Area, room?.Closed ?? false)).Append('\n');
            }
            text.Append("floors: ").Append(snap.Floors).Append('\n');
            text.Append("roofs: ").Append(snap.Roofs).Append('\n');
            if (!snap.Daylight) text.Append("daylight: none");
            else
            {
                text.Append("daylight:\n");
                foreach (var room in snap.Rooms)
                    text.Append(Blank(DxfText.RepairRemnant(room?.Name), "Room")).Append(": ").Append(room?.Cells ?? 0).Append(" cells\n");
                if (snap.UnassignedCells > 0)
                    text.Append("unassigned: ").Append(snap.UnassignedCells).Append(" cells");
            }
            if (!string.IsNullOrWhiteSpace(snap.ModelError))
                text.Append("\n").Append(snap.ModelError.Trim());
            return text.ToString().TrimEnd();
        }

        static string Turns(IList<DebugTurn> turns)
        {
            if (turns == null || turns.Count == 0) return "none";
            var text = new StringBuilder();
            for (var i = 0; i < turns.Count; i++)
            {
                var turn = turns[i];
                if (i > 0) text.Append('\n');
                text.Append(i + 1).Append(". ").Append(Blank(turn?.Intent, "General"))
                    .Append(" · ").Append(Blank(turn?.Role, "auto"));
                foreach (var call in turn?.Calls ?? new List<DebugCall>())
                {
                    text.Append("\n   ").Append(string.IsNullOrWhiteSpace(call?.Name) ? "tool" : call.Name.Trim());
                    if (!string.IsNullOrWhiteSpace(call?.Args)) text.Append(' ').Append(call.Args.Trim());
                }
                if (!string.IsNullOrWhiteSpace(turn?.Receipt))
                {
                    foreach (var line in turn.Receipt.Replace("\r", "").Split('\n'))
                        if (line.Length > 0) text.Append("\n   ").Append(line);
                }
            }
            return text.ToString();
        }

        static string Count(int n, string one, string many)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        }

        static string Blank(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        /// <summary>
        /// Tool calls live on the model history, not the thread. Each history user
        /// message takes the next unused turn with the same text.
        /// </summary>
        static void AttachCalls(List<DebugTurn> turns, IList<JObject> history)
        {
            if (history == null || turns.Count == 0) return;
            var used = new bool[turns.Count];
            for (var i = 0; i < history.Count; i++)
            {
                var message = history[i];
                if (message == null || message["role"]?.ToString() != "user") continue;
                var content = message["content"]?.Type == JTokenType.Null ? "" : message["content"]?.ToString() ?? "";
                content = content.Trim();
                var at = -1;
                for (var t = 0; t < turns.Count; t++)
                {
                    if (used[t] || turns[t].User != content) continue;
                    at = t;
                    break;
                }
                if (at < 0) continue;
                used[at] = true;
                for (var j = i + 1; j < history.Count; j++)
                {
                    var next = history[j];
                    if (next == null) continue;
                    if (next["role"]?.ToString() == "user") break;
                    var calls = next["tool_calls"] as JArray;
                    if (calls == null) continue;
                    foreach (var token in calls)
                    {
                        var fn = (token as JObject)?["function"] as JObject;
                        var name = fn?["name"]?.ToString() ?? "";
                        if (name.Length == 0) continue;
                        turns[at].Calls.Add(new DebugCall { Name = name, Args = Trim(fn?["arguments"]?.ToString(), ArgLimit) });
                    }
                }
            }
        }
    }
}
