using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// F7: a detected plan (forsk.plan_import.v0) becomes review geometry. The
/// clean-up squares near-orthogonal walls and leaves real diagonals, merges
/// collinear pieces (through an opening too), hosts each opening on its
/// nearest wall and reports the ones with none, and the two-point scale moves
/// the underlay and the geometry together without drift.
/// </summary>
public class PlanImportTests
{
    const double M2 = 1000000.0;

    static PlanImport.Wall W(double ax, double ay, double bx, double by, double thickness = 200) =>
        new PlanImport.Wall { A = new Pt(ax, ay), B = new Pt(bx, by), Thickness = thickness };

    static PlanImport.Opening Opening(string kind, double ax, double ay, double bx, double by) =>
        new PlanImport.Opening
        {
            Kind = kind,
            A = new Pt(ax, ay),
            B = new Pt(bx, by),
            Width = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay))
        };

    static PlanImport.Plan PlanOf(params PlanImport.Wall[] walls)
    {
        var plan = new PlanImport.Plan();
        plan.Walls.AddRange(walls);
        return plan;
    }

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };

    static double Length(PlanImport.Wall wall) =>
        Math.Sqrt(Math.Pow(wall.B.X - wall.A.X, 2) + Math.Pow(wall.B.Y - wall.A.Y, 2));

    [Fact]
    public void NearOrthogonalWall_IsSquaredAboutItsMiddle()
    {
        var result = PlanImport.Clean(PlanOf(W(0, 0, 4000, 100)));
        var wall = Assert.Single(result.Walls);
        Assert.Equal(wall.A.Y, wall.B.Y, 9);
        Assert.Equal(50.0, wall.A.Y, 9);
        Assert.Equal(Math.Sqrt(4000.0 * 4000 + 100 * 100), Length(wall), 6);
        Assert.Equal(1, result.Snapped);
        Assert.Equal(0, result.Diagonal);
    }

    [Theory]
    [InlineData(3000, 3000)]
    [InlineData(4000, 400)]
    [InlineData(1000, -1732)]
    public void Diagonal_StaysDiagonal(double bx, double by)
    {
        var result = PlanImport.Clean(PlanOf(W(0, 0, bx, by)));
        var wall = Assert.Single(result.Walls);
        var ends = new[] { wall.A, wall.B }.OrderBy(p => p.X).ToArray();
        Assert.Equal(0.0, ends[0].X, 9);
        Assert.Equal(0.0, ends[0].Y, 9);
        Assert.Equal(bx, ends[1].X, 9);
        Assert.Equal(by, ends[1].Y, 9);
        Assert.Equal(0, result.Snapped);
        Assert.Equal(1, result.Diagonal);
    }

    [Theory]
    [InlineData(243, 240)]
    [InlineData(247, 250)]
    [InlineData(255, 260)]
    [InlineData(98, 100)]
    [InlineData(3, 10)]
    public void Thickness_RoundsToTen(double detected, double rounded)
    {
        var wall = Assert.Single(PlanImport.Clean(PlanOf(W(0, 0, 4000, 0, detected))).Walls);
        Assert.Equal(rounded, wall.Thickness);
    }

    [Fact]
    public void CollinearPieces_MergeIntoOneWall()
    {
        var result = PlanImport.Clean(PlanOf(W(0, 0, 2000, 0), W(1990, 5, 5000, 5), W(5030, 0, 6000, 0)));
        var wall = Assert.Single(result.Walls);
        Assert.Equal(0.0, wall.A.X, 6);
        Assert.Equal(6000.0, wall.B.X, 6);
        Assert.InRange(wall.A.Y, 0, 5);
        Assert.Equal(wall.A.Y, wall.B.Y, 9);
        Assert.Equal(3, wall.Pieces);
        Assert.Equal(2, result.Merged);
        Assert.Equal(3, result.Detected);
    }

    [Fact]
    public void CollinearPieces_OfDifferentThickness_StayTwoWalls()
    {
        var result = PlanImport.Clean(PlanOf(W(0, 0, 2000, 0, 390), W(2000, 0, 5000, 0, 270)));
        Assert.Equal(2, result.Walls.Count);
        Assert.Equal(0, result.Merged);
    }

    [Fact]
    public void CollinearPieces_ApartWithNoOpening_StayTwoWalls()
    {
        var result = PlanImport.Clean(PlanOf(W(0, 0, 2000, 0), W(3000, 0, 6000, 0)));
        Assert.Equal(2, result.Walls.Count);
    }

    [Fact]
    public void ParallelWalls_SideBySide_StayTwoWalls()
    {
        var result = PlanImport.Clean(PlanOf(W(0, 0, 4000, 0), W(0, 150, 4000, 150)));
        Assert.Equal(2, result.Walls.Count);
    }

    /// <summary>Three walls of a 6000 x 4000 room. The tests add the bottom wall along y 0.</summary>
    static PlanImport.Wall[] ThreeSides() =>
        new[] { W(6000, 0, 6000, 4000), W(0, 4000, 6000, 4000), W(0, 0, 0, 4000) };

    [Fact]
    public void PiecesEitherSideOfAnOpening_BecomeOneWallThatHostsIt()
    {
        var plan = PlanOf(ThreeSides().Concat(new[] { W(0, 0, 2000, 0), W(3000, 0, 6000, 0) }).ToArray());
        plan.Openings.Add(Opening("window", 2000, 0, 3000, 0));
        var result = PlanImport.Clean(plan);
        Assert.Equal(4, result.Walls.Count);
        Assert.Equal(1, result.Merged);
        var window = Assert.Single(result.Openings);
        var host = result.Walls[window.Host];
        Assert.Equal(2, host.Pieces);
        // One wall from corner to corner, run to the far face of the wall at each end.
        Assert.Equal(-100.0, host.A.X, 6);
        Assert.Equal(6100.0, host.B.X, 6);
        Assert.Equal(0.0, host.A.Y, 6);
        Assert.Equal(2000.0, window.A.X, 6);
        Assert.Equal(3000.0, window.B.X, 6);
        Assert.Null(window.Note);
        Assert.Equal(220.0, window.Depth);
        Assert.Equal(1, result.Windows);
        Assert.Equal(0, result.Uncut);
        Assert.Empty(result.Review);
    }

    [Fact]
    public void Opening_SnapsOntoItsNearestWall()
    {
        var plan = PlanOf(W(0, 3000, 6000, 3000), W(0, 0, 6000, 0, 300));
        plan.Openings.Add(Opening("window", 2000, 120, 3200, 130));
        var result = PlanImport.Clean(plan);
        var window = Assert.Single(result.Openings);
        var host = result.Walls[window.Host];
        Assert.Equal(0.0, host.A.Y, 9);
        Assert.Equal(0.0, window.A.Y, 6);
        Assert.Equal(0.0, window.B.Y, 6);
        Assert.Equal(2600.0, (window.A.X + window.B.X) / 2, 6);
        Assert.Equal(320.0, window.Depth);
        Assert.Equal(0, result.Loose);

        // The footprint crosses the wall: the bake's cutter goes through both faces.
        var ring = PlanImport.Ring(window);
        Assert.Equal(-160.0, ring.Min(p => p.Y), 6);
        Assert.Equal(160.0, ring.Max(p => p.Y), 6);
    }

    [Fact]
    public void OpeningBeyondReach_StaysWhereItWasAndIsReported()
    {
        var plan = PlanOf(W(0, 0, 6000, 0));
        plan.Openings.Add(Opening("window", 2000, 600, 3000, 600));
        var result = PlanImport.Clean(plan);
        var window = Assert.Single(result.Openings);
        Assert.Equal(-1, window.Host);
        Assert.Equal(600.0, window.A.Y);
        Assert.Equal(1, result.Loose);
        var line = Assert.Single(result.Review, row => row.StartsWith("window"));
        Assert.Contains("window at 2.5, 0.6 m", line);
        Assert.Contains("nearest face 500 mm away", line);
    }

    [Fact]
    public void OpeningDrawnAcrossAWall_IsNotThatWallsOpening()
    {
        var plan = PlanOf(W(0, 0, 6000, 0));
        plan.Openings.Add(Opening("window", 3000, -200, 3000, 230));
        var result = PlanImport.Clean(plan);
        Assert.Equal(-1, Assert.Single(result.Openings).Host);
        Assert.Equal(1, result.Loose);
    }

    [Fact]
    public void DoorPastAWallEnd_RunsTheWallThroughIt()
    {
        var plan = PlanOf(W(0, 0, 2000, 0));
        plan.Openings.Add(Opening("door", 2000, 0, 2900, 0));
        var result = PlanImport.Clean(plan);
        var wall = Assert.Single(result.Walls);
        Assert.Equal(2900.0, wall.B.X, 6);
        Assert.Equal(1, result.Extended);
        Assert.Equal(0, Assert.Single(result.Openings).Host);
        Assert.Equal(1, result.Doors);
    }

    [Fact]
    public void PassageWithNoDoor_ComesInAsADoorAndSaysSo()
    {
        var plan = PlanOf(ThreeSides().Concat(new[] { W(0, 0, 6000, 0) }).ToArray());
        plan.Openings.Add(Opening("opening", 2000, 0, 3300, 0));
        var result = PlanImport.Clean(plan);
        Assert.Equal("door", Assert.Single(result.Openings).Kind);
        Assert.Contains("a passage with no door drawn", Assert.Single(result.Review));
    }

    [Fact]
    public void OpeningOnADiagonalWall_IsNotFlagged()
    {
        // The bake cuts square to a diagonal wall: nothing to review.
        var plan = PlanOf(W(0, 0, 3000, 3000));
        plan.Openings.Add(Opening("window", 1000, 1000, 2000, 2000));
        var result = PlanImport.Clean(plan);
        Assert.Equal(0, Assert.Single(result.Openings).Host);
        Assert.DoesNotContain(result.Review, row => row.StartsWith("window"));
    }

    /// <summary>A 5000 x 4000 room on centrelines, each corner drawn a little short or just touching.</summary>
    static PlanImport.Plan LooseCorners()
    {
        var plan = PlanOf(
            W(0, 0, 5000, 0),
            W(5000, 60, 5000, 4000),
            W(0, 4000, 4940, 4000),
            W(0, 0, 0, 3850));
        plan.Rooms.Add(new PlanImport.Room { Label = "Stue", Ring = Rect(100, 100, 4900, 3900) });
        return plan;
    }

    [Fact]
    public void WallEnds_RunIntoTheWallsTheyStopAt_AndTheOutlineCloses()
    {
        // As detected the corners do not meet all round: no loop closes a room.
        var before = RoomDetect.Union(LooseCorners().Walls.Select(PlanImport.Ring).ToList(), 1.0);
        Assert.DoesNotContain(before, loop => RoomDetect.Area(loop) < 0);

        var result = PlanImport.Clean(LooseCorners());
        Assert.Equal(4, result.Walls.Count);
        Assert.Equal(8, result.Joined);
        var loops = RoomDetect.Union(result.Walls.Select(PlanImport.Ring).ToList(), 1.0);
        Assert.Equal(new[] { -18.24, 21.84 }, Areas(loops));
        Assert.All(loops, loop => Assert.Equal(4, loop.Count));
        Assert.Equal(1, result.Outlines);
        Assert.Equal(0, result.Blocks);
        Assert.False(Assert.Single(result.Rooms).Outside);
        Assert.Empty(result.Review);
    }

    [Fact]
    public void WallsThatDoNotClose_AreReportedAgainstTheirRooms()
    {
        var plan = PlanOf(W(0, 0, 5000, 0), W(5000, 0, 5000, 4000), W(0, 0, 0, 4000));
        plan.Rooms.Add(new PlanImport.Room { Label = "Stue", Ring = Rect(100, 100, 4900, 3900) });
        var result = PlanImport.Clean(plan);
        Assert.True(Assert.Single(result.Rooms).Outside);
        Assert.Equal(1, result.Outside);
        Assert.Contains("The walls do not close around all 1 room", Assert.Single(result.Review));
    }

    [Fact]
    public void OpeningOnAWallRunStandingFree_Cuts_OnABlockTheBakeSkips_DoesNot()
    {
        var plan = PlanOf(
            W(0, 0, 5000, 0), W(5000, 0, 5000, 4000), W(0, 4000, 5000, 4000), W(0, 0, 0, 4000),
            W(8000, 0, 8000, 3000),
            // As wide across as a small room: the bake reads this one as a room outline.
            W(12000, 0, 12000, 3000, 1600));
        plan.Openings.Add(Opening("window", 8000, 1000, 8000, 2000));
        plan.Openings.Add(Opening("window", 1000, 0, 2000, 0));
        plan.Openings.Add(Opening("door", 6500, 1000, 6500, 1900));
        plan.Openings.Add(Opening("window", 12000, 500, 12000, 2500));
        var result = PlanImport.Clean(plan);
        Assert.Equal(2, result.Blocks);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.Loose);
        // The loose door, and the window on the block. The window on the free wall run cuts.
        Assert.Equal(2, result.Uncut);
        Assert.Null(result.Openings[0].Note);
        Assert.Null(result.Openings[1].Note);
        Assert.Contains(result.Review, row => row.StartsWith("window at 12.0, 1.5 m: its wall is in a block the bake skips"));
        Assert.Contains(result.Review, row => row.StartsWith("wall at 12.0, 1.5 m stands free of the other walls: a block this wide reads as a room outline"));
    }

    [Theory]
    // A wall run, however long: twice the area over the edge is its width across.
    [InlineData(3685, 270, true)]
    [InlineData(600, 170, true)]
    [InlineData(20000, 600, true)]
    // A room outline drawn on the wall layer.
    [InlineData(4000, 3000, false)]
    [InlineData(2000, 1400, false)]
    public void ALoneBlock_IsAWallRunWhenSlender_NeverARoom(double length, double across, bool run)
    {
        Assert.True(RoomDetect.IsBlock(length * across, length * across));
        Assert.Equal(run, RoomDetect.IsWallRun(length * across, 2 * (length + across)));
        // An L of two runs is slender all along, though its bounding box is a room's.
        Assert.True(RoomDetect.IsWallRun(2 * 4000 * 250.0, 2 * (8000 + 250.0)));
    }

    /// <summary>A closed room whose right wall stops short of the top wall by gap, all walls the same thickness.</summary>
    static PlanImport.Plan RoomWithGap(double gap, double thickness)
    {
        var half = thickness / 2.0;
        var plan = PlanOf(
            W(0, 0, 5000, 0, thickness), W(0, 4000, 5000, 4000, thickness), W(0, 0, 0, 4000, thickness),
            W(5000, 0, 5000, 4000 - half - gap, thickness));
        plan.Rooms.Add(new PlanImport.Room { Label = "Stue", Ring = Rect(half, half, 5000 - half, 4000 - half) });
        return plan;
    }

    [Theory]
    // Within the old 100 mm: joined, and not worth a line.
    [InlineData(80, 300, true, 0)]
    // Wider, but narrower than the walls are thick: no doorway fits, so it is a gap in the detection.
    [InlineData(250, 300, true, 1)]
    [InlineData(390, 400, true, 1)]
    // Wider than the walls are thick: left open.
    [InlineData(250, 200, false, 0)]
    [InlineData(450, 400, false, 0)]
    public void GapNarrowerThanTheWallIsThick_IsClosedAndReported(double gap, double thickness, bool closes, int reported)
    {
        var result = PlanImport.Clean(RoomWithGap(gap, thickness));
        Assert.Equal(reported, result.Closed);
        Assert.Equal(reported, result.Review.Count(row => row.StartsWith("Closed a " + gap + " mm gap at 5.0, ")));
        var holes = RoomDetect.Union(result.Walls.Select(PlanImport.Ring).ToList(), 1.0).Count(loop => RoomDetect.Area(loop) < 0);
        Assert.Equal(closes ? 1 : 0, holes);
        Assert.Equal(1, result.Outlines);
        Assert.Equal(!closes, Assert.Single(result.Rooms).Outside);
        var receipt = PlanImport.Message(result, "");
        Assert.Contains(reported > 0 ? ", 1 gap closed, " : ", 0 gaps closed, ", receipt);
        Assert.Equal(reported > 0, receipt.Contains("1 wall gap closed"));
    }

    [Fact]
    public void GapBetweenTwoEndsInOneLine_IsOneGap()
    {
        // Two pieces of one 300 mm wall, 200 mm apart, with no opening between them.
        var result = PlanImport.Clean(PlanOf(W(0, 0, 3000, 0, 300), W(3200, 0, 6000, 0, 300)));
        Assert.Equal(2, result.Joined);
        Assert.Equal(1, result.Closed);
        Assert.Single(RoomDetect.Union(result.Walls.Select(PlanImport.Ring).ToList(), 1.0));
    }

    [Fact]
    public void WallRunStandingFreeOfAHouseWithGapsClosed_LeavesTwoOutlines()
    {
        // The room closes once its gap is; the run beside it is too far off to be a gap.
        var plan = RoomWithGap(250, 300);
        plan.Walls.Add(W(1000, -900, 3000, -900, 300));
        var result = PlanImport.Clean(plan);
        Assert.Equal(1, result.Closed);
        Assert.Equal(2, result.Outlines);
        Assert.Equal(1, result.Blocks);
        Assert.Equal(0, result.Uncut);
        Assert.Contains(result.Review, row => row.Contains("stands free of the other walls: it bakes as a wall on its own"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpeningsAtAWallEnd_FindTheirWallInEitherOrder(bool windowFirst)
    {
        // The wall stops at 2000. A door runs on from there to 2900, then a window to 3400, then the cross wall.
        var plan = PlanOf(W(0, 0, 2000, 0, 250), W(3500, -2000, 3500, 2000, 250));
        var door = Opening("door", 2000, 0, 2900, 0);
        var window = Opening("window", 2900, 0, 3400, 0);
        plan.Openings.AddRange(windowFirst ? new[] { window, door } : new[] { door, window });
        var result = PlanImport.Clean(plan);
        Assert.Equal(0, result.Loose);
        Assert.Equal(0, result.Uncut);
        Assert.All(result.Openings, opening => Assert.Equal(0, opening.Host));
        // And the wall runs through both to the far face of the cross wall.
        Assert.Equal(3625.0, result.Walls[0].B.X, 6);
        // The file's order is kept, whichever pass found the wall.
        Assert.Equal(windowFirst ? "window" : "door", result.Openings[0].Kind);
        Assert.DoesNotContain(result.Review, row => row.Contains("no wall in reach"));
    }

    /// <summary>
    /// rooms_detect after the bake: the walls are the outlines the wall
    /// rectangles make together, and the imported room outlines are kept.
    /// Every imported room is still a room, walls closed round it or not,
    /// and detection adds no second room on top of one.
    /// </summary>
    [Theory]
    [InlineData(250, 300, 2)]
    [InlineData(450, 300, 1)]
    public void ImportedRooms_SurviveRoomsDetect_ClosedOrNot(double gap, double thickness, int closed)
    {
        var plan = RoomWithGap(gap, thickness);
        plan.Walls.Add(W(2500, 0, 2500, 4000, thickness));
        plan.Rooms.Clear();
        plan.Rooms.Add(new PlanImport.Room { Label = "Stue", Ring = Rect(150, 150, 2350, 3850) });
        plan.Rooms.Add(new PlanImport.Room { Label = "Bad", Ring = Rect(2650, 150, 4850, 3850) });
        var result = PlanImport.Clean(plan);
        Assert.Equal(2, result.Rooms.Count);

        var loops = RoomDetect.Union(result.Walls.Select(PlanImport.Ring).ToList(), 1.0);
        var scene = new RoomDetect.Scene();
        foreach (var outer in loops.Where(loop => RoomDetect.Area(loop) > 0))
            scene.Walls.Add(new[] { outer }.Concat(loops.Where(loop => RoomDetect.Area(loop) < 0 && RoomDetect.Contains(outer, loop[0]))).ToList());
        scene.Keep.AddRange(result.Rooms.Select(room => room.Ring));
        var found = RoomDetect.Detect(scene);

        // No region is detected a second time: what the walls close is an imported room already.
        Assert.Empty(found.Rooms);
        Assert.Equal(closed, found.Kept);
        Assert.Equal(2 - closed, result.Outside);
        // The receipt names each room the walls stay open around.
        if (closed < 2)
            Assert.Contains(result.Review, row => row.StartsWith("The walls do not close around Bad."));
        else
            Assert.DoesNotContain(result.Review, row => row.StartsWith("The walls do not close"));
    }

    [Fact]
    public void WallEndFarFromAnyWall_IsLeftAlone()
    {
        var result = PlanImport.Clean(PlanOf(W(0, 0, 5000, 0), W(5000, 400, 5000, 4000)));
        Assert.Equal(0, result.Joined);
        Assert.Equal(400.0, result.Walls.Min(w => Math.Max(w.A.Y, w.B.Y) > 3000 ? Math.Min(w.A.Y, w.B.Y) : 1e9));
    }

    [Fact]
    public void WallInsideAThickerWall_IsDroppedWithTheReason()
    {
        var result = PlanImport.Clean(PlanOf(W(0, 0, 5000, 0, 400), W(1000, 50, 2000, 50, 100)));
        Assert.Single(result.Walls);
        Assert.Equal("wall at 1.5, 0.1 m: inside a thicker wall", Assert.Single(result.Dropped));
        Assert.Contains(result.Review, row => row.StartsWith("Dropped wall at 1.5, 0.1 m"));
    }

    [Fact]
    public void StubAndWallWithNoThickness_AreDroppedWithTheReason()
    {
        var result = PlanImport.Clean(PlanOf(W(0, 0, 5000, 0), W(7000, 0, 7040, 0), W(0, 2000, 5000, 2000, 0)));
        Assert.Single(result.Walls);
        Assert.Equal(new[] { "wall at 7.0, 0.0 m: 40 mm long", "wall at 2.5, 2.0 m: no thickness" }, result.Dropped);
        Assert.Contains("(3 detected, 2 dropped)", PlanImport.Message(result, ""));
    }

    const string Synthetic = @"{
      ""schema"": ""forsk.plan_import.v0"", ""units"": ""mm"", ""y_axis"": ""up"",
      ""source"": { ""vendor"": ""synthetic"" },
      ""scale"": { ""status"": ""detected"", ""ratio"": ""1:50"" },
      ""image"": { ""width_mm"": 21006, ""height_mm"": 14852 },
      ""walls"": [
        { ""start"": [0, 0], ""end"": [5000, 0], ""thickness"": 203 },
        { ""start"": [5000, 0], ""end"": [5000, -4000], ""thickness"": 198, ""class"": ""wall"" },
        { ""start"": [5000, -4000], ""end"": [0, -4000], ""thickness"": 200 },
        { ""start"": [0, -4000], ""end"": [0, 0], ""thickness"": 200 },
        { ""start"": [2500, 0], ""end"": [2500, -4000], ""thickness"": 98, ""class"": ""partition"" }
      ],
      ""openings"": [
        { ""kind"": ""window"", ""a"": [1000, 0], ""b"": [2000, 0], ""width"": 1000, ""glass_lines"": 2 },
        { ""kind"": ""door"", ""hinge"": [2540, -1000], ""closed"": [2460, -1900], ""open"": [3400, -1000],
          ""width"": 900, ""opening_width"": 1010 }
      ],
      ""rooms"": [
        { ""label"": ""Bad"", ""ocr_text"": ""Bad"", ""boundary"": [[100, -100], [2450, -100], [2450, -3900], [100, -3900]] },
        { ""label"": ""Rom"", ""ocr_text"": null, ""boundary"": [[2550, -100], [4900, -100], [4900, -3900], [2550, -3900]] }
      ],
      ""wall_polygons"": [ { ""outer"": [[0, 0], [1, 0], [1, 1]], ""holes"": [] } ]
    }";

    [Fact]
    public void Parse_ReadsWallsOpeningsRoomsAndScale_AndIgnoresOtherKeys()
    {
        var plan = PlanImport.Parse(Synthetic);
        Assert.Equal(5, plan.Walls.Count);
        Assert.Equal("partition", plan.Walls[4].Class);
        Assert.Equal("synthetic", plan.Vendor);
        Assert.Equal("detected", plan.ScaleStatus);
        Assert.Equal("1:50", plan.ScaleRatio);
        Assert.Equal(21006.0, plan.ImageWidthMm);
        Assert.Equal(0.0, PlanImport.Parse(@"{ ""schema"": ""forsk.plan_import.v0"" }").ImageWidthMm);

        var door = plan.Openings[1];
        Assert.Equal("door", door.Kind);
        Assert.Equal(1010.0, door.Width);
        Assert.Equal(2540.0, door.A.X);
        Assert.Equal(-1900.0, door.B.Y);
        Assert.Equal(1000.0, plan.Openings[0].Width);

        Assert.Equal("Bad", plan.Rooms[0].Label);
        Assert.Null(plan.Rooms[1].Label);
    }

    [Fact]
    public void SyntheticPlan_CleansToAClosedTwoRoomPlan()
    {
        var result = PlanImport.Clean(PlanImport.Parse(Synthetic));
        Assert.Equal(5, result.Walls.Count);
        Assert.Equal(new[] { 100.0, 200.0, 200.0, 200.0, 200.0 }, result.Walls.Select(w => w.Thickness).OrderBy(t => t));
        Assert.Equal(1, result.Doors);
        Assert.Equal(1, result.Windows);
        Assert.Equal(0, result.Loose);

        // The door's leaf was drawn 5 degrees off the partition: it lands on the centreline.
        var door = result.Openings[1];
        Assert.Equal(2500.0, door.A.X, 6);
        Assert.Equal(2500.0, door.B.X, 6);
        Assert.Equal(1010.0, Math.Abs(door.A.Y - door.B.Y), 6);
        Assert.Equal(100.0, result.Walls[door.Host].Thickness);

        Assert.Equal(2, result.Rooms.Count);
        Assert.Equal(1, result.Unlabelled);
        Assert.Equal(0, result.Outside);
        Assert.Contains("1 room without a label (3.7, -2.0 m)", Assert.Single(result.Review));
        Assert.Equal(
            "Imported 5 walls, 1 door, 1 window, 2 rooms. Wall cleanup: 5 wall pieces merged into 1 outline, "
            + "0 gaps closed, 0 overlaps left. Scale 1:50 read from the plan and applied: "
            + "confirm it, or override it, with Set scale (two points and a known length). Review: 1 room without a label.",
            PlanImport.Message(result, PlanImport.ScaleLine("detected", "1:50")));
    }

    /// <summary>
    /// The synthetic plan file (server/tests/fixtures): a source's
    /// forsk.plan_import.v0 as the plugin reads and cleans it.
    /// </summary>
    [Fact]
    public void SyntheticPlanFile_ReadsAndCleansOnThePluginSide()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "plan_import_synthetic.json");
        var plan = PlanImport.Parse(File.ReadAllText(path));
        Assert.Equal("synthetic", plan.Vendor);
        Assert.Equal("assumed", plan.ScaleStatus);
        Assert.Equal("1:100", plan.ScaleRatio);
        Assert.Equal(12000.0, plan.ImageWidthMm);

        var result = PlanImport.Clean(plan);
        Assert.Equal(new[] { 180.0, 250.0 }, result.Walls.Select(w => w.Thickness));
        // The horizontal wall stopped at the vertical wall's near face: it runs to the far one.
        Assert.Equal(6250.0, result.Walls[0].B.X, 6);
        Assert.Equal(new[] { 0, 1 }, result.Openings.Select(o => o.Host));
        Assert.Equal(new[] { "window", "door" }, result.Openings.Select(o => o.Kind));
        Assert.Equal(new[] { "Stue", "Bad", null }, result.Rooms.Select(r => r.Label));
        Assert.Equal(1, result.Unlabelled);
        Assert.Equal(
            "Scale not detected (assumed 1:100): set it with two points and a known length.",
            PlanImport.ScaleLine("unconfirmed", plan.ScaleRatio));
    }

    [Theory]
    [InlineData(@"{ ""schema"": ""floorplan.v1"", ""walls"": [] }", "Not a forsk.plan_import.v0 file (schema floorplan.v1).")]
    [InlineData(@"{ ""walls"": [] }", "Not a forsk.plan_import.v0 file.")]
    [InlineData(@"{ ""schema"": ""forsk.plan_import.v0"", ""units"": ""m"" }", "plan_import units must be mm, not m.")]
    [InlineData(@"{ ""schema"": ""forsk.plan_import.v0"", ""walls"": [ { ""start"": [0, 0] } ] }", "Wall 1 has no start and end.")]
    public void Parse_RefusesWhatItCannotRead(string json, string message)
    {
        Assert.Equal(message, Assert.Throws<FormatException>(() => PlanImport.Parse(json)).Message);
    }

    [Fact]
    public void Parse_YAxisDown_IsFlippedToUp()
    {
        var plan = PlanImport.Parse(@"{ ""schema"": ""forsk.plan_import.v0"", ""y_axis"": ""down"",
            ""walls"": [ { ""start"": [0, 100], ""end"": [4000, 100], ""thickness"": 200 } ] }");
        Assert.Equal(-100.0, plan.Walls[0].A.Y);
    }

    [Theory]
    [InlineData("user", "1:100", "Scale set from two points and a known length.")]
    [InlineData("detected", "1:100", "Scale 1:100 read from the plan and applied: confirm it, or override it, with Set scale (two points and a known length).")]
    [InlineData("user_supplied", "1:100", "Scale not detected (assumed 1:100): set it with two points and a known length.")]
    [InlineData(null, null, "Scale not detected: set it with two points and a known length.")]
    public void ScaleLine_SaysHowTheScaleStands(string status, string ratio, string line)
    {
        Assert.Equal(line, PlanImport.ScaleLine(status, ratio));
    }

    static PlanImport.Scale At(double factor) => new PlanImport.Scale { Origin = new Pt(0, 0), Factor = factor };

    [Fact]
    public void TwoPointsAndALength_ScaleTheUnderlayAndTheWallsTogether()
    {
        var now = At(1.0);
        var p1 = new Pt(1000, -2000);
        var p2 = new Pt(4900, -2000);
        var factor = PlanImport.FactorFor(now, p1, p2, 4000, false);
        Assert.Equal(4000.0 / 3900.0, factor, 12);

        // The plan image, top-left at the origin, and a wall on it keep their place on each other.
        var underlay = new List<Pt> { new Pt(0, 0), new Pt(42012, 0), new Pt(42012, -29700), new Pt(0, -29700) };
        var scaledUnderlay = underlay.Select(p => PlanImport.Rescale(now, factor, p)).ToList();
        Assert.Equal(0.0, scaledUnderlay[0].X, 9);
        Assert.Equal(42012 * factor, scaledUnderlay[1].X, 6);
        Assert.Equal(-29700 * factor, scaledUnderlay[2].Y, 6);

        // A wall outline scales as drawn, corner by corner, about the same corner.
        var wall = PlanImport.Ring(W(1000, -2000, 4900, -2000, 250)).Select(p => PlanImport.Rescale(now, factor, p)).ToList();
        Assert.Equal(1000 * factor, (wall[0].X + wall[3].X) / 2, 6);
        Assert.Equal(-2000 * factor, (wall[0].Y + wall[3].Y) / 2, 6);
        Assert.Equal(250 * factor, wall[3].Y - wall[0].Y, 6);

        // The two picked points are now the known length apart.
        var a = PlanImport.Rescale(now, factor, p1);
        var b = PlanImport.Rescale(now, factor, p2);
        Assert.Equal(4000.0, b.X - a.X, 6);
    }

    [Fact]
    public void RepeatingTheScaleStep_ChangesNothing()
    {
        var p1 = new Pt(1000, -2000);
        var p2 = new Pt(4900, -2000);
        var factor = PlanImport.FactorFor(At(1.0), p1, p2, 4000, false);
        var now = At(factor);

        // Picked again on the same two features, in the model as it is now.
        var a = PlanImport.Rescale(At(1.0), factor, p1);
        var b = PlanImport.Rescale(At(1.0), factor, p2);
        var again = PlanImport.FactorFor(now, a, b, 4000, false);
        Assert.Equal(factor, again, 12);
        Assert.Equal(4000.0, PlanImport.Measure(now, a, b, false), 6);

        // Given in the detection's own mm, the answer never depends on the scale the plan has.
        Assert.Equal(factor, PlanImport.FactorFor(now, p1, p2, 4000, true), 12);
        Assert.Equal(factor, PlanImport.FactorFor(At(7.5), p1, p2, 4000, true), 12);
        Assert.Equal(4000.0, PlanImport.Measure(now, p1, p2, true), 6);

        var corner = new Pt(42012 * factor, -29700 * factor);
        var same = PlanImport.Rescale(now, again, corner);
        Assert.Equal(corner.X, same.X, 6);
        Assert.Equal(corner.Y, same.Y, 6);
    }

    [Fact]
    public void ManyRescales_EndWhereTheDetectionStarted()
    {
        var corner = new Pt(42012, -29700);
        var ring = PlanImport.Ring(W(1000, -2000, 4900, -2000, 250));
        var now = At(1.0);
        var factors = new[] { 1.037, 0.5, 2.0, 0.998, 1.25, 0.013, 3.3, 0.73, 1.0001, 1.0 };
        for (var round = 0; round < 5; round++)
        {
            foreach (var factor in factors)
            {
                corner = PlanImport.Rescale(now, factor, corner);
                ring = ring.Select(p => PlanImport.Rescale(now, factor, p)).ToList();
                now = At(factor);
            }
        }
        Assert.Equal(42012.0, corner.X, 6);
        Assert.Equal(-29700.0, corner.Y, 6);
        var start = PlanImport.Ring(W(1000, -2000, 4900, -2000, 250));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(start[i].X, ring[i].X, 6);
            Assert.Equal(start[i].Y, ring[i].Y, 6);
        }
    }

    [Fact]
    public void ScaleStep_RefusesOnePointAndNoLength()
    {
        var p = new Pt(1000, 0);
        Assert.Throws<ArgumentException>(() => PlanImport.FactorFor(At(1.0), p, p, 4000, false));
        Assert.Throws<ArgumentException>(() => PlanImport.FactorFor(At(1.0), p, new Pt(2000, 0), 0, false));
    }

    static byte[] Png(int width, int height, int pixelsPerMetre)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        void Int(int value) => bytes.AddRange(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        void Chunk(string type, Action body, int length)
        {
            Int(length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            body();
            Int(0);
        }
        Chunk("IHDR", () => { Int(width); Int(height); bytes.AddRange(new byte[] { 8, 2, 0, 0, 0 }); }, 13);
        if (pixelsPerMetre > 0)
            Chunk("pHYs", () => { Int(pixelsPerMetre); Int(pixelsPerMetre); bytes.Add(1); }, 9);
        Chunk("IDAT", () => { }, 0);
        return bytes.ToArray();
    }

    [Fact]
    public void PngHeader_GivesPixelsAndDpi_AndTheWidthOnThePlan()
    {
        Assert.True(PlanImport.TryPngSize(Png(3308, 2339, 7874), out var width, out var height, out var dpi));
        Assert.Equal(3308, width);
        Assert.Equal(2339, height);
        Assert.Equal(200.0, dpi);
        Assert.Equal(42011.6, PlanImport.ImageWidthMm(width, dpi, 100), 6);

        Assert.True(PlanImport.TryPngSize(Png(800, 600, 0), out _, out _, out dpi));
        Assert.Equal(0.0, dpi);
        Assert.False(PlanImport.TryPngSize(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, out _, out _, out _));
    }

    [Theory]
    [InlineData("1:100", true, 100)]
    [InlineData(" 1:50 ", true, 50)]
    [InlineData("1:0", false, 0)]
    [InlineData("100", false, 0)]
    [InlineData(null, false, 0)]
    public void Ratio_ReadsTheDenominator(string ratio, bool ok, double denominator)
    {
        Assert.Equal(ok, PlanImport.TryRatio(ratio, out var read));
        Assert.Equal(denominator, read);
    }

    static double[] Areas(List<List<Pt>> loops) =>
        loops.Select(l => Math.Round(RoomDetect.Area(l) / M2, 6)).OrderBy(a => a).ToArray();

    [Fact]
    public void Union_SeparateWallRectangles_GiveTheOutlineAndTheRoomsTheyClose()
    {
        var rings = new List<List<Pt>>
        {
            Rect(0, 0, 4200, 200), Rect(0, 4000, 4200, 4200), Rect(0, 0, 200, 4200), Rect(4000, 0, 4200, 4200),
            // A partition that only touches the walls it runs between.
            Rect(2000, 200, 2100, 4000)
        };
        var loops = RoomDetect.Union(rings, 1.0);
        Assert.Equal(new[] { -7.22, -6.84, 17.64 }, Areas(loops));
        Assert.All(loops, loop => Assert.Equal(4, loop.Count));
    }

    [Fact]
    public void Union_OpenRing_IsOneOutlineWithNoHole()
    {
        var rings = new List<List<Pt>> { Rect(0, 0, 4200, 200), Rect(0, 0, 200, 4200), Rect(4000, 0, 4200, 4200) };
        var loop = Assert.Single(RoomDetect.Union(rings, 1.0));
        Assert.Equal((4200.0 * 200 + 2 * 4000.0 * 200) / M2, RoomDetect.Area(loop) / M2, 6);
        Assert.Equal(8, loop.Count);
    }

    [Fact]
    public void Union_OutlinesApart_StayAsDrawn()
    {
        var rings = new List<List<Pt>> { Rect(0, 0, 4000, 3000), Rect(6000, 0, 9000, 3000) };
        var loops = RoomDetect.Union(rings, 1.0);
        Assert.Equal(new[] { 9.0, 12.0 }, Areas(loops));
        Assert.All(loops, loop => Assert.Equal(4, loop.Count));
    }

    [Fact]
    public void Union_RectangleInsideAWall_ChangesNothing()
    {
        var walls = new List<List<Pt>>
        {
            Rect(0, 0, 4200, 200), Rect(0, 4000, 4200, 4200), Rect(0, 0, 200, 4200), Rect(4000, 0, 4200, 4200)
        };
        var inside = new List<List<Pt>>(walls) { Rect(1000, 50, 2000, 150) };
        Assert.Equal(new[] { -14.44, 17.64 }, Areas(RoomDetect.Union(walls, 1.0)));
        Assert.Equal(new[] { -14.44, 17.64 }, Areas(RoomDetect.Union(inside, 1.0)));
    }

    [Fact]
    public void Union_DiagonalAcrossACorner_ClosesATriangle()
    {
        // A bay: two walls at a right angle and a diagonal drawn across them.
        var rings = new List<List<Pt>>
        {
            Rect(0, 0, 4000, 200), Rect(0, 0, 200, 4000),
            PlanImport.Ring(W(3000, 100, 100, 3000))
        };
        var loops = RoomDetect.Union(rings, 1.0);
        Assert.Equal(2, loops.Count);
        Assert.Single(loops, loop => RoomDetect.Area(loop) < 0);
    }

    [Fact]
    public void WallStandingFree_IsReported_AndBakesAsAWall()
    {
        var plan = PlanOf(
            W(0, 0, 5000, 0), W(5000, 0, 5000, 4000), W(0, 4000, 5000, 4000), W(0, 0, 0, 4000),
            W(8000, 0, 8000, 3000));
        var result = PlanImport.Clean(plan);
        Assert.Equal(2, result.Outlines);
        Assert.Equal(1, result.Blocks);
        Assert.True(result.Walls.Single(w => w.A.X == 8000).Free);
        Assert.Equal(1, result.Walls.Count(w => w.Free));
        Assert.DoesNotContain(result.Walls, w => w.Skipped);
        Assert.Contains("wall at 8.0, 1.5 m stands free of the other walls: it bakes as a wall on its own", Assert.Single(result.Review));
        Assert.Contains("1 wall standing free", PlanImport.Message(result, ""));
    }

    static PlanImport.Plan Room200() => PlanOf(
        W(0, 0, 5000, 0), W(5000, 0, 5000, -4000), W(0, -4000, 5000, -4000), W(0, 0, 0, -4000));

    [Fact]
    public void Rescale_CleansTheWallsAgain_SoThicknessIsRoundedAtTheScaleSet()
    {
        // 200 mm walls at x1.037: scaled as drawn they are 207.4 mm thick, cleaned again 210.
        var origin = new Pt(0, 0);
        var at = PlanImport.Clean(PlanImport.Scaled(Room200(), 1.037, origin));
        Assert.All(at.Walls, w => Assert.Equal(210.0, w.Thickness));
        var outline = Assert.Single(at.Networks);
        var hole = Assert.Single(outline.Holes);
        // The outline's two faces are the rounded thickness apart.
        Assert.Equal(210.0, hole.Min(p => p.X) - outline.Outer.Min(p => p.X), 6);
        Assert.Equal(210.0, outline.Outer.Max(p => p.Y) - hole.Max(p => p.Y), 6);
        Assert.All(PlanImport.Clean(PlanImport.Scaled(Room200(), 2.074, origin)).Walls, w => Assert.Equal(410.0, w.Thickness));
    }

    [Fact]
    public void Rescale_AwayAndBack_LeavesEveryWallLoopWhereItWas()
    {
        var origin = new Pt(120.0, -35.0);
        var source = PlanImport.Parse(PlanImport.SourceJson(Room200()));
        List<PlanImport.WallLoop> At(double factor) => PlanImport.WallLoops(PlanImport.Clean(PlanImport.Scaled(source, factor, origin)));
        var first = At(0.999724);
        At(1.999448);
        var back = At(0.999724);
        Assert.Equal(new[] { "import-wall-01", "import-wall-01-hole-01" }, first.Select(l => l.Name));
        Assert.True(PlanImport.SameLoops(first, back.ToDictionary(l => l.Name, l => l.Ring), 0.0));
        // The source kept on the underlay cleans to what the import drew from the file.
        Assert.True(PlanImport.SameLoops(PlanImport.WallLoops(PlanImport.Clean(Room200())),
            PlanImport.WallLoops(PlanImport.Clean(PlanImport.Scaled(source, 1.0, new Pt(0, 0)))).ToDictionary(l => l.Name, l => l.Ring), 0.0));
    }

    [Fact]
    public void Rescale_AnEditedWallLoop_IsNotTheImports()
    {
        var loops = PlanImport.WallLoops(PlanImport.Clean(Room200()));
        var drawn = loops.ToDictionary(l => l.Name, l => new List<Pt>(l.Ring));
        Assert.True(PlanImport.SameLoops(loops, drawn, 0.01));
        // A face moved 5 mm: the user's wall now, and it scales as drawn.
        var ring = drawn["import-wall-01"];
        ring[0] = new Pt(ring[0].X - 5.0, ring[0].Y);
        Assert.False(PlanImport.SameLoops(loops, drawn, 0.01));
        drawn = loops.ToDictionary(l => l.Name, l => l.Ring);
        drawn.Remove("import-wall-01-hole-01");
        Assert.False(PlanImport.SameLoops(loops, drawn, 0.01));
    }

    static PlanImport.Opening DoorIn(string door)
    {
        var plan = PlanImport.Parse(@"{ ""schema"": ""forsk.plan_import.v0"",
            ""walls"": [ { ""start"": [0, 0], ""end"": [6000, 0], ""thickness"": 200 } ],
            ""openings"": [ " + door + @" ] }");
        return Assert.Single(PlanImport.Clean(plan).Openings);
    }

    [Fact]
    public void DoorSwing_TheFileDraws_IsCarriedOntoTheHostedDoor()
    {
        // Hinge at 2000, closed at 2900 in the wall, the leaf open toward +y.
        var door = DoorIn(@"{ ""kind"": ""door"", ""hinge"": [2000, 0], ""closed"": [2900, 0], ""open"": [2000, 900] }");
        Assert.True(door.Swings);
        Assert.Equal(-1.0, door.HingeDir.X, 9);
        Assert.Equal(0.0, door.HingeDir.Y, 9);
        Assert.Equal(1.0, door.OpensDir.Y, 9);

        var mirrored = DoorIn(@"{ ""kind"": ""door"", ""hinge"": [2900, 0], ""closed"": [2000, 0], ""open"": [2900, -900] }");
        Assert.Equal(1.0, mirrored.HingeDir.X, 9);
        Assert.Equal(-1.0, mirrored.OpensDir.Y, 9);
    }

    [Theory]
    [InlineData(@"{ ""kind"": ""door"", ""hinge"": [2000, 0], ""closed"": [2900, 0] }")]
    [InlineData(@"{ ""kind"": ""door"", ""hinge"": [2000, 0], ""closed"": [2900, 0], ""open"": [1100, 0] }")]
    [InlineData(@"{ ""kind"": ""window"", ""a"": [2000, 0], ""b"": [2900, 0] }")]
    public void NoSwingDrawn_NoSwingCarried(string opening)
    {
        Assert.False(DoorIn(opening).Swings);
    }

    [Theory]
    [InlineData(0, 1, -1, 0, 0, 1, "R", "in")]
    [InlineData(0, -1, -1, 0, 0, 1, "L", "out")]
    [InlineData(1, 0, 0, 1, -1, 0, "R", "out")]
    [InlineData(-1, 0, 0, 1, 1, 0, "L", "out")]
    public void DoorSwing_ReadsAsHandAndSwing_AgainstTheWallsInward(
        double ix, double iy, double hx, double hy, double ox, double oy, string hand, string swing)
    {
        OpeningTypes.HandSwingFrom(hx, hy, ox, oy, ix, iy, out var gotHand, out var gotSwing);
        Assert.Equal(hand, gotHand);
        Assert.Equal(swing, gotSwing);
    }

    /// <summary>
    /// The plan symbol draws the hinge at HandSign × the frame's X and the leaf
    /// at SwingSign × its Y, with xLeft and yInward from the wall's Inward.
    /// For every wall direction, face and frame orientation, the hand and
    /// swing read from a drawn swing draw that same swing again.
    /// </summary>
    [Fact]
    public void DoorSwing_HandAndSwing_DrawTheSwingAgain_InEveryFrame()
    {
        var signs = new[] { 1.0, -1.0 };
        foreach (var (ux, uy) in new[] { (1.0, 0.0), (0.0, 1.0) })
        foreach (var inwardSign in signs)
        foreach (var hingeSign in signs)
        foreach (var opensSign in signs)
        foreach (var xSign in signs)
        foreach (var ySign in signs)
        {
            double nx = -uy, ny = ux;
            double ix = nx * inwardSign, iy = ny * inwardSign;
            double hx = ux * hingeSign, hy = uy * hingeSign;
            double ox = nx * opensSign, oy = ny * opensSign;
            OpeningTypes.HandSwingFrom(hx, hy, ox, oy, ix, iy, out var hand, out var swing);
            Assert.True(OpeningTypes.TryRead("door", null, hand, swing, null, out var record, out _));

            // The frame as OpeningFacing makes it: X along the wall, Y across, either way round.
            double px = ux * xSign, py = uy * xSign, qx = nx * ySign, qy = ny * ySign;
            var xLeft = px * iy + py * -ix >= 0 ? 1 : -1;
            var yInward = qx * ix + qy * iy >= 0 ? 1 : -1;
            var hingeAt = OpeningTypes.HandSign(record, xLeft);
            var leafAt = OpeningTypes.SwingSign(record, yInward);
            Assert.Equal(hx, hingeAt * px, 9);
            Assert.Equal(hy, hingeAt * py, 9);
            Assert.Equal(ox, leafAt * qx, 9);
            Assert.Equal(oy, leafAt * qy, 9);
        }
    }

    /// <summary>
    /// The real plan the detection was made from. A client drawing, so it is
    /// not in git: this runs only where forsk-private is checked out.
    /// </summary>
    static string Plan1(string file)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, "Documents", "hobby", "forsk-private", "import", "plan1", file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void Plan1_PdfVector_ClosesAroundAllTwelveRooms()
    {
        var json = Plan1("plan1_pdfvector.json");
        if (json == null) return;
        var result = PlanImport.Clean(PlanImport.Parse(json));
        Assert.Equal(54, result.Detected);
        Assert.Equal(47, result.Walls.Count);
        Assert.Equal(12, result.Doors);
        Assert.Equal(10, result.Windows);
        Assert.Equal(0, result.Loose);
        Assert.Equal(4, result.Diagonal);
        Assert.Equal(12, result.Rooms.Count);
        Assert.Equal(0, result.Unlabelled);
        Assert.Equal(0, result.Outside);
        // One outline, and the twelve rooms are the twelve holes the walls close.
        Assert.Equal(1, result.Outlines);
        Assert.Equal(0, result.Blocks);
        // Drawn from the vector file, no end stops short: nothing to close.
        Assert.Equal(0, result.Closed);
        Assert.Equal(12, result.Networks[0].Holes.Count);
        Assert.Equal(54, result.Networks[0].Pieces);
        Assert.Equal(0, result.Uncut);
        WallCleanupTests.AssertSound(result, 5.0);
    }
}
