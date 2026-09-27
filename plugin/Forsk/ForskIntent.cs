using System;
using System.Collections.Generic;
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
        General
    }

    /// <summary>
    /// One-turn tool bias from the message. Order is print, daylight, sheets, edit, build.
    /// An opening selection is edit when those words are absent.
    /// </summary>
    public static class ForskIntentRouter
    {
        public static ForskIntent Classify(string text, string target)
        {
            var t = Normalize(text);
            if (IsPrint(t)) return ForskIntent.Print;
            if (IsDaylight(t)) return ForskIntent.Daylight;
            if (IsSheets(t)) return ForskIntent.Sheets;
            if (IsEdit(t)) return ForskIntent.Edit;
            if (IsBuild(t)) return ForskIntent.Build;
            if (TargetIsOpening(target)) return ForskIntent.Edit;
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
            return t.Contains("sheet pack") || t.Contains("clear drawings");
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
            var verb = HasWord(t, "move") || HasWord(t, "add") || HasWord(t, "delete") || HasWord(t, "remove")
                || HasWord(t, "resize") || HasWord(t, "widen") || HasWord(t, "wider")
                || HasWord(t, "narrow") || HasWord(t, "narrower");
            var noun = HasWord(t, "window") || HasWord(t, "windows")
                || HasWord(t, "door") || HasWord(t, "doors")
                || HasWord(t, "opening") || HasWord(t, "openings");
            return verb && noun;
        }

        static bool IsBuild(string t)
        {
            if (t.Contains("wall height") || t.Contains("tilbygg") || t.Contains("påbygg")) return true;
            if (t.Contains("paabygg") || t.Contains("pabygg") || HasWord(t, "extension")) return true;
            if (HasWord(t, "generate") || HasWord(t, "bake") || HasWord(t, "rebuild") || HasWord(t, "regenerate"))
                return true;
            if (t.Contains("clear generated") || t.Contains("clear and regenerate")) return true;
            if (t.Contains("mark as existing") || t.Contains("mark existing")) return true;
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

        static bool TargetIsOpening(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            var t = target.ToLowerInvariant();
            return t.Contains("forsk:opening") || t.Contains("a-open");
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

    public enum DaylightAction
    {
        None,
        Run,
        Clear,
        MakeRooms
    }

    /// <summary>
    /// One document object as the Daylight chip sees it. The rows come from the
    /// same enumeration daylight_scene uses: hidden layers such as A-OPEN
    /// included, X-EXIST left out.
    /// </summary>
    public sealed class ChipRow
    {
        public bool Generated;
        public string Kind;
        public string OpeningKind;
        public string Layer;
        public bool ClosedCurve;
    }

    /// <summary>
    /// Chip state. The primary chip is Generate or Print. The secondary Daylight
    /// chip sits under Print: it runs when walls, windows, and rooms exist, clears
    /// while an overlay is on A-ANALYSE, and offers Make rooms when rooms are missing.
    /// </summary>
    public sealed class BakeChip
    {
        public const string NeedsWindows = "Needs windows";
        public const string NeedsWindowsHint = "Daylight comes in through windows. Add a window, then run it.";

        public bool HasPlan;
        public bool HasWalls;
        public bool HasWindows;
        public bool HasRooms;
        public bool HasRoomCurves;
        public bool HasOverlay;
        public bool ShowPrint => HasWalls;
        public bool ShowGenerate => HasPlan && !HasWalls;
        public bool Visible => ShowPrint || ShowGenerate;
        public string Label => ShowPrint ? "Print PDF" : "Generate 3D model";

        public bool ShowDaylight => HasWalls || HasOverlay;

        public DaylightAction Daylight
        {
            get
            {
                if (HasOverlay) return DaylightAction.Clear;
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
                    case DaylightAction.Clear: return "Clear daylight";
                    case DaylightAction.MakeRooms: return "Make rooms";
                    case DaylightAction.None: return NeedsWindows;
                    default: return "Daylight";
                }
            }
        }

        public string DaylightHint => Daylight == DaylightAction.None ? NeedsWindowsHint : null;

        /// <summary>Daylight facts from the model rows. Called on every refresh; nothing is cached.</summary>
        public void ReadDaylight(IEnumerable<ChipRow> rows)
        {
            HasWindows = HasRooms = HasRoomCurves = HasOverlay = false;
            foreach (var row in rows)
            {
                if (row == null) continue;
                if (!row.Generated)
                {
                    if (row.ClosedCurve && string.Equals(row.Layer, "A-ROOM", StringComparison.OrdinalIgnoreCase))
                        HasRoomCurves = true;
                    continue;
                }
                if (Is(row.Kind, "room")) HasRooms = true;
                else if (Is(row.Kind, "analysis")) HasOverlay = true;
                else if (Is(row.Kind, "opening_marker") && Is(row.OpeningKind, "window")) HasWindows = true;
            }
        }

        static bool Is(string value, string expected)
        {
            return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
