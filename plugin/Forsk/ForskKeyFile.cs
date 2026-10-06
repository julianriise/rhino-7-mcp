using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The user's own Grok key in ~/.forsk/grok.env: one FORSK_GROK_API_KEY
    /// line among whatever else the file holds. A bare line (no "=") is the
    /// older form of the same key. The text edits are pure and the file is
    /// written owner-only (600). No RhinoCommon, so it tests headless.
    /// </summary>
    public static class ForskKeyFile
    {
        public const string Name = "FORSK_GROK_API_KEY";
        /// <summary>The Settings menu row and the card's kind.</summary>
        public const string MenuId = "grok.key";
        public const string FieldKey = "key";

        /// <summary>~/.forsk/grok.env</summary>
        public static string DefaultPath
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, ".forsk", "grok.env");
            }
        }

        /// <summary>The key in the text: the FORSK_GROK_API_KEY value, else the first bare line. Null when there is none.</summary>
        public static string Read(string text)
        {
            string bare = null;
            foreach (var raw in Lines(text))
            {
                var line = Strip(raw);
                if (line == null) continue;
                if (line.StartsWith(Name + "=", StringComparison.Ordinal))
                {
                    var value = line.Substring(Name.Length + 1).Trim().Trim('"').Trim('\'');
                    if (value.Length > 0) return value;
                }
                else if (bare == null && line.IndexOf('=') < 0)
                    bare = line;
            }
            return bare;
        }

        /// <summary>The text with the key on one FORSK_GROK_API_KEY line, where the first key line was, else at the end. The other lines stay.</summary>
        public static string Set(string text, string key)
        {
            var lines = Lines(text);
            var at = lines.FindIndex(IsKeyLine);
            lines.RemoveAll(IsKeyLine);
            lines.Insert(at < 0 ? lines.Count : at, Name + "=" + key);
            return Join(lines);
        }

        /// <summary>The text without its key lines. The other lines stay.</summary>
        public static string Unset(string text)
        {
            var lines = Lines(text);
            lines.RemoveAll(IsKeyLine);
            return Join(lines);
        }

        /// <summary>The last four characters: all the card ever shows of a key.</summary>
        public static string Tail(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return key.Length <= 4 ? key : key.Substring(key.Length - 4);
        }

        /// <summary>The pasted key, trimmed. Null with a one-line reason when it is empty or has a space inside.</summary>
        public static string Clean(string typed, out string reason)
        {
            var key = (typed ?? "").Trim();
            reason = null;
            if (key.Length == 0) reason = ForskText.Get("grok.key.empty");
            else if (key.Any(char.IsWhiteSpace)) reason = ForskText.Get("grok.key.spaces");
            return reason == null ? key : null;
        }

        /// <summary>The key the file holds, or null when there is no file or no key.</summary>
        public static string Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return Read(File.ReadAllText(path));
        }

        /// <summary>True when the file holds a key that Remove would take out.</summary>
        public static bool Stored(string path)
        {
            return !string.IsNullOrEmpty(Load(path));
        }

        /// <summary>Write the key into the file, creating ~/.forsk and the file when missing.</summary>
        public static void Save(string path, string key)
        {
            Write(path, Set(File.Exists(path) ? File.ReadAllText(path) : "", key));
        }

        /// <summary>Take the key out of the file. No file: nothing to do.</summary>
        public static void Remove(string path)
        {
            if (!File.Exists(path)) return;
            Write(path, Unset(File.ReadAllText(path)));
        }

        /// <summary>A comment, a blank line, or the line without "export ". Null for the first two.</summary>
        static string Strip(string raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) return null;
            return line.StartsWith("export ", StringComparison.Ordinal) ? line.Substring(7).Trim() : line;
        }

        static bool IsKeyLine(string raw)
        {
            var line = Strip(raw);
            return line != null && (line.StartsWith(Name + "=", StringComparison.Ordinal) || line.IndexOf('=') < 0);
        }

        static List<string> Lines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<string>();
            var lines = text.Split('\n').ToList();
            if (lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        static string Join(List<string> lines)
        {
            return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
        }

        /// <summary>
        /// A new file beside the old one, owner-only before the key goes in,
        /// then moved over it: the key is never in a file others can read.
        /// The account token (ForskAccount) is written the same way.
        /// </summary>
        internal static void Write(string path, string text)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
            try
            {
                File.WriteAllText(tmp, "");
                OwnerOnly(tmp);
                File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
        static extern int Chmod(string path, int mode);

        /// <summary>Mode 600 on macOS and Linux (net48 has no file-mode API, so libc's chmod). Windows has no mode to set.</summary>
        static void OwnerOnly(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX) return;
            if (Chmod(path, 0x180) != 0)
                throw new IOException("Could not make " + Path.GetFileName(path) + " private (chmod " + Marshal.GetLastWin32Error() + ").");
        }
    }
}
