using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>Jump inside: the first interior shot stands inside the door at eye height and looks across the room.</summary>
public class InteriorCameraTests
{
    static readonly Pt[] Room = { new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 5000), new Pt(0, 5000) };

    static Furnish.Opening Door(double x, double y) => new Furnish.Opening { Centre = new Pt(x, y), Width = 900, Door = true };

    [Fact]
    public void AtTheDoor_ItStepsInAndLooksAtTheFarWall()
    {
        var shot = InteriorCamera.For(Room, new[] { Door(1000, 0) })!;
        Assert.Equal(1000, shot.Eye.X, 6);
        Assert.Equal(InteriorCamera.StepInMm, shot.Eye.Y, 6);
        Assert.Equal(1000, shot.Target.X, 6);
        Assert.Equal(5000, shot.Target.Y, 6);
        Assert.Equal("from the door", shot.From);
    }

    [Fact]
    public void OfTwoDoors_TheLongerViewWins_AndAWindowIsNoDoor()
    {
        var shot = InteriorCamera.For(Room, new[] { Door(4000, 2500), Door(2000, 5000), new Furnish.Opening { Centre = new Pt(2000, 0), Width = 1200 } })!;
        // From the north door the room is 5 m deep; from the east door 4 m.
        Assert.Equal(2000, shot.Eye.X, 6);
        Assert.Equal(5000 - InteriorCamera.StepInMm, shot.Eye.Y, 6);
        Assert.Equal(0, shot.Target.Y, 6);
    }

    [Fact]
    public void WithNoDoor_ItLooksAlongTheLongestDiagonal()
    {
        var shot = InteriorCamera.For(Room, new Furnish.Opening[0])!;
        Assert.Equal("from the corner", shot.From);
        Assert.True(RoomDetect.Contains(Room, shot.Eye));
        var span = Math.Sqrt(Math.Pow(shot.Target.X - shot.Eye.X, 2) + Math.Pow(shot.Target.Y - shot.Eye.Y, 2));
        Assert.True(span > 6000, span.ToString());
        Assert.Null(InteriorCamera.For(new Pt[0], null));
    }
}
