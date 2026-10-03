using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// A wall drag projects onto the run's normal, snaps, and names the distance.
/// </summary>
public class WallDragTests
{
    static WallEdit.Run EastWest() => new WallEdit.Run
    {
        Dir = new Pt(1, 0),
        Normal = new Pt(0, 1),
        Near = 0,
        Far = 200,
        Lo = 0,
        Hi = 4000
    };

    static WallEdit.Run NorthSouth() => new WallEdit.Run
    {
        Dir = new Pt(0, 1),
        Normal = new Pt(1, 0),
        Near = 0,
        Far = 200,
        Lo = 0,
        Hi = 4000
    };

    [Fact]
    public void DiagonalDrag_ProjectsOntoTheNormal()
    {
        Assert.Equal(400, WallDrag.Along(new Pt(0, 0), new Pt(300, 400), new Pt(0, 1)), 6);
        Assert.Equal(300, WallDrag.Along(new Pt(0, 0), new Pt(300, 400), new Pt(1, 0)), 6);
        Assert.Equal(-250, WallDrag.Along(new Pt(1000, 2000), new Pt(1000, 1750), new Pt(0, 1)), 6);
    }

    [Fact]
    public void SidewaysDrag_IsZero()
    {
        Assert.Equal(0, WallDrag.Along(new Pt(0, 0), new Pt(300, 0), new Pt(0, 1)), 6);
        Assert.Equal(0, WallDrag.Along(new Pt(500, 500), new Pt(500, 900), new Pt(1, 0)), 6);
    }

    [Theory]
    [InlineData(14, 10, 10)]
    [InlineData(15, 10, 20)]
    [InlineData(5, 10, 10)]
    [InlineData(-5, 10, -10)]
    [InlineData(-15, 10, -20)]
    [InlineData(24, 50, 0)]
    [InlineData(25, 50, 50)]
    [InlineData(-25, 50, -50)]
    [InlineData(75, 50, 100)]
    [InlineData(-74, 50, -50)]
    [InlineData(140, 100, 100)]
    [InlineData(150, 100, 200)]
    public void Snap_RoundsToTheStep_HalvesAwayFromZero(double raw, double step, double expected)
    {
        Assert.Equal(expected, WallDrag.Snap(raw, step), 6);
    }

    [Theory]
    [InlineData(250, 1, 250)]
    [InlineData(250, -1, -250)]
    [InlineData(250, 0, 250)]
    [InlineData(-250, 1, -250)]
    [InlineData(-250, -1, 250)]
    public void Typed_PositiveFollowsTheMouse_ZeroIsOutward(double number, int sign, double expected)
    {
        Assert.Equal(expected, WallDrag.Typed(number, sign), 6);
    }

    [Fact]
    public void Toward_NamesTheCompass_AndDropsUnderOneStep()
    {
        var ew = EastWest();
        var ns = NorthSouth();
        Assert.Equal(("north", 300.0), WallDrag.Toward(ew, 300));
        Assert.Equal(("south", 300.0), WallDrag.Toward(ew, -300));
        Assert.Equal(("east", 250.0), WallDrag.Toward(ns, 250));
        Assert.Equal(("west", 100.0), WallDrag.Toward(ns, -80, 50));
        Assert.Null(WallDrag.Toward(ew, 4));
        Assert.Null(WallDrag.Toward(ns, -20, 50));
        Assert.Equal("Not moved: the wall was not dragged.", WallDrag.NotMoved(false));
        Assert.Equal("Ikke flyttet: veggen ble ikke dratt.", WallDrag.NotMoved(true));
    }

    [Fact]
    public void Outward_IsTheWallsOwnSide()
    {
        var ew = EastWest();
        var ns = NorthSouth();
        Assert.Equal(1, WallDrag.Outward("north", ew));
        Assert.Equal(-1, WallDrag.Outward("south", ew));
        Assert.Equal(1, WallDrag.Outward("east", ns));
        Assert.Equal(-1, WallDrag.Outward("west", ns));
        Assert.Equal(0, WallDrag.Outward(null, ew));
        Assert.Equal(0, WallDrag.Outward("", ns));
    }

    [Fact]
    public void Dimension_SnapsAndSpeaksOutInOrASign()
    {
        Assert.Equal("300 mm out", WallDrag.Dimension(304, 10, 1, false));
        Assert.Equal("300 mm ut", WallDrag.Dimension(304, 10, 1, true));
        Assert.Equal("350 mm out", WallDrag.Dimension(325, 50, 1, false));
        Assert.Equal("300 mm in", WallDrag.Dimension(-300, 10, 1, false));
        Assert.Equal("300 mm inn", WallDrag.Dimension(-300, 10, 1, true));
        Assert.Equal("300 mm out", WallDrag.Dimension(-300, 10, -1, false));
        Assert.Equal("300 mm in", WallDrag.Dimension(300, 10, -1, false));
        Assert.Equal("+300 mm", WallDrag.Dimension(300, 10, 0, false));
        Assert.Equal("+300 mm", WallDrag.Dimension(300, 10, 0, true));
        Assert.Equal("-300 mm", WallDrag.Dimension(-300, 10, 0, false));
        Assert.Equal("-300 mm", WallDrag.Dimension(-280, 50, 0, true));
        Assert.Equal("0 mm", WallDrag.Dimension(0, 10, 1, false));
        Assert.Equal("0 mm", WallDrag.Dimension(4, 10, 1, true));
        Assert.Equal("0 mm", WallDrag.Dimension(0, 10, 0, false));
        Assert.Equal("200 mm out", WallDrag.Dimension(150, 100, 1, false));
    }

    [Fact]
    public void Measure_RunsFromTheFaceAlongTheNormal()
    {
        var (from, to) = WallDrag.Measure(EastWest(), 300);
        Assert.Equal(2000, from.X, 6);
        Assert.Equal(200, from.Y, 6);
        Assert.Equal(2000, to.X, 6);
        Assert.Equal(500, to.Y, 6);

        (from, to) = WallDrag.Measure(EastWest(), -300);
        Assert.Equal(2000, from.X, 6);
        Assert.Equal(0, from.Y, 6);
        Assert.Equal(2000, to.X, 6);
        Assert.Equal(-300, to.Y, 6);

        (from, to) = WallDrag.Measure(NorthSouth(), 0);
        Assert.Equal(from.X, to.X, 6);
        Assert.Equal(from.Y, to.Y, 6);
    }
}
