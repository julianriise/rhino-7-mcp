using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The raster source (F7): an image, or a PDF page with no vector walls,
/// read through cubicasa-plan as the contract has it. A stub cubicasa-plan
/// stands in for the model: a shell script that records its arguments and
/// writes a fixed plan_import.v0, or exits 3 (no plan) or 4 (no weights).
/// The PDF runs need pdf-vector, as PlanPdfTests do.
/// </summary>
public class PlanRasterTests : IDisposable
{
    const string Scan = "no vector walls found; page looks like a raster scan";

    /// <summary>One room, 4 x 3 m, a window in the top wall and a door in the bottom one, as cubicasa-plan writes it.</summary>
    const string RoomPlan = @"{
  ""schema"": ""forsk.plan_import.v0"", ""units"": ""mm"", ""y_axis"": ""up"",
  ""source"": ""cubicasa5k"", ""licence"": ""CC BY-NC 4.0 — non-commercial use only"",
  ""scale"": { ""status"": ""assumed"", ""ratio"": ""1:100"" },
  ""image"": { ""width_mm"": 42011.6, ""height_mm"": 29705.3 },
  ""walls"": [
    { ""start"": [1000, -1000], ""end"": [5000, -1000], ""thickness"": 200 },
    { ""start"": [5000, -1000], ""end"": [5000, -4000], ""thickness"": 200 },
    { ""start"": [5000, -4000], ""end"": [1000, -4000], ""thickness"": 200 },
    { ""start"": [1000, -4000], ""end"": [1000, -1000], ""thickness"": 200 }
  ],
  ""openings"": [
    { ""kind"": ""window"", ""a"": [2000, -1000], ""b"": [3200, -1000] },
    { ""kind"": ""door"", ""a"": [2000, -4000], ""b"": [2900, -4000] }
  ],
  ""rooms"": [ { ""label"": ""Stue"", ""boundary"": [[1100, -1100], [4900, -1100], [4900, -3900], [1100, -3900]] } ]
}";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "forsk-plan-raster-tests", Guid.NewGuid().ToString("N"));
    readonly string _command = Environment.GetEnvironmentVariable(PlanRaster.CommandVariable);

    public PlanRasterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PlanRaster.CommandVariable, _command);
        Directory.Delete(_dir, true);
    }

    string Out => Path.Combine(_dir, "out");

    /// <summary>A stub cubicasa-plan running body after reading --out and --png, set as the one to run. Null on Windows.</summary>
    string Stub(string body, string plan = RoomPlan)
    {
        if (OperatingSystem.IsWindows()) return null;
        var dir = Path.Combine(_dir, "stub");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plan.json"), plan);
        var script = Path.Combine(dir, "cubicasa-plan");
        File.WriteAllText(script, "#!/bin/sh\n"
            + "here=$(dirname \"$0\")\n"
            + "printf '%s\\n' \"$@\" > \"$here/args.txt\"\n"
            + "while [ $# -gt 0 ]; do\n"
            + "  case \"$1\" in --out) out=\"$2\"; shift ;; --png) png=\"$2\"; shift ;; esac\n"
            + "  shift\n"
            + "done\n"
            + body + "\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable(PlanRaster.CommandVariable, script);
        return script;
    }

    const string Writes = "cp \"$here/plan.json\" \"$out\"\nprintf 'png' > \"$png\"";

    string[] Args() => File.ReadAllLines(Path.Combine(_dir, "stub", "args.txt"));

    string Image(string name = "plan.png")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4e, 0x47 });
        return path;
    }

    static string Receipt(PlanSource.Resolved resolved)
    {
        var status = PlanSource.Scale(resolved.Plan, null, out var ratio);
        return PlanSource.Receipt(resolved, PlanImport.Clean(resolved.Plan), status, ratio);
    }

    string ScanPdf()
    {
        var pdf = Path.Combine(_dir, "scan.pdf");
        File.WriteAllBytes(pdf, PlanPdfTests.Pdf("q 180 0 0 180 40 400 cm /Im1 Do Q\n", new byte[] { 0xff, 0x00, 0x00 }, 595.28, 841.89));
        return pdf;
    }

    [Fact]
    public void AnImage_GoesToTheRasterSource_AndTheReceiptCarriesTheAssumedScaleAndTheLicence()
    {
        if (Stub(Writes) == null) return;
        var image = Image();

        var resolved = PlanSource.Resolve(new JObject { ["image_path"] = image }, Out);

        Assert.Null(resolved.Pdf);
        Assert.Equal("plan.png", resolved.Label);
        Assert.Equal(4, resolved.Plan.Walls.Count);
        Assert.Equal(Path.Combine(Out, "plan-raster.json"), resolved.PlanPath);
        Assert.Equal(Path.Combine(Out, "plan-raster.png"), resolved.ImagePath);
        Assert.True(File.Exists(resolved.ImagePath));
        // The contract's call: INPUT --out plan.json --png plan.png. An image has no page, and keeps its own dpi.
        Assert.Equal(new[] { image, "--out", resolved.PlanPath, "--png", resolved.ImagePath }, Args());
        Assert.Equal("plan.png", resolved.Raster["file"]?.ToString());
        Assert.Equal("cubicasa5k", resolved.Raster["model"]?.ToString());
        Assert.Equal(PlanRaster.Licence, resolved.Raster["licence"]?.ToString());
        Assert.Null(resolved.Raster["page"]);
        Assert.Null(resolved.Raster["scan"]);

        Assert.Equal("unconfirmed", PlanSource.Scale(resolved.Plan, null, out var ratio));
        Assert.Equal("1:100", ratio);
        var receipt = Receipt(resolved);
        Assert.StartsWith("Imported 4 walls, 1 door, 1 window, 1 room.", receipt);
        Assert.Contains("Scale not detected (assumed 1:100): set it with two points and a known length.", receipt);
        Assert.EndsWith("Plan read by the CubiCasa5k model, licensed CC BY-NC 4.0 — non-commercial use only.", receipt);
    }

    [Fact]
    public void ARasterPlan_IsNeverDetectedToScale_AndStatesItsLicenceEvenWhenTheFileDoesNot()
    {
        var plan = RoomPlan.Replace(@"""status"": ""assumed""", @"""status"": ""detected""")
            .Replace(@"""licence"": ""CC BY-NC 4.0 — non-commercial use only"",", "");
        if (Stub(Writes, plan) == null) return;

        var resolved = PlanSource.Resolve(new JObject { ["image_path"] = Image("photo.jpeg") }, Out);

        Assert.Equal("unconfirmed", PlanSource.Scale(resolved.Plan, null, out _));
        Assert.Equal(PlanRaster.Licence, resolved.Licence);
        Assert.EndsWith("licensed CC BY-NC 4.0 — non-commercial use only.", Receipt(resolved));
    }

    [Fact]
    public void AScannedPdf_FallsThroughToTheRasterSource_AndTheReceiptSaysSo()
    {
        if (!PlanPdfTests.CanExtract || Stub(Writes) == null) return;
        var pdf = ScanPdf();

        var resolved = PlanSource.Resolve(new JObject { ["pdf_path"] = pdf }, Out);

        Assert.Null(resolved.Pdf);
        Assert.Equal("scan.pdf page 1", resolved.Label);
        Assert.Equal(Scan, resolved.Raster["scan"]?.ToString());
        Assert.Equal(1, resolved.Raster["page"]?.ToObject<int>());
        Assert.Equal(Path.Combine(Out, "scan-p1-raster.json"), resolved.PlanPath);
        // The PDF itself goes to the raster source, its page rasterised at pdf-vector's dpi.
        Assert.Equal(new[] { pdf, "--page", "1", "--dpi", "200", "--out", resolved.PlanPath, "--png", resolved.ImagePath }, Args());
        var receipt = Receipt(resolved);
        Assert.Contains("scan.pdf page 1: " + Scan + ", so the raster source read it as a scan.", receipt);
        Assert.Contains("Scale not detected (assumed 1:100)", receipt);
        Assert.EndsWith("Plan read by the CubiCasa5k model, licensed CC BY-NC 4.0 — non-commercial use only.", receipt);
    }

    [Fact]
    public void NoPlanFound_Exit3_SaysSoAndWhatToTryNext()
    {
        if (Stub("echo 'no walls found in the image' >&2\nexit 3") == null) return;
        var error = Assert.Throws<InvalidOperationException>(() => PlanSource.Resolve(new JObject { ["image_path"] = Image() }, Out));
        Assert.Equal("plan.png: the raster source found no plan (no walls found in the image). Nothing was imported. "
            + "Try a sharper scan of the plan alone, at 200 dpi or more, or a vector PDF or DXF of it.", error.Message);
    }

    [Fact]
    public void MissingWeights_Exit4_NamesTheFetchCommand()
    {
        var stub = Stub("echo 'model weights missing: run cubicasa-plan --fetch-weights' >&2\nexit 4");
        if (stub == null) return;
        var error = Assert.Throws<InvalidOperationException>(() => PlanSource.Resolve(new JObject { ["image_path"] = Image() }, Out));
        Assert.Equal("plan.png: the raster source has no model weights. Nothing was imported. "
            + "Fetch them once with: \"" + stub + "\" --fetch-weights, then import again.", error.Message);

        // Run from the forsk checkout, the command is uv's on tools/cubicasa.
        Environment.SetEnvironmentVariable(PlanRaster.CommandVariable, null);
        Assert.StartsWith("uv run --frozen --project \"", PlanRaster.FetchCommand());
        Assert.EndsWith("cubicasa\" cubicasa-plan --fetch-weights", PlanRaster.FetchCommand());
    }

    [Fact]
    public void AScannedPdf_WithNoWeights_SaysWhyItWasReadAsAScan_AndHowToFetchThem()
    {
        if (!PlanPdfTests.CanExtract) return;
        var stub = Stub("exit 4");
        if (stub == null) return;
        var error = Assert.Throws<InvalidOperationException>(() => PlanSource.Resolve(new JObject { ["pdf_path"] = ScanPdf() }, Out));
        Assert.Equal("scan.pdf page 1: " + Scan + ". Read as a scan, the raster source has no model weights. Nothing was imported. "
            + "Fetch them once with: \"" + stub + "\" --fetch-weights, then import again.", error.Message);
    }

    [Fact]
    public void AnImageWithItsPlanFile_IsImportedAsGiven_WithTheLicenceWhenCubiCasaMadeIt()
    {
        var planPath = Path.Combine(_dir, "plan.json");
        File.WriteAllText(planPath, RoomPlan.Replace(@"""licence"": ""CC BY-NC 4.0 — non-commercial use only"",", ""));
        var resolved = PlanSource.Resolve(new JObject { ["image_path"] = Image(), ["plan_path"] = planPath }, Out);
        Assert.Null(resolved.Raster);
        Assert.Equal("plan.json", resolved.Label);
        Assert.EndsWith("Plan read by the CubiCasa5k model, licensed CC BY-NC 4.0 — non-commercial use only.", Receipt(resolved));

        var synthetic = Path.Combine(AppContext.BaseDirectory, "fixtures", "plan_import_synthetic.json");
        resolved = PlanSource.Resolve(new JObject { ["image_path"] = Image(), ["plan_path"] = synthetic }, Out);
        Assert.Null(resolved.Licence);
        Assert.DoesNotContain("licensed", Receipt(resolved));
    }

    [Fact]
    public void AnImageTheRasterSourceCannotRead_IsRefusedBeforeItRuns()
    {
        var error = Assert.Throws<ArgumentException>(() => PlanSource.Resolve(new JObject { ["image_path"] = Image("plan.tif") }, Out));
        Assert.Equal("image_path must be a PNG or JPEG for the raster source to read, or come with its plan_path.", error.Message);
        Assert.True(PlanRaster.IsImage("a.PNG") && PlanRaster.IsImage("a.jpg") && PlanRaster.IsImage("a.jpeg"));
        Assert.False(PlanRaster.IsImage("a.pdf") || PlanRaster.IsImage("a.dxf") || PlanRaster.IsImage(null));
    }

    [Theory]
    [InlineData(@"""source"": ""cubicasa5k"", ""licence"": ""CC BY-NC 4.0""")]
    [InlineData(@"""source"": { ""vendor"": ""cubicasa5k"", ""licence"": ""CC BY-NC 4.0"" }")]
    [InlineData(@"""metadata"": { ""source"": ""cubicasa5k"", ""licence"": ""CC BY-NC 4.0"" }")]
    public void Parse_ReadsTheSourceAndLicence_WhereverTheFileStatesThem(string meta)
    {
        var plan = PlanImport.Parse(@"{ ""schema"": ""forsk.plan_import.v0"", " + meta + " }");
        Assert.Equal("cubicasa5k", plan.Vendor);
        Assert.Equal("CC BY-NC 4.0", plan.Licence);
        Assert.Null(plan.Diagnostic);
    }
}
