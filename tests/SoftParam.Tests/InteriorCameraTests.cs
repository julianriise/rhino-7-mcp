using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// Jump inside (Julian, 2026-10-07): a level, straight-on interior shot. The
/// camera stands in the middle of the room's back wall, just inside it, and
/// looks the chosen way (north by default) across the room, square to the
/// room's own walls. Doors play no part.
/// </summary>
public class InteriorCameraTests
{
    static readonly Pt[] Room = { new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 5000), new Pt(0, 5000) };

    static Pt Turn(Pt p, double degrees)
    {
        var a = degrees * Math.PI / 180;
        return new Pt(p.X * Math.Cos(a) - p.Y * Math.Sin(a), p.X * Math.Sin(a) + p.Y * Math.Cos(a));
    }

    [Fact]
    public void North_StandsAtTheMiddleOfTheSouthWall_AndLooksAtTheNorthWall()
    {
        var shot = InteriorCamera.For(Room, "north")!;
        Assert.Equal(2000, shot.Eye.X, 6);
        Assert.Equal(InteriorCamera.StepInMm, shot.Eye.Y, 6);
        Assert.Equal(2000, shot.Target.X, 6);
        Assert.Equal(5000, shot.Target.Y, 6);
        Assert.Equal("looking north", shot.From);
    }

    [Theory]
    [InlineData("east", 300, 2500, 4000, 2500)]
    [InlineData("south", 2000, 4700, 2000, 0)]
    [InlineData("west", 3700, 2500, 0, 2500)]
    public void EachDirection_LooksAcrossTheRoomFromTheOppositeWall(string direction, double ex, double ey, double tx, double ty)
    {
        var shot = InteriorCamera.For(Room, direction)!;
        Assert.Equal((ex, ey, tx, ty), (Math.Round(shot.Eye.X), Math.Round(shot.Eye.Y), Math.Round(shot.Target.X), Math.Round(shot.Target.Y)));
    }

    [Fact]
    public void NoDirection_IsNorth_AndTheChoicesAreTheFourWays()
    {
        Assert.Equal(InteriorCamera.For(Room, "north")!.Target.Y, InteriorCamera.For(Room, null)!.Target.Y);
        Assert.Equal(new[] { "north", "east", "south", "west" }, InteriorCamera.Directions);
        Assert.Null(InteriorCamera.For(new Pt[0], "north"));
    }

    [Fact]
    public void ATurnedRoom_IsShotSquareToItsOwnWalls()
    {
        var turned = Room.Select(p => Turn(p, 10)).ToArray();
        var shot = InteriorCamera.For(turned, "north")!;
        var dx = shot.Target.X - shot.Eye.X;
        var dy = shot.Target.Y - shot.Eye.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        // North snaps to the room's own axis nearest north: 10° off true north.
        Assert.Equal(-Math.Sin(10 * Math.PI / 180), dx / length, 6);
        Assert.Equal(Math.Cos(10 * Math.PI / 180), dy / length, 6);
        Assert.Equal(5000 - InteriorCamera.StepInMm, length, 3);
    }

    [Fact]
    public void AnLShapedRoom_StandsInsideIt()
    {
        var l = new[] { new Pt(0, 0), new Pt(6000, 0), new Pt(6000, 2000), new Pt(2000, 2000), new Pt(2000, 6000), new Pt(0, 6000) };
        foreach (var direction in InteriorCamera.Directions)
        {
            var shot = InteriorCamera.For(l, direction)!;
            Assert.True(RoomDetect.Contains(l, shot.Eye), direction);
        }
    }

    [Fact]
    public void JumpInside_AsksWhichWay_NorthFirst()
    {
        var facts = Docs.Facts("house, room selected");
        var card = ForskCards.For("room.inside", facts)!;
        Assert.Equal(new[] { "north", "east", "south", "west", "cancel" }, card.Pills.Select(p => p.Id));
        Assert.True(card.Pills[0].Primary);
        Assert.Equal("Look north", card.Pills[0].Label);
    }
}
