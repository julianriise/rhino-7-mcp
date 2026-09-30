using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// A vector PDF as a plan_import source (F7.7). Forsk's extractor
/// (tools/pdf_vector in the forsk checkout: pdfplumber and pypdfium2, MIT and
/// Apache) reads one page into forsk.plan_import.v0 and renders that page in
/// the same frame. It runs in a child process with uv from its own lock, so
/// there is one copy of it, nothing of it in the plugin, and nothing added to
/// the server's environment. No RhinoCommon, so it tests headless.
/// </summary>
public static class PlanPdf
{
    /// <summary>The page raster's resolution: 12.7 mm a pixel at 1:100.</summary>
    public const int Dpi = 200;
    // The first run lets uv install the extractor's environment.
    const int TimeoutMs = 300000;

    public sealed class Extracted
    {
        public string PlanPath;
        public string ImagePath;
    }

    /// <summary>tools/pdf_vector: FORSK_HOME, else the forsk checkout beside rhino-7-mcp.</summary>
    public static string ExtractorDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Environment.GetEnvironmentVariable("FORSK_HOME"), Path.Combine(home, "Documents", "hobby", "forsk") })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var dir = Path.Combine(root, "tools", "pdf_vector");
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

    /// <summary>Pages in the PDF.</summary>
    public static int Pages(string pdf)
    {
        var output = Run(pdf, "--pages");
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("pages ", StringComparison.Ordinal)
                && int.TryParse(text.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages))
                return pages;
        }
        throw new InvalidOperationException("The PDF extractor did not say how many pages " + Path.GetFileName(pdf) + " has.");
    }

    /// <summary>
    /// One page (1-based) of the PDF into outDir: the plan file, and the page
    /// at Dpi with its top-left on the plan's origin. The plan file states the
    /// image's size in its own mm, at the scale read off the sheet.
    /// </summary>
    public static Extracted Extract(string pdf, int page, string outDir)
    {
        if (page < 1) throw new ArgumentException("page is 1-based.");
        Directory.CreateDirectory(outDir);
        var stem = Path.GetFileNameWithoutExtension(pdf) + "-p" + page.ToString(CultureInfo.InvariantCulture);
        var extracted = new Extracted
        {
            PlanPath = Path.Combine(outDir, stem + ".json"),
            ImagePath = Path.Combine(outDir, stem + ".png")
        };
        foreach (var old in new[] { extracted.PlanPath, extracted.ImagePath })
            if (File.Exists(old)) File.Delete(old);
        Run(pdf, "--page " + page.ToString(CultureInfo.InvariantCulture)
            + " --out " + Quote(extracted.PlanPath) + " --png " + Quote(extracted.ImagePath)
            + " --dpi " + Dpi.ToString(CultureInfo.InvariantCulture));
        if (!File.Exists(extracted.PlanPath) || !File.Exists(extracted.ImagePath))
            throw new InvalidOperationException("The PDF extractor wrote no plan for page " + page + " of " + Path.GetFileName(pdf) + ".");
        return extracted;
    }

    /// <summary>Where a PDF's plan file and page image go: they stay beside each other, and the image is embedded in the .3dm.</summary>
    public static string WorkDir()
    {
        return Path.Combine(Path.GetTempPath(), "forsk-pdf-import");
    }

    /// <summary>pdf-vector on the PDF with args; its stdout. Its last stderr line on failure.</summary>
    static string Run(string pdf, string args)
    {
        if (string.IsNullOrWhiteSpace(pdf) || !File.Exists(pdf))
            throw new ArgumentException("pdf_path must be an existing PDF.");
        var dir = ExtractorDir();
        if (dir == null)
            throw new InvalidOperationException(
                "Importing a PDF needs the forsk checkout's tools/pdf_vector. Set FORSK_HOME to the forsk checkout.");
        var uv = Uv();
        if (uv == null)
            throw new InvalidOperationException(
                "Importing a PDF needs uv (docs.astral.sh/uv) to run tools/pdf_vector. Install uv, or set UV to its path.");

        var start = new ProcessStartInfo(uv, "run --frozen --quiet --project " + Quote(dir) + " pdf-vector " + Quote(pdf) + " " + args)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using (var process = Process.Start(start))
        {
            if (process == null) throw new InvalidOperationException("Could not start the PDF extractor.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(); } catch { /* already gone */ }
                throw new TimeoutException("The PDF extractor took over " + TimeoutMs / 1000 + " s.");
            }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The PDF extractor failed: " + LastLine(stderr.Result));
            return stdout.Result;
        }
    }

    static string Quote(string path)
    {
        return "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static string LastLine(string text)
    {
        var lines = (text ?? "").Trim().Split('\n');
        var last = lines[lines.Length - 1].Trim();
        return last.Length == 0 ? "no output" : last;
    }
}
