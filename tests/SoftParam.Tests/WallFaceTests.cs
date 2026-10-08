using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// WF: a click resolves to an end, side or top face from the run's lines;
/// an end changes the length and a side alone changes the thickness, with
/// the walls that meet that face following along their own lines.
/// </summary>
public class WallFaceTests
{
    const double Tol = 1.0;
    const double Top = 2700;

    /// <summary>4000 long, 200 thick, east–west, free at both ends.</summary>
    static List<List<Pt>> Straight() => new() { WallJoinsTests.Rect(0, 0, 4000, 200) };

    /// <summary>An east–west wall and a north–south wall in one record, cornered at the east end.</summary>
    static List<List<Pt>> L() => new()
    {
        new() { new(0, 0), new(4000, 0), new(4000, 3000), new(3800, 3000), new(3800, 200), new(0, 200) }
    };

    /// <summary>An east–west bar with a stem teed into its north face at x 2000.</summary>
    static List<List<Pt>> T() => new()
    {
        new() { new(0, 0), new(4000, 0), new(4000, 200), new(2100, 200), new(2100, 2000), new(1900, 2000), new(1900, 200), new(0, 200) }
    };

    static WallJoins.Graph Graph(List<List<List<Pt>>> records) =>
        WallJoins.Build(records, WallJoins.ClusterOf(records, 0, Tol), Tol);

    static WallFace.Hit Pick(WallJoins.Graph g, double x, double y, double? z = null)
    {
        Assert.True(WallFace.TryPick(g, new Pt(x, y), z, Top, Tol, out var hit, out var why), why);
        return hit;
    }

    static (double MinX, double MinY, double MaxX, double MaxY) Box(List<List<Pt>> rings) =>
        (rings[0].Min(p => p.X), rings[0].Min(p => p.Y), rings[0].Max(p => p.X), rings[0].Max(p => p.Y));

    [Fact]
    public void Straight_EndsSidesAndTop()
    {
        var g = Graph(new() { Straight() });
        var east = Pick(g, 4000, 100);
        Assert.Equal(WallFace.Kind.End, east.Kind);
        Assert.Equal(1, east.End);
        Assert.Equal(-1, Pick(g, 0, 80).End);
        var north = Pick(g, 2000, 150);
        Assert.Equal(WallFace.Kind.Side, north.Kind);
        Assert.Equal(1, north.Side);
        Assert.Equal(1, north.Out.Y, 6);
        Assert.Equal(-1, Pick(g, 2000, 20).Side);
        // Perspective: a point at the wall's height is the top, dragged as its nearer side.
        var top = Pick(g, 2000, 30, Top);
        Assert.Equal(WallFace.Kind.Top, top.Kind);
        Assert.Equal(-1, top.Side);
        // Below the top it is the face the point lies on.
        Assert.Equal(WallFace.Kind.End, Pick(g, 4000, 100, 1200).Kind);
    }

    [Fact]
    public void Straight_TooFar_IsRefused()
    {
        var g = Graph(new() { Straight() });
        Assert.False(WallFace.TryPick(g, new Pt(2000, 900), null, Top, Tol, out _, out var why));
        Assert.Contains("No wall face within 300 mm", why);
    }

    [Fact]
    public void L_CornerEndIsTheOtherWallsSide_FreeEndsAreEnds()
    {
        var g = Graph(new() { L() });
        Assert.Equal(2, g.Runs.Count);
        // The east end of the east–west wall is the north–south wall's outer face.
        var corner = Pick(g, 4000, 100);
        Assert.Equal(WallFace.Kind.Side, corner.Kind);
        Assert.True(Math.Abs(corner.Run.Normal.X) > 0.99);
        var west = Pick(g, 0, 100);
        Assert.Equal(WallFace.Kind.End, west.Kind);
        Assert.True(Math.Abs(west.Run.Dir.X) > 0.99);
        var north = Pick(g, 3900, 3000);
        Assert.Equal(WallFace.Kind.End, north.Kind);
        Assert.True(Math.Abs(north.Run.Dir.Y) > 0.99);
    }

    [Fact]
    public void T_StemEndIsAnEnd_BarFaceIsASide()
    {
        var g = Graph(new() { T() });
        var stem = Pick(g, 2000, 2000);
        Assert.Equal(WallFace.Kind.End, stem.Kind);
        Assert.Equal(1800, stem.Run.Length, 0);
        var bar = Pick(g, 2000, 0);
        Assert.Equal(WallFace.Kind.Side, bar.Kind);
        Assert.Equal(4000, bar.Run.Length, 0);
    }

    [Fact]
    public void Stretch_EndLengthensAndShortens_OtherEndStays()
    {
        var records = new List<List<List<Pt>>> { Straight() };
        var g = Graph(records);
        var east = Pick(g, 4000, 100);
        Assert.True(WallFace.TryStretch(records, g, east, 800, Tol, out var longer, out var why), why);
        Assert.Equal((0.0, 0.0, 4800.0, 200.0), Box(longer.Shape));
        Assert.True(WallFace.TryStretch(records, g, east, -800, Tol, out var shorter, out why), why);
        Assert.Equal((0.0, 0.0, 3200.0, 200.0), Box(shorter.Shape));
        var west = Pick(g, 0, 100);
        Assert.True(WallFace.TryStretch(records, g, west, 500, Tol, out var back, out why), why);
        Assert.Equal((-500.0, 0.0, 4000.0, 200.0), Box(back.Shape));
        Assert.Equal(4800, WallFace.After(east, 800), 6);
    }

    [Fact]
    public void Stretch_TooShort_IsRefused()
    {
        var records = new List<List<List<Pt>>> { Straight() };
        var g = Graph(records);
        Assert.False(WallFace.TryStretch(records, g, Pick(g, 4000, 100), -3850, Tol, out _, out var why));
        Assert.StartsWith("Not shortened: 3850 mm would leave the wall no longer than it is thick", why);
    }

    [Fact]
    public void Stretch_IntoAnotherWall_IsRefused()
    {
        // A free wall pointing at a second wall 500 mm off its east end.
        var records = new List<List<List<Pt>>> { new() { new() { new(0, 0), new(4000, 0), new(4000, 200), new(0, 200) },
                                                          new() { new(4500, -1000), new(4700, -1000), new(4700, 1000), new(4500, 1000) } } };
        var g = WallJoins.Build(records, new List<int> { 0 }, Tol);
        var east = Pick(g, 4000, 100);
        Assert.False(WallFace.TryStretch(records, g, east, 800, Tol, out _, out var why));
        Assert.StartsWith("Not lengthened:", why);
    }

    [Fact]
    public void Stretch_StemOfATee()
    {
        var records = new List<List<List<Pt>>> { T() };
        var g = Graph(records);
        Assert.True(WallFace.TryStretch(records, g, Pick(g, 2000, 2000), 800, Tol, out var moved, out var why), why);
        Assert.Equal(2800, Box(moved.Shape).MaxY, 3);
        var after = WallJoins.Runs(moved.Shape, Tol);
        Assert.Contains(after, r => Math.Abs(r.Length - 2600) < 1 && Math.Abs(r.Dir.Y) > 0.99);
    }

    [Fact]
    public void Stretch_FreeEndOfAnL_CornerStaysPut()
    {
        var records = new List<List<List<Pt>>> { L() };
        var g = Graph(records);
        Assert.True(WallFace.TryStretch(records, g, Pick(g, 3900, 3000), -1000, Tol, out var moved, out var why), why);
        Assert.Equal((0.0, 0.0, 4000.0, 2000.0), Box(moved.Shape));
    }

    [Fact]
    public void Thicken_Garage_NorthWall200To250_OuterFaceStays_CornersFollow()
    {
        var records = new List<List<List<Pt>>> { WallJoinsTests.Garage() };
        var g = Graph(records);
        // The inner face of the north wall.
        var inner = Pick(g, 4000, 3800);
        Assert.Equal(WallFace.Kind.Side, inner.Kind);
        Assert.Equal(-1, inner.Side);
        Assert.True(WallFace.TryThicken(records, g, inner, 250, Tol, out var moved, out var by, out var why), why);
        Assert.Equal(-50, by, 6);
        Assert.Equal(4000, Box(moved.Shape).MaxY, 3);
        Assert.Equal(3750, moved.Shape[1].Max(p => p.Y), 3);
        var north = WallJoins.Runs(moved.Shape, Tol).Single(r => Math.Abs(r.Normal.Y) > 0.99 && r.Far > 3900);
        Assert.Equal(250, north.Thickness, 3);
        Assert.Equal(4000, north.Far, 3);
        // The east and west walls' inner faces got 50 mm shorter: the room is 50 mm shallower.
        Assert.Equal(7600 * 3550, Math.Abs(RoomDetect.Area(moved.Shape[1])), 0);
    }

    [Fact]
    public void Thicken_OuterFace_InnerFaceStays()
    {
        var records = new List<List<List<Pt>>> { Straight() };
        var g = Graph(records);
        var north = Pick(g, 2000, 200);
        Assert.True(WallFace.TryThicken(records, g, north, 300, Tol, out var moved, out var by, out var why), why);
        Assert.Equal(100, by, 6);
        Assert.Equal((0.0, 0.0, 4000.0, 300.0), Box(moved.Shape));
        Assert.True(WallFace.TryThicken(records, g, north, 120, Tol, out var thinner, out _, out why), why);
        Assert.Equal((0.0, 0.0, 4000.0, 120.0), Box(thinner.Shape));
    }

    [Fact]
    public void Thicken_FourRecords_ClusterFollows()
    {
        var records = WallJoinsTests.FourRects();
        var g = Graph(records);
        var inner = Pick(g, 4000, 3800);
        Assert.True(WallFace.TryThicken(records, g, inner, 250, Tol, out var moved, out _, out var why), why);
        Assert.Equal(3750, moved.Shape[1].Max(p => p.Y), 3);
        Assert.True(moved.Records.Count >= 1);
    }

    [Fact]
    public void Thicken_RefusesNoChangeAndTooThick_AndEnds()
    {
        var records = new List<List<List<Pt>>> { Straight() };
        var g = Graph(records);
        var north = Pick(g, 2000, 200);
        Assert.False(WallFace.TryThicken(records, g, north, 200, Tol, out _, out _, out var why));
        Assert.Equal("Not changed: the wall is already 200 mm thick.", why);
        Assert.False(WallFace.TryThicken(records, g, north, 5000, Tol, out _, out _, out why));
        Assert.StartsWith("thickness is above 0", why);
        Assert.False(WallFace.TryThicken(records, g, Pick(g, 4000, 100), 250, Tol, out _, out _, out why));
        Assert.Equal("Only a side face changes the thickness.", why);
        Assert.False(WallFace.TryStretch(records, g, north, 250, Tol, out _, out why));
        Assert.Equal("Only an end face changes the length.", why);
    }

    [Fact]
    public void Shortening_PastAWindow_LeavesItUnheld()
    {
        // WF.5: openings stay put; a window from x 2600 to 3400 is not held once the wall ends at 3000.
        var records = new List<List<List<Pt>>> { Straight() };
        var g = Graph(records);
        var east = Pick(g, 4000, 100);
        var window = new RoomDetect.Box(2600, 90, 3400, 110);
        Assert.True(WallFace.TryStretch(records, g, east, -500, Tol, out var ok, out _));
        Assert.True(WallEdit.Holds(ok.Shape, window));
        Assert.True(WallFace.TryStretch(records, g, east, -1000, Tol, out var past, out _));
        Assert.False(WallEdit.Holds(past.Shape, window));
    }

    [Fact]
    public void SampleHouse_ThickeningAPartition_HoldsEveryGapDoor()
    {
        // The sample house's doors stand in gaps between records, their ends on the jambs.
        var records = WallEditTests.SampleHouse();
        var g = Graph(records);
        Assert.True(WallFace.TryThicken(records, g, Pick(g, 12000, 5400), 300, Tol, out var edit, out _, out var why), why);
        foreach (var door in WallEditTests.SampleHouseDoors())
            Assert.True(WallEdit.Holds(edit.Shape, door, Tol), $"door at {door.MinX},{door.MinY}");
        Assert.False(WallEdit.Holds(edit.Shape, WallEditTests.SampleHouseDoors()[0]));
    }

    [Fact]
    public void Middle_PicksTheSameFaceAgain()
    {
        var g = Graph(new() { WallJoinsTests.TwoRooms() });
        foreach (var run in g.Runs)
        {
            var first = Pick(g, (run.Lo + run.Hi) / 2.0 * run.Dir.X + run.Near * run.Normal.X,
                (run.Lo + run.Hi) / 2.0 * run.Dir.Y + run.Near * run.Normal.Y);
            var again = Pick(g, first.Middle.X, first.Middle.Y);
            Assert.Equal(first.Kind, again.Kind);
            Assert.Equal(first.Index, again.Index);
            Assert.Equal(first.Side, again.Side);
        }
    }

    [Fact]
    public void Dimension_SaysTheChangeAndTheResult()
    {
        var g = Graph(new() { Straight() });
        var end = Pick(g, 4000, 100);
        Assert.Equal("+800 mm, 4800 mm long", WallFace.Dimension(end, 800, false, false));
        Assert.Equal("-800 mm, 3200 mm lang", WallFace.Dimension(end, -800, false, true));
        Assert.Equal("0 mm", WallFace.Dimension(end, 0, false, false));
        var side = Pick(g, 2000, 200);
        Assert.Equal("250 mm thick", WallFace.Dimension(side, 50, true, false));
        Assert.Equal("300 mm out", WallFace.Dimension(side, 300, false, false));
        Assert.Equal("100 mm inn", WallFace.Dimension(side, -100, false, true));
        // A typed number is the change for an end or a move, the new thickness for Thickness.
        Assert.Equal(-800, WallFace.Typed(end, -800, false));
        Assert.Equal(50, WallFace.Typed(side, 250, true));
        Assert.StartsWith("Drag the end", WallFace.Prompt(end, false));
        Assert.Contains("Thickness", WallFace.Prompt(side, false));
    }

    [Fact]
    public void ThicknessPill_PromptNamesTheThicknessNow()
    {
        var g = Graph(new() { Straight() });
        var side = Pick(g, 2000, 200);
        Assert.Equal("Type the new thickness (now 200 mm), or drag the face. Click or Enter places it. Esc cancels.",
            WallFace.ThicknessPrompt(side, false));
        Assert.StartsWith("Skriv ny tykkelse (nå 200 mm)", WallFace.ThicknessPrompt(side, true));
        // Typing 250 on the pill is a 50 mm move of the clicked side.
        Assert.Equal(50, WallFace.Typed(side, 250, true));
    }
}
