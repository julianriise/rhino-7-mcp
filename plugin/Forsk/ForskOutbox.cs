using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>One report waiting in the outbox. Body is the exact JSON to POST.</summary>
    public sealed class OutboxItem
    {
        public string Id;
        public DateTimeOffset QueuedAt;
        public string Body;
        /// <summary>The .json path. A claim moves the bytes and leaves this name for the release.</summary>
        public string JsonPath;
        /// <summary>Where the bytes are now: the .json path, or the .sending path after a claim.</summary>
        public string FilePath;
    }

    /// <summary>
    /// Reports the server did not take, one file each under ~/.forsk/outbox.
    /// Oldest first. A file older than seven days is dropped. A claim renames
    /// the file so a second send cannot pick it up, and a crash leaves a
    /// .sending file that the next pass puts back.
    /// </summary>
    public sealed class ForskOutbox
    {
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
        public readonly string Root;
        public readonly object Gate = new object();
        readonly HashSet<string> _held = new HashSet<string>(StringComparer.Ordinal);

        public ForskOutbox(string root)
        {
            Root = root;
        }

        /// <summary>~/.forsk/outbox</summary>
        public static string DefaultRoot()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".forsk", "outbox");
        }

        /// <summary>The one outbox the plugin sends from. Tests use their own folder.</summary>
        public static ForskOutbox Shared { get; } = new ForskOutbox(DefaultRoot());

        public OutboxItem Enqueue(JObject payload, DateTimeOffset now)
        {
            Directory.CreateDirectory(Root);
            var id = Guid.NewGuid().ToString("n");
            var when = now.ToUniversalTime();
            var name = when.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + id + ".json";
            var path = Path.Combine(Root, name);
            var stored = new JObject
            {
                ["id"] = id,
                ["queued_at"] = when.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["body"] = (payload ?? new JObject()).ToString(Formatting.None)
            };
            File.WriteAllText(path, stored.ToString(Formatting.None), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return Read(path);
        }

        /// <summary>Waiting reports, oldest first. Expired and unreadable files are removed. Stale claims are put back.</summary>
        public List<OutboxItem> Pending(DateTimeOffset now)
        {
            var items = new List<OutboxItem>();
            if (!Directory.Exists(Root)) return items;
            Reclaim();
            foreach (var path in Directory.GetFiles(Root))
            {
                if (!path.EndsWith(".json", StringComparison.Ordinal)) continue;
                OutboxItem item;
                try { item = Read(path); }
                catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
                {
                    item = null;
                }
                if (item == null)
                {
                    TryDelete(path);
                    continue;
                }
                if (now.ToUniversalTime() - item.QueuedAt >= KeepFor)
                {
                    TryDelete(path);
                    continue;
                }
                items.Add(item);
            }
            items.Sort((a, b) =>
            {
                var byTime = a.QueuedAt.CompareTo(b.QueuedAt);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.JsonPath, b.JsonPath);
            });
            return items;
        }

        /// <summary>Renames the file so no other pass can send it. False when the file is already gone.</summary>
        public bool TryClaim(OutboxItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.JsonPath) || !File.Exists(item.JsonPath)) return false;
            var sending = item.JsonPath + ".sending";
            try
            {
                File.Move(item.JsonPath, sending);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
            item.FilePath = sending;
            lock (_held) _held.Add(sending);
            return true;
        }

        public void Complete(OutboxItem item)
        {
            ReleaseHold(item);
            if (item != null) TryDelete(item.FilePath);
        }

        /// <summary>The send did not stick. The file is waiting again.</summary>
        public void Release(OutboxItem item)
        {
            if (item == null) return;
            ReleaseHold(item);
            var from = item.FilePath;
            var to = item.JsonPath;
            if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to) && !string.Equals(from, to, StringComparison.Ordinal) && File.Exists(from) && !File.Exists(to))
            {
                try { File.Move(from, to); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
            }
            item.FilePath = to;
        }

        /// <summary>
        /// Newtonsoft otherwise reads the timestamp as a DateTime, and ToString
        /// then follows the local culture. A report would look months old and be dropped.
        /// </summary>
        static readonly JsonSerializerSettings Stored = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };

        static OutboxItem Read(string path)
        {
            var json = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path, Encoding.UTF8), Stored);
            if (json == null) return null;
            var id = json["id"]?.ToString();
            var body = json["body"]?.Type == JTokenType.String ? json["body"].ToString() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(body)) return null;
            if (!DateTimeOffset.TryParse(json["queued_at"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
                return null;
            return new OutboxItem { Id = id, QueuedAt = when.ToUniversalTime(), Body = body, JsonPath = path, FilePath = path };
        }

        void Reclaim()
        {
            foreach (var sending in Directory.GetFiles(Root))
            {
                if (!sending.EndsWith(".json.sending", StringComparison.Ordinal)) continue;
                lock (_held)
                {
                    if (_held.Contains(sending)) continue;
                }
                var json = sending.Substring(0, sending.Length - ".sending".Length);
                if (File.Exists(json))
                {
                    TryDelete(sending);
                    continue;
                }
                try { File.Move(sending, json); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
        }

        void ReleaseHold(OutboxItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath)) return;
            lock (_held) _held.Remove(item.FilePath);
        }

        static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }
    }
}
