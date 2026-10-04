using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// <summary>The lower chord the sheet number must fit: the circle's width at the text's bottom, less ClearMm each side.</summary>
        public double SheetRoom;
        public double NumberWidth;
        /// <summary>The upper chord the number must fit: the circle's width at the text's top, less ClearMm each side.</summary>
        public double NumberRoom;
        public bool Fits => SheetWidth <= SheetRoom + 1e-6 && NumberWidth <= NumberRoom + 1e-6;
    }

    /// <summary>
    /// The bubble for its texts (paper mm): at least the diameter, wider when
    /// the number or the sheet number would not clear the circle; the
    /// divider through the centre, the number above it and the sheet number
    /// below. Texts are measured by measure (paper mm of a text 1 mm high),
    /// else 0.6 mm a character.
    /// </summary>
    public static Bubble Of(string sheetNo, double diameter = CalloutMm, Func<string, double> measure = null, string number = null)
    {
        double Width(string text, double height)
        {
            var w = (measure?.Invoke(text ?? "") ?? 0) * height;
            return w > 0 ? w : 0.6 * height * (text ?? "").Length;
        }
        double Need(double width, double y) => Math.Sqrt((width / 2.0 + ClearMm) * (width / 2.0 + ClearMm) + y * y);
        double Room(double r, double y) => 2 * Math.Sqrt(Math.Max(0, r * r - y * y)) - 2 * ClearMm;
        const double gap = 0.5;
        var low = gap + SheetMm;
        var high = gap + NumberMm;
        var sheetWidth = Width(sheetNo, SheetMm);
        var numberWidth = string.IsNullOrEmpty(number) ? 0 : Width(number, NumberMm);
        var r = Math.Max(diameter / 2.0, Math.Max(Need(sheetWidth, low), numberWidth > 0 ? Need(numberWidth, high) : 0));
        return new Bubble
        {
            Radius = r,
            DividerFrom = new Pt(-r, 0),
            DividerTo = new Pt(r, 0),
            Number = new Pt(0, gap + NumberMm / 2.0),
            Sheet = new Pt(0, -gap - SheetMm / 2.0),
            SheetWidth = sheetWidth,
            SheetRoom = Room(r, low),
            NumberWidth = numberWidth,
            NumberRoom = Room(r, high)
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

    /// <summary>Paper mm a mark's arrow reaches past its bubble.</summary>
    public const double MarkArrowMm = 2.0;

    /// <summary>
    /// A companion mark where it is drawn, drawing mm: what it reads (its
    /// drawing's number over that sheet's number) in its bubble, the point it
    /// marks, its bubble's centre, moved off the point (then a leader runs back).
    /// </summary>
    public sealed class PlacedMark
    {
        public Details.Mark Mark;
        public int Number;
        public string Sheet;
        public Bubble Bubble;
        public Pt At;
        public Pt Centre;
        public bool Moved;
        /// <summary>Half the box it keeps clear, paper mm: its bubble, and the arrow past it along its look.</summary>
        public double HalfX => Bubble.Radius + MarkArrowMm * Math.Abs(Mark.Look.X);
        public double HalfY => Bubble.Radius + MarkArrowMm * Math.Abs(Mark.Look.Y);
    }

    /// <summary>
    /// A plan detail's marks whose drawing is on a sheet, in drawing mm
    /// (frame u through Map, then shifted), each in a bubble sized to its
    /// texts. On its point when it clears what is drawn: taken (the
    /// dimensions as LayoutFixed laid them), lines (the drawing's strokes),
    /// the openings its cut passes through and the poché. Else at the
    /// nearest clear spot inside the drawing's band that PlanDims.PlaceLeader
    /// finds, tried in turn beyond its point on its own side of the wall
    /// (past the outermost dimension there), at either end of the run, on
    /// the other side, then anywhere; else on its point. Each joins taken.
    /// </summary>
    public static List<PlacedMark> PlaceMarks(Details.Drawing d, IList<DetailSheet.Sheet> plan, Pt shift, int scale,
        List<PlanDims.Obstacle> taken, List<List<List<Pt>>> walls, Func<string, double> measure = null,
        IList<PlanDims.Obstacle> lines = null)
    {
        var placed = new List<PlacedMark>();
        if (d == null || scale < 1) return placed;
        taken = taken ?? new List<PlanDims.Obstacle>();
        walls = walls ?? new List<List<List<Pt>>>();
        var ids = (plan ?? new List<DetailSheet.Sheet>()).Select(sh => sh.Id).ToList();
        var s = (double)scale;
        var clear = PlanDims.ClearMm * s;
        var band = Details.BandMm * s;
        var x0 = d.Map(d.U0) + shift.X - band;
        var x1 = d.Map(d.U0) + d.Width + shift.X + band;
        var y0 = d.V0 + shift.Y - band;
        var y1 = d.V1 + shift.Y + band;
        var far = 4 * PlanDims.LeaderReachMm * s;
        var outside = new[]
        {
            new RoomDetect.Box(x0 - far, y0 - far, x0, y1 + far),
            new RoomDetect.Box(x1, y0 - far, x1 + far, y1 + far),
            new RoomDetect.Box(x0 - far, y0 - far, x1 + far, y0),
            new RoomDetect.Box(x0 - far, y1, x1 + far, y1 + far)
        }.Select(b => new PlanDims.Obstacle(b, PlanDims.Kind.Line)).ToList();
        var run = d.Facts?.Run;
        double lo = run == null ? d.V0 : Math.Min(run.Near, run.Far), hi = run == null ? d.V1 : Math.Max(run.Near, run.Far);
        var openings = (run == null ? new List<Details.Hosted>() : Details.PlanOpenings(d.Facts)).Select(o =>
        {
            var a = d.Map(Details.U(d.Facts, o.U - o.Opening.Width / 2.0)) + shift.X;
            var b = d.Map(Details.U(d.Facts, o.U + o.Opening.Width / 2.0)) + shift.X;
            return new PlanDims.Obstacle(new RoomDetect.Box(Math.Min(a, b), lo + shift.Y, Math.Max(a, b), hi + shift.Y), PlanDims.Kind.Line);
        });
        var fixedLines = outside.Concat(openings).Concat(lines ?? new List<PlanDims.Obstacle>()).ToList();
        var left = d.Map(d.U0) + shift.X;
        var right = left + d.Width;
        RoomDetect.Box Square(Pt c, double hx, double hy) => new RoomDetect.Box(c.X - hx, c.Y - hy, c.X + hx, c.Y + hy);
        PlanDims.Obstacle Block(double minX, double minY, double maxX, double maxY) =>
            new PlanDims.Obstacle(new RoomDetect.Box(minX, minY, maxX, maxY), PlanDims.Kind.Line);
        foreach (var mark in d.Marks)
        {
            if (!DetailSheet.Find(plan, d.Facts.Record.Id, mark.View, out var sheet, out var target)) continue;
            var sheetNo = DetailSheet.Number(sheet.Id, ids);
            var number = target.Number.ToString(CultureInfo.InvariantCulture);
            var one = new PlacedMark { Mark = mark, Number = target.Number, Sheet = sheetNo, Bubble = Of(sheetNo, MarkMm, measure, number) };
            var hx = one.HalfX * s;
            var hy = one.HalfY * s;
            var at = new Pt(d.Map(mark.At.X) + shift.X, mark.At.Y + shift.Y);
            var drawn = fixedLines.Concat(taken).ToList();
            var centre = at;
            var moved = false;
            if (drawn.Any(o => Schedules.Overlaps(Square(at, hx, hy), o.Box, clear)) || Schedules.OnWalls(Square(at, hx + clear, hy + clear), walls))
            {
                var dot = new List<Pt>
                {
                    new Pt(at.X - clear, at.Y - clear), new Pt(at.X + clear, at.Y - clear),
                    new Pt(at.X + clear, at.Y + clear), new Pt(at.X - clear, at.Y + clear)
                };
                // Its own side of the wall: below it when the mark is below the wall's middle.
                var below = at.Y < (lo + hi) / 2.0 + shift.Y;
                double xa = x0 - far, xb = x1 + far, ya = y0 - far, yb = y1 + far;
                var tries = new[]
                {
                    below ? Block(xa, at.Y, xb, yb) : Block(xa, ya, xb, at.Y),
                    Block(left, ya, right, yb),
                    below ? Block(xa, ya, xb, hi + shift.Y) : Block(xa, lo + shift.Y, xb, yb),
                    (PlanDims.Obstacle?)null
                };
                foreach (var keepOut in tries)
                {
                    var those = keepOut == null ? drawn : new[] { keepOut.Value }.Concat(drawn).ToList();
                    moved = PlanDims.PlaceLeader(at, dot, hx, hy, scale, those, walls, out var spot, out _);
                    if (!moved) continue;
                    centre = spot;
                    break;
                }
            }
            taken.Add(new PlanDims.Obstacle(Square(centre, hx, hy), PlanDims.Kind.Text));
            if (moved)
                taken.Add(new PlanDims.Obstacle(PlanDims.SegBox(new PlanDims.Seg(at, LeaderStart(centre, at, one.Bubble.Radius, scale))), PlanDims.Kind.Line));
            one.At = at;
            one.Centre = centre;
            one.Moved = moved;
            placed.Add(one);
        }
        return placed;
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
