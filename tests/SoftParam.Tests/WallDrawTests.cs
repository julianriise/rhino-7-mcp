using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// Draw wall: points to wall segments, the snapping, the typed length, the
/// closed loop, and that the segments go through the same WallEdit.TryAdd
/// core as add_wall and leave clean corners.
/// </summary>
public class WallDrawTests
{
    const double Tol = 1.0;
    const double T = 200;

    static WallSketch Sketch(WallDraw.Targets targets = null, double thickness = T) => new WallSketch(targets, thickness);

    static void Click(WallSketch sketch, double x, double y, bool shift = false)
    {
        Assert.True(sketch.Add(new Pt(x, y), shift, out var why), why);
    }

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };

    static WallDraw.Targets Garage() => WallDraw.TargetsFrom(new[]
    {
        new List<List<Pt>> { Rect(0, 0, 8000, 4000), Rect(200, 200, 7800, 3800) }
    });

    /// <summary>What add_wall does to the document's records for one wall.</summary>
    static void AddWall(List<List<List<Pt>>> records, WallDraw.Segment s, double thickness)
    {
        Assert.True(WallEdit.TryAdd(records, s.From, s.To, thickness, Tol, out var added, out var why), why);
        if (added.Joined >= 0) records[added.Joined] = added.Rings;
        else records.Add(added.Rings);
    }

    static List<List<List<Pt>>> Draw(IList<Pt> points, bool closed, double thickness = T)
    {
        var records = new List<List<List<Pt>>>();
        foreach (var segment in WallDraw.Plan(points, closed, thickness)) AddWall(records, segment, thickness);
        return records;
    }

    // ---- angle and length steps ----

    [Theory]
    [InlineData(1000, 80, 1000, 0)]
    [InlineData(1000, -80, 1000, 0)]
    [InlineData(60, 1000, 0, 1000)]
    [InlineData(-1003, 40, -1000, 0)]
    [InlineData(1000, 1000, 997, 997)]
    public void SnapAngle_TurnsToTheNearestStep_AndRoundsTheLength(double x, double y, double ex, double ey)
    {
        var snapped = WallDraw.SnapAngle(new Pt(0, 0), new Pt(x, y));
        Assert.Equal(ex, snapped.X, 0);
        Assert.Equal(ey, snapped.Y, 0);
    }

    [Fact]
    public void SnapAngle_KeepsThe15DegreeSteps()
    {
        // 20 degrees is nearer 15 than 30; 23 is nearer 30.
        var a = WallDraw.AngleDir(new Pt(0, 0), new Pt(Math.Cos(20 * Math.PI / 180), Math.Sin(20 * Math.PI / 180)));
        Assert.Equal(15, Math.Atan2(a.Y, a.X) * 180 / Math.PI, 6);
        var b = WallDraw.AngleDir(new Pt(0, 0), new Pt(Math.Cos(23 * Math.PI / 180), Math.Sin(23 * Math.PI / 180)));
        Assert.Equal(30, Math.Atan2(b.Y, b.X) * 180 / Math.PI, 6);
    }

    [Fact]
    public void Hover_IsOrthoByDefault_AndShiftTurnsItOff()
    {
        var sketch = Sketch();
        Click(sketch, 0, 0);
        var ortho = sketch.Hover(new Pt(3000, 90), false);
        Assert.Equal(new Pt(3000, 0), ortho.Point);
        Assert.Equal(WallDraw.SnapKind.Angle, ortho.Kind);
        var free = sketch.Hover(new Pt(3000.4, 90.2), true);
        Assert.Equal(new Pt(3000, 90), free.Point);
        Assert.Equal(WallDraw.SnapKind.None, free.Kind);
    }

    [Fact]
    public void FirstPoint_RoundsTo10Mm_ShiftToTheMillimetre()
    {
        var sketch = Sketch();
        Assert.Equal(new Pt(1230, 4560), sketch.Hover(new Pt(1233.4, 4558.1), false).Point);
        Assert.Equal(new Pt(1233, 4558), sketch.Hover(new Pt(1233.4, 4558.1), true).Point);
    }

    // ---- object snaps ----

    [Fact]
    public void Snap_ToAWallCorner_BeatsAMidpointAndAFace()
    {
        var sketch = Sketch(Garage());
        var hover = sketch.Hover(new Pt(8040, 50), false);
        Assert.Equal(WallDraw.SnapKind.End, hover.Kind);
        Assert.Equal(new Pt(8000, 0), hover.Point);
    }

    [Fact]
    public void Snap_ToAFaceMidpoint()
    {
        var sketch = Sketch(Garage());
        var hover = sketch.Hover(new Pt(4060, 260), false);
        Assert.Equal(WallDraw.SnapKind.Mid, hover.Kind);
        Assert.Equal(new Pt(4000, 200), hover.Point);
    }

    [Fact]
    public void Snap_ToTheMiddleOfAWallEnd_IsThePointOnItsCentreline()
    {
        var wall = new List<List<Pt>> { Rect(0, 0, 3000, 200) };
        var sketch = Sketch(WallDraw.TargetsFrom(new[] { wall }));
        var hover = sketch.Hover(new Pt(3040, 90), false);
        Assert.Equal(WallDraw.SnapKind.Mid, hover.Kind);
        Assert.Equal(new Pt(3000, 100), hover.Point);
    }

    [Fact]
    public void Snap_ToAFace_FreeKeepsTheNearestPoint()
    {
        var sketch = Sketch(Garage());
        var hover = sketch.Hover(new Pt(2013, 250), true);
        Assert.Equal(WallDraw.SnapKind.Face, hover.Kind);
        Assert.Equal(new Pt(2013, 200), hover.Point);
    }

    [Fact]
    public void Snap_ToAFace_WithOrtho_StaysOnTheOrthoRay()
    {
        var sketch = Sketch(Garage());
        Click(sketch, 2000, 1500);
        var hover = sketch.Hover(new Pt(2010, 230), false);
        Assert.Equal(WallDraw.SnapKind.Face, hover.Kind);
        Assert.Equal(new Pt(2000, 200), hover.Point);
    }

    [Fact]
    public void Snap_OutOfReach_FallsBackToTheSteps()
    {
        var sketch = Sketch(Garage());
        var hover = sketch.Hover(new Pt(4000, 1000), false);
        Assert.Equal(WallDraw.SnapKind.None, hover.Kind);
        Assert.Equal(new Pt(4000, 1000), hover.Point);
    }

    [Fact]
    public void Snap_ToThePolylinesOwnPoints()
    {
        var sketch = Sketch();
        Click(sketch, 0, 0);
        Click(sketch, 4000, 0);
        var hover = sketch.Hover(new Pt(3, 2900), true);
        Assert.Equal(WallDraw.SnapKind.None, hover.Kind);
        Click(sketch, 4000, 3000);
        var back = sketch.Hover(new Pt(60, 40), false);
        Assert.Equal(WallDraw.SnapKind.Start, back.Kind);
        Assert.Equal(new Pt(0, 0), back.Point);
    }

    // ---- clicks, typed lengths, close, back ----

    [Fact]
    public void Click_ShorterThanTheWallIsThick_IsRefused()
    {
        var sketch = Sketch();
        Click(sketch, 0, 0);
        Assert.False(sketch.Add(new Pt(150, 0), false, out var why));
        Assert.Equal(WallDraw.TooShort, why);
        Assert.Single(sketch.Points);
        Assert.False(sketch.Hover(new Pt(150, 0), false).Valid);
    }

    [Fact]
    public void TypedLength_GoesAlongTheDirectionTheMouseShowed()
    {
        var sketch = Sketch();
        Click(sketch, 1000, 1000);
        sketch.Hover(new Pt(1000, 2500), false);
        Assert.True(sketch.AddTyped(3250, out var why), why);
        Assert.Equal(new Pt(1000, 4250), sketch.Points[1]);
        sketch.Hover(new Pt(-3000, 4260), false);
        Assert.True(sketch.AddTyped(1200, out why), why);
        Assert.Equal(new Pt(-200, 4250), sketch.Points[2]);
    }

    [Fact]
    public void TypedLength_NeedsAFirstPoint_AndMoreThanTheThickness()
    {
        var sketch = Sketch();
        Assert.False(sketch.AddTyped(1000, out _));
        Click(sketch, 0, 0);
        sketch.Hover(new Pt(500, 0), false);
        Assert.False(sketch.AddTyped(0, out _));
        Assert.False(sketch.AddTyped(200, out var why));
        Assert.Equal(WallDraw.TooShort, why);
    }

    [Fact]
    public void Close_NeedsThreePoints_AndTurnsTheLastPointBackToTheFirst()
    {
        var sketch = Sketch();
        Click(sketch, 0, 0);
        Click(sketch, 4000, 0);
        Assert.False(sketch.Close(out var why));
        Assert.NotNull(why);
        Click(sketch, 4000, 3000);
        Assert.True(sketch.Close(out why), why);
        Assert.True(sketch.Closed);
        Assert.Equal(3, sketch.Segments().Count);
        Assert.False(sketch.Add(new Pt(0, 3000), false, out _));
    }

    [Fact]
    public void ClickingTheFirstPointAgain_ClosesTheLoop_WithoutADuplicatePoint()
    {
        var sketch = Sketch();
        Click(sketch, 0, 0);
        Click(sketch, 4000, 0);
        Click(sketch, 4000, 3000);
        Click(sketch, 0, 3000);
        Click(sketch, 30, 20);
        Assert.True(sketch.Closed);
        Assert.Equal(4, sketch.Points.Count);
        Assert.Equal(4, sketch.Segments().Count);
    }

    [Fact]
    public void Back_StepsBackOnePoint_AndTheLastOneEndsTheCommand()
    {
        var sketch = Sketch();
        Click(sketch, 0, 0);
        Click(sketch, 4000, 0);
        Click(sketch, 4000, 3000);
        Assert.True(sketch.Back());
        Assert.Equal(2, sketch.Points.Count);
        Assert.True(sketch.Back());
        Assert.False(sketch.Back());
        Assert.Empty(sketch.Points);
        Assert.Empty(sketch.Segments());
    }

    [Fact]
    public void Back_OnAClosedLoop_OpensItFirst()
    {
        var sketch = Sketch();
        Click(sketch, 0, 0);
        Click(sketch, 4000, 0);
        Click(sketch, 4000, 3000);
        Assert.True(sketch.Close(out _));
        Assert.True(sketch.Back());
        Assert.False(sketch.Closed);
        Assert.Equal(3, sketch.Points.Count);
    }

    // ---- segments ----

    [Fact]
    public void Plan_OnePointOrNone_MakesNoWalls()
    {
        Assert.Empty(WallDraw.Plan(new List<Pt>(), false, T));
        Assert.Empty(WallDraw.Plan(new List<Pt> { new Pt(0, 0) }, false, T));
    }

    [Fact]
    public void Plan_AnOpenPolyline_ExtendsEachLaterWallBackByHalfTheThickness()
    {
        var segments = WallDraw.Plan(new[] { new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 3000) }, false, T);
        Assert.Equal(2, segments.Count);
        Assert.Equal(new Pt(0, 0), segments[0].From);
        Assert.Equal(new Pt(4000, 0), segments[0].To);
        Assert.Equal(new Pt(4000, -100), segments[1].From);
        Assert.Equal(new Pt(4000, 3000), segments[1].To);
        Assert.Equal(4000, segments[0].Length, 6);
        Assert.Equal(3000, segments[1].Length, 6);
    }

    [Fact]
    public void Plan_AStraightContinuation_IsNotExtended()
    {
        var segments = WallDraw.Plan(new[] { new Pt(0, 0), new Pt(2000, 0), new Pt(5000, 0) }, false, T);
        Assert.Equal(new Pt(2000, 0), segments[1].From);
    }

    [Fact]
    public void Plan_AClosedLoop_ExtendsTheFirstWallToo()
    {
        var segments = WallDraw.Plan(Rect(0, 0, 4000, 3000), true, T);
        Assert.Equal(4, segments.Count);
        Assert.Equal(new Pt(-100, 0), segments[0].From);
        Assert.Equal(new Pt(0, 3100), segments[3].From);
        Assert.Equal(new Pt(0, 0), segments[3].To);
    }

    [Fact]
    public void ToolCalls_AreAddWallParameters()
    {
        var calls = WallDraw.ToolCalls(WallDraw.Plan(new[] { new Pt(0, 0), new Pt(4000, 0) }, false, 150), 150);
        var call = Assert.Single(calls);
        Assert.Equal(new JArray(0, 0), call["from"]);
        Assert.Equal(new JArray(4000, 0), call["to"]);
        Assert.Equal(150, call["thickness"].Value<double>());
        Assert.Equal(new[] { "from", "thickness", "to" }, call.Properties().Select(p => p.Name).OrderBy(n => n).ToArray());
    }

    // ---- the model: the same core as add_wall ----

    [Fact]
    public void Corner_At90Degrees_IsSquare_NoNotchOutside()
    {
        var records = Draw(new[] { new Pt(0, 0), new Pt(4000, 0), new Pt(4000, 3000) }, false);
        var rings = Assert.Single(records);
        var ring = Assert.Single(rings);
        Assert.Equal(6, ring.Count);
        // Two walls 200 thick: 4000 + 3000 long on the centreline.
        Assert.Equal(T * 7000, Math.Abs(RoomDetect.Area(ring)), 3);
        Assert.Contains(ring, p => p.X == 4100 && p.Y == -100);
    }

    [Fact]
    public void Corner_WithoutTheExtension_WouldLeaveANotch()
    {
        var records = new List<List<List<Pt>>>();
        AddWall(records, new WallDraw.Segment { From = new Pt(0, 0), To = new Pt(4000, 0) }, T);
        AddWall(records, new WallDraw.Segment { From = new Pt(4000, 0), To = new Pt(4000, 3000) }, T);
        var ring = Assert.Single(Assert.Single(records));
        Assert.True(Math.Abs(RoomDetect.Area(ring)) < T * 7000 - 1);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(90)]
    public void Corner_AtOtherTurns_IsOneConnectedWall_OfAboutTheStrokeArea(double turn)
    {
        var rad = turn * Math.PI / 180;
        var corner = new Pt(4000, 0);
        var end = new Pt(4000 + 3000 * Math.Cos(rad), 3000 * Math.Sin(rad));
        var records = Draw(new[] { new Pt(0, 0), corner, new Pt(Math.Round(end.X, 3), Math.Round(end.Y, 3)) }, false);
        var rings = Assert.Single(records);
        var ring = Assert.Single(rings);
        var stroke = T * 7000;
        // Exact at 90; a smaller turn leaves a sliver at most as wide as the wall.
        Assert.InRange(Math.Abs(RoomDetect.Area(ring)), stroke - T * T / 2, stroke + T * T / 2);
    }

    [Fact]
    public void ClosedRectangle_IsOneRecord_WithItsRoomAsAHole()
    {
        var records = Draw(Rect(0, 0, 4000, 3000), true);
        var rings = Assert.Single(records);
        Assert.Equal(2, rings.Count);
        var outer = rings.OrderByDescending(r => Math.Abs(RoomDetect.Area(r))).First();
        var hole = rings.OrderBy(r => Math.Abs(RoomDetect.Area(r))).First();
        Assert.Equal(4200.0 * 3200, Math.Abs(RoomDetect.Area(outer)), 3);
        Assert.Equal(3800.0 * 2800, Math.Abs(RoomDetect.Area(hole)), 3);
        Assert.Equal(4, outer.Count);
        Assert.Equal(4, hole.Count);
    }

    [Fact]
    public void ClosedRectangle_ReadsAsFourRuns_OfTheSameThickness()
    {
        var records = Draw(Rect(0, 0, 4000, 3000), true);
        var graph = WallJoins.Build(records, new List<int> { 0 }, Tol);
        Assert.Equal(4, graph.Runs.Count);
        Assert.All(graph.Runs, run => Assert.Equal(T, run.Thickness, 6));
    }

    [Fact]
    public void ADrawnWall_ThatEndsOnAnExistingFace_JoinsThatRecord()
    {
        var records = new List<List<List<Pt>>> { new List<List<Pt>> { Rect(0, 0, 8000, 4000), Rect(200, 200, 7800, 3800) } };
        var sketch = Sketch(WallDraw.TargetsFrom(records), 100);
        Click(sketch, 5000, 2000, true);
        // The mouse is near the north inner face: the ortho ray from the first point lands on it.
        var hover = sketch.Hover(new Pt(5010, 3750), false);
        Assert.Equal(WallDraw.SnapKind.Face, hover.Kind);
        Assert.Equal(new Pt(5000, 3800), hover.Point);
        Click(sketch, 5010, 3750);
        foreach (var segment in sketch.Segments()) AddWall(records, segment, 100);
        Assert.Single(records);
        Assert.Equal(2, records[0].Count);
        Assert.Equal(new Pt(5000, 3800), LastSegment(sketch).To);
    }

    static WallDraw.Segment LastSegment(WallSketch sketch) => sketch.Segments().Last();

    // ---- types, words ----

    [Fact]
    public void DefaultThickness_IsTheLastUsed_ElseTheCommonest_Else200()
    {
        Assert.Equal(150, WallDraw.DefaultThickness(new double[] { 200, 200, 100 }, 150));
        Assert.Equal(200, WallDraw.DefaultThickness(new double[] { 100, 200, 200, 300 }, null));
        Assert.Equal(100, WallDraw.DefaultThickness(new double[] { 100, 100, 200, 200 }, null));
        Assert.Equal(200, WallDraw.DefaultThickness(new double[0], null));
        Assert.Equal(200, WallDraw.DefaultThickness(new double[] { 0, -5 }, 0));
        Assert.Equal(200, WallDraw.DefaultThickness(null, 9999));
    }

    [Fact]
    public void Types_AreThePresets_PlusTheCurrentOneWhenItIsNone()
    {
        Assert.Equal(new double[] { 100, 150, 200, 250, 300 }, WallDraw.Types(200));
        Assert.Equal(new double[] { 100, 150, 180, 200, 250, 300 }, WallDraw.Types(180));
    }

    [Fact]
    public void Dimension_ReadsMillimetres_AndTheAngleOffTheAxes()
    {
        Assert.Equal("3450 mm", WallDraw.Dimension(3450, new Pt(1, 0)));
        Assert.Equal("3450 mm", WallDraw.Dimension(3450, new Pt(0, -1)));
        var d = new Pt(Math.Cos(30 * Math.PI / 180), Math.Sin(30 * Math.PI / 180));
        Assert.Equal("1000 mm · 30°", WallDraw.Dimension(1000, d));
        var down = new Pt(Math.Cos(-45 * Math.PI / 180), Math.Sin(-45 * Math.PI / 180));
        Assert.Equal("1000 mm · 315°", WallDraw.Dimension(1000, down));
    }

    [Fact]
    public void Receipt_WithNoIdsKnown_NamesNone_AndSaysClosedOnlyWhenAllWereDrawn()
    {
        Assert.Equal("Drew a 200 mm wall, 3.5 m long.", WallDraw.Receipt(1, 1, 200, 3500, false));
        Assert.Equal("Drew 4 walls, 150 mm thick, 14.0 m in all, closed.", WallDraw.Receipt(4, 4, 150, 14000, true));
        Assert.Equal("Drew 2 walls, 150 mm thick, 7.0 m in all.", WallDraw.Receipt(2, 4, 150, 7000, true));
        Assert.Equal("No walls drawn.", WallDraw.Receipt(0, 3, 200, 0, false));
        Assert.DoesNotMatch(@"\bw\d{2}\b", WallDraw.Receipt(4, 4, 200, 14000, true));
    }

    [Fact]
    public void Band_IsTheStripAddWallLaysDown()
    {
        var band = WallDraw.Band(new Pt(0, 0), new Pt(4000, 0), 200);
        Assert.Equal(new Pt(0, 100), band[0].Y < 0 ? band[3] : band[0]);
        Assert.Equal(4000.0 * 200, Math.Abs(RoomDetect.Area(band)), 6);
    }

    [Fact]
    public void Hover_TheRubberBandCarriesTheCornerExtension()
    {
        var sketch = Sketch();
        Click(sketch, 0, 0);
        Click(sketch, 4000, 0);
        var hover = sketch.Hover(new Pt(4000, 3000), false);
        Assert.NotNull(hover.Band);
        Assert.Equal(3000, hover.Length, 6);
        Assert.Equal("3000 mm", hover.Label);
        Assert.Equal(3100.0 * 200, Math.Abs(RoomDetect.Area(hover.Band)), 6);
    }
}
