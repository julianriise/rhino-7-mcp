using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Band thickness is the gap between the outer loop and the inner loop.
/// A single wall line still uses its short edges. A loop with no measurable
/// gap names the fallback instead of storing it quietly.
/// </summary>
public class ThicknessTests
{
    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(250)]
    public void RectangularBand_ReadsTheGap(double gap)
    {
        var read = SoftParamPlan.ReadThickness(
            Rect(0, 0, 6000, 4000),
            Rect(gap, gap, 6000 - gap, 4000 - gap),
            true,
            250);

        Assert.True(read.Measured);
        Assert.Equal(gap, read.Millimetres);
        Assert.Equal("", read.Receipt);
    }

    [Fact]
    public void OfficePlan_Keeps200()
    {
        var inner = new List<SoftParamPlan.Seg>();
        inner.AddRange(Rect(200, 200, 3900, 5800));
        inner.AddRange(Rect(4100, 200, 7800, 5800));
        var read = SoftParamPlan.ReadThickness(Rect(0, 0, 8000, 6000), inner, true, 250);

        Assert.True(read.Measured);
        Assert.Equal(200, read.Millimetres);
        Assert.Equal("", read.Receipt);
    }

    [Fact]
    public void ClosedOutline_WithNoInnerLoop_NamesTheFallback()
    {
        var read = SoftParamPlan.ReadThickness(Rect(0, 0, 8000, 6000), null, true, 250);

        Assert.False(read.Measured);
        Assert.Equal(250, read.Millimetres);
        Assert.Equal("Could not measure wall thickness. Used 250 mm.", read.Receipt);
    }

    [Fact]
    public void BandWithNoParallelInner_DoesNotUseTheEdgeHeuristic()
    {
        // Two 200 mm jogs would be the old answer. The hole is not parallel, so
        // the band has no gap to read.
        var outer = Loop(
            0, 0, 5000, 0, 5000, 200, 8000, 200,
            8000, 3800, 7800, 3800, 7800, 4000, 0, 4000);
        var inner = Loop(1000, 1000, 1800, 1400, 1200, 2000);
        var read = SoftParamPlan.ReadThickness(outer, inner, true, 250);

        Assert.False(read.Measured);
        Assert.Equal("Could not measure wall thickness. Used 250 mm.", read.Receipt);
    }

    [Fact]
    public void ThinWallLine_UsesTheShortEdges()
    {
        var read = SoftParamPlan.ReadThickness(Rect(0, 0, 9000, 180), null, true, 250);

        Assert.True(read.Measured);
        Assert.Equal(180, read.Millimetres);
        Assert.Equal("", read.Receipt);
    }

    [Fact]
    public void OpenChain_UsesTheShortEdges()
    {
        var open = new List<SoftParamPlan.Seg>
        {
            new SoftParamPlan.Seg(0, 0, 220, 0, true),
            new SoftParamPlan.Seg(220, 0, 220, 4000, true),
            new SoftParamPlan.Seg(220, 4000, 440, 4000, true)
        };
        var read = SoftParamPlan.ReadThickness(open, null, false, 250);

        Assert.True(read.Measured);
        Assert.Equal(220, read.Millimetres);
    }

    private static List<SoftParamPlan.Seg> Rect(double x0, double y0, double x1, double y1)
    {
        return Loop(x0, y0, x1, y0, x1, y1, x0, y1);
    }

    private static List<SoftParamPlan.Seg> Loop(params double[] xy)
    {
        var segs = new List<SoftParamPlan.Seg>();
        var n = xy.Length / 2;
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            segs.Add(new SoftParamPlan.Seg(xy[i * 2], xy[i * 2 + 1], xy[j * 2], xy[j * 2 + 1], true));
        }
        return segs;
    }
}
