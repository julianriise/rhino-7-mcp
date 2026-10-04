using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
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
    public void ACenterlineSlide_StaysOnTheCentreline()
    {
        // South wall, segment on y=100, inward +Y, thickness 200. Five hundred
        // millimetres from x=2600 must not add the half-thickness (that is
        // sqrt(500²+100²) = 509.9, the opening on the face).
        SoftParamPlan.FootCenter(
            0, 100, 1, 0, 0, 1, 8000, 3100.0 / 8000.0, 200, true, out var x, out var y);
        Assert.Equal(3100, x, 3);
        Assert.Equal(100, y, 3);
        var shift = Math.Sqrt(Math.Pow(x - 2600, 2) + Math.Pow(y - 100, 2));
        Assert.Equal(500, shift, 3);
    }

    [Fact]
    public void AFaceSegment_LandsOnTheCentreline()
    {
        SoftParamPlan.FootCenter(0, 0, 1, 0, 0, 1, 8000, 0.5, 200, false, out var x, out var y);
        Assert.Equal(4000, x, 3);
        Assert.Equal(100, y, 3);
    }

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
    public void RemovalLine_NamesTheKind_AndNoWallId()
    {
        Assert.Equal("Removed 2 windows.", SoftParamPlan.RemovalLine(2, 0, 1));
        Assert.Equal("Removed 1 door.", SoftParamPlan.RemovalLine(0, 1, 1));
        Assert.Equal("Removed 3 openings from 2 walls.", SoftParamPlan.RemovalLine(2, 1, 2));
        Assert.Equal("Removed 0 openings.", SoftParamPlan.RemovalLine(0, 0, 0));
    }

    [Fact]
    public void RemovalLine_NamesAMarkedOpening_AndKeepsTheCountWhenItHasNone()
    {
        var door = new SoftParamPlan.RemovedName("door", "D02");
        var window = new SoftParamPlan.RemovedName("window", "V01");
        var english = new SoftParamPlan.RemovedName("window", "W01");
        Assert.Equal("Removed Door D02.", SoftParamPlan.RemovalLine(new[] { door }, 1));
        Assert.Equal("Removed Window V01.", SoftParamPlan.RemovalLine(new[] { window }, 1));
        Assert.Equal("Removed Window W01.", SoftParamPlan.RemovalLine(new[] { english }, 1));
        Assert.Equal("Removed Door D02 and Window V01.", SoftParamPlan.RemovalLine(new[] { door, window }, 1));
        Assert.Equal(
            "Removed Door D02, Window V01 and Window W01.",
            SoftParamPlan.RemovalLine(new[] { door, window, english }, 2));

        // Before Print there is no forsk:mark. The count line stays, and a marker
        // name or a Rhino id is not a label.
        Assert.Equal("Removed 2 windows.", SoftParamPlan.RemovalLine(new[]
        {
            new SoftParamPlan.RemovedName("window", ""),
            new SoftParamPlan.RemovedName("window", null)
        }, 1));
        Assert.Equal("Removed 1 door.", SoftParamPlan.RemovalLine(
            new[] { new SoftParamPlan.RemovedName("door", "door-02") }, 1));
        Assert.Equal("Removed 1 door.", SoftParamPlan.RemovalLine(
            new[] { new SoftParamPlan.RemovedName("door", "a1b2c3d4-e5f6-7890-abcd-ef1234567890") }, 1));
        Assert.DoesNotContain("a1b2c3d4", SoftParamPlan.RemovalLine(
            new[] { new SoftParamPlan.RemovedName("door", "a1b2c3d4-e5f6-7890-abcd-ef1234567890") }, 1));
    }

    [Fact]
    public void ADeleteReceipt_NamesTheDoor_AndNotTheMarkerId()
    {
        var message = SoftParamPlan.RemovalLine(new[] { new SoftParamPlan.RemovedName("door", "D02") }, 1);
        var envelope = new JObject
        {
            ["status"] = "success",
            ["result"] = new JObject
            {
                ["message"] = message,
                ["deleted_marker_id"] = "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
                ["host_id"] = "b2c3d4e5-f6a7-8901-bcde-f12345678901",
                ["ok"] = true
            }
        };
        var receipt = ForskReceipt.From("delete_opening", envelope);
        Assert.True(receipt.Ok);
        Assert.Equal("D02", receipt.Subject);
        Assert.Contains("Door D02", receipt.Text);
        Assert.DoesNotContain("a1b2c3d4", receipt.Text);

        var window = ForskReceipt.From("delete_opening", new JObject
        {
            ["status"] = "success",
            ["result"] = new JObject
            {
                ["message"] = SoftParamPlan.RemovalLine(new[] { new SoftParamPlan.RemovedName("window", "W01") }, 1)
            }
        });
        Assert.Equal("W01", window.Subject);
        Assert.Contains("Window W01", window.Text);
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
