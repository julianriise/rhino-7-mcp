using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Where plan_import's plan file and underlay image come from (F7): a vector
/// PDF through pdf-vector (PlanPdf), a scan or photo through the raster source
/// (PlanRaster), or an image with a plan file made elsewhere. A PDF page with
/// no vector walls goes on to the raster source, and the receipt says so. No
/// RhinoCommon, so it tests headless.
/// </summary>
public static class PlanSource
{
    public sealed class Resolved
    {
        public PlanImport.Plan Plan;
        public string PlanPath;
        public string ImagePath;
        /// <summary>What was read, as the receipt names it: plan1.pdf page 1, plan.png, plan.json.</summary>
        public string Label;
        /// <summary>With pdf-vector's plan: the page read, and the plan file and page image it wrote. Else null.</summary>
        public JObject Pdf;
        /// <summary>With the raster source's plan: what it read, the files it wrote, the model and its licence, and why a PDF page went to it. Else null.</summary>
        public JObject Raster;
        /// <summary>The licence of the model that made the plan, when there is one to state.</summary>
        public string Licence;
        /// <summary>Why a PDF page was read as a scan: pdf-vector's diagnostic. Else null.</summary>
        public string Scan;
    }

    /// <summary>The stages Resolve reports: a PDF's own lines read, then a page or image read as a scan.</summary>
    public const string StagePdf = "pdf";
    public const string StageScan = "scan";

    static readonly object HeldLock = new object();
    static string _heldKey;
    static Resolved _held;

    /// <summary>
    /// Resolve off the UI thread, before plan_import runs (UX.2): the slow
    /// reading (pdf-vector, the raster model) no longer holds Rhino, and stage
    /// names each step as it starts. The result is held for the next
    /// plan_import with the same source, once.
    /// </summary>
    public static Resolved Prepare(JObject parameters, string workDir, Action<string> stage)
    {
        lock (HeldLock)
        {
            _held = null;
            _heldKey = null;
        }
        var resolved = Resolve(parameters, workDir, stage);
        lock (HeldLock)
        {
            _held = resolved;
            _heldKey = Key(parameters);
        }
        return resolved;
    }

    /// <summary>The source Prepare read for these parameters, taken once; null when none is held for them.</summary>
    public static Resolved Take(JObject parameters)
    {
        lock (HeldLock)
        {
            if (_held == null || _heldKey != Key(parameters)) return null;
            var held = _held;
            _held = null;
            _heldKey = null;
            return held;
        }
    }

    static string Key(JObject parameters)
    {
        return string.Join("|", Given(parameters?["pdf_path"]), parameters?["page"]?.ToString(),
            Given(parameters?["image_path"]), Given(parameters?["plan_path"]));
    }

    /// <summary>Where a source's plan file and image go: they stay beside each other, and the image is embedded in the .3dm.</summary>
    public static string WorkDir()
    {
        return Path.Combine(Path.GetTempPath(), "forsk-plan-import");
    }

    /// <summary>
    /// plan_import's source: pdf_path (and page) through pdf-vector, and on
    /// to the raster source when the page has no vector walls; image_path
    /// alone through the raster source; image_path with plan_path as given.
    /// The plan and image files go to workDir. Throws with the reason, and
    /// the next step where there is one, when nothing can be imported.
    /// </summary>
    public static Resolved Resolve(JObject parameters, string workDir, Action<string> stage = null)
    {
        var imagePath = Given(parameters["image_path"]);
        var planPath = Given(parameters["plan_path"]);
        var pdfPath = Given(parameters["pdf_path"]);
        Resolved resolved;
        if (pdfPath != null)
        {
            if (imagePath != null || planPath != null)
                throw new ArgumentException("Pass pdf_path or image_path, not both.");
            if (!File.Exists(pdfPath))
                throw new ArgumentException("pdf_path must be an existing PDF.");
            var page = parameters["page"]?.ToObject<int?>();
            if (page == null)
            {
                var pages = PlanPdf.Pages(pdfPath);
                if (pages > 1)
                    throw new ArgumentException(Path.GetFileName(pdfPath) + " has " + pages + " pages. Pass page, 1 to " + pages + ".");
                page = 1;
            }
            var label = Path.GetFileName(pdfPath) + " page " + page.Value.ToString(CultureInfo.InvariantCulture);
            stage?.Invoke(StagePdf);
            var extracted = PlanPdf.Extract(pdfPath, page.Value, workDir);
            var plan = PlanImport.Parse(File.ReadAllText(extracted.PlanPath));
            // No vector walls, a scan or a page drawn some other way: the raster source reads the page as an image.
            if (plan.Walls.Count == 0)
                return Raster(pdfPath, page, workDir, label, plan.Diagnostic ?? "no vector walls found", stage);
            resolved = new Resolved
            {
                Plan = plan,
                PlanPath = extracted.PlanPath,
                ImagePath = extracted.ImagePath,
                Label = label,
                Pdf = new JObject
                {
                    ["file"] = Path.GetFileName(pdfPath),
                    ["page"] = page.Value,
                    ["plan_path"] = extracted.PlanPath,
                    ["image_path"] = extracted.ImagePath
                }
            };
        }
        else
        {
            if (imagePath == null || !File.Exists(imagePath))
                throw new ArgumentException("image_path must be an existing image file.");
            if (planPath == null)
            {
                if (!PlanRaster.IsImage(imagePath))
                    throw new ArgumentException("image_path must be a PNG or JPEG for the raster source to read, or come with its plan_path.");
                return Raster(imagePath, null, workDir, Path.GetFileName(imagePath), null, stage);
            }
            if (!File.Exists(planPath))
                throw new ArgumentException("plan_path must be an existing forsk.plan_import.v0 file.");
            var plan = PlanImport.Parse(File.ReadAllText(planPath));
            resolved = new Resolved
            {
                Plan = plan,
                PlanPath = planPath,
                ImagePath = imagePath,
                Label = Path.GetFileName(planPath),
                Licence = plan.Licence ?? (PlanRaster.IsModel(plan.Vendor) ? PlanRaster.Licence : null)
            };
        }
        Refuse(resolved);
        return resolved;
    }

    /// <summary>
    /// How the scale stands at import: detected when the source read it off
    /// the plan, else unconfirmed. ratio is the one read, else the hint, else
    /// the one the plan file assumed.
    /// </summary>
    public static string Scale(PlanImport.Plan plan, string hint, out string ratio)
    {
        var detected = string.Equals(plan.ScaleStatus, "detected", StringComparison.OrdinalIgnoreCase);
        ratio = detected && !string.IsNullOrWhiteSpace(plan.ScaleRatio) ? plan.ScaleRatio
            : !string.IsNullOrWhiteSpace(hint) ? hint.Trim()
            : plan.ScaleRatio;
        return detected ? "detected" : "unconfirmed";
    }

    /// <summary>
    /// The receipt: counts, how the scale stands and what needs review, then
    /// why a PDF page was read as a scan and the licence of the model that
    /// read it.
    /// </summary>
    public static string Receipt(Resolved resolved, PlanImport.Result cleaned, string status, string ratio)
    {
        var receipt = PlanImport.Message(cleaned, PlanImport.ScaleLine(status, ratio));
        if (resolved.Scan != null)
            receipt += " " + resolved.Label + ": " + resolved.Scan + ", so the raster source read it as a scan.";
        if (resolved.Licence != null)
            receipt += " Plan read by " + (resolved.Raster != null || PlanRaster.IsModel(resolved.Plan.Vendor) ? "the CubiCasa5k model" : resolved.Plan.Vendor ?? "its source")
                + ", licensed " + resolved.Licence + ".";
        return receipt;
    }

    /// <summary>The raster source on input; a failure is named with label, and with scan, why the PDF page came to it.</summary>
    static Resolved Raster(string input, int? page, string workDir, string label, string scan, Action<string> stage)
    {
        stage?.Invoke(StageScan);
        PlanPdf.Extracted detected;
        try
        {
            detected = PlanRaster.Detect(input, page, workDir);
        }
        catch (PlanRaster.Failure failure)
        {
            throw new InvalidOperationException(label + ": " + (scan == null ? "" : scan + ". Read as a scan, ") + failure.Message);
        }
        var plan = PlanImport.Parse(File.ReadAllText(detected.PlanPath));
        // An image carries no scale: whatever the file says, the user sets it.
        if (string.Equals(plan.ScaleStatus, "detected", StringComparison.OrdinalIgnoreCase)) plan.ScaleStatus = "assumed";
        var licence = plan.Licence ?? PlanRaster.Licence;
        var raster = new JObject
        {
            ["file"] = Path.GetFileName(input),
            ["model"] = plan.Vendor ?? PlanRaster.Model,
            ["licence"] = licence,
            ["plan_path"] = detected.PlanPath,
            ["image_path"] = detected.ImagePath
        };
        if (page != null) raster["page"] = page.Value;
        if (scan != null) raster["scan"] = scan;
        var resolved = new Resolved
        {
            Plan = plan,
            PlanPath = detected.PlanPath,
            ImagePath = detected.ImagePath,
            Label = label,
            Raster = raster,
            Licence = licence,
            Scan = scan
        };
        Refuse(resolved);
        return resolved;
    }

    /// <summary>A detection with no walls whose source says why: nothing is placed.</summary>
    static void Refuse(Resolved resolved)
    {
        var refusal = PlanImport.Refusal(resolved.Plan, resolved.Label);
        if (refusal != null) throw new InvalidOperationException(refusal);
    }

    static string Given(JToken token)
    {
        var text = token?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
