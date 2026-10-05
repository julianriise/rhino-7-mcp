using System;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Plug-in settings for the hint. Dismissed or printed, on this Mac, hides
    /// it on the next empty file. Values are "1".
    /// </summary>
    public static class FirstRunGate
    {
        public const string DismissedKey = "forsk.guide.dismissed";
        public const string PrintedKey = "forsk.printed";

        public static bool Off()
        {
            try
            {
                var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
                if (settings == null) return false;
                return settings.GetString(DismissedKey, "") == "1"
                    || settings.GetString(PrintedKey, "") == "1";
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Dismiss()
        {
            try { global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.SetString(DismissedKey, "1"); }
            catch (Exception) { }
        }

        public static void Note(string line)
        {
            if (!FirstRun.CountsAsPrinted(line)) return;
            try { global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.SetString(PrintedKey, "1"); }
            catch (Exception) { }
        }
    }
}
