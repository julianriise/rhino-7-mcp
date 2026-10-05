using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Rhino;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Daylight from the panel, chip and chat. daylight_scene reads the model,
    /// the server's own tracer (python -m forsk_daylight in server/.venv) scores it
    /// in a child process over stdin and stdout, and daylight_paint draws it.
    /// No tracer in C#, and no MCP server needed.
    /// </summary>
    public static class ForskDaylight
    {
        public const string ToolName = ForskToolPacks.DaylightTool;
        const int TimeoutMs = 180000;

        /// <summary>The daylight grid saved on this Mac (plug-in settings). Low when unset.</summary>
        public static string Quality
        {
            get
            {
                try { return DaylightQuality.Normal(global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.GetString(DaylightQuality.Setting, DaylightQuality.Low)); }
                catch (Exception) { return DaylightQuality.Low; }
            }
            set
            {
                try { global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings.SetString(DaylightQuality.Setting, DaylightQuality.Normal(value)); }
                catch (Exception) { /* settings unavailable: the run stays at low */ }
            }
        }

        /// <summary>call runs one bridge command and returns its envelope.</summary>
        public static JObject Run(string target, Func<string, JObject, JObject> call)
        {
            var scene = call("daylight_scene", new JObject());
            if (!Ok(scene)) return scene;

            JObject traced;
            try
            {
                traced = Trace(new JObject
                {
                    ["scene"] = scene["result"],
                    ["target"] = string.IsNullOrEmpty(target) ? "floor" : target,
                    ["cell_size"] = DaylightQuality.CellMm(Quality)
                });
            }
            catch (Exception e)
            {
                return ForskTools.Fail(e.Message);
            }
            if (traced["success"]?.Type != JTokenType.Boolean || !traced["success"].Value<bool>())
                return ForskTools.Fail(traced["message"]?.ToString() ?? "Daylight failed.");

            var paint = traced["paint"] as JObject;
            traced.Remove("paint");
            var painted = call("daylight_paint", paint);
            if (!Ok(painted)) return painted;
            var result = painted["result"] as JObject ?? new JObject();
            traced["id"] = result["id"];
            traced["deleted"] = result["deleted"];
            traced["layer"] = result["layer"];
            return new JObject { ["status"] = "success", ["result"] = traced };
        }

        /// <summary>Already on the UI thread. Hides or shows the mesh and records one undo.</summary>
        public static JObject ApplyVisible(bool visible)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return ForskTools.Fail("No active document.");
            // Inside a pill's record, the hide or show is part of it.
            var own = !doc.UndoRecordingIsActive;
            var record = own ? doc.BeginUndoRecord(visible ? "Forsk: show daylight" : "Forsk: hide daylight") : 0;
            ForskCalls.Enter();
            try
            {
                return new JObject
                {
                    ["status"] = "success",
                    ["result"] = RhinoMCPFunctions.SetAnalysisVisible(doc, visible)
                };
            }
            catch (Exception e)
            {
                return ForskTools.Fail(e.Message);
            }
            finally
            {
                ForskCalls.Exit();
                if (own) doc.EndUndoRecord(record);
            }
        }

        /// <summary>From a background thread. The panel chip calls this.</summary>
        public static JObject SetVisible(bool visible)
        {
            JObject envelope = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                envelope = ApplyVisible(visible);
            }));
            return envelope ?? ForskTools.Fail("No result");
        }

        public const string NoRooms =
            "No closed rooms between the walls. Put a door in each gap, or draw closed room outlines on A-ROOM.";

        /// <summary>
        /// The Daylight chip's click, off the UI thread. line is the receipt row;
        /// note is the assistant line under it (the disclaimer, or how to get rooms).
        /// Make rooms is rooms_detect: rooms from the walls, outlines on A-ROOM kept.
        /// </summary>
        public static void Chip(DaylightAction action, out string line, out string note)
        {
            note = "";
            if (action == DaylightAction.MakeRooms)
            {
                var rooms = MakeRooms(ForskTools.CommandOnUi);
                line = RoomsLine(rooms);
                if (Ok(rooms) && (rooms["result"]?["count"]?.Value<int>() ?? 0) == 0)
                    note = NoRooms;
                return;
            }
            if (action == DaylightAction.Hide)
            {
                line = Line("Hide daylight map", SetVisible(false));
                return;
            }
            if (action == DaylightAction.Show)
            {
                line = Line("Show daylight map", SetVisible(true));
                return;
            }
            if (action == DaylightAction.Run)
            {
                var envelope = Run("floor", ForskTools.CommandOnUi);
                line = Line("Daylight", envelope);
                note = Disclaimer(envelope);
                return;
            }
            line = BakeChip.NeedsWindows + " · " + BakeChip.NeedsWindowsHint;
        }

        public static JObject MakeRooms(Func<string, JObject, JObject> call)
        {
            return call("rooms_detect", new JObject());
        }

        /// <summary>Chip receipt: Make rooms · ok · 15 rooms, 412.3 m². 1 open: gap 0.9 m without a door.</summary>
        public static string RoomsLine(JObject envelope)
        {
            if (!Ok(envelope))
                return "Make rooms · error · " + ForskTools.Clip(envelope?["message"]?.ToString() ?? "failed");
            return "Make rooms · ok · " + ForskTools.Clip(envelope["result"]?["message"]?.ToString() ?? "");
        }

        /// <summary>Chip receipt: Daylight · ok · 1 space · 1 window · 0.4 s.</summary>
        public static string Line(string label, JObject envelope)
        {
            if (!Ok(envelope))
                return label + " · error · " + ForskTools.Clip(envelope?["message"]?.ToString() ?? "failed");
            var r = envelope["result"] as JObject ?? new JObject();
            if (r["spaces"] == null)
                return label + " · ok · " + ForskTools.Clip(r["message"]?.ToString() ?? "");
            var spaces = r["spaces"].Value<int>();
            var windows = r["windows"]?.Value<int>() ?? 0;
            var seconds = (r["compute_ms"]?.Value<double>() ?? 0) / 1000.0;
            return label + " · ok · " + Count(spaces, "space") + " · " + Count(windows, "window")
                + " · " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        public static string Disclaimer(JObject envelope)
        {
            return Ok(envelope) ? envelope["result"]?["disclaimer"]?.ToString() ?? "" : "";
        }

        static string Count(int n, string noun)
        {
            return n + " " + noun + (n == 1 ? "" : "s");
        }

        static bool Ok(JObject envelope)
        {
            return string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
        }

        static JObject Trace(JObject input)
        {
            var src = ServerSource();
            var python = Path.Combine(src, "..", ".venv", "bin", "python");
            if (!File.Exists(python))
                throw new InvalidOperationException("Daylight needs the server venv at " + Path.GetFullPath(python) + ". See INSTALL.md.");

            var start = new ProcessStartInfo(python, "-m forsk_daylight")
            {
                WorkingDirectory = src,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.EnvironmentVariables["PYTHONPATH"] = src;

            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Could not start the daylight tracer.");
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                process.StandardInput.Write(input.ToString(Newtonsoft.Json.Formatting.None));
                process.StandardInput.Close();
                if (!process.WaitForExit(TimeoutMs))
                {
                    try { process.Kill(); } catch { /* already gone */ }
                    throw new TimeoutException("Daylight tracer took over " + TimeoutMs / 1000 + " s.");
                }
                Task.WaitAll(stdout, stderr);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Daylight tracer failed: " + LastLine(stderr.Result));
                return JObject.Parse(stdout.Result);
            }
        }

        /// <summary>rhino-7-mcp/server/src: RHINO_MCP_HOME, else the sibling of the Forsk checkout.</summary>
        static string ServerSource()
        {
            var env = Environment.GetEnvironmentVariable("RHINO_MCP_HOME");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var root in new[] { env, Path.Combine(home, "Documents", "hobby", "rhino-7-mcp") })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var src = Path.Combine(root, "server", "src");
                if (File.Exists(Path.Combine(src, "forsk_daylight.py"))) return src;
            }
            throw new InvalidOperationException("Daylight needs the rhino-7-mcp checkout. Set RHINO_MCP_HOME to it.");
        }

        static string LastLine(string text)
        {
            var lines = (text ?? "").Trim().Split('\n');
            var last = lines[lines.Length - 1].Trim();
            return last.Length == 0 ? "no output" : last;
        }
    }
}
