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

        /// <summary>call runs one bridge command and returns its envelope. quality overrides the saved one (a live run is Low).</summary>
        public static JObject Run(string target, Func<string, JObject, JObject> call, string quality = null)
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
                    ["cell_size"] = DaylightQuality.CellMm(quality ?? Quality)
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
            // AN.1: the menu's last result and each room's mean, for the live line.
            RhinoMCPFunctions.NoteDaylight(RhinoDoc.ActiveDoc, traced);
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
            // A checkout's server venv, else uv with numpy (the release package has no venv).
            var python = Venv(src);
            var exe = python;
            var args = "-m forsk_daylight";
            if (!File.Exists(python))
            {
                exe = ForskUv.Uv() ?? throw new InvalidOperationException("Daylight needs " + ForskText.Get("setup.where") + ".");
                args = ForskUv.RunModuleArgs("forsk_daylight");
            }

            var start = new ProcessStartInfo(exe, args)
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

        /// <summary>
        /// The tracer would run through uv and there is none: the window answers
        /// with the Set up Forsk card instead of starting. A missing tracer is
        /// not this: Trace says so itself.
        /// </summary>
        public static bool NeedsSetup()
        {
            string src;
            try { src = ServerSource(); }
            catch (InvalidOperationException) { return false; }
            return !File.Exists(Venv(src)) && ForskUv.Uv() == null;
        }

        /// <summary>A checkout's server venv python beside the tracer's folder.</summary>
        static string Venv(string src) => Path.Combine(src, "..", ".venv", "bin", "python");

        /// <summary>The tracer's folder: RHINO_MCP_HOME's server/src, the package's daylight folder, else the checkout's server/src.</summary>
        static string ServerSource()
        {
            var env = Environment.GetEnvironmentVariable("RHINO_MCP_HOME");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var bundled = ForskUv.Bundled();
            foreach (var src in new[]
            {
                string.IsNullOrWhiteSpace(env) ? null : Path.Combine(env, "server", "src"),
                bundled == null ? null : Path.Combine(bundled, "daylight"),
                Path.Combine(home, "Documents", "hobby", "rhino-7-mcp", "server", "src")
            })
            {
                if (src != null && File.Exists(Path.Combine(src, "forsk_daylight.py"))) return src;
            }
            throw new InvalidOperationException("Daylight's tracer is missing. Reinstall Forsk from the Package Manager.");
        }

        static string LastLine(string text)
        {
            var lines = (text ?? "").Trim().Split('\n');
            var last = lines[lines.Length - 1].Trim();
            return last.Length == 0 ? "no output" : last;
        }
    }
}
