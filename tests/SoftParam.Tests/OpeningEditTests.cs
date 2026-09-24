using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// One opening slides or changes size. A miss does not write a new t or size.
/// The other opening's offset is not an input to that decision.
/// </summary>
public class OpeningEditTests
{
    [Fact]
    public void Slide_FiveHundredMillimetres_LeavesTheSiblingOffset()
    {
        var segs = Garage();
        var sibling = SoftParamPlan.OffsetOf(segs, 1, 0.5);

        Assert.True(SoftParamPlan.TrySlide(
            segs, 0, 0.25, 1200, 500, null, 50, 0.01, out var slide));
        Assert.True(slide.Moved);
        Assert.Equal(0, slide.Index);
        Assert.Equal(0.3125, slide.T, 6);
        Assert.Equal(2500, SoftParamPlan.OffsetOf(segs, slide.Index, slide.T), 3);
        Assert.Equal(sibling, SoftParamPlan.OffsetOf(segs, 1, 0.5), 6);
    }

    [Fact]
    public void Slide_NegativeDelta_MovesTowardTheStart()
    {
        var segs = Garage();
        Assert.True(SoftParamPlan.TrySlide(
            segs, 0, 0.25, 1200, -500, null, 50, 0.01, out var slide));
        Assert.True(slide.Moved);
        Assert.Equal(0.1875, slide.T, 6);
    }

    [Fact]
    public void Slide_ClampsAtTheCorner_AndANoOpStays()
    {
        var segs = Garage();
        Assert.True(SoftParamPlan.TrySlide(
            segs, 0, 0.95, 1200, 1000, null, 50, 0.01, out var towardEnd));
        Assert.True(towardEnd.Moved);
        Assert.Equal(0.91875, towardEnd.T, 6);

        Assert.True(SoftParamPlan.TrySlide(
            segs, 0, towardEnd.T, 1200, 500, null, 50, 0.01, out var again));
        Assert.False(again.Moved);
        Assert.Equal(towardEnd.T, again.T, 6);
    }

    [Fact]
    public void Slide_Miss_LeavesTheRecord()
    {
        var segs = Garage();
        const double t = 0.25;
        var ok = SoftParamPlan.TrySlide(
            segs, 0, t, 8100, 500, null, 50, 0.01, out _);
        Assert.False(ok);
        Assert.False(SoftParamPlan.Fits(segs[0], 8100, 50));
        Assert.Equal(0.25, t);
        Assert.Equal(2000, SoftParamPlan.OffsetOf(segs, 0, t), 3);
    }

    [Fact]
    public void SetSize_KeepsOmittedFields_AndRejectsAMiss()
    {
        Assert.True(SoftParamPlan.TrySetSize(
            1200, 900, 2100, 1400, null, 2200, out var size, out var why));
        Assert.Equal("", why);
        Assert.True(size.Changed);
        Assert.Equal(1400, size.Width);
        Assert.Equal(900, size.Sill);
        Assert.Equal(2200, size.Head);

        Assert.True(SoftParamPlan.TrySetSize(
            1400, 900, 2200, 1400, 900, 2200, out var same, out _));
        Assert.False(same.Changed);

        Assert.False(SoftParamPlan.TrySetSize(
            1200, 900, 2100, null, null, null, out _, out why));
        Assert.Equal("Specify width, sill, or head.", why);

        Assert.False(SoftParamPlan.TrySetSize(
            1200, 900, 2100, 0, null, null, out _, out why));
        Assert.Equal("width must be positive.", why);

        Assert.False(SoftParamPlan.TrySetSize(
            1200, 900, 2100, null, 2200, 1000, out var refused, out why));
        Assert.Equal("head must be greater than sill.", why);
        Assert.Equal(1200, refused.Width);
        Assert.False(refused.Changed);

        // A cutter above the wall is a legal record. The host rebuild refuses
        // it and the command restores the previous size.
        Assert.True(SoftParamPlan.TrySetSize(
            1200, 900, 2100, null, 8000, 9000, out var above, out why));
        Assert.Equal("", why);
        Assert.True(above.Changed);
        Assert.Equal(8000, above.Sill);
        Assert.Equal(9000, above.Head);
    }

    [Fact]
    public void RemovalLine_NamesTheHostAndTheKind()
    {
        Assert.Equal(
            "Removed 2 windows from w01",
            SoftParamPlan.RemovalLine(2, 0, new[] { "w01" }));
        Assert.Equal(
            "Removed 1 door from w01",
            SoftParamPlan.RemovalLine(0, 1, new[] { "w01" }));
        Assert.Equal(
            "Removed 3 openings from w01, w02",
            SoftParamPlan.RemovalLine(2, 1, new[] { "w01", "w01", "w02" }));
        Assert.Equal(
            "Removed 0 openings from the wall",
            SoftParamPlan.RemovalLine(0, 0, null));
    }

    // South wall 8 m, east wall 6 m. The edited opening is on the south run.
    private static List<SoftParamPlan.Seg> Garage()
    {
        return new List<SoftParamPlan.Seg>
        {
            new SoftParamPlan.Seg(0, 0, 8000, 0, true),
            new SoftParamPlan.Seg(8000, 0, 8000, 6000, true)
        };
    }
}
