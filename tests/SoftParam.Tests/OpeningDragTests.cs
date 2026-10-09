using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// FS-Z383WAWP (Julian, 2026-10-09): three doors dragged with Rhino's Drag sat
/// right in 3D and wrong in plan, because the plan symbol is drawn from the
/// marker the drag left behind. The marker now follows its block.
/// </summary>
public class OpeningDragTests
{
    [Fact]
    public void TwoDragsOfOneDoor_MoveItsMarkerByTheirSum()
    {
        var drag = new OpeningDrag();
        drag.Moved("m1", 0, 0, 300, 0);
        drag.Moved("m1", 300, 0, 450, 20);
        var moves = drag.Take();
        Assert.Single(moves);
        Assert.Equal(("m1", 450.0, 20.0), moves[0]);
        Assert.False(drag.Pending);
        Assert.Empty(drag.Take());
    }

    [Fact]
    public void ADragBackToTheStart_OrAClick_MovesNothing()
    {
        var drag = new OpeningDrag();
        drag.Moved("m1", 0, 0, 300, 0);
        drag.Moved("m1", 300, 0, 0, 0);
        drag.Moved("m2", 10, 10, 10.4, 10);
        Assert.True(drag.Pending);
        Assert.Empty(drag.Take());
    }

    [Fact]
    public void ADoorMovedTogetherWithItsMarker_IsNotMovedTwice()
    {
        var drag = new OpeningDrag();
        drag.Moved("m1", 0, 0, 300, 0);
        drag.MarkerMoved("m1");
        Assert.Empty(drag.Take());
        drag.Moved("m1", 0, 0, 300, 0);
        Assert.Single(drag.Take());
    }

    [Fact]
    public void ABlockWithNoMarker_IsNotTracked()
    {
        var drag = new OpeningDrag();
        drag.Moved(null, 0, 0, 300, 0);
        Assert.False(drag.Pending);
    }

    [Fact]
    public void ASlideOntoAnotherSegmentOfTheWall_IsAMove()
    {
        var segs = new[]
        {
            new SoftParamPlan.Seg(0, 0, 4000, 0, true),
            new SoftParamPlan.Seg(4000, 0, 4000, 3000, true)
        };
        // The door sat at t 0.5 of the first segment; it was dragged to t 0.5 of the second.
        Assert.True(SoftParamPlan.TrySlideTo(segs, 0, 0.5, 1, 0.5, 900, 100, 0.01, out var slide));
        Assert.Equal(1, slide.Index);
        Assert.True(slide.Moved);
        Assert.True(SoftParamPlan.TrySlideTo(segs, 0, 0.5, 0, 0.5, 900, 100, 0.01, out slide));
        Assert.False(slide.Moved);
    }
}
