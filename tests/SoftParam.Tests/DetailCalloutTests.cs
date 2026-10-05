using RhinoMCPPlugin.Functions;
using Xunit;
using static SoftParam.Tests.DetailFixtures;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// D4 callouts: the 12 mm bubble with its divider, the number over the sheet
/// number, and the sheet number fitting the lower chord.
/// </summary>
public class DetailCalloutTests
{
    [Fact]
    public void A50001_FitsThe12mmBubble_At1Point8()
    {
        var bubble = DetailCallout.Of("A-50-001");
        Assert.Equal(6, bubble.Radius, 6);
        Assert.True(bubble.Fits, bubble.SheetWidth + " in " + bubble.SheetRoom);
        Assert.True(bubble.Number.Y > 0 && bubble.Sheet.Y < 0, "the number above the divider, the sheet below");
        Assert.Equal(-6, bubble.DividerFrom.X, 6);
        Assert.Equal(6, bubble.DividerTo.X, 6);
    }

    [Theory]
    [InlineData("A-50-002", DetailCallout.MarkMm)]
    [InlineData("A-50-002", DetailCallout.CalloutMm)]
    [InlineData("A-50-0001-long", DetailCallout.CalloutMm)]
    public void ABubble_GrowsToFitItsSheetNumber(string sheetNo, double diameter)
    {
        var bubble = DetailCallout.Of(sheetNo, diameter, Arial, "12");
        Assert.True(bubble.Fits, bubble.SheetWidth + " in " + bubble.SheetRoom);
        Assert.True(bubble.Radius >= diameter / 2 - 1e-9);
        // No bigger than it must be: the sheet number just fits, or the bubble kept its size.
        Assert.True(Math.Abs(bubble.Radius - diameter / 2) < 1e-9 || Math.Abs(bubble.SheetRoom - bubble.SheetWidth) < 1e-6);
        Assert.Equal(-bubble.Radius, bubble.DividerFrom.X, 9);
        Assert.Equal(bubble.Radius, bubble.DividerTo.X, 9);
    }

    [Fact]
    public void TheMark_GrowsForA50002_AndTheCalloutKeeps12mm()
    {
        // 7.4 mm of text against a 9 mm circle's 6.1 mm of room on the sheet number's line.
        Assert.True(DetailCallout.Of("A-50-002", DetailCallout.MarkMm, Arial).Radius > DetailCallout.MarkMm / 2 + 0.4);
        Assert.Equal(6, DetailCallout.Of("A-50-002", DetailCallout.CalloutMm, Arial).Radius, 9);
    }

    [Fact]
    public void AWideNumber_GrowsTheBubbleToo()
    {
        var bubble = DetailCallout.Of("A", DetailCallout.MarkMm, t => t.Length * 1.0, "1234567");
        Assert.True(bubble.NumberWidth <= bubble.NumberRoom + 1e-6, bubble.NumberWidth + " in " + bubble.NumberRoom);
        Assert.True(bubble.Radius > DetailCallout.MarkMm / 2);
    }

    static Pt Frame(Details.Drawing d, Pt p) => new(p.X * d.X.X + p.Y * d.X.Y, p.X * d.Y.X + p.Y * d.Y.Y);

    /// <summary>The openings a plan cut at 1200 passes through, each as its box across its wall (world mm).</summary>
    static List<List<Pt>> CutOpenings(IfcExport.Model model)
    {
        var boxes = new List<List<Pt>>();
        foreach (var o in model.Openings.Where(o => o.Sill < ForskPlanCut.AboveFloorMm && o.Head > ForskPlanCut.AboveFloorMm))
        {
            var wall = model.Walls.Single(w => w.Id == o.Host).Rings[0];
            double y0 = wall.Min(p => p.Y), y1 = wall.Max(p => p.Y);
            boxes.Add(DetailFixtures.Box(o.Centre.X - o.Width / 2, y0, o.Centre.X + o.Width / 2, y1)[0]);
        }
        return boxes;
    }

    /// <summary>The smoke garage's pocket slot (world mm), none on the brief's garage.</summary>
    static List<List<Pt>> Pocket(IfcExport.Model model) =>
        model.Openings.Any(o => o.Id == "o-d04")
            ? new List<List<Pt>> { DetailFixtures.Box(DetailFixtures.PocketLo, 0, DetailFixtures.PocketHi, 200)[0] }
            : new List<List<Pt>>();

    /// <summary>The fixture's walls (boxes along x or y) less their cut openings and the pocket slot, as the plan's poché is.</summary>
    static List<List<Pt>> WallPieces(IfcExport.Model model)
    {
        var pieces = new List<List<Pt>>();
        foreach (var wall in model.Walls)
        {
            var r = wall.Rings[0];
            double x0 = r.Min(p => p.X), y0 = r.Min(p => p.Y), x1 = r.Max(p => p.X), y1 = r.Max(p => p.Y);
            var from = x0;
            foreach (var o in CutOpenings(model).Concat(Pocket(model)).Where(b => b.Min(p => p.Y) == y0 && b.Max(p => p.Y) == y1).OrderBy(b => b.Min(p => p.X)))
            {
                pieces.Add(DetailFixtures.Box(from, y0, o.Min(p => p.X), y1)[0]);
                from = o.Max(p => p.X);
            }
            pieces.Add(DetailFixtures.Box(from, y0, x1, y1)[0]);
        }
        return pieces;
    }

    /// <summary>World rings in a plan detail, clipped to each stretch it keeps and moved as the bake moves them; the crop edges they meet, too.</summary>
    static List<List<Pt>> InDrawing(Details.Drawing d, IEnumerable<List<Pt>> rings, List<KeyValuePair<Pt, Pt>> cropEdges = null)
    {
        var keep = d.Breaks?.Keep() ?? new List<KeyValuePair<double, double>> { new(d.U0, d.U1) };
        var drawn = new List<List<Pt>>();
        foreach (var world in rings)
        foreach (var part in keep)
        {
            var rect = new DetailClip.Rect(part.Key, d.V0, part.Value, d.V1);
            var ring = DetailClip.Ring(world.Select(p => Frame(d, p)).ToList(), rect);
            if (ring.Count < 3) continue;
            var du = d.Map(part.Key) - part.Key;
            Pt Move(Pt p) => new(p.X + du, p.Y);
            cropEdges?.AddRange(DetailClip.CropEdges(ring, rect, 0.5).Select(e => new KeyValuePair<Pt, Pt>(Move(e.Key), Move(e.Value))));
            drawn.Add(ring.Select(Move).ToList());
        }
        return drawn;
    }

    /// <summary>The walls' poché in a plan detail, with a gap at every opening its cut passes through.</summary>
    static List<List<List<Pt>>> Poche(Details.Drawing d, IfcExport.Model model) =>
        InDrawing(d, WallPieces(model)).Select(r => new List<List<Pt>> { r }).ToList();

    /// <summary>The sheets the drawing's detail packs onto, alone.</summary>
    static List<DetailSheet.Sheet> SheetsOf(Details.Drawing d) => DetailSheet.Plan(DetailSheet.Items(new[] { d.Facts }));

    static List<DetailCallout.PlacedMark> Marks(Details.Drawing d, IfcExport.Model model, out List<PlanDims.Chain> laid)
    {
        var walls = Poche(d, model);
        var taken = new List<PlanDims.Obstacle>();
        laid = PlanDims.LayoutFixed(DetailDims.PlanChains(d.Facts, d), 20, taken, walls, text => 0.6 * PlanDims.TextMm * text.Length);
        return DetailCallout.PlaceMarks(d, SheetsOf(d), new Pt(0, 0), 20, taken, walls, Arial);
    }

    static RoomDetect.Box Square(DetailCallout.PlacedMark m) =>
        new(m.Centre.X - m.HalfX * 20, m.Centre.Y - m.HalfY * 20, m.Centre.X + m.HalfX * 20, m.Centre.Y + m.HalfY * 20);

    static IEnumerable<KeyValuePair<Pt, Pt>> Edges(List<Pt> ring) =>
        ring.Select((p, i) => new KeyValuePair<Pt, Pt>(p, ring[(i + 1) % ring.Count]));

    /// <summary>
    /// Every bubble on a sheet's plan details against what the sheet draws
    /// there: the poché's outline, the break lines, each cut opening's
    /// doors or window (its box across the wall), the dimension lines,
    /// ticks and values, and the other bubbles.
    /// </summary>
    static void AssertBubblesInClearSpace(Details.Drawing d, List<DetailSheet.Sheet> sheets, IfcExport.Model model,
        out List<DetailCallout.PlacedMark> marks, out List<PlanDims.Chain> laid)
    {
        var cropEdges = new List<KeyValuePair<Pt, Pt>>();
        var poche = InDrawing(d, WallPieces(model), cropEdges);
        var walls = poche.Select(r => new List<List<Pt>> { r }).ToList();
        var openings = InDrawing(d, CutOpenings(model).Concat(Pocket(model)));
        var leaf = InDrawing(d, Pocket(model).Select(b => DetailFixtures.Box(b[0].X, 95, b[2].X, 105)[0]));
        // What the bake strokes: the poché's outline, the break lines, and the doors, windows and pocket seen in their gaps.
        var drawnLines = poche.SelectMany(Edges)
            .Concat(cropEdges.SelectMany(e => Edges(DetailClip.BreakLine(e.Key, e.Value, 20)).SkipLast(1)))
            .Concat(openings.Concat(leaf).SelectMany(Edges))
            .ToList();
        var taken = new List<PlanDims.Obstacle>();
        laid = PlanDims.LayoutFixed(DetailDims.PlanChains(d.Facts, d), 20, taken, walls, text => Arial(text) * PlanDims.TextMm);
        marks = DetailCallout.PlaceMarks(d, sheets, new Pt(0, 0), 20, taken, walls, Arial,
            drawnLines.Select(l => new PlanDims.Obstacle(PlanDims.SegBox(new PlanDims.Seg(l.Key, l.Value)), PlanDims.Kind.Line)).ToList());
        var lines = drawnLines
            .Concat(laid.Where(c => c.Placed).SelectMany(c => c.Lines.Concat(c.Ticks)).Select(l => new KeyValuePair<Pt, Pt>(l.A, l.B)))
            .ToList();
        var labels = laid.Where(c => c.Placed).SelectMany(c => c.Texts).ToList();
        Assert.NotEmpty(labels);
        foreach (var m in marks)
        {
            var box = Square(m);
            var name = m.Mark.View + " mark " + m.Number + " / " + m.Sheet + " at (" + m.Centre.X + ", " + m.Centre.Y + ")";
            Assert.False(lines.Any(l => Schedules.SegmentHitsBox(l.Key, l.Value, box)), name + " crosses a line");
            Assert.False(Schedules.OnWalls(box, walls), name + " sits on the poché");
            Assert.False(openings.Any(o => Schedules.Overlaps(box, new RoomDetect.Box(o.Min(p => p.X), o.Min(p => p.Y), o.Max(p => p.X), o.Max(p => p.Y)), 0)),
                name + " sits on an opening");
            var over = labels.Where(l => Schedules.Overlaps(box, l.Box, 0)).Select(l => l.Text).ToList();
            Assert.True(over.Count == 0, name + " sits on " + string.Join(", ", over));
            Assert.DoesNotContain(marks, o => o != m && Schedules.Overlaps(box, Square(o), 0));
            Assert.True(box.MinX >= d.U0 - Details.BandMm * 20 && box.MaxX <= d.U0 + d.Width + Details.BandMm * 20
                && box.MinY >= d.V0 - Details.BandMm * 20 && box.MaxY <= d.V1 + Details.BandMm * 20, name + " leaves its band");
        }
    }

    [Fact]
    public void NoBubbleOnAnyA50Sheet_TouchesLinesValuesOrAnotherBubble_ForTheWallAndD01()
    {
        var model = DetailFixtures.SmokeGarage();
        var wall = Details.Resolve(new Details.Record { Id = "DET01", Wall = "w01" }, model, DetailFixtures.Tol);
        var door = Details.Resolve(new Details.Record { Id = "DET02", Opening = "o-d01" }, model, DetailFixtures.Tol);
        var sheets = DetailSheet.Plan(DetailSheet.Items(new[] { wall, door }));
        var plans = sheets.SelectMany(s => s.Drawings).Where(p => p.Drawing.View == Details.Plan).ToList();
        Assert.Equal(2, plans.Count);
        var count = 0;
        foreach (var placed in plans)
        {
            AssertBubblesInClearSpace(placed.Drawing, sheets, model, out var marks, out _);
            count += marks.Count;
        }
        Assert.Equal(3, count);
    }

    [Fact]
    public void TheWallPlansSectionMark_HangsBelowItsOverallLine_OnAShortLeader()
    {
        var model = DetailFixtures.SmokeGarage();
        var wall = Details.Resolve(new Details.Record { Id = "DET01", Wall = "w01" }, model, DetailFixtures.Tol);
        var door = Details.Resolve(new Details.Record { Id = "DET02", Opening = "o-d01" }, model, DetailFixtures.Tol);
        var sheets = DetailSheet.Plan(DetailSheet.Items(new[] { wall, door }));
        var d = sheets[0].Drawings.Single().Drawing;
        AssertBubblesInClearSpace(d, sheets, model, out var marks, out var laid);
        var m = Assert.Single(marks);
        Assert.Equal("1 A-50-002", m.Number + " " + m.Sheet);
        Assert.True(m.Bubble.Radius > DetailCallout.MarkMm / 2, "grown");
        var overall = laid.Single(c => c.Kind == "outer_overall");
        var line = overall.Lines.OrderByDescending(l => Math.Abs(l.B.X - l.A.X)).First();
        var beyond = (m.Centre.Y - line.A.Y) * overall.Out.Y;
        Assert.True(beyond >= m.HalfY * 20, "the bubble clears the overall line on its far side: " + beyond / 20 + " mm");
        var lead = Math.Sqrt((m.Centre.X - m.At.X) * (m.Centre.X - m.At.X) + (m.Centre.Y - m.At.Y) * (m.Centre.Y - m.At.Y)) / 20;
        Assert.True(lead <= 16, "a short leader: " + lead + " mm");
    }

    static void AssertClearOfValues(List<DetailCallout.PlacedMark> marks, List<PlanDims.Chain> laid, Details.Drawing d)
    {
        var labels = laid.Where(c => c.Placed).SelectMany(c => c.Texts).ToList();
        Assert.NotEmpty(labels);
        foreach (var m in marks)
        {
            var box = Square(m);
            var over = labels.Where(l => Schedules.Overlaps(box, l.Box, 0)).Select(l => l.Text).ToList();
            Assert.True(over.Count == 0, m.Mark.View + " mark sits on " + string.Join(", ", over));
            Assert.True(box.MinX >= d.U0 - Details.BandMm * 20 && box.MaxX <= d.U0 + d.Width + Details.BandMm * 20
                && box.MinY >= d.V0 - Details.BandMm * 20 && box.MaxY <= d.V1 + Details.BandMm * 20, m.Mark.View + " mark leaves its band");
        }
    }

    [Fact]
    public void TheDoorPlansMarks_ClearIts900s_OnALeader()
    {
        var model = DetailFixtures.Garage();
        var d = Details.Frame(DetailFixtures.Facts(model, opening: "o-door"), Details.Plan);
        var marks = Marks(d, model, out var laid);
        Assert.Equal(2, marks.Count);
        AssertClearOfValues(marks, laid, d);
        Assert.All(marks, m => Assert.True(m.Moved, m.Mark.View + " stayed on its point, on the 900"));
    }

    [Fact]
    public void TheSmokeWallPlansSectionMark_ClearsItsValues()
    {
        var model = DetailFixtures.SmokeGarage();
        var d = Details.Frame(DetailFixtures.Facts(model, wall: "w01"), Details.Plan);
        var marks = Marks(d, model, out var laid);
        Assert.Single(marks);
        AssertClearOfValues(marks, laid, d);
    }

    [Fact]
    public void AMarkWithNothingOnIt_StaysOnItsPoint()
    {
        var model = DetailFixtures.Garage();
        var d = Details.Frame(DetailFixtures.Facts(model, opening: "o-door"), Details.Plan);
        var marks = DetailCallout.PlaceMarks(d, SheetsOf(d), new Pt(0, 0), 20, new List<PlanDims.Obstacle>(), Poche(d, model));
        Assert.All(marks, m => Assert.False(m.Moved));
        Assert.Equal(d.Marks.Select(m => m.At), marks.Select(m => m.Centre));
    }

    [Fact]
    public void TheDoorPlansMarks_ReadTheirSheet_InBubblesTheirTextsFit()
    {
        var model = DetailFixtures.Garage();
        var d = Details.Frame(DetailFixtures.Facts(model, opening: "o-door"), Details.Plan);
        var marks = Marks(d, model, out _);
        Assert.Equal(new[] { "2 A-50-001", "3 A-50-001" }, marks.Select(m => m.Number + " " + m.Sheet));
        Assert.All(marks, m => Assert.True(m.Bubble.Fits, m.Bubble.SheetWidth + " in " + m.Bubble.SheetRoom));
        Assert.All(marks, m => Assert.True(m.Bubble.Radius > DetailCallout.MarkMm / 2, "A-50-001 is wider than a 9 mm mark holds"));
        Assert.All(marks, m => Assert.Equal(m.Bubble.Radius + DetailCallout.MarkArrowMm * Math.Abs(m.Mark.Look.X), m.HalfX, 9));
        Assert.All(marks, m => Assert.Equal(m.Bubble.Radius + DetailCallout.MarkArrowMm * Math.Abs(m.Mark.Look.Y), m.HalfY, 9));
    }

    [Fact]
    public void TheDetailBake_PlacesItsMarksAfterItsDimensions_AgainstEveryStroke()
    {
        var source = File.ReadAllText(Path.Combine(FunctionsDir(), "DetailBake.cs"));
        var dims = source.IndexOf("BakeFixedChains(doc, layer, chains, scale, baseProfile.Silhouette, taken, walls,", StringComparison.Ordinal);
        var marks = source.IndexOf("DetailCallout.PlaceMarks(d, plan, shift, scale, taken, walls, PaperTextWidth(doc, scale), stroked)", StringComparison.Ordinal);
        Assert.True(dims > 0 && marks > dims, "the marks are placed against the laid dimensions and the strokes");
        foreach (var role in new[] { "\"beyond\"", "\"cut\"", "\"break_line\"" })
        {
            var stroke = source.IndexOf(role, StringComparison.Ordinal);
            Assert.True(stroke > 0 && source.LastIndexOf("Stroked(", stroke, StringComparison.Ordinal) > source.IndexOf("void Stroked(", StringComparison.Ordinal),
                role + " strokes join what the bubbles keep clear of");
        }
    }

    [Fact]
    public void TheDetailBake_DrawsPlacesAndLeadsEachBubbleAtItsOwnRadius()
    {
        // The PDF and the DWG both take this circle: the DWG flattens the same strokes and writes the texts at their paper height.
        var source = File.ReadAllText(Path.Combine(FunctionsDir(), "DetailBake.cs"));
        Assert.DoesNotMatch(@"(MarkMm|CalloutMm) / 2", source);
        Assert.Contains("new Circle(centre, bubble.Radius * scale)", source);
        Assert.Contains("LeaderStart(centre, at, bubble.Radius, scale)", source);
        Assert.Contains("LeaderStart(placed.Centre, placed.At, placed.Bubble.Radius, scale)", source);
        Assert.Contains("var r = bubble.Radius * scale;", source);
    }

    static string FunctionsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Functions");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("plugin/Functions above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheLeader_StartsOnTheBubblesEdge()
    {
        var start = DetailCallout.LeaderStart(new Pt(0, 0), new Pt(1000, 0), DetailCallout.CalloutMm / 2, 50);
        Assert.Equal(300, start.X, 6);
        Assert.Equal(0, start.Y, 6);
    }
}
