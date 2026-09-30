using System;
using System.Globalization;
using System.IO;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// A vector PDF as a plan_import source (F7.7). Forsk's extractor
/// (tools/pdf_vector in the forsk checkout: pdfplumber and pypdfium2, MIT and
/// Apache) reads one page into forsk.plan_import.v0 and renders that page in
/// the same frame. It runs with uv (ForskUv). No RhinoCommon, so it tests headless.
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
    public static string ExtractorDir() => ForskUv.ToolDir("pdf_vector");

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
            + " --out " + ForskUv.Quote(extracted.PlanPath) + " --png " + ForskUv.Quote(extracted.ImagePath)
            + " --dpi " + Dpi.ToString(CultureInfo.InvariantCulture));
        if (!File.Exists(extracted.PlanPath) || !File.Exists(extracted.ImagePath))
            throw new InvalidOperationException("The PDF extractor wrote no plan for page " + page + " of " + Path.GetFileName(pdf) + ".");
        return extracted;
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
        var uv = ForskUv.Uv();
        if (uv == null)
            throw new InvalidOperationException(
                "Importing a PDF needs uv (docs.astral.sh/uv) to run tools/pdf_vector. Install uv, or set UV to its path.");

        var ran = ForskUv.Run(uv, ForskUv.RunArgs(dir, "pdf-vector") + " " + ForskUv.Quote(pdf) + " " + args, dir, "The PDF extractor", TimeoutMs);
        if (ran.Code != 0)
            throw new InvalidOperationException("The PDF extractor failed: " + ForskUv.LastLine(ran.Stderr));
        return ran.Stdout;
    }
}
