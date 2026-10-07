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

    /// <summary>whats_new.json: the release it describes and its 2 to 4 items.</summary>
    public sealed class WhatsNewNotes
    {
        public string Version;
        public List<WhatsNewItem> Items = new List<WhatsNewItem>();
    }

    /// <summary>
    /// The What's new card: once, the first time the Forsk window opens on a
    /// new version, from the Support crew member. Never on a fresh install
    /// (no ~/.forsk/window.txt and no version seen before) and never when the
    /// notes describe another version. The release edits only whats_new.json.
    /// The choice is pure, so it tests headless; ForskWhatsNewGate reads
    /// the file and the plug-in settings.
    /// </summary>
    public static class ForskWhatsNew
    {
        public const string Kind = "whats.new";
        /// <summary>Settings → Release notes: this version's What's new card again.</summary>
        public const string MenuId = "whats.new.show";
        /// <summary>The embedded copy of plugin/Forsk/whats_new.json.</summary>
        public const string Resource = "rhinomcp.Forsk.whats_new.json";
        public const string DefaultIcon = "sparkles";
        /// <summary>The Lucide icons the window carries for the card's items.</summary>
        public static readonly string[] Icons = { "sparkles", "armchair", "activity", "link" };
        public const int MinItems = 2;
        public const int MaxItems = 4;

        /// <summary>The notes in the file, or null when they don't read or don't have 2 to 4 items with a title and a how.</summary>
        public static WhatsNewNotes Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var body = JObject.Parse(json);
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
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True when the card should show: these notes are for the running
        /// version, and this Mac ran an older Forsk (an older version seen, or
        /// none seen but the window was opened before, as 1.0.0 never stored one).
        /// </summary>
        public static bool Show(WhatsNewNotes notes, string current, string seen, bool usedBefore)
        {
            if (notes == null || string.IsNullOrEmpty(current) || notes.Version != current) return false;
            if (string.IsNullOrEmpty(seen)) return usedBefore;
            return ForskUpdate.IsNewer(current, seen);
        }

        public static CardSpec Card(WhatsNewNotes notes)
        {
            var card = new CardSpec
            {
                Kind = Kind,
                Question = ForskText.Format("whatsnew.ask", "version", notes.Version),
                Features = notes.Items.ToList(),
                Note = ForskText.Get("whatsnew.note")
            };
            card.Pills.Add(new CardPill("done", ForskText.Get("whatsnew.done")));
            return card;
        }
    }
}
