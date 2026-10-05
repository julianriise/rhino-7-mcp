using System;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// The daylight grid as a setting on this Mac: low, medium or high. Low is
    /// the tracer's 400 mm grid; each step up splits every tile into four.
    /// No RhinoCommon, so it tests headless.
    /// </summary>
    public static class DaylightQuality
    {
        public const string Setting = "daylight_quality";
        public const string Low = "low";
        public const string Medium = "medium";
        public const string High = "high";

        public static readonly string[] All = { Low, Medium, High };

        /// <summary>An unknown or empty value is low.</summary>
        public static string Normal(string raw)
        {
            var value = (raw ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(All, value) >= 0 ? value : Low;
        }

        public static double CellMm(string quality)
        {
            switch (Normal(quality))
            {
                case High: return 100.0;
                case Medium: return 200.0;
                default: return 400.0;
            }
        }
    }
}
