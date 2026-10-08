using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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

        // ------------------------------------------------------------ the role

        /// <summary>The header's role control: a pick is an override until Auto clears it. Slot 1 does not read it.</summary>
        void PickRole(string id)
        {
            var thread = Active();
            if (thread == null) return;
            var pickedRole = ForskRoles.Parse(id);
            if (pickedRole != thread.Override)
                NoteMisroute(ForskMisroutes.Hand(ForskMisroutes.AnsweredSentence(thread.Items), PickedNow(), pickedRole, DateTimeOffset.UtcNow));
            thread.Override = pickedRole;
            Log("role: " + (thread.Override == ForskRole.None ? "auto (the router names each answer)" : thread.Override + " (override)"));
            Render();
        }

        static Picked PickedNow()
        {
            var doc = RhinoDoc.ActiveDoc;
            return doc == null ? Picked.None : ReadFacts(doc).Picked;
        }

        /// <summary>One correction line. A full disk is not a reason to drop the turn.</summary>
        static void NoteMisroute(JObject entry)
        {
            if (entry == null) return;
            try
            {
                ForskMisroutes.Append(entry, ForskMisroutes.DefaultPath);
            }
            catch (Exception e)
            {
                Log("misroute: " + e.GetType().Name);
            }
        }

        /// <summary>A typed turn's mark: the override when one is set, else the router's role for this sentence.</summary>
        static string TurnMark(DocThread thread, string text)
        {
            var doc = RhinoDoc.ActiveDoc;
            return ForskRoles.Mark(ForskIntentRouter.Classify(text, doc == null ? Picked.None : ReadFacts(doc).Picked), thread.Override);
        }

        // ------------------------------------------------------------ the bar

        /// <summary>The bar as the page draws it: the same facts and the same role boost.</summary>
        BarView Drawn(RhinoDoc doc)
        {
            return ForskRegistry.Bar(Facts(doc), Active()?.Override ?? ForskRole.None);
        }

        /// <summary>Cmd+1..4: that slot of the bar as it is drawn now.</summary>
        void Slot(int n)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null || n < 1) return;
            var slot = Drawn(doc).Slots.Skip(n - 1).FirstOrDefault();
            if (slot != null) Fire(slot.Id, false);
        }

        void Fire(string id, bool fromCard)
        {
            if (id == ForskDebug.MenuId)
            {
                CopyDebugReport(RhinoDoc.ActiveDoc);
                return;
            }
            if (id == ForskWhatsNew.MenuId)
            {
                ReleaseNotes();
                return;
            }
            if (id == FirstRun.DismissId)
            {
                FirstRunGate.Dismiss();
                MarkDirty();
                Render();
                return;
            }
            var doc = RhinoDoc.ActiveDoc;
            var thread = Active();
            if (id == ForskKeyFile.MenuId)
            {
                if (thread != null) KeyCard(thread, ForskText.Get(id), null);
                return;
            }
            if (id == ForskSetup.MenuId)
            {
                if (thread != null) SetupCard(thread, ForskText.Get(id), null);
                return;
            }
            if (id == ForskUpdate.MenuId)
            {
                if (thread != null) UpdateCard(thread);
                return;
            }
            var action = ForskRegistry.Find(id);
            if (doc == null || thread == null || action == null) return;
            // A click checks the precondition again. If it is no longer true, nothing runs and one line says so.
            // The facts the window holds (read again only when the document changed): a click must not wait on a full read.
            var facts = Facts(doc);
            if (!action.Shows(facts))
            {
                thread.AddLine(ForskText.Format("bar.refused", "label", action.Label));
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
                    Run(thread, action, facts, doc);
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
            var prefill = ForskPrefill.For(action.Id, facts, thread.LastUserText());
            if (prefill == null) return;
            thread.Prefill = prefill.ToJson(++_prefillCount);
            Render();
            FocusComposer();
        }

        void Run(DocThread thread, ForskAction action, FileFacts facts, RhinoDoc doc)
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
                case "plan.show":
                case "plan.hide":
                    Job(thread, action.Id, label, sink =>
                    {
                        var line = "";
                        RhinoApp.InvokeOnUiThread(new Action(() => line = PlanLayerHost.Toggle(RhinoDoc.ActiveDoc)));
                        sink.Line(line);
                    });
                    return;
                case "file.print":
                    Print(thread, null);
                    return;
                case "export.dwg":
                    Export(thread, null, "dwg");
                    return;
                case "export.ifc":
                    Export(thread, null, "ifc");
                    return;
                case "view.export":
                    ExportViewport(thread);
                    return;
                case "export.csv":
                    Export(thread, null, "csv");
                    return;
                case "detail.add":
                    Job(thread, action.Id, label, sink => sink.Tool("details", new JObject { ["action"] = "add" }));
                    return;
                case "file.sample":
                    OpenSample(thread, action, doc);
                    return;
                case "file.draw":
                    Draw(thread, action, doc);
                    return;
                case "daylight.rooms":
                    Job(thread, action.Id, label, sink => Daylight(sink, DaylightAction.MakeRooms));
                    return;
                case "area.stats":
                    Job(thread, action.Id, label, sink => sink.Tool("area_stats", new JObject()));
                    return;
                case "option.save":
                    Job(thread, action.Id, label, sink => sink.Tool("save_option", new JObject()));
                    return;
                case "analysis.add":
                    // AN.3: the analysis that just ran goes into the Analysis set, on the file.
                    var added = facts.Analysed;
                    if (added == null) return;
                    RhinoMCPFunctions.WriteAnalysis(doc, Functions.Analysis.SetKey(added), "1");
                    Job(thread, action.Id, label, sink => sink.Line(ForskText.Format("analysis.added", "name", ForskText.Get("analysis." + added))));
                    return;
                case "takeoff":
                    Job(thread, action.Id, label, sink => sink.Tool(ForskToolPacks.TakeoffTool, new JObject()));
                    return;
                case "daylight.run":
                case "daylight.again":
                    if (AskSetup(thread, label, ForskDaylight.NeedsSetup())) return;
                    Job(thread, action.Id, label, sink => Daylight(sink, DaylightAction.Run));
                    return;
                case "daylight.hide":
                    Job(thread, action.Id, label, sink => Daylight(sink, DaylightAction.Hide));
                    return;
                case "daylight.show":
                    Job(thread, action.Id, label, sink => Daylight(sink, DaylightAction.Show));
                    return;
                case "daylight.room":
                    if (AskSetup(thread, label, ForskDaylight.NeedsSetup())) return;
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
                    // A wall of one run is the run: no question.
                    if (ForskPick.OneRunWall(facts.Selected) != null)
                        Job(thread, action.Id, label, sink => sink.Tool("delete_wall", new JObject()));
                    else
                        AskWallSide(thread, facts);
                    return;
                case "wall.split":
                    Job(thread, action.Id, label, sink => sink.Tool("split_walls", new JObject()));
                    return;
                case "wall.drag":
                    DragWall(thread);
                    return;
                case "wall.draw":
                    DrawPick(thread, label, "wall.draw", "prompt.wall", "add_wall", ForskDrawWall.RunOnUi);
                    return;
                case "room.draw":
                case "room.redraw":
                    var redraw = action.Id == "room.redraw";
                    DrawPick(thread, label, action.Id, "prompt.area", "add_room_area", own => ForskDrawArea.RunOnUi(own, redraw));
                    return;
                case "exist.mark":
                    Job(thread, action.Id, label, sink => sink.Tool("mark_as_existing", new JObject()));
                    return;
                case "stair.add":
                    Job(thread, action.Id, label, sink => sink.Tool("add_stair", new JObject { ["along_wall"] = true }));
                    return;
                case "furniture.furnish":
                    // The picked room, else every room: both layouts as ghosts, and the card to place one.
                    var picked = facts.Picked == Picked.Room;
                    var furnish = new JObject { ["preview"] = true };
                    if (!picked) furnish["room"] = "all";
                    Job(thread, action.Id, label, sink => sink.Tool("furnish_room", furnish));
                    return;
                case "stair.draw":
                    DrawPick(thread, label, "stair.draw", "prompt.stair", "add_stair", ForskStair.RunOnUi);
                    return;
                case "stair.delete":
                    Job(thread, action.Id, label, sink => sink.Tool("delete_stair", new JObject()));
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
                    // Interior render with no room picked asks for one, waits for the click, then opens itself (Julian, 2026-10-07).
                    var needsPick = action.Id == "room.inside" ? ForskCards.JumpInsideNeedsPick(facts) : null;
                    if (needsPick != null)
                    {
                        PickRoomThenJump(thread, action, needsPick);
                        return;
                    }
                    var spec = ForskCards.For(action.Id, facts);
                    if (spec == null) return;
                    thread.Add("user", action.Label);
                    thread.BeginReply(ForskRoles.MarkForAction(action.Id));
                    thread.AddCard(spec, facts);
                    thread.EndReply();
                    Models.Persist(thread);
                    // Interior render and Exterior render show north as their card opens (Julian, 2026-10-07).
                    if (action.Id == "room.inside") TryInside("north");
                    if (action.Id == "view.exterior") TryOutside("north");
                    Render();
                    return;
            }
        }

        /// <summary>AN.1: the Analyser's face opens the analyses card, as a card action does.</summary>
        void AnalyserCard()
        {
            var doc = RhinoDoc.ActiveDoc;
            var thread = Active();
            if (doc == null || thread == null) return;
            var facts = Facts(doc);
            thread.Add("user", ForskText.Get("analyser.title"));
            thread.BeginReply(ForskRoles.Label(ForskRole.Analyser));
            thread.AddCard(ForskCards.Analyser(facts), facts);
            thread.EndReply();
            Models.Persist(thread);
            Render();
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

        /// <summary>
        /// A one-time card answered once: the pill, and its fields' values when
        /// it has fields, and their order when its rows move. A stale, answered
        /// or closed card does nothing.
        /// </summary>
        void Answer(string cardId, string pillId, JObject values, JArray order = null)
        {
            var thread = Active();
            var card = thread?.Find(cardId);
            if (card == null) return;
            // A click checks again: a card whose selection or model moved since it opened is grey now, even before idle.
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null && thread.StaleCards(ReadFacts(doc)) > 0) Log("cards: went stale at the click");
            var kind = card["kind"]?.ToString();
            var length = values?["length"]?.ToString();
            if (kind == "scale" && pillId == "set" && ParseMm(length) == null && card["state"]?.ToString() == "open")
            {
                card["note"] = ForskText.Get("line.scale.number");
                Render();
                return;
            }
            // Set up, the key card and Get a key keep the Set up Forsk card open; they run while a job does.
            if (kind == ForskSetup.MenuId && pillId != "done" && card["state"]?.ToString() == "open")
            {
                if ((card["pills"] as JArray ?? new JArray()).Any(p => p["id"]?.ToString() == pillId)) SetupPill(thread, pillId);
                return;
            }
            // Open Package Manager and How to update keep the update card open. They run while a job does.
            if (kind == ForskUpdate.MenuId && pillId != "done" && card["state"]?.ToString() == "open")
            {
                if ((card["pills"] as JArray ?? new JArray()).Any(p => p["id"]?.ToString() == pillId)) UpdatePill(pillId);
                return;
            }
            if (pillId != "cancel" && pillId != "done" && Refuse(thread)) return;
            // A missing reply address keeps the card open and shows the field. Send does not close it in silence.
            if (kind == "support.report" && pillId == "send" && card["state"]?.ToString() == "open")
            {
                KeepTyped(card, values);
                var description = Typed(card, "description");
                var sample = string.IsNullOrWhiteSpace(description) ? thread.LastUserText() : description;
                var note = ForskSupport.Hold(Typed(card, "type"), description, Typed(card, "email"), ForskPrefill.Language(sample));
                if (note != null)
                {
                    ForskReports.EnsureEmailField(card);
                    card["note"] = note;
                    Render();
                    return;
                }
            }
            // A key that is empty or has a space keeps the card open with the reason. The key is never kept on the card.
            if (kind == ForskKeyFile.MenuId && pillId == "save" && card["state"]?.ToString() == "open"
                && ForskKeyFile.Clean(values?[ForskKeyFile.FieldKey]?.ToString(), out var keyReason) == null)
            {
                card["note"] = keyReason;
                Render();
                return;
            }
            // A choice card (Julian, 2026-10-07): an option applies at once and keeps the card open;
            // Confirm closes it with no chat message.
            if (card["state"]?.ToString() == "open" && ForskCards.IsChoiceOption(card, pillId))
            {
                ForskCards.HoldChoice(card, pillId, ApplyChoice(kind, card, pillId));
                Models.Persist(thread);
                Render();
                return;
            }
            if (card["state"]?.ToString() == "open" && card["choice"] != null && pillId == "done")
            {
                var held = ForskCards.HeldChoice(card);
                ConfirmChoice(kind, held);
                var heldLabel = (card["pills"] as JArray ?? new JArray()).FirstOrDefault(p => p["id"]?.ToString() == held)?["label"]?.ToString();
                thread.Settle(cardId, heldLabel ?? ForskText.Get("word.unchanged"));
                Models.Persist(thread);
                Render();
                return;
            }
            // Show in Finder opens the folder and closes Export viewport's receipt, as Done does (Julian, 2026-10-08).
            if (kind == "view.exported" && pillId == "reveal")
            {
                RevealInFinder(card["data"]?["path"]?.ToString());
                thread.Close(cardId);
                Models.Persist(thread);
                Render();
                return;
            }
            // Choose logo and Remove logo keep the Project info card open with the change shown. Save keeps it.
            if (kind == "meta.title" && (pillId == "logo" || pillId == "logo_remove") && card["state"]?.ToString() == "open")
            {
                KeepTyped(card, values);
                if (pillId == "logo_remove") ForskCards.HoldLogoRemoval(card);
                else HoldPickedLogo(card);
                Render();
                return;
            }
            var pill = thread.Answer(cardId, pillId);
            card.Remove("image");
            if (pill == null)
            {
                Render();
                return;
            }
            // AN.2: the menu's Live switches are kept on the file whichever pill closes it.
            if (kind == "analyser" && pill.Id != "cancel") SaveLive(values);
            // The furnish ghosts go with their card, placed or not.
            if (kind == "furnish.pick") FurnishPreview.Hide(RhinoDoc.ActiveDoc);
            if (pill.Id == "cancel" || pill.Id == "done")
            {
                Models.Persist(thread);
                Render();
                return;
            }
            // A form's receipt is the whole answered line. The pill is not also posted as its own "Save".
            var receipt = ForskCards.FormReceipt(kind, pill.Id, values);
            if (receipt != null) card["receipt"] = receipt;
            switch (kind)
            {
                case "analyser":
                case "options":
                    // The pill is the action's id: it runs as the bar's pill does, with the same checks.
                    Models.Persist(thread);
                    Fire(pill.Id, true);
                    return;
                case "scale":
                    var mm = ParseMm(length).Value;
                    card["answer"] = FormatMm(mm) + " mm";
                    var scaleArgs = new JObject { ["p1"] = card["data"]?["p1"], ["p2"] = card["data"]?["p2"], ["length_mm"] = mm };
                    Job(thread, "file.scale", ForskText.Label("file.scale"), sink => sink.Tool(ForskPlanImport.ScaleTool, scaleArgs), userText: FormatMm(mm) + " mm");
                    break;
                case "furnish.pick":
                    // The layout the ghost showed: the same rules give the same pieces.
                    var furnishArgs = new JObject { ["variant"] = pill.Id };
                    foreach (var key in new[] { "room", "density", "replace" })
                        if (card["data"]?[key] != null && card["data"][key].Type != JTokenType.Null) furnishArgs[key] = card["data"][key];
                    Job(thread, kind, ForskText.Get("tool.furnish_room"), sink => sink.Tool("furnish_room", furnishArgs), userText: pill.Label);
                    break;
                case "wall.delete":
                    Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("delete_wall", new JObject { ["side"] = pill.Id }), userText: pill.Label);
                    break;
                case "meta.title":
                    var meta = new JObject();
                    foreach (var field in card["fields"] as JArray ?? new JArray())
                    {
                        var key = field["key"]?.ToString();
                        if (string.IsNullOrEmpty(key)) continue;
                        var typed = values?[key]?.ToString() ?? "";
                        field["value"] = typed;
                        meta[key] = typed.Trim();
                    }
                    var save = pill.Id == "save";
                    if (save) ForskCards.LogoMeta(card, meta);
                    var logoPill = meta["logo_path"] != null || meta["logo"] != null;
                    if (save)
                    {
                        // Today shown in the field is the print day, not a date the user typed.
                        var previousDate = doc?.Strings.GetValue(ProjectInfo.Section, ProjectInfo.Date);
                        meta[ProjectInfo.Date] = ProjectInfo.DateToStore(meta[ProjectInfo.Date]?.ToString(), previousDate, DateTime.Now);
                        ForskPrint.SaveFirmArchitect(meta[ProjectInfo.Architect]?.ToString());
                    }
                    var pending = card["data"]?["pending"]?.ToString();
                    if (string.IsNullOrEmpty(pending))
                    {
                        Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("set_project_meta", meta, quiet: !logoPill), userText: receipt ?? pill.Label, noteUser: receipt == null);
                        break;
                    }
                    // Asked once: either pill, then the action the card stood in front of.
                    doc?.Strings.SetString(ProjectInfo.Section, ProjectInfo.AskedKey, "1");
                    var pendingView = card["data"]?["view"]?.ToString();
                    var pendingId = PendingJobId(pending);
                    Job(thread, pendingId, ForskText.Label(pendingId), sink =>
                    {
                        if (save) sink.Tool("set_project_meta", meta, quiet: true);
                        RunPending(sink, pending, pendingView);
                    }, userText: receipt ?? pill.Label, noteUser: receipt == null);
                    break;
                case "print.one":
                    Print(thread, pill.Label, pill.Id);
                    break;
                case "print.pages":
                    var pagesArgs = ForskCards.PagesArgs(pill.Id, values, order);
                    if (pill.Id == "export_ifc")
                        Export(thread, receipt ?? pill.Label, "ifc", noteUser: receipt == null);
                    else if (pill.Id == "export_csv")
                        Export(thread, receipt ?? pill.Label, "csv", noteUser: receipt == null);
                    else if (pill.Id == "export")
                        Job(thread, "export.dwg", ForskText.Label("export.dwg"), sink =>
                        {
                            sink.Tool("print_pages", pagesArgs);
                            sink.Line(ForskPrint.Export(sink.Step, this, "dwg"));
                        }, userText: receipt ?? pill.Label, noteUser: receipt == null);
                    else if (pill.Id == "print")
                        Job(thread, kind, ForskText.Label(kind), sink =>
                        {
                            sink.Tool("print_pages", pagesArgs);
                            sink.Step(ForskText.Format("line.printing", "i", "1", "n", "2", "what", ForskText.Get("line.printing.layout")));
                            sink.Line(ForskPrint.Run(status => sink.Step(ForskText.Format("line.printing", "i", "2", "n", "2", "what", status)), this, null));
                        }, userText: receipt ?? pill.Label, noteUser: receipt == null);
                    else
                        Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("print_pages", pagesArgs), userText: receipt ?? pill.Label, noteUser: receipt == null);
                    break;
                case "print.clear":
                    Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("clear_layouts", new JObject()), userText: pill.Label);
                    break;
                case "option.restore":
                case "option.delete":
                    var picked = pill.Id;
                    Job(thread, kind, ForskText.Label(kind), sink => sink.Tool(kind == "option.restore" ? "restore_option" : "delete_option", new JObject { ["name"] = picked }), userText: pill.Label);
                    break;
                case "option.compare":
                    var optionName = pill.Id;
                    Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("compare_option", new JObject { ["name"] = optionName }), userText: pill.Label);
                    break;
                case "analysis.print":
                    // AN.4: the ticks are kept on the file; Print then writes the Analysis set's own PDF.
                    var setNames = SaveAnalysisSet(values);
                    if (pill.Id == "save" || setNames.Count == 0)
                        Job(thread, kind, ForskText.Label(kind), sink => sink.Line(setNames.Count == 0
                            ? ForskText.Get("analysis.set.empty")
                            : ForskText.Format("analysis.saved", "names", string.Join(", ", setNames))), userText: pill.Label);
                    else
                        Job(thread, kind, ForskText.Label(kind), sink =>
                        {
                            sink.Step(ForskText.Format("line.printing", "i", "1", "n", "2", "what", ForskText.Get("line.printing.layout")));
                            sink.Line(ForskPrint.RunAnalysis(status => sink.Step(ForskText.Format("line.printing", "i", "2", "n", "2", "what", status)), this));
                        }, userText: pill.Label);
                    break;
                case "detail.list":
                    var unticked = ForskCards.Unticked(values);
                    if (pill.Id == "save" && unticked.Count == 0)
                        Job(thread, kind, ForskText.Label(kind), sink => sink.Line("Kept every detail."), userText: pill.Label);
                    else
                    {
                        var detailArgs = new JObject { ["action"] = "remove" };
                        if (pill.Id == "save") detailArgs["ids"] = unticked;
                        Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("details", detailArgs), userText: pill.Label);
                    }
                    break;
                case "sheets.clear":
                    Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("clear_drawings", new JObject()), userText: pill.Label);
                    break;
                case "furniture.add":
                    Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("add_furniture", ForskCards.AddFurnitureArgs(values)), userText: values?["item"]?.ToString() ?? pill.Label);
                    break;
                case "stair.edit":
                    var stairArgs = ForskCards.StairArgs(pill.Id, values, card["fields"] as JArray);
                    foreach (var field in card["fields"] as JArray ?? new JArray())
                    {
                        var key = field["key"]?.ToString();
                        if (!string.IsNullOrEmpty(key) && values?[key] != null) field["value"] = values[key].ToString();
                    }
                    if (stairArgs == null)
                        card["receipt"] = ForskText.Get("stair.same");
                    else
                        Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("edit_stair", stairArgs), userText: receipt ?? pill.Label, noteUser: receipt == null);
                    break;
                case "section.remove":
                    var sectionArgs = pill.Id == "all" ? new JObject() : new JObject { ["letter"] = pill.Id };
                    Job(thread, kind, ForskText.Label(kind), sink => sink.Tool("section_clear", sectionArgs), userText: pill.Label);
                    break;
                case "pdf.page":
                    var page = int.Parse(pill.Id, CultureInfo.InvariantCulture);
                    ImportJob(thread, new JObject { ["pdf_path"] = card["data"]?["pdf_path"], ["page"] = page }, pill.Label);
                    break;
                case "support.report":
                    SendReport(thread, card, values, doc);
                    break;
                case ForskKeyFile.MenuId:
                    SaveKey(card, pill.Id, values);
                    break;
            }
            Models.Persist(thread);
            Render();
        }

        /// <summary>
        /// The Grok key card from Settings, or after a chat turn with no key
        /// (line says why). An older open key card closes, so one is pinned.
        /// </summary>
        void KeyCard(DocThread thread, string userText, string line)
        {
            foreach (var item in thread.Items)
                if (item["kind"]?.ToString() == ForskKeyFile.MenuId) thread.Close(item["id"]?.ToString());
            thread.Add("user", userText);
            thread.BeginReply(null);
            if (line != null) thread.Add("assistant", line);
            thread.AddCard(ForskCards.GrokKey(ForskKeys.Load(), ForskKeyFile.Stored(ForskKeyFile.DefaultPath)), null);
            thread.EndReply();
            Models.Persist(thread);
            Render();
        }

        /// <summary>Save or Remove on the key card: ~/.forsk/grok.env changes, and chat shows or hides at once. The receipt never holds the key.</summary>
        void SaveKey(JObject card, string pillId, JObject values)
        {
            try
            {
                if (pillId == "save")
                    ForskKeyFile.Save(ForskKeyFile.DefaultPath, ForskKeyFile.Clean(values?[ForskKeyFile.FieldKey]?.ToString(), out _));
                else if (pillId == "remove")
                    ForskKeyFile.Remove(ForskKeyFile.DefaultPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                card["receipt"] = ForskText.Format("grok.key.failed", "reason", e.Message);
                Log("grok key · " + e.GetType().Name);
            }
            Poll(true);
            RefreshSetup();
        }

        /// <summary>Daylight and AI detection's uv, this session. Null until first looked at.</summary>
        static SetupState _setup;

        /// <summary>The uv row as of now: the install under way, else what the disk says.</summary>
        static SetupState Tools()
        {
            var found = ForskUv.Uv() != null;
            _setup = (_setup ?? SetupState.From(found)).Seen(found);
            return _setup;
        }

        /// <summary>
        /// Settings → Set up Forsk, the first-run button, or daylight or an
        /// import with no uv (line says why). An older open setup card closes,
        /// so one is pinned.
        /// </summary>
        void SetupCard(DocThread thread, string userText, string line)
        {
            foreach (var item in thread.Items)
                if (item["kind"]?.ToString() == ForskSetup.MenuId) thread.Close(item["id"]?.ToString());
            thread.Add("user", userText);
            thread.BeginReply(null);
            if (line != null) thread.Add("assistant", line);
            thread.AddCard(ForskCards.Setup(ForskKeys.Load(), Tools(), Account()), null);
            thread.EndReply();
            Models.Persist(thread);
            Render();
        }

        /// <summary>Daylight or AI detection with no uv: one line and the Set up Forsk card instead of an error.</summary>
        bool AskSetup(DocThread thread, string userText, bool needed)
        {
            if (!needed) return false;
            SetupCard(thread, userText, ForskText.Get("setup.needed"));
            return true;
        }

        /// <summary>The open setup card in the active thread shows the key and the uv row as they are now, and the page redraws.</summary>
        void RefreshSetup()
        {
            var thread = Active();
            if (thread != null && thread.Refresh(ForskCards.Setup(ForskKeys.Load(), Tools(), Account())) > 0) Models.Persist(thread);
            Render();
        }

        void SetupPill(DocThread thread, string pillId)
        {
            switch (pillId)
            {
                case "uv":
                    InstallUv();
                    return;
                case "key":
                    KeyCard(thread, ForskText.Get(ForskKeyFile.MenuId), null);
                    return;
                case "get_key":
                    try { System.Diagnostics.Process.Start("/usr/bin/open", ForskSetup.KeySite); }
                    catch (Exception e) { Log("setup · open " + ForskSetup.KeySite + " · " + e.GetType().Name); }
                    return;
                case "connect":
                    ConnectAccount();
                    return;
                case "disconnect":
                    try { AccountFile.Remove(AccountFile.DefaultPath); }
                    catch (Exception e) { Log("account · remove · " + e.GetType().Name); }
                    _account = AccountState.Disconnected();
                    Log("account · disconnected on this Mac");
                    RefreshSetup();
                    return;
            }
        }

        // ------------------------------------------------------------ account (Connect Rhino)

        static AccountState _account;
        static int _linking;

        /// <summary>
        /// The Account row. The first look reads ~/.forsk/account.json and, when
        /// it holds a token, asks the dashboard once in the background: a Mac
        /// disconnected there turns Not connected here. Offline changes nothing.
        /// </summary>
        AccountState Account()
        {
            if (_account != null) return _account;
            AccountFile file = null;
            try { file = AccountFile.Load(AccountFile.DefaultPath); }
            catch (Exception e) { Log("account · read · " + e.GetType().Name); }
            _account = AccountState.From(file);
            if (file != null) CheckLicence(file);
            return _account;
        }

        void CheckLicence(AccountFile file)
        {
            var forsk = PluginVersion();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var status = ForskAccountHttp.Licence(file.Token, forsk, out var body);
                var known = ForskAccount.ParseLicence(status, body, out var email, out var plan);
                if (known == null) return;
                Post(() =>
                {
                    if (_account?.Phase != AccountPhase.Connected) return;
                    try
                    {
                        if (known == false) AccountFile.Remove(AccountFile.DefaultPath);
                        else if (email != file.Email || plan != file.Plan)
                            new AccountFile { Token = file.Token, Email = email, Plan = plan }.Save(AccountFile.DefaultPath);
                    }
                    catch (Exception e) { Log("account · write · " + e.GetType().Name); }
                    _account = known == false ? AccountState.Disconnected() : _account.Connected(email, plan);
                    Log("account · licence · " + (known == false ? "disconnected on the dashboard" : plan));
                    RefreshSetup();
                });
            });
        }

        /// <summary>
        /// Connect on the card: ask the dashboard for a code, show it on the
        /// card, open the Connect page in the browser, and wait for the tap off
        /// the UI thread. A second click while it waits does nothing.
        /// </summary>
        void ConnectAccount()
        {
            if (Interlocked.CompareExchange(ref _linking, 1, 0) != 0) return;
            var rhino = RhinoVersion();
            var forsk = PluginVersion();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Link(MacName(), rhino, forsk); }
                catch (Exception e) { Post(() => FailLink(e.GetType().Name)); }
                finally { Interlocked.Exchange(ref _linking, 0); }
            });
        }

        void Link(string name, string rhino, string forsk)
        {
            var status = ForskAccountHttp.Post(ForskAccount.StartUrl, ForskAccount.StartBody(name, rhino, forsk), out var body);
            var started = ForskAccount.ParseStart(status, body);
            if (started == null)
            {
                Post(() => FailLink(status == 0 ? ForskText.Get("setup.account.offline") : "the dashboard did not answer (HTTP " + status + "), try again later."));
                return;
            }
            Post(() =>
            {
                _account = (_account ?? AccountState.Disconnected()).Start(started.Code);
                Log("account · waiting · " + started.Code);
                RefreshSetup();
                try { System.Diagnostics.Process.Start("/usr/bin/open", started.Url); }
                catch (Exception e) { Log("account · open · " + e.GetType().Name); }
            });

            var deadline = DateTime.UtcNow + ForskAccount.GiveUp;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromSeconds(started.Interval));
                var pollStatus = ForskAccountHttp.Post(ForskAccount.PollUrl, ForskAccount.PollBody(started.Poll), out var pollBody);
                var state = ForskAccount.ParsePoll(pollStatus, pollBody, out var connected);
                if (state == "pending" || state == null) continue;
                if (state == "expired") break;
                connected.Save(AccountFile.DefaultPath);
                Post(() =>
                {
                    _account = _account.Connected(connected.Email, connected.Plan);
                    Log("account · connected · " + connected.Plan);
                    RefreshSetup();
                });
                return;
            }
            Post(() => FailLink(ForskText.Get("setup.account.expired")));
        }

        void FailLink(string reason)
        {
            _account = (_account ?? AccountState.Disconnected()).Fail(reason);
            Log("account · failed · " + reason);
            RefreshSetup();
        }

        /// <summary>"Julian's MacBook Air" from System Settings → Sharing, else the host name.</summary>
        static string MacName()
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("/usr/sbin/scutil", "--get ComputerName")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    var name = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(2000);
                    if (name.Length > 0) return name;
                }
            }
            catch (Exception) { }
            return Environment.MachineName;
        }

        /// <summary>Settings → Update available. An older open update card closes, so one is pinned. No update known: nothing.</summary>
        void UpdateCard(DocThread thread)
        {
            var version = ForskUpdate.Latest;
            if (version == null) return;
            foreach (var item in thread.Items)
                if (item["kind"]?.ToString() == ForskUpdate.MenuId) thread.Close(item["id"]?.ToString());
            thread.Add("user", ForskText.Get(ForskUpdate.MenuId));
            thread.BeginReply(null);
            thread.AddCard(ForskCards.Update(version, ForskUpdate.Current), null);
            thread.EndReply();
            Models.Persist(thread);
            Render();
        }

        /// <summary>Before Package Manager: Rhino has to quit and reopen, so save first. Cancel opens nothing.</summary>
        static bool ConfirmUpdate()
        {
            var doc = RhinoDoc.ActiveDoc;
            var text = ForskUpdate.ConfirmText(doc != null && doc.Modified ? (string.IsNullOrEmpty(doc.Name) ? "Untitled" : doc.Name) : null);
            var answer = Rhino.UI.Dialogs.ShowMessage(text, ForskText.Get("update.confirm.title"),
                Rhino.UI.ShowMessageButton.OKCancel, Rhino.UI.ShowMessageIcon.Information);
            return answer == Rhino.UI.ShowMessageResult.OK;
        }

        void UpdatePill(string pillId)
        {
            switch (pillId)
            {
                case "open":
                    if (ConfirmUpdate()) RhinoApp.RunScript("_PackageManager", false);
                    return;
                case "howto":
                    try { System.Diagnostics.Process.Start("/usr/bin/open", ForskUpdate.HowTo); }
                    catch (Exception e) { Log("update · open " + ForskUpdate.HowTo + " · " + e.GetType().Name); }
                    return;
            }
        }

        /// <summary>
        /// Set up on the card: uv installs off the UI thread, with no undo record
        /// and without holding the window busy. Each step, and the end, redraws
        /// the card. A second click while it runs does nothing.
        /// </summary>
        void InstallUv()
        {
            if (Tools().Phase == SetupPhase.Running) return;
            _setup = _setup.Start();
            RefreshSetup();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string failed = null;
                ForskUvInstall.Installed installed = null;
                try
                {
                    installed = ForskUvInstall.Install(new ForskUvInstall.Options(), step => Post(() =>
                    {
                        _setup = _setup.Step(step);
                        RefreshSetup();
                    }));
                }
                catch (ForskUvInstall.Failure e)
                {
                    failed = e.Message;
                }
                Post(() =>
                {
                    _setup = failed == null ? _setup.Done() : _setup.Fail(failed);
                    Log("setup · uv · " + (failed ?? installed.Version + " · sha256 " + installed.Sha));
                    // The facts read uv again, so the first-run line and the card both move now.
                    Poll(true);
                    RefreshSetup();
                });
            });
        }

        // ------------------------------------------------------------ chat

        void Send(string text)
        {
            var thread = Active();
            var doc = RhinoDoc.ActiveDoc;
            if (thread == null || doc == null || string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            // A slot's exact English label fires that slot; so does "draw a wall".
            var hit = ForskRegistry.ByLabel(Drawn(doc), text) ?? ForskRegistry.ByDrawPhrase(text);
            if (hit != null)
            {
                Fire(hit.Id, false);
                return;
            }
            if (Refuse(thread)) return;
            NoteMisroute(ForskMisroutes.Rephrase(ForskMisroutes.AnsweredSentence(thread.Items), PickedNow(), text, DateTimeOffset.UtcNow));
            // Support, picked or routed, answers a bug or a feature request and the card follows.
            if (ForskReports.EndsWithReport(thread.Override, text))
            {
                Chat(thread, text, "answer");
                return;
            }
            // A question is an answer. From any other role, a bug or a feature request is the card alone.
            if (ForskIntentRouter.Classify(text) == ForskIntent.Support)
            {
                var report = ReportCard(text, doc);
                if (report != null)
                {
                    thread.Add("user", text);
                    thread.BeginReply(TurnMark(thread, text));
                    thread.AddCard(report, null);
                    thread.EndReply();
                    Models.Persist(thread);
                    Render();
                    return;
                }
                Chat(thread, text, "answer");
                return;
            }
            var toggle = BakeChip.MapToggle(text);
            if (toggle != DaylightAction.None)
            {
                var id = toggle == DaylightAction.Show ? "daylight.show" : "daylight.hide";
                Job(thread, id, ForskText.Label(id), sink => Daylight(sink, toggle), userText: text, mark: TurnMark(thread, text));
                return;
            }
            if (Sections.IsPickPhrase(text))
            {
                SectionPick(thread, text, TurnMark(thread, text));
                return;
            }
            // Before Print: "export dwg" names no print, and "eksporter dxf" is no DXF import.
            var exportFormat = ForskIntentRouter.ExportFormat(text);
            if (exportFormat != null)
            {
                Export(thread, text, exportFormat, TurnMark(thread, text));
                return;
            }
            // The view picker from chat: "show the south elevation".
            var viewPick = ForskIntentRouter.ViewPick(text);
            if (viewPick != null)
            {
                Job(thread, "view.show", ForskText.Get("view.show"), sink => sink.Tool("show_view", new JObject { ["view"] = viewPick }),
                    userText: text, mark: TurnMark(thread, text));
                return;
            }
            // AN.3: the Analysis set prints as its own PDF, from its card.
            if (ForskIntentRouter.AnalysisPrint(text))
            {
                Fire("analysis.print", false);
                return;
            }
            if (ForskPrint.IsRequest(text))
            {
                Print(thread, text, mark: TurnMark(thread, text));
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
            // No key: one line and the key card, not a turn. Local actions never come here.
            if (string.IsNullOrEmpty(ForskKeys.Load()))
            {
                KeyCard(thread, text, ForskText.Get("grok.key.missing"));
                return;
            }
            var doc = RhinoDoc.ActiveDoc;
            _busy = true;
            var picked = doc == null ? Picked.None : Facts(doc).Picked;
            thread.Add("user", text);
            // The router's role for this sentence. Support's pick applies only to a
            // question, a bug, or a feature request, so an edit stays with Modeller.
            var intent = ForskIntentRouter.Classify(text, picked);
            var role = ForskRoles.Turn(thread.Override, intent);
            thread.BeginReply(ForskRoles.Mark(intent, role));
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
                    AddReceipt(thread, name, envelope);
                    Render();
                }),
                DialogParent = this,
                Role = role
            };
            Render();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ForskGrok.RunTurn(text, picked, history, (role, line) =>
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
                    if (ForskReports.EndsWithReport(thread.Override, text))
                    {
                        var report = ReportCard(text, RhinoDoc.ActiveDoc);
                        if (report != null) thread.AddCard(report, null);
                    }
                    Finish(thread, kind, undo, serial, null, text);
                });
            });
        }

        /// <summary>The report card, with the remembered reply address filled in when there is one.</summary>
        static CardSpec ReportCard(string text, RhinoDoc doc)
        {
            return ForskReports.Card(text, FileName(doc), ForskReplyEmail.Read(ForskReplyEmail.DefaultPath));
        }

        /// <summary>Keep what was typed across a render. The page rebuilds the inputs from the card.</summary>
        static void KeepTyped(JObject card, JObject values)
        {
            foreach (var field in card["fields"] as JArray ?? new JArray())
            {
                var key = field["key"]?.ToString();
                if (string.IsNullOrEmpty(key) || values == null || values[key] == null) continue;
                field["value"] = values[key].ToString();
            }
        }

        static string Typed(JObject card, string key)
        {
            foreach (var field in card["fields"] as JArray ?? new JArray())
                if (field["key"]?.ToString() == key) return field["value"]?.ToString() ?? "";
            return "";
        }

        /// <summary>
        /// Posts the report off the UI thread. The step line says Sending, then
        /// the receipt carries the FS- reference. The debug report rides along
        /// only when the box is ticked.
        /// </summary>
        void SendReport(DocThread thread, JObject card, JObject values, RhinoDoc doc)
        {
            KeepTyped(card, values);
            var type = Typed(card, "type");
            var message = Typed(card, "description");
            var email = Typed(card, "email");
            if (ForskSupport.EmailOk(email)) ForskReplyEmail.Remember(ForskReplyEmail.DefaultPath, email);
            string debug = null;
            var attach = values != null && values["attach"] != null ? values["attach"].ToString() : Typed(card, "attach");
            if (ForskReports.WantsDebug(attach))
            {
                try { debug = BuildDebugReport(doc); }
                catch (Exception e)
                {
                    debug = ForskDebug.Redact("Debug report failed.\n" + e.GetType().Name + ": " + e.Message + "\n");
                }
            }
            var sample = string.IsNullOrWhiteSpace(message) ? thread.LastUserText() : message;
            var language = ForskPrefill.Language(sample);
            var plugin = PluginVersion();
            var rhino = RhinoVersion();
            var os = OsDescription();
            var sending = ForskSupportChat.Sending(language);
            card["answer"] = sending;
            card.Remove("note");
            thread.Busy = sending;
            thread.TurnMark = "Support";
            _busy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var outcome = PostReport(type, message, email, plugin, rhino, os, debug, language);
                Post(() =>
                {
                    ForskSupportChat.Show(thread, outcome);
                    card["answer"] = ForskSupportChat.CardAnswer(outcome, language);
                    _busy = false;
                    try { Log("support: " + (outcome.Accepted ? outcome.Id : outcome.Code)); }
                    catch (Exception) { }
                    Models.Persist(thread);
                    Render();
                });
            });
        }

        /// <summary>The POST, and the outbox when the server does not take it. Not the UI thread.</summary>
        static ForskSupport.Outcome PostReport(string type, string message, string email, string plugin, string rhino, string os, string debug, string language)
        {
            try
            {
                var built = ForskSupport.Build(type, message, email, plugin, rhino, os, debug);
                if (!built.Ok)
                {
                    var status = built.Error == "payload_too_large" || built.Error == "debug_report_too_large" ? 413 : 400;
                    return ForskSupport.Parse(status, "{\"ok\":false,\"error\":\"" + (built.Error ?? "rejected") + "\"}", null, language, DateTimeOffset.UtcNow);
                }
                return ForskSupportSend.Deliver(built.Json, ForskSupportHttp.Shared, ForskOutbox.Shared, DateTimeOffset.UtcNow, language);
            }
            catch (Exception)
            {
                return ForskSupport.Network(language);
            }
        }

        static string PluginVersion()
        {
            var info = typeof(ForskSupport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info;
            return typeof(ForskSupport).Assembly.GetName().Version?.ToString() ?? "";
        }

        static string RhinoVersion()
        {
            try { return RhinoApp.Version == null ? "" : RhinoApp.Version.ToString(); }
            catch (Exception) { return ""; }
        }

        static string OsDescription()
        {
            try { return RuntimeInformation.OSDescription ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>Settings → Release notes: the What's new card again, every release, from Support, under the chat.</summary>
        void ReleaseNotes()
        {
            var thread = Active();
            var notes = ForskWhatsNewGate.Notes();
            if (thread == null || notes == null) return;
            thread.BeginReply(ForskText.Get("role.support"));
            thread.AddCard(ForskWhatsNew.Card(notes), null);
            thread.EndReply();
            Models.Persist(thread);
            Render();
        }

        /// <summary>The analysis menu's Live ticks, on the file.</summary>
        /// <summary>AN.4: the Choose analyses ticks onto the file. The names of the analyses now in the set.</summary>
        /// <summary>A choice card's option, applied at once. Returns the card's new note.</summary>
        string ApplyChoice(string kind, JObject card, string option)
        {
            switch (kind)
            {
                case "daylight.quality":
                    // Saving is instant: no job, no tracer. A map on screen is now out of
                    // date, so the bar offers Run again at the new grid when the user wants it.
                    ForskDaylight.Quality = option;
                    var doc = RhinoDoc.ActiveDoc;
                    // The facts the window already holds: a second full read here cost a click as much as the render.
                    if (doc != null && Facts(doc).Map == MapState.Shown) RhinoMCPFunctions.MarkMapAfterEdit(doc, MapEdit.Opening);
                    MarkDirty();
                    return ForskText.Format("daylight.quality.now", "quality", ForskText.Get("daylight.quality." + option));
                case "room.inside":
                    TryInside(option);
                    return ForskText.Format("room.inside.held", "way", option);
                case "view.exterior":
                    TryOutside(option);
                    return ForskText.Format("view.exterior.held", "way", option);
                case "ink.set":
                    return QuietTool("print_profile", new JObject { ["name"] = option })
                        ?? ForskText.Format("ink.set.now", "ink", option);
                case "opening.type":
                    // The card made with nothing picked changes every opening of the pill's kind.
                    var typeArgs = new JObject { ["type"] = option };
                    if (card["data"]?["all"]?.Value<bool>() == true) typeArgs["all"] = true;
                    return QuietTool("set_opening_type", typeArgs) ?? ForskText.Format("opening.type.now", "type", OpeningTypes.All.FirstOrDefault(t => t.Id == option)?.Label ?? option);
                default:
                    return null;
            }
        }

        /// <summary>A tool run for a choice card, with no chat line. The reason when it failed, else null.</summary>
        static string QuietTool(string name, JObject args)
        {
            var envelope = ForskTools.CommandOnUi(name, args);
            if (string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)) return null;
            return envelope?["message"]?.ToString() ?? "That did not change.";
        }

        /// <summary>
        /// Jump inside with no room picked: the chat asks for a room's floor, Rhino
        /// waits for the click, the room is picked, and the direction card opens
        /// with north shown. Esc says nothing was clicked.
        /// </summary>
        void PickRoomThenJump(DocThread thread, ForskAction action, string prompt)
        {
            if (Refuse(thread)) return;
            var doc = RhinoDoc.ActiveDoc;
            thread.Add("user", action.Label);
            thread.BeginReply(ForskRoles.MarkForAction(action.Id));
            thread.Add("line", prompt);
            Render();
            HandToRhino();
            var room = ForskRoomPick.Pick(doc, prompt);
            if (room == Guid.Empty)
            {
                thread.Add("line", ForskText.Get("room.inside.nopick"));
                thread.EndReply();
                Models.Persist(thread);
                Render();
                TakeKeyboard();
                return;
            }
            doc.Objects.UnselectAll();
            doc.Objects.Select(room);
            doc.Views.Redraw();
            MarkDirty();
            var facts = Facts(doc);
            var spec = ForskCards.For(action.Id, facts);
            if (spec != null) thread.AddCard(spec, facts);
            thread.EndReply();
            Models.Persist(thread);
            if (spec != null) TryInside("north");
            Render();
            TakeKeyboard();
        }

        /// <summary>A choice card's Confirm: what the held option still needs. Quiet unless it fails.</summary>
        void ConfirmChoice(string kind, string held)
        {
            if (kind != "room.inside" && kind != "view.exterior") return;
            var tool = kind == "room.inside" ? "jump_inside" : "exterior_render";
            var envelope = ForskTools.CommandOnUi(tool, new JObject { ["direction"] = held });
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                Active()?.AddLine(envelope?["message"]?.ToString() ?? "The view was not saved.");
        }

        /// <summary>Interior render, tried: the Perspective view takes the shot, nothing saved. Quiet unless it fails.</summary>
        void TryInside(string way)
        {
            var envelope = ForskTools.CommandOnUi("jump_inside", new JObject { ["direction"] = way, ["save"] = false });
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                Active()?.AddLine(envelope?["message"]?.ToString() ?? "The view did not change.");
        }

        /// <summary>
        /// Export viewport: the active render view saved as a PNG at twice its size
        /// with its render look, where the save dialog says (the file's folder,
        /// named after the view). No chat line; a receipt with Show in Finder.
        /// </summary>
        void ExportViewport(DocThread thread)
        {
            var doc = RhinoDoc.ActiveDoc;
            var view = doc?.Views.ActiveView;
            if (thread == null || view == null || view is Rhino.Display.RhinoPageView) return;
            var vp = view.ActiveViewport;
            var dialog = new Eto.Forms.SaveFileDialog { Title = ForskText.Get("view.export"), FileName = FileStem(vp.Name) + ".png", CheckFileExists = false };
            dialog.Filters.Add(new Eto.Forms.FileFilter("PNG image", ".png"));
            var folder = string.IsNullOrEmpty(doc.Path) ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : System.IO.Path.GetDirectoryName(doc.Path);
            try { if (!string.IsNullOrEmpty(folder)) dialog.Directory = new Uri("file://" + folder.TrimEnd('/') + "/"); }
            catch (Exception) { }
            if (dialog.ShowDialog(this) != Eto.Forms.DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName)) return;
            var path = dialog.FileName.Trim();
            if (!System.IO.Path.IsPathRooted(path)) path = System.IO.Path.Combine(folder ?? "", path);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
            var size = new System.Drawing.Size(vp.Size.Width * 2, vp.Size.Height * 2);
            try
            {
                // The look's own attributes: a size-only capture of a render look draws it white on the Mac.
                using (var bitmap = view.CaptureToBitmap(size, vp.DisplayMode.DisplayAttributes))
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception e)
            {
                thread.AddLine("The image was not saved: " + e.Message);
                Render();
                return;
            }
            thread.BeginReply(ForskRoles.MarkForAction("view.export"));
            thread.AddCard(ForskCards.ViewExported(path, size.Width, size.Height), Facts(doc));
            thread.EndReply();
            Models.Persist(thread);
            Render();
        }

        /// <summary>A view name as a file name: no slashes or colons.</summary>
        static string FileStem(string name)
        {
            var stem = new string((name ?? "").Select(c => c == '/' || c == ':' || c == '\\' ? ' ' : c).ToArray()).Trim();
            return stem.Length == 0 ? "Forsk view" : stem;
        }

        static void RevealInFinder(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            try { System.Diagnostics.Process.Start("open", "-R \"" + path + "\""); }
            catch (Exception) { }
        }

        /// <summary>Exterior render, tried: the Perspective view takes the shot from that side, nothing saved. Quiet unless it fails.</summary>
        void TryOutside(string way)
        {
            var envelope = ForskTools.CommandOnUi("exterior_render", new JObject { ["direction"] = way, ["save"] = false });
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                Active()?.AddLine(envelope?["message"]?.ToString() ?? "The view did not change.");
        }

        /// <summary>
        /// The view picker: the active viewport shows the view, or a saved
        /// interior or exterior render view with its look. Quiet unless it fails.
        /// </summary>
        void PickView(string id)
        {
            if (Functions.ViewPicker.TryRender(id, out var renderName, out var exterior))
            {
                var why = Functions.ForskInteriorHost.Show(RhinoDoc.ActiveDoc, renderName, exterior);
                if (why != null) Active()?.AddLine(why);
                MarkDirty();
                Render();
                return;
            }
            if (!Functions.ViewPicker.Known(id)) return;
            var envelope = ForskTools.CommandOnUi("show_view", new JObject { ["view"] = id });
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                Active()?.AddLine(envelope?["message"]?.ToString() ?? "The view did not change.");
            MarkDirty();
            Render();
        }

        /// <summary>
        /// An edit typed or picked in the info panel: the same tool the chat runs
        /// (ForskInfo.Edit), with no chat line. A refusal or a failed edit is one
        /// line in the chat; the panel shows the record as it now is either way.
        /// </summary>
        void InfoEdit(string id, string field, string value)
        {
            var edit = ForskInfo.Edit(id, field, value, out var error);
            if (edit.HasValue) error = QuietTool(edit.Value.Tool, edit.Value.Args);
            if (error != null) Active()?.AddLine(error);
            MarkDirty();
            Render();
        }

        static List<string> SaveAnalysisSet(JObject values)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return new List<string>();
            foreach (var (key, value) in Functions.Analysis.SetChoice(k => values?[k]?.ToString()))
                RhinoMCPFunctions.WriteAnalysis(doc, key, value);
            var state = RhinoMCPFunctions.ReadAnalysis(doc);
            return Functions.Analysis.All.Where(state.InSet).Select(id => ForskText.Get("analysis." + id)).ToList();
        }

        static void SaveLive(JObject values)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null || values == null) return;
            foreach (var id in Functions.Analysis.All)
            {
                var value = values[Functions.Analysis.LiveKey(id)]?.ToString();
                if (value == "1" || value == "0") RhinoMCPFunctions.WriteAnalysis(doc, Functions.Analysis.LiveKey(id), value);
            }
        }

        /// <summary>A tool's receipt, and under it the wall review when more than the wall changed.</summary>
        void AddReceipt(DocThread thread, string tool, JObject envelope, string text = null)
        {
            var item = ForskReceipt.From(tool, envelope);
            if (!string.IsNullOrWhiteSpace(text)) item.Text = text;
            thread.Add(item);
            var cards = new List<CardSpec> { ForskCards.WallReview(envelope, LastUserIsNorwegian(thread)) };
            if (string.Equals(tool, "compare_option", StringComparison.Ordinal)) cards.Add(ForskCards.OptionCompareFrom(envelope));
            if (string.Equals(tool, "area_stats", StringComparison.Ordinal)) cards.Add(ForskCards.AreaSummary(envelope));
            if (string.Equals(tool, ForskToolPacks.TakeoffTool, StringComparison.Ordinal)) cards.Add(ForskCards.Takeoff(envelope));
            if (string.Equals(tool, "furnish_room", StringComparison.Ordinal)) cards.Add(ForskCards.FurnishChoice(envelope));
            cards.RemoveAll(c => c == null);
            // The file's facts stamp the cards; a receipt with no card reads nothing (the render after the job does).
            if (cards.Count == 0) return;
            var doc = RhinoDoc.ActiveDoc;
            var facts = doc == null ? null : ReadFacts(doc);
            foreach (var card in cards) thread.AddCard(card, facts);
        }

        /// <summary>The review card follows the last thing the user wrote. The tool's own phrase was already chosen.</summary>
        static bool LastUserIsNorwegian(DocThread thread)
        {
            for (var i = thread.Items.Count - 1; i >= 0; i--)
            {
                if (thread.Items[i]["role"]?.ToString() != "user") continue;
                return ForskPrefill.Language(thread.Items[i]["text"]?.ToString()) == "nb";
            }
            return false;
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
                    if (line.Contains(" · ") || line.StartsWith(ForskReceipt.Done, StringComparison.Ordinal)) _thread.Add(ForskReceipt.FromLine(line));
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

            /// <summary>
            /// One tool, on the UI thread, and its structured receipt.
            /// words replaces the receipt text. On a failure it is one chat line instead.
            /// quiet skips the receipt when the tool succeeded: the card already says what was saved.
            /// </summary>
            public JObject Tool(string name, JObject args, Func<JObject, string> words = null, bool quiet = false)
            {
                JObject envelope = null;
                RhinoApp.InvokeOnUiThread(new Action(() => envelope = ForskTools.ExecuteAllowed(name, args)));
                envelope = envelope ?? ForskTools.Fail("No result");
                var ok = string.Equals(envelope["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase);
                if (quiet && ok) return envelope;
                var text = words?.Invoke(envelope);
                var failed = words != null && !ok;
                Post(() =>
                {
                    if (failed)
                    {
                        _thread.Add("line", string.IsNullOrWhiteSpace(text) ? envelope["message"]?.ToString() : text);
                        _window.Render();
                        return;
                    }
                    _window.AddReceipt(_thread, name, envelope, text);
                    _window.Render();
                });
                return envelope;
            }
        }

        /// <summary>
        /// A pill's work, off the UI thread, inside one undo record named after
        /// the pill. The step line stays on screen; the longest UI stall is logged.
        /// </summary>
        void Job(DocThread thread, string kind, string label, Action<JobSink> work, string userText = null, bool ownRecord = true, Action after = null, string mark = null, bool noteUser = true)
        {
            var doc = RhinoDoc.ActiveDoc;
            _busy = true;
            if (noteUser) thread.Add("user", userText ?? label);
            // A pill's receipts carry the role that knows the action; a typed shortcut passes the router's.
            thread.BeginReply(mark ?? ForskRoles.MarkForAction(kind));
            thread.Busy = ForskText.Format("line.running", "what", label);
            // Generate 3D suspends view redraw for the whole pill and logs each phase.
            if (kind == "file.generate" || kind == "file.rebuild")
            {
                BakePace.Begin(text =>
                {
                    thread.Busy = text;
                    Render();
                }, Log);
                BakePace.Hold(doc);
            }
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
                ForskSpeech.Use(userText ?? label);
                try
                {
                    work(sink);
                }
                catch (Exception e)
                {
                    sink.Line(label + " · error · " + ForskTools.Clip(e.Message));
                }
                finally
                {
                    ForskSpeech.Clear();
                }
                var longest = stall.Stop();
                Post(() =>
                {
                    Finish(thread, kind, undo, serial, longest, label);
                    if (after == null) return;
                    after();
                    Models.Persist(thread);
                    Render();
                });
            });
        }

        void Finish(DocThread thread, string kind, ForskUndo undo, uint serial, long? longest, string label)
        {
            undo?.End();
            // One redraw for the pass, then the phase line. Other jobs leave the pace closed.
            BakePace.End(RhinoDoc.ActiveDoc);
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
            thread.EndReply();
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

        /// <summary>Print, or one sheet of it (view). The save dialog is parented to this window.</summary>
        void Print(DocThread thread, string userText, string view = null, string mark = null)
        {
            var id = view == null ? "file.print" : "print.one";
            if (AskInfoFirst(thread, id, view, userText, mark)) return;
            Job(thread, id, ForskText.Label(id), sink => PrintSteps(sink, view), userText: userText, mark: mark);
        }

        void PrintSteps(JobSink sink, string view)
        {
            sink.Step(ForskText.Format("line.printing", "i", "1", "n", "2", "what", ForskText.Get("line.printing.layout")));
            var line = ForskPrint.Run(status => sink.Step(ForskText.Format("line.printing", "i", "2", "n", "2", "what", status)), this, view);
            sink.Line(line);
        }

        /// <summary>R3: the set as files, one per sheet. The folder dialog is parented to this window.</summary>
        void Export(DocThread thread, string userText, string format, string mark = null, bool noteUser = true)
        {
            var pending = "export." + format;
            var id = PendingJobId(pending);
            if (format != "ifc" && AskInfoFirst(thread, pending, null, userText, mark)) return;
            Job(thread, id, ForskText.Label(id), sink => RunPending(sink, pending, null), userText: userText, mark: mark, noteUser: noteUser);
        }

        /// <summary>The registry id a pending action runs under: DXF is the DWG pill's.</summary>
        static string PendingJobId(string pending)
        {
            return pending == "export.dxf" ? "export.dwg" : pending;
        }

        /// <summary>The work of a Print or an Export, after the Project info card or without it.</summary>
        void RunPending(JobSink sink, string pending, string view)
        {
            switch (pending)
            {
                case "file.print":
                    PrintSteps(sink, null);
                    return;
                case "print.one":
                    PrintSteps(sink, view);
                    return;
                case "export.ifc":
                    sink.Line(ForskPrint.ExportIfc(sink.Step, this));
                    return;
                case "export.csv":
                    sink.Line(ForskPrint.ExportCsv(sink.Step, this));
                    return;
                case "export.dwg":
                case "export.dxf":
                    sink.Line(ForskPrint.Export(sink.Step, this, pending.Substring("export.".Length)));
                    return;
            }
        }

        /// <summary>
        /// v3: the first Print or Export of a file with no project name posts
        /// the Project info card instead, once. The card's answer runs pending.
        /// </summary>
        bool AskInfoFirst(DocThread thread, string pending, string view, string userText, string mark)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return false;
            var facts = ReadFacts(doc);
            var spec = ForskCards.AskInfoFirst(facts, pending, view);
            if (spec == null) return false;
            var id = PendingJobId(pending);
            thread.Add("user", userText ?? ForskText.Label(id));
            thread.BeginReply(mark ?? ForskRoles.MarkForAction(id));
            thread.AddCard(spec, facts);
            thread.EndReply();
            Models.Persist(thread);
            Render();
            return true;
        }

        /// <summary>
        /// The import dialog, parented to this window. A PDF with more than one
        /// page asks which on a card in the thread; then plan_import or dxf_import.
        /// </summary>
        /// <summary>
        /// The logo's file dialog, parented to this window. A usable file is
        /// held on the card (an SVG as the PNG it prints as); one that is not
        /// says why on the card. Cancel changes nothing.
        /// </summary>
        void HoldPickedLogo(JObject card)
        {
            var dialog = new Eto.Forms.OpenFileDialog { Title = "Office logo for the title block: a PNG, JPEG or SVG" };
            dialog.Filters.Add(new Eto.Forms.FileFilter("PNG, JPEG or SVG", ".png", ".jpg", ".jpeg", ".svg"));
            if (dialog.ShowDialog(this) != Eto.Forms.DialogResult.Ok) return;
            var path = dialog.FileName;
            byte[] bytes = null, picture;
            string error;
            try
            {
                bytes = File.ReadAllBytes(path);
                picture = OfficeLogo.Prepare(bytes, OfficeLogo.RasterizeWithQuickLook, out error);
            }
            catch (Exception) { picture = null; error = OfficeLogo.Unreadable; }
            if (picture == null)
            {
                card["note"] = error;
                return;
            }
            // The card carries a path, so an SVG's PNG goes to a temp file that Save reads.
            var held = path;
            if (picture != bytes)
            {
                held = Path.Combine(Path.GetTempPath(), "forsk-logo-" + Guid.NewGuid().ToString("N") + ".png");
                File.WriteAllBytes(held, picture);
            }
            ForskCards.HoldLogo(card, Path.GetFileName(path), held, picture);
        }

        void Import(DocThread thread, ForskAction action)
        {
            var path = ForskPlanImport.PickFile(this);
            if (path == null) return;
            var argument = ForskPlanFile.Argument(path);
            if (argument == null) return;
            if (argument == "pdf_path")
            {
                var pages = ForskPlanImport.PageCount(path);
                if (pages > 1)
                {
                    thread.Add("user", action.Label);
                    thread.BeginReply(ForskRoles.MarkForAction(action.Id));
                    thread.AddCard(ForskCards.PdfPage(path, pages), null);
                    thread.EndReply();
                    Models.Persist(thread);
                    Render();
                    return;
                }
                ImportJob(thread, new JObject { ["pdf_path"] = path, ["page"] = 1 }, action.Label);
                return;
            }
            ImportJob(thread, new JObject { [argument] = path }, action.Label);
        }

        /// <summary>
        /// One import: a short receipt, then the review as a card under it, and
        /// only when the review was stored on the underlay. A DXF keeps its note.
        /// </summary>
        void ImportJob(DocThread thread, JObject source, string userText)
        {
            // A PDF, scan or image goes through uv's tools; a DXF does not.
            if (source["dxf"] == null && AskSetup(thread, userText, ForskUv.Uv() == null)) return;
            var doc = RhinoDoc.ActiveDoc;
            Job(thread, "file.import", ForskText.Label("file.import"), sink =>
            {
                if (source["dxf"] != null)
                {
                    var dxf = sink.Tool(ForskDxf.Tool, new JObject { ["path"] = source["dxf"] });
                    sink.Say(ForskPlanImport.DxfNote(dxf));
                    return;
                }
                sink.Tool(ForskPlanImport.ImportTool, source);
            }, userText: userText, after: () =>
            {
                if (doc == null) return;
                var facts = ReadFacts(doc);
                var review = ForskCards.Review(facts);
                if (review != null) thread.AddCard(review, facts);
            });
        }

        /// <summary>Two points in the view, then the length in a Forsk field.</summary>
        void Scale(DocThread thread, ForskAction action)
        {
            thread.Add("user", action.Label);
            thread.BeginReply(ForskRoles.MarkForAction(action.Id));
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
                var doc = RhinoDoc.ActiveDoc;
                thread.AddCard(new CardSpec
                {
                    Kind = "scale",
                    Question = ForskText.Get("prompt.scale.length"),
                    Fields = new List<CardField> { new CardField { Key = "length", Value = FormatMm(measured), Unit = "mm" } },
                    Pills = { new CardPill("set", ForskText.Get("prompt.scale.set")), new CardPill("cancel", ForskText.Get("word.cancel")) },
                    Depends = "model",
                    Data = new JObject { ["p1"] = pick["p1"], ["p2"] = pick["p2"] }
                }, doc == null ? null : ReadFacts(doc));
            }
            thread.EndReply();
            Models.Persist(thread);
            MarkDirty();
            Render();
            TakeKeyboard();
        }

        /// <summary>The sample house, drawn in the empty file. Generate 3D is the next step, not this one.</summary>
        void OpenSample(DocThread thread, ForskAction action, RhinoDoc doc)
        {
            thread.Add("user", action.Label);
            thread.BeginReply(ForskRoles.MarkForAction(action.Id));
            string line;
            ForskCalls.Enter();
            try
            {
                line = RhinoMCPFunctions.OpenSampleHouse(doc);
            }
            catch (Exception e)
            {
                line = "Sample house · error · " + e.Message;
            }
            finally
            {
                ForskCalls.Exit();
            }
            thread.Add("line", line);
            thread.EndReply();
            Models.Persist(thread);
            MarkDirty();
            Render();
        }

        /// <summary>The wall layer made current, created when missing, then Rhino's Polyline in the view.</summary>
        void Draw(DocThread thread, ForskAction action, RhinoDoc doc)
        {
            thread.Add("user", action.Label);
            // Planner from the click. The header reads the turn before the polyline returns.
            thread.BeginReply(ForskRoles.MarkForAction(action.Id));
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
                thread.EndReply();
            }
            Models.Persist(thread);
            MarkDirty();
            Render();
        }

        /// <summary>The selected curves go onto the wall layer. With none selected, one line asks for them.</summary>
        void UseCurves(DocThread thread, ForskAction action, RhinoDoc doc)
        {
            thread.Add("user", action.Label);
            thread.BeginReply(ForskRoles.MarkForAction(action.Id));
            var curves = RhinoMCPFunctions.ListSelected(doc).Where(o => o?.Geometry is Curve).ToList();
            if (curves.Count == 0)
            {
                thread.Add("line", ForskText.Get("file.use_curves.ask"));
                thread.EndReply();
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
        void SectionPick(DocThread thread, string userText, string mark = null)
        {
            if (Refuse(thread)) return;
            var doc = RhinoDoc.ActiveDoc;
            thread.Add("user", userText);
            thread.BeginReply(mark ?? ForskRoles.MarkForAction("section.add"));
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

        /// <summary>A draw tool: its prompt, the points in the view, then the tool calls in one undo record. Esc adds nothing.</summary>
        void DrawPick(DocThread thread, string userText, string actionId, string promptKey, string tool, Func<bool, JObject> run)
        {
            if (Refuse(thread)) return;
            var doc = RhinoDoc.ActiveDoc;
            thread.Add("user", userText);
            thread.BeginReply(ForskRoles.MarkForAction(actionId));
            thread.Add("line", ForskText.Get(promptKey));
            Render();
            HandToRhino();
            _busy = true;
            _jobChanges = 0;
            JObject envelope;
            try
            {
                envelope = run(true);
            }
            finally
            {
                _busy = false;
            }
            thread.Add(ForskReceipt.From(tool, envelope));
            // The tool kept its own single record.
            Finish(thread, actionId, null, doc?.RuntimeSerialNumber ?? 0, null, userText);
            TakeKeyboard();
        }

        /// <summary>The wall is dragged in the view. Release runs move_wall inside this pill's one record.</summary>
        void DragWall(DocThread thread)
        {
            if (Refuse(thread)) return;
            var doc = RhinoDoc.ActiveDoc;
            var nb = ForskPrefill.Language(thread.LastUserText()) == "nb";
            var shown = ForskText.Get(nb ? "wall.drag.nb" : "wall.drag");
            thread.Add("user", shown);
            thread.BeginReply(ForskRoles.MarkForAction("wall.drag"));
            thread.Add("line", ForskText.Get(nb ? "wall.drag.prompt.nb" : "wall.drag.prompt"));
            Render();
            HandToRhino();
            _busy = true;
            ForskDragWall.Outcome outcome = null;
            ForskSpeech.Use(nb ? "dra veggen" : "drag");
            try
            {
                outcome = ForskDragWall.Pick(doc);
            }
            finally
            {
                ForskSpeech.Clear();
            }
            if (outcome == null || string.IsNullOrEmpty(outcome.Toward))
            {
                _busy = false;
                if (!string.IsNullOrEmpty(outcome?.Line)) thread.Add("line", outcome.Line);
                thread.EndReply();
                Models.Persist(thread);
                Render();
                TakeKeyboard();
                return;
            }
            Job(thread, "wall.drag", shown, sink => sink.Tool("move_wall", new JObject
            {
                ["toward"] = outcome.Toward,
                ["distance_mm"] = outcome.Mm
            }, envelope => ForskDragWall.Words(outcome, envelope)), userText: shown, noteUser: false, after: TakeKeyboard);
        }

        static string SelectedRoom(RhinoDoc doc)
        {
            foreach (var picked in RhinoMCPFunctions.ListSelected(doc))
            {
                // A floor plate stands for its room's marker.
                var obj = RhinoMCPFunctions.ResolveRoomHandle(doc, picked);
                var attr = obj?.Attributes;
                if (attr == null || attr.GetUserString("forsk:kind") != "room") continue;
                return attr.GetUserString("forsk:room_name") ?? attr.GetUserString("forsk:room_id") ?? obj.Name;
            }
            return null;
        }

        void AskWallSide(DocThread thread, FileFacts facts)
        {
            thread.Add("user", ForskText.Label("wall.delete"));
            thread.BeginReply(ForskRoles.MarkForAction("wall.delete"));
            thread.AddCard(new CardSpec
            {
                Kind = "wall.delete",
                Question = ForskText.Get("wall.delete.ask"),
                Pills =
                {
                    new CardPill("north", ForskText.Get("word.north")),
                    new CardPill("south", ForskText.Get("word.south")),
                    new CardPill("east", ForskText.Get("word.east")),
                    new CardPill("west", ForskText.Get("word.west")),
                    new CardPill("cancel", ForskText.Get("word.cancel"))
                },
                Note = ForskText.Get("wall.delete.inner"),
                Depends = "selection"
            }, facts);
            thread.EndReply();
            Models.Persist(thread);
            Render();
        }

        /// <summary>The one Forsk record, while it is the newest thing in the document.</summary>
        void Undo(DocThread thread, ForskAction action, RhinoDoc doc)
        {
            thread.Add("user", action.Label);
            thread.BeginReply(ForskRoles.MarkForAction(action.Id));
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
            thread.EndReply();
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
