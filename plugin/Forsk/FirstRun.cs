using System;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The empty-file hint: three lines, until the user dismisses it or a
    /// print succeeds. Not a thread line, so "This file is empty." stays.
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
            return line.Contains("Printed ") && line.Contains(" on A3");
        }

        public static JObject Guide()
        {
            return new JObject
            {
                ["lines"] = new JArray(Lines),
                ["dismiss"] = DismissId
            };
        }
    }
}
