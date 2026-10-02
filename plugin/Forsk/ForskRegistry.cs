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

    /// <summary>The pinned bar: slot 1, up to two contextual slots, then "?". One reason line under it.</summary>
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
                ["reason"] = ForskText.Get("bar.because") + " " + LowerFirst(Reason),
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
            return new JObject { ["groups"] = groups, ["hints"] = new JArray(Hints.Select(ForskText.Get)) };
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
        public static readonly IReadOnlyList<string> GroupOrder = new[]
        {
            "group.start", "group.import", "group.model", "group.openings", "group.rooms",
            "group.print", "group.daylight", "group.sections", "group.profiles"
        };

        public static readonly IReadOnlyList<ForskAction> All = new List<ForskAction>
        {
            new ForskAction("file.import", Runs.Run, "group.import", f => !f.HasWalls),
            new ForskAction("file.use_curves", Runs.Run, "group.import", f => f.Kind == FileKind.Foreign),
            new ForskAction("file.scale", Runs.Run, "group.import", f => f.HasUnderlay && !f.ScaleSet),
            new ForskAction("file.check", Runs.Card, "group.import", f => f.ReviewStored && !f.HasWalls),
            new ForskAction("file.draw", Runs.Run, "group.import", f => !f.HasWalls),
            new ForskAction("file.generate", Runs.Run, "group.model", f => f.HasPlanCurves && !f.HasGenerated),
            new ForskAction("file.rebuild", Runs.Run, "group.model", f => f.HasGenerated),
            new ForskAction("wall.move", Runs.Prefill, "group.model", f => f.Picked == Picked.Wall),
            new ForskAction("wall.delete", Runs.Run, "group.model", f => f.Picked == Picked.Wall),
            new ForskAction("exist.mark", Runs.Run, "group.model", f => f.Picked == Picked.Loose),
            new ForskAction("edit.undo", Runs.Run, "group.model", f => f.UndoNewest),
            new ForskAction("opening.move", Runs.Prefill, "group.openings", f => f.Picked == Picked.Opening),
            new ForskAction("opening.resize", Runs.Prefill, "group.openings", f => f.Picked == Picked.Opening),
            new ForskAction("opening.type", Runs.Card, "group.openings", f => f.Picked == Picked.Opening && f.PickedOpeningKind != null),
            new ForskAction("opening.delete", Runs.Run, "group.openings", f => f.Picked == Picked.Opening),
            new ForskAction("opening.add_door", Runs.Ask, "group.openings", f => f.Picked == Picked.Wall),
            new ForskAction("daylight.window", Runs.Ask, "group.openings", f => f.HasWalls && f.HasRooms && !f.HasWindows && f.Map == MapState.None),
            new ForskAction("daylight.rooms", Runs.Run, "group.rooms", f => f.HasWalls && !f.HasRooms && f.Map == MapState.None),
            new ForskAction("rooms.list", Runs.Card, "group.rooms", f => f.HasRooms),
            new ForskAction("file.print", Runs.Run, "group.print", f => f.HasWalls),
            new ForskAction("print.one", Runs.Card, "group.print", f => f.HasWalls),
            new ForskAction("meta.title", Runs.Card, "group.print", f => true),
            new ForskAction("print.clear", Runs.Card, "group.print", f => f.Layouts > 0),
            new ForskAction("sheets.clear", Runs.Card, "group.print", f => f.HasSheetCache),
            new ForskAction("daylight.run", Runs.Run, "group.daylight", f => f.HasWalls && f.HasRooms && f.HasWindows && f.Map == MapState.None),
            new ForskAction("daylight.again", Runs.Run, "group.daylight", f => f.Map == MapState.Stale),
            new ForskAction("daylight.hide", Runs.Run, "group.daylight", f => f.Map == MapState.Shown),
            new ForskAction("daylight.show", Runs.Run, "group.daylight", f => f.Map == MapState.Hidden),
            new ForskAction("daylight.room", Runs.Run, "group.daylight", f => f.Picked == Picked.Room),
            new ForskAction("section.add", Runs.Run, "group.sections", f => f.HasWalls),
            new ForskAction("section.room", Runs.Run, "group.sections", f => f.Picked == Picked.Room && f.PickedCount == 1),
            new ForskAction("section.remove", Runs.Card, "group.sections", f => f.Sections > 0),
            new ForskAction("ink.set", Runs.Card, "group.profiles", f => f.HasWalls),
            new ForskAction("bridge.start", Runs.Card, "group.start", f => !f.ListenerUp),
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

        public static BarView Bar(FileFacts f)
        {
            // While a wall or an opening is selected no file-level action fills a slot: the
            // candidates' own conditions already keep them out (RegistryTests checks it).
            var bar = new BarView { Slot1 = Slot1(f) };
            foreach (var action in Candidates(f, bar.Slot1))
            {
                if (bar.Context.Count == 2) break;
                if (action == null || action == bar.Slot1 || bar.Context.Contains(action)) continue;
                if (!action.Shows(f)) continue;
                bar.Context.Add(action);
            }
            bar.Reason = Reason(f, bar);
            return bar;
        }

        static IEnumerable<ForskAction> Candidates(FileFacts f, ForskAction slot1)
        {
            if (f.UndoNewest && f.Picked == Picked.None) yield return Find("edit.undo");
            if (f.Picked == Picked.Opening) yield return Find("opening.move");
            if (f.Picked == Picked.Wall) yield return Find("wall.move");
            if (f.Picked == Picked.Opening) yield return Find("opening.resize");
            if (f.Picked == Picked.Room) yield return Find("daylight.room");
            if (f.Picked == Picked.Room) yield return Find("section.room");
            if (f.HasWalls && f.Picked != Picked.Opening && f.Picked != Picked.Wall && f.Picked != Picked.Room)
                yield return DaylightAction(f);
            if (f.HasWalls && f.Picked == Picked.None) yield return Find("section.add");
            if (f.Picked == Picked.Wall) yield return Find("opening.add_door");
            if (slot1.Id == "file.scale" && f.HasPlanCurves) yield return Find("file.generate");
            if (f.ReviewStored && !f.HasWalls) yield return Find("file.check");
            if (!f.HasWalls) yield return Find("file.draw");
            if (f.Picked == Picked.Loose) yield return Find("exist.mark");
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
            if (bar.Context.Any(a => a.Id == "wall.move")) return ForskText.Get("wall.move.reason");
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

        /// <summary>The status line: listener down, and a non-default ink. Empty when there is nothing to say.</summary>
        public static string Status(FileFacts f)
        {
            var parts = new List<string>();
            if (!f.ListenerUp) parts.Add(ForskText.Get("status.bridge"));
            if (!string.Equals(f.Ink, "default", StringComparison.OrdinalIgnoreCase))
                parts.Add(ForskText.Format("status.ink", "ink", f.Ink));
            return string.Join(" · ", parts);
        }
    }
}
