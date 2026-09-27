using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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
        public const string ToolName = "daylight_from_model";
        const int TimeoutMs = 120000;

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
                    ["target"] = string.IsNullOrEmpty(target) ? "floor" : target
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

        public static JObject Clear(Func<string, JObject, JObject> call)
        {
            return call("daylight_clear", new JObject());
        }

        public const string DrawRooms =
            "Daylight scores rooms. Draw closed room outlines on A-ROOM, then press Make rooms or say make rooms.";

        /// <summary>
        /// The Daylight chip's click, off the UI thread. line is the receipt row;
        /// note is the assistant line under it (the disclaimer, or how to get rooms).
        /// Make rooms is the chat offer: rooms_from_layer when A-ROOM holds closed
        /// curves, otherwise how to draw them.
        /// </summary>
        public static void Chip(DaylightAction action, bool roomCurves, out string line, out string note)
        {
            note = "";
            if (action == DaylightAction.MakeRooms)
            {
                if (!roomCurves)
                {
                    line = "Make rooms · no closed curves on A-ROOM";
                    note = DrawRooms;
                    return;
                }
                line = ForskTools.Receipt("rooms_from_layer", ForskTools.CommandOnUi("rooms_from_layer", new JObject()));
                return;
            }
            if (action == DaylightAction.Clear)
            {
                line = Line("Clear daylight", Clear(ForskTools.CommandOnUi));
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
