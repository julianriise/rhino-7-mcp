using System;
using System.Globalization;
using System.IO;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The raster source of plan_import (F7): a scanned or photographed plan (a
/// PNG, a JPEG, or a PDF page with no vector walls) read by the CubiCasa5k
/// model, tools/cubicasa in the forsk checkout, run with uv (ForskUv) as
/// cubicasa-plan. The model is licensed CC BY-NC 4.0, non-commercial use
/// only, and every receipt of a plan it read says so; forsk's own MIT model
/// replaces it later. A raster has no known scale: the plan comes at 1:100 at
/// the input's dpi, marked assumed, and the user sets the scale. No
/// RhinoCommon, so it tests headless.
/// </summary>
public static class PlanRaster
{
    public const string Model = "cubicasa5k";
    public const string Licence = "CC BY-NC 4.0 — non-commercial use only";
    /// <summary>cubicasa-plan's exit codes: no plan found in the input, and the model weights missing.</summary>
    public const int NoPlan = 3;
    public const int NoWeights = 4;
    /// <summary>A cubicasa-plan to run in place of tools/cubicasa's: one installed elsewhere, or the tests' stub.</summary>
    public const string CommandVariable = "FORSK_CUBICASA_PLAN";
    // The first run lets uv install the model's environment.
    const int TimeoutMs = 300000;

    /// <summary>Why the raster source read nothing, and the user's next step. Its message ends "Nothing was imported." and the step.</summary>
    public sealed class Failure : InvalidOperationException
    {
        public Failure(string reason, string next)
            : base(reason + ". Nothing was imported." + (next == null ? "" : " " + next))
        {
        }
    }

    /// <summary>A file the raster source reads as an image: PNG or JPEG.</summary>
    public static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path ?? "").ToLowerInvariant();
        return extension == ".png" || extension == ".jpg" || extension == ".jpeg";
    }

    /// <summary>CubiCasa5k made the plan file, by the source it names.</summary>
    public static bool IsModel(string vendor)
    {
        return string.Equals(vendor, Model, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>tools/cubicasa: FORSK_HOME, else the forsk checkout beside rhino-7-mcp.</summary>
    public static string ToolDir() => ForskUv.ToolDir("cubicasa");

    /// <summary>The command that fetches the model weights, as the user types it.</summary>
    public static string FetchCommand()
    {
        var set = Environment.GetEnvironmentVariable(CommandVariable);
        if (!string.IsNullOrWhiteSpace(set)) return ForskUv.Quote(set) + " --fetch-weights";
        return "uv run --frozen --project " + ForskUv.Quote(ToolDir() ?? "tools/cubicasa") + " cubicasa-plan --fetch-weights";
    }

    /// <summary>
    /// The input (an image, or page, 1-based, of a PDF) into outDir: the plan
    /// file, and the raster the model read with its top-left on the plan's
    /// origin. A PDF page is rasterised at PlanPdf.Dpi, the resolution
    /// pdf-vector renders at. Throws a Failure with the reason and the next step.
    /// </summary>
    public static PlanPdf.Extracted Detect(string input, int? page, string outDir)
    {
        if (page < 1) throw new ArgumentException("page is 1-based.");
        Command(out var exe, out var prefix, out var dir);
        Directory.CreateDirectory(outDir);
        var at = page == null ? "" : "-p" + page.Value.ToString(CultureInfo.InvariantCulture);
        var stem = Path.GetFileNameWithoutExtension(input) + at + "-raster";
        var detected = new PlanPdf.Extracted
        {
            PlanPath = Path.Combine(outDir, stem + ".json"),
            ImagePath = Path.Combine(outDir, stem + ".png")
        };
        foreach (var old in new[] { detected.PlanPath, detected.ImagePath })
            if (File.Exists(old)) File.Delete(old);

        var args = ForskUv.Quote(input)
            + (page == null ? "" : " --page " + page.Value.ToString(CultureInfo.InvariantCulture)
                + " --dpi " + PlanPdf.Dpi.ToString(CultureInfo.InvariantCulture))
            + " --out " + ForskUv.Quote(detected.PlanPath) + " --png " + ForskUv.Quote(detected.ImagePath);
        var ran = ForskUv.Run(exe, prefix + args, dir, "The raster source", TimeoutMs);
        if (ran.Code == NoPlan)
            throw new Failure("the raster source found no plan (" + ForskUv.LastLine(ran.Stderr) + ")",
                "Try a sharper scan of the plan alone, at 200 dpi or more, or a vector PDF or DXF of it.");
        if (ran.Code == NoWeights)
            throw new Failure("the raster source has no model weights",
                "Fetch them once with: " + FetchCommand() + ", then import again.");
        if (ran.Code != 0)
            throw new Failure("the raster source failed (" + ForskUv.LastLine(ran.Stderr) + ")", null);
        if (!File.Exists(detected.PlanPath) || !File.Exists(detected.ImagePath))
            throw new Failure("the raster source wrote no plan", null);
        return detected;
    }

    /// <summary>What to run: CommandVariable's cubicasa-plan as it is, else tools/cubicasa's through uv.</summary>
    static void Command(out string exe, out string prefix, out string dir)
    {
        var set = Environment.GetEnvironmentVariable(CommandVariable);
        if (!string.IsNullOrWhiteSpace(set))
        {
            if (!File.Exists(set))
                throw new Failure(CommandVariable + " names no file (" + set + ")", "Unset it, or point it at a cubicasa-plan.");
            exe = set;
            prefix = "";
            dir = Path.GetDirectoryName(set);
            return;
        }
        dir = ToolDir();
        if (dir == null)
            throw new Failure("reading a scan or image needs Forsk's tools/cubicasa, which is missing",
                "Reinstall Forsk from the Package Manager, or set FORSK_HOME to the forsk checkout.");
        exe = ForskUv.Uv();
        if (exe == null)
            throw new Failure("reading a scan or image needs " + ForskText.Get("setup.where"), null);
        prefix = ForskUv.RunArgs(dir, "cubicasa-plan") + " ";
    }
}
