using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>run: local code. ask: through the model. card: opens a card. prefill: writes a sentence into the composer and waits.</summary>
    public enum Runs
    {
        Run,
        Ask,
        Card,
        Prefill
    }

    /// <summary>One registry entry. Its label and reason live in ForskText under its id.</summary>
    public sealed class ForskAction
    {
        readonly Func<FileFacts, bool> _when;

        public ForskAction(string id, Runs runs, string group, Func<FileFacts, bool> when)
        {
            Id = id;
            Runs = runs;
            Group = group;
            _when = when;
        }

        public string Id { get; }
        public Runs Runs { get; }
        /// <summary>The "What can I do here?" group key.</summary>
        public string Group { get; }
        public string Label => ForskText.Label(Id);

        /// <summary>True for this file now. A click checks it again.</summary>
        public bool When(FileFacts f)
        {
            return f != null && _when(f);
        }

        /// <summary>True, and an ask action also needs the chat key.</summary>
        public bool Shows(FileFacts f)
        {
            return When(f) && (Runs != Runs.Ask || f.KeyPresent);
        }
    }

    /// <summary>The pinned bar: slot 1, up to two contextual slots, then Add detail when a wall, door or window is picked and it is not already there, then "⋯". One reason line under it.</summary>
    public sealed class BarView
    {
        public ForskAction Slot1;
        public readonly List<ForskAction> Context = new List<ForskAction>();
        /// <summary>The reason sentence, as stored.</summary>
        public string Reason;

        public IEnumerable<ForskAction> Slots
        {
            get
            {
                if (Slot1 != null) yield return Slot1;
                foreach (var action in Context) yield return action;
            }
        }

        public JObject ToJson()
        {
            var slots = new JArray();
            var index = 0;
            foreach (var action in Slots)
            {
                index++;
                slots.Add(new JObject
                {
                    ["id"] = action.Id,
                    ["label"] = action.Label,
                    ["runs"] = action.Runs.ToString().ToLowerInvariant(),
                    ["key"] = "⌘" + index
                });
            }
            return new JObject
            {
                ["slots"] = slots,
                ["because"] = ForskText.Get("bar.because"),
                ["reason"] = LowerFirst(Reason),
                ["help"] = new JObject
                {
                    ["id"] = "help.card",
                    ["label"] = ForskText.Get("bar.help"),
                    ["title"] = ForskText.Label("help.card"),
                    ["key"] = "⌘/"
                }
            };
        }

        /// <summary>"A 3D model is in the file." reads "a 3D model is in the file." after "Suggested because". PDF stays PDF.</summary>
        static string LowerFirst(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length > 1 && char.IsUpper(text[1])) return text;
            return char.ToLowerInvariant(text[0]) + text.Substring(1);
        }
    }

    public sealed class CardGroup
    {
        public string Title;
        public List<ForskAction> Actions = new List<ForskAction>();
    }

    /// <summary>"What can I do here?": every action true for this file, by group, then one sentence per missing piece.</summary>
    public sealed class HelpView
    {
        public List<CardGroup> Groups = new List<CardGroup>();
        public List<string> Hints = new List<string>();

        public IEnumerable<ForskAction> Actions => Groups.SelectMany(g => g.Actions);

        public JObject ToJson()
        {
            var groups = new JArray();
            foreach (var group in Groups)
            {
                var actions = new JArray();
                foreach (var action in group.Actions)
                    actions.Add(new JObject { ["id"] = action.Id, ["label"] = action.Label, ["runs"] = action.Runs.ToString().ToLowerInvariant() });
                groups.Add(new JObject { ["title"] = group.Title, ["actions"] = actions });
            }
            return new JObject
            {
                ["title"] = ForskText.Label("help.card"),
                ["groups"] = groups,
                ["hints"] = new JArray(Hints.Select(ForskText.Get))
            };
        }
    }

    /// <summary>
    /// The one action registry. Slot 1 reads the file only: selection, the
    /// last action and the role never move it. The contextual slots are the
    /// first two eligible in a fixed order. The card is a read of the same
    /// list. Code decides what is true; the model adds no suggestion.
    /// </summary>
    public static class ForskRegistry
    {
        /// <summary>
        /// More actions' groups, by the work (UI unification, 2026-10-07). Two groups
        /// stay off the sheet: Settings (the gear's menu is their one home) and
        /// Options (the Options card opens them).
        /// </summary>
        public static readonly IReadOnlyList<string> GroupOrder = new[]
        {
            "group.start", "group.model", "group.rooms", "group.analyses", "group.print", "group.chat"
        };

        /// <summary>Groups with a home of their own, off the More actions sheet.</summary>
        public static readonly IReadOnlyList<string> OffSheet = new[] { "group.settings", "group.options" };

        public static readonly IReadOnlyList<ForskAction> All = new List<ForskAction>
        {
            new ForskAction("file.sample", Runs.Run, "group.start", f => f.Kind == FileKind.Empty),
            new ForskAction("file.import", Runs.Run, "group.start", f => !f.HasWalls),
            new ForskAction("file.use_curves", Runs.Run, "group.start", f => f.Kind == FileKind.Foreign),
            new ForskAction("file.scale", Runs.Run, "group.start", f => f.HasUnderlay && !f.ScaleSet),
            new ForskAction("file.check", Runs.Card, "group.start", f => f.ReviewStored && !f.HasWalls),
            new ForskAction("file.draw", Runs.Run, "group.start", f => !f.HasWalls),
            new ForskAction("file.generate", Runs.Run, "group.model", f => f.HasPlanCurves && !f.HasGenerated),
            new ForskAction("file.rebuild", Runs.Run, "group.model", f => f.HasGenerated),
            new ForskAction("plan.show", Runs.Run, "group.model", f => f.PlanHidden),
            new ForskAction("plan.hide", Runs.Run, "group.model", f => f.PlanRecall && !f.PlanHidden),
            new ForskAction("wall.split", Runs.Run, "group.model", f => f.WholeWalls),
            new ForskAction("wall.move", Runs.Run, "group.model", f => f.Picked == Picked.Wall),
            new ForskAction("wall.drag", Runs.Run, "group.model", f => f.Picked == Picked.Wall && f.PickedCount == 1),
            new ForskAction("wall.delete", Runs.Run, "group.model", f => f.Picked == Picked.Wall),
            // Draw tools: click the walls or the stair in the Top view.
            new ForskAction("wall.draw", Runs.Run, "group.model", f => true),
            new ForskAction("stair.draw", Runs.Run, "group.model", f => f.HasWalls),
            new ForskAction("exist.mark", Runs.Run, "group.model", f => f.Picked == Picked.Loose),
            // Exterior render: the building from the north, east, south or west, at eye height (Julian, 2026-10-08).
            new ForskAction("view.exterior", Runs.Card, "group.model", f => f.HasWalls),
            new ForskAction("edit.undo", Runs.Run, "group.model", f => f.UndoNewest),
            // R5: a stair along the one wall picked. With nothing picked, Draw stair is the way.
            new ForskAction("stair.add", Runs.Run, "group.model", f => f.HasWalls && f.Picked == Picked.Wall && f.PickedCount == 1),
            new ForskAction("stair.edit", Runs.Card, "group.model", f => f.Picked == Picked.Stair && f.PickedCount == 1),
            new ForskAction("stair.delete", Runs.Run, "group.model", f => f.Picked == Picked.Stair),
            new ForskAction("opening.move", Runs.Prefill, "group.model", f => f.Picked == Picked.Opening),
            new ForskAction("opening.resize", Runs.Prefill, "group.model", f => f.Picked == Picked.Opening),
            // One kind of opening picked: those. Nothing picked: all of a kind (the card's pills say which).
            new ForskAction("opening.type", Runs.Card, "group.model", f => f.Picked == Picked.Opening && f.PickedOpeningKind != null
                || f.Picked == Picked.None && (f.HasDoors || f.HasWindows)),
            new ForskAction("opening.delete", Runs.Run, "group.model", f => f.Picked == Picked.Opening),
            new ForskAction("opening.add_door", Runs.Ask, "group.model", f => f.Picked == Picked.Wall),
            new ForskAction("daylight.window", Runs.Ask, "group.model", f => f.HasWalls && f.HasRooms && !f.HasWindows && f.Map == MapState.None),
            new ForskAction("daylight.rooms", Runs.Run, "group.rooms", f => f.HasWalls && !f.HasRooms && f.Map == MapState.None),
            new ForskAction("rooms.list", Runs.Card, "group.rooms", f => f.HasRooms),
            // AI detection misses rooms: click a room's corners to draw its area, or redraw the picked room.
            new ForskAction("room.draw", Runs.Run, "group.rooms", f => f.HasWalls),
            new ForskAction("room.redraw", Runs.Run, "group.rooms", f => f.Picked == Picked.Room && f.PickedCount == 1),
            new ForskAction("room.set_type", Runs.Prefill, "group.rooms", f => f.Picked == Picked.Room && ForskPick.UntypedRoom(f.Selected)),
            new ForskAction("room.push_pull", Runs.Prefill, "group.rooms", f => f.Picked == Picked.Room && f.PickedCount == 1 && f.JoinGraph),
            // FU: a piece from the catalogue into a room, and furnishing by rules (the picked room, else every room) with the two previews.
            // Interior render: a first interior shot of the one room picked, at 1.2 m.
            new ForskAction("room.inside", Runs.Card, "group.rooms", f => f.HasRooms),
            new ForskAction("furniture.add", Runs.Card, "group.rooms", f => f.HasRooms),
            new ForskAction("furniture.furnish", Runs.Run, "group.rooms", f => f.HasRooms),
            new ForskAction("file.print", Runs.Run, "group.print", f => f.HasWalls),
            new ForskAction("print.one", Runs.Card, "group.print", f => f.HasWalls),
            new ForskAction("print.pages", Runs.Card, "group.print", f => f.HasWalls),
            // Every pick a wall, a door or a window: each gets its detail drawings on a detail sheet.
            new ForskAction("detail.add", Runs.Run, "group.print", f => f.PickedCount >= 1
                && f.PickedWalls + f.PickedOpenings == f.PickedCount),
            new ForskAction("detail.list", Runs.Card, "group.print", f => f.Details.Count > 0),
            // R3: the set as DWG, one file per sheet. Also a pill on Choose sheets, and the bar's next step after a Print.
            new ForskAction("export.dwg", Runs.Run, "group.print", f => f.HasWalls),
            // R4: the model as IFC4, one file.
            new ForskAction("export.ifc", Runs.Run, "group.print", f => f.HasWalls),
            // A render view on screen: save it as an image (Julian, 2026-10-08).
            new ForskAction("view.export", Runs.Run, "group.print", f => f.RenderView != null),
            // N2: the takeoff as one CSV table. Print and Export DWG also write it beside their files.
            new ForskAction("export.csv", Runs.Run, "group.print", f => f.HasWalls),
            // Like area.stats: the tool runs, then its receipt and the card of its lines.
            new ForskAction("takeoff", Runs.Run, "group.print", f => f.HasWalls),
            new ForskAction("meta.title", Runs.Card, "group.settings", f => true),
            new ForskAction("print.clear", Runs.Card, "group.print", f => f.Layouts > 0),
            new ForskAction("sheets.clear", Runs.Card, "group.print", f => f.HasSheetCache),
            // The Analyses menu (Live switches, last results) from More actions in every role; the Analyser face opens it too.
            new ForskAction("analysis.menu", Runs.Card, "group.analyses", f => f.HasWalls),
            new ForskAction("daylight.run", Runs.Run, "group.analyses", f => f.HasWalls && f.HasRooms && f.HasWindows && f.Map == MapState.None),
            new ForskAction("daylight.again", Runs.Run, "group.analyses", f => f.Map == MapState.Stale),
            new ForskAction("daylight.hide", Runs.Run, "group.analyses", f => f.Map == MapState.Shown),
            new ForskAction("daylight.show", Runs.Run, "group.analyses", f => f.Map == MapState.Hidden),
            new ForskAction("daylight.room", Runs.Run, "group.analyses", f => f.Picked == Picked.Room),
            new ForskAction("daylight.quality", Runs.Card, "group.settings", f => f.HasWalls),
            // Areas sit with daylight: both are analyses.
            new ForskAction("area.stats", Runs.Run, "group.analyses", f => f.HasRooms),
            // AN.3: the analysis that just ran goes into the Analysis set; AN.4 picks the set's analyses and prints it.
            new ForskAction("analysis.add", Runs.Run, "group.analyses", f => f.Analysed != null && !(f.Analysis?.InSet(f.Analysed) ?? false)),
            new ForskAction("analysis.print", Runs.Card, "group.analyses", f => f.HasWalls && f.HasRooms),
            // AN.5 and AN.6: the model saved as option A, B, … beside the 3dm, and an option against the model now.
            // One Options entry on the sheet and in the Analyses menu opens these four.
            new ForskAction("options", Runs.Card, "group.analyses", f => f.HasWalls && f.Saved || f.Options != null && f.Options.Count > 0),
            new ForskAction("option.save", Runs.Run, "group.options", f => f.HasWalls && f.Saved),
            new ForskAction("option.compare", Runs.Card, "group.options", f => f.Options != null && f.Options.Count > 0),
            new ForskAction("option.restore", Runs.Card, "group.options", f => f.Options != null && f.Options.Count > 0),
            new ForskAction("option.delete", Runs.Card, "group.options", f => f.Options != null && f.Options.Count > 0),
            new ForskAction("section.add", Runs.Run, "group.print", f => f.HasWalls),
            new ForskAction("section.room", Runs.Run, "group.print", f => f.Picked == Picked.Room && f.PickedCount == 1),
            new ForskAction("section.remove", Runs.Card, "group.print", f => f.Sections > 0),
            new ForskAction("ink.set", Runs.Card, "group.settings", f => f.HasWalls),
            // A clean slate (Julian, 2026-10-09): the thread and the chat model's memory go, the file stays.
            new ForskAction("chat.clear", Runs.Run, "group.chat", f => true),
            new ForskAction("bridge.start", Runs.Card, "group.settings", f => !f.ListenerUp),
            new ForskAction("help.card", Runs.Card, null, f => true)
        };

        /// <summary>The one daylight action for the bar, by rooms, windows and the map.</summary>
        static readonly string[] DaylightOrder = { "daylight.again", "daylight.hide", "daylight.show", "daylight.run", "daylight.window", "daylight.rooms" };

        public static ForskAction Find(string id)
        {
            foreach (var action in All)
                if (string.Equals(action.Id, id, StringComparison.Ordinal)) return action;
            return null;
        }

        /// <summary>Slot 1, from the file state alone.</summary>
        public static ForskAction Slot1(FileFacts f)
        {
            switch (f.Kind)
            {
                case FileKind.Model: return Find("file.print");
                case FileKind.Partial: return Find("file.rebuild");
                case FileKind.Unscaled: return Find("file.scale");
                case FileKind.Plan: return Find("file.generate");
                case FileKind.Foreign: return Find("file.use_curves");
                default: return Find("file.import");
            }
        }

        /// <param name="role">
        /// The user's role pick, or None. It is a small boost: slot 2 stays the
        /// first eligible action, and slot 3 may go to that role's first eligible
        /// action instead of the next one. Slot 1 never reads it.
        /// </param>
        public static BarView Bar(FileFacts f, ForskRole role = ForskRole.None)
        {
            // While a wall or an opening is selected no file-level action fills a slot: the
            // candidates' own conditions already keep them out (RegistryTests checks it).
            var bar = new BarView { Slot1 = Slot1(f) };
            var eligible = new List<ForskAction>();
            foreach (var action in Candidates(f, bar.Slot1))
            {
                if (action == null || action == bar.Slot1 || eligible.Contains(action)) continue;
                if (action.Shows(f)) eligible.Add(action);
            }
            if (eligible.Count > 0) bar.Context.Add(eligible[0]);
            if (eligible.Count > 1)
            {
                var boosted = role == ForskRole.None ? null : eligible.Skip(1).FirstOrDefault(a => ForskRoles.OfAction(a.Id) == role);
                bar.Context.Add(boosted ?? eligible[1]);
            }
            // One wall, door or window keeps Move and Drag (or Change type). Add detail sits beside them.
            var detail = Find("detail.add");
            if (detail.Shows(f) && bar.Context.Count < 3 && !bar.Context.Contains(detail))
                bar.Context.Add(detail);
            bar.Reason = Reason(f, bar);
            return bar;
        }

        static IEnumerable<ForskAction> Candidates(FileFacts f, ForskAction slot1)
        {
            if (f.RenderView != null && f.Picked == Picked.None) yield return Find("view.export");
            if (f.JustPrinted && f.Picked == Picked.None) yield return Find("export.dwg");
            if (f.Picked == Picked.None) yield return Find("analysis.add");
            if (f.OfferArea && f.HasRooms && f.Picked == Picked.None) yield return Find("area.stats");
            // Walls with doors and windows: Add detail is the one thing for them all.
            if (f.Picked == Picked.Other) yield return Find("detail.add");
            if (f.UndoNewest && f.Picked == Picked.None) yield return Find("edit.undo");
            // After AI detection and Generate 3D: rooms come from the walls, and Draw area makes the ones they do not close.
            if (f.HasUnderlay && f.HasWalls && f.Picked == Picked.None) yield return Find("room.draw");
            if (f.Picked == Picked.None && f.PlanHidden) yield return Find("plan.show");
            else if (f.Picked == Picked.None && f.PlanRecall && !f.PlanHidden) yield return Find("plan.hide");
            if (f.Picked == Picked.Opening) yield return Find("opening.move");
            if (f.Picked == Picked.Stair) yield return Find("stair.edit");
            if (f.Picked == Picked.Stair) yield return Find("stair.delete");
            if (f.Picked == Picked.Wall) yield return Find("wall.move");
            // One straight run: Drag a face is the next suggestion, so the bar shows it.
            // A whole record is not one run, and the bar stays Move, then Add a door.
            if (ForskPick.OneRunWall(f.Selected) != null) yield return Find("wall.drag");
            // Two or more walls: Move, then Add detail (Add a door is a one-wall act).
            if (f.Picked == Picked.Wall && f.PickedCount >= 2) yield return Find("detail.add");
            if (f.Picked == Picked.Opening) yield return Find("opening.type");
            // A room with no type yet: Set room type writes the start of the sentence into the chat box.
            if (f.Picked == Picked.Room && ForskPick.UntypedRoom(f.Selected)) yield return Find("room.set_type");
            // AI detection often gets a room's outline wrong: Redraw area is the first suggestion for one room.
            if (f.Picked == Picked.Room && f.PickedCount == 1) yield return Find("room.redraw");
            // A picked room is furnished next; Daylight for this room stays on the card.
            if (f.Picked == Picked.Room) yield return Find("furniture.add");
            if (f.Picked == Picked.Room) yield return Find("section.room");
            if (f.Picked == Picked.Room) yield return Find("room.push_pull");
            if (f.HasWalls && f.Picked != Picked.Opening && f.Picked != Picked.Wall && f.Picked != Picked.Room)
                yield return DaylightAction(f);
            if (f.HasWalls && f.Picked == Picked.None) yield return Find("section.add");
            if (f.Picked == Picked.Wall) yield return Find("opening.add_door");
            if (slot1.Id == "file.scale" && f.HasPlanCurves) yield return Find("file.generate");
            if (f.ReviewStored && !f.HasWalls) yield return Find("file.check");
            if (f.Kind == FileKind.Empty) yield return Find("file.sample");
            if (!f.HasWalls) yield return Find("file.draw");
            if (f.Picked == Picked.Loose) yield return Find("exist.mark");
        }

        /// <summary>"clear chat" (and "tøm chatten"): Clear chat, typed.</summary>
        public static ForskAction ByChatPhrase(string text)
        {
            var typed = string.Join(" ", (text ?? "").Trim().TrimEnd('.', '!').ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            switch (typed)
            {
                case "clear chat":
                case "clear the chat":
                case "tøm chat":
                case "tøm chatten":
                    return Find("chat.clear");
                default:
                    return null;
            }
        }

        /// <summary>"draw a wall", "draw stairs": the draw tools by name, wherever they sit on the bar.</summary>
        public static ForskAction ByDrawPhrase(string text)
        {
            var typed = string.Join(" ", (text ?? "").Trim().TrimEnd('.', '!').ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            switch (typed)
            {
                case "draw wall":
                case "draw walls":
                case "draw a wall":
                    return Find("wall.draw");
                case "draw stair":
                case "draw stairs":
                case "draw a stair":
                    return Find("stair.draw");
                default:
                    return null;
            }
        }

        /// <summary>
        /// Typing a slot's exact English label and pressing Enter fires that
        /// slot. Case and a closing full stop do not matter; nothing else does.
        /// </summary>
        public static ForskAction ByLabel(BarView bar, string text)
        {
            var typed = (text ?? "").Trim().TrimEnd('.').Trim();
            if (typed.Length == 0 || bar == null) return null;
            return bar.Slots.FirstOrDefault(a => string.Equals(a.Label, typed, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The one daylight action that is true now, or null.</summary>
        public static ForskAction DaylightAction(FileFacts f)
        {
            foreach (var id in DaylightOrder)
            {
                var action = Find(id);
                if (action.When(f)) return action;
            }
            return null;
        }

        /// <summary>The first true of: stale sheets, a stale map, a wall Move in the bar, then slot 1's own reason.</summary>
        static string Reason(FileFacts f, BarView bar)
        {
            var print = bar.Slot1.Id == "file.print";
            if (print && f.SheetsStale) return ForskText.Get("file.print.stale");
            if (f.Map == MapState.Stale) return ForskText.Get("daylight.again.reason");
            if (bar.Context.Any(a => a.Id == "view.export")) return ForskText.Get("view.export.reason");
            if (bar.Context.Any(a => a.Id == "room.set_type")) return ForskText.Get("room.set_type.reason");
            if (bar.Context.Any(a => a.Id == "wall.move"))
                return ForskText.Get(ForskPick.OneRunWall(f.Selected) != null ? "wall.move.reason.one" : "wall.move.reason");
            if (print && f.Map == MapState.Shown) return ForskText.Get("file.print.map");
            if (bar.Slot1.Id == "file.import" && f.Kind == FileKind.NoPlan) return ForskText.Get("file.import.noplan");
            return ForskText.Get(bar.Slot1.Id + ".reason");
        }

        public static HelpView Card(FileFacts f)
        {
            var help = new HelpView();
            foreach (var key in GroupOrder)
            {
                var actions = All.Where(a => a.Group == key && a.Shows(f)).ToList();
                if (actions.Count > 0) help.Groups.Add(new CardGroup { Title = ForskText.Get(key), Actions = actions });
            }
            if (!f.Millimetres) help.Hints.Add("hint.units");
            if (!f.KeyPresent) help.Hints.Add("hint.key");
            if (f.Kind == FileKind.Unscaled) help.Hints.Add("hint.scale");
            if (f.HasWalls && f.HasRooms && !f.HasWindows && f.Map == MapState.None) help.Hints.Add("hint.windows");
            if (Find("file.generate").Shows(f) || Find("file.rebuild").Shows(f)) help.Hints.Add("hint.heights");
            return help;
        }

        /// <summary>The one local sentence on an empty thread. It names the file state and needs no key.</summary>
        public static string StateSentence(FileFacts f)
        {
            if (!f.Millimetres) return ForskText.Get("state.units");
            switch (f.Kind)
            {
                case FileKind.Empty: return ForskText.Get("state.empty");
                case FileKind.Foreign: return ForskText.Get("state.foreign");
                case FileKind.Unscaled: return ForskText.Get("state.unscaled");
                case FileKind.Plan: return ForskText.Get("state.plan");
                case FileKind.Partial: return ForskText.Get("state.partial");
                case FileKind.Model: return ForskText.Get("state.model");
                default: return ForskText.Get("state.noplan");
            }
        }

        /// <summary>
        /// The status line: a non-default ink. Empty when there is nothing to say.
        /// The bridge is for outside AI apps (MCP); Forsk itself never needs it, so
        /// a stopped bridge is no status (Julian, 2026-10-07: "Bridge off" read as broken).
        /// Settings keeps Start bridge for those apps.
        /// </summary>
        public static string Status(FileFacts f)
        {
            var parts = new List<string>();
            if (!string.Equals(f.Ink, "default", StringComparison.OrdinalIgnoreCase))
                parts.Add(ForskText.Format("status.ink", "ink", f.Ink));
            // Hooks for later jobs: a Render job and the v4 grade. Both are empty until those exist.
            if (!string.IsNullOrWhiteSpace(f.RenderJob)) parts.Add(f.RenderJob);
            if (!string.IsNullOrWhiteSpace(f.Grade)) parts.Add(f.Grade);
            return string.Join(" · ", parts);
        }
    }
}
