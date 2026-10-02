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
        static readonly string[] SheetViews = { "plan", "north", "east", "south", "west", "schedules" };
        static readonly string[] MetaKeys = { "project", "client", "address" };

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
                case "print.clear": return Confirm("print.clear", "print.clear.ask");
                case "sheets.clear": return Confirm("sheets.clear", "sheets.clear.ask");
                case "rooms.list": return Rooms(f);
                case "section.remove": return RemoveSection(f);
                default: return null;
            }
        }

        /// <summary>The types for the selected opening's kind only: a door lists four, a window three.</summary>
        public static CardSpec SwapType(FileFacts f)
        {
            var kind = f?.PickedOpeningKind;
            if (kind == null) return null;
            var card = new CardSpec
            {
                Kind = "opening.type",
                Question = ForskText.Format("opening.type.ask", "kind", ForskText.Get("word." + kind)),
                Depends = "selection"
            };
            foreach (var type in OpeningTypes.All.Where(t => t.Kind == kind))
                card.Pills.Add(new CardPill(type.Id, type.Label));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            return card;
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

        /// <summary>The plan, the four elevations, the schedules, and each stored section.</summary>
        public static CardSpec PrintOne(FileFacts f)
        {
            var card = new CardSpec { Kind = "print.one", Question = ForskText.Get("print.one.ask"), Depends = "model" };
            foreach (var view in SheetViews)
                card.Pills.Add(new CardPill(view, ForskText.Get("sheet." + view)));
            foreach (var letter in f?.SectionLetters ?? new List<string>())
                card.Pills.Add(new CardPill("section_" + letter.ToLowerInvariant(), ForskText.Format("sheet.section", "letter", letter.ToUpperInvariant())));
            card.Pills.Add(new CardPill("cancel", ForskText.Get("word.cancel")));
            return card;
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
