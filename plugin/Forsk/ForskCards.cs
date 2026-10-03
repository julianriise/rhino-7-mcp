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
        public string Note;
        public string Depends = "none";
        public JObject Data;
    }

    /// <summary>The cards' contents from the classifier's facts. No RhinoCommon, so they test from fixtures.</summary>
    public static class ForskCards
    {
        public const int MaxPages = 24;
        static readonly string[] MetaKeys = { "project", "client", "address", "revision" };

        /// <summary>The card a registry card action opens, or null for help, the bridge, and an action that is not a card.</summary>
        public static CardSpec For(string actionId, FileFacts f)
        {
            switch (actionId)
            {
                case "opening.type": return SwapType(f);
                case "file.check": return Review(f);
                case "ink.set": return Ink(f);
                case "meta.title": return TitleBlock(f);
                case "print.one": return PrintOne(f);
                case "print.pages": return Pages(f);
                case "print.clear": return Confirm("print.clear", "print.clear.ask");
                case "sheets.clear": return Confirm("sheets.clear", "sheets.clear.ask");
                case "rooms.list": return Rooms(f);
                case "section.remove": return RemoveSection(f);
                case "dims.list": return DimsList(f);
                case "stair.edit": return EditStair(f);
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
                kinds.Add(f.PickedOpeningKind);
                question = ForskText.Format("opening.type.ask", "kind", ForskText.Get("word." + f.PickedOpeningKind));
            }
            else if (f.Picked == Picked.None && (f.HasDoors || f.HasWindows))
            {
                if (f.HasDoors) kinds.Add("door");
                if (f.HasWindows) kinds.Add("window");
                question = ForskText.Get(kinds.Count == 2 ? "opening.type.all.ask" : "opening.type.all." + kinds[0]);
            }
            else return null;
            var card = new CardSpec { Kind = "opening.type", Question = question, Depends = "selection" };
            if (f.Picked == Picked.None) card.Data = new JObject { ["all"] = true };
            foreach (var type in OpeningTypes.All.Where(t => kinds.Contains(t.Kind)))
                card.Pills.Add(new CardPill(type.Id, type.Label, type.Id));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
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
        /// R2: one tick per user dimension ("U01 · 2 walls, 1 window"). Save
        /// removes the unticked ones, Remove all removes every one. Null when there are none.
        /// </summary>
        public static CardSpec DimsList(FileFacts f)
        {
            if (f?.Dims == null || f.Dims.Count == 0) return null;
            var card = new CardSpec
            {
                Kind = "dims.list",
                Question = ForskText.Get("dims.list.ask"),
                Fields = new List<CardField>(),
                Depends = "model",
                Pills =
                {
                    new CardPill("save", ForskText.Get("word.save")),
                    new CardPill("remove_all", ForskText.Get("word.remove_all")),
                    new CardPill("cancel", ForskText.Get("word.cancel"))
                }
            };
            foreach (var dim in f.Dims)
            {
                var walls = dim.Refs.Count(r => r.Wall != null);
                var openings = dim.Refs.Count(r => r.Opening != null);
                var what = walls + (walls == 1 ? " wall" : " walls")
                    + (openings == 0 ? "" : ", " + openings + (openings == 1 ? " opening" : " openings"));
                card.Fields.Add(new CardField { Key = dim.Id, Label = dim.Id + " · " + what, Check = true, Value = "1" });
            }
            return card;
        }

        /// <summary>The dimensions a Save on the list removes: the unticked rows.</summary>
        public static JArray DimsRemoved(JObject values)
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

        public static CardSpec Ink(FileFacts f)
        {
            var card = new CardSpec
            {
                Kind = "ink.set",
                Question = ForskText.Get("ink.set.ask"),
                Note = ForskText.Format("ink.set.now", "ink", f?.Ink ?? "default")
            };
            foreach (var profile in PrintProfiles.All)
                card.Pills.Add(new CardPill(profile.Name, ForskText.Get("ink." + profile.Name)));
            return card;
        }

        public static CardSpec TitleBlock(FileFacts f)
        {
            var card = new CardSpec
            {
                Kind = "meta.title",
                Question = ForskText.Get("meta.title.ask"),
                Fields = new List<CardField>(),
                Pills = { new CardPill("save", ForskText.Get("word.save")), new CardPill("cancel", ForskText.Get("word.cancel")) }
            };
            foreach (var key in MetaKeys)
            {
                string value = null;
                f?.Meta?.TryGetValue(key, out value);
                card.Fields.Add(new CardField { Key = key, Label = ForskText.Get("meta." + key), Value = value ?? "" });
            }
            return card;
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
                    ? "1:" + shown.ToString(CultureInfo.InvariantCulture) + " · A3"
                    : ForskText.Get("print.pages.fit"),
                Pills =
                {
                    new CardPill("print", ForskText.Get("word.print")),
                    new CardPill("save", ForskText.Get("word.save")),
                    new CardPill("reset", ForskText.Get("word.reset")),
                    new CardPill("export", ForskText.Label("export.dwg")),
                    new CardPill("export_ifc", ForskText.Label("export.ifc"))
                }
            };
            card.Fields.Add(new CardField
            {
                Key = ScaleKey,
                Label = ForskText.Get("print.pages.scale"),
                Options = ScaleOptions(),
                Value = shown > 0 ? "1:" + shown.ToString(CultureInfo.InvariantCulture) : "Fit"
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
                if (pair.Key == ScaleKey) continue;
                (pair.Value?.ToString() == "0" ? off : on).Add(pair.Key);
            }
            var args = new JObject { ["on"] = on, ["off"] = off };
            if (order != null && order.Count > 0)
                args["order"] = new JArray(order.Select(t => t.ToString()).Where(id => id != ScaleKey));
            if (values?["scale"] != null)
                args["scale"] = SheetScale.Parse(values["scale"].ToString());
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
                Lists = ListKinds(f)
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
            return SheetSet.Number(id, 0) + " " + SheetSet.Title(id, 0, ListKinds(f));
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
        /// The rooms under an area answer, one row each. The chat line does not
        /// list them. Null when the tool failed or there are no rooms.
        /// </summary>
        public static CardSpec AreaRooms(JObject envelope, bool nb = false)
        {
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)) return null;
            var result = envelope["result"] as JObject;
            var rooms = result?["rooms"] as JArray;
            if (rooms == null || rooms.Count == 0) return null;
            var many = (result["floors"] as JArray)?.Count > 1;
            var card = new CardSpec
            {
                Kind = "area.rooms",
                Question = ForskText.Get(nb ? "area.rooms.ask.nb" : "area.rooms.ask"),
                Rows = new List<string>(),
                Depends = "model",
                Pills = { new CardPill("done", ForskText.Get("word.done")) }
            };
            foreach (var token in rooms)
            {
                var room = token as JObject;
                var name = room?["name"]?.ToString();
                var area = room?["area"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(area)) continue;
                var level = room["level"]?.ToString();
                card.Rows.Add(many && !string.IsNullOrWhiteSpace(level)
                    ? name + ", " + level + ", " + area
                    : name + " " + area);
            }
            return card.Rows.Count == 0 ? null : card;
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
