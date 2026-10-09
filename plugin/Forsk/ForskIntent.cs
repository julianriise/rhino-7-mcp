using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RhinoMCPPlugin.Forsk
{
    public enum ForskIntent
    {
        Build,
        Edit,
        Sheets,
        Print,
        Daylight,
        Area,
        /// <summary>Quantities: takeoff, mengdeliste, how much wall. A list is the card.</summary>
        Takeoff,
        Dxf,
        Import,
        Support,
        General
    }

    /// <summary>
    /// What the router decided. Unsure is a miss or a weak clash: the turn is
    /// General and the tool pack is the full union, never a narrow pack.
    /// Confidence is 1 when a rule decided, and 0 when it did not.
    /// </summary>
    public sealed class ForskRoute
    {
        public ForskIntent Intent;
        public bool Unsure;
        public double Confidence;

        public static ForskRoute Sure(ForskIntent intent)
        {
            return new ForskRoute { Intent = intent, Unsure = false, Confidence = 1 };
        }

        public static ForskRoute Miss()
        {
            return new ForskRoute { Intent = ForskIntent.General, Unsure = true, Confidence = 0 };
        }
    }

    /// <summary>
    /// One-turn tool bias from the message. A clear question, bug report, or
    /// feature request is Support, ahead of the others, so "how do I print" is
    /// not Print. Then PDF or image import, print, daylight, area, sheets (sections
    /// too), DXF, import, edit, build. An opening selection is edit when those
    /// words are absent. A miss, or a lone "bug" or "wrong" sitting on another
    /// intent, is General: the full tool pack, not Support's short list.
    /// </summary>
    public static class ForskIntentRouter
    {
        /// <param name="picked">What the selection is (FileFacts.Picked). An opening picked makes an otherwise plain sentence an edit.</param>
        public static ForskIntent Classify(string text, Picked picked = Picked.None)
        {
            return Route(text, picked).Intent;
        }

        /// <summary>The intent, and whether the router is willing to narrow the tool pack.</summary>
        public static ForskRoute Route(string text, Picked picked = Picked.None)
        {
            var t = Normalize(text);
            var support = SupportMatch(t);
            var task = Task(t, picked);
            // A clear question or bug stays Support, even when the words also name a task.
            if (support == SupportKind.Strong) return ForskRoute.Sure(ForskIntent.Support);
            // "wrong" or "bug" on a sentence that already has a task is not enough to drop the other tools.
            if (support == SupportKind.Weak && task != null) return ForskRoute.Miss();
            if (support == SupportKind.Weak) return ForskRoute.Sure(ForskIntent.Support);
            if (task != null) return ForskRoute.Sure(task.Value);
            return ForskRoute.Miss();
        }

        enum SupportKind { None, Weak, Strong }

        /// <summary>Strong is a question, a feature request, or a bug phrase. Weak is only the word bug, wrong, or feil.</summary>
        static SupportKind SupportMatch(string t)
        {
            if (Question(t) || BugStrong(t) || Feature(t)) return SupportKind.Strong;
            if (BugWeak(t)) return SupportKind.Weak;
            return SupportKind.None;
        }

        /// <summary>The first task rule that hits, in the router's order. Null when none do. Opening selection is the last of them.</summary>
        static ForskIntent? Task(string t, Picked picked)
        {
            if (IsPlanFile(t)) return ForskIntent.Import;
            if (IsPrint(t)) return ForskIntent.Print;
            if (IsDaylight(t)) return ForskIntent.Daylight;
            // Before Area: m² of wall is a quantity, not a floor area.
            if (IsTakeoff(t)) return ForskIntent.Takeoff;
            if (IsArea(t)) return ForskIntent.Area;
            if (IsSheets(t)) return ForskIntent.Sheets;
            if (IsDxf(t)) return ForskIntent.Dxf;
            if (IsImport(t)) return ForskIntent.Import;
            if (IsEdit(t)) return ForskIntent.Edit;
            if (IsBuild(t)) return ForskIntent.Build;
            if (picked == Picked.Opening || picked == Picked.Stair) return ForskIntent.Edit;
            return null;
        }

        static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            // "m²" stays the word m2, so an area question can match it.
            text = text.Replace('²', '2');
            var sb = new StringBuilder(text.Length);
            var space = true;
            foreach (var raw in text.ToLowerInvariant())
            {
                var c = raw == '_' ? ' ' : raw;
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                    space = false;
                }
                else if (!space)
                {
                    sb.Append(' ');
                    space = true;
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>A how-to: "how do I…", "what does … do", "hvordan…".</summary>
        public static bool IsQuestion(string text) => Question(Normalize(text));

        /// <summary>A bug report: "this is broken", "there is an issue", "feil", "funker ikke".</summary>
        public static bool IsBug(string text) => Bug(Normalize(text));

        /// <summary>A feature request: "it would be nice if…", "can you add…".</summary>
        public static bool IsFeature(string text) => Feature(Normalize(text));

        static bool Question(string t)
        {
            if (t.Contains("how do i")) return true;
            if (HasWord(t, "hvordan")) return true;
            return t.Contains("what does") && HasWord(t, "do");
        }

        static bool Bug(string t) => BugStrong(t) || BugWeak(t);

        /// <summary>A bug phrase. These stay Support even when the sentence also names a task.</summary>
        static bool BugStrong(string t)
        {
            if (t.Contains("this is broken") || HasWord(t, "broken")) return true;
            if (t.Contains("there is an issue") || t.Contains("issue with") || t.Contains("problem with")) return true;
            // Normalize turns an apostrophe into a space: "doesn't" is "doesn t".
            if (t.Contains("doesn t work") || t.Contains("doesnt work") || t.Contains("does not work")) return true;
            if (t.Contains("not working") || t.Contains("isn t working")) return true;
            if (t.Contains("selects multiple")) return true;
            return t.Contains("problem med") || t.Contains("funker ikke") || t.Contains("virker ikke");
        }

        /// <summary>One word. On its own it is still a bug report. Next to a task it is not sure enough to drop that task's tools.</summary>
        static bool BugWeak(string t)
        {
            return HasWord(t, "bug") || HasWord(t, "wrong") || HasWord(t, "feil");
        }

        static bool Feature(string t)
        {
            return t.Contains("would be nice") || t.Contains("can you add");
        }

        /// <summary>
        /// A PDF, scan or image brought in: import plan.pdf, import scan.jpg. It
        /// comes before print, which "pdf" would match, and sheets, which
        /// "tegning" would.
        /// </summary>
        static bool IsPlanFile(string t)
        {
            if (!HasWord(t, "pdf") && !HasWord(t, "png") && !HasWord(t, "jpg") && !HasWord(t, "jpeg") && !HasWord(t, "scan"))
                return false;
            return HasWord(t, "import") || HasWord(t, "importer") || HasWord(t, "importere") || t.Contains("bring in");
        }

        static bool IsPrint(string t)
        {
            if (t.Length == 0) return false;
            if (HasWord(t, "print") || HasWord(t, "pdf") || HasWord(t, "a3")) return true;
            if (HasWord(t, "layout") || HasWord(t, "layouts")) return true;
            return t.Contains("skriv ut");
        }

        static bool IsDaylight(string t)
        {
            if (HasWord(t, "daylight") || HasWord(t, "dagslys")) return true;
            if (t.Contains("natural light") || t.Contains("sky visibility") || t.Contains("how much light")) return true;
            if (HasWord(t, "darker") || HasWord(t, "darkest") || HasWord(t, "brighter") || HasWord(t, "brightest")
                || HasWord(t, "mørk") || HasWord(t, "mørkt") || HasWord(t, "lyst"))
                return true;
            // "dark" and "bright" on their own are a place or a mood ("dark corner", "feel bright").
            // They count when the sentence asks whether a room is dark or bright.
            if (!HasWord(t, "dark") && !HasWord(t, "bright")) return false;
            var asks = HasWord(t, "is") || HasWord(t, "er");
            var room = HasWord(t, "room") || HasWord(t, "rooms") || HasWord(t, "rom") || HasWord(t, "rommet");
            return asks && room;
        }

        /// <summary>
        /// Area statistics: areal, m², kvm, BRA, BTA, how big, hvor stor.
        /// "print the room schedule" is print, checked first, and "romliste" is sheets.
        /// "bra" the adjective ("ser bra", "er bra") is not a figure.
        /// "the area is wrong" stays a bug report: a lone wrong, bug or feil is not this task.
        /// A target size ("12m²", "make this room 12 m2") is an edit, not this.
        /// </summary>
        static bool IsArea(string t)
        {
            if (BugWeak(t)) return false;
            if (IsSizeEdit(t)) return false;
            if (t.Contains("how big") || t.Contains("hvor stor")) return true;
            if (HasWord(t, "area") || HasWord(t, "areas") || HasStem(t, "areal")) return true;
            if (HasWord(t, "kvm") || HasWord(t, "kvadratmeter") || HasWord(t, "kvadratmetre") || HasWord(t, "m2")) return true;
            if (HasWord(t, "bruksareal") || HasWord(t, "bruttoareal") || HasWord(t, "bta")) return true;
            if (!HasWord(t, "bra")) return false;
            return !t.Contains("er bra") && !t.Contains("ser bra") && !t.Contains("veldig bra")
                && !t.Contains("helt bra") && !t.Contains("ganske bra");
        }

        /// <summary>
        /// Quantities: takeoff, mengde, mengdeliste, hvor mye vegg, how much
        /// wall, m² yttervegg. Adding the Mengdeliste to the set is a sheets turn.
        /// </summary>
        static bool IsTakeoff(string t)
        {
            if (IsSetEdit(t)) return false;
            if (HasWord(t, "takeoff") || t.Contains("take off") || t.Contains("take-off")) return true;
            if (HasStem(t, "mengde") || HasWord(t, "quantities") || HasWord(t, "quantity")) return true;
            var wall = HasStem(t, "vegg") || HasStem(t, "yttervegg") || HasStem(t, "innervegg") || HasWord(t, "wall") || HasWord(t, "walls");
            if (!wall) return false;
            return t.Contains("hvor mye") || t.Contains("hvor mange") || t.Contains("how much") || t.Contains("how many")
                || HasWord(t, "m2") || HasWord(t, "m3") || HasWord(t, "meter") || HasWord(t, "metres") || HasWord(t, "meters");
        }

        /// <summary>A word that starts with stem: areal, arealet, arealene.</summary>
        static bool HasStem(string text, string stem)
        {
            var i = 0;
            while ((i = text.IndexOf(stem, i, StringComparison.Ordinal)) >= 0)
            {
                if (i == 0 || text[i - 1] == ' ') return true;
                i += stem.Length;
            }
            return false;
        }

        static bool IsSheets(string t)
        {
            if (HasWord(t, "sheets") || HasWord(t, "sheet")) return true;
            if (t.Contains("make2d") || t.Contains("make 2d")) return true;
            if (HasWord(t, "drawings") || HasWord(t, "tegning") || HasWord(t, "tegninger")) return true;
            if (HasWord(t, "schedule") || HasWord(t, "schedules")) return true;
            if (HasWord(t, "dørliste") || HasWord(t, "vindusliste") || HasWord(t, "romliste")) return true;
            if (IsDetail(t)) return true;
            if (IsDimensions(t)) return true;
            if (IsSection(t)) return true;
            if (IsProfile(t)) return true;
            if (IsSetEdit(t)) return true;
            return t.Contains("sheet pack") || t.Contains("clear drawings");
        }

        /// <summary>
        /// A change to the set Print writes: drop the facades from the set, ta
        /// bort fasadene, fjern snitt A fra settet, rekkefølgen på arkene. A
        /// verb alone (ta bort døra) is an edit; it needs a sheet word with it.
        /// </summary>
        static bool IsSetEdit(string t)
        {
            if (HasWord(t, "ark") || HasWord(t, "arket") || HasWord(t, "arkene")) return true;
            if (t.Contains("rekkefølge") || t.Contains("rekkefolge")) return true;
            if (HasWord(t, "settet") || (" " + t + " ").Contains(" the set ")) return true;
            var verb = HasWord(t, "drop") || t.Contains("ta med") || t.Contains("ta bort") || HasWord(t, "fjern")
                || HasWord(t, "include") || HasWord(t, "exclude") || HasWord(t, "skip") || HasWord(t, "put") || HasWord(t, "move");
            if (!verb) return false;
            return HasWord(t, "facade") || HasWord(t, "facades") || HasWord(t, "fasade") || HasWord(t, "fasader")
                || HasStem(t, "mengdelist")
                || HasWord(t, "fasadene") || HasWord(t, "elevation") || HasWord(t, "elevations")
                || HasWord(t, "tegningsliste") || HasWord(t, "tegningslisten") || HasWord(t, "forside") || HasWord(t, "forsiden")
                || ((HasWord(t, "put") || HasWord(t, "move")) && (t.Contains("before the plan") || t.Contains("after the plan")));
        }

        /// <summary>
        /// The sheet set asked for as files: "export dwg", "send dwg", "eksporter dxf"; or the
        /// model as IFC: "export ifc"; or the takeoff as CSV: "export csv", "export schedule".
        /// dwg, dxf, ifc, csv, or null. Importing or opening a file is the
        /// import's, and a question is Support's.
        /// </summary>
        public static string ExportFormat(string text)
        {
            var t = Normalize(text);
            if (t.Length == 0 || t.Length > 160 || Question(t)) return null;
            var dwg = HasWord(t, "dwg");
            var dxf = HasWord(t, "dxf");
            var ifc = HasWord(t, "ifc");
            // N2: the takeoff as a spreadsheet: export csv, export schedule, eksporter csv.
            var csv = HasWord(t, "csv") || HasWord(t, "schedule");
            if (!dwg && !dxf && !ifc && !csv) return null;
            if (HasWord(t, "import") || HasWord(t, "importer") || HasWord(t, "importere")
                || HasWord(t, "open") || HasWord(t, "åpne") || t.Contains("bring in")) return null;
            var verb = HasWord(t, "export") || HasWord(t, "eksporter") || HasWord(t, "eksportere")
                || HasWord(t, "send") || HasWord(t, "save") || HasWord(t, "lagre");
            if (!verb) return null;
            return ifc ? "ifc" : dwg ? "dwg" : dxf ? "dxf" : "csv";
        }

        /// <summary>
        /// The view picker from chat: "show the south elevation", "vis
        /// sørfasaden", "plan view", "3d view". A view word with a show verb,
        /// or a short phrase that is only the view. Null for anything else:
        /// "add a window to the south facade" is an edit, "print the plan" a print.
        /// </summary>
        public static string ViewPick(string text)
        {
            var t = Normalize(text);
            if (t.Length == 0 || t.Length > 80 || Question(t)) return null;
            var words = t.Split(' ');
            var verb = HasWord(t, "show") || HasWord(t, "see") || HasWord(t, "look") || HasWord(t, "vis") || HasWord(t, "se")
                || t.Contains("go to") || t.Contains("switch to") || t.Contains("gå til") || t.Contains("bytt til");
            var view = HasWord(t, "view") || HasWord(t, "visning");
            string Side(string w)
            {
                if (w.StartsWith("north") || w.StartsWith("nord")) return "north";
                if (w.StartsWith("east") || w.StartsWith("øst")) return "east";
                if (w.StartsWith("south") || w.StartsWith("sør")) return "south";
                if (w.StartsWith("west") || w.StartsWith("vest")) return "west";
                return null;
            }
            bool Facade(string w) => w.Contains("elevation") || w.Contains("facade") || w.Contains("façade") || w.Contains("fasade") || w.Contains("oppriss");
            if (words.Any(Facade))
            {
                var side = words.Select(Side).FirstOrDefault(x => x != null);
                if (side != null && (verb || view || words.Length <= 3)) return side;
                return null;
            }
            if (words.Any(w => w == "perspective" || w.StartsWith("perspektiv") || w == "3d"))
                return verb || view || words.Length == 1 ? Functions.ViewPicker.Perspective : null;
            var plan = words.Any(w => w == "plan" || w.StartsWith("plantegning") || w == "planet");
            if ((plan && (verb || view && words.Length <= 2)) || (view && HasWord(t, "top") && words.Length <= 2))
                return Functions.ViewPicker.Plan;
            return null;
        }

        /// <summary>
        /// AN.3: the Analysis set's PDF asked for: "print the analysis set",
        /// "skriv ut analysene", "analysis pdf". Not a question.
        /// </summary>
        public static bool AnalysisPrint(string text)
        {
            var t = Normalize(text);
            if (t.Length == 0 || t.Length > 160 || Question(t) || !t.Contains("analys")) return false;
            return HasWord(t, "print") || HasWord(t, "pdf") || t.Contains("skriv ut");
        }

        /// <summary>A DXF asked for: import plan.dxf, import a DXF. It comes before the plan image import, which "plan" would match.</summary>
        static bool IsDxf(string t)
        {
            if (!HasWord(t, "dxf")) return false;
            return HasWord(t, "import") || HasWord(t, "importer") || HasWord(t, "importere")
                || HasWord(t, "open") || HasWord(t, "åpne") || t.Contains("bring in");
        }

        /// <summary>A plan image brought in as an underlay with its detection, and the plan's scale from two points.</summary>
        static bool IsImport(string t)
        {
            if (HasWord(t, "underlay") || HasWord(t, "plantegning") || HasWord(t, "plantegningen")) return true;
            if (t.Contains("floor plan") || HasWord(t, "floorplan") || t.Contains("plan image")) return true;
            if (t.Contains("set scale") || t.Contains("set the scale") || t.Contains("scale the plan")
                || t.Contains("plan scale") || t.Contains("sett målestokk"))
                return true;
            var verb = HasWord(t, "import") || HasWord(t, "importer") || HasWord(t, "importere");
            return verb && (HasWord(t, "plan") || HasWord(t, "planen") || HasWord(t, "image") || HasWord(t, "bilde"));
        }

        /// <summary>A cross section: section A through the living room, tverrsnitt, lengdesnitt, snitt A-A.</summary>
        static bool IsSection(string t)
        {
            return HasWord(t, "section") || HasWord(t, "sections") || HasWord(t, "snitt") || HasWord(t, "snittet")
                || HasWord(t, "tverrsnitt") || HasWord(t, "lengdesnitt") || HasWord(t, "snitter");
        }

        /// <summary>A print profile asked for: use the grey profile, bytt profil, hatched poché, skravert poché.</summary>
        static bool IsProfile(string t)
        {
            if (HasWord(t, "profile") || HasWord(t, "profiles") || HasWord(t, "profil") || HasWord(t, "profiler"))
                return true;
            return t.Contains("poch") && (HasWord(t, "grey") || HasWord(t, "gray") || HasWord(t, "grå") || HasWord(t, "hatch")
                || HasWord(t, "hatched") || HasWord(t, "skravert") || HasWord(t, "skravur") || HasWord(t, "black") || HasWord(t, "svart"));
        }

        /// <summary>
        /// Detail drawings added or removed: add detail, detail this wall, remove
        /// the details, legg til detalj, fjern detaljene. The word alone ("explain
        /// in detail") is not one.
        /// </summary>
        static bool IsDetail(string t)
        {
            var noun = HasWord(t, "detail") || HasWord(t, "details") || HasWord(t, "detalj") || HasWord(t, "detaljen")
                || HasWord(t, "detaljer") || HasWord(t, "detaljene");
            if (!noun) return false;
            if (t.StartsWith("detail ", StringComparison.Ordinal)) return true;
            return HasWord(t, "add") || HasWord(t, "remove") || HasWord(t, "delete") || HasWord(t, "legg")
                || HasWord(t, "fjern") || HasWord(t, "slett");
        }

        /// <summary>The plan's dimensions, unless a door or window is named: its size is an edit.</summary>
        static bool IsDimensions(string t)
        {
            var words = HasWord(t, "dimension") || HasWord(t, "dimensions") || HasWord(t, "dims")
                || HasWord(t, "målsett") || HasWord(t, "målsetting") || HasWord(t, "målkjede") || HasWord(t, "målkjeder")
                || HasWord(t, "målene") || HasWord(t, "målet");
            if (!words) return false;
            foreach (var opening in new[] { "window", "windows", "door", "doors", "opening", "vindu", "vinduet", "dør", "døra", "døren", "åpning" })
                if (HasWord(t, opening)) return false;
            return true;
        }

        /// <summary>
        /// A number and m² is a size to set: "12m²", "12 m2", "make this room 12 m2",
        /// "rommet skal være 12 m²". A question (hvor stor, areal, BRA) is not.
        /// </summary>
        static bool IsSizeEdit(string t)
        {
            if (!HasAreaMeasure(t) || AreaQuestion(t)) return false;
            return BareMeasure(t) || SizingVerb(t);
        }

        /// <summary>m² glued to or following a number. "12m2" and "12 m2" both count. "m2" alone does not.</summary>
        static bool HasAreaMeasure(string t)
        {
            var i = 0;
            while ((i = t.IndexOf("m2", i, StringComparison.Ordinal)) >= 0)
            {
                var j = i - 1;
                while (j >= 0 && t[j] == ' ') j--;
                if (j >= 0 && t[j] >= '0' && t[j] <= '9') return true;
                i += 2;
            }
            return false;
        }

        /// <summary>Nothing in the message but the measure, and a few fillers.</summary>
        static bool BareMeasure(string t)
        {
            var any = false;
            foreach (var word in t.Split(' '))
            {
                if (word.Length == 0) continue;
                any = true;
                if (word == "m2" || Digits(word)) continue;
                if (word.EndsWith("m2", StringComparison.Ordinal) && Digits(word.Substring(0, word.Length - 2))) continue;
                if (word == "ca" || word == "about" || word == "rundt" || word == "omtrent") continue;
                return false;
            }
            return any;
        }

        static bool Digits(string word)
        {
            if (word.Length == 0) return false;
            foreach (var c in word)
                if (c < '0' || c > '9') return false;
            return true;
        }

        static bool SizingVerb(string t)
        {
            return HasWord(t, "make") || HasWord(t, "lag") || HasWord(t, "gjør") || HasWord(t, "gjor")
                || HasWord(t, "sett") || HasWord(t, "set") || HasWord(t, "resize")
                || HasWord(t, "endre") || HasWord(t, "change")
                || HasWord(t, "skal") || HasWord(t, "should");
        }

        /// <summary>The words that ask for a figure. A size target can carry m² without being one of these.</summary>
        static bool AreaQuestion(string t)
        {
            if (t.Contains("how big") || t.Contains("hvor stor")) return true;
            if (HasWord(t, "area") || HasWord(t, "areas") || HasStem(t, "areal")) return true;
            if (HasWord(t, "bruksareal") || HasWord(t, "bruttoareal") || HasWord(t, "bta")) return true;
            if (HasWord(t, "kvm") || HasWord(t, "kvadratmeter") || HasWord(t, "kvadratmetre")) return true;
            if (!HasWord(t, "bra")) return false;
            return !t.Contains("er bra") && !t.Contains("ser bra") && !t.Contains("veldig bra")
                && !t.Contains("helt bra") && !t.Contains("ganske bra");
        }

        static bool IsEdit(string t)
        {
            if (IsStairEdit(t)) return true;
            if (IsSizeEdit(t)) return true;
            if (t.Contains("move opening") || t.Contains("add opening") || t.Contains("delete opening"))
                return true;
            if (t.Contains("set opening") || t.Contains("set window") || t.Contains("set door"))
                return true;
            if (t.Contains("toward the corner") || t.Contains("along the wall"))
                return true;
            if (HasWord(t, "set") && (HasWord(t, "width") || HasWord(t, "sill") || HasWord(t, "head")))
                return true;
            if (IsWallEdit(t))
                return true;
            var verb = HasWord(t, "move") || HasWord(t, "flytt")
                || HasWord(t, "put") || HasWord(t, "place")
                || HasWord(t, "add") || HasWord(t, "legg")
                || HasWord(t, "delete") || HasWord(t, "remove") || HasWord(t, "slett") || HasWord(t, "fjern")
                || HasWord(t, "resize") || HasWord(t, "widen") || HasWord(t, "wider")
                || HasWord(t, "narrow") || HasWord(t, "narrower")
                || HasWord(t, "make") || HasWord(t, "lag") || HasWord(t, "gjør")
                || HasWord(t, "swap") || HasWord(t, "bytt");
            var noun = HasWord(t, "window") || HasWord(t, "windows")
                || HasWord(t, "door") || HasWord(t, "doors")
                || HasWord(t, "opening") || HasWord(t, "openings")
                || HasWord(t, "vindu") || HasWord(t, "vinduet") || HasWord(t, "vinduer")
                || HasWord(t, "dør") || HasWord(t, "døra") || HasWord(t, "døren") || HasWord(t, "dører")
                || HasWord(t, "åpning") || HasWord(t, "åpningen");
            return verb && noun;
        }

        /// <summary>
        /// One wall moved, deleted or added: move the north wall 500 mm north,
        /// fjern veggen, add a wall from … to …, make this line a wall. It comes
        /// before build, where "wall 500" would read as a wall height. Delete and
        /// add name one wall: "delete the walls and rebuild" and "make the wall
        /// 3000 high" stay build.
        /// </summary>
        static bool IsWallEdit(string t)
        {
            var one = HasWord(t, "wall") || HasWord(t, "partition")
                || HasWord(t, "vegg") || HasWord(t, "veggen")
                || HasWord(t, "skillevegg") || HasWord(t, "skilleveggen");
            var move = HasWord(t, "move") || HasWord(t, "shift") || HasWord(t, "nudge") || HasWord(t, "push")
                || HasWord(t, "flytt");
            if (move && (one || HasWord(t, "walls") || HasWord(t, "veggene"))) return true;
            if (IsRoomPushPull(t)) return true;
            if (!one) return false;
            if (HasWord(t, "delete") || HasWord(t, "remove") || HasWord(t, "fjern") || HasWord(t, "slett"))
                return true;
            var height = HasWord(t, "high") || HasWord(t, "height") || HasWord(t, "tall")
                || HasWord(t, "høy") || HasWord(t, "høyde");
            return !height && (HasWord(t, "add") || HasWord(t, "draw") || HasWord(t, "make")
                || HasWord(t, "tegn") || HasWord(t, "lag") || HasWord(t, "legg"));
        }

        /// <summary>
        /// R5: a stair named (stair, trapp, trappa), or its step sizes with a
        /// number: "steps 170 high", "going 280", "opptrinn 170".
        /// </summary>
        static bool IsStairEdit(string t)
        {
            if (HasWord(t, "stair") || HasWord(t, "stairs") || HasWord(t, "staircase") || HasWord(t, "stairway")
                || HasWord(t, "trapp") || HasWord(t, "trappa") || HasWord(t, "trappen") || HasWord(t, "trapper")
                || HasWord(t, "trappene") || HasWord(t, "trappeløp"))
                return true;
            return NumberAfter(t, "steps") || NumberAfter(t, "step") || NumberAfter(t, "going")
                || NumberAfter(t, "riser") || NumberAfter(t, "risers") || NumberAfter(t, "tread")
                || NumberAfter(t, "trinn") || NumberAfter(t, "opptrinn") || NumberAfter(t, "inntrinn");
        }

        /// <summary>The word, then a number as the next word.</summary>
        static bool NumberAfter(string t, string word)
        {
            var words = t.Split(' ');
            for (var i = 0; i + 1 < words.Length; i++)
                if (words[i] == word && words[i + 1].Length > 0 && char.IsDigit(words[i + 1][0])) return true;
            return false;
        }

        /// <summary>F2: push or pull one side of a room. "Push the north side of this room 500 mm out", "skyv nordsiden av rommet ut".</summary>
        static bool IsRoomPushPull(string t)
        {
            var verb = HasWord(t, "push") || HasWord(t, "pull") || HasWord(t, "skyv") || HasWord(t, "dra");
            var room = HasWord(t, "room") || HasWord(t, "rom") || HasWord(t, "rommet");
            return verb && room;
        }

        static bool IsBuild(string t)
        {
            if (t.Contains("wall height") || t.Contains("tilbygg") || t.Contains("påbygg")) return true;
            if (t.Contains("paabygg") || t.Contains("pabygg") || HasWord(t, "extension")) return true;
            if (HasWord(t, "generate") || HasWord(t, "bake") || HasWord(t, "rebuild") || HasWord(t, "regenerate"))
                return true;
            if (t.Contains("clear generated") || t.Contains("clear and regenerate")) return true;
            if (t.Contains("mark as existing") || t.Contains("mark existing")) return true;
            if (t.Contains("make rooms") || t.Contains("find rooms") || t.Contains("detect rooms")) return true;
            return WallsWithHeight(t);
        }

        static bool WallsWithHeight(string t)
        {
            var i = 0;
            while ((i = t.IndexOf("wall", i, StringComparison.Ordinal)) >= 0)
            {
                var left = i == 0 || t[i - 1] == ' ';
                var end = i + 4;
                if (end < t.Length && t[end] == 's') end++;
                if (left && (end == t.Length || t[end] == ' '))
                {
                    var j = end;
                    while (j < t.Length && t[j] == ' ') j++;
                    if (j < t.Length && char.IsDigit(t[j])) return true;
                }
                i += 4;
            }
            return false;
        }

        static bool HasWord(string text, string word)
        {
            var i = 0;
            while ((i = text.IndexOf(word, i, StringComparison.Ordinal)) >= 0)
            {
                var left = i == 0 || text[i - 1] == ' ';
                var end = i + word.Length;
                var right = end == text.Length || text[end] == ' ';
                if (left && right) return true;
                i = end;
            }
            return false;
        }
    }

    /// <summary>
    /// Area statistics from the panel's chat. The bias is the one line the turn
    /// gets: totals first, the tool's figures, and Make rooms when there are none.
    /// </summary>
    public static class ForskArea
    {
        public const string Bias = "Turn bias: Area. Call area_stats. "
            + "The window prints that summary, and a card under it with BRA and BTA per floor, then each room type, then the total. "
            + "Do not repeat the net total, the floors, or the rooms. "
            + "No coordinates, no ids. Do not invent a figure. "
            + "No rooms: the summary offers rooms_detect, which finds them from the walls. "
            + "A typed room is grouped under its English type, such as Living. Unassigned keeps the name.";

        /// <summary>A successful area_stats is the whole answer. The model does not add a second line.</summary>
        public static bool Answered(string tool, string status)
        {
            return string.Equals(tool, "area_stats", StringComparison.Ordinal)
                && string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// dxf_import from the panel's chat ("import plan.dxf", "import a DXF"):
    /// the handler MCP clients call. With no path it can open as it stands the
    /// user picks the file, and the receipt is the tool's whole message.
    /// </summary>
    public static class ForskDxf
    {
        public const string Tool = "dxf_import";

        public const string Bias = "Turn bias: Import DXF. Call dxf_import. Pass path only when the user gave an absolute .dxf path; "
            + "with no path the user picks the file. "
            + "The panel prints the receipt: objects, texts, units and scale, suspect labels. "
            + "Reply with one line, and pass on a units guess or a suspect label. "
            + "Do not generate 3D here. The user asks for that.";

        /// <summary>No path the import can open as it stands (none, a bare file name, a file that is not there): the user picks the file.</summary>
        public static bool NeedsPick(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            path = path.Trim();
            return !Path.IsPathRooted(path) || !File.Exists(path);
        }

        /// <summary>A tool message's first sentence (the counts), and the rest of it.</summary>
        public static string Split(string message, out string rest)
        {
            message = message ?? "";
            var stop = message.IndexOf(". ", StringComparison.Ordinal);
            rest = stop < 0 ? "" : message.Substring(stop + 2);
            return stop < 0 ? message : message.Substring(0, stop + 1);
        }

        /// <summary>The receipt row: Import DXF · ok · Imported plan.dxf: 283 objects.</summary>
        public static string Line(bool ok, string message)
        {
            if (!ok) return "Import DXF · error · " + (string.IsNullOrEmpty(message) ? "failed" : message);
            return "Import DXF · ok · " + Split(message, out _);
        }

        /// <summary>
        /// Under the receipt: the rest of the tool's message (texts, units and
        /// scale, suspect labels), then each text left as Rhino made it and
        /// each warning the message does not already carry, one row each.
        /// </summary>
        public static string Note(string message, IEnumerable<string> unmatched, IEnumerable<string> warnings)
        {
            Split(message, out var rest);
            var note = new StringBuilder(rest);
            foreach (var row in unmatched ?? new string[0])
                note.Append("\n· ").Append(row);
            foreach (var row in warnings ?? new string[0])
                if (!rest.Contains(row)) note.Append("\n· ").Append(row);
            return note.ToString().Trim();
        }
    }

    /// <summary>
    /// The Import plan chip's file and receipt (F7). A PDF is plan_import's
    /// pdf_path, a PNG or JPEG its image_path for the raster source, and a DXF
    /// goes to dxf_import. The receipt row is the message's first sentence:
    /// the counts, or on an error the reason. The rest goes under it, the
    /// scale, the licence or the next step, then what needs review.
    /// </summary>
    public static class ForskPlanFile
    {
        public static readonly string[] Extensions = { ".pdf", ".png", ".jpg", ".jpeg", ".dxf" };
        const int ReviewRows = 6;

        /// <summary>The argument a picked file goes in: pdf_path, image_path, or dxf for dxf_import. Null for a file the chip does not take.</summary>
        public static string Argument(string path)
        {
            switch (Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".pdf": return "pdf_path";
                case ".png":
                case ".jpg":
                case ".jpeg": return "image_path";
                case ".dxf": return "dxf";
                default: return null;
            }
        }

        /// <summary>The receipt row: Import plan · ok · Imported 30 walls, 7 doors, 12 windows, 11 rooms.</summary>
        public static string Line(bool ok, string message)
        {
            return "Import plan · " + (ok ? "ok" : "error") + " · " + ForskDxf.Split(string.IsNullOrEmpty(message) ? "failed" : message, out _);
        }

        /// <summary>Under the receipt: the rest of the message, then what needs review, one row each.</summary>
        public static string Note(bool ok, string message, IEnumerable<string> review)
        {
            ForskDxf.Split(message, out var rest);
            var note = new StringBuilder(rest);
            var rows = ok && review != null ? new List<string>(review) : new List<string>();
            for (var i = 0; i < rows.Count && i < ReviewRows; i++)
                note.Append("\n· ").Append(rows[i]);
            if (rows.Count > ReviewRows)
                note.Append("\n· and ").Append(rows.Count - ReviewRows).Append(" more");
            return note.ToString().Trim();
        }
    }

    public enum ImportAction
    {
        None,
        ImportPlan,
        SetScale
    }

    public enum DaylightAction
    {
        None,
        Run,
        Hide,
        Show,
        MakeRooms
    }

    /// <summary>
    /// One document object as Forsk sees it: the chips and the v3 classifier
    /// read the same rows. They come from one enumeration with hidden layers
    /// such as A-OPEN included. Visible is false when the object or its layer
    /// is hidden, so a hidden daylight mesh still counts. An object on X-EXIST
    /// or marked existing is a row with Existing set; the chips skip it.
    /// </summary>
    public sealed class ChipRow
    {
        public bool Generated;
        public string Kind;
        public string OpeningKind;
        public string Layer;
        public bool Visible;
        /// <summary>forsk:import_kind: underlay for the imported plan image.</summary>
        public string ImportKind;
        /// <summary>forsk:import_scale_status on the underlay: user once two points set it.</summary>
        public string ScaleStatus;
        /// <summary>The object's id. The sheet fingerprint keys on it.</summary>
        public string Id;
        /// <summary>On X-EXIST, or forsk:kind=existing.</summary>
        public bool Existing;
        public bool Selected;
        public bool Curve;
        /// <summary>A closed curve, or a closed solid.</summary>
        public bool Closed;
        public bool Solid;
        /// <summary>forsk:daylight_stale=1 on a daylight mesh: the model changed after the map was made.</summary>
        public bool Stale;
        /// <summary>Forsk's own Top plan cut (forsk:plan_cut). A view helper: the file classifier skips it.</summary>
        public bool PlanCut;
        /// <summary>forsk:import_review_rows on the underlay: the import's review rows, as JSON.</summary>
        public string Review;
        /// <summary>The edit stamp of a generated object: its geometry and its forsk strings, hashed.</summary>
        public string Stamp;
        /// <summary>A room's name (forsk:room_name, else the object's name).</summary>
        public string Name;
        /// <summary>A room's area, forsk:area, in mm².</summary>
        public string Area;
        /// <summary>forsk:room_type when stored. Empty on an old file, which reads as unassigned.</summary>
        public string RoomType;
        /// <summary>A generated wall whose forsk:path reads as wall loops: the join graph can see it.</summary>
        public bool PathReads;
        /// <summary>How many straight runs a generated wall's path holds (WallJoins.Runs); 0 when it does not read.</summary>
        public int Runs;
        /// <summary>The marker this row stands for: forsk:marker on a room plate, forsk:marker_id on an opening's block.</summary>
        public string Marker;
        /// <summary>forsk:id: w01, rd-03.</summary>
        public string ForskId;
        /// <summary>A wall's forsk:thickness, mm.</summary>
        public string Thickness;
        /// <summary>An opening's forsk:mark (D03, V02) from its schedule, its own or its marker's; empty before Print.</summary>
        public string Mark;
        /// <summary>An opening's forsk:width, forsk:sill and forsk:head, mm.</summary>
        public string Width;
        public string Sill;
        public string Head;
        /// <summary>A selected one-run wall's name in the join graph (F2): "the north wall", "the wall at (4000, 2000)". Selected walls only.</summary>
        public string RunName;
        /// <summary>The way that run moves first: its compass side, else north for an east–west run and east for a north–south one.</summary>
        public string RunToward;
        /// <summary>That run's length in the join graph, mm, and whether one of its ends stands free (the Properties panel can change its length).</summary>
        public double? RunLength;
        public bool RunFreeEnd;
        /// <summary>forsk:part on an opening: frame, leaf, glass, sash, sill, threshold, track.</summary>
        public string Part;
        /// <summary>Rhino group indexes, sorted and comma-separated. Empty when the object is in none.</summary>
        public string Group;
        /// <summary>R5: a stair's risers, its riser max and its going, mm, as its record holds them.</summary>
        public string Risers;
        public string RiserMax;
        public string Going;
        /// <summary>
        /// The info panel's record (ForskInfo): the object's forsk:* strings and a few
        /// read from the model (info:height, info:host, info:ceiling). Picked things
        /// and their markers only; null for every other row.
        /// </summary>
        public Dictionary<string, string> Info;

        /// <summary>This row as a selected thing. The source row's Selected flag stays as read.</summary>
        public ChipRow SelectedCopy()
        {
            var copy = (ChipRow)MemberwiseClone();
            copy.Selected = true;
            return copy;
        }
    }

    /// <summary>
    /// Chip state. The primary chip is Generate or Print. The secondary Daylight
    /// chip sits under Print: it runs when walls, windows, and rooms exist, hides
    /// or shows the mesh on A-ANALYSE, and offers Make rooms when rooms are missing.
    /// The Import chip offers Import plan in a document with no plan, and Set scale
    /// on an imported plan until two points have set its scale.
    /// </summary>
    public sealed class BakeChip
    {
        public const string NeedsWindows = "Needs windows";
        public const string NeedsWindowsHint = "Daylight comes in through windows. Add a window, then run it.";

        public bool HasPlan;
        public bool HasWalls;
        public bool HasWindows;
        public bool HasRooms;
        public bool HasOverlay;
        /// <summary>A daylight mesh is in the model and currently shown.</summary>
        public bool OverlayVisible;
        public bool ShowPrint => HasWalls;
        public bool ShowGenerate => HasPlan && !HasWalls;
        public bool Visible => ShowPrint || ShowGenerate;
        public string Label => ShowPrint ? "Print PDF" : "Generate 3D model";

        public bool ShowDaylight => HasWalls || HasOverlay;

        public DaylightAction Daylight
        {
            get
            {
                if (HasOverlay) return OverlayVisible ? DaylightAction.Hide : DaylightAction.Show;
                if (!HasRooms) return DaylightAction.MakeRooms;
                if (!HasWindows) return DaylightAction.None;
                return DaylightAction.Run;
            }
        }

        public bool DaylightEnabled => Daylight != DaylightAction.None;

        public string DaylightLabel
        {
            get
            {
                switch (Daylight)
                {
                    case DaylightAction.Hide: return "Hide daylight map";
                    case DaylightAction.Show: return "Show daylight map";
                    case DaylightAction.MakeRooms: return "Make rooms";
                    case DaylightAction.None: return NeedsWindows;
                    default: return "Daylight";
                }
            }
        }

        public string DaylightHint => Daylight == DaylightAction.None ? NeedsWindowsHint : null;

        public bool HasUnderlay;
        public bool ScaleSet;

        public ImportAction Import
        {
            get
            {
                if (HasWalls) return ImportAction.None;
                if (HasUnderlay) return ScaleSet ? ImportAction.None : ImportAction.SetScale;
                return HasPlan ? ImportAction.None : ImportAction.ImportPlan;
            }
        }

        public bool ShowImport => Import != ImportAction.None;
        public string ImportLabel => Import == ImportAction.SetScale ? "Set scale" : "Import plan";

        /// <summary>Import facts from the model rows: the plan underlay and whether its scale was set.</summary>
        public void ReadImport(IEnumerable<ChipRow> rows)
        {
            HasUnderlay = ScaleSet = false;
            foreach (var row in rows)
            {
                if (row == null || row.Existing || !Is(row.ImportKind, "underlay")) continue;
                HasUnderlay = true;
                if (Is(row.ScaleStatus, "user")) ScaleSet = true;
            }
        }

        /// <summary>Daylight facts from the model rows. Called on every refresh; nothing is cached.</summary>
        public void ReadDaylight(IEnumerable<ChipRow> rows)
        {
            HasWindows = HasRooms = HasOverlay = OverlayVisible = false;
            foreach (var row in rows)
            {
                if (row == null || row.Existing) continue;
                if (!row.Generated) continue;
                if (Is(row.Kind, "room")) HasRooms = true;
                else if (Is(row.Kind, "analysis"))
                {
                    HasOverlay = true;
                    if (row.Visible) OverlayVisible = true;
                }
                else if (Is(row.Kind, "opening_marker") && Is(row.OpeningKind, "window")) HasWindows = true;
            }
        }

        /// <summary>
        /// Generate 3D already ran rooms_from_layer. A count above zero means drawn
        /// A-ROOM outlines became markers, and they win. Zero means no rooms were
        /// found, so rooms_detect runs. Null means that step failed: do not detect.
        /// </summary>
        public static bool ShouldDetectRooms(int? roomsFromLayerCount)
        {
            return roomsFromLayerCount == 0;
        }

        /// <summary>
        /// A panel sentence that hides or shows the daylight map. Run, dark, and
        /// bright stay with the daylight chat. Clear means hide: the mesh stays.
        /// </summary>
        public static DaylightAction MapToggle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return DaylightAction.None;
            var t = text.Trim().ToLowerInvariant();
            var aboutMap = t.Contains("daylight map") || t.Contains("clear daylight") || t.Contains("fjern dagslys")
                || t.Contains("hide daylight") || t.Contains("show daylight");
            if (!aboutMap) return DaylightAction.None;
            if (t.Contains("show")) return DaylightAction.Show;
            return DaylightAction.Hide;
        }

        static bool Is(string value, string expected)
        {
            return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
