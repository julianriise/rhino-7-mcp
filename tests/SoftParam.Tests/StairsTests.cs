using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// v3 R5: the straight stair. Equal risers from the rise, the comfort rule,
/// the sawtooth body, the plan symbol's cut and dashes, the record, and the
/// placement along a room face.
/// </summary>
public class StairsTests
{
    [Fact]
    public void Rise2880_Is16RisersOf180()
    {
        var f = Stairs.Plan(2880, 180, 260, 900);
        Assert.Equal(16, f.Risers);
        Assert.Equal(180, f.Riser);
        Assert.Equal(16, f.Treads);
        Assert.Equal(4160, f.Run);
        Assert.Equal(620, f.Rule);
        Assert.False(f.Steep);
        Assert.False(f.Shallow);
        Assert.Null(Stairs.Comfort(f));
        Assert.Equal("16 × 180/260", Stairs.Sizes(f));
    }

    [Fact]
    public void Rise2750_Is16EqualRisers_LastLandsOnTop()
    {
        var f = Stairs.Plan(2750, 180, 260, 900);
        Assert.Equal(16, f.Risers);
        Assert.Equal(171.875, f.Riser);
        Assert.Equal(2750, Stairs.StepTop(f, 16));
        for (var i = 1; i <= 16; i++)
            Assert.Equal(171.875, Stairs.StepTop(f, i) - Stairs.StepTop(f, i - 1), 9);
        Assert.Equal("16 × 172/260", Stairs.Sizes(f));
    }

    [Fact]
    public void WallHeightChange_ReplansCount_KeepsRisersEqual()
    {
        // The slab hangs below the walking surface, so the rise is the wall top.
        // 3000 / 180 ceils to 17; 2400 / 180 ceils to 14. The last riser lands on that top.
        var before = Stairs.Plan(Stairs.AutoRise(new double[] { 3000, 3000, 2400 }, 3000), 180, 260, 900);
        Assert.Equal(3000, before.Rise);
        Assert.Equal(17, before.Risers);
        Assert.Equal(17, before.Treads);
        Assert.Equal(17 * 260, before.Run);
        double top = 0;
        foreach (var p in Stairs.Profile(before)) if (p.Y > top) top = p.Y;
        Assert.Equal(before.Rise, top, 6);
        Assert.Equal(before.Rise, Stairs.StepTop(before, before.Risers));
        var after = Stairs.Plan(Stairs.AutoRise(new double[] { 2400, 2400, 3000 }, 3000), 180, 260, 900);
        Assert.Equal(2400, after.Rise);
        Assert.Equal(14, after.Risers);
        Assert.Equal(2400 / 14.0, after.Riser);
        Assert.Equal(after.Rise, Stairs.StepTop(after, after.Risers));
    }

    [Fact]
    public void AutoRise_NoWalls_UsesFallback()
    {
        Assert.Equal(3000, Stairs.AutoRise(new double[0], 3000));
        Assert.Equal(3000, Stairs.AutoRise(new double[] { 3000 }, 3000));
    }

    [Fact]
    public void CustomRiser200_BuildsButFlagsSteep()
    {
        var f = Stairs.Plan(2800, 200, 260, 900);
        Assert.Equal(14, f.Risers);
        Assert.Equal(200, f.Riser);
        Assert.True(f.Steep);
        Assert.Equal("steep: 2R+G = 660", Stairs.Comfort(f));
        Assert.Equal("Added a straight stair, 14 steps of 200 (steep: 2R+G = 660).", Stairs.Receipt("Added", f));
    }

    [Fact]
    public void Riser185_WithinRule_StillSteep()
    {
        var f = Stairs.Plan(185 * 15, 185, 250, 900);
        Assert.Equal(620, f.Rule);
        Assert.Equal("steep: steps of 185", Stairs.Comfort(f));
    }

    [Fact]
    public void Narrow_AndShallow_AreFlagged()
    {
        var f = Stairs.Plan(2880, 150, 290, 750);
        Assert.Equal("shallow: 2R+G = 578, narrow: 750 wide", Stairs.Comfort(f));
    }

    [Fact]
    public void Receipt_Plain()
    {
        var f = Stairs.Plan(2880, 180, 260, 900);
        Assert.Equal("Added a straight stair along the north wall, 16 steps of 180.", Stairs.Receipt("Added", f, "along the north wall"));
    }

    [Theory]
    [InlineData(180)]   // one step only
    [InlineData(-1)]
    public void TooSmallRise_IsRefused(double rise)
    {
        Assert.Throws<ArgumentException>(() => Stairs.Plan(rise, 180, 260, 900));
    }

    [Fact]
    public void Profile_IsClosedSawtoothFromTheFloor()
    {
        var f = Stairs.Plan(2880, 180, 260, 900);
        var p = Stairs.Profile(f);
        Assert.Equal(2 + 2 * f.Treads, p.Count);
        Assert.Equal(new Pt(0, 0), p[0]);
        Assert.Equal(new Pt(0, 180), p[1]);
        Assert.Equal(new Pt(260, 180), p[2]);
        // The last riser is the vertical at the end of the tread below, up to the full rise, then one going of tread.
        Assert.Equal(new Pt(15 * 260, 15 * 180), p[2 * 15]);
        Assert.Equal(new Pt(15 * 260, 2880), p[2 * 16 - 1]);
        Assert.Equal(new Pt(16 * 260, 2880), p[^2]);
        Assert.Equal(new Pt(16 * 260, 0), p[^1]);
        Assert.Equal(f.Rise, p.Max(pt => pt.Y));
        // Area: the treads' columns, 260 × (180 + 360 + … + 2880).
        double area = 0;
        for (var i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        Assert.Equal(260.0 * 180 * (16 * 17 / 2), Math.Abs(area) / 2, 6);
    }

    [Fact]
    public void Cut_AtTreadWhoseTopPasses1200_StepsAboveDashed()
    {
        var f = Stairs.Plan(2880, 180, 260, 900);
        Assert.Equal(7, Stairs.CutTread(f, ForskPlanCut.AboveFloorMm));
        var marks = Stairs.PlanSymbol(f, ForskPlanCut.AboveFloorMm, 50, Stairs.LabelHeight(f, 50));
        var cut = Assert.Single(marks, m => m.Part == "cut");
        Assert.Equal(6 * 260, cut.U0);
        Assert.Equal(-450, cut.V0);
        Assert.Equal(7 * 260, cut.U1);
        Assert.Equal(450, cut.V1);
        Assert.False(cut.Dashed);
        foreach (var step in marks.Where(m => m.Part == "step" || m.Part == "outline" && m.U0 == m.U1))
            Assert.Equal(step.U0 >= 7 * 260, step.Dashed);
        // Both sides go dashed past the break; the walking line too.
        Assert.Equal(2, marks.Count(m => m.Part == "outline" && m.V0 == m.V1 && m.Dashed));
        Assert.Single(marks, m => m.Part == "walk" && m.Shape == "line" && m.Dashed);
        var dot = Assert.Single(marks, m => m.Shape == "dot");
        Assert.Equal(0, dot.U0);
        Assert.Equal(0, dot.V0);
        var walk = marks.First(m => m.Part == "walk" && m.Shape == "line" && !m.Dashed);
        Assert.Equal(dot.Radius, walk.U0);
        Assert.Equal(0, walk.V0);
        Assert.Equal(2, marks.Count(m => m.Part == "arrow"));
        var label = Assert.Single(marks, m => m.Shape == "text");
        Assert.Equal("UP 16 × 180/260", label.Text);
        // Below the break, in the left half, clear of the walking line.
        Assert.True(label.U0 < 6 * 260);
        Assert.Equal(225, label.V0);
        Assert.True(label.V0 - label.Radius / 2 > 0);
        Assert.Equal(100, Stairs.LabelHeight(f, 50));
        Assert.Equal(200, Stairs.LabelHeight(f, 100));
        Assert.Equal(360, Stairs.LabelHeight(f, 500));
        // 17 step lines across: the first riser, 15 between, the top edge.
        Assert.Equal(17, marks.Count(m => m.U0 == m.U1 && m.V0 == -450 && m.V1 == 450));
    }

    [Fact]
    public void ShortFlight_BelowCut_HasNoBreakAndNoDashes()
    {
        var f = Stairs.Plan(900, 180, 260, 900);
        Assert.Equal(0, Stairs.CutTread(f, 1200));
        var marks = Stairs.PlanSymbol(f, 1200, 50, 125);
        Assert.DoesNotContain(marks, m => m.Part == "cut");
        Assert.DoesNotContain(marks, m => m.Dashed);
    }

    [Fact]
    public void Record_RoundTrips_AutoAndExplicit()
    {
        var spec = new Stairs.Spec { X = 1000, Y = 200.5, Z = 0, Dx = 0, Dy = 1, Width = 1000, RiserMax = 170, Going = 280 };
        var f = Stairs.Plan(spec, 2880);
        var strings = Stairs.Write(spec, f);
        Assert.Equal("auto", strings[Stairs.RiseKey]);
        Assert.Equal("17", strings[Stairs.RisersKey]);
        Assert.Equal("straight", strings[Stairs.ShapeKey]);
        var back = Stairs.Read(k => strings.TryGetValue(k, out var v) ? v : null);
        Assert.Null(back.Rise);
        Assert.Equal(1000, back.X);
        Assert.Equal(200.5, back.Y);
        Assert.Equal(1, back.Dy);
        Assert.Equal(1000, back.Width);
        Assert.Equal(170, back.RiserMax);
        Assert.Equal(280, back.Going);

        spec.Rise = 2750;
        strings = Stairs.Write(spec, Stairs.Plan(spec, 0));
        Assert.Equal("2750", strings[Stairs.RiseKey]);
        Assert.Equal(2750, Stairs.Read(k => strings.TryGetValue(k, out var v) ? v : null).Rise);
    }

    [Fact]
    public void Read_BadStart_IsNull()
    {
        Assert.Null(Stairs.Read(k => k == Stairs.StartKey ? "x,y" : "1,0"));
    }

    [Fact]
    public void Flip_KeepsFootprint_ReversesClimb()
    {
        var spec = new Stairs.Spec { X = 0, Y = 0, Dx = 1, Dy = 0 };
        var f = Stairs.Plan(spec, 2880);
        var flipped = Stairs.Flipped(spec, f);
        Assert.Equal(4160, flipped.X);
        Assert.Equal(-1, flipped.Dx);
        var a = Stairs.Footprint(spec, f).Select(p => (Math.Round(p.X), Math.Round(p.Y))).OrderBy(p => p).ToList();
        var b = Stairs.Footprint(flipped, f).Select(p => (Math.Round(p.X), Math.Round(p.Y))).OrderBy(p => p).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Resize_GrowsAwayFromTheWall_FlipSwapsTheSide()
    {
        // Climbing east, wall on the right (south): the north side moves out, the south side stays.
        var spec = new Stairs.Spec { X = 0, Y = 650, Dx = 1, Dy = 0, Width = 900, Against = "right" };
        var wide = Stairs.Resized(spec, 1000);
        Assert.Equal(700, wide.Y, 9);
        Assert.Equal(200, wide.Y - wide.Width / 2, 9);
        var free = Stairs.Resized(new Stairs.Spec { Y = 650, Width = 900 }, 1000);
        Assert.Equal(650, free.Y);
        var flipped = Stairs.Flipped(spec, Stairs.Plan(spec, 2880));
        Assert.Equal("left", flipped.Against);
        var strings = Stairs.Write(flipped, Stairs.Plan(flipped, 2880));
        Assert.Equal("left", Stairs.Read(k => strings.TryGetValue(k, out var v) ? v : null).Against);
    }

    [Fact]
    public void NextId_FillsTheFirstGap()
    {
        Assert.Equal("S01", Stairs.NextId(new string[0]));
        Assert.Equal("S03", Stairs.NextId(new[] { "S01", "S02", "w01" }));
        Assert.Equal("S02", Stairs.NextId(new[] { "S01", "S03" }));
    }

    [Fact]
    public void EditReceipt_OneLine()
    {
        var f = Stairs.Plan(2880, 170, 280, 1000);
        Assert.Equal("Changed the stair: 17 steps of 169, 1000 wide.", Stairs.EditReceipt(f, false, true, true));
        Assert.Equal("Flipped the stair, 17 steps of 169.", Stairs.EditReceipt(f, true, false, false));
        var steep = Stairs.Plan(2800, 200, 260, 900);
        Assert.Equal("Changed the stair: 14 steps of 200 (steep: 2R+G = 660).", Stairs.EditReceipt(steep, false, false, true));
    }

    [Fact]
    public void PickLine_EnglishAndNorwegian()
    {
        Assert.Equal("Stair · 16 risers", Stairs.PickLine("16", false));
        Assert.Equal("Trapp · 16 opptrinn", Stairs.PickLine("16", true));
        Assert.Equal("Stair", Stairs.PickLine(null, false));
    }

    [Fact]
    public void Garage_InteriorFaces_LongestRunsAlongTheLongWall()
    {
        var records = new List<List<List<Pt>>> { WallJoinsTests.Garage() };
        var graph = WallJoins.Build(records, new List<int> { 0 }, 1.0);
        var faces = Stairs.InteriorFaces(graph);
        Assert.Equal(4, faces.Count);
        var f = Stairs.Plan(2880, 180, 260, 900);
        var face = Stairs.LongestFace(faces, f.Run);
        Assert.Equal(7600, face.Length, 6);
        Stairs.AlongFace(face, null, 900, out var start, out var dir, out var against);
        Assert.NotNull(against);
        // The stair's side lies on the face, its centre 450 into the room.
        var onSouth = Math.Abs(start.Y - 650) < 1e-6;
        var onNorth = Math.Abs(start.Y - 3350) < 1e-6;
        Assert.True(onSouth || onNorth, $"start {start.X},{start.Y}");
        Assert.Equal(1, Math.Abs(dir.X), 9);
        Assert.StartsWith("along the ", Stairs.Where(face));
        Assert.DoesNotContain("(", Stairs.Where(face));
    }

    [Fact]
    public void AlongFace_StartsAtTheEndNearThePick()
    {
        var face = new Stairs.Face { A = new Pt(200, 200), B = new Pt(7800, 200), Inward = new Pt(0, 1), Length = 7600 };
        Stairs.AlongFace(face, new Pt(7000, 100), 1000, out var start, out var dir, out var against);
        Assert.Equal(new Pt(7800, 700), start);
        Assert.Equal(new Pt(-1, 0), dir);
        // Climbing west with the room to the north: the wall is on the left.
        Assert.Equal("left", against);
    }

    [Fact]
    public void Where_HidesCoordinates()
    {
        Assert.Equal("along the north wall", Stairs.Where(new Stairs.Face { Name = "the north wall" }));
        Assert.Equal("along the longest free wall", Stairs.Where(new Stairs.Face { Name = "the wall at (4000, 2000)" }));
    }
}
