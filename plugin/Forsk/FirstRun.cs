using System;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The empty-file hint: three lines, and a fourth while chat has no key,
    /// until the user dismisses it or a print succeeds. Not a thread line, so
    /// "This file is empty." stays.
    /// </summary>
    public static class FirstRun
    {
        public const string DismissId = "guide.dismiss";

        public static readonly string[] Lines = { "Open sample house", "Generate 3D", "Print" };

        public static bool Show(FileKind kind, bool off) => kind == FileKind.Empty && !off;

        /// <summary>A successful Print PDF line. Cancel and error do not count.</summary>
        public static bool CountsAsPrinted(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (line.IndexOf("cancelled", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return line.Contains("Printed ") && PrintTemplate.Papers.Any(p => line.Contains(" on " + p.Name));
        }

        /// <summary>The three lines, then "Add your Grok key" when chat has no key: the steps run without one.</summary>
        public static JObject Guide(bool keyPresent)
        {
            var lines = new JArray(Lines);
            if (!keyPresent) lines.Add(ForskText.Get("grok.key.guide"));
            return new JObject
            {
                ["lines"] = lines,
                ["dismiss"] = DismissId
            };
        }
    }
}
