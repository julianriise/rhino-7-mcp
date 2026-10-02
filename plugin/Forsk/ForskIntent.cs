using System;
using System.Collections.Generic;
using System.IO;
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
        Dxf,
        Import,
        General
    }

    /// <summary>
    /// One-turn tool bias from the message. Order is PDF or image import, print, daylight, sheets (sections too), DXF, import, edit, build.
    /// An opening selection is edit when those words are absent.
    /// </summary>
    public static class ForskIntentRouter
    {
        /// <param name="picked">What the selection is (FileFacts.Picked). An opening picked makes an otherwise plain sentence an edit.</param>
        public static ForskIntent Classify(string text, Picked picked = Picked.None)
        {
            var t = Normalize(text);
            if (IsPlanFile(t)) return ForskIntent.Import;
            if (IsPrint(t)) return ForskIntent.Print;
            if (IsDaylight(t)) return ForskIntent.Daylight;
            if (IsSheets(t)) return ForskIntent.Sheets;
            if (IsDxf(t)) return ForskIntent.Dxf;
            if (IsImport(t)) return ForskIntent.Import;
            if (IsEdit(t)) return ForskIntent.Edit;
            if (IsBuild(t)) return ForskIntent.Build;
            if (picked == Picked.Opening) return ForskIntent.Edit;
            return ForskIntent.General;
        }

        static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
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
            return HasWord(t, "dark") || HasWord(t, "darker") || HasWord(t, "darkest")
                || HasWord(t, "bright") || HasWord(t, "brighter") || HasWord(t, "brightest")
                || HasWord(t, "mørk") || HasWord(t, "mørkt") || HasWord(t, "lyst");
        }

        static bool IsSheets(string t)
        {
            if (HasWord(t, "sheets") || HasWord(t, "sheet")) return true;
            if (t.Contains("make2d") || t.Contains("make 2d")) return true;
            if (HasWord(t, "drawings") || HasWord(t, "tegning") || HasWord(t, "tegninger")) return true;
            if (HasWord(t, "schedule") || HasWord(t, "schedules")) return true;
            if (HasWord(t, "dørliste") || HasWord(t, "vindusliste") || HasWord(t, "romliste")) return true;
            if (IsDimensions(t)) return true;
            if (IsSection(t)) return true;
            if (IsProfile(t)) return true;
            return t.Contains("sheet pack") || t.Contains("clear drawings");
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

        /// <summary>The plan's dimensions, unless a door or window is named: its size is an edit.</summary>
        static bool IsDimensions(string t)
        {
            var words = HasWord(t, "dimension") || HasWord(t, "dimensions") || HasWord(t, "dims")
                || HasWord(t, "målsett") || HasWord(t, "målsetting") || HasWord(t, "målkjede") || HasWord(t, "målkjeder");
            if (!words) return false;
            foreach (var opening in new[] { "window", "windows", "door", "doors", "opening", "vindu", "vinduet", "dør", "døra", "døren", "åpning" })
                if (HasWord(t, opening)) return false;
            return true;
        }

        static bool IsEdit(string t)
        {
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
            var verb = HasWord(t, "move") || HasWord(t, "add") || HasWord(t, "delete") || HasWord(t, "remove")
                || HasWord(t, "resize") || HasWord(t, "widen") || HasWord(t, "wider")
                || HasWord(t, "narrow") || HasWord(t, "narrower");
            var noun = HasWord(t, "window") || HasWord(t, "windows")
                || HasWord(t, "door") || HasWord(t, "doors")
                || HasWord(t, "opening") || HasWord(t, "openings");
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
        /// <summary>forsk:import_review_rows on the underlay: the import's review rows, as JSON.</summary>
        public string Review;
        /// <summary>The edit stamp of a generated object: its geometry and its forsk strings, hashed.</summary>
        public string Stamp;
        /// <summary>A room's name (forsk:room_name, else the object's name).</summary>
        public string Name;
        /// <summary>A room's area, forsk:area, in mm².</summary>
        public string Area;
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
