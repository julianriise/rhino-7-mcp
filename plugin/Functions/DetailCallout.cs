using System;
using System.Collections.Generic;
using System.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The callout on the main plan for a detail: a 12 mm circle with a
/// divider, the plan detail's number on top (2.5 mm) and its sheet number
/// below (1.8 mm, A-50-001), a leader to the element ending in a 0.8 mm dot.
/// Inside a plan detail the same bubble, 9 mm, marks its companion drawings.
/// Paper mm about the bubble's centre. Pure, no RhinoCommon.
/// </summary>
public static class DetailCallout
{
    public const double CalloutMm = 12;
    public const double MarkMm = 9;
    public const double NumberMm = 2.5;
    public const double SheetMm = 1.8;
    /// <summary>The clear space kept round a bubble and its texts.</summary>
    public const double ClearMm = 0.8;

    public sealed class Bubble
    {
        public double Radius;
        public Pt DividerFrom;
        public Pt DividerTo;
        /// <summary>The centres of the number and of the sheet number.</summary>
        public Pt Number;
        public Pt Sheet;
        public double SheetWidth;
        /// <summary>The lower chord the sheet number must fit: the circle's width at the text's top and bottom.</summary>
        public double SheetRoom;
        public bool Fits => SheetWidth <= SheetRoom + 1e-9;
    }

    /// <summary>
    /// The bubble of a diameter (paper mm): the divider through the centre,
    /// the number above it and the sheet number below, measured by measure
    /// (paper mm of a text at 1 mm high) or 0.6 mm a character.
    /// </summary>
    public static Bubble Of(string sheetNo, double diameter = CalloutMm, Func<string, double> measure = null)
    {
        var r = diameter / 2.0;
        var width = (measure?.Invoke(sheetNo ?? "") ?? 0) * SheetMm;
        if (width <= 0) width = 0.6 * SheetMm * (sheetNo ?? "").Length;
        var gap = 0.5;
        var low = gap + SheetMm;
        var chord = 2 * Math.Sqrt(Math.Max(0, r * r - low * low));
        return new Bubble
        {
            Radius = r,
            DividerFrom = new Pt(-r, 0),
            DividerTo = new Pt(r, 0),
            Number = new Pt(0, gap + NumberMm / 2.0),
            Sheet = new Pt(0, -gap - SheetMm / 2.0),
            SheetWidth = width,
            SheetRoom = chord - 2 * ClearMm
        };
    }

    /// <summary>A detail's callout on the main plan: what it points at (world mm) and what it reads.</summary>
    public sealed class Callout
    {
        public string Detail;
        public string Name;
        public Pt Target;
        public int Number;
        public string Sheet;
    }

    /// <summary>
    /// One callout per detail whose plan detail is on a sheet: the plan
    /// detail's number over its sheet's number, pointing at the middle of the
    /// wall's main run on its centreline, or at the opening's centre there.
    /// </summary>
    public static List<Callout> Callouts(IList<DetailSheet.Sheet> sheets)
    {
        var ids = (sheets ?? new List<DetailSheet.Sheet>()).Select(s => s.Id).ToList();
        var callouts = new List<Callout>();
        foreach (var sheet in sheets ?? new List<DetailSheet.Sheet>())
        {
            foreach (var placed in sheet.Drawings.Where(p => p.Drawing.View == Details.Plan && p.Drawing.Facts?.Run != null))
            {
                var facts = placed.Drawing.Facts;
                callouts.Add(new Callout
                {
                    Detail = facts.Record.Id,
                    Name = placed.Drawing.Title,
                    Target = Target(facts),
                    Number = placed.Number,
                    Sheet = DetailSheet.Number(sheet.Id, ids)
                });
            }
        }
        return callouts;
    }

    public static Pt Target(Details.Facts facts)
    {
        var run = facts.Run;
        if (facts.IsWall || facts.Opening == null) return WallJoins.Middle(run);
        var c = (run.Near + run.Far) / 2.0;
        var u = facts.Opening.U;
        return new Pt(run.Dir.X * u + run.Normal.X * c, run.Dir.Y * u + run.Normal.Y * c);
    }

    /// <summary>The leader from the bubble's edge to the target.</summary>
    public static Pt LeaderStart(Pt centre, Pt target, double radius, int scale)
    {
        var dx = target.X - centre.X;
        var dy = target.Y - centre.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return centre;
        var r = radius * scale;
        return new Pt(centre.X + dx / len * r, centre.Y + dy / len * r);
    }
}
