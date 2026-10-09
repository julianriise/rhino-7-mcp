using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Rhino;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// UX.2, the guided AI detection flow in the window: one pinned step card
    /// (ForskImportGuide) from +, the Import a plan pill or "import a plan" in
    /// chat. The file is read off the UI thread with a step line that names the
    /// stage and the time taken; the card then walks Set scale, Review and
    /// Generate 3D, each continuing from the last.
    /// </summary>
    sealed partial class ForskWindow
    {
        /// <summary>Opens the guide at step 1. An older open guide closes, so one is pinned.</summary>
        void OpenImportGuide(DocThread thread, string userText)
        {
            thread.Add("user", userText);
            thread.BeginReply(ForskRoles.MarkForAction("file.import"));
            AddImportGuide(thread);
            thread.EndReply();
            Models.Persist(thread);
            Render();
        }

        /// <summary>
        /// The pinned guide at step 1, in the reply that is open. An older open
        /// guide closes, so one is pinned. Chat's plan_import with no file lands
        /// here too, inside its own turn.
        /// </summary>
        void AddImportGuide(DocThread thread)
        {
            foreach (var item in thread.Items)
                if (item["kind"]?.ToString() == ForskImportGuide.Kind) thread.Close(item["id"]?.ToString());
            var doc = RhinoDoc.ActiveDoc;
            var facts = doc == null ? null : Facts(doc);
            var card = thread.AddCard(new CardSpec { Kind = ForskImportGuide.Kind, Question = ForskText.Get("guide.title"), Pin = true, Data = ForskImportGuide.Start(facts) }, null);
            ForskImportGuide.Paint(card, facts);
        }

        /// <summary>The guide's pills. Every one but Cancel and Later waits while a job runs.</summary>
        void GuidePill(DocThread thread, JObject card, string pillId)
        {
            if (card["state"]?.ToString() != "open") return;
            if (!(card["pills"] as JArray ?? new JArray()).Any(p => p["id"]?.ToString() == pillId)) return;
            if (pillId == ForskImportGuide.Cancel)
            {
                // During the read: stop it. The job ends without placing anything.
                if (card["data"]?["at"]?.ToString() == ForskImportGuide.AtReading && _read != null)
                {
                    _read.Cancel();
                    thread.Add("line", ForskText.Get("guide.read.cancelled"));
                }
                thread.Close(card["id"]?.ToString());
                Models.Persist(thread);
                Render();
                return;
            }
            if (pillId != ForskImportGuide.Later && Refuse(thread)) return;
            var data = (JObject)card["data"];
            var page = ForskImportGuide.Page(pillId);
            if (page != null)
            {
                GuideRead(thread, card, new JObject { ["pdf_path"] = data["path"], ["page"] = page.Value });
                return;
            }
            switch (pillId)
            {
                case ForskImportGuide.Choose:
                    GuideChoose(thread, card);
                    return;
                case ForskImportGuide.SetScale:
                    // Two points in the view, then the length card; the guide moves on when that job finishes.
                    Scale(thread, ForskRegistry.Find("file.scale"));
                    return;
                case ForskImportGuide.Keep:
                    data["kept"] = true;
                    break;
                case ForskImportGuide.Looks:
                    data["reviewed"] = true;
                    break;
                case ForskImportGuide.Generate:
                case ForskImportGuide.Later:
                    card["receipt"] = ForskImportGuide.Receipt(pillId);
                    thread.Settle(card["id"]?.ToString(), card["receipt"].ToString());
                    Models.Persist(thread);
                    Render();
                    if (pillId == ForskImportGuide.Generate) Fire("file.generate", true);
                    return;
            }
            RefreshImportGuide(thread);
        }

        /// <summary>Step 1: the file dialog, parented to this window. A PDF of several pages asks which, on the same card.</summary>
        void GuideChoose(DocThread thread, JObject card)
        {
            var path = ForskPlanImport.PickFile(this);
            if (path == null) return;
            var argument = ForskPlanFile.Argument(path);
            if (argument == null) return;
            var data = (JObject)card["data"];
            if (argument == "pdf_path")
            {
                var pages = ForskPlanImport.PageCount(path);
                if (pages > 1)
                {
                    ForskImportGuide.Pages(data, path, pages);
                    RefreshImportGuide(thread);
                    return;
                }
                GuideRead(thread, card, new JObject { ["pdf_path"] = path, ["page"] = 1 });
                return;
            }
            GuideRead(thread, card, new JObject { [argument] = path });
        }

        /// <summary>
        /// The read: the card says which file at once, then the source is read
        /// off the UI thread (PlanSource.Prepare) with the stage and the time on
        /// the step line, and plan_import places what it found in one undo record.
        /// </summary>
        void GuideRead(DocThread thread, JObject card, JObject source)
        {
            var data = (JObject)card["data"];
            var path = (source["pdf_path"] ?? source["image_path"] ?? source["dxf"])?.ToString();
            var doc = RhinoDoc.ActiveDoc;
            var dxf = source["dxf"] != null;
            // Nothing slow starts when it cannot work: no uv for a PDF or an image, a file not in millimetres.
            if (!dxf && ForskUv.Uv() == null)
            {
                ForskImportGuide.Reading(data, path);
                ForskImportGuide.Failed(data, ForskText.Get("guide.setup"));
                RefreshImportGuide(thread);
                AskSetup(thread, ForskText.Label("file.import"), true);
                return;
            }
            if (!dxf && doc != null && doc.ModelUnitSystem != UnitSystem.Millimeters)
            {
                ForskImportGuide.Reading(data, path);
                ForskImportGuide.Failed(data, ForskText.Get("guide.units"));
                RefreshImportGuide(thread);
                return;
            }
            ForskImportGuide.Reading(data, path);
            ForskImportGuide.Paint(card, doc == null ? null : Facts(doc));
            if (!dxf) source["replace"] = true;
            var file = data["file"]?.ToString();
            var read = new CancellationTokenSource();
            _read = read;
            Job(thread, "file.import", ForskText.Label("file.import"), sink =>
            {
                var started = DateTime.UtcNow;
                var stage = dxf ? ForskImportGuide.ReadDxf : "start";
                void Show() => sink.Step(ForskImportGuide.Progress(stage, file, DateTime.UtcNow - started));
                Show();
                JObject envelope;
                try
                {
                    envelope = dxf
                        ? OnUiTool(ForskDxf.Tool, new JObject { ["path"] = source["dxf"] })
                        : ReadPlan(source, s =>
                        {
                            stage = s;
                            Show();
                        }, Show, read.Token);
                }
                catch (Exception e)
                {
                    envelope = ForskTools.Fail(e.Message);
                }
                Post(() =>
                {
                    if (_read == read) _read = null;
                    read.Dispose();
                    // Cancel already closed the card and said so.
                    if (read.IsCancellationRequested) return;
                    // The card always leaves Reading, whatever the read did.
                    GuideDone(thread, card, envelope, dxf);
                });
            }, userText: file);
        }

        /// <summary>The AI read in progress, or null. Cancel on the card cancels it.</summary>
        CancellationTokenSource _read;

        /// <summary>
        /// A PDF or an image: read off the UI thread, the step line ticking each
        /// second, then plan_import on the UI thread places what was found.
        /// </summary>
        static JObject ReadPlan(JObject source, Action<string> stage, Action tick, CancellationToken cancel)
        {
            using (var ticked = new ManualResetEvent(false))
            {
                var timer = new Timer(_ => tick(), null, 1000, 1000);
                try
                {
                    using (ForskUv.Cancellable(cancel))
                        PlanSource.Prepare(source, PlanSource.WorkDir(), stage);
                }
                catch (Exception e)
                {
                    return ForskTools.Fail(e.Message);
                }
                finally
                {
                    // Every tick is posted before Finish clears the step line.
                    timer.Dispose(ticked);
                    ticked.WaitOne();
                }
            }
            // Cancelled after the last tool finished: still place nothing.
            if (cancel.IsCancellationRequested) return ForskTools.Fail("Cancelled");
            stage("place");
            return OnUiTool(ForskPlanImport.ImportTool, source);
        }

        /// <summary>One tool on the UI thread, as a job's sink runs it, with no receipt: the guide carries the result.</summary>
        static JObject OnUiTool(string name, JObject args)
        {
            JObject envelope = null;
            RhinoApp.InvokeOnUiThread(new Action(() => envelope = ForskTools.ExecuteAllowed(name, args)));
            return envelope ?? ForskTools.Fail("No result");
        }

        /// <summary>The read's result on the card, and one receipt row in the thread when something was imported.</summary>
        void GuideDone(DocThread thread, JObject card, JObject envelope, bool dxf)
        {
            var data = (JObject)card["data"];
            var ok = string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
            if (!ok)
                ForskImportGuide.Failed(data, envelope?["message"]?.ToString());
            else if (dxf)
            {
                ForskImportGuide.ImportedDxf(data, ForskPlanImport.DxfNote(envelope).Split('\n').Select(row => row.Trim().TrimStart('·').Trim()));
                thread.Add(ForskReceipt.FromLine(ForskPlanImport.DxfLine(envelope)));
            }
            else
            {
                var result = envelope["result"] as JObject;
                ForskImportGuide.Imported(data, result);
                thread.AddReceipt(true, ForskText.Get("guide.subject"), ForskImportGuide.Found(data["found"] as JObject));
            }
            // The card is drawn again when the job finishes, from the file as the import left it.
        }

        /// <summary>
        /// The open guide drawn again from its data and the facts the window
        /// holds: after a job finishes (the facts are read again then), or a pill.
        /// </summary>
        void RefreshImportGuide(DocThread thread)
        {
            var card = thread?.Items.LastOrDefault(i => i["kind"]?.ToString() == ForskImportGuide.Kind && i["state"]?.ToString() == "open");
            if (card == null) return;
            var doc = RhinoDoc.ActiveDoc;
            ForskImportGuide.Paint(card, doc == null ? null : Facts(doc));
            Models.Persist(thread);
            Render();
        }
    }
}
