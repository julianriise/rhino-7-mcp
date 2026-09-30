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
    public void Thickness_RoundsToTenAndKeepsTheDetectedValue(double detected, double rounded)
    {
        var wall = Assert.Single(PlanImport.Clean(PlanOf(W(0, 0, 4000, 0, detected))).Walls);
        Assert.Equal(rounded, wall.Thickness);
        Assert.Equal(detected, wall.Detected);
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

    [Fact]
    public void PiecesEitherSideOfAnOpening_BecomeOneWallThatHostsIt()
    {
        var plan = PlanOf(W(0, 0, 2000, 0), W(3000, 0, 6000, 0));
        plan.Openings.Add(Opening("window", 2000, 0, 3000, 0));
        var result = PlanImport.Clean(plan);
        var wall = Assert.Single(result.Walls);
        Assert.Equal(0.0, wall.A.X, 6);
        Assert.Equal(6000.0, wall.B.X, 6);
        var window = Assert.Single(result.Openings);
        Assert.Equal(0, window.Host);
        Assert.Null(window.Note);
        Assert.Equal(220.0, window.Depth);
        Assert.Equal(1, result.Windows);
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
        var line = Assert.Single(result.Review);
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
        var plan = PlanOf(W(0, 0, 6000, 0));
        plan.Openings.Add(Opening("opening", 2000, 0, 3300, 0));
        var result = PlanImport.Clean(plan);
        Assert.Equal("door", Assert.Single(result.Openings).Kind);
        Assert.Contains("a passage with no door drawn", Assert.Single(result.Review));
    }

    [Fact]
    public void OpeningOnADiagonalWall_IsFlagged()
    {
        var plan = PlanOf(W(0, 0, 3000, 3000));
        plan.Openings.Add(Opening("window", 1000, 1000, 2000, 2000));
        var result = PlanImport.Clean(plan);
        Assert.Equal(0, Assert.Single(result.Openings).Host);
        Assert.Contains("on a diagonal wall", Assert.Single(result.Review));
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
        var before = RoomDetect.Footprints(LooseCorners().Walls.Select(PlanImport.Ring).ToList(), 1.0);
        Assert.True(Math.Abs(RoomDetect.Area(before[0])) < 5 * M2);

        var result = PlanImport.Clean(LooseCorners());
        Assert.Equal(4, result.Walls.Count);
        Assert.Equal(8, result.Joined);
        var footprint = Assert.Single(RoomDetect.Footprints(result.Walls.Select(PlanImport.Ring).ToList(), 1.0));
        Assert.Equal(5.2 * 4.2, RoomDetect.Area(footprint) / M2, 6);
        Assert.Equal(4, footprint.Count);
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
        Assert.Contains("Dropped wall at 1.5, 0.1 m", Assert.Single(result.Review));
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
            "Imported 5 walls, 1 door, 1 window, 2 rooms. Scale 1:50 read from the plan, not confirmed: "
            + "check it with two points and a known length. Review: 1 room without a label.",
            PlanImport.Message(result, PlanImport.ScaleLine("detected", "1:50")));
    }

    /// <summary>
    /// The file the server's Tectly adapter writes from its synthetic fixture
    /// (server/tests/fixtures): the same bytes on both sides of the schema.
    /// </summary>
    [Fact]
    public void AdapterOutput_ReadsAndCleansOnThePluginSide()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "plan_import_synthetic.json");
        var plan = PlanImport.Parse(File.ReadAllText(path));
        Assert.Equal("tectly", plan.Vendor);
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
    [InlineData(@"{ ""schema"": ""tectly.v1"", ""walls"": [] }", "Not a forsk.plan_import.v0 file (schema tectly.v1).")]
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
    [InlineData("detected", "1:100", "Scale 1:100 read from the plan, not confirmed: check it with two points and a known length.")]
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

        var wall = PlanImport.Ring(W(1000, -2000, 4900, -2000, 250));
        var scaledWall = PlanImport.RescaleWall(now, factor, wall, 247, 250, 10, out var thickness);
        Assert.Equal(250.0, thickness);
        Assert.Equal(1000 * factor, (scaledWall[0].X + scaledWall[3].X) / 2, 6);
        Assert.Equal(-2000 * factor, (scaledWall[0].Y + scaledWall[3].Y) / 2, 6);

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
        var stamped = 250.0;
        var factors = new[] { 1.037, 0.5, 2.0, 0.998, 1.25, 0.013, 3.3, 0.73, 1.0001, 1.0 };
        for (var round = 0; round < 5; round++)
        {
            foreach (var factor in factors)
            {
                corner = PlanImport.Rescale(now, factor, corner);
                ring = PlanImport.RescaleWall(now, factor, ring, 247, stamped, 10, out stamped);
                now = At(factor);
            }
        }
        Assert.Equal(42012.0, corner.X, 6);
        Assert.Equal(-29700.0, corner.Y, 6);
        Assert.Equal(250.0, stamped);
        var start = PlanImport.Ring(W(1000, -2000, 4900, -2000, 250));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(start[i].X, ring[i].X, 6);
            Assert.Equal(start[i].Y, ring[i].Y, 6);
        }
    }

    [Fact]
    public void RescaledWall_RoundsItsThicknessFromTheDetectedValue()
    {
        var ring = PlanImport.Ring(W(0, 0, 4000, 0, 250));
        var scaled = PlanImport.RescaleWall(At(1.0), 1.1, ring, 247, 250, 10, out var thickness);
        Assert.Equal(270.0, thickness);
        Assert.Equal(270.0, scaled.Max(p => p.Y) - scaled.Min(p => p.Y), 6);
        Assert.Equal(4400.0, scaled.Max(p => p.X) - scaled.Min(p => p.X), 6);

        var back = PlanImport.RescaleWall(At(1.1), 1.0, scaled, 247, 270, 10, out thickness);
        Assert.Equal(250.0, thickness);
        Assert.Equal(250.0, back.Max(p => p.Y) - back.Min(p => p.Y), 6);
    }

    [Fact]
    public void WallTheUserReshaped_ScalesAsDrawn()
    {
        // Stamped 250 by the import, since made 300 thick by hand.
        var ring = PlanImport.Ring(W(0, 0, 4000, 0, 300));
        var scaled = PlanImport.RescaleWall(At(1.0), 1.1, ring, 247, 250, 10, out var thickness);
        Assert.True(double.IsNaN(thickness));
        Assert.Equal(330.0, scaled.Max(p => p.Y) - scaled.Min(p => p.Y), 6);

        // A diagonal drawn on the wall layer has no import stamp at all.
        var drawn = new List<Pt> { new Pt(0, 0), new Pt(1000, 1000), new Pt(900, 1100), new Pt(-100, 100) };
        var moved = PlanImport.RescaleWall(At(1.0), 2.0, drawn, 0, 0, 10, out thickness);
        Assert.True(double.IsNaN(thickness));
        Assert.Equal(2000.0, moved[1].X, 9);
        Assert.Equal(2200.0, moved[2].Y, 9);
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

    [Fact]
    public void Footprints_SeparateWallRectangles_GiveTheOutlineTheyClose()
    {
        var rings = new List<List<Pt>>
        {
            Rect(0, 0, 4200, 200), Rect(0, 4000, 4200, 4200), Rect(0, 0, 200, 4200), Rect(4000, 0, 4200, 4200),
            // A partition that only touches the walls, and a column standing free in the room.
            Rect(2000, 200, 2100, 4000), Rect(3000, 1000, 3300, 1300)
        };
        var footprint = Assert.Single(RoomDetect.Footprints(rings, 1.0));
        Assert.Equal(4.2 * 4.2, RoomDetect.Area(footprint) / M2, 6);
        Assert.Equal(4, footprint.Count);
    }

    [Fact]
    public void Footprints_OpenRing_FollowsTheWalls()
    {
        var rings = new List<List<Pt>> { Rect(0, 0, 4200, 200), Rect(0, 0, 200, 4200), Rect(4000, 0, 4200, 4200) };
        var footprint = Assert.Single(RoomDetect.Footprints(rings, 1.0));
        Assert.Equal((4200.0 * 200 + 2 * 4000.0 * 200) / M2, RoomDetect.Area(footprint) / M2, 6);
    }

    [Fact]
    public void Footprints_OutlinesApart_StayAsDrawn()
    {
        var rings = new List<List<Pt>> { Rect(0, 0, 4000, 3000), Rect(6000, 0, 9000, 3000) };
        var footprints = RoomDetect.Footprints(rings, 1.0);
        Assert.Equal(2, footprints.Count);
        Assert.All(footprints, f => Assert.Equal(4, f.Count));
        Assert.Equal(new[] { 9.0, 12.0 }, footprints.Select(f => Math.Round(RoomDetect.Area(f) / M2, 6)).OrderBy(a => a));
    }

    /// <summary>
    /// The real plan both detections were made from. A client drawing, so it
    /// is not in git: this runs only where forsk-private is checked out.
    /// </summary>
    static string Plan1(string file)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, "Documents", "hobby", "forsk-private", "import", "plan1", file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void Plan1_Tectly_KeepsEveryOpeningAndReportsTheOpenWalls()
    {
        var json = Plan1("plan1_forsk.json");
        if (json == null) return;
        var result = PlanImport.Clean(PlanImport.Parse(json));
        Assert.Equal(41, result.Detected);
        Assert.Equal(30, result.Walls.Count);
        Assert.Equal(7, result.Doors);
        Assert.Equal(12, result.Windows);
        // One of Tectly's two false windows lies across its wall.
        Assert.Equal(1, result.Loose);
        Assert.Equal(11, result.Rooms.Count);
        Assert.Equal(3, result.Unlabelled);
        Assert.Equal(0, result.Diagonal);
        // Tectly missed the diagonal bay walls: the outline is open and says so.
        Assert.True(result.Outside > 0);
        Assert.Empty(result.Dropped);
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
        Assert.Single(RoomDetect.Footprints(result.Walls.Select(PlanImport.Ring).ToList(), 1.0));
    }
}
