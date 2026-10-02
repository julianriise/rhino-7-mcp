using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Eto.Forms;
using Rhino;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>Copies the debug report. The menu and the ForskDebug command share this.</summary>
    sealed partial class ForskWindow
    {
        /// <summary>True when the clipboard or the Desktop file received the report.</summary>
        public static bool CopyDebugReport(RhinoDoc doc)
        {
            string text;
            try
            {
                text = BuildDebugReport(doc);
            }
            catch (Exception e)
            {
                text = ForskDebug.Redact("Debug report failed.\n" + e.GetType().Name + ": " + e.Message + "\n");
            }

            string path = null;
            try
            {
                var desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (string.IsNullOrEmpty(desk))
                    desk = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = Path.Combine(desk, ForskDebug.FileName);
                File.WriteAllText(path, text);
            }
            catch (Exception e)
            {
                path = null;
                Log("debug report file · " + e.GetType().Name);
            }

            var copied = false;
            try
            {
                Clipboard.Instance.Text = text;
                copied = true;
            }
            catch (Exception e)
            {
                Log("debug report clipboard · " + e.GetType().Name);
            }

            var where = new List<string>();
            if (copied) where.Add("the clipboard");
            if (path != null) where.Add(path);
            var line = where.Count == 0
                ? "The debug report could not be copied."
                : "Copied the debug report to " + string.Join(" and ", where) + ".";
            if (doc != null)
            {
                var thread = Models.For(doc.RuntimeSerialNumber, FileName(doc), doc.Path);
                thread.AddLine(line);
                Models.Persist(thread);
            }
            try { RhinoApp.WriteLine(line); }
            catch (Exception) { /* The command history is the plugin log. A missing one still leaves the file. */ }
            _open?.Render();
            return copied || path != null;
        }

        /// <summary>The redacted debug report. Support reads it. Nothing is written to the model.</summary>
        public static string DebugReportText(RhinoDoc doc)
        {
            return BuildDebugReport(doc);
        }

        static string BuildDebugReport(RhinoDoc doc)
        {
            var snap = new DebugSnapshot();
            var info = typeof(ForskDebug).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            snap.Commit = ForskDebug.CommitOf(info);
            try { snap.Rhino = RhinoApp.Version == null ? "unknown" : RhinoApp.Version.ToString(); }
            catch (Exception) { snap.Rhino = "unknown"; }
            if (doc != null)
            {
                snap.File = FileName(doc);
                snap.Units = doc.ModelUnitSystem == UnitSystem.Millimeters ? "millimetres" : doc.ModelUnitSystem.ToString();
                RhinoMCPFunctions.FillDebugModel(doc, snap);
                var thread = Models.For(doc.RuntimeSerialNumber, snap.File, doc.Path);
                foreach (var turn in ForskDebug.LastTurns(thread.Items, thread.History))
                    snap.Turns.Add(turn);
            }
            snap.WindowLog = ForskDebug.Tail(ReadText(LogPath), ForskDebug.LogLines);
            var plugin = "";
            try { plugin = RhinoApp.CommandHistoryWindowText ?? ""; }
            catch (Exception) { plugin = ""; }
            snap.PluginLog = ForskDebug.Tail(plugin, ForskDebug.LogLines);
            return ForskDebug.Publish(snap);
        }

        static string ReadText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
