using System;
using System.Collections.Generic;
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

    /// <summary>The release package's folder beside the plugin assembly: prompts, tools and the daylight tracer.</summary>
    public static string Bundled()
    {
        var dir = Path.GetDirectoryName(typeof(ForskUv).Assembly.Location);
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "forsk");
    }

    /// <summary>
    /// Where Forsk's own files are looked for, in order: the folder the
    /// variable names, the package beside the plugin, then the developer's
    /// checkout ~/Documents/hobby/forsk.
    /// </summary>
    public static IEnumerable<string> Roots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Environment.GetEnvironmentVariable("FORSK_HOME"), Bundled(), Path.Combine(home, "Documents", "hobby", "forsk") })
            if (!string.IsNullOrWhiteSpace(root)) yield return root;
    }

    /// <summary>tools/name under the first of Roots that has it. Null when none has it.</summary>
    public static string ToolDir(string name)
    {
        foreach (var root in Roots())
        {
            var dir = Path.Combine(root, "tools", name);
            if (File.Exists(Path.Combine(dir, "pyproject.toml"))) return dir;
        }
        return null;
    }

    /// <summary>uv's arguments to run a module with numpy in a throwaway environment: the daylight tracer without a venv.</summary>
    public static string RunModuleArgs(string module) => "run --no-project --quiet --with numpy python -m " + module;

    /// <summary>~/.forsk/bin: where Settings → Set up Forsk puts uv.</summary>
    public static string ForskBin()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".forsk", "bin");
    }

    /// <summary>
    /// uv: UV when set, else the copy Set up Forsk installed, else PATH, else
    /// where uv's own installers put it. Rhino's PATH has no Homebrew. Looked
    /// up on every call, so an install shows without a restart.
    /// </summary>
    public static string Uv()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Find(Environment.GetEnvironmentVariable("UV"), home, Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>Uv's lookup from its inputs: the UV variable, the home folder and PATH.</summary>
    public static string Find(string set, string home, string path)
    {
        if (!string.IsNullOrWhiteSpace(set) && File.Exists(set)) return set;
        var dirs = new List<string> { Path.Combine(home, ".forsk", "bin") };
        dirs.AddRange((path ?? "").Split(Path.PathSeparator));
        dirs.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", Path.Combine(home, ".local", "bin"), Path.Combine(home, ".cargo", "bin") });
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var uv = Path.Combine(dir, "uv");
            if (File.Exists(uv)) return uv;
        }
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
    /// name in those messages. env adds to the child's environment.
    /// </summary>
    public static Ran Run(string exe, string args, string dir, string what, int timeoutMs, IDictionary<string, string> env = null)
    {
        var start = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var pair in env ?? new Dictionary<string, string>())
            start.EnvironmentVariables[pair.Key] = pair.Value;
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
