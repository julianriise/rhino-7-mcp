using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// An opening's footprint is a rectangle on its wall. A 45° wall's door is a
/// turned rectangle: its width is the side along the wall, not the world box.
/// </summary>
public class OpeningFootTests
{
    static OpeningFoot.Edge[] Outline(params double[] xy)
    {
        var n = xy.Length / 2;
        var edges = new OpeningFoot.Edge[n];
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            edges[i] = new OpeningFoot.Edge(xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1]);
        }
        return edges;
    }

    [Fact]
    public void A45DegreeRectangle_IsItsWidthAndDepthOnTheWall()
    {
        // 1000 x 300 centred on (5000, 5000), turned 45°.
        var h = Math.Sqrt(0.5);
        double ux = h, uy = h, vx = -h, vy = h;
        double Cx(double a, double c) => 5000 + a * ux + c * vx;
        double Cy(double a, double c) => 5000 + a * uy + c * vy;
        var edges = Outline(
            Cx(-500, -150), Cy(-500, -150),
            Cx(500, -150), Cy(500, -150),
            Cx(500, 150), Cy(500, 150),
            Cx(-500, 150), Cy(-500, 150));

        Assert.True(OpeningFoot.TryFromEdges(edges, out var foot));
        Assert.Equal(1000, foot.Width, 6);
        Assert.Equal(300, foot.Depth, 6);
        Assert.Equal(0.7071, foot.DirX, 4);
        Assert.Equal(0.7071, foot.DirY, 4);
        Assert.Equal(5000, foot.X, 6);
        Assert.Equal(5000, foot.Y, 6);
    }

    [Fact]
    public void A45DegreeMarker_ReachesItsDiagonalExtentNotItsWidth()
    {
        // The world box of that rectangle is 919.2 on X and Y: the width read
        // off it was 919 for a 1000 mm door.
        var foot = OpeningFoot.Along(5000, 5000, 1, 1, 1000, 300);
        foot.MarkerHalves(50, out var along, out var across);
        foot.WorldBox(along, across, out var minX, out var minY, out var maxX, out var maxY);
        Assert.Equal(919.2388, maxX - minX, 3);
        Assert.Equal(919.2388, maxY - minY, 3);
    }

    [Fact]
    public void AnAxisRectangle_IsItsWorldBoxExactly()
    {
        // Walked from the east end: the direction keeps the walk, the numbers
        // are the bounding box's to the last bit.
        var edges = Outline(3000, 100, 2100, 100, 2100, 300, 3000, 300);
        Assert.True(OpeningFoot.TryFromEdges(edges, out var foot));
        Assert.Equal(-1.0, foot.DirX);
        Assert.Equal(0.0, foot.DirY);
        Assert.Equal(900.0, foot.Width);
        Assert.Equal(200.0, foot.Depth);
        Assert.Equal(2550.0, foot.X);
        Assert.Equal(200.0, foot.Y);

        var canonical = foot.Canonical();
        Assert.Equal(1.0, canonical.DirX);
        Assert.Equal(900.0, canonical.Width);
    }

    [Fact]
    public void AWallAlongY_RunsAlongY()
    {
        var edges = Outline(100, 4000, 300, 4000, 300, 4900, 100, 4900);
        Assert.True(OpeningFoot.TryFromEdges(edges, out var foot));
        Assert.Equal(0.0, foot.DirX);
        Assert.Equal(1.0, foot.DirY);
        Assert.Equal(900.0, foot.Width);
        Assert.Equal(200.0, foot.Depth);
        Assert.Equal(200.0, foot.X);
        Assert.Equal(4450.0, foot.Y);
    }

    [Theory]
    [InlineData(0, 0, 900, 200, 1, 0, 900, 200)]
    [InlineData(0, 0, 200, 900, 0, 1, 900, 200)]
    [InlineData(0, 0, 500, 500, 1, 0, 500, 500)]
    public void ABox_TakesItsLongerSideAsTheWidth(
        double minX, double minY, double maxX, double maxY,
        double dirX, double dirY, double width, double depth)
    {
        var foot = OpeningFoot.FromBox(minX, minY, maxX, maxY);
        Assert.Equal(dirX, foot.DirX);
        Assert.Equal(dirY, foot.DirY);
        Assert.Equal(width, foot.Width);
        Assert.Equal(depth, foot.Depth);
    }

    [Theory]
    [InlineData(-1, -1, 0.7071, 0.7071)]
    [InlineData(-1, 1, 0.7071, -0.7071)]
    [InlineData(0, -1, 0, 1)]
    [InlineData(-3, 0, 1, 0)]
    public void ARun_PointsPlusXFirst(double tx, double ty, double dirX, double dirY)
    {
        var foot = OpeningFoot.Along(0, 0, tx, ty, 900, 1);
        Assert.Equal(dirX, foot.DirX, 4);
        Assert.Equal(dirY, foot.DirY, 4);
    }

    [Fact]
    public void ThePlacedDoor_CutsWithPadAndDepthAndMarksThin()
    {
        var foot = OpeningFoot.Along(2550, 100, 1, 0, 900, 1);
        foot.CutterHalves(50, 250, out var cutAlong, out var cutAcross);
        Assert.Equal(500.0, cutAlong);
        Assert.Equal(125.0, cutAcross);
        foot.MarkerHalves(50, out var markAlong, out var markAcross);
        Assert.Equal(450.0, markAlong);
        Assert.Equal(25.0, markAcross);
    }

    [Fact]
    public void EdgesOfAMillimetreOrLess_AreNoFootprint()
    {
        Assert.False(OpeningFoot.TryFromEdges(Outline(0, 0, 0.6, 0, 0.6, 0.5), out _));
        Assert.False(OpeningFoot.TryFromEdges(new OpeningFoot.Edge[0], out _));
    }
}
