using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Forsk's Python tools (tools/NAME in the forsk checkout) run in a child
/// process with uv from their own lock, so there is one copy of each, nothing
/// of them in the plugin, and nothing added to the server's environment. No
/// RhinoCommon, so it tests headless.
/// </summary>
public static class ForskUv
{
    public sealed class Ran
    {
        public int Code;
        public string Stdout;
        public string Stderr;
    }

    /// <summary>tools/name: FORSK_HOME, else the forsk checkout beside rhino-7-mcp. Null when neither has it.</summary>
    public static string ToolDir(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Environment.GetEnvironmentVariable("FORSK_HOME"), Path.Combine(home, "Documents", "hobby", "forsk") })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var dir = Path.Combine(root, "tools", name);
            if (File.Exists(Path.Combine(dir, "pyproject.toml"))) return dir;
        }
        return null;
    }

    /// <summary>uv: UV when set, else PATH, else where its installers put it. Rhino's PATH has no Homebrew.</summary>
    public static string Uv()
    {
        var set = Environment.GetEnvironmentVariable("UV");
        if (!string.IsNullOrWhiteSpace(set) && File.Exists(set)) return set;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var path = Path.Combine(dir, "uv");
            if (File.Exists(path)) return path;
        }
        foreach (var path in new[]
        {
            "/opt/homebrew/bin/uv", "/usr/local/bin/uv",
            Path.Combine(home, ".local", "bin", "uv"), Path.Combine(home, ".cargo", "bin", "uv")
        })
            if (File.Exists(path)) return path;
        return null;
    }

    /// <summary>uv's arguments to run a tool's script from its own lock, quietly, so stderr is the tool's own.</summary>
    public static string RunArgs(string dir, string script)
    {
        return "run --frozen --quiet --project " + Quote(dir) + " " + script;
    }

    /// <summary>
    /// exe with args in dir: what it exited with and what it wrote. Throws
    /// only when it cannot start or runs over timeoutMs; what is the tool's
    /// name in those messages.
    /// </summary>
    public static Ran Run(string exe, string args, string dir, string what, int timeoutMs)
    {
        var start = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using (var process = Process.Start(start))
        {
            if (process == null) throw new InvalidOperationException("Could not start " + what + ".");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { /* already gone */ }
                throw new TimeoutException(what + " took over " + timeoutMs / 1000 + " s.");
            }
            Task.WaitAll(stdout, stderr);
            return new Ran { Code = process.ExitCode, Stdout = stdout.Result, Stderr = stderr.Result };
        }
    }

    public static string Quote(string path)
    {
        return "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>The last line a tool wrote: its diagnostic.</summary>
    public static string LastLine(string text)
    {
        var lines = (text ?? "").Trim().Split('\n');
        var last = lines[lines.Length - 1].Trim();
        return last.Length == 0 ? "no output" : last;
    }
}
