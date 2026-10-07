using System;
using System.IO;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// What's new on this Mac: the version last seen in plug-in settings, and
    /// whether the window was ever opened (~/.forsk/window.txt). Decided once
    /// at plug-in load; the window takes the card the first time it draws.
    /// </summary>
    public static class ForskWhatsNewGate
    {
        public const string SeenKey = "forsk.whatsnew.seen";

        static WhatsNewNotes _pending;

        /// <summary>
        /// At plug-in load, before the window can write window.txt: decide once.
        /// No card to show means this version counts as seen now.
        /// </summary>
        public static void Prepare()
        {
            try
            {
                var current = ForskUpdate.Current;
                var seen = Seen();
                if (seen == current) return;
                var notes = ForskWhatsNew.Parse(Read());
                if (ForskWhatsNew.Show(notes, current, seen, File.Exists(WindowTxt()))) _pending = notes;
                else MarkSeen(current);
            }
            catch (Exception)
            {
                // No card. Forsk works as before.
            }
        }

        /// <summary>The card to add now, once. The version counts as seen when it is taken.</summary>
        public static WhatsNewNotes Take()
        {
            var notes = _pending;
            if (notes == null) return null;
            _pending = null;
            MarkSeen(notes.Version);
            return notes;
        }

        static string Read()
        {
            using (var stream = typeof(ForskWhatsNew).Assembly.GetManifestResourceStream(ForskWhatsNew.Resource))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        static string WindowTxt()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".forsk", "window.txt");
        }

        static string Seen()
        {
            try { return global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.GetString(SeenKey, "") ?? ""; }
            catch (Exception) { return ""; }
        }

        static void MarkSeen(string version)
        {
            try { global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.SetString(SeenKey, version); }
            catch (Exception) { /* settings unavailable: the card may show once more */ }
        }
    }
}
