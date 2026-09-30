using System;
using System.IO;
using Eto.Forms;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Plan import from the panel, chip and chat. Import plan asks for the plan
    /// image and its detection (forsk.plan_import.v0) and runs plan_import. Set
    /// scale runs the ForskSetScale command: two picked points and the real
    /// length between them, prefilled with what the plan measures now. The
    /// geometry and the clean-up live in the plan_import and plan_scale handlers.
    /// </summary>
    public static class ForskPlanImport
    {
        public const string ImportTool = "plan_import";
        public const string ScaleTool = "plan_scale";
        public const string ScaleCommand = "ForskSetScale";
        const int ReviewRows = 6;

        static JObject _lastScale;

        /// <summary>The Import chip's click, off the UI thread. line is the receipt row, note what needs review.</summary>
        public static void Chip(ImportAction action, string image, string plan, out string line, out string note)
        {
            note = "";
            if (action == ImportAction.SetScale)
            {
                line = ScaleLine(PickScaleFromBackground());
                return;
            }
            var envelope = ForskTools.CommandOnUi(ImportTool, new JObject
            {
                ["image_path"] = image,
                ["plan_path"] = plan
            });
            line = ImportLine(envelope);
            note = ReviewNote(envelope);
        }

        /// <summary>The plan image, then its detection beside it. UI thread. False when either dialog is cancelled.</summary>
        public static bool PickFiles(out string image, out string plan)
        {
            image = null;
            plan = null;
            var parent = RhinoEtoApp.MainWindow;
            var imageDialog = new Eto.Forms.OpenFileDialog { Title = "Import plan: the plan image" };
            imageDialog.Filters.Add(new FileFilter("Plan image", ".png", ".jpg", ".jpeg"));
            if (imageDialog.ShowDialog(parent) != DialogResult.Ok) return false;

            var planDialog = new Eto.Forms.OpenFileDialog { Title = "Import plan: its detection (forsk.plan_import.v0 JSON)" };
            planDialog.Filters.Add(new FileFilter("Plan detection", ".json"));
            try
            {
                var folder = Path.GetDirectoryName(imageDialog.FileName);
                if (!string.IsNullOrEmpty(folder)) planDialog.Directory = new Uri(folder);
            }
            catch (Exception)
            {
                // The dialog opens where the system last left it.
            }
            if (planDialog.ShowDialog(parent) != DialogResult.Ok) return false;
            image = imageDialog.FileName;
            plan = planDialog.FileName;
            return true;
        }

        /// <summary>Chip receipt: Import plan · ok · Imported 30 walls, 7 doors, 12 windows, 11 rooms.</summary>
        public static string ImportLine(JObject envelope)
        {
            if (!Ok(envelope))
                return "Import plan · error · " + ForskTools.Clip(envelope?["message"]?.ToString() ?? "failed");
            return "Import plan · ok · " + ForskTools.Clip(Split(envelope, out _));
        }

        /// <summary>Under the receipt: how the scale stands, then what needs review, one row each.</summary>
        public static string ReviewNote(JObject envelope)
        {
            if (!Ok(envelope)) return "";
            Split(envelope, out var rest);
            var review = envelope["result"]?["review"] as JArray;
            if (review == null) return rest;
            for (var i = 0; i < review.Count && i < ReviewRows; i++)
                rest += "\n· " + review[i];
            if (review.Count > ReviewRows)
                rest += "\n· and " + (review.Count - ReviewRows) + " more";
            return rest.Trim();
        }

        /// <summary>The tool message's first sentence (the counts), and the rest of it.</summary>
        static string Split(JObject envelope, out string rest)
        {
            var message = envelope["result"]?["message"]?.ToString() ?? "";
            var stop = message.IndexOf(". ", StringComparison.Ordinal);
            rest = stop < 0 ? "" : message.Substring(stop + 2);
            return stop < 0 ? message : message.Substring(0, stop + 1);
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
