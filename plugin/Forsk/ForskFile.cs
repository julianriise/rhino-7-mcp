using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>What the file is. Slot 1 reads this and nothing else.</summary>
    public enum FileKind
    {
        /// <summary>The document has no objects.</summary>
        Empty,
        /// <summary>Geometry Forsk did not make, and no underlay, plan-layer curve or generated object.</summary>
        Foreign,
        /// <summary>A Forsk underlay whose scale is not user.</summary>
        Unscaled,
        /// <summary>Plan curves, no generated object, the scale settled or no underlay.</summary>
        Plan,
        /// <summary>Objects, but nothing to build from: the existing house only, or an underlay with no curves.</summary>
        NoPlan,
        /// <summary>A generated object and no generated wall.</summary>
        Partial,
        /// <summary>A generated wall, on any layer, hidden or not.</summary>
        Model
    }

    /// <summary>The daylight map: none, shown, hidden by the user, or out of date.</summary>
    public enum MapState
    {
        None,
        Shown,
        Hidden,
        Stale
    }

    /// <summary>What the selection is, when all of it is one thing.</summary>
    public enum Picked
    {
        None,
        Wall,
        Opening,
        Room,
        /// <summary>A source curve on a plan layer. Not a wall selection.</summary>
        PlanCurve,
        /// <summary>Closed curves or solids that are not on a Forsk layer and not generated.</summary>
        Loose,
        /// <summary>R5: Forsk stairs.</summary>
        Stair,
        Other
    }

    /// <summary>What a model edit does to the daylight map.</summary>
    public enum MapEdit
    {
        None,
        Opening,
        Wall
    }

    /// <summary>The document as the classifier reads it: the object rows and the facts that are not objects.</summary>
    public sealed class DocInput
    {
        public IList<ChipRow> Rows = new List<ChipRow>();
        /// <summary>The model fingerprint stamped at the last Print, or null.</summary>
        public string StoredFingerprint;
        /// <summary>Forsk layout pages in the document.</summary>
        public int Layouts;
        /// <summary>The letters of the sections stored with section_add.</summary>
        public List<string> SectionLetters = new List<string>();
        /// <summary>The project info as stored (ProjectInfo.Keys), the asked flag and the scale label.</summary>
        public Dictionary<string, string> Meta = new Dictionary<string, string>();
        /// <summary>The architect last saved on this Mac (plug-in settings), which prefills an empty Architect.</summary>
        public string FirmArchitect;
        /// <summary>The set as the user left it (SheetSet JSON), or null when Forsk infers it.</summary>
        public string PrintPages;
        /// <summary>The set's scale: the one asked for, else the plan page's from the last Print. 0 when neither.</summary>
        public int PrintScale;
        public string Ink = "default";
        /// <summary>The daylight grid saved on this Mac (DaylightQuality).</summary>
        public string DaylightQuality = Forsk.DaylightQuality.Low;
        public bool Millimetres = true;
        public bool KeyPresent;
        public bool ListenerUp = true;
        /// <summary>The last Forsk record is still the newest thing in this document (LastActionTracker).</summary>
        public bool UndoNewest;
        /// <summary>The last action was a whole-set Print, on this document, and nothing changed since.</summary>
        public bool JustPrinted;
        /// <summary>The last action was Generate 3D or area statistics, and nothing changed since.</summary>
        public bool OfferArea;
        /// <summary>The first-run hint is off: dismissed, or a sheet has been printed.</summary>
        public bool GuideOff;
        /// <summary>Full paths of the 2D layers Generate 3D hid, one per line.</summary>
        public string PlanLayers;
        /// <summary>Those layers are currently hidden.</summary>
        public bool PlanHidden;
        /// <summary>The stored details (forsk/details JSON), or null.</summary>
        public string Details;
        /// <summary>Each stored detail's name by id ("North wall", "Door D01"), read from the model. One whose element is gone has none.</summary>
        public Dictionary<string, string> DetailNames = new Dictionary<string, string>();
        /// <summary>The detail sheets the details pack onto (detail_20_1, …), read from the model.</summary>
        public List<string> DetailSheets = new List<string>();
    }

    /// <summary>The classifier's answer. One pure read of the rows; no RhinoCommon.</summary>
    public sealed class FileFacts
    {
        public FileKind Kind;
        public bool HasGenerated;
        public bool HasWalls;
        /// <summary>Every generated wall has a path that reads, so the join graph sees every wall (F2).</summary>
        public bool JoinGraph;
        /// <summary>A generated wall holds more than one straight run, so a click picks all of them (selection S1).</summary>
        public bool WholeWalls;
        public bool HasPlanCurves;
        public bool HasUnderlay;
        public bool ScaleSet;
        public bool HasForeign;
        public bool HasExisting;
        public bool HasRooms;
        public bool HasWindows;
        public bool HasDoors;
        /// <summary>R5: a Forsk stair is in the model.</summary>
        public bool HasStairs;
        public bool HasSheetCache;
        public MapState Map;
        public List<string> Review;
        public bool ReviewStored => Review != null;
        public int Layouts;
        public List<string> SectionLetters = new List<string>();
        public int Sections => SectionLetters.Count;
        public Dictionary<string, string> Meta = new Dictionary<string, string>();
        public string FirmArchitect;
        /// <summary>The stored set (SheetSet JSON), or null.</summary>
        public string PrintPages;
        /// <summary>The set's scale denominator, 0 when it is not known yet.</summary>
        public int PrintScale;
        /// <summary>Room names with their area, as "Stue · 24.5 m²".</summary>
        public List<string> Rooms = new List<string>();
        public bool SheetsStale;
        /// <summary>The selected objects' ids. A card about the selection is stale once it changes.</summary>
        public string SelectionKey = "";
        /// <summary>Every object's id and edit stamp. A card about the model is stale once it changes.</summary>
        public string ModelKey = "";
        /// <summary>The status line's Render job, when Render exists. Empty for now.</summary>
        public string RenderJob;
        /// <summary>The status line's v4 grade, when v4 exists. Empty for now.</summary>
        public string Grade;
        public string Ink = "default";
        public string DaylightQuality = Forsk.DaylightQuality.Low;
        public Picked Picked;
        public int PickedCount;
        /// <summary>Of the things picked, the Forsk walls and the doors and windows.</summary>
        public int PickedWalls;
        public int PickedOpenings;
        /// <summary>The details, as stored, and their names by id.</summary>
        public List<global::RhinoMCPPlugin.Functions.Details.Record> Details = new List<global::RhinoMCPPlugin.Functions.Details.Record>();
        public Dictionary<string, string> DetailNames = new Dictionary<string, string>();
        public List<string> DetailSheets = new List<string>();
        /// <summary>The things picked, one row each (ForskPick.Things). The pick line reads them.</summary>
        public List<ChipRow> Selected = new List<ChipRow>();
        /// <summary>door or window when every picked opening is that kind, else null.</summary>
        public string PickedOpeningKind;
        public bool KeyPresent;
        public bool ListenerUp = true;
        public bool UndoNewest;
        /// <summary>A Print was the last action: the bar offers Export DWG next.</summary>
        public bool JustPrinted;
        /// <summary>Generate 3D or area statistics just ran: the bar offers Area summary.</summary>
        public bool OfferArea;
        /// <summary>The first-run hint is off: dismissed, or a sheet has been printed.</summary>
        public bool GuideOff;
        /// <summary>Generate 3D remembered 2D layers.</summary>
        public bool PlanRecall;
        /// <summary>Those layers are hidden. The bar offers Show 2D.</summary>
        public bool PlanHidden;
        public bool Millimetres = true;
    }

    public static class FileClassifier
    {
        /// <summary>Layers Forsk writes. A selection on one of them is never "the existing house?".</summary>
        static readonly HashSet<string> ForskLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wall", "door", "window", "room", "plan", "label", "space_divider",
            "A-WALL", "A-FLOR", "A-ROOF", "A-OPEN", "A-ROOM", "A-ANALYSE", "A-ANNO", "A-STAIR",
            "X-PLAN", "X-EXIST"
        };

        public static FileFacts Read(DocInput input)
        {
            input = input ?? new DocInput();
            // The plan cut is Forsk's own view helper, not something in the file.
            var rows = (input.Rows ?? new List<ChipRow>()).Where(r => r != null && !r.PlanCut).ToList();
            var facts = new FileFacts
            {
                Layouts = input.Layouts,
                SectionLetters = new List<string>(input.SectionLetters ?? new List<string>()),
                Meta = new Dictionary<string, string>(input.Meta ?? new Dictionary<string, string>()),
                FirmArchitect = input.FirmArchitect,
                PrintPages = input.PrintPages,
                PrintScale = input.PrintScale,
                Ink = string.IsNullOrWhiteSpace(input.Ink) ? "default" : input.Ink,
                DaylightQuality = Forsk.DaylightQuality.Normal(input.DaylightQuality),
                KeyPresent = input.KeyPresent,
                ListenerUp = input.ListenerUp,
                UndoNewest = input.UndoNewest,
                JustPrinted = input.JustPrinted,
                OfferArea = input.OfferArea,
                GuideOff = input.GuideOff,
                PlanRecall = global::RhinoMCPPlugin.Functions.PlanLayers.Stored(input.PlanLayers).Count > 0,
                PlanHidden = input.PlanHidden,
                Millimetres = input.Millimetres
            };
            var wallsRead = true;
            var mapAny = false;
            var mapShown = false;
            var mapStale = false;
            foreach (var row in rows)
            {
                if (row == null) continue;
                if (row.Existing)
                {
                    facts.HasExisting = true;
                    continue;
                }
                if (Is(row.ImportKind, "underlay"))
                {
                    facts.HasUnderlay = true;
                    if (Is(row.ScaleStatus, "user")) facts.ScaleSet = true;
                    var review = ReviewRows(row.Review);
                    if (review != null) facts.Review = review;
                    continue;
                }
                if (row.Generated)
                {
                    facts.HasGenerated = true;
                    if (Is(row.Kind, "wall"))
                    {
                        facts.HasWalls = true;
                        if (!row.PathReads) wallsRead = false;
                        if (row.Runs > 1) facts.WholeWalls = true;
                    }
                    else if (Is(row.Kind, "room"))
                    {
                        facts.HasRooms = true;
                        facts.Rooms.Add(RoomLine(row));
                    }
                    else if (Is(row.Kind, "opening_marker") && Is(row.OpeningKind, "window")) facts.HasWindows = true;
                    else if (Is(row.Kind, "opening_marker") && Is(row.OpeningKind, "door")) facts.HasDoors = true;
                    else if (Is(row.Kind, "stair")) facts.HasStairs = true;
                    else if (Is(row.Kind, "analysis"))
                    {
                        mapAny = true;
                        if (row.Visible) mapShown = true;
                        if (row.Stale) mapStale = true;
                    }
                    else if (Is(row.Kind, "drawing") && IsSheetCacheLayer(row.Layer)) facts.HasSheetCache = true;
                    continue;
                }
                if (row.Curve && IsPlanLayer(row.Layer))
                {
                    facts.HasPlanCurves = true;
                    continue;
                }
                facts.HasForeign = true;
            }
            facts.JoinGraph = facts.HasWalls && wallsRead;
            facts.Map = !mapAny ? MapState.None : mapStale ? MapState.Stale : mapShown ? MapState.Shown : MapState.Hidden;
            facts.Kind = Kind(facts, rows.Count);
            facts.SheetsStale = facts.Layouts > 0
                && !string.IsNullOrEmpty(input.StoredFingerprint)
                && input.StoredFingerprint != SheetFingerprint.Of(rows, input.Details);
            facts.Details = global::RhinoMCPPlugin.Functions.Details.Read(input.Details);
            facts.DetailNames = new Dictionary<string, string>(input.DetailNames ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            facts.DetailSheets = new List<string>(input.DetailSheets ?? new List<string>());
            ReadSelection(rows, facts);
            facts.SelectionKey = string.Join(",", rows.Where(ForskPick.DrivesSelection).Select(r => r.Id ?? "").OrderBy(id => id, StringComparer.Ordinal));
            facts.ModelKey = ModelKey(rows);
            return facts;
        }

        /// <summary>"Stue · 24.5 m²", or the name alone when no area is stored.</summary>
        static string RoomLine(ChipRow row)
        {
            var name = string.IsNullOrWhiteSpace(row.Name) ? "Room" : row.Name.Trim();
            var line = name;
            if (double.TryParse(row.Area, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mm2) && mm2 > 0)
                line = name + " · " + (mm2 / 1e6).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m²";
            return line + global::RhinoMCPPlugin.Functions.RoomTypes.LineSuffix(row.RoomType);
        }

        /// <summary>Every row's id, layer and edit stamp, sorted and hashed: it changes when the model does.</summary>
        static string ModelKey(IList<ChipRow> rows)
        {
            var parts = rows.Where(r => r != null)
                .Select(r => (r.Id ?? "") + ":" + (r.Layer ?? "") + ":" + (r.Stamp ?? "") + ":" + (r.Visible ? "1" : "0"))
                .OrderBy(p => p, StringComparer.Ordinal);
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
                return string.Concat(hash.Select(b => b.ToString("x2")));
            }
        }

        static FileKind Kind(FileFacts f, int rowCount)
        {
            if (f.HasWalls) return FileKind.Model;
            if (f.HasGenerated) return FileKind.Partial;
            if (f.HasUnderlay && !f.ScaleSet) return FileKind.Unscaled;
            if (f.HasPlanCurves) return FileKind.Plan;
            if (f.HasForeign && !f.HasUnderlay) return FileKind.Foreign;
            return rowCount == 0 ? FileKind.Empty : FileKind.NoPlan;
        }

        static void ReadSelection(IList<ChipRow> rows, FileFacts facts)
        {
            var kinds = new HashSet<Picked>();
            var openingKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A plate or a block picked with its marker is that one room or opening.
            facts.Selected = ForskPick.Things(rows);
            facts.PickedCount = facts.Selected.Count;
            foreach (var row in facts.Selected)
            {
                var picked = PickOf(row);
                kinds.Add(picked);
                if (picked == Picked.Wall) facts.PickedWalls++;
                if (picked == Picked.Opening)
                {
                    facts.PickedOpenings++;
                    openingKinds.Add(row.OpeningKind ?? "");
                }
            }
            if (facts.PickedCount == 0) facts.Picked = Picked.None;
            else facts.Picked = kinds.Count == 1 ? kinds.First() : Picked.Other;
            if (facts.Picked == Picked.Opening && openingKinds.Count == 1)
            {
                var kind = openingKinds.First();
                if (Is(kind, "door") || Is(kind, "window")) facts.PickedOpeningKind = kind.ToLowerInvariant();
            }
        }

        static Picked PickOf(ChipRow row)
        {
            if (row.Generated && !row.Existing)
            {
                if (Is(row.Kind, "wall")) return Picked.Wall;
                if (Is(row.Kind, "opening_marker") || Is(row.Kind, "opening")) return Picked.Opening;
                if (Is(row.Kind, "room") || Is(row.Kind, "room_plate")) return Picked.Room;
                if (Is(row.Kind, "stair")) return Picked.Stair;
                return Picked.Other;
            }
            if (row.Existing || Is(row.ImportKind, "underlay")) return Picked.Other;
            if (row.Curve && IsPlanLayer(row.Layer)) return Picked.PlanCurve;
            if ((row.Closed && row.Curve || row.Solid) && !IsForskLayer(row.Layer)) return Picked.Loose;
            return Picked.Other;
        }

        /// <summary>A layer the bake reads plan curves from. S-* sheets and X-EXIST never are.</summary>
        public static bool IsPlanLayer(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var key = name.Trim().ToLowerInvariant();
            if (key.StartsWith("s-")) return false;
            if (key == "x-exist") return false;
            if (key == "wall" || key == "door" || key == "window" || key == "room" || key == "plan" || key == "a-room")
                return true;
            return key.StartsWith("a-") && key.Contains("plan");
        }

        public static bool IsForskLayer(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var key = name.Trim();
            return ForskLayers.Contains(key) || IsPlanLayer(key) || key.StartsWith("S-", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The Make2D sheet cache (sheet_pack): S-PLAN and S-ELEV. S-DRAW is the layouts' own drawing.</summary>
        static bool IsSheetCacheLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer)) return false;
            var key = layer.Trim();
            return key.StartsWith("S-", StringComparison.OrdinalIgnoreCase) && !key.Equals("S-DRAW", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The review rows stored on the underlay, or null when none were stored.</summary>
        public static List<string> ReviewRows(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            try
            {
                return JArray.Parse(stored).Select(t => t.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        static bool Is(string value, string expected)
        {
            return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The model a print was drawn from: each generated model object's id and
    /// edit stamp, sorted and hashed. Drawings, layouts and the daylight map
    /// are not the model.
    /// </summary>
    public static class SheetFingerprint
    {
        public static string Of(IEnumerable<ChipRow> rows)
        {
            return Of(rows, null);
        }

        /// <summary>The stored details are part of what the sheets were drawn from.</summary>
        public static string Of(IEnumerable<ChipRow> rows, string details)
        {
            var parts = new List<string>();
            foreach (var row in rows ?? Enumerable.Empty<ChipRow>())
            {
                if (row == null || !row.Generated || row.Existing) continue;
                if (IsKind(row, "drawing") || IsKind(row, "layout") || IsKind(row, "analysis")) continue;
                if (!string.IsNullOrEmpty(row.Layer) && row.Layer.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add((row.Id ?? "") + ":" + (row.Stamp ?? ""));
            }
            parts.Sort(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(details)) parts.Add("details:" + details.Trim());
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
                var sb = new StringBuilder(40);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static bool IsKind(ChipRow row, string kind)
        {
            return string.Equals(row.Kind, kind, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// What a model edit does to the daylight map: every wall or opening edit
    /// makes it out of date; a wall edit also hides a shown map. Running
    /// daylight again paints a fresh map, which is current.
    /// </summary>
    public static class DaylightMap
    {
        public static void AfterEdit(MapEdit edit, ref bool visible, ref bool stale)
        {
            if (edit == MapEdit.None) return;
            stale = true;
            if (edit == MapEdit.Wall) visible = false;
        }
    }

    /// <summary>The last Forsk pill or answer: what ran, on which document, in which turn.</summary>
    public sealed class LastAction
    {
        public string Kind;
        public uint Doc;
        public long Turn;
        /// <summary>The undo record's name: Forsk: label or turn.</summary>
        public string Record;
        /// <summary>The record holds a change, so Rhino's Undo would take it back.</summary>
        public bool Undoable;
    }

    /// <summary>
    /// Keeps the last action while it is still the newest thing in the
    /// document. Rhino's own Undo or Redo, an object added, deleted or changed
    /// outside a Forsk call, a file opened, or another document made active
    /// clears it, and with it Undo and any "daylight ran" or "printed" branch.
    /// </summary>
    public sealed class LastActionTracker
    {
        public LastAction Current { get; private set; }

        public void Record(LastAction action)
        {
            Current = action;
        }

        /// <param name="insideForskCall">The change came from a Forsk tool call: it is part of the record.</param>
        public void ObjectChanged(bool insideForskCall)
        {
            if (!insideForskCall) Current = null;
        }

        public void UndoRedo()
        {
            Current = null;
        }

        public void DocumentOpened()
        {
            Current = null;
        }

        public void ActiveDocumentChanged()
        {
            Current = null;
        }

        public bool UndoNewest(uint doc)
        {
            return Current != null && Current.Undoable && Current.Doc == doc;
        }

        public bool Was(string kind, uint doc)
        {
            return Current != null && Current.Doc == doc && string.Equals(Current.Kind, kind, StringComparison.Ordinal);
        }
    }
}
