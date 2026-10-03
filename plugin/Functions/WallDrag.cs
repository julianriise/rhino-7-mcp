using System;
using System.Collections.Generic;
using System.Globalization;
using RhinoMCPPlugin.Forsk;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// A wall drag locked to the run's normal. Sideways motion is ignored, the
/// distance snaps, and the words for the prompt and the receipt live here.
/// Pure geometry, no RhinoCommon, so it tests headless. The command draws
/// and move_wall commits.
/// </summary>
public static class WallDrag
{
    public const int DefaultStep = 10;
    public static readonly int[] Steps = { 10, 50, 100 };

    /// <summary>Signed millimetres along the normal. Motion across the normal is ignored.</summary>
    public static double Along(Pt start, Pt now, Pt normal)
    {
        return (now.X - start.X) * normal.X + (now.Y - start.Y) * normal.Y;
    }

    /// <summary>
    /// The nearest multiple of the step. A half step rounds away from zero,
    /// so 15 on a 10 mm step is 20 and -25 on a 50 mm step is -50.
    /// </summary>
    public static double Snap(double raw, double step)
    {
        if (step <= 0) step = DefaultStep;
        return Math.Round(raw / step, MidpointRounding.AwayFromZero) * step;
    }

    /// <summary>
    /// A typed distance. A positive number goes to the side the mouse is on.
    /// sign +1 is the +normal side, -1 the other. 0 is the default, +normal,
    /// which the command replaces with the outward side when the mouse has not chosen.
    /// A negative number reverses that side.
    /// </summary>
    public static double Typed(double number, int sign)
    {
        var way = sign < 0 ? -1.0 : 1.0;
        return number * way;
    }

    /// <summary>
    /// The compass word and the positive distance move_wall takes.
    /// Null when the snapped distance is under one step: the wall was not dragged.
    /// </summary>
    public static (string Compass, double Mm)? Toward(WallEdit.Run run, double by, double step = DefaultStep)
    {
        if (run == null) return null;
        var snapped = Snap(by, step);
        if (Math.Abs(snapped) < step) return null;
        return (WallEdit.Heading(run, snapped), Math.Abs(snapped));
    }

    public static string NotMoved(bool nb) => Text("wall.drag.not", nb);

    public static string Many(bool nb) => Text("wall.drag.many", nb);

    public static string Curved(bool nb) => Text("wall.drag.curved", nb);

    /// <summary>
    /// Null when this record is one straight run and can be dragged.
    /// A curve is named first, then a record that holds several runs.
    /// </summary>
    public static string Check(WallJoins.Graph graph, List<List<Pt>> rings, WallEdit.Run run)
    {
        return Check(graph, rings, run, 1.0, false);
    }

    public static string Check(WallJoins.Graph graph, List<List<Pt>> rings, WallEdit.Run run, double tol, bool nb)
    {
        if (graph?.Shape == null || rings == null) return Many(nb);
        if (!WallSplit.Straight(graph, tol, out _)) return Curved(nb);
        var only = WallJoins.RunIn(graph, rings);
        if (only < 0) return Many(nb);
        if (run != null && graph.Find(run, tol) != only) return Many(nb);
        return null;
    }

    /// <summary>+1 when +normal is out, -1 when +normal is in, 0 for an inner wall.</summary>
    public static int Outward(string outerSide, WallEdit.Run run)
    {
        if (run == null || string.IsNullOrEmpty(outerSide)) return 0;
        return WallEdit.Heading(run, 1) == outerSide ? 1 : -1;
    }

    /// <summary>The snapped distance as the live dimension reads it.</summary>
    public static string Dimension(double raw, double step, int outward, bool nb)
    {
        return FormatDimension(Snap(raw, step), outward, nb);
    }

    /// <summary>The same words for a distance that is already snapped.</summary>
    public static string FormatDimension(double snapped, int outward, bool nb)
    {
        var n = (long)Math.Round(snapped, MidpointRounding.AwayFromZero);
        if (n == 0) return Text("wall.drag.dim.zero", nb);
        var abs = Math.Abs(n).ToString(CultureInfo.InvariantCulture);
        if (outward == 0)
            return ForskText.Format(TextKey(n > 0 ? "wall.drag.dim.plus" : "wall.drag.dim.minus", nb), "n", abs);
        var outwardMove = n * (outward < 0 ? -1 : 1) > 0;
        return ForskText.Format(TextKey(outwardMove ? "wall.drag.dim.out" : "wall.drag.dim.in", nb), "n", abs);
    }

    /// <summary>The original face at mid-run, and that face after the signed move.</summary>
    public static (Pt From, Pt To) Measure(WallEdit.Run run, double by)
    {
        var s = (run.Lo + run.Hi) / 2.0;
        var face = by >= 0 ? run.Far : run.Near;
        var from = new Pt(run.Dir.X * s + run.Normal.X * face, run.Dir.Y * s + run.Normal.Y * face);
        var to = new Pt(from.X + run.Normal.X * by, from.Y + run.Normal.Y * by);
        return (from, to);
    }

    static string Text(string key, bool nb) => ForskText.Get(TextKey(key, nb));

    static string TextKey(string key, bool nb) => nb && ForskText.Has(key + ".nb") ? key + ".nb" : key;
}
