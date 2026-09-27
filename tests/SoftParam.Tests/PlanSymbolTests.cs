using System;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Plan marks stay in the opening frame. Hand and swing use the same signs
/// as the 3D leaf. 1:50 still returns the 1:100 set.
/// </summary>
public class PlanSymbolTests
{
    static OpeningTypes.PlanFrame Frame(double sill = 0, double head = 2100, double cut = 1200)
    {
        return new OpeningTypes.PlanFrame
        {
            OuterHalf = 500,
            InnerHalf = 450,
            HalfThick = 100,
            Sill = sill,
            Head = head,
            CutZ = cut,
            YInward = 1,
            XLeft = 1
        };
    }

    static OpeningTypes.Record Read(string kind, string type, string hand, string swing)
    {
        Assert.True(OpeningTypes.TryRead(kind, type, hand, swing, out var record, out var why));
        Assert.Equal("", why);
        return record;
    }

    [Fact]
    public void EveryId_ReturnsMarks_AndDetailFallsBack()
    {
        var frame = Frame();
        foreach (var def in OpeningTypes.All)
        {
            var record = Read(def.Kind, def.Id, null, null);
            var marks = OpeningTypes.PlanSymbol(record, "1:100", frame);
            Assert.NotEmpty(marks);
            var other = OpeningTypes.PlanSymbol(record, "1:50", frame);
            Assert.Equal(marks.Count, other.Count);
        }
    }

    [Fact]
    public void HingedSingle_ArcRadiusIsLeafWidth_AndFlipsMirror()
    {
        var frame = Frame();
        var leaf = 2.0 * (frame.InnerHalf - 1.0);
        var left = OpeningTypes.PlanSymbol(Read("door", "door.hinged_single", "L", "in"), "1:100", frame);
        var arc = Assert.Single(left, m => m.Shape == "arc");
        Assert.Equal(leaf, arc.Radius, 6);
        var line = Assert.Single(left, m => m.Part == "leaf");
        Assert.Equal(OpeningTypes.HandSign("L", frame.XLeft) * (frame.InnerHalf - 1.0), line.X0, 6);
        Assert.Equal(0, line.Y0, 6);
        Assert.Equal(OpeningTypes.SwingSign(Read("door", "door.hinged_single", "L", "in"), frame.YInward) * leaf, line.Y1, 6);
        Assert.False(line.Dashed);

        var right = OpeningTypes.PlanSymbol(Read("door", "door.hinged_single", "R", "in"), "1:100", frame);
        AssertMirrorX(left, right);
        var swung = OpeningTypes.PlanSymbol(Read("door", "door.hinged_single", "L", "out"), "1:100", frame);
        AssertMirrorY(left, swung);
    }

    [Fact]
    public void HingedDouble_TwoArcs_EachItsOwnLeaf()
    {
        var frame = Frame();
        var span = (frame.InnerHalf - 1.0) - 3.0;
        var marks = OpeningTypes.PlanSymbol(Read("door", "door.hinged_double", null, "in"), "1:100", frame);
        var arcs = marks.Where(m => m.Shape == "arc").ToList();
        Assert.Equal(2, arcs.Count);
        Assert.All(arcs, arc => Assert.Equal(span, arc.Radius, 6));
        Assert.Equal(2, marks.Count(m => m.Part == "leaf"));
    }

    [Fact]
    public void Sliding_NoArc_ArrowTowardPark()
    {
        var frame = Frame();
        var record = Read("door", "door.sliding", "L", null);
        var marks = OpeningTypes.PlanSymbol(record, "1:100", frame);
        Assert.DoesNotContain(marks, m => m.Shape == "arc");
        var park = OpeningTypes.HandSign(record, frame.XLeft);
        var leaf = Assert.Single(marks, m => m.Part == "leaf");
        var tip = park > 0 ? Math.Max(leaf.X0, leaf.X1) : Math.Min(leaf.X0, leaf.X1);
        Assert.True(park * tip > frame.InnerHalf - 1.0);
        Assert.Equal(2, marks.Count(m => m.Part == "arrow"));
        Assert.All(marks.Where(m => m.Part == "arrow"), arrow =>
        {
            var ax = park > 0 ? Math.Max(arrow.X0, arrow.X1) : Math.Min(arrow.X0, arrow.X1);
            Assert.Equal(tip, ax, 6);
        });

        var flipped = OpeningTypes.PlanSymbol(Read("door", "door.sliding", "R", null), "1:100", frame);
        AssertMirrorX(marks, flipped);
    }

    [Fact]
    public void Pocket_DashedLeaf_InsideTheWallOnTheParkSide()
    {
        var frame = Frame();
        var record = Read("door", "door.pocket", "L", null);
        var marks = OpeningTypes.PlanSymbol(record, "1:100", frame);
        var leaf = Assert.Single(marks, m => m.Part == "leaf");
        Assert.True(leaf.Dashed);
        Assert.Equal(0, leaf.Y0, 6);
        Assert.Equal(0, leaf.Y1, 6);
        var pocket = marks.Where(m => m.Part == "pocket").ToList();
        Assert.Equal(4, pocket.Count);
        Assert.All(pocket, m => Assert.False(m.Dashed));
        Assert.All(pocket, m =>
        {
            Assert.True(Math.Abs(m.Y0) < frame.HalfThick);
            Assert.True(Math.Abs(m.Y1) < frame.HalfThick);
        });
        Assert.Contains(pocket, m => Math.Abs(m.Y0) > frame.HalfThick * 0.5);
        var minX = pocket.Min(m => Math.Min(m.X0, m.X1));
        var maxX = pocket.Max(m => Math.Max(m.X0, m.X1));
        Assert.True(leaf.X0 >= minX - 0.1 && leaf.X0 <= maxX + 0.1);
        Assert.True(leaf.X1 >= minX - 0.1 && leaf.X1 <= maxX + 0.1);
        var park = OpeningTypes.HandSign(record, frame.XLeft);
        Assert.True(park * leaf.X0 > frame.InnerHalf);
        Assert.True(park * leaf.X1 > frame.InnerHalf);
        Assert.DoesNotContain(marks, m => m.Shape == "arc");

        var right = OpeningTypes.PlanSymbol(Read("door", "door.pocket", "R", null), "1:100", frame);
        AssertMirrorX(marks, right);
    }

    [Fact]
    public void Windows_SillsAtBothFaces_AndGlassCount()
    {
        var frame = Frame(900, 2100, 1200);
        var fix = OpeningTypes.PlanSymbol(Read("window", "window.fixed", null, null), "1:100", frame);
        AssertSills(fix, frame);
        Assert.Single(fix, m => m.Part == "glass");
        Assert.All(fix, m => Assert.False(m.Dashed));

        var hung = OpeningTypes.PlanSymbol(Read("window", "window.side_hung", "L", "in"), "1:100", frame);
        AssertSills(hung, frame);
        Assert.Equal(2, hung.Count(m => m.Part == "glass"));
        var top = OpeningTypes.PlanSymbol(Read("window", "window.top_hung", null, "in"), "1:100", frame);
        Assert.Equal(2, top.Count(m => m.Part == "glass"));

        var hand = OpeningTypes.PlanSymbol(Read("window", "window.side_hung", "R", "in"), "1:100", frame);
        Assert.Equal(hung.Count, hand.Count);
        var swung = OpeningTypes.PlanSymbol(Read("window", "window.side_hung", "L", "out"), "1:100", frame);
        var sashIn = hung.Where(m => m.Part == "glass").Single(m => Math.Abs(m.Y0) > 1);
        var sashOut = swung.Where(m => m.Part == "glass").Single(m => Math.Abs(m.Y0) > 1);
        Assert.Equal(-sashIn.Y0, sashOut.Y0, 6);
    }

    [Fact]
    public void CutRule_DoorsStaySolid_WindowsAboveAreDashed()
    {
        var door = Read("door", "door.hinged_single", "L", "in");
        Assert.False(OpeningTypes.AboveCut(door, 0, 2100, 1200));
        Assert.False(OpeningTypes.AboveCut(door, 1300, 2100, 1200));

        var window = Read("window", "window.fixed", null, null);
        Assert.False(OpeningTypes.AboveCut(window, 900, 2100, 1200));
        Assert.True(OpeningTypes.AboveCut(window, 1300, 2100, 1200));
        Assert.True(OpeningTypes.AboveCut(window, 1200, 2100, 1200));
        Assert.False(OpeningTypes.AboveCut(window, 0, 1000, 1200));
        Assert.False(OpeningTypes.AboveCut(window, 900, 1200, 1200));

        var above = OpeningTypes.PlanSymbol(window, "1:100", Frame(1300, 2100, 1200));
        Assert.All(above, m => Assert.True(m.Dashed));
        var cut = OpeningTypes.PlanSymbol(window, "1:100", Frame(900, 2100, 1200));
        Assert.All(cut, m => Assert.False(m.Dashed));
    }

    [Fact]
    public void AnnotationHeight_IsPaperTimesScale()
    {
        Assert.Equal(0, OpeningTypes.PlanAnnotationHeight(0));
        Assert.Equal(250, OpeningTypes.PlanAnnotationHeight(100));
        Assert.Equal(500, OpeningTypes.PlanAnnotationHeight(200));
    }

    [Fact]
    public void WallCenter_MovesOffTheFaceOntoTheMeasuredAxis()
    {
        double x, y;
        OpeningTypes.WallCenter(2600, 0, 1800, 100, 0, 1, out x, out y);
        Assert.Equal(2600, x, 6);
        Assert.Equal(100, y, 6);
        OpeningTypes.WallCenter(x, y, 1800, 100, 0, 1, out var x2, out var y2);
        Assert.Equal(y, y2, 6);
        Assert.Equal(x, x2, 6);
    }

    [Fact]
    public void InteriorPoint_UsesCentroid_OrAPointInsideAnL()
    {
        double x, y;
        Assert.True(OpeningTypes.TryInteriorPoint(
            new[] { 0d, 10, 10, 0 }, new[] { 0d, 0, 6, 6 }, out x, out y));
        Assert.Equal(5, x, 6);
        Assert.Equal(3, y, 6);

        var ex = new[] { 0d, 6, 6, 2, 2, 0 };
        var ey = new[] { 0d, 0, 2, 2, 6, 6 };
        Assert.True(OpeningTypes.TryInteriorPoint(ex, ey, out x, out y));
        Assert.True(OpeningTypes.PointInPolygon(x, y, ex, ey));
        Assert.False(OpeningTypes.PointInPolygon(4, 4, ex, ey));
    }

    [Fact]
    public void WindowSills_SpanTheVoid_AndDoorsSitOnTheFaces()
    {
        var frame = Frame();
        frame.VoidHalf = 600;
        frame.HalfThick = 100;
        var window = OpeningTypes.PlanSymbol(Read("window", "window.fixed", null, null), "1:100", frame);
        var sills = window.Where(m => m.Part == "sill").ToList();
        Assert.Equal(2, sills.Count);
        Assert.All(sills, m =>
        {
            Assert.Equal(-600, Math.Min(m.X0, m.X1), 6);
            Assert.Equal(600, Math.Max(m.X0, m.X1), 6);
            Assert.Equal(Math.Abs(m.Y0), frame.HalfThick, 6);
        });

        var door = new System.Collections.Generic.List<OpeningTypes.PlanMark>();
        OpeningTypes.AddWallFrame(door, frame);
        Assert.Equal(2, door.Count(m => m.Part == "frame"));
        Assert.Equal(2, door.Count(m => m.Part == "jamb"));
        Assert.All(door.Where(m => m.Part == "frame"), m => Assert.Equal(frame.HalfThick, Math.Abs(m.Y0), 6));
        Assert.All(door.Where(m => m.Part == "jamb"), m =>
        {
            Assert.Equal(frame.VoidHalf, Math.Abs(m.X0), 6);
            Assert.Equal(-frame.HalfThick, Math.Min(m.Y0, m.Y1), 6);
            Assert.Equal(frame.HalfThick, Math.Max(m.Y0, m.Y1), 6);
        });
    }

    [Theory]
    [InlineData(34.55, 35)]
    [InlineData(4.2, 5)]
    [InlineData(50, 50)]
    [InlineData(50.01, 60)]
    [InlineData(100, 100)]
    [InlineData(100.01, 125)]
    [InlineData(116.1, 125)]
    [InlineData(137, 150)]
    [InlineData(500, 500)]
    [InlineData(500.01, 550)]
    [InlineData(0, 0)]
    public void RoundScaleUp_StandardSteps(double need, int scale)
    {
        Assert.Equal(scale, OpeningTypes.RoundScaleUp(need));
    }

    [Fact]
    public void ClampAlong_KeepsClearOfCornerWall()
    {
        // 8 m outer face, 900 door: 50 mm margin inside the 200 mm cross wall.
        Assert.Equal(7300.0 / 8000, OpeningTypes.ClampAlong(8000, 900, 0.99, 50, 200), 9);
        Assert.Equal(7500.0 / 8000, OpeningTypes.ClampAlong(8000, 900, 0.99, 50, 0), 9);
        Assert.Equal(0.5, OpeningTypes.ClampAlong(8000, 900, 0.5, 50, 200), 9);
        Assert.True(double.IsNaN(OpeningTypes.ClampAlong(1000, 900, 0.5, 50, 200)));
    }

    [Fact]
    public void Pocket_ParksWhereTheWallHasRoom()
    {
        var frame = Frame();
        var reach = OpeningTypes.PocketReach(frame);
        Assert.Equal(1350, reach, 6);
        var marks = OpeningTypes.PlanSymbol(Read("door", "door.pocket", "L", null), "1:100", frame);
        Assert.Equal(reach, marks.Max(m => Math.Max(Math.Abs(m.X0), Math.Abs(m.X1))), 6);

        // L parks +X here. East end of the garage: 70 mm to the wall end.
        Assert.True(OpeningTypes.PocketHand("L", 1, 1880, 70, reach, out var hand));
        Assert.Equal("R", hand);
        Assert.True(OpeningTypes.PocketHand("L", 1, 0, 5000, reach, out hand));
        Assert.Equal("L", hand);
        Assert.True(OpeningTypes.PocketHand("R", 1, 1400, 0, reach, out hand));
        Assert.Equal("R", hand);
        Assert.False(OpeningTypes.PocketHand("L", 1, 900, 900, reach, out _));
    }

    static OpeningTypes.PlanRegion Band()
    {
        // 8 x 4 m garage band, 200 mm walls: outer ring plus the room hole.
        var band = new OpeningTypes.PlanRegion();
        band.Xs.Add(new double[] { 0, 8000, 8000, 0 });
        band.Ys.Add(new double[] { 0, 0, 4000, 4000 });
        band.Xs.Add(new double[] { 200, 7800, 7800, 200 });
        band.Ys.Add(new double[] { 200, 200, 3800, 3800 });
        return band;
    }

    [Fact]
    public void Room_BoundedByWalls()
    {
        var walls = new[] { Band() };
        Assert.True(OpeningTypes.InRegions(100, 2000, walls));
        Assert.False(OpeningTypes.InRegions(4000, 2000, walls));
        Assert.False(OpeningTypes.InRegions(9000, 2000, walls));

        var room = new[] { 200.0, 7800, 7800, 200 };
        var roomY = new[] { 200.0, 200, 3800, 3800 };
        Assert.Equal(1.0, OpeningTypes.BoundedFraction(room, roomY, walls, 50), 9);
        // Clockwise input scores the same.
        Assert.Equal(1.0, OpeningTypes.BoundedFraction(
            room.Reverse().ToArray(), roomY.Reverse().ToArray(), walls, 50), 9);

        // The old office fixture: a 4 x 5 m rectangle in open space.
        var loose = new[] { 9500.0, 13500, 13500, 9500 };
        var looseY = new[] { 1500.0, 1500, 6500, 6500 };
        Assert.Equal(0.0, OpeningTypes.BoundedFraction(loose, looseY, walls, 50), 9);

        // Half inside the room: two sides float, so it is not a room.
        var half = new[] { 1000.0, 3000, 3000, 1000 };
        var halfY = new[] { 200.0, 200, 2000, 2000 };
        Assert.True(OpeningTypes.BoundedFraction(half, halfY, walls, 50) < 0.9);
    }

    [Theory]
    [InlineData(100, 250)]
    [InlineData(200, 500)]
    public void PlanText_PrintsAt2_5mm(int scale, double model)
    {
        var height = OpeningTypes.PlanAnnotationHeight(scale);
        Assert.Equal(model, height, 6);
        Assert.Equal(2.5, OpeningTypes.PaperTextHeight(height, scale, false), 6);
        // Layout-space scaling prints the model height on paper: the old bug.
        Assert.Equal(model, OpeningTypes.PaperTextHeight(height, scale, true), 6);
        Assert.Equal(0, OpeningTypes.PaperTextHeight(height, 0, false));
    }

    [Fact]
    public void RoomTag_AndViewTitle()
    {
        Assert.Equal("ca. 12,4 m²", OpeningTypes.RoomTag(12400000));
        Assert.Equal("ca. 20,2 m²", OpeningTypes.RoomTag(20160000));
        Assert.Equal("Plan 1. etg", OpeningTypes.ViewTitle("plan", 0));
        Assert.Equal("Plan 2. etg", OpeningTypes.ViewTitle("plan", 1));
        Assert.Equal("Fasade mot sør", OpeningTypes.ViewTitle("south", 0));
        Assert.Equal("Fasade mot nord", OpeningTypes.ViewTitle("north", 0));
        Assert.Equal("Fasade mot øst", OpeningTypes.ViewTitle("east", 0));
        Assert.Equal("Fasade mot vest", OpeningTypes.ViewTitle("west", 0));
    }

    [Theory]
    [InlineData(20, 1, 50.0)]
    [InlineData(30, 2, 66.667)]
    [InlineData(50, 3, 60.0)]
    [InlineData(55, 3, 54.5455)]
    [InlineData(60, 3, 50.0)]
    [InlineData(75, 5, 66.667)]
    [InlineData(100, 5, 50.0)]
    [InlineData(125, 5, 40.0)]
    [InlineData(150, 10, 66.667)]
    [InlineData(200, 10, 50.0)]
    [InlineData(500, 30, 60.0)]
    [InlineData(525, 30, 57.143)]
    [InlineData(550, 30, 54.5455)]
    [InlineData(600, 30, 50.0)]
    [InlineData(1000, 50, 50.0)]
    [InlineData(2000, 100, 50.0)]
    public void ScaleBar_LengthIn40To80mm(int scale, int meters, double paper)
    {
        Assert.Equal(meters, OpeningTypes.ScaleBarMeters(scale));
        var mm = OpeningTypes.ScaleBarPaperMm(meters, scale);
        Assert.Equal(paper, mm, 2);
        Assert.InRange(mm, 40.0, 80.0);
        Assert.Contains(meters, OpeningTypes.ScaleBarLengthsM);
    }

    [Fact]
    public void ScaleBar_EveryScaleFrom13To2500Fits()
    {
        for (var scale = 13; scale <= 2500; scale++)
        {
            var meters = OpeningTypes.ScaleBarMeters(scale);
            var mm = OpeningTypes.ScaleBarPaperMm(meters, scale);
            Assert.InRange(mm, 40.0, 80.0);
            // No other listed length is nearer 60 mm inside the band.
            foreach (var other in OpeningTypes.ScaleBarLengthsM)
            {
                var alt = OpeningTypes.ScaleBarPaperMm(other, scale);
                if (alt >= 40.0 && alt <= 80.0)
                    Assert.True(Math.Abs(mm - 60.0) <= Math.Abs(alt - 60.0) + 1e-9, $"1:{scale} {meters} m vs {other} m");
            }
        }
        Assert.Equal(0, OpeningTypes.ScaleBarMeters(0));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    [InlineData(5, 5)]
    [InlineData(10, 5)]
    [InlineData(20, 4)]
    [InlineData(30, 5)]
    [InlineData(50, 5)]
    [InlineData(100, 5)]
    public void ScaleBar_FourOrFiveRoundSegments(int meters, int segments)
    {
        Assert.Equal(segments, OpeningTypes.ScaleBarSegments(meters));
        var each = meters / (double)segments;
        Assert.Equal(Math.Round(each * 10) / 10, each, 9);
        Assert.Equal(meters + " m", OpeningTypes.ScaleBarLabel(meters));
    }

    [Fact]
    public void TitleCells_DropEmptyAndSplitTheWidth()
    {
        var cells = OpeningTypes.TitleCells(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>("Drawing", "Plan 1. etg"),
            new System.Collections.Generic.KeyValuePair<string, string>("Scale", "1:50"),
            new System.Collections.Generic.KeyValuePair<string, string>("Project", ""),
            new System.Collections.Generic.KeyValuePair<string, string>("Client", "—"),
            new System.Collections.Generic.KeyValuePair<string, string>("Address", "  "),
            new System.Collections.Generic.KeyValuePair<string, string>("Date", "2026-09-27")
        });
        Assert.Equal(new[] { "Drawing", "Scale", "Date" }, cells.Select(c => c.Key).ToArray());
        var widths = OpeningTypes.TitleCellWidths(cells, 280);
        Assert.Equal(280, widths.Sum(), 6);
        Assert.True(widths[0] > widths[1]);
        Assert.Empty(OpeningTypes.TitleCellWidths(new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>(), 280));
    }

    static void AssertSills(System.Collections.Generic.List<OpeningTypes.PlanMark> marks, OpeningTypes.PlanFrame frame)
    {
        var sills = marks.Where(m => m.Part == "sill").ToList();
        Assert.Equal(2, sills.Count);
        Assert.Contains(sills, m => Nearly(m.Y0, frame.HalfThick) && Nearly(m.Y1, frame.HalfThick));
        Assert.Contains(sills, m => Nearly(m.Y0, -frame.HalfThick) && Nearly(m.Y1, -frame.HalfThick));
    }

    static void AssertMirrorX(System.Collections.Generic.List<OpeningTypes.PlanMark> a, System.Collections.Generic.List<OpeningTypes.PlanMark> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Part, b[i].Part);
            Assert.Equal(-a[i].X0, b[i].X0, 6);
            Assert.Equal(a[i].Y0, b[i].Y0, 6);
            Assert.Equal(-a[i].X1, b[i].X1, 6);
            Assert.Equal(a[i].Y1, b[i].Y1, 6);
            if (a[i].Shape == "arc")
            {
                Assert.Equal(-a[i].Cx, b[i].Cx, 6);
                Assert.Equal(a[i].Radius, b[i].Radius, 6);
            }
        }
    }

    static void AssertMirrorY(System.Collections.Generic.List<OpeningTypes.PlanMark> a, System.Collections.Generic.List<OpeningTypes.PlanMark> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(-a[i].Y0, b[i].Y0, 6);
            Assert.Equal(a[i].X0, b[i].X0, 6);
            Assert.Equal(-a[i].Y1, b[i].Y1, 6);
            Assert.Equal(a[i].X1, b[i].X1, 6);
        }
    }

    static bool Nearly(double a, double b)
    {
        return Math.Abs(a - b) < 1e-6;
    }
}
