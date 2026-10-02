using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Eto.Forms;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// What the window does with a click, a shortcut, a card answer or a
    /// sentence. Every click checks its precondition again. A pill or an
    /// answer is one job: one undo record, a static step line, receipts, and
    /// the last action. A viewport pick is mirrored as a line in the thread and
    /// hands Rhino the keyboard.
    /// </summary>
    sealed partial class ForskWindow
    {
        bool _busy;
        bool _helpOpen;
        long _prefillCount;
        long _turn;

        static void Post(Action action)
        {
            Application.Instance.AsyncInvoke(action);
        }

        // ------------------------------------------------------------ the bar

        /// <summary>Cmd+1..3: that slot of the bar as it is drawn now.</summary>
        void Slot(int n)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null || n < 1) return;
            var slot = ForskRegistry.Bar(Facts(doc)).Slots.Skip(n - 1).FirstOrDefault();
            if (slot != null) Fire(slot.Id, false);
        }

        void Fire(string id, bool fromCard)
        {
            var doc = RhinoDoc.ActiveDoc;
            var thread = Active();
            var action = ForskRegistry.Find(id);
            if (doc == null || thread == null || action == null) return;
            // A click checks the precondition again. If it is no longer true, nothing runs and one line says so.
            var facts = ReadFacts(doc);
            if (!action.Shows(facts))
            {
                thread.Add("line", ForskText.Format("bar.refused", "label", action.Label));
                MarkDirty();
                Render();
                return;
            }
            if (fromCard) _helpOpen = false;
            switch (action.Runs)
            {
                case Runs.Prefill:
                    Prefill(thread, action, facts);
                    return;
                case Runs.Card:
                    OpenCard(thread, action, facts, doc);
                    return;
                case Runs.Ask:
                    if (Refuse(thread)) return;
                    Chat(thread, ForskText.Get(action.Id + ".ask"), action.Id);
                    return;
                default:
                    if (Refuse(thread)) return;
                    Run(thread, action, doc);
                    return;
            }
        }

        bool Refuse(DocThread thread)
        {
            if (!_busy) return false;
            thread.Add("line", ForskText.Get("line.busy"));
            Render();
            return true;
        }

        /// <summary>The sentence goes into the composer with the number selected. Nothing is sent.</summary>
        void Prefill(DocThread thread, ForskAction action, FileFacts facts)
        {
            var prefill = ForskPrefill.For(action.Id, facts, LastUserText(thread));
            if (prefill == null) return;
            thread.Prefill = prefill.ToJson(++_prefillCount);
            Render();
            FocusComposer();
        }

        static string LastUserText(DocThread thread)
        {
            for (var i = thread.Items.Count - 1; i >= 0; i--)
                if (thread.Items[i]["role"]?.ToString() == "user") return thread.Items[i]["text"]?.ToString();
            return null;
        }

        void Run(DocThread thread, ForskAction action, RhinoDoc doc)
        {
            var label = action.Label;
            switch (action.Id)
            {
                case "file.import":
                    Import(thread, action);
                    return;
                case "file.use_curves":
                    UseCurves(thread, action, doc);
                    return;
                case "file.scale":
                    Scale(thread, action);
                    return;
                case "file.generate":
                    Job(thread, action.Id, label, sink => Bake(sink, false));
                    return;
                case "file.rebuild":
                    Job(thread, action.Id, label, sink => Bake(sink, true));
                    return;
                case "file.print":
                    Print(thread, null);
                    return;
                case "file.draw":
                    Draw(thread, action, doc);
                    return;
                case "daylight.rooms":
                    Job(thread, action.Id, label, sink => Daylight(sink, DaylightAction.MakeRooms));
                    return;
                case "daylight.run":
                case "daylight.again":
                    Job(thread, action.Id, label, sink => Daylight(sink, DaylightAction.Run));
                    return;
                case "daylight.hide":
                    Job(thread, action.Id, label, sink => Daylight(sink, DaylightAction.Hide));
                    return;
                case "daylight.show":
                    Job(thread, action.Id, label, sink => Daylight(sink, DaylightAction.Show));
                    return;
                case "daylight.room":
                    Job(thread, action.Id, label, sink =>
                    {
                        var envelope = ForskDaylight.Run("selection", ForskTools.CommandOnUi);
                        sink.Line(ForskDaylight.Line(label, envelope));
                        sink.Say(ForskDaylight.Disclaimer(envelope));
                    });
                    return;
                case "section.add":
                    SectionPick(thread, label);
                    return;
                case "section.room":
                    var room = SelectedRoom(doc);
                    Job(thread, action.Id, label, sink => sink.Tool("section_add", new JObject { ["room"] = room }));
                    return;
                case "opening.delete":
                    Job(thread, action.Id, label, sink => sink.Tool("delete_opening", new JObject()));
                    return;
                case "wall.delete":
                    AskWallSide(thread);
                    return;
                case "exist.mark":
                    Job(thread, action.Id, label, sink => sink.Tool("mark_as_existing", new JObject()));
                    return;
                case "edit.undo":
                    Undo(thread, action, doc);
                    return;
            }
        }

        void OpenCard(DocThread thread, ForskAction action, FileFacts facts, RhinoDoc doc)
        {
            switch (action.Id)
            {
                case "help.card":
                    _helpOpen = true;
                    Render();
                    return;
                case "bridge.start":
                    StartBridge(thread);
                    return;
                default:
                    _helpOpen = true;
                    Render();
                    return;
            }
        }

        void CloseCard(string id)
        {
            if (id == "help")
            {
                _helpOpen = false;
                Render();
                return;
            }
            if (Active()?.Close(id) == true) Render();
        }

        /// <summary>A one-time card answered: the pill, and the field's value when the card has one.</summary>
        void Answer(string cardId, string pillId, string value)
        {
            var thread = Active();
            var card = thread?.Find(cardId);
            if (card == null) return;
            var kind = card["kind"]?.ToString();
            if (kind == "scale" && pillId == "set" && ParseMm(value) == null)
            {
                card["note"] = ForskText.Get("line.scale.number");
                Render();
                return;
            }
            var pill = thread.Answer(cardId, pillId);
            if (pill == null)
            {
                Render();
                return;
            }
            switch (kind)
            {
                case "scale":
                    if (pill.Id == "set")
                    {
                        var mm = ParseMm(value).Value;
                        card["answer"] = FormatMm(mm) + " mm";
                        var args = new JObject { ["p1"] = card["data"]?["p1"], ["p2"] = card["data"]?["p2"], ["length_mm"] = mm };
                        Job(thread, "file.scale", ForskText.Label("file.scale"), sink => sink.Tool(ForskPlanImport.ScaleTool, args), userText: FormatMm(mm) + " mm");
                    }
                    break;
                case "wall.delete":
                    if (pill.Id != "cancel")
                        Job(thread, "wall.delete", ForskText.Label("wall.delete"), sink => sink.Tool("delete_wall", new JObject { ["side"] = pill.Id }), userText: pill.Label);
                    break;
            }
            Models.Persist(thread);
            Render();
        }

        // ------------------------------------------------------------ chat

        void Send(string text)
        {
            var thread = Active();
            var doc = RhinoDoc.ActiveDoc;
            if (thread == null || doc == null || string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            // A slot's exact English label fires that slot.
            var hit = ForskRegistry.ByLabel(ForskRegistry.Bar(Facts(doc)), text);
            if (hit != null)
            {
                Fire(hit.Id, false);
                return;
            }
            if (Refuse(thread)) return;
            var toggle = BakeChip.MapToggle(text);
            if (toggle != DaylightAction.None)
            {
                var id = toggle == DaylightAction.Show ? "daylight.show" : "daylight.hide";
                Job(thread, id, ForskText.Label(id), sink => Daylight(sink, toggle), userText: text);
                return;
            }
            if (Sections.IsPickPhrase(text))
            {
                SectionPick(thread, text);
                return;
            }
            if (ForskPrint.IsRequest(text))
            {
                Print(thread, text);
                return;
            }
            Chat(thread, text, "answer");
        }

        /// <summary>
        /// One chat turn: one undo record, the dots only while the model call is
        /// in flight, a structured receipt per tool. The turn works on a copy of
        /// the history and hands it back at the end, so nothing reads it mid-turn.
        /// </summary>
        void Chat(DocThread thread, string text, string kind)
        {
            var doc = RhinoDoc.ActiveDoc;
            _busy = true;
            thread.Add("user", text);
            var target = ForskTarget.Read();
            var undo = ForskUndo.Begin(doc, text);
            _jobChanges = 0;
            var serial = doc?.RuntimeSerialNumber ?? 0;
            var history = new List<JObject>(thread.History);
            var hooks = new TurnHooks
            {
                Thinking = on => Post(() =>
                {
                    thread.Thinking = on;
                    Render();
                }),
                Tool = (name, envelope) => Post(() =>
                {
                    thread.Add(ForskReceipt.From(name, envelope));
                    Render();
                }),
                DialogParent = this
            };
            Render();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ForskGrok.RunTurn(text, target, history, (role, line) =>
                    {
                        // A tool's row came through hooks.Tool as a structured receipt.
                        if (role == "receipt") return;
                        Post(() =>
                        {
                            thread.Add("assistant", line);
                            Render();
                        });
                    }, hooks);
                }
                catch (Exception e)
                {
                    var message = ForskTools.Clip(e.Message);
                    Post(() => thread.Add("assistant", message));
                }
                Post(() =>
                {
                    thread.History.Clear();
                    thread.History.AddRange(history);
                    Finish(thread, kind, undo, serial, null, text);
                });
            });
        }

        // ------------------------------------------------------------ jobs

        /// <summary>The job's channel to the thread. Every call posts to the UI thread.</summary>
        sealed class JobSink
        {
            readonly ForskWindow _window;
            readonly DocThread _thread;

            public JobSink(ForskWindow window, DocThread thread)
            {
                _window = window;
                _thread = thread;
            }

            public void Step(string text)
            {
                Post(() =>
                {
                    _thread.Busy = text;
                    _window.Render();
                });
            }

            /// <summary>A "Label · ok · rest" line becomes a receipt; a plain sentence stays a line.</summary>
            public void Line(string line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                Post(() =>
                {
                    if (line.Contains(" · ")) _thread.Add(ForskReceipt.FromLine(line));
                    else _thread.Add("line", line);
                    _window.Render();
                });
            }

            public void Say(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                Post(() =>
                {
                    _thread.Add("assistant", text);
                    _window.Render();
                });
            }

            /// <summary>One tool, on the UI thread, and its structured receipt.</summary>
            public JObject Tool(string name, JObject args)
            {
                JObject envelope = null;
                RhinoApp.InvokeOnUiThread(new Action(() => envelope = ForskTools.ExecuteAllowed(name, args)));
                envelope = envelope ?? ForskTools.Fail("No result");
                Post(() =>
                {
                    _thread.Add(ForskReceipt.From(name, envelope));
                    _window.Render();
                });
                return envelope;
            }
        }

        /// <summary>
        /// A pill's work, off the UI thread, inside one undo record named after
        /// the pill. The step line stays on screen; the longest UI stall is logged.
        /// </summary>
        void Job(DocThread thread, string kind, string label, Action<JobSink> work, string userText = null, bool ownRecord = true)
        {
            var doc = RhinoDoc.ActiveDoc;
            _busy = true;
            thread.Add("user", userText ?? label);
            thread.Busy = ForskText.Format("line.running", "what", label);
            var undo = ownRecord ? ForskUndo.Begin(doc, label) : null;
            _jobChanges = 0;
            var serial = doc?.RuntimeSerialNumber ?? 0;
            var stall = StallWatch.Start();
            Render();
            var sink = new JobSink(this, thread);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // The first tool takes the UI thread. Let the step line paint first.
                Thread.Sleep(50);
                try
                {
                    work(sink);
                }
                catch (Exception e)
                {
                    sink.Line(label + " · error · " + ForskTools.Clip(e.Message));
                }
                var longest = stall.Stop();
                Post(() => Finish(thread, kind, undo, serial, longest, label));
            });
        }

        void Finish(DocThread thread, string kind, ForskUndo undo, uint serial, long? longest, string label)
        {
            undo?.End();
            Tracker.Record(new LastAction
            {
                Kind = kind,
                Doc = serial,
                Turn = ++_turn,
                Record = undo?.Name,
                Undoable = _jobChanges > 0
            });
            Log(label + " · " + _jobChanges + " change(s)"
                + (longest.HasValue ? " · longest UI stall " + longest.Value + " ms"
                    + (longest.Value >= FrozenMs ? " · the page could not repaint, the shimmer was frozen" : " · the shimmer kept moving") : ""));
            thread.Busy = null;
            thread.Thinking = false;
            _busy = false;
            Models.Persist(thread);
            MarkDirty();
            Render();
        }

        static void Bake(JobSink sink, bool rebuild)
        {
            var lines = ForskBake.Run(rebuild, (i, n) => sink.Step(ForskText.Format("line.generating",
                "i", i.ToString(CultureInfo.InvariantCulture), "n", n.ToString(CultureInfo.InvariantCulture))));
            foreach (var line in lines)
                sink.Line(line);
        }

        static void Daylight(JobSink sink, DaylightAction action)
        {
            ForskDaylight.Chip(action, out var line, out var note);
            sink.Line(line);
            sink.Say(note);
        }

        void Print(DocThread thread, string userText)
        {
            var label = ForskText.Label("file.print");
            Job(thread, "file.print", label, sink =>
            {
                sink.Step(ForskText.Format("line.printing", "i", "1", "n", "2", "what", ForskText.Get("line.printing.layout")));
                var line = ForskPrint.Run(status => sink.Step(ForskText.Format("line.printing", "i", "2", "n", "2", "what", status)), this);
                sink.Line(line);
            }, userText: userText);
        }

        /// <summary>The import dialog, parented to this window, then plan_import or dxf_import.</summary>
        void Import(DocThread thread, ForskAction action)
        {
            var source = ForskPlanImport.PickSource(this);
            if (source == null) return;
            Job(thread, action.Id, action.Label, sink =>
            {
                ForskPlanImport.Chip(ImportAction.ImportPlan, source, out var line, out var note);
                sink.Line(line);
                sink.Say(note);
            });
        }

        /// <summary>Two points in the view, then the length in a Forsk field.</summary>
        void Scale(DocThread thread, ForskAction action)
        {
            thread.Add("user", action.Label);
            thread.Add("line", ForskText.Get("prompt.scale"));
            Render();
            HandToRhino();
            _busy = true;
            JObject pick;
            try
            {
                pick = ForskPlanImport.PickScalePoints();
            }
            finally
            {
                _busy = false;
            }
            if (pick == null)
                thread.Add("line", ForskText.Get("line.scale.cancelled"));
            else if (pick["status"] != null)
                thread.Add(ForskReceipt.From(ForskPlanImport.ScaleTool, pick));
            else
            {
                var measured = pick["measured_mm"]?.ToObject<double>() ?? 0;
                var card = thread.AddCard("scale", ForskText.Get("prompt.scale.length"),
                    new CardPill("set", ForskText.Get("prompt.scale.set")),
                    new CardPill("cancel", ForskText.Get("word.cancel")));
                card["field"] = new JObject { ["value"] = FormatMm(measured), ["unit"] = "mm" };
                card["data"] = new JObject { ["p1"] = pick["p1"], ["p2"] = pick["p2"] };
            }
            Models.Persist(thread);
            MarkDirty();
            Render();
            TakeKeyboard();
        }

        /// <summary>The wall layer made current, created when missing, then Rhino's Polyline in the view.</summary>
        void Draw(DocThread thread, ForskAction action, RhinoDoc doc)
        {
            thread.Add("user", action.Label);
            thread.Add("line", ForskText.Get("prompt.draw"));
            Render();
            ForskCalls.Enter();
            try
            {
                var index = doc.Layers.FindByFullPath("wall", -1);
                if (index < 0) index = doc.Layers.Add("wall", System.Drawing.Color.FromArgb(30, 30, 30));
                if (index >= 0) doc.Layers.SetCurrentLayerIndex(index, true);
            }
            finally
            {
                ForskCalls.Exit();
            }
            HandToRhino();
            _busy = true;
            try
            {
                RhinoApp.RunScript("_Polyline", false);
            }
            finally
            {
                _busy = false;
            }
            Models.Persist(thread);
            MarkDirty();
            Render();
        }

        /// <summary>The selected curves go onto the wall layer. With none selected, one line asks for them.</summary>
        void UseCurves(DocThread thread, ForskAction action, RhinoDoc doc)
        {
            thread.Add("user", action.Label);
            var curves = RhinoMCPFunctions.ListSelected(doc).Where(o => o?.Geometry is Curve).ToList();
            if (curves.Count == 0)
            {
                thread.Add("line", ForskText.Get("file.use_curves.ask"));
                Render();
                return;
            }
            var undo = ForskUndo.Begin(doc, action.Label);
            _jobChanges = 0;
            ForskCalls.Enter();
            try
            {
                var index = doc.Layers.FindByFullPath("wall", -1);
                if (index < 0) index = doc.Layers.Add("wall", System.Drawing.Color.FromArgb(30, 30, 30));
                foreach (var curve in curves)
                {
                    var attr = curve.Attributes.Duplicate();
                    attr.LayerIndex = index;
                    doc.Objects.ModifyAttributes(curve, attr, true);
                }
                doc.Views.Redraw();
            }
            finally
            {
                ForskCalls.Exit();
            }
            thread.AddReceipt(true, ForskReceipt.StepLabel("plan_import"),
                ForskText.Format("file.use_curves.done", "n", curves.Count.ToString(CultureInfo.InvariantCulture)));
            Finish(thread, action.Id, undo, doc.RuntimeSerialNumber, null, action.Label);
        }

        /// <summary>The section line is dragged in the view; its name comes from a dialog parented to this window.</summary>
        void SectionPick(DocThread thread, string userText)
        {
            if (Refuse(thread)) return;
            var doc = RhinoDoc.ActiveDoc;
            thread.Add("user", userText);
            thread.Add("line", ForskText.Get("prompt.section"));
            Render();
            HandToRhino();
            _busy = true;
            _jobChanges = 0;
            JObject envelope;
            try
            {
                envelope = ForskSection.RunOnUi(true, this);
            }
            finally
            {
                _busy = false;
            }
            thread.Add(ForskReceipt.From("section_pick", envelope));
            // ForskSection kept its own single record.
            Finish(thread, "section.add", null, doc?.RuntimeSerialNumber ?? 0, null, userText);
            TakeKeyboard();
        }

        static string SelectedRoom(RhinoDoc doc)
        {
            foreach (var obj in RhinoMCPFunctions.ListSelected(doc))
            {
                var attr = obj?.Attributes;
                if (attr == null || attr.GetUserString("forsk:kind") != "room") continue;
                return attr.GetUserString("forsk:room_name") ?? attr.GetUserString("forsk:room_id") ?? obj.Name;
            }
            return null;
        }

        void AskWallSide(DocThread thread)
        {
            thread.Add("user", ForskText.Label("wall.delete"));
            var card = thread.AddCard("wall.delete", ForskText.Get("wall.delete.ask"),
                new CardPill("north", ForskText.Get("word.north")),
                new CardPill("south", ForskText.Get("word.south")),
                new CardPill("east", ForskText.Get("word.east")),
                new CardPill("west", ForskText.Get("word.west")),
                new CardPill("cancel", ForskText.Get("word.cancel")));
            card["note"] = ForskText.Get("wall.delete.inner");
            Render();
        }

        /// <summary>The one Forsk record, while it is the newest thing in the document.</summary>
        void Undo(DocThread thread, ForskAction action, RhinoDoc doc)
        {
            thread.Add("user", action.Label);
            var record = Tracker.Current?.Record;
            bool undone;
            ForskCalls.Enter();
            try
            {
                undone = doc.Undo();
            }
            finally
            {
                ForskCalls.Exit();
            }
            Log("undo: " + (record ?? "?") + " · " + undone);
            thread.AddReceipt(undone, action.Label, undone ? record ?? "" : ForskText.Get("edit.undo.none"));
            Models.Persist(thread);
            MarkDirty();
            Render();
        }

        void StartBridge(DocThread thread)
        {
            thread.Add("user", ForskText.Label("bridge.start"));
            try
            {
                RhinoMCPServerController.StartServer();
                thread.AddReceipt(true, ForskText.Label("bridge.start"), ForskText.Get("line.bridge"));
            }
            catch (Exception e)
            {
                thread.AddReceipt(false, ForskText.Label("bridge.start"), ForskText.Format("bridge.start.failed", "why", ForskTools.Clip(e.Message)));
            }
            Poll(force: true);
            Models.Persist(thread);
            Render();
        }

        /// <summary>After a pick in the view, the Forsk window takes the keyboard back for the card it opened.</summary>
        void TakeKeyboard()
        {
            Application.Instance.AsyncInvoke(() =>
            {
                try
                {
                    Focus();
                    _web.Focus();
                }
                catch (Exception e)
                {
                    Log("focus " + e.Message);
                }
            });
        }

        /// <summary>A length typed in a Forsk field: 4000, 4 000, 4000,5 or 4000.5. Null when it is not a positive number.</summary>
        internal static double? ParseMm(string text)
        {
            var t = (text ?? "").Replace(" ", "").Replace(" ", "").Replace("mm", "").Trim().Replace(',', '.');
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            return value > 0 && !double.IsInfinity(value) ? value : (double?)null;
        }

        static string FormatMm(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
