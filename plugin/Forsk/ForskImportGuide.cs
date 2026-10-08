using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// UX.2, the guided AI detection flow: + , the Import a plan pill and
    /// "import a plan" in chat open one step card, pinned under the top bar:
    /// 1 Choose file, 2 Set scale, 3 Review, 4 Generate 3D. The card's data
    /// holds where the flow is; Paint draws the card from it and the file's
    /// facts, so a Set scale or an Undo moves the card without its own message.
    /// No RhinoCommon, so it tests headless.
    /// </summary>
    public static class ForskImportGuide
    {
        public const string Kind = "import.guide";

        public const string Choose = "choose";
        public const string SetScale = "scale";
        public const string Keep = "keep";
        public const string Looks = "looks";
        public const string Generate = "generate";
        public const string Later = "done";
        public const string Cancel = "cancel";
        /// <summary>A page pill's id: page:3.</summary>
        public const string PagePrefix = "page:";

        /// <summary>Where the flow is, in the card's data.</summary>
        public const string AtFile = "file";
        public const string AtPage = "page";
        public const string AtReading = "reading";
        public const string AtFailed = "failed";
        public const string AtImported = "imported";

        /// <summary>How the plan was read: a PDF's own lines, a scan or image by the model, or a DXF's layers.</summary>
        public const string ReadPdf = "pdf";
        public const string ReadScan = "scan";
        public const string ReadDxf = "dxf";

        const int MaxRows = 8;

        /// <summary>A new flow's data: step 1, and whether a plan is already there to replace.</summary>
        public static JObject Start(FileFacts f)
        {
            return new JObject { ["at"] = AtFile, ["replaces"] = f?.HasUnderlay == true };
        }

        /// <summary>A multi-page PDF picked: its pages as pills on the same card.</summary>
        public static void Pages(JObject data, string path, int pages)
        {
            data["at"] = AtPage;
            data["path"] = path;
            data["file"] = System.IO.Path.GetFileName(path ?? "");
            data["pages"] = pages;
            data.Remove("error");
        }

        /// <summary>A file picked: the card reads it, and step 1 says which.</summary>
        public static void Reading(JObject data, string path)
        {
            data["at"] = AtReading;
            data["path"] = path;
            data["file"] = System.IO.Path.GetFileName(path ?? "");
            data.Remove("error");
            data.Remove("undone");
        }

        /// <summary>Nothing was imported: step 1 says why, in plain words, and offers another file.</summary>
        public static void Failed(JObject data, string reason)
        {
            data["at"] = AtFailed;
            data["error"] = Plain(reason);
        }

        /// <summary>
        /// plan_import's result on the card: the counts, how the scale stands,
        /// what to review, how it was read and the model's licence.
        /// </summary>
        public static void Imported(JObject data, JObject result)
        {
            data["at"] = AtImported;
            data["read"] = result?["raster"] != null ? ReadScan : ReadPdf;
            data["found"] = new JObject
            {
                ["walls"] = Int(result?["walls"]),
                ["doors"] = Int(result?["doors"]),
                ["windows"] = Int(result?["windows"]),
                ["rooms"] = Int(result?["rooms"])
            };
            data["scale"] = result?["scale"]?["status"]?.ToString() ?? "unconfirmed";
            var ratio = result?["scale"]?["ratio"]?.ToString();
            if (!string.IsNullOrWhiteSpace(ratio)) data["ratio"] = ratio;
            else data.Remove("ratio");
            data["review"] = new JArray((result?["review"] as JArray ?? new JArray()).Select(r => r.ToString()));
            data.Remove("error");
            data.Remove("kept");
            data.Remove("reviewed");
        }

        /// <summary>dxf_import's result: the scale is the DXF's units, and its warnings are what to review.</summary>
        public static void ImportedDxf(JObject data, IEnumerable<string> review)
        {
            data["at"] = AtImported;
            data["read"] = ReadDxf;
            data.Remove("found");
            data["scale"] = "dxf";
            data.Remove("ratio");
            data["review"] = new JArray((review ?? Enumerable.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)));
            data.Remove("error");
            data.Remove("kept");
            data.Remove("reviewed");
        }

        /// <summary>The step under way after the import: scale, review or generate.</summary>
        public static string Next(JObject data, FileFacts f)
        {
            if (data?["at"]?.ToString() != AtImported) return null;
            var scaled = data["scale"]?.ToString() == "dxf" || f?.ScaleSet == true || data["kept"]?.ToObject<bool>() == true;
            if (!scaled) return SetScale;
            if (data["reviewed"]?.ToObject<bool>() != true) return Looks;
            return Generate;
        }

        /// <summary>
        /// The card as the flow stands: question, steps, pills, rows and note.
        /// An import that was undone (no underlay and no plan curves left) goes
        /// back to step 1 and says so.
        /// </summary>
        public static void Paint(JObject card, FileFacts f)
        {
            var data = card["data"] as JObject;
            if (data == null) card["data"] = data = Start(f);
            if (data["at"]?.ToString() == AtImported && f != null && !f.HasUnderlay && !f.HasPlanCurves)
            {
                data["at"] = AtFile;
                data["undone"] = true;
            }
            var at = data["at"]?.ToString() ?? AtFile;
            var file = data["file"]?.ToString() ?? "";
            var steps = new JArray();
            var pills = new JArray();
            var rows = new List<string>();
            string note = null;
            var question = ForskText.Get("guide.title");

            // Step 1: choose file.
            switch (at)
            {
                case AtPage:
                    var pages = Int(data["pages"]);
                    question = ForskText.Format("pdf.page.ask", "file", file, "n", pages.ToString(CultureInfo.InvariantCulture));
                    steps.Add(Step(1, "guide.step.file", "now", file));
                    foreach (var pill in ForskCards.PdfPage(file, pages).Pills.Where(p => p.Id != Cancel))
                        pills.Add(Pill(PagePrefix + pill.Id, pill.Label));
                    if (pages > ForskCards.MaxPages) note = ForskText.Format("pdf.page.more", "n", ForskCards.MaxPages.ToString(CultureInfo.InvariantCulture));
                    pills.Add(Pill(Choose, ForskText.Get("guide.pill.another")));
                    pills.Add(Pill(Cancel, ForskText.Get("word.cancel")));
                    break;
                case AtReading:
                    steps.Add(Step(1, "guide.step.file", "now", ForskText.Format("guide.file.reading", "file", file)));
                    break;
                case AtFailed:
                    question = ForskText.Format("guide.failed", "file", file);
                    steps.Add(Step(1, "guide.step.file", "failed", data["error"]?.ToString()));
                    pills.Add(Pill(Choose, ForskText.Get("guide.pill.another"), "file-up", true));
                    pills.Add(Pill(Cancel, ForskText.Get("word.cancel")));
                    break;
                case AtImported:
                    steps.Add(Step(1, "guide.step.file", "done", ForskText.Format("guide.file.read." + (data["read"]?.ToString() ?? ReadPdf), "file", file)));
                    break;
                default:
                    steps.Add(Step(1, "guide.step.file", "now", ForskText.Get(data["undone"]?.ToObject<bool>() == true ? "guide.file.undone" : "guide.file.how")));
                    pills.Add(Pill(Choose, ForskText.Get("guide.pill.choose"), "file-up", true));
                    pills.Add(Pill(Cancel, ForskText.Get("word.cancel")));
                    if (data["replaces"]?.ToObject<bool>() == true || f?.HasUnderlay == true) note = ForskText.Get("guide.file.replaces");
                    break;
            }

            var next = Next(data, f);
            var ratio = data["ratio"]?.ToString();

            // Step 2: set scale.
            if (next == null)
                steps.Add(Step(2, "guide.step.scale", "todo", null));
            else if (next == SetScale)
            {
                var detected = data["scale"]?.ToString() == "detected" && !string.IsNullOrEmpty(ratio);
                steps.Add(Step(2, "guide.step.scale", "now", detected
                    ? ForskText.Format("guide.scale.detected", "ratio", ratio)
                    : ForskText.Format("guide.scale.unsure", "ratio", string.IsNullOrEmpty(ratio) ? "" : ForskText.Format("guide.scale.assumed", "ratio", ratio))));
                pills.Add(Pill(SetScale, ForskText.Get("guide.pill.scale"), "ruler", true));
                pills.Add(Pill(Keep, detected ? ForskText.Format("guide.pill.keep", "ratio", ratio) : ForskText.Get("guide.pill.skip")));
            }
            else
            {
                string how;
                if (data["scale"]?.ToString() == "dxf") how = ForskText.Get("guide.scale.dxf");
                else if (f?.ScaleSet == true) how = ForskText.Get("guide.scale.user");
                else if (data["scale"]?.ToString() == "detected" && !string.IsNullOrEmpty(ratio)) how = ForskText.Format("guide.scale.kept", "ratio", ratio);
                else how = ForskText.Get("guide.scale.skipped");
                steps.Add(Step(2, "guide.step.scale", f?.ScaleSet == true || data["scale"]?.ToString() != "unconfirmed" && data["scale"]?.ToString() != "assumed" ? "done" : "skipped", how));
            }

            // Step 3: review.
            if (next == null || next == SetScale)
                steps.Add(Step(3, "guide.step.review", "todo", null));
            else if (next == Looks)
            {
                var review = (data["review"] as JArray ?? new JArray()).Select(r => r.ToString()).ToList();
                var found = Found(data["found"] as JObject);
                var ask = ForskText.Get(review.Count == 0 ? "guide.review.none" : "guide.review.ask");
                steps.Add(Step(3, "guide.step.review", "now", string.IsNullOrEmpty(found) ? ask : found + " " + ask));
                rows.AddRange(review.Take(MaxRows));
                if (review.Count > MaxRows) rows.Add(ForskText.Format("guide.review.more", "n", (review.Count - MaxRows).ToString(CultureInfo.InvariantCulture)));
                pills.Add(Pill(Looks, ForskText.Get("guide.pill.looks"), "check", true));
                pills.Add(Pill(Choose, ForskText.Get("guide.pill.another")));
            }
            else
                steps.Add(Step(3, "guide.step.review", "done", Found(data["found"] as JObject)));

            // Step 4: generate 3D.
            if (next == Generate)
            {
                steps.Add(Step(4, "guide.step.generate", "now", ForskText.Get("guide.generate.ask")));
                pills.Add(Pill(Generate, ForskText.Get("guide.pill.generate"), "box", true));
                pills.Add(Pill(Later, ForskText.Get("guide.pill.later")));
            }
            else
                steps.Add(Step(4, "guide.step.generate", "todo", null));

            // The model's licence line, while a scan's result is on the card.
            if (at == AtImported && data["read"]?.ToString() == ReadScan && note == null) note = ForskText.Get("guide.licence");

            card["question"] = question;
            card["steps"] = steps;
            card["pills"] = pills;
            if (rows.Count > 0) card["rows"] = new JArray(rows);
            else card.Remove("rows");
            if (note != null) card["note"] = note;
            else card.Remove("note");
        }

        /// <summary>The answered card's one line: Generate 3D, or Later.</summary>
        public static string Receipt(string pillId)
        {
            return ForskText.Get(pillId == Generate ? "guide.receipt.generate" : "guide.receipt.later");
        }

        /// <summary>The page number on a page pill, or null.</summary>
        public static int? Page(string pillId)
        {
            if (pillId == null || !pillId.StartsWith(PagePrefix, StringComparison.Ordinal)) return null;
            return int.TryParse(pillId.Substring(PagePrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : (int?)null;
        }

        /// <summary>The step line while AI detection runs: what it is doing and how long it has taken, 0:42.</summary>
        public static string Progress(string stage, string file, TimeSpan elapsed)
        {
            var what = stage == PlanSource.StagePdf ? ForskText.Get("guide.progress.pdf")
                : stage == PlanSource.StageScan ? ForskText.Get("guide.progress.scan")
                : stage == "place" ? ForskText.Get("guide.progress.place")
                : stage == ReadDxf ? ForskText.Get("guide.progress.dxf")
                : ForskText.Format("guide.progress.start", "file", file ?? "");
            var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
            return what + "… " + (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>The receipt row after a read: AI detection found 30 walls, 7 doors, 12 windows and 11 rooms.</summary>
        public static string Found(JObject found)
        {
            if (found == null) return "";
            return ForskText.Format("guide.found",
                "walls", Count(Int(found["walls"]), "wall"),
                "doors", Count(Int(found["doors"]), "door"),
                "windows", Count(Int(found["windows"]), "window"),
                "rooms", Count(Int(found["rooms"]), "room"));
        }

        static readonly Regex ImportWord = new Regex(@"\b(import|importer|importere|last inn|les inn|bring in|read)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex PlanWord = new Regex(@"(\bplan\b|\bplans\b|\bplanen\b|plantegning|\bpdf\b|\.pdf\b|\bdxf\b|\.dxf\b|\bscan\b|\bskann|\bphoto\b|\bbilde\b|\bdrawing\b|\btegning)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Detection = new Regex(@"\bai[- ](detection|gjenkjenning)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// plan_import from chat with nothing it can open as it stands: the user
        /// picks. A PDF with no page and more than one is asked for its page.
        /// An image alone goes to the raster source; a plan file named with it
        /// has to be there too. Null when nothing needs asking.
        /// </summary>
        public static JObject NeedsSource(JObject args)
        {
            var pdf = args?["pdf_path"]?.ToString();
            if (!ForskDxf.NeedsPick(pdf))
                return args["page"] == null ? new JObject { ["pdf_path"] = pdf, ["ask_page"] = true } : null;
            var image = args?["image_path"]?.ToString();
            var plan = args?["plan_path"]?.ToString();
            if (!ForskDxf.NeedsPick(image) && (string.IsNullOrWhiteSpace(plan) || !ForskDxf.NeedsPick(plan))) return null;
            return new JObject();
        }

        /// <summary>
        /// True when chat's plan_import names no file it can open and the window
        /// can show the AI detection card: the card asks for the file, not a
        /// dialog. A PDF named without its page still asks for the page.
        /// </summary>
        public static bool OpensGuide(string name, JObject args, bool windowHasGuide)
        {
            if (!windowHasGuide || name != ForskToolPacks.ImportTool) return false;
            var need = NeedsSource(args ?? new JObject());
            return need != null && need["ask_page"] == null;
        }

        /// <summary>What chat's model reads back when the card opened in place of the import.</summary>
        public static JObject GuideOpened() => new JObject
        {
            ["status"] = "success",
            ["result"] = new JObject
            {
                ["message"] = "The AI detection card is open in the Forsk window. The user chooses the plan file there; nothing was imported yet."
            }
        };

        /// <summary>
        /// A sentence that asks to import a plan with no file Forsk can open
        /// from it ("import a plan", "import plan.pdf", "importer plantegningen",
        /// "how do I use AI detection"): it opens the card, with no chat model call.
        /// A sentence with a full path goes to the chat model as before.
        /// </summary>
        public static bool Opens(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.IndexOf('/') >= 0 || text.IndexOf('\\') >= 0) return false;
            if (Detection.IsMatch(text)) return true;
            return ImportWord.IsMatch(text) && PlanWord.IsMatch(text) && !Regex.IsMatch(text, @"\bscale\b|\bskala\b", RegexOptions.IgnoreCase);
        }

        /// <summary>A failure in the user's words: AI detection, not the raster source, and the next step.</summary>
        public static string Plain(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return ForskText.Get("guide.failed.unknown");
            if (reason.IndexOf("must be millimetres", StringComparison.OrdinalIgnoreCase) >= 0) return ForskText.Get("guide.units");
            var text = reason.Trim();
            text = text.Replace("Reading a scan or image needs", "AI detection needs")
                .Replace("reading a scan or image needs", "AI detection needs")
                .Replace("The raster source", "AI detection")
                .Replace("the raster source", "AI detection")
                .Replace("The PDF extractor", "AI detection")
                .Replace("the CubiCasa5k model", "the AI detection model");
            return text;
        }

        static JObject Step(int n, string title, string state, string detail)
        {
            var step = new JObject { ["n"] = n, ["title"] = ForskText.Get(title), ["state"] = state };
            if (!string.IsNullOrWhiteSpace(detail)) step["detail"] = detail;
            return step;
        }

        static JObject Pill(string id, string label, string icon = null, bool primary = false)
        {
            var pill = new JObject { ["id"] = id, ["label"] = label };
            if (icon != null) pill["icon"] = icon;
            if (primary) pill["primary"] = true;
            return pill;
        }

        static string Count(int n, string noun)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
        }

        static int Int(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ? 0 : token.ToObject<int?>() ?? 0;
        }
    }
}
