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
        /// <summary>An inline page icon drawn before the label (window.html holder icon-{Icon}), or null.</summary>
        public string Icon;

        public CardPill(string id, string label, string icon = null)
        {
            Id = id;
            Label = label;
            Icon = icon;
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

        /// <summary>The user's last message, or null. Prefills and the pick line follow its language.</summary>
        public string LastUserText()
        {
            for (var i = Items.Count - 1; i >= 0; i--)
                if (Items[i]["role"]?.ToString() == "user") return Items[i]["text"]?.ToString();
            return null;
        }
        /// <summary>The chat model's messages for this file. One history for every role.</summary>
        public readonly List<JObject> History = new List<JObject>();
        /// <summary>A static step line while a long job runs, or null.</summary>
        public string Busy;
        /// <summary>True only while a model call is in flight: the three dots.</summary>
        public bool Thinking;
        /// <summary>The sentence a prefill action put in the composer, once (its n grows), or null.</summary>
        public JObject Prefill;
        /// <summary>The user's role pick for this file. None: the router names each answer's role.</summary>
        public ForskRole Override;
        /// <summary>The running turn's role mark, shown on its thinking or step line.</summary>
        public string TurnMark;
        /// <summary>The last role a turn took. Idle keeps it, so the header does not snap back.</summary>
        public string Shown;
        string _replyMark;
        long _next;

        /// <summary>A turn starts: its first reply (an answer, a receipt or a card) carries the role mark.</summary>
        public void BeginReply(string mark)
        {
            _replyMark = string.IsNullOrWhiteSpace(mark) ? null : mark;
            TurnMark = _replyMark;
            if (_replyMark != null) Shown = _replyMark;
        }

        public void EndReply()
        {
            _replyMark = null;
            TurnMark = null;
        }

        public JObject Add(string role, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return Push(new JObject { ["role"] = role, ["text"] = text.Trim() });
        }

        /// <summary>
        /// A line in the thread. The same line already at the end is not added
        /// again, so one stale notice does not stack when it arrives twice.
        /// </summary>
        public JObject AddLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (Items.Count > 0)
            {
                var last = Items[Items.Count - 1];
                if (last["role"]?.ToString() == "line" && last["text"]?.ToString() == text)
                    return last;
            }
            return Push(new JObject { ["role"] = "line", ["text"] = text });
        }

        /// <summary>A receipt: one line per change, the object in bold.</summary>
        public JObject AddReceipt(bool ok, string subject, string text)
        {
            var item = new JObject { ["role"] = "receipt", ["ok"] = ok, ["text"] = (text ?? "").Trim() };
            if (!string.IsNullOrWhiteSpace(subject)) item["subject"] = subject.Trim();
            return Push(item);
        }

        /// <summary>A structured receipt: done, failed or skipped, the object, what happened.</summary>
        public JObject Add(ForskReceipt receipt)
        {
            return receipt == null ? null : Push(receipt.ToJson());
        }

        /// <summary>A one-time question. kind tells the window what the pills do.</summary>
        public JObject AddCard(string kind, string question, params CardPill[] pills)
        {
            return AddCard(new CardSpec { Kind = kind, Question = question, Pills = new List<CardPill>(pills ?? new CardPill[0]) }, null);
        }

        /// <summary>
        /// A one-time question from its spec, stamped with the facts it depends
        /// on now, so it turns grey once they change.
        /// </summary>
        public JObject AddCard(CardSpec spec, FileFacts facts)
        {
            var item = new JObject
            {
                ["role"] = "card",
                ["kind"] = spec.Kind,
                ["question"] = spec.Question,
                ["pills"] = Pills(spec),
                ["state"] = "open",
                ["depends"] = spec.Depends ?? "none",
                ["stamp"] = ForskCards.Stamp(spec.Depends, facts)
            };
            if (spec.Fields != null)
            {
                var fields = new JArray();
                foreach (var field in spec.Fields)
                {
                    var f = new JObject { ["key"] = field.Key, ["label"] = field.Label ?? "", ["value"] = field.Value ?? "" };
                    if (!string.IsNullOrEmpty(field.Unit)) f["unit"] = field.Unit;
                    if (field.Check) f["check"] = true;
                    if (field.Long) f["long"] = true;
                    if (field.Order) f["order"] = true;
                    if (field.Options != null && field.Options.Count > 0)
                        f["options"] = new JArray(field.Options);
                    if (!string.IsNullOrEmpty(field.Placeholder)) f["placeholder"] = field.Placeholder;
                    if (field.Secret) f["secret"] = true;
                    fields.Add(f);
                }
                item["fields"] = fields;
            }
            if (spec.Rows != null) item["rows"] = new JArray(spec.Rows);
            if (!string.IsNullOrEmpty(spec.Note)) item["note"] = spec.Note;
            if (spec.Data != null) item["data"] = spec.Data.DeepClone();
            return Push(item);
        }

        static JArray Pills(CardSpec spec)
        {
            var pills = new JArray();
            foreach (var pill in spec.Pills)
            {
                var token = new JObject { ["id"] = pill.Id, ["label"] = pill.Label };
                if (!string.IsNullOrEmpty(pill.Icon)) token["icon"] = pill.Icon;
                pills.Add(token);
            }
            return pills;
        }

        /// <summary>
        /// Background work moved what an open card of spec's kind shows: its
        /// question, rows, pills and note become spec's, in place. Returns how many.
        /// </summary>
        public int Refresh(CardSpec spec)
        {
            var count = 0;
            foreach (var item in Items)
            {
                if (item["role"]?.ToString() != "card" || item["state"]?.ToString() != "open" || item["kind"]?.ToString() != spec.Kind) continue;
                item["question"] = spec.Question;
                item["pills"] = Pills(spec);
                if (spec.Rows != null) item["rows"] = new JArray(spec.Rows);
                else item.Remove("rows");
                if (!string.IsNullOrEmpty(spec.Note)) item["note"] = spec.Note;
                else item.Remove("note");
                count++;
            }
            return count;
        }

        /// <summary>
        /// The state moved: each open card whose selection or model is no longer
        /// what it was turns grey and stops taking clicks. Returns how many.
        /// </summary>
        public int StaleCards(FileFacts facts)
        {
            var count = 0;
            foreach (var item in Items)
            {
                if (item["role"]?.ToString() != "card" || item["state"]?.ToString() != "open") continue;
                var depends = item["depends"]?.ToString() ?? "none";
                if (depends == "none") continue;
                if (item["stamp"]?.ToString() == ForskCards.Stamp(depends, facts)) continue;
                item["state"] = "stale";
                count++;
            }
            return count;
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
            if (Thinking) model["busy"] = new JObject { ["kind"] = "thinking", ["text"] = ForskText.Get("line.thinking") };
            else if (!string.IsNullOrEmpty(Busy)) model["busy"] = new JObject { ["kind"] = "step", ["text"] = Busy };
            if (model["busy"] is JObject busy && TurnMark != null) busy["mark"] = TurnMark;
            // The header reads turn the moment the action starts, before a step line or a reply exists.
            if (TurnMark != null) model["turn"] = TurnMark;
            if (Shown != null) model["shown"] = Shown;
            if (Prefill != null) model["prefill"] = Prefill.DeepClone();
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
            var role = item["role"]?.ToString();
            if (_replyMark != null && (role == "assistant" || role == "receipt" || role == "card"))
            {
                item["mark"] = _replyMark;
                _replyMark = null;
            }
            Items.Add(item);
            return item;
        }
    }

    /// <summary>
    /// The page's whole model for one document: the file name, the pick line, the
    /// status line, the thread, the pinned bar, the settings menu, and the
    /// attention list. C# builds it from the thread and the classifier's facts;
    /// the page draws it. An empty thread shows one local sentence that names
    /// the file state; it is not stored, so it never goes out of date.
    /// </summary>
    public static class WindowView
    {
        public static JObject Build(DocThread thread, FileFacts facts, bool helpOpen = false)
        {
            var model = thread.ToJson();
            if (thread.Items.Count == 0)
                ((JArray)model["thread"]).Add(new JObject { ["role"] = "line", ["id"] = "state", ["text"] = ForskRegistry.StateSentence(facts) });
            // The pick line: what is selected, in words (selection S4).
            model["target"] = ForskPick.Line(facts.Selected, ForskPrefill.Language(thread.LastUserText()) == "nb");
            model["status"] = ForskRegistry.Status(facts);
            model["bar"] = ForskRegistry.Bar(facts, thread.Override).ToJson();
            model["role"] = ForskRoles.Control(thread.Override);
            model["settings"] = Settings(facts);
            model["attention"] = Attention(facts);
            if (helpOpen) model["help"] = ForskRegistry.Card(facts).ToJson();
            if (FirstRun.Show(facts.Kind, facts.GuideOff)) model["guide"] = FirstRun.Guide(facts.KeyPresent, facts.ToolsReady);
            return model;
        }

        /// <summary>Update available first while forsk.app names a newer Forsk. Then ink, the title block, and the bridge, then Set up Forsk, the Grok key and Copy debug report. Those three are always there and are not help-card actions.</summary>
        static JArray Settings(FileFacts facts)
        {
            var menu = new JArray();
            if (facts.UpdateVersion != null)
                menu.Add(new JObject { ["id"] = ForskUpdate.MenuId, ["label"] = ForskText.Format("update.row", "version", facts.UpdateVersion) });
            foreach (var id in new[] { "ink.set", "daylight.quality", "meta.title", "bridge.start" })
            {
                var action = ForskRegistry.Find(id);
                if (action != null && action.Shows(facts))
                    menu.Add(new JObject { ["id"] = action.Id, ["label"] = action.Label });
            }
            menu.Add(new JObject { ["id"] = ForskSetup.MenuId, ["label"] = ForskText.Get(ForskSetup.MenuId) });
            menu.Add(new JObject { ["id"] = ForskKeyFile.MenuId, ["label"] = ForskText.Get(ForskKeyFile.MenuId) });
            menu.Add(new JObject { ["id"] = ForskDebug.MenuId, ["label"] = ForskDebug.MenuLabel });
            return menu;
        }

        /// <summary>
        /// What this file still owes the user. Each row is an id, the label the
        /// menu already uses, and whether it needs attention. A new case is one
        /// row. The page dots the gear when any row needs it, and dots the menu
        /// item with the same id. Saving the title block re-reads the file on
        /// the next push, so a stored project name clears the row at once.
        /// </summary>
        static JArray Attention(FileFacts facts)
        {
            var rows = new JArray
            {
                AttentionRow("meta.title", ForskCards.InfoMissing(facts))
            };
            // Only while there is an update: the row has no menu item to dot otherwise.
            if (facts.UpdateVersion != null) rows.Add(AttentionRow(ForskUpdate.MenuId, true));
            return rows;
        }

        static JObject AttentionRow(string id, bool needs)
        {
            return new JObject
            {
                ["id"] = id,
                ["label"] = ForskText.Label(id),
                ["needs"] = needs
            };
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
