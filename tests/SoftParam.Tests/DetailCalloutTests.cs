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

    [Fact]
    public void TheLeader_StartsOnTheBubblesEdge()
    {
        var start = DetailCallout.LeaderStart(new Pt(0, 0), new Pt(1000, 0), DetailCallout.CalloutMm / 2, 50);
        Assert.Equal(300, start.X, 6);
        Assert.Equal(0, start.Y, 6);
    }
}
