using RhinoMCPPlugin.Functions;
using Xunit;
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

    [Fact]
    public void ASheetNumberTooWide_DoesNotFit()
    {
        Assert.False(DetailCallout.Of("A-50-001", DetailCallout.MarkMm, t => t.Length * 1.0).Fits);
        Assert.False(DetailCallout.Of("A-50-0001-long").Fits);
    }

    static Pt Frame(Details.Drawing d, Pt p) => new(p.X * d.X.X + p.Y * d.X.Y, p.X * d.Y.X + p.Y * d.Y.Y);

    /// <summary>The walls' poché in a plan detail, clipped to each stretch it keeps and moved as the bake moves it.</summary>
    static List<List<List<Pt>>> Poche(Details.Drawing d, IfcExport.Model model)
    {
        var keep = d.Breaks?.Keep() ?? new List<KeyValuePair<double, double>> { new(d.U0, d.U1) };
        var walls = new List<List<List<Pt>>>();
        foreach (var wall in model.Walls)
        foreach (var part in keep)
        {
            var ring = DetailClip.Ring(wall.Rings[0].Select(p => Frame(d, p)).ToList(), new DetailClip.Rect(part.Key, d.V0, part.Value, d.V1));
            var du = d.Map(part.Key) - part.Key;
            if (ring.Count >= 3) walls.Add(new List<List<Pt>> { ring.Select(p => new Pt(p.X + du, p.Y)).ToList() });
        }
        return walls;
    }

    static List<DetailCallout.PlacedMark> Marks(Details.Drawing d, IfcExport.Model model, out List<PlanDims.Chain> laid)
    {
        var walls = Poche(d, model);
        var taken = new List<PlanDims.Obstacle>();
        laid = PlanDims.LayoutFixed(DetailDims.PlanChains(d.Facts, d), 20, taken, walls, text => 0.6 * PlanDims.TextMm * text.Length);
        return DetailCallout.PlaceMarks(d, new Pt(0, 0), 20, taken, walls);
    }

    static RoomDetect.Box Square(Pt c) =>
        new(c.X - DetailCallout.MarkHalfMm * 20, c.Y - DetailCallout.MarkHalfMm * 20, c.X + DetailCallout.MarkHalfMm * 20, c.Y + DetailCallout.MarkHalfMm * 20);

    static void AssertClearOfValues(List<DetailCallout.PlacedMark> marks, List<PlanDims.Chain> laid, Details.Drawing d)
    {
        var labels = laid.Where(c => c.Placed).SelectMany(c => c.Texts).ToList();
        Assert.NotEmpty(labels);
        foreach (var m in marks)
        {
            var box = Square(m.Centre);
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
        var marks = DetailCallout.PlaceMarks(d, new Pt(0, 0), 20, new List<PlanDims.Obstacle>(), Poche(d, model));
        Assert.All(marks, m => Assert.False(m.Moved));
        Assert.Equal(d.Marks.Select(m => m.At), marks.Select(m => m.Centre));
    }

    [Fact]
    public void TheDetailBake_PlacesItsMarksAfterItsDimensions()
    {
        var source = File.ReadAllText(Path.Combine(FunctionsDir(), "DetailBake.cs"));
        var dims = source.IndexOf("PlanDims.LayoutFixed(chains, scale, taken, walls, measure)", StringComparison.Ordinal);
        var marks = source.IndexOf("DetailCallout.PlaceMarks(d, shift, scale, taken, walls)", StringComparison.Ordinal);
        Assert.True(dims > 0 && marks > dims, "the marks are placed against the laid dimensions");
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
