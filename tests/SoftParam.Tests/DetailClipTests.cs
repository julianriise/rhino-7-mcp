using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// D2 crop in 2D: the south wall plan detail's crop cuts the joined end
/// walls' poché at v = 500, which becomes a break line, and the 2D clip
/// keeps a line that ran past the crop inside it. Also the source check that
/// the detail bake trims every solid by the crop planes.
/// </summary>
public class DetailClipTests
{
    static readonly DetailClip.Rect SouthCrop = new(-300, -300, 8300, 500);

    [Fact]
    public void AJoinedEndWall_IsClippedToTheCrop_AndBreaksOnIt()
    {
        var west = new List<Pt> { new(0, 0), new(200, 0), new(200, 4000), new(0, 4000) };
        var ring = DetailClip.Ring(west, SouthCrop);
        Assert.Equal(4, ring.Count);
        Assert.Equal(500, ring.Max(p => p.Y), 6);
        var edge = Assert.Single(DetailClip.CropEdges(ring, SouthCrop, 0.5));
        Assert.Equal(500, edge.Key.Y, 6);
        Assert.Equal(200, Math.Abs(edge.Value.X - edge.Key.X), 6);
    }

    [Fact]
    public void ARingOutsideTheCrop_IsGone()
    {
        Assert.Empty(DetailClip.Ring(new List<Pt> { new(9000, 0), new(9200, 0), new(9200, 200) }, SouthCrop));
    }

    [Fact]
    public void ALine_IsClippedToTheCrop_AndALineOnItIsNotACut()
    {
        Assert.True(DetailClip.Segment(new Pt(-1000, 100), new Pt(9000, 100), SouthCrop, out var a, out var b));
        Assert.Equal(-300, a.X, 6);
        Assert.Equal(8300, b.X, 6);
        Assert.False(DetailClip.Segment(new Pt(-1000, 900), new Pt(9000, 900), SouthCrop, out _, out _));
        Assert.True(DetailClip.OnCrop(new Pt(0, 500), new Pt(200, 500), SouthCrop, 0.5));
        Assert.False(DetailClip.OnCrop(new Pt(0, 200), new Pt(200, 200), SouthCrop, 0.5));
    }

    [Fact]
    public void ABreakLine_RunsPastTheWallAtEachEnd_WithOneZigzag()
    {
        var line = DetailClip.BreakLine(new Pt(0, 500), new Pt(200, 500), 20);
        Assert.Equal(6, line.Count);
        Assert.Equal(-40, line[0].X, 6);
        Assert.Equal(240, line[^1].X, 6);
        Assert.Equal(20, line.Max(p => p.Y) - 500, 6);
    }

    static double Length(List<Pt> run) =>
        run.Zip(run.Skip(1), (a, b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y))).Sum();

    [Fact]
    public void AClippedRing_CutsAlongItsFaces_NotAlongTheCrop()
    {
        var ring = DetailClip.Ring(new List<Pt> { new(0, 0), new(200, 0), new(200, 4000), new(0, 4000) }, SouthCrop);
        var run = Assert.Single(DetailClip.CutRuns(ring, SouthCrop, 0.5));
        Assert.Equal(1200, Length(run), 6);
        Assert.DoesNotContain(run.Zip(run.Skip(1)), e => DetailClip.OnCrop(e.First, e.Second, SouthCrop, 0.5));
    }

    [Fact]
    public void ARingCutAtBothEnds_CutsAlongEachFace()
    {
        var ring = DetailClip.Ring(new List<Pt> { new(-1000, 0), new(9000, 0), new(9000, 200), new(-1000, 200) }, SouthCrop);
        var runs = DetailClip.CutRuns(ring, SouthCrop, 0.5);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(8600, Length(r), 6));
    }

    [Fact]
    public void ARingInsideTheCrop_CutsAllRound()
    {
        var ring = new List<Pt> { new(1000, 0), new(1200, 0), new(1200, 200), new(1000, 200) };
        var run = Assert.Single(DetailClip.CutRuns(ring, SouthCrop, 0.5));
        Assert.Equal(run[0], run[^1]);
        Assert.Equal(800, Length(run), 6);
    }

    [Fact]
    public void TheDetailBake_StrokesThePocheAsCut_AndDropsTheLinesOnIt()
    {
        // The bake trims the solids itself, so no HLD segment is a section cut: the poché outline is.
        var source = File.ReadAllText(Path.Combine(FunctionsDir(), "DetailBake.cs"));
        Assert.DoesNotContain("IsSectionCut", source);
        Assert.Contains("DetailClip.CutRuns(ring, part.Rect,", source);
        Assert.Matches(@"PenCut, scale, false, pattern, tol,\s*""cut""", source);
        Assert.Contains("Sections.IsCut(", source);
    }

    [Fact]
    public void TheDetailBake_TrimsBySectionSideForTheCropPlanes()
    {
        var source = File.ReadAllText(Path.Combine(FunctionsDir(), "DetailBake.cs"));
        Assert.Contains("foreach (var plane in planes)", source);
        Assert.Contains("KeepSectionSide(piece, hldPlane, tol)", source);
        Assert.Contains("planes.Add(new Plane(At(d.U0, d.V0), x));", source);
        Assert.Contains("planes.Add(new Plane(At(d.U0, d.V1), -y));", source);
        Assert.Contains("PrintProfiles.AtScale(baseProfile, scale)", source);
        var make = File.ReadAllText(Path.Combine(FunctionsDir(), "Make2dView.cs"));
        Assert.Contains("return BakeDetailSheet(doc, detailScale, detailSheet, includeExisting);", make);
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
}
