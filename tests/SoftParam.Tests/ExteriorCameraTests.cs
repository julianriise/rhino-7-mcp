using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Exterior render (Julian, 2026-10-08): the camera stands outside, at eye
/// height and level, and looks straight at the building from the chosen side,
/// far enough back that the facade and the roof fit the frame.
/// </summary>
public class ExteriorCameraTests
{
    // A 10 × 6 m house, 3 m to the eaves with its roof on, on the ground at 0.
    static ExteriorCamera.Shot From(string side) => ExteriorCamera.For(0, 0, 10000, 6000, 0, 3200, side)!;

    [Fact]
    public void FromTheNorth_StandsNorthAndLooksSouthAtTheMiddle()
    {
        var shot = From("north");
        Assert.Equal(5000, shot.EyeX, 6);
        Assert.True(shot.EyeY > 6000);
        Assert.Equal((5000.0, 3000.0), (shot.TargetX, shot.TargetY));
        Assert.Equal(1600, shot.EyeZ, 6);
        // Level: no tilt, so verticals stay vertical.
        Assert.Equal(shot.EyeZ, shot.TargetZ, 6);
        Assert.Equal(("from the north", "north"), (shot.From, shot.Side));
    }

    [Theory]
    [InlineData("east", 1, 0)]
    [InlineData("south", 0, -1)]
    [InlineData("west", -1, 0)]
    public void EachSide_StandsOnThatSide(string side, int sx, int sy)
    {
        var shot = From(side);
        Assert.Equal(Math.Sign(shot.EyeX - 5000), sx);
        Assert.Equal(Math.Sign(shot.EyeY - 3000), sy);
    }

    [Fact]
    public void TheFacade_FitsTheFrameWithAMargin()
    {
        var shot = From("south");
        // The south facade is 10 m wide; a 24 mm lens sees 0.75 of the distance either side.
        var distance = 3000 - shot.EyeY - 3000;
        Assert.True(5000 * ExteriorCamera.Margin <= distance * 0.75 + 1e-6);
        Assert.Equal(5000 / 0.75 * ExteriorCamera.Margin, distance, 3);
    }

    [Fact]
    public void ATallBuilding_StandsBackForItsTop()
    {
        var shot = ExteriorCamera.For(0, 0, 4000, 4000, 0, 12000, "north")!;
        var distance = shot.EyeY - 4000;
        Assert.Equal((12000 - 1600) / 0.5 * ExteriorCamera.Margin, distance, 3);
    }

    [Fact]
    public void NoSide_IsNorth_AnEmptyBox_IsNoShot_AndTheViewIsNamedAfterTheSide()
    {
        Assert.Equal("north", ExteriorCamera.For(0, 0, 1000, 1000, 0, 3000, null)!.Side);
        Assert.Null(ExteriorCamera.For(0, 0, 0, 0, 0, 0, "north"));
        Assert.Equal("Exterior south", ExteriorCamera.ViewName("south"));
    }
}
