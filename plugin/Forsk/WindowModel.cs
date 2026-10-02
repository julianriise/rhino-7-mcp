using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>One pill on an options card: the id the page posts back, the label it shows.</summary>
    public sealed class CardPill
    {
        public string Id;
        public string Label;

        public CardPill(string id, string label)
        {
            Id = id;
            Label = label;
        }
    }

    /// <summary>
    /// One document's thread as the page draws it: bubbles, lines, receipts
    /// and options cards, plus the model history and a static step line while
    /// a long job runs. An options card is open until it is answered once,
    /// closed, or made stale. No RhinoCommon, so it tests headless.
    /// </summary>
    public sealed class DocThread
    {
        public const int Keep = 200;

        public uint Serial;
        /// <summary>The file name in the header. "Untitled" until the file has a name.</summary>
        public string File = "Untitled";
        /// <summary>The full path, once the file is saved. The store keys on it.</summary>
        public string Path;
        public readonly List<JObject> Items = new List<JObject>();
        /// <summary>The chat model's messages for this file. One history for every role.</summary>
        public readonly List<JObject> History = new List<JObject>();
        /// <summary>A static step line while a long job runs, or null.</summary>
        public string Busy;
        long _next;

        public JObject Add(string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return Push(new JObject { ["role"] = role, ["text"] = text.Trim() });
        }

        /// <summary>A receipt: one line per change, the object in bold.</summary>
        public JObject AddReceipt(bool ok, string subject, string text)
        {
            var item = new JObject { ["role"] = "receipt", ["ok"] = ok, ["text"] = (text ?? "").Trim() };
            if (!string.IsNullOrWhiteSpace(subject)) item["subject"] = subject.Trim();
            return Push(item);
        }

        /// <summary>A one-time question. kind tells the window what the pills do.</summary>
        public JObject AddCard(string kind, string question, params CardPill[] pills)
        {
            var list = new JArray();
            foreach (var pill in pills ?? new CardPill[0])
                list.Add(new JObject { ["id"] = pill.Id, ["label"] = pill.Label });
            return Push(new JObject
            {
                ["role"] = "card",
                ["kind"] = kind,
                ["question"] = question,
                ["pills"] = list,
                ["state"] = "open"
            });
        }

        public JObject Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var item in Items)
                if (string.Equals(item["id"]?.ToString(), id, StringComparison.Ordinal)) return item;
            return null;
        }

        /// <summary>
        /// Answer an open card once. Null when the card is not open (answered,
        /// closed, stale, or never there) or the pill is not on it: the click does nothing.
        /// </summary>
        public CardPill Answer(string cardId, string pillId)
        {
            var card = Find(cardId);
            if (card == null || card["role"]?.ToString() != "card" || card["state"]?.ToString() != "open") return null;
            foreach (var token in card["pills"] as JArray ?? new JArray())
            {
                if (!string.Equals(token["id"]?.ToString(), pillId, StringComparison.Ordinal)) continue;
                var pill = new CardPill(pillId, token["label"]?.ToString());
                card["state"] = "answered";
                card["answer"] = pill.Label;
                return pill;
            }
            return null;
        }

        /// <summary>Esc on an open card: it closes and does nothing else.</summary>
        public bool Close(string cardId)
        {
            var card = Find(cardId);
            if (card == null || card["state"]?.ToString() != "open") return false;
            card["state"] = "closed";
            return true;
        }

        /// <summary>The state moved under every open card: each turns grey and stops taking clicks.</summary>
        public int StaleOpenCards()
        {
            var count = 0;
            foreach (var item in Items)
            {
                if (item["role"]?.ToString() != "card" || item["state"]?.ToString() != "open") continue;
                item["state"] = "stale";
                count++;
            }
            return count;
        }

        public string OpenCard()
        {
            for (var i = Items.Count - 1; i >= 0; i--)
                if (Items[i]["role"]?.ToString() == "card" && Items[i]["state"]?.ToString() == "open")
                    return Items[i]["id"]?.ToString();
            return null;
        }

        /// <summary>The page's model for this document.</summary>
        public JObject ToJson()
        {
            var model = new JObject
            {
                ["serial"] = Serial,
                ["file"] = File ?? "",
                ["thread"] = new JArray(Items.ConvertAll(i => (JToken)i.DeepClone()))
            };
            if (!string.IsNullOrEmpty(Busy)) model["busy"] = new JObject { ["text"] = Busy };
            return model;
        }

        /// <summary>What the store keeps: the thread and the history, never a busy line.</summary>
        public JObject Save()
        {
            var items = Items.Count > Keep ? Items.GetRange(Items.Count - Keep, Keep) : Items;
            return new JObject
            {
                ["path"] = Path ?? "",
                ["next"] = _next,
                ["thread"] = new JArray(items.ConvertAll(i => (JToken)i.DeepClone())),
                ["history"] = new JArray(History.ConvertAll(h => (JToken)h.DeepClone()))
            };
        }

        /// <summary>A stored thread comes back. A card left open when the file closed is stale now.</summary>
        public void Restore(JObject stored)
        {
            Items.Clear();
            History.Clear();
            if (stored == null) return;
            foreach (var token in stored["thread"] as JArray ?? new JArray())
                if (token is JObject item) Items.Add(item);
            foreach (var token in stored["history"] as JArray ?? new JArray())
                if (token is JObject message) History.Add(message);
            _next = stored["next"]?.Type == JTokenType.Integer ? stored["next"].Value<long>() : Items.Count;
            StaleOpenCards();
        }

        JObject Push(JObject item)
        {
            _next++;
            item["id"] = "m" + _next;
            Items.Add(item);
            return item;
        }
    }

    /// <summary>
    /// The page's whole model for one document: the file name, Target, the
    /// status line, the thread, and the pinned bar. C# builds it from the
    /// thread and the classifier's facts; the page draws it. An empty thread
    /// shows one local sentence that names the file state; it is not stored,
    /// so it never goes out of date.
    /// </summary>
    public static class WindowView
    {
        public static JObject Build(DocThread thread, FileFacts facts, string target)
        {
            var model = thread.ToJson();
            if (thread.Items.Count == 0)
                ((JArray)model["thread"]).Add(new JObject { ["role"] = "line", ["id"] = "state", ["text"] = ForskRegistry.StateSentence(facts) });
            model["target"] = target ?? "";
            model["status"] = ForskRegistry.Status(facts);
            model["bar"] = ForskRegistry.Bar(facts).ToJson();
            return model;
        }
    }

    /// <summary>The window serves every open file: one thread per document, keyed by RuntimeSerialNumber.</summary>
    public sealed class WindowModels
    {
        readonly Dictionary<uint, DocThread> _byDoc = new Dictionary<uint, DocThread>();
        readonly ThreadStore _store;

        public WindowModels(ThreadStore store)
        {
            _store = store;
        }

        /// <summary>The thread for a document. A saved file's thread comes back from the store the first time.</summary>
        public DocThread For(uint serial, string file, string path)
        {
            if (!_byDoc.TryGetValue(serial, out var thread))
            {
                thread = new DocThread { Serial = serial };
                _byDoc[serial] = thread;
                if (!string.IsNullOrEmpty(path) && _store != null)
                    thread.Restore(_store.Load(path));
            }
            if (!string.IsNullOrWhiteSpace(file)) thread.File = file;
            if (!string.IsNullOrEmpty(path)) thread.Path = path;
            return thread;
        }

        public bool Has(uint serial)
        {
            return _byDoc.ContainsKey(serial);
        }

        /// <summary>The file closed. Its thread stays in the store, not in memory.</summary>
        public void Forget(uint serial)
        {
            _byDoc.Remove(serial);
        }

        /// <summary>Keep the thread on disk under the file's path. An untitled file waits until it is saved.</summary>
        public void Persist(DocThread thread)
        {
            if (thread == null || string.IsNullOrEmpty(thread.Path) || _store == null) return;
            _store.Save(thread.Path, thread.Save());
        }
    }

    /// <summary>
    /// Threads on disk, one JSON file per document path under ~/.forsk/threads.
    /// The .3dm is not touched, so keeping a thread never adds an undo step or
    /// marks the file changed.
    /// </summary>
    public sealed class ThreadStore
    {
        readonly string _root;

        public ThreadStore(string root)
        {
            _root = root;
        }

        public static string DefaultRoot()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(home, ".forsk", "threads");
        }

        public string FileFor(string path)
        {
            var key = (path ?? "").Trim().ToLowerInvariant();
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder(40);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2"));
                return System.IO.Path.Combine(_root, sb + ".json");
            }
        }

        public JObject Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var file = FileFor(path);
            if (!File.Exists(file)) return null;
            try
            {
                return JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Save(string path, JObject stored)
        {
            if (string.IsNullOrEmpty(path) || stored == null) return;
            var file = FileFor(path);
            Directory.CreateDirectory(_root);
            var temp = file + ".tmp";
            File.WriteAllText(temp, stored.ToString(Formatting.None), Encoding.UTF8);
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
        }
    }
}
