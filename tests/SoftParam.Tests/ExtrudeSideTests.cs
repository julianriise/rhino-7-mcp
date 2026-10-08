using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// A slab extrudes down from its top and a wall up from its base. On the
/// sample house after Undo and Redo, a wall move rebuilt the floor at 0..400
/// instead of -400..0: Extrusion.Create went the other way.
/// </summary>
public class ExtrudeSideTests
{
    [Fact]
    public void FloorExtrudedUpMovesDownToItsTop()
    {
        Assert.Equal(-400, ExtrudeSide.Shift(0, -400, 0, 400, 1.0), 6);
    }

    [Fact]
    public void FloorOnTheRightSideStays()
    {
        Assert.Equal(0, ExtrudeSide.Shift(0, -400, -400, 0, 1.0));
    }

    [Fact]
    public void WallExtrudedDownMovesUpToItsBase()
    {
        Assert.Equal(2700, ExtrudeSide.Shift(0, 2700, -2700, 0, 1.0), 6);
        Assert.Equal(0, ExtrudeSide.Shift(0, 2700, 0, 2700, 1.0));
    }

    [Fact]
    public void RoofAtItsTopMovesDownWhenItWentUp()
    {
        Assert.Equal(-250, ExtrudeSide.Shift(3000, -250, 3000, 3250, 1.0), 6);
    }

    [Fact]
    public void RoundingInsideTheToleranceStays()
    {
        Assert.Equal(0, ExtrudeSide.Shift(0, -400, -400.4, 0.3, 1.0));
    }
}
