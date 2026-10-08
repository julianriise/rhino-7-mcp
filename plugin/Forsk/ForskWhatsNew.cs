using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>One line on the What's new card: what it is, and where to click to try it.</summary>
    public sealed class WhatsNewItem
    {
        public string Title;
        public string How;
        /// <summary>A Lucide icon the window has (ForskWhatsNew.Icons). Sparkles when unset or unknown.</summary>
        public string Icon;
    }

    /// <summary>One release in whats_new.json: its number and its 2 to 4 items.</summary>
    public sealed class WhatsNewNotes
    {
        public string Version;
        public List<WhatsNewItem> Items = new List<WhatsNewItem>();
    }

    /// <summary>One slide of the What's new card: a release, headed with its number.</summary>
    public sealed class WhatsNewSlide
    {
        public string Version;
        public string Title;
        public List<WhatsNewItem> Items;
    }

    /// <summary>
    /// The What's new card: once, the first time the Forsk window opens on a
    /// new version, from the Support crew member. It holds every release this
    /// Mac skipped (1.0.0 to 1.3.0 shows 1.3.0, 1.2.0 and 1.1.0, one slide
    /// each), newest first. Never on a fresh install (no ~/.forsk/window.txt
    /// and no version seen before). The release edits only whats_new.json.
    /// The choice is pure, so it tests headless; ForskWhatsNewGate reads
    /// the file and the plug-in settings.
    /// </summary>
    public static class ForskWhatsNew
    {
        public const string Kind = "whats.new";
        /// <summary>Settings → Release notes: the What's new card again, every release in the file.</summary>
        public const string MenuId = "whats.new.show";
        /// <summary>The embedded copy of plugin/Forsk/whats_new.json.</summary>
        public const string Resource = "rhinomcp.Forsk.whats_new.json";
        public const string DefaultIcon = "sparkles";
        /// <summary>The Lucide icons the window carries for the card's items.</summary>
        public static readonly string[] Icons = { "sparkles", "armchair", "activity", "link", "book-open", "rocket", "box", "camera", "git-compare", "info" };
        /// <summary>The slider's arrows. Not item icons; the window carries them too.</summary>
        public static readonly string[] SlideIcons = { "chevron-left", "chevron-right" };
        public const int MinItems = 2;
        public const int MaxItems = 4;

        /// <summary>
        /// The releases in the file, newest first, or null when the file does
        /// not read: no releases, one out of order or repeated, or one without
        /// 2 to 4 items with a title and a how. The single-release shape that
        /// shipped up to 1.3.0 still reads, as a list of one.
        /// </summary>
        public static List<WhatsNewNotes> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var body = JObject.Parse(json);
                var tokens = body["releases"] is JArray list ? list.OfType<JObject>().ToList() : new List<JObject> { body };
                var releases = new List<WhatsNewNotes>();
                foreach (var token in tokens)
                {
                    var notes = Release(token);
                    if (notes == null) return null;
                    // Newest first, each once: the slider's order is the file's.
                    if (releases.Count > 0 && !ForskUpdate.IsNewer(releases[releases.Count - 1].Version, notes.Version)) return null;
                    releases.Add(notes);
                }
                return releases.Count > 0 ? releases : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static WhatsNewNotes Release(JObject body)
        {
            var notes = new WhatsNewNotes { Version = body["version"]?.ToString().Trim() };
            foreach (var token in body["items"] as JArray ?? new JArray())
            {
                var icon = token["icon"]?.ToString().Trim();
                notes.Items.Add(new WhatsNewItem
                {
                    Title = token["title"]?.ToString().Trim(),
                    How = token["how"]?.ToString().Trim(),
                    Icon = Icons.Contains(icon) ? icon : DefaultIcon
                });
            }
            if (string.IsNullOrEmpty(notes.Version) || !ForskUpdate.IsVersion(notes.Version)) return null;
            if (notes.Items.Count < MinItems || notes.Items.Count > MaxItems) return null;
            if (notes.Items.Any(i => string.IsNullOrEmpty(i.Title) || string.IsNullOrEmpty(i.How))) return null;
            return notes;
        }

        /// <summary>
        /// The releases the card shows after an update, newest first: each one
        /// newer than the version this Mac last saw, up to the running one.
        /// None seen but the window was opened before means 1.0.0, which never
        /// stored one: every release up to the running one. Empty on a fresh
        /// install, a downgrade, or when nothing new was written down.
        /// </summary>
        public static List<WhatsNewNotes> Due(IList<WhatsNewNotes> releases, string current, string seen, bool usedBefore)
        {
            var none = new List<WhatsNewNotes>();
            if (releases == null || !ForskUpdate.IsVersion(current)) return none;
            if (string.IsNullOrEmpty(seen) && !usedBefore) return none;
            return releases
                .Where(r => !ForskUpdate.IsNewer(r.Version, current))
                .Where(r => string.IsNullOrEmpty(seen) || ForskUpdate.IsNewer(r.Version, seen))
                .ToList();
        }

        /// <summary>One card, one slide per release, opening on the newest (the first).</summary>
        public static CardSpec Card(IList<WhatsNewNotes> releases)
        {
            var slides = releases.Select(r => new WhatsNewSlide
            {
                Version = r.Version,
                Title = ForskText.Format("whatsnew.ask", "version", r.Version),
                Items = r.Items.ToList()
            }).ToList();
            var card = new CardSpec
            {
                Kind = Kind,
                Question = slides[0].Title,
                Slides = slides,
                Note = ForskText.Get("whatsnew.note")
            };
            card.Pills.Add(new CardPill("done", ForskText.Get("whatsnew.done")));
            return card;
        }
    }
}
