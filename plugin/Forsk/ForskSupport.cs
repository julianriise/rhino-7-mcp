using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The body POST https://forsk.app/api/support accepts, and the one sentence
    /// each response becomes. No network and no RhinoCommon: the window sends it.
    /// The website honeypot is never set.
    /// </summary>
    public static class ForskSupport
    {
        public const string Endpoint = "https://forsk.app/api/support";
        public const int MessageMaxChars = 10000;
        public const int EmailMaxChars = 254;
        public const int MetaMaxChars = 200;
        /// <summary>debug_report, UTF-8 bytes. The same cap the server enforces.</summary>
        public const int DebugMaxBytes = 204800;
        /// <summary>The whole JSON body, UTF-8 bytes. The server rejects a larger POST.</summary>
        public const int BodyMaxBytes = 256 * 1024;
        public const string TruncationNote = "\n\n[Debug report truncated.]\n";

        static readonly Regex Email = new Regex("^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        /// <summary>Crockford base32, the alphabet the server uses for FS- ids.</summary>
        static readonly Regex ReportId = new Regex("^FS-[0-9A-HJKMNP-TV-Z]{8}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A payload, or the code that kept it from being one.</summary>
        public sealed class Body
        {
            public bool Ok;
            public string Error;
            public JObject Json;
            public bool DebugTruncated;
        }

        /// <summary>What the chat should say. Retry means keep the report and try later.</summary>
        public sealed class Outcome
        {
            public bool Accepted;
            public bool Retry;
            public int Status;
            public string Code;
            public string Id;
            public string Text;
        }

        /// <summary>The card's Type dropdown, or the API word, as bug, feature or question.</summary>
        public static string MapType(string label)
        {
            var t = (label ?? "").Trim();
            if (t.Length == 0) return null;
            if (t.Equals("bug", StringComparison.OrdinalIgnoreCase) || t.Equals(ForskText.Get("support.bug"), StringComparison.OrdinalIgnoreCase))
                return "bug";
            if (t.Equals("question", StringComparison.OrdinalIgnoreCase) || t.Equals(ForskText.Get("support.question"), StringComparison.OrdinalIgnoreCase))
                return "question";
            if (t.Equals("feature", StringComparison.OrdinalIgnoreCase) || t.Equals(ForskText.Get("support.feature"), StringComparison.OrdinalIgnoreCase))
                return "feature";
            return null;
        }

        /// <summary>The same check the server runs on reply_email.</summary>
        public static bool EmailOk(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            var t = email.Trim();
            return t.Length <= EmailMaxChars && Email.IsMatch(t);
        }

        /// <summary>
        /// The JSON object to POST. debug is omitted when it is null (the box
        /// was unticked). A debug report over the byte cap is cut, and a note
        /// is added, and the whole body is kept under the server's limit.
        /// </summary>
        public static Body Build(string typeLabel, string message, string replyEmail, string pluginVersion, string rhinoVersion, string os, string debug)
        {
            var type = MapType(typeLabel);
            if (type == null) return Fail("invalid_type");
            var text = (message ?? "").Trim();
            if (text.Length == 0) return Fail("missing_message");
            if (text.Length > MessageMaxChars) return Fail("message_too_long");
            var email = (replyEmail ?? "").Trim();
            if (!EmailOk(email)) return Fail("invalid_reply_email");

            var plugin = Cap(pluginVersion);
            var rhino = Cap(rhinoVersion);
            var system = Cap(os);
            string debugText = null;
            var truncated = false;
            if (debug != null)
            {
                debugText = FitDebug(debug, draft => JsonBytes(type, text, email, plugin, rhino, system, draft), out truncated, out var error);
                if (error != null) return Fail(error);
            }
            else if (JsonBytes(type, text, email, plugin, rhino, system, null) > BodyMaxBytes)
            {
                return Fail("payload_too_large");
            }

            return new Body
            {
                Ok = true,
                Json = Object(type, text, email, plugin, rhino, system, debugText),
                DebugTruncated = truncated
            };
        }

        /// <summary>One sentence for a code, in en or nb. An FS- id and a wait are filled in when the code needs them.</summary>
        public static string Sentence(string code, string language, string id = null, int? retryAfterSeconds = null)
        {
            var nb = language == "nb";
            if (code == "ok")
            {
                if (!string.IsNullOrEmpty(id)) return ForskText.Format(nb ? "support.sent.nb" : "support.sent", "id", id);
                return ForskText.Get(nb ? "support.sent.plain.nb" : "support.sent.plain");
            }
            if (code == "rate_limited")
            {
                if (retryAfterSeconds == null)
                    return ForskText.Get(nb ? "support.rate.later.nb" : "support.rate.later");
                var minutes = Math.Max(1, (retryAfterSeconds.Value + 59) / 60);
                if (minutes == 1) return ForskText.Get(nb ? "support.rate.one.nb" : "support.rate.one");
                return ForskText.Format(nb ? "support.rate.nb" : "support.rate", "n", minutes.ToString(CultureInfo.InvariantCulture));
            }
            var key = Key(code);
            return ForskText.Get(nb ? key + ".nb" : key);
        }

        /// <summary>A response, or a transport failure (status 0), as one outcome. now reads an HTTP-date Retry-After.</summary>
        public static Outcome Parse(int status, string body, string retryAfter, string language, DateTimeOffset now)
        {
            JObject json = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try { json = JObject.Parse(body); }
                catch (JsonException) { json = null; }
            }
            var code = CodeOf(status, json);
            var retry = status == 502 || status == 503 || status == 504 || status == 0
                || code == "email_not_configured" || code == "email_send_failed" || code == "network";
            string id = null;
            if (status == 200)
            {
                retry = false;
                code = "ok";
                var raw = json?["id"]?.ToString();
                if (raw != null && ReportId.IsMatch(raw.Trim())) id = raw.Trim().ToUpperInvariant();
            }
            else if (code == "rate_limited" || status == 429)
            {
                retry = false;
                code = "rate_limited";
            }
            return new Outcome
            {
                Accepted = status == 200,
                Retry = status == 200 ? false : retry,
                Status = status,
                Code = code,
                Id = id,
                Text = Sentence(code, language == "nb" ? "nb" : "en", id, status == 429 || code == "rate_limited" ? RetrySeconds(retryAfter, now) : (int?)null)
            };
        }

        /// <summary>The sentence that keeps the card open, or null when the report can be sent.</summary>
        public static string Hold(string typeLabel, string message, string replyEmail, string language)
        {
            var built = Build(typeLabel, message, replyEmail, null, null, null, null);
            return built.Ok ? null : Sentence(built.Error, language);
        }

        /// <summary>The POST never came back. The report is kept.</summary>
        public static Outcome Network(string language)
        {
            return Parse(0, null, null, language, DateTimeOffset.UtcNow);
        }

        /// <summary>Retry-After as seconds. An integer, or an HTTP date. Null when it is missing or not a wait.</summary>
        public static int? RetrySeconds(string header, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            var t = header.Trim();
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                return Math.Max(0, seconds);
            if (DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                var delta = (int)Math.Ceiling((when - now).TotalSeconds);
                return Math.Max(0, delta);
            }
            return null;
        }

        /// <summary>
        /// Cut on a UTF-8 boundary so the result, including the note, is at most
        /// maxBytes. A string that already fits is returned unchanged.
        /// </summary>
        public static string TruncateUtf8(string text, int maxBytes, string note)
        {
            if (text == null) return null;
            var utf8 = Encoding.UTF8;
            if (utf8.GetByteCount(text) <= maxBytes) return text;
            note = note ?? "";
            var noteBytes = utf8.GetByteCount(note);
            var budget = maxBytes - noteBytes;
            if (budget <= 0) return note;
            var bytes = utf8.GetBytes(text);
            var cut = Math.Min(budget, bytes.Length);
            if (cut < bytes.Length && (bytes[cut] & 0xC0) == 0x80)
            {
                while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
            }
            return utf8.GetString(bytes, 0, cut) + note;
        }

        static Body Fail(string error)
        {
            return new Body { Ok = false, Error = error };
        }

        static string Cap(string value)
        {
            var t = (value ?? "").Trim();
            if (t.Length <= MetaMaxChars) return t;
            return t.Substring(0, MetaMaxChars);
        }

        static string Key(string code)
        {
            switch (code)
            {
                case "email_not_configured": return "support.later";
                case "email_send_failed":
                case "network": return "support.failed";
                case "invalid_reply_email": return "support.email.bad";
                case "missing_message": return "support.message.missing";
                case "message_too_long": return "support.message.long";
                case "invalid_type": return "support.type.bad";
                case "payload_too_large": return "support.too_large";
                case "debug_report_too_large": return "support.debug_large";
                default: return "support.rejected";
            }
        }

        static string CodeOf(int status, JObject json)
        {
            var error = json?["error"]?.Type == JTokenType.String ? json["error"].ToString() : null;
            if (!string.IsNullOrEmpty(error)) return error;
            if (status == 200) return "ok";
            if (status == 503) return "email_not_configured";
            if (status == 502 || status == 504) return "email_send_failed";
            if (status == 429) return "rate_limited";
            if (status == 413) return "payload_too_large";
            if (status == 0) return "network";
            return "rejected";
        }

        /// <summary>Shrink the original debug until both caps hold. The note is added only when it was cut.</summary>
        static string FitDebug(string debug, Func<string, int> jsonBytes, out bool truncated, out string error)
        {
            truncated = false;
            error = null;
            var max = DebugMaxBytes;
            for (var i = 0; i < 12; i++)
            {
                var text = TruncateUtf8(debug, max, TruncationNote);
                var cut = !string.Equals(text, debug, StringComparison.Ordinal);
                var debugBytes = Encoding.UTF8.GetByteCount(text);
                var size = jsonBytes(text);
                if (debugBytes <= DebugMaxBytes && size <= BodyMaxBytes)
                {
                    truncated = cut;
                    return text;
                }
                var overflow = Math.Max(size - BodyMaxBytes, debugBytes - DebugMaxBytes);
                var next = max - Math.Max(overflow, 64) - 64;
                if (next < Encoding.UTF8.GetByteCount(TruncationNote) + 32)
                {
                    error = "payload_too_large";
                    return null;
                }
                max = next;
            }
            error = "payload_too_large";
            return null;
        }

        static int JsonBytes(string type, string message, string email, string plugin, string rhino, string os, string debug)
        {
            return Encoding.UTF8.GetByteCount(Object(type, message, email, plugin, rhino, os, debug).ToString(Formatting.None));
        }

        static JObject Object(string type, string message, string email, string plugin, string rhino, string os, string debug)
        {
            var json = new JObject
            {
                ["type"] = type,
                ["message"] = message,
                ["reply_email"] = email
            };
            if (plugin.Length > 0) json["plugin_version"] = plugin;
            if (rhino.Length > 0) json["rhino_version"] = rhino;
            if (os.Length > 0) json["os"] = os;
            if (debug != null) json["debug_report"] = debug;
            return json;
        }
    }
}
