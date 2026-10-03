using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Which Rhino chrome the demo may close. The command field stays: it is the
/// top of the left sidebar, and closing that sidebar hides command entry.
/// A piece is toggled only when its live state differs from the target, so a
/// sidebar that is already shut is not opened by a blind toggle.
/// </summary>
public static class ForskChrome
{
    public const string Palettes = "palettes";
    public const string RightSidebar = "right";
    public const string Toolbar = "toolbar";
    public const string StatusBar = "status";
    public const string CommandField = "command";

    static readonly string[] Order = { Palettes, RightSidebar, Toolbar, StatusBar };

    public readonly struct Piece
    {
        public Piece(string id, bool open)
        {
            Id = id;
            Open = open;
        }

        public string Id { get; }
        public bool Open { get; }
    }

    /// <summary>Close a listed piece only when it is open. The command field is not listed.</summary>
    public static IReadOnlyList<Piece> HidePlan(IEnumerable<Piece> live)
    {
        var open = Map(live);
        var steps = new List<Piece>();
        foreach (var id in Order)
        {
            bool isOpen;
            if (!open.TryGetValue(id, out isOpen) || !isOpen) continue;
            steps.Add(new Piece(id, false));
        }
        return steps;
    }

    /// <summary>
    /// Put each saved piece back. A piece already in that state, or one the
    /// caller cannot see now, is left out so nothing is flipped blind.
    /// </summary>
    public static IReadOnlyList<Piece> RestorePlan(IEnumerable<Piece> snapshot, IEnumerable<Piece> current)
    {
        var want = Map(snapshot);
        var now = Map(current);
        var steps = new List<Piece>();
        foreach (var id in Order)
        {
            bool saved, live;
            if (!want.TryGetValue(id, out saved) || !now.TryGetValue(id, out live)) continue;
            if (saved == live) continue;
            steps.Add(new Piece(id, saved));
        }
        return steps;
    }

    public static string Format(IEnumerable<Piece> pieces)
    {
        var map = Map(pieces);
        var parts = new List<string>();
        foreach (var id in Order)
        {
            bool open;
            if (!map.TryGetValue(id, out open)) continue;
            parts.Add(id + "=" + (open ? "1" : "0"));
        }
        return string.Join(",", parts);
    }

    public static IReadOnlyList<Piece> Parse(string text)
    {
        var list = new List<Piece>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var part in text.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var id = part.Substring(0, eq).Trim();
            var value = part.Substring(eq + 1).Trim();
            if (!Listed(id)) continue;
            if (value != "1" && value != "0") continue;
            list.Add(new Piece(id, value == "1"));
        }
        return list;
    }

    static bool Listed(string id)
    {
        foreach (var candidate in Order)
            if (candidate.Equals(id, StringComparison.Ordinal)) return true;
        return false;
    }

    static Dictionary<string, bool> Map(IEnumerable<Piece> pieces)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (pieces == null) return map;
        foreach (var piece in pieces)
        {
            if (!Listed(piece.Id)) continue;
            map[piece.Id] = piece.Open;
        }
        return map;
    }
}
