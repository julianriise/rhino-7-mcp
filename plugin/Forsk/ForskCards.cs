using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>One labelled field on a card.</summary>
    public sealed class CardField
    {
        public string Key;
        public string Label;
        public string Value;
        public string Unit;
        /// <summary>A checkbox. Value "1" is ticked. The page posts "1" or "0".</summary>
        public bool Check;
        /// <summary>A dropdown. The page posts the chosen text.</summary>
        public List<string> Options;
        /// <summary>A multi-line field.</summary>
        public bool Long;
        /// <summary>The row moves with ↑ ↓, and the page posts the rows' order with the values.</summary>
        public bool Order;
        /// <summary>Grey hint shown while Value is empty. The page does not submit it.</summary>
        public string Placeholder;
        /// <summary>A password input: what is typed shows as dots. Its value is never stored on the card.</summary>
        public bool Secret;
    }

    /// <summary>
    /// A one-time question: the question, its pills (the first is filled),
    /// its fields or rows, and what makes it stale. Selection: a new selection.
    /// Model: a changed object. None: it stays until answered or closed.
    /// </summary>
    public sealed class CardSpec
    {
        public string Kind;
        public string Question;
        public List<CardPill> Pills = new List<CardPill>();
        public List<CardField> Fields;
        public List<string> Rows;
        /// <summary>What's new: one slide per release, each a list of items with a title and a how.</summary>
        public List<WhatsNewSlide> Slides;
        public string Note;
        public string Depends = "none";
        public JObject Data;
        /// <summary>Drawn in the panel under the top bar, not in the thread (AN.1's menu).</summary>
        public bool Pin;
        /// <summary>
        /// A choice card: each option pill applies at once and keeps the card
        /// open, filled; Confirm ("done") closes it with no chat message
        /// (Julian, 2026-10-07). Cancel closes it too.
        /// </summary>
        public bool Choice;
    }

    /// <summary>The cards' contents from the classifier's facts. No RhinoCommon, so they test from fixtures.</summary>
    public static class ForskCards
    {
        public const int MaxPages = 24;

        /// <summary>The card a registry card action opens, or null for help, the bridge, and an action that is not a card.</summary>
        public static CardSpec For(string actionId, FileFacts f, DateTime? today = null)
        {
            switch (actionId)
            {
                case "opening.type": return SwapType(f);
                case "file.check": return Review(f);
                case "ink.set": return Ink(f);
                case "daylight.quality": return Quality(f);
                case "meta.title": return TitleBlock(f, today: today);
                case "print.one": return PrintOne(f);
                case "print.pages": return Pages(f);
                case "analysis.print": return AnalysisSet(f);
                case "option.compare":
                case "option.restore":
                case "option.delete":
                    return OptionPick(f, actionId);
                case "room.inside": return JumpInside(f);
                case "view.exterior": return ExteriorRender(f);
                case "options": return OptionsMenu(f);
                case "analysis.menu": return Analyser(f);
                case "print.clear": return Confirm("print.clear", "print.clear.ask");
                case "sheets.clear": return Confirm("sheets.clear", "sheets.clear.ask");
                case "rooms.list": return Rooms(f);
                case "section.remove": return RemoveSection(f);
                case "detail.list": return DetailList(f);
                case "stair.edit": return EditStair(f);
                case "furniture.add": return AddFurniture(f);
                default: return null;
            }
        }

        /// <summary>
        /// Openings of one kind picked: that kind's types, for the picked ones.
        /// Nothing picked: every type of the kinds the file has, and a pill
        /// changes all of its kind (Data all). Each type pill has its icon.
        /// </summary>
        public static CardSpec SwapType(FileFacts f)
        {
            if (f == null) return null;
            var kinds = new List<string>();
            string question;
            if (f.Picked == Picked.Opening && f.PickedOpeningKind != null)
            {
                // The picked kind's types first, then the other kind's: AI detection reads doors as windows.
                kinds.Add(f.PickedOpeningKind);
                kinds.Add(f.PickedOpeningKind == "door" ? "window" : "door");
                question = ForskText.Get("opening.type.ask");
            }
            else if (f.Picked == Picked.None && (f.HasDoors || f.HasWindows))
            {
                if (f.HasDoors) kinds.Add("door");
                if (f.HasWindows) kinds.Add("window");
                question = ForskText.Get(kinds.Count == 2 ? "opening.type.all.ask" : "opening.type.all." + kinds[0]);
            }
            else return null;
            // A choice card: a type applies at once (one Undo each) and Confirm alone closes it.
            var card = new CardSpec { Kind = "opening.type", Question = question, Depends = "selection", Choice = true };
            if (f.Picked == Picked.None) card.Data = new JObject { ["all"] = true };
            foreach (var type in kinds.SelectMany(k => OpeningTypes.All.Where(t => t.Kind == k)))
                card.Pills.Add(new CardPill(type.Id, type.Label, type.Id));
            card.Pills.Add(new CardPill("done", ForskText.Get("word.confirm")));
            return card;
        }

        /// <summary>
        /// R5: the picked stair's sizes as fields (width, step height at most,
        /// going), with Save, Flip and Cancel. Null unless one stair is picked.
        /// </summary>
        public static CardSpec EditStair(FileFacts f)
        {
            if (f == null || f.Picked != Picked.Stair || f.Selected.Count != 1) return null;
            var row = f.Selected[0];
            CardField Size(string key, string raw, double fallback) => new CardField
            {
                Key = key,
                Label = ForskText.Get("stair." + key),
                Value = Stairs.Mm(StairMm(raw) ?? fallback),
                Unit = "mm"
            };
            return new CardSpec
            {
                Kind = "stair.edit",
                Question = ForskText.Get("stair.edit.ask"),
                Depends = "selection",
                Fields = new List<CardField>
                {
                    Size("width", row.Width, Stairs.WidthDefault),
                    Size("riser_max", row.RiserMax, Stairs.RiserMaxDefault),
                    Size("going", row.Going, Stairs.GoingDefault)
                },
                Pills =
                {
                    new CardPill("save", ForskText.Get("word.save")),
                    new CardPill("flip", ForskText.Get("stair.flip")),
                    new CardPill("cancel", ForskText.Get("word.cancel"))
                }
            };
        }

        /// <summary>
        /// More actions → Add furniture: the catalogue's pieces by name, and the
        /// room (the picked one first, else the file's rooms by name; none to
        /// choose when there is one). Add runs add_furniture.
        /// </summary>
        public static CardSpec AddFurniture(FileFacts f)
        {
            if (f == null || !f.HasRooms) return null;
            var card = new CardSpec
            {
                Kind = "furniture.add",
                Question = ForskText.Get("furniture.add.ask"),
                Note = ForskText.Get("furniture.add.note"),
                Fields = new List<CardField>
                {
                    new CardField
                    {
                        Key = "item",
                        Label = ForskText.Get("furniture.add.item"),
                        Options = Functions.Furniture.All.Select(p => p.Name).ToList(),
                        Value = Functions.Furniture.All.First(p => p.Default && p.Type == "bed" && p.W >= 1400).Name
                    }
                },
                Pills =
                {
                    new CardPill("add", ForskText.Get("furniture.add")),
                    new CardPill("cancel", ForskText.Get("word.cancel"))
                }
            };
            var rooms = new List<string>();
            if (f.Picked == Picked.Room) rooms.Add(ForskText.Get("furniture.add.picked"));
            rooms.AddRange(f.Rooms.Select(RoomName).Where(n => n.Length > 0).Distinct());
            if (rooms.Count > 1)
                card.Fields.Add(new CardField { Key = "room", Label = ForskText.Get("furniture.add.room"), Options = rooms, Value = rooms[0] });
            return card;
        }

        /// <summary>"Stue" from the facts' room line "Stue · 24.5 m² (living)".</summary>
        public static string RoomName(string line)
        {
            var name = (line ?? "").Split('·')[0].Trim();
            var paren = name.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? name.Substring(0, paren).Trim() : name;
        }

        /// <summary>The add_furniture arguments for the Add pill: the piece by name, and the room unless it is the picked one.</summary>
        public static JObject AddFurnitureArgs(JObject values)
        {
            var args = new JObject { ["item"] = (values?["item"]?.ToString() ?? "").Trim() };
            var room = (values?["room"]?.ToString() ?? "").Trim();
            if (room.Length > 0 && room != ForskText.Get("furniture.add.picked")) args["room"] = room;
            return args;
        }

        /// <summary>
        /// The edit_stair arguments for a Save or a Flip: only the sizes typed
        /// different from the card's own values. Null when nothing changed.
        /// </summary>
        public static JObject StairArgs(string pillId, JObject values, JArray fields)
        {
            if (pillId == "flip") return new JObject { ["flip"] = true };
            if (pillId != "save") return null;
            var args = new JObject();
            foreach (var field in fields ?? new JArray())
            {
                var key = field?["key"]?.ToString();
                if (string.IsNullOrEmpty(key)) continue;
                var typed = StairMm(values?[key]?.ToString());
                var was = StairMm(field["value"]?.ToString());
                if (typed.HasValue && (!was.HasValue || Math.Abs(typed.Value - was.Value) >= 0.5)) args[key] = typed.Value;
            }
            return args.Count == 0 ? null : args;
        }

        static double? StairMm(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var text = raw.Trim().Replace(',', '.');
            if (text.EndsWith("mm", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 2).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : (double?)null;
        }

        /// <summary>
        /// One tick per detail ("North wall · plan, section"). Save removes the
        /// unticked ones, Remove all removes every one. Null when there are none.
        /// </summary>
        public static CardSpec DetailList(FileFacts f)
        {
            if (f?.Details == null || f.Details.Count == 0) return null;
            var card = new CardSpec
            {
                Kind = "detail.list",
                Question = ForskText.Get("detail.list.ask"),
                Fields = new List<CardField>(),
                Depends = "model",
                Pills =
                {
                    new CardPill("save", ForskText.Get("word.save")),
                    new CardPill("remove_all", ForskText.Get("word.remove_all")),
                    new CardPill("cancel", ForskText.Get("word.cancel"))
                }
            };
            foreach (var record in f.Details)
            {
                var views = string.Join(", ", Details.Views(record).Select(v => ForskText.Get("detail." + v)));
                card.Fields.Add(new CardField { Key = record.Id, Label = DetailName(f, record) + " · " + views, Check = true, Value = "1" });
            }
            return card;
        }

        /// <summary>The detail's element as the model names it, else its stored id ("Wall W03").</summary>
        static string DetailName(FileFacts f, Details.Record record)
        {
            if (f.DetailNames != null && f.DetailNames.TryGetValue(record.Id, out var name) && !string.IsNullOrWhiteSpace(name)) return name;
            return record.Wall != null ? "Wall " + record.Wall.ToUpperInvariant() : ForskText.Get("pick.opening");
        }

        /// <summary>The rows a Save on a tick list removes: the unticked ones.</summary>
        public static JArray Unticked(JObject values)
        {
            var ids = new JArray();
            foreach (var pair in values ?? new JObject())
                if (pair.Value?.ToString() == "0") ids.Add(pair.Key);
            return ids;
        }

        /// <summary>The import's review rows as stored on the underlay. Absent when none were stored.</summary>
        public static CardSpec Review(FileFacts f)
        {
            if (f?.Review == null || f.Review.Count == 0) return null;
            return new CardSpec
            {
                Kind = "file.check",
                Question = ForskText.Get("file.check.ask"),
                Rows = new List<string>(f.Review),
                Pills = { new CardPill("done", ForskText.Get("word.done")) }
            };
        }

        /// <summary>⋯ → Daylight quality: Low, Medium, High, with the one saved on this Mac named.</summary>
        public static CardSpec Quality(FileFacts f)
        {
            var now = DaylightQuality.Normal(f?.DaylightQuality);
            var card = new CardSpec
            {
                Kind = "daylight.quality",
                Question = ForskText.Get("daylight.quality.ask"),
                Note = ForskText.Format("daylight.quality.now", "quality", ForskText.Get("daylight.quality." + now)),
                Choice = true
            };
            // The saved quality is the filled pill: a filled Low read as "it went back to Low".
            foreach (var quality in DaylightQuality.All)
                card.Pills.Add(new CardPill(quality, ForskText.Get("daylight.quality." + quality)) { Primary = quality == now });
            card.Pills.Add(new CardPill("done", ForskText.Get("word.confirm")));
            return card;
        }

        /// <summary>
        /// Interior render, a choice card: the viewport shows north as the card
        /// opens, a direction shows at once, and Confirm keeps the shot as a
        /// named view (Julian, 2026-10-07).
        /// </summary>
        public static CardSpec JumpInside(FileFacts f)
        {
            var card = new CardSpec
            {
                Kind = "room.inside",
                Question = ForskText.Get("room.inside.ask"),
                Depends = "selection",
                Note = ForskText.Format("room.inside.held", "way", "north"),
                Choice = true
            };
            foreach (var way in Functions.InteriorCamera.Directions)
                card.Pills.Add(new CardPill(way, ForskText.Get("room.inside." + way)) { Primary = way == "north" });
            card.Pills.Add(new CardPill("done", ForskText.Get("word.confirm")));
            return card;
        }

        /// <summary>
        /// Exterior render, a choice card like Interior render: the viewport
        /// shows the building from the north as the card opens, a side shows at
        /// once, and Confirm keeps the shot as a named view (Julian, 2026-10-08).
        /// </summary>
        public static CardSpec ExteriorRender(FileFacts f)
        {
            var card = new CardSpec
            {
                Kind = "view.exterior",
                Question = ForskText.Get("view.exterior.ask"),
                Note = ForskText.Format("view.exterior.held", "way", "north"),
                Choice = true
            };
            foreach (var way in Functions.InteriorCamera.Directions)
                card.Pills.Add(new CardPill(way, ForskText.Get("room.inside." + way)) { Primary = way == "north" });
            card.Pills.Add(new CardPill("done", ForskText.Get("word.confirm")));
            return card;
        }

        /// <summary>Export viewport's receipt: the file saved, Show in Finder and Done.</summary>
        public static CardSpec ViewExported(string path, int width, int height)
        {
            var card = new CardSpec
            {
                Kind = "view.exported",
                Question = ForskText.Format("view.exported", "file", System.IO.Path.GetFileName(path ?? ""), "width", width.ToString(System.Globalization.CultureInfo.InvariantCulture), "height", height.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Data = new JObject { ["path"] = path }
            };
            card.Pills.Add(new CardPill("reveal", ForskText.Get("view.exported.reveal")) { Primary = true });
            card.Pills.Add(new CardPill("done", ForskText.Get("word.done")));
            return card;
        }

        /// <summary>An option pill of an open choice card: neither Confirm nor Cancel.</summary>
        /// <summary>What Interior render says when no single room is picked; null when one is.</summary>
        public static string JumpInsideNeedsPick(FileFacts f) =>
            f != null && f.Picked == Picked.Room && f.PickedCount == 1 ? null : ForskText.Get("room.inside.pick");

        public static bool IsChoiceOption(JObject card, string pillId)
        {
            if (card?["choice"]?.Type != JTokenType.Boolean || !card["choice"].Value<bool>()) return false;
            if (pillId == "done" || pillId == "cancel") return false;
            return (card["pills"] as JArray ?? new JArray()).Any(p => p["id"]?.ToString() == pillId);
        }

        /// <summary>The option a choice card holds: the one picked, else the one it opened filled, else null.</summary>
        public static string HeldChoice(JObject card)
        {
            var held = card?["data"]?["choice"]?.ToString();
            if (!string.IsNullOrEmpty(held)) return held;
            return (card?["pills"] as JArray ?? new JArray()).OfType<JObject>()
                .FirstOrDefault(p => (bool?)p["primary"] == true)?["id"]?.ToString();
        }

        /// <summary>An option picked on an open choice card: held, filled, and said in the note.</summary>
        public static void HoldChoice(JObject card, string pillId, string note)
        {
            if (!IsChoiceOption(card, pillId)) return;
            LogoData(card)["choice"] = pillId;
            foreach (var pill in (card["pills"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (pill["id"]?.ToString() == pillId) pill["primary"] = true;
                else pill.Remove("primary");
            }
            if (note != null) card["note"] = note;
        }

        /// <summary>The Options card: the option actions this file can take now, then Done. Each pill runs its action.</summary>
        public static CardSpec OptionsMenu(FileFacts f)
        {
            var card = new CardSpec { Kind = "options", Question = ForskText.Get("options.ask"), Depends = "model" };
            foreach (var action in ForskRegistry.All.Where(a => a.Group == "group.options" && a.Shows(f)))
                card.Pills.Add(new CardPill(action.Id, ForskText.Label(action.Id)));
            card.Pills.Add(new CardPill("done", ForskText.Get("word.done")));
            return card;
        }

        /// <summary>AN.6: which saved option to compare with the model now, newest first, at most four.</summary>
        public static CardSpec OptionPick(FileFacts f, string kind = "option.compare")
        {
            var card = new CardSpec { Kind = kind, Question = ForskText.Get(kind + ".ask"), Depends = "model" };
            foreach (var name in (f?.Options ?? new List<string>()).AsEnumerable().Reverse().Take(4))
                card.Pills.Add(new CardPill(name, ForskText.Format("option.name", "name", name)));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            return card;
        }

        /// <summary>
        /// The view picker's Rename or delete (Julian, 2026-10-09): one name
        /// field per saved view, in the picker's order. Save renames the changed
        /// ones and deletes the emptied ones. Null when there are none.
        /// </summary>
        public static CardSpec SavedViews(IList<string> names)
        {
            if (names == null || names.Count == 0) return null;
            var card = new CardSpec
            {
                Kind = Functions.SavedViews.CardKind,
                Question = ForskText.Get("saved_views.ask"),
                Note = ForskText.Get("saved_views.note"),
                Fields = new List<CardField>(),
                Data = new JObject { ["names"] = new JArray(names) }
            };
            for (var i = 0; i < names.Count; i++)
                card.Fields.Add(new CardField { Key = Functions.SavedViews.FieldStem + i, Label = ForskText.Get("saved_views.name"), Value = names[i] });
            card.Pills.Add(new CardPill("save", ForskText.Get("word.save")));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            return card;
        }

        /// <summary>The compare card from compare_option's result. Null when it failed.</summary>
        public static CardSpec OptionCompareFrom(JObject envelope)
        {
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)) return null;
            var result = envelope?["result"] as JObject;
            if (result == null) return null;
            var rows = (result["rows"] as JArray ?? new JArray()).OfType<JObject>().Select(r => new Functions.OptionCompare.Row
            {
                Label = r["label"]?.ToString() ?? "",
                Option = r["option"]?.ToString() ?? "",
                Now = r["now"]?.ToString() ?? "",
                Better = r["better"]?.ToString() ?? ""
            }).ToList();
            return OptionCompare(result["name"]?.ToString() ?? "", result["summary"]?.ToString(), rows);
        }

        /// <summary>AN.6: an option against the model now, one line per row, the better daylight named.</summary>
        public static CardSpec OptionCompare(string name, string summary, IList<Functions.OptionCompare.Row> rows)
        {
            var card = new CardSpec
            {
                Kind = "option.compare.done",
                Question = ForskText.Format("option.compare.title", "name", name, "summary", (summary ?? "").TrimEnd('.')),
                Rows = new List<string>()
            };
            foreach (var row in rows ?? new List<Functions.OptionCompare.Row>())
            {
                var line = ForskText.Format("option.row", "label", row.Label, "option", row.Option, "now", row.Now).Replace("A " + row.Option, name + " " + row.Option);
                if (row.Better == "now") line += " · now is better";
                else if (row.Better == "option") line += " · " + name + " is better";
                card.Rows.Add(line);
            }
            card.Pills.Add(new CardPill("done", ForskText.Get("word.done")));
            return card;
        }

        /// <summary>
        /// AN.4 Choose analyses: a tick per analysis with what its sheet holds,
        /// ticked when it is in the Analysis set. Print stores the ticks on the
        /// file and prints the set as its own PDF; Save only stores them.
        /// </summary>
        public static CardSpec AnalysisSet(FileFacts f)
        {
            var state = f?.Analysis ?? new Functions.Analysis.State();
            var card = new CardSpec
            {
                Kind = "analysis.print",
                Question = ForskText.Get("analysis.print.ask"),
                Fields = new List<CardField>(),
                Depends = "model"
            };
            foreach (var id in Functions.Analysis.All)
                card.Fields.Add(new CardField
                {
                    Key = Functions.Analysis.SetKey(id),
                    Label = ForskText.Format("analysis.check", "name", ForskText.Get("analysis." + id), "about", ForskText.Get("analysis.about." + id)),
                    Value = state.InSet(id) ? "1" : "0",
                    Check = true
                });
            card.Pills.Add(new CardPill("print", ForskText.Label("analysis.print")) { Primary = true });
            card.Pills.Add(new CardPill("save", ForskText.Get("word.save")));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            return card;
        }

        /// <summary>The Analyser's daylight pill: the one the file's state offers, in the bar's order.</summary>
        static readonly string[] AnalyserDaylight = { "daylight.again", "daylight.hide", "daylight.show", "daylight.run", "daylight.rooms" };

        /// <summary>
        /// AN.1: tapping the Analyser's face opens the analysis menu under the
        /// top bar: one row per analysis with its last result, a Live switch
        /// for each (AN.2), and a pill per analysis this file can run now that
        /// runs the existing action. A file with neither says what comes first.
        /// </summary>
        public static CardSpec Analyser(FileFacts f)
        {
            var state = f?.Analysis ?? new Functions.Analysis.State();
            var card = new CardSpec
            {
                Kind = "analyser",
                Question = ForskText.Get("analyser.ask"),
                Pin = true,
                Rows = new List<string>(),
                Fields = new List<CardField>(),
                Note = ForskText.Get("analysis.live.note")
            };
            foreach (var id in Functions.Analysis.All)
            {
                var name = ForskText.Get("analysis." + id);
                card.Rows.Add(ForskText.Format("analysis.row", "name", name, "last",
                    state.Last.TryGetValue(id, out var last) ? last : ForskText.Get("analysis.never")));
                card.Fields.Add(new CardField
                {
                    Key = Functions.Analysis.LiveKey(id),
                    Label = ForskText.Format("analysis.live", "name", name),
                    Value = state.IsLive(id) ? "1" : "0",
                    Check = true
                });
            }
            var daylight = AnalyserDaylight.Select(ForskRegistry.Find).FirstOrDefault(a => a != null && a.Shows(f));
            if (daylight != null) card.Pills.Add(new CardPill(daylight.Id, ForskText.Label(daylight.Id)));
            var area = ForskRegistry.Find("area.stats");
            if (area != null && area.Shows(f)) card.Pills.Add(new CardPill(area.Id, ForskText.Label(area.Id)));
            foreach (var id in new[] { "analysis.print", "options" })
            {
                var action = ForskRegistry.Find(id);
                if (action != null && action.Shows(f)) card.Pills.Add(new CardPill(action.Id, ForskText.Label(action.Id)));
            }
            if (card.Pills.Count == 0) card.Note = ForskText.Get("analyser.none");
            card.Pills.Add(new CardPill("done", ForskText.Get("word.done")));
            return card;
        }

        /// <summary>
        /// Settings → Grok API key: one masked field to paste the user's own
        /// key, Save, Remove while ~/.forsk/grok.env holds one, and Cancel. The
        /// note says whether a key is set, by its last four characters only.
        /// </summary>
        public static CardSpec GrokKey(string loaded, bool stored)
        {
            var card = new CardSpec
            {
                Kind = ForskKeyFile.MenuId,
                Question = ForskText.Get("grok.key.ask"),
                Note = string.IsNullOrEmpty(loaded)
                    ? ForskText.Get("grok.key.none")
                    : ForskText.Format("grok.key.set", "tail", ForskKeyFile.Tail(loaded)),
                Fields = new List<CardField>
                {
                    new CardField { Key = ForskKeyFile.FieldKey, Label = ForskText.Get("grok.key.field"), Value = "", Secret = true }
                }
            };
            card.Pills.Add(new CardPill("save", ForskText.Get("word.save")));
            if (stored) card.Pills.Add(new CardPill("remove", ForskText.Get("word.remove")));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            return card;
        }

        /// <summary>
        /// Settings → Set up Forsk: a row for chat and a row for daylight and AI
        /// detection, each with its state. The pills are the actions still
        /// open, the one most needed first: Set up (Try again after a failure)
        /// while uv is missing, the Grok key card, Get a key while there is no
        /// key, Connect (or Disconnect) for the account, and Done. Only Done answers the card.
        /// </summary>
        public static CardSpec Setup(string key, SetupState tools, AccountState account = null)
        {
            var card = new CardSpec
            {
                Kind = ForskSetup.MenuId,
                Question = ForskText.Get("setup.ask"),
                Rows = new List<string> { ForskSetup.ChatRow(key), ForskSetup.ToolsRow(tools), ForskSetup.AccountRow(account) },
                Note = ForskText.Get("setup.note")
            };
            var noKey = string.IsNullOrEmpty(key);
            if (tools != null && tools.NeedsAction)
                card.Pills.Add(new CardPill("uv", ForskText.Get(tools.Phase == SetupPhase.Failed ? "setup.uv.again" : "setup.uv")));
            card.Pills.Add(new CardPill("key", ForskText.Get(noKey ? "setup.key.add" : "setup.key.change")));
            if (noKey) card.Pills.Add(new CardPill("get_key", ForskText.Get("setup.key.get")));
            if (account == null || account.CanConnect)
                card.Pills.Add(new CardPill("connect", ForskText.Get(account?.Phase == AccountPhase.Failed ? "setup.account.again" : "setup.account.connect")));
            else if (account.Phase == AccountPhase.Connected)
                card.Pills.Add(new CardPill("disconnect", ForskText.Get("setup.account.disconnect")));
            card.Pills.Add(new CardPill("done", ForskText.Get("word.done")));
            return card;
        }

        /// <summary>
        /// Settings → Update available: the new version and the steps in
        /// Package Manager, with Food4Rhino's way below. Open Package Manager
        /// and How to update keep the card open. Only Done answers it.
        /// </summary>
        public static CardSpec Update(string version, string current)
        {
            var card = new CardSpec
            {
                Kind = ForskUpdate.MenuId,
                Question = ForskText.Format("update.ask", "version", version, "current", current),
                Rows = new List<string>
                {
                    ForskText.Format("update.step.pm", "version", version),
                    ForskText.Get("update.step.restart")
                },
                Note = ForskText.Get("update.note")
            };
            card.Pills.Add(new CardPill("open", ForskText.Get("update.open")));
            card.Pills.Add(new CardPill("howto", ForskText.Get("update.howto")));
            card.Pills.Add(new CardPill("done", ForskText.Get("word.done")));
            return card;
        }

        public static CardSpec Ink(FileFacts f)
        {
            var now = f?.Ink ?? "default";
            var card = new CardSpec
            {
                Kind = "ink.set",
                Question = ForskText.Get("ink.set.ask"),
                Note = ForskText.Format("ink.set.now", "ink", now),
                Choice = true
            };
            foreach (var profile in PrintProfiles.All)
                card.Pills.Add(new CardPill(profile.Name, ForskText.Get("ink." + profile.Name)) { Primary = profile.Name == now });
            card.Pills.Add(new CardPill("done", ForskText.Get("word.confirm")));
            return card;
        }

        /// <summary>
        /// Choose logo on the Project info card: the picked file (an SVG already
        /// a PNG at path) is shown on the card, which stays open. Save keeps it.
        /// </summary>
        public static void HoldLogo(JObject card, string name, string path, byte[] picture)
        {
            var data = LogoData(card);
            data.Remove("logo_remove");
            data["logo_path"] = path;
            data["logo_name"] = name;
            var preview = OfficeLogo.PreviewUrl(picture);
            if (preview != null) card["image"] = preview;
            else card.Remove("image");
            card["note"] = "Logo: " + name + " · click Save to keep it.";
        }

        /// <summary>Remove logo: held like a pick, so Save removes it and Cancel keeps it.</summary>
        public static void HoldLogoRemoval(JObject card)
        {
            var data = LogoData(card);
            data.Remove("logo_path");
            data.Remove("logo_name");
            data["logo_remove"] = true;
            card.Remove("image");
            card["note"] = "The logo is removed when you click Save.";
        }

        /// <summary>The held logo change as set_project_meta arguments. Nothing held adds nothing.</summary>
        public static void LogoMeta(JObject card, JObject meta)
        {
            var data = card["data"] as JObject;
            var path = data?["logo_path"]?.ToString();
            if (!string.IsNullOrEmpty(path))
            {
                meta["logo_path"] = path;
                meta["logo_name"] = data["logo_name"]?.ToString() ?? "";
            }
            else if (data?["logo_remove"]?.Value<bool>() == true)
                meta["logo"] = "";
        }

        static JObject LogoData(JObject card)
        {
            if (!(card["data"] is JObject data))
            {
                data = new JObject();
                card["data"] = data;
            }
            return data;
        }

        /// <summary>
        /// ⋯ → Project info: the seven ProjectInfo fields, Save and Cancel. With
        /// pending (file.print, print.one, export.dwg, export.dxf, export.csv)
        /// it is the ask-once card before that action: Save and print (or
        /// export), Print without, Cancel; Data carries the action and its
        /// sheet so the answer runs it. An empty Architect takes the name saved
        /// for this Mac. An empty Date shows today, and saving that prefill
        /// does not lock it.
        /// </summary>
        public static CardSpec TitleBlock(FileFacts f, string pending = null, string view = null, DateTime? today = null)
        {
            var day = today ?? DateTime.Now;
            var exports = pending != null && pending.StartsWith("export.", StringComparison.Ordinal);
            var card = new CardSpec
            {
                Kind = "meta.title",
                Question = ForskText.Get(pending == null ? "meta.title.ask" : "meta.title.first"),
                Fields = new List<CardField>()
            };
            if (pending == null)
            {
                card.Pills.Add(new CardPill("save", ForskText.Get("word.save")));
                // The office logo prints at the right end of every title block.
                string logo = null;
                f?.Meta?.TryGetValue(OfficeLogo.NameKey, out logo);
                card.Pills.Add(new CardPill("logo", ForskText.Get(string.IsNullOrEmpty(logo) ? "meta.logo.choose" : "meta.logo.change")));
                if (!string.IsNullOrEmpty(logo))
                {
                    card.Pills.Add(new CardPill("logo_remove", ForskText.Get("meta.logo.remove")));
                    card.Note = "Logo: " + logo;
                }
            }
            else
            {
                card.Data = new JObject { ["pending"] = pending };
                if (view != null) card.Data["view"] = view;
                card.Pills.Add(new CardPill("save", ForskText.Get(exports ? "meta.save.export" : "meta.save.print")));
                card.Pills.Add(new CardPill("skip", ForskText.Get(exports ? "meta.skip.export" : "meta.skip.print")));
            }
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            foreach (var key in ProjectInfo.Keys)
            {
                var shown = ShownMeta(f, key, day);
                card.Fields.Add(new CardField
                {
                    Key = key,
                    Label = ProjectInfo.Caption(key),
                    Value = shown,
                    Placeholder = MetaPlaceholder(key)
                });
            }
            return card;
        }

        /// <summary>
        /// What the field shows. A stored value wins. An empty Architect takes
        /// the name saved for this Mac. An empty Date takes today, as a value,
        /// so the field is not a blank asking to be typed. Client and address
        /// stay empty, with their hint, until someone types them.
        /// </summary>
        static string ShownMeta(FileFacts f, string key, DateTime today)
        {
            string value = null;
            f?.Meta?.TryGetValue(key, out value);
            value = MetaValue(key, value);
            if (value.Length > 0) return value;
            if (key == ProjectInfo.Architect) return (f?.FirmArchitect ?? "").Trim();
            if (key == ProjectInfo.Date) return ProjectInfo.SheetDate(null, today);
            return "";
        }

        /// <summary>
        /// The one line an answered form card shows, instead of "question · Save"
        /// plus a second line that says Save. Null for a card that is not one of
        /// these forms: its pill stays the answer.
        /// </summary>
        public static string FormReceipt(string kind, string pillId, JObject values)
        {
            string Value(string key) => (values?[key]?.ToString() ?? "").Trim();
            switch (kind)
            {
                case "meta.title":
                    if (pillId == "skip") return "Project info skipped";
                    if (pillId != "save") return null;
                    var record = new ProjectInfo.Record();
                    foreach (var key in ProjectInfo.Keys) record[key] = Value(key);
                    return ProjectInfo.SavedLine(record);
                case "stair.edit":
                    if (pillId == "flip") return "Stair flipped";
                    if (pillId != "save") return null;
                    var sizes = new[] { Value("width"), Value("riser_max"), Value("going") }.Where(s => s.Length > 0).ToList();
                    return sizes.Count == 0 ? "Stair sizes saved" : "Stair sizes saved · " + string.Join(" × ", sizes);
                case ForskKeyFile.MenuId:
                    if (pillId == "save") return ForskText.Get("grok.key.saved");
                    return pillId == "remove" ? ForskText.Get("grok.key.removed") : null;
                case "print.pages":
                    if (pillId == "reset") return "Sheet set reset";
                    if (pillId == "export_ifc") return "Export IFC";
                    if (pillId == "export_csv") return "Export CSV";
                    if (pillId != "save" && pillId != "print" && pillId != "export") return null;
                    var scale = Value("scale");
                    return scale.Length == 0 ? "Sheets saved" : "Sheets saved · " + scale;
                default:
                    return null;
            }
        }

        /// <summary>
        /// A stored Norwegian seed is the old empty hint, not a saved name.
        /// The field stays blank and the English hint shows.
        /// </summary>
        static string MetaValue(string key, string value)
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0) return "";
            return string.Equals(text, MetaSeed(key), StringComparison.Ordinal) ? "" : text;
        }

        static string MetaPlaceholder(string key)
        {
            switch (key)
            {
                case "project": return "Project name";
                case "client": return "Client";
                case "address": return "Address";
                default: return null;
            }
        }

        static string MetaSeed(string key)
        {
            switch (key)
            {
                case "project": return "Min tittel";
                case "client": return "Klient";
                case "address": return "Adresse";
                default: return null;
            }
        }

        /// <summary>
        /// The Project info card to answer before pending runs, or null to run
        /// it now: once a project name is stored, or once the file was asked.
        /// </summary>
        public static CardSpec AskInfoFirst(FileFacts f, string pending, string view = null, DateTime? today = null)
        {
            if (f == null || !InfoMissing(f)) return null;
            string asked = null;
            f.Meta?.TryGetValue(ProjectInfo.AskedKey, out asked);
            if (!string.IsNullOrWhiteSpace(asked)) return null;
            return TitleBlock(f, pending, view, today);
        }

        /// <summary>No project name is stored: the gear's dot and the ask-once card.</summary>
        public static bool InfoMissing(FileFacts f)
        {
            return ProjectInfo.Missing(ProjectInfo.Read(key =>
            {
                string value = null;
                f?.Meta?.TryGetValue(key, out value);
                return value;
            }));
        }

        /// <summary>Every sheet of the set in set order, those switched off too: one sheet prints on its own.</summary>
        public static CardSpec PrintOne(FileFacts f)
        {
            var card = new CardSpec { Kind = "print.one", Question = ForskText.Get("print.one.ask"), Depends = "model" };
            foreach (var sheet in Set(f))
                card.Pills.Add(new CardPill(sheet.Id, SheetLine(sheet.Id, f)));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            return card;
        }

        public const string ScaleKey = "scale";
        public const string PaperKey = "paper";

        /// <summary>The paper the set prints on: the one kept on the file, else A3.</summary>
        static string PaperOf(FileFacts f)
        {
            string stored = null;
            f?.Meta?.TryGetValue(PrintTemplate.PaperKey, out stored);
            return PrintTemplate.Stored(stored).Name;
        }

        /// <summary>
        /// Choose sheets: a scale for the whole set, then one tick per sheet
        /// in set order, each row movable. The note gives the scale once.
        /// Print saves and prints, Save saves, Reset forgets the user's set
        /// and the asked scale. Null without walls.
        /// </summary>
        public static CardSpec Pages(FileFacts f)
        {
            if (f?.HasWalls != true) return null;
            var shown = f.PrintScale > 0 ? SheetScale.Listed(f.PrintScale) : 0;
            var card = new CardSpec
            {
                Kind = "print.pages",
                Question = ForskText.Get("print.pages.ask"),
                Fields = new List<CardField>(),
                Depends = "model",
                Note = shown > 0
                    ? "1:" + shown.ToString(CultureInfo.InvariantCulture) + " · " + PaperOf(f)
                    : ForskText.Get("print.pages.fit"),
                Pills =
                {
                    new CardPill("print", ForskText.Get("word.print")),
                    new CardPill("save", ForskText.Get("word.save")),
                    new CardPill("reset", ForskText.Get("word.reset")),
                    new CardPill("export", ForskText.Label("export.dwg")),
                    new CardPill("export_ifc", ForskText.Label("export.ifc")),
                    new CardPill("export_csv", ForskText.Label("export.csv"))
                }
            };
            card.Fields.Add(new CardField
            {
                Key = ScaleKey,
                Label = ForskText.Get("print.pages.scale"),
                Options = ScaleOptions(),
                Value = shown > 0 ? "1:" + shown.ToString(CultureInfo.InvariantCulture) : "Fit"
            });
            card.Fields.Add(new CardField
            {
                Key = PaperKey,
                Label = ForskText.Get("print.pages.paper"),
                Options = PrintTemplate.Papers.Select(p => p.Name).ToList(),
                Value = PaperOf(f)
            });
            foreach (var sheet in Set(f))
                card.Fields.Add(new CardField { Key = sheet.Id, Label = SheetLine(sheet.Id, f), Check = true, Order = true, Value = sheet.On ? "1" : "0" });
            return card;
        }

        static List<string> ScaleOptions()
        {
            var options = new List<string> { "Fit" };
            foreach (var step in SheetScale.Ladder)
                options.Add("1:" + step.ToString(CultureInfo.InvariantCulture));
            return options;
        }

        /// <summary>
        /// The print_pages call a Choose sheets answer makes: Reset forgets the
        /// set; Print and Save write the ticks, the rows' posted order, and the
        /// scale (0 fits again). The scale field is not a sheet id.
        /// </summary>
        public static JObject PagesArgs(string pill, JObject values, JArray order)
        {
            if (pill == "reset") return new JObject { ["reset"] = true };
            var on = new JArray();
            var off = new JArray();
            foreach (var pair in values ?? new JObject())
            {
                if (pair.Key == ScaleKey || pair.Key == PaperKey) continue;
                (pair.Value?.ToString() == "0" ? off : on).Add(pair.Key);
            }
            var args = new JObject { ["on"] = on, ["off"] = off };
            if (order != null && order.Count > 0)
                args["order"] = new JArray(order.Select(t => t.ToString()).Where(id => id != ScaleKey && id != PaperKey));
            if (values?["scale"] != null)
                args["scale"] = SheetScale.Parse(values["scale"].ToString());
            if (PrintTemplate.Find(values?[PaperKey]?.ToString()) is PrintTemplate.Paper paper)
                args["paper"] = paper.Name;
            return args;
        }

        /// <summary>
        /// The set as the next Print writes it, from the facts the window
        /// reads: the inferred set under what the user stored. The model has
        /// one storey (level 0).
        /// </summary>
        public static List<SheetSet.Sheet> Set(FileFacts f)
        {
            var facts = new SheetSet.SetFacts
            {
                Walls = f?.HasWalls == true,
                Sections = new List<string>(f?.SectionLetters ?? new List<string>()),
                Lists = ListKinds(f),
                DetailSheets = new List<string>(f?.DetailSheets ?? new List<string>())
            };
            return SheetSet.Merge(SheetSet.Infer(facts), SheetSet.Read(f?.PrintPages));
        }

        /// <summary>The lists that have rows: door, window, room.</summary>
        static List<string> ListKinds(FileFacts f)
        {
            var kinds = new List<string>();
            if (f?.HasDoors == true) kinds.Add("door");
            if (f?.HasWindows == true) kinds.Add("window");
            if (f?.HasRooms == true) kinds.Add("room");
            return kinds;
        }

        /// <summary>A sheet as the cards name it: A-40-001 North elevation.</summary>
        static string SheetLine(string id, FileFacts f)
        {
            return SheetSet.Number(id, 0, 0, f?.DetailSheets) + " " + SheetSet.Title(id, 0, ListKinds(f));
        }

        public static CardSpec Rooms(FileFacts f)
        {
            if (f?.Rooms == null || f.Rooms.Count == 0) return null;
            return new CardSpec
            {
                Kind = "rooms.list",
                Question = ForskText.Get("rooms.list.ask"),
                Rows = new List<string>(f.Rooms),
                Depends = "model",
                Pills = { new CardPill("done", ForskText.Get("word.done")) }
            };
        }

        public static CardSpec RemoveSection(FileFacts f)
        {
            if (f?.SectionLetters == null || f.SectionLetters.Count == 0) return null;
            var card = new CardSpec { Kind = "section.remove", Question = ForskText.Get("section.remove.ask"), Depends = "model" };
            foreach (var letter in f.SectionLetters)
                card.Pills.Add(new CardPill(letter.ToUpperInvariant(), ForskText.Format("sheet.section", "letter", letter.ToUpperInvariant())));
            if (f.SectionLetters.Count > 1) card.Pills.Add(new CardPill("all", ForskText.Get("section.remove.all")));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            return card;
        }

        /// <summary>
        /// BRA and BTA per floor, then each room type, then the total. English.
        /// Null when the tool failed or there are no rooms. The chat line stays the short summary.
        /// </summary>
        public static CardSpec AreaSummary(JObject envelope)
        {
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)) return null;
            var rows = (envelope?["result"]?["breakdown"] as JArray)?
                .Select(row => row?.ToString())
                .Where(row => !string.IsNullOrWhiteSpace(row))
                .ToList();
            if (rows == null || rows.Count == 0) return null;
            return new CardSpec
            {
                Kind = "area.summary",
                Question = ForskText.Get("area.stats"),
                Rows = rows,
                Note = ForskText.Get("area.summary.note"),
                Depends = "model",
                Pills = { new CardPill("done", ForskText.Get("word.done")) }
            };
        }

        /// <summary>
        /// The furnish preview under its receipt: one row per layout (blue the
        /// usual, orange the creative), a pill to place each, and Cancel. One
        /// row and one pill when both are the same. Null for a placing run.
        /// </summary>
        public static CardSpec FurnishChoice(JObject envelope)
        {
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)) return null;
            var result = envelope["result"] as JObject;
            if (result?["preview"]?.Value<bool>() != true || !(result["options"] is JArray options) || options.Count < 2) return null;
            var room = result["room_words"]?.ToString() ?? "the room";
            var same = result["same"]?.Value<bool>() == true;
            var card = new CardSpec
            {
                Kind = "furnish.pick",
                Question = ForskText.Format(same ? "furnish.pick.one" : "furnish.pick.ask", "room", room),
                Rows = new List<string>(),
                Depends = "model",
                Data = new JObject
                {
                    ["room"] = result["room"],
                    ["density"] = result["density"],
                    ["replace"] = result["replace"]
                }
            };
            card.Rows.Add(ForskText.Format("furnish.pick.usual", "pieces", options[0]["summary"]?.ToString() ?? ""));
            card.Pills.Add(new CardPill("consistent", ForskText.Get(same ? "furnish.place.one" : "furnish.place.usual")));
            if (!same)
            {
                card.Rows.Add(ForskText.Format("furnish.pick.other", "pieces", options[1]["summary"]?.ToString() ?? ""));
                card.Pills.Add(new CardPill("creative", ForskText.Get("furnish.place.other")));
            }
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            return card;
        }

        /// <summary>The takeoff under its receipt: one row per line. Null when the tool failed or found nothing.</summary>
        public static CardSpec Takeoff(JObject envelope)
        {
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)) return null;
            var rows = (envelope["result"]?["rows"] as JArray)?.Select(r => r.ToString()).Where(r => r.Length > 0).ToList();
            if (rows == null || rows.Count == 0) return null;
            return new CardSpec
            {
                Kind = "takeoff",
                Question = ForskText.Get("takeoff.ask"),
                Rows = rows,
                Note = ForskText.Get("takeoff.note"),
                Depends = "model",
                Pills = { new CardPill("done", ForskText.Get("word.done")) }
            };
        }

        /// <summary>
        /// After a wall edit that changed more than the wall: one row per wall
        /// that followed, then what was rebuilt. Outer walls by side, inner
        /// walls by the rooms they bound. Null when only the wall changed, or
        /// the edit failed. Read from the tool's result.
        /// </summary>
        public static CardSpec WallReview(JObject envelope, bool nb = false)
        {
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)) return null;
            var result = envelope["result"] as JObject;
            var followed = result?["followed"] as JArray;
            var records = result?["records"] as JArray;
            if ((followed?.Count ?? 0) == 0 && (records?.Count ?? 0) <= 1) return null;
            var card = new CardSpec
            {
                Kind = "wall.review",
                Question = ForskText.Get(nb ? "wall.review.ask.nb" : "wall.review.ask"),
                Rows = new List<string>(),
                Depends = "model",
                Pills = { new CardPill("done", ForskText.Get("word.done")) }
            };
            var inner = 0;
            foreach (var item in followed ?? new JArray())
            {
                var graph = item["wall"]?.ToString() ?? "";
                var sideNamed = WallFollowPlan.IsSide(graph, out var side);
                if (!sideNamed) inner++;
                var wall = item["label"]?.ToString();
                if (string.IsNullOrWhiteSpace(wall) || wall.StartsWith("the wall at (", StringComparison.Ordinal))
                    wall = sideNamed ? WallFollowPlan.Side(side, nb) : WallFollowPlan.InnerName(null, inner, nb);
                if (wall.Length > 0) wall = char.ToUpperInvariant(wall[0]) + wall.Substring(1);
                var change = item["change_mm"]?.ToObject<double>() ?? 0;
                var key = (change > 0 ? "wall.review.longer" : change < 0 ? "wall.review.shorter" : "wall.review.row")
                    + (nb ? ".nb" : "");
                card.Rows.Add(ForskText.Format(key, "wall", wall,
                    "mm", Math.Abs(change).ToString("0", CultureInfo.InvariantCulture)));
            }
            var rebuilt = result?["rebuilt"]?.ToString();
            if (!string.IsNullOrWhiteSpace(rebuilt)) card.Rows.Add(rebuilt);
            return card;
        }

        /// <summary>A multi-page PDF on import: which page is the plan. The file goes in the card's data.</summary>
        public static CardSpec PdfPage(string pdfPath, int pages)
        {
            var name = System.IO.Path.GetFileName(pdfPath ?? "");
            var card = new CardSpec
            {
                Kind = "pdf.page",
                Question = ForskText.Format("pdf.page.ask", "file", name, "n", pages.ToString(CultureInfo.InvariantCulture)),
                Data = new JObject { ["pdf_path"] = pdfPath }
            };
            for (var n = 1; n <= Math.Min(pages, MaxPages); n++)
                card.Pills.Add(new CardPill(n.ToString(CultureInfo.InvariantCulture), ForskText.Format("pdf.page.pill", "n", n.ToString(CultureInfo.InvariantCulture))));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            if (pages > MaxPages) card.Note = ForskText.Format("pdf.page.more", "n", MaxPages.ToString(CultureInfo.InvariantCulture));
            return card;
        }

        static CardSpec Confirm(string kind, string question)
        {
            return new CardSpec
            {
                Kind = kind,
                Question = ForskText.Get(question),
                Pills = { new CardPill("yes", ForskText.Label(kind)), new CardPill("cancel", ForskText.Get("word.cancel")) }
            };
        }

        /// <summary>The stamp a card keeps: the facts its staleness depends on, as they were when it opened.</summary>
        public static string Stamp(string depends, FileFacts f)
        {
            switch (depends)
            {
                case "selection": return "s:" + (f?.SelectionKey ?? "") + "|m:" + (f?.ModelKey ?? "");
                case "model": return "m:" + (f?.ModelKey ?? "");
                default: return "";
            }
        }
    }
}
