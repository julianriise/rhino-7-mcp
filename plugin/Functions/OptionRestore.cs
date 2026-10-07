using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// AN.5 restore, first cut: from the model now back to an option. Walls whose
/// record changed get the saved record back; doors and windows move and
/// resize back. What restore cannot put back yet (a wall or opening added or
/// removed since, a changed door or window type, a file saved before records
/// were kept) is refused with the reason. No RhinoCommon.
/// </summary>
public static class OptionRestore
{
    public sealed class WallBack
    {
        public string Id;
        public string Record;
    }

    public sealed class Move
    {
        public string Id;
        public double Dx, Dy;
    }

    public sealed class Size
    {
        public string Id;
        public double Width, Sill, Head;
    }

    public sealed class Steps
    {
        public List<WallBack> Walls = new List<WallBack>();
        public List<Move> Moves = new List<Move>();
        public List<Size> Sizes = new List<Size>();
        /// <summary>Why the option cannot be restored, or null.</summary>
        public string Refusal;
        public bool Nothing => Refusal == null && Walls.Count == 0 && Moves.Count == 0 && Sizes.Count == 0;
    }

    public static Steps Plan(OptionSnapshot.Snapshot option, OptionSnapshot.Snapshot now)
    {
        var steps = new Steps();
        var name = option?.Name ?? "";
        var problems = new List<(string Noun, string Verb)>();
        var walls = (now?.Walls ?? new List<OptionSnapshot.Wall>()).GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First());
        var saved = (option?.Walls ?? new List<OptionSnapshot.Wall>()).GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var id in walls.Keys.Where(k => !saved.ContainsKey(k))) problems.Add(("wall", "added"));
        foreach (var id in saved.Keys.Where(k => !walls.ContainsKey(k))) problems.Add(("wall", "removed"));
        var unrecorded = false;
        foreach (var pair in saved.Where(p => walls.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var was = pair.Value.Record;
            if (was == walls[pair.Key].Record) continue;
            if (string.IsNullOrEmpty(was)) { unrecorded = true; continue; }
            steps.Walls.Add(new WallBack { Id = pair.Key, Record = was });
        }
        var openings = (now?.Openings ?? new List<OptionSnapshot.Opening>()).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        var savedOpenings = (option?.Openings ?? new List<OptionSnapshot.Opening>()).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var o in openings.Values.Where(o => !savedOpenings.ContainsKey(o.Id))) problems.Add((Noun(o), "added"));
        foreach (var o in savedOpenings.Values.Where(o => !openings.ContainsKey(o.Id))) problems.Add((Noun(o), "removed"));
        foreach (var was in savedOpenings.Values.Where(o => openings.ContainsKey(o.Id)).OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var o = openings[was.Id];
            if (o.Type != was.Type || o.Hand != was.Hand || o.Swing != was.Swing) { problems.Add((Noun(o), "changed type")); continue; }
            double dx = was.Centre.X - o.Centre.X, dy = was.Centre.Y - o.Centre.Y;
            if (Math.Abs(dx) > OptionSnapshot.ToleranceMm || Math.Abs(dy) > OptionSnapshot.ToleranceMm)
                steps.Moves.Add(new Move { Id = was.Id, Dx = dx, Dy = dy });
            if (!Near(was.Width, o.Width) || !Near(was.Sill, o.Sill) || !Near(was.Head, o.Head))
                steps.Sizes.Add(new Size { Id = was.Id, Width = was.Width, Sill = was.Sill, Head = was.Head });
        }
        if (problems.Count > 0)
            steps.Refusal = "Option " + name + " cannot be restored yet: " + Said(problems) + " since. Undo those first, or save the model as a new option.";
        else if (unrecorded)
            steps.Refusal = "Option " + name + " was saved before restore was possible: save it again to restore it.";
        return steps;
    }

    static string Noun(OptionSnapshot.Opening o) => string.IsNullOrEmpty(o.Kind) ? "opening" : o.Kind;

    static bool Near(double a, double b) => Math.Abs(a - b) <= OptionSnapshot.ToleranceMm;

    /// <summary>"1 wall added and 1 door removed".</summary>
    static string Said(List<(string Noun, string Verb)> problems)
    {
        var parts = problems.GroupBy(p => p)
            .Select(g => g.Count().ToString(CultureInfo.InvariantCulture) + " " + (g.Count() == 1 ? g.Key.Noun : g.Key.Noun + "s") + " " + g.Key.Verb)
            .ToList();
        return parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
    }
}
