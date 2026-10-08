using System;
using System.IO;
using System.Linq;
using Eto.Forms;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Plan import from the panel, chip and chat. Import plan asks for a PDF
    /// (and its page, when it has more than one), a scanned or photographed
    /// plan (PNG or JPEG, read by the raster source), or a DXF (dxf_import),
    /// and runs the import. Set scale runs the ForskSetScale command: two
    /// picked points and the real length between them, prefilled with what the
    /// plan measures now. The geometry and the clean-up live in the plan_import
    /// and plan_scale handlers. A DXF asked for in chat with no path is picked
    /// here, and its receipt shown whole (ForskDxf).
    /// </summary>
    public static class ForskPlanImport
    {
        public const string ImportTool = ForskToolPacks.ImportTool;
        public const string ScaleTool = ForskToolPacks.ScaleTool;
        public const string ScaleCommand = "ForskSetScale";

        static JObject _lastScale;
        static bool _lengthInWindow;
        static JObject _lastPick;

        /// <summary>The Import chip's click, off the UI thread. source is what PickSource chose. line is the receipt row, note what needs review.</summary>
        public static void Chip(ImportAction action, JObject source, out string line, out string note)
        {
            note = "";
            if (action == ImportAction.SetScale)
            {
                line = ScaleLine(PickScaleFromBackground());
                return;
            }
            if (source?["dxf"] != null)
            {
                var dxf = ForskTools.CommandOnUi(ForskDxf.Tool, new JObject { ["path"] = source["dxf"] });
                line = DxfLine(dxf);
                note = DxfNote(dxf);
                return;
            }
            var envelope = ForskTools.CommandOnUi(ImportTool, source);
            line = ImportLine(envelope);
            note = ReviewNote(envelope);
        }

        /// <summary>
        /// What to import: plan_import's arguments for a PDF and its page
        /// (asked for when it has more than one) or an image, or the DXF as dxf.
        /// UI thread. Null when a dialog is cancelled.
        /// </summary>
        public static JObject PickSource(Window parent = null)
        {
            var first = new Eto.Forms.OpenFileDialog { Title = "Import plan: a PDF, a scan or photo of the plan, or a DXF" };
            first.Filters.Add(new FileFilter("Plan PDF, image or DXF", ForskPlanFile.Extensions));
            if (first.ShowDialog(parent ?? RhinoEtoApp.MainWindow) != DialogResult.Ok) return null;
            var argument = ForskPlanFile.Argument(first.FileName);
            if (argument == "pdf_path")
            {
                var page = AskPage(first.FileName);
                return page == null ? null : new JObject { ["pdf_path"] = first.FileName, ["page"] = page.Value };
            }
            return argument == null ? null : new JObject { [argument] = first.FileName };
        }

        /// <summary>The import's file dialog alone, parented to the window. UI thread. Null when cancelled.</summary>
        public static string PickFile(Window parent)
        {
            var dialog = new Eto.Forms.OpenFileDialog { Title = "Import plan: a PDF, a scan or photo of the plan, or a DXF" };
            dialog.Filters.Add(new FileFilter("Plan PDF, image or DXF", ForskPlanFile.Extensions));
            return dialog.ShowDialog(parent ?? RhinoEtoApp.MainWindow) == DialogResult.Ok ? dialog.FileName : null;
        }

        /// <summary>A PDF's page count. One when the extractor cannot count it: plan_import then says what went wrong.</summary>
        public static int PageCount(string pdf)
        {
            try
            {
                return Math.Max(1, PlanPdf.Pages(pdf));
            }
            catch (Exception)
            {
                return 1;
            }
        }

        /// <summary>PickSource from a background thread.</summary>
        public static JObject PickSourceFromBackground(Window parent = null)
        {
            return OnUi(() => PickSource(parent));
        }

        /// <summary>
        /// The page to import: 1 for a one-page PDF, else the user's choice. UI
        /// thread. Null when cancelled. A PDF the extractor cannot count goes on
        /// as page 1, and plan_import reports what went wrong with it.
        /// </summary>
        public static int? AskPage(string pdf)
        {
            int pages;
            try
            {
                pages = PlanPdf.Pages(pdf);
            }
            catch (Exception)
            {
                return 1;
            }
            if (pages <= 1) return 1;
            var items = Enumerable.Range(1, pages).Select(n => "Page " + n).ToList();
            var chosen = Rhino.UI.Dialogs.ShowListBox("Import plan", Path.GetFileName(pdf) + " has " + pages + " pages. Which is the plan?", items) as string;
            if (chosen == null) return null;
            return items.IndexOf(chosen) + 1;
        }

        /// <summary>The plan_import arguments chat should run with, asking the user what it lacks. Background thread. Null when cancelled.</summary>
        public static JObject SourceFromBackground(JObject args, Window parent = null)
        {
            var need = ForskImportGuide.NeedsSource(args);
            if (need == null) return args;
            if (need["ask_page"] == null) return PickSourceFromBackground(parent);
            var pdf = need["pdf_path"].ToString();
            var page = OnUi(() => AskPage(pdf) is int n ? new JObject { ["page"] = n } : null);
            if (page == null) return null;
            var run = (JObject)args.DeepClone();
            run["page"] = page["page"];
            return run;
        }

        static JObject OnUi(Func<JObject> ask)
        {
            JObject answer = null;
            using (var done = new System.Threading.ManualResetEvent(false))
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    try { answer = ask(); }
                    finally { done.Set(); }
                });
                done.WaitOne();
            }
            return answer;
        }

        /// <summary>Chip receipt: Import plan · ok · Imported 30 walls, 7 doors, 12 windows, 11 rooms. On an error, the reason.</summary>
        public static string ImportLine(JObject envelope)
        {
            return ForskTools.Clip(ForskPlanFile.Line(Ok(envelope), Message(envelope)));
        }

        /// <summary>Under the receipt: how the scale stands and the model's licence, then what needs review. On an error, the next step.</summary>
        public static string ReviewNote(JObject envelope)
        {
            var ok = Ok(envelope);
            var review = (ok ? envelope["result"]?["review"] as JArray : null)?.Select(row => row.ToString());
            return ForskPlanFile.Note(ok, Message(envelope), review);
        }

        /// <summary>The tool's message: the receipt, or why it failed.</summary>
        static string Message(JObject envelope)
        {
            return (Ok(envelope) ? envelope["result"]?["message"] : envelope?["message"])?.ToString();
        }

        /// <summary>The DXF to import, asked for from a background thread. Null when the dialog is cancelled.</summary>
        public static string PickDxfFromBackground(Window parent = null)
        {
            string path = null;
            using (var done = new System.Threading.ManualResetEvent(false))
            {
                Application.Instance.AsyncInvoke(() =>
                {
                    try
                    {
                        var dialog = new Eto.Forms.OpenFileDialog { Title = "Import DXF" };
                        dialog.Filters.Add(new FileFilter("DXF", ".dxf"));
                        if (dialog.ShowDialog(parent ?? RhinoEtoApp.MainWindow) == DialogResult.Ok) path = dialog.FileName;
                    }
                    finally { done.Set(); }
                });
                done.WaitOne();
            }
            return path;
        }

        /// <summary>Chat receipt for dxf_import: Import DXF · ok · Imported plan.dxf: 283 objects.</summary>
        public static string DxfLine(JObject envelope)
        {
            var ok = Ok(envelope);
            return ForskTools.Clip(ForskDxf.Line(ok, (ok ? envelope["result"]?["message"] : envelope?["message"])?.ToString()));
        }

        /// <summary>Under it: texts, units and scale, suspect labels, then what needs a look.</summary>
        public static string DxfNote(JObject envelope)
        {
            if (!Ok(envelope)) return "";
            var result = envelope["result"];
            return ForskDxf.Note(result?["message"]?.ToString(),
                (result?["unmatched"] as JArray)?.Select(row => row.ToString()),
                (result?["warnings"] as JArray)?.Select(row => row.ToString()));
        }

        /// <summary>Chip receipt: Set scale · ok · Scale set: 4000 mm between the two points ...</summary>
        public static string ScaleLine(JObject envelope)
        {
            if (!Ok(envelope))
                return "Set scale · error · " + ForskTools.Clip(envelope?["message"]?.ToString() ?? "failed");
            return "Set scale · ok · " + ForskTools.Clip(envelope["result"]?["message"]?.ToString() ?? "");
        }

        /// <summary>plan_scale from chat with no points: the user picks them.</summary>
        public static bool NeedsPick(JObject args)
        {
            return !(args?["p1"] is JArray) || !(args["p2"] is JArray);
        }

        /// <summary>Runs ForskSetScale from a background thread and returns what plan_scale answered.</summary>
        public static JObject PickScaleFromBackground()
        {
            JObject envelope = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                _lastScale = null;
                RhinoApp.RunScript("_" + ScaleCommand, false);
                envelope = _lastScale;
            }));
            return envelope ?? ForskTools.Fail("Set scale cancelled.");
        }

        /// <summary>
        /// The Forsk window's Set scale: the two points are picked in the view by
        /// ForskSetScale, and the length is then typed in a Forsk field, not on
        /// the command line. UI thread. Returns p1, p2 and measured_mm, or null
        /// when the pick was cancelled; a failed measure is the envelope.
        /// </summary>
        public static JObject PickScalePoints()
        {
            _lastPick = null;
            _lastScale = null;
            _lengthInWindow = true;
            try
            {
                RhinoApp.RunScript("_" + ScaleCommand, false);
            }
            finally
            {
                _lengthInWindow = false;
            }
            return _lastPick ?? (_lastScale != null && !Ok(_lastScale) ? _lastScale : null);
        }

        /// <summary>
        /// The ForskSetScale command: two points on a length the user knows,
        /// then that length in mm, prefilled with what the plan measures now
        /// (a detected scale makes that the right number already).
        /// </summary>
        public static Rhino.Commands.Result RunScaleCommand()
        {
            _lastScale = null;
            var first = new GetPoint();
            first.SetCommandPrompt("Set scale: first point of a length you know");
            if (first.Get() != GetResult.Point) return Rhino.Commands.Result.Cancel;
            var p1 = first.Point();

            var second = new GetPoint();
            second.SetCommandPrompt("Second point");
            second.SetBasePoint(p1, true);
            second.DrawLineFromPoint(p1, true);
            if (second.Get() != GetResult.Point) return Rhino.Commands.Result.Cancel;
            var p2 = second.Point();

            var args = new JObject
            {
                ["p1"] = new JArray(p1.X, p1.Y),
                ["p2"] = new JArray(p2.X, p2.Y)
            };
            var measured = ForskTools.Command(ScaleTool, args);
            if (!Ok(measured))
            {
                _lastScale = measured;
                RhinoApp.WriteLine(ScaleLine(measured));
                return Rhino.Commands.Result.Failure;
            }
            var now = Math.Round(measured["result"]?["measured_mm"]?.ToObject<double?>() ?? p1.DistanceTo(p2));
            if (_lengthInWindow)
            {
                _lastPick = new JObject { ["p1"] = args["p1"], ["p2"] = args["p2"], ["measured_mm"] = now };
                return Rhino.Commands.Result.Success;
            }

            var length = new GetNumber();
            length.SetCommandPrompt("Real length between the points, mm");
            length.SetDefaultNumber(now);
            length.SetLowerLimit(0, true);
            length.AcceptNothing(true);
            var got = length.Get();
            if (got != GetResult.Number && got != GetResult.Nothing) return Rhino.Commands.Result.Cancel;
            args["length_mm"] = got == GetResult.Number ? length.Number() : now;

            _lastScale = ForskTools.Command(ScaleTool, args);
            RhinoApp.WriteLine(ScaleLine(_lastScale));
            return Ok(_lastScale) ? Rhino.Commands.Result.Success : Rhino.Commands.Result.Failure;
        }

        static bool Ok(JObject envelope)
        {
            return string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
        }
    }
}
