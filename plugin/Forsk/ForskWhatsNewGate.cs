using System;
using System.Collections.Generic;
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

        static List<WhatsNewNotes> _pending;

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
                var due = ForskWhatsNew.Due(ForskWhatsNew.Parse(Read()), current, seen, File.Exists(WindowTxt()));
                if (due.Count > 0) _pending = due;
                else MarkSeen(current);
            }
            catch (Exception)
            {
                // No card. Forsk works as before.
            }
        }

        /// <summary>Settings → Release notes: every release in the file, newest first, whenever asked. Null when they don't read.</summary>
        public static List<WhatsNewNotes> Notes()
        {
            try { return ForskWhatsNew.Parse(Read()); }
            catch (Exception) { return null; }
        }

        /// <summary>The releases to show now, once. The running version counts as seen when they are taken.</summary>
        public static List<WhatsNewNotes> Take()
        {
            var due = _pending;
            if (due == null) return null;
            _pending = null;
            MarkSeen(ForskUpdate.Current);
            return due;
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

        /// <summary>~/.forsk/whats_new_seen: kept by a Rhino that quits without saving its plug-in settings too.</summary>
        static string SeenFile()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".forsk", "whats_new_seen");
        }

        static string Seen()
        {
            try
            {
                if (File.Exists(SeenFile())) return File.ReadAllText(SeenFile()).Trim();
            }
            catch (Exception) { /* unreadable: the plug-in settings answer */ }
            try { return global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.GetString(SeenKey, "") ?? ""; }
            catch (Exception) { return ""; }
        }

        static void MarkSeen(string version)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SeenFile()));
                File.WriteAllText(SeenFile(), version ?? "");
            }
            catch (Exception) { /* no home folder: the plug-in settings keep it */ }
            try { global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.SetString(SeenKey, version); }
            catch (Exception) { /* settings unavailable: the card may show once more */ }
        }
    }
}
