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
        /// <summary>Sections stored with section_add.</summary>
        public int Sections;
        public string Ink = "default";
        public bool Millimetres = true;
        public bool KeyPresent;
        public bool ListenerUp = true;
        /// <summary>The last Forsk record is still the newest thing in this document (LastActionTracker).</summary>
        public bool UndoNewest;
    }

    /// <summary>The classifier's answer. One pure read of the rows; no RhinoCommon.</summary>
    public sealed class FileFacts
    {
        public FileKind Kind;
        public bool HasGenerated;
        public bool HasWalls;
        public bool HasPlanCurves;
        public bool HasUnderlay;
        public bool ScaleSet;
        public bool HasForeign;
        public bool HasExisting;
        public bool HasRooms;
        public bool HasWindows;
        public bool HasDoors;
        public bool HasSheetCache;
        public MapState Map;
        public List<string> Review;
        public bool ReviewStored => Review != null;
        public int Layouts;
        public int Sections;
        public bool SheetsStale;
        public string Ink = "default";
        public Picked Picked;
        public int PickedCount;
        /// <summary>door or window when every picked opening is that kind, else null.</summary>
        public string PickedOpeningKind;
        public bool KeyPresent;
        public bool ListenerUp = true;
        public bool UndoNewest;
        public bool Millimetres = true;
    }

    public static class FileClassifier
    {
        /// <summary>Layers Forsk writes. A selection on one of them is never "the existing house?".</summary>
        static readonly HashSet<string> ForskLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wall", "door", "window", "room", "plan", "label", "space_divider",
            "A-WALL", "A-FLOR", "A-ROOF", "A-OPEN", "A-ROOM", "A-ANALYSE", "A-ANNO",
            "X-PLAN", "X-EXIST"
        };

        public static FileFacts Read(DocInput input)
        {
            input = input ?? new DocInput();
            var rows = input.Rows ?? new List<ChipRow>();
            var facts = new FileFacts
            {
                Layouts = input.Layouts,
                Sections = input.Sections,
                Ink = string.IsNullOrWhiteSpace(input.Ink) ? "default" : input.Ink,
                KeyPresent = input.KeyPresent,
                ListenerUp = input.ListenerUp,
                UndoNewest = input.UndoNewest,
                Millimetres = input.Millimetres
            };
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
                    if (Is(row.Kind, "wall")) facts.HasWalls = true;
                    else if (Is(row.Kind, "room")) facts.HasRooms = true;
                    else if (Is(row.Kind, "opening_marker") && Is(row.OpeningKind, "window")) facts.HasWindows = true;
                    else if (Is(row.Kind, "opening_marker") && Is(row.OpeningKind, "door")) facts.HasDoors = true;
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
            facts.Map = !mapAny ? MapState.None : mapStale ? MapState.Stale : mapShown ? MapState.Shown : MapState.Hidden;
            facts.Kind = Kind(facts, rows.Count);
            facts.SheetsStale = facts.Layouts > 0
                && !string.IsNullOrEmpty(input.StoredFingerprint)
                && input.StoredFingerprint != SheetFingerprint.Of(rows);
            ReadSelection(rows, facts);
            return facts;
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
            foreach (var row in rows)
            {
                if (row == null || !row.Selected) continue;
                facts.PickedCount++;
                var picked = PickOf(row);
                kinds.Add(picked);
                if (picked == Picked.Opening) openingKinds.Add(row.OpeningKind ?? "");
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
                if (Is(row.Kind, "room")) return Picked.Room;
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
            var parts = new List<string>();
            foreach (var row in rows ?? Enumerable.Empty<ChipRow>())
            {
                if (row == null || !row.Generated || row.Existing) continue;
                if (IsKind(row, "drawing") || IsKind(row, "layout") || IsKind(row, "analysis")) continue;
                if (!string.IsNullOrEmpty(row.Layer) && row.Layer.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add((row.Id ?? "") + ":" + (row.Stamp ?? ""));
            }
            parts.Sort(StringComparer.Ordinal);
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
