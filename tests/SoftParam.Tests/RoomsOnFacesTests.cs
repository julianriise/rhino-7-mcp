using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// An imported room outline is moved onto the walls' inner faces when any
/// corner sits in a wall, however short the overlap. Rounding stays put.
/// </summary>
public class RoomsOnFacesTests
{
    const double Tol = 1.0;

    static List<List<List<Pt>>> RoomWalls()
    {
        // A 4000 x 3000 room inside 200 mm walls: outer 0..4400 x 0..3400.
        var outer = new List<Pt> { new(0, 0), new(4400, 0), new(4400, 3400), new(0, 3400) };
        var hole = new List<Pt> { new(200, 200), new(4200, 200), new(4200, 3200), new(200, 3200) };
        return new() { new() { outer, hole } };
    }

    [Fact]
    public void ACorner30mmIntoAWall_MovesOntoTheFaces()
    {
        // The room's outline as AI detection drew it: one corner 30 mm into the east wall.
        var drawn = new List<Pt> { new(200, 200), new(4230, 200), new(4200, 3200), new(200, 3200) };
        var inside = RoomDetect.InsideWalls(drawn, RoomWalls(), Tol);
        Assert.NotNull(inside);
        Assert.True(RoomDetect.MovesOntoFaces(drawn, inside));
        Assert.All(inside, p => Assert.InRange(p.X, 199, 4201));
    }

    [Fact]
    public void AnOutlineOnTheFaces_WithRoundingOnly_Stays()
    {
        var drawn = new List<Pt> { new(201, 199.5), new(4199, 200.5), new(4200.5, 3201), new(199.5, 3199) };
        var inside = RoomDetect.InsideWalls(drawn, RoomWalls(), Tol);
        // Either no clip (the outline is kept as drawn) or a clip on the same corners.
        Assert.True(inside == null || !RoomDetect.MovesOntoFaces(drawn, inside), string.Join(" ", (inside ?? new List<Pt>()).Select(p => $"({p.X:0.#},{p.Y:0.#})")));
    }

    [Fact]
    public void AnOutlineExactlyOnTheFaces_ClipsToItself_NotToTheWallsOuterLoop()
    {
        var drawn = new List<Pt> { new(200, 200), new(4200, 200), new(4200, 3200), new(200, 3200) };
        var inside = RoomDetect.InsideWalls(drawn, RoomWalls(), Tol);
        Assert.NotNull(inside);
        Assert.Equal(4000 * 3000, System.Math.Abs(RoomDetect.Area(inside!)), 0);
    }
}
