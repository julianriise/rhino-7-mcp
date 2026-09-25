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
        var leaf = Assert.Single(marks);
        Assert.True(leaf.Dashed);
        Assert.Equal(0, leaf.Y0, 6);
        Assert.Equal(0, leaf.Y1, 6);
        Assert.True(Math.Abs(leaf.Y0) <= frame.HalfThick);
        var park = OpeningTypes.HandSign(record, frame.XLeft);
        Assert.True(park * leaf.X0 > frame.InnerHalf);
        Assert.True(park * leaf.X1 > frame.InnerHalf);
        Assert.DoesNotContain(marks, m => m.Shape == "arc");
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
    public void RoomTag_AndViewTitle()
    {
        Assert.Equal("ca. 12,4 m²", OpeningTypes.RoomTag(12400000));
        Assert.Equal("ca. 20,2 m²", OpeningTypes.RoomTag(20160000));
        Assert.Equal("Plan 1. etg 1:100", OpeningTypes.ViewTitle("plan", 0, 100, true));
        Assert.Equal("Plan 1. etg 1:200", OpeningTypes.ViewTitle("plan", 0, 200, true));
        Assert.Equal("Plan 1. etg fit", OpeningTypes.ViewTitle("plan", 0, 200, false));
        Assert.Equal("Fasade mot sør", OpeningTypes.ViewTitle("south", 0, 100, true));
        Assert.Equal("Fasade mot nord", OpeningTypes.ViewTitle("north", 0, 100, true));
        Assert.Equal("Fasade mot øst", OpeningTypes.ViewTitle("east", 0, 100, true));
        Assert.Equal("Fasade mot vest", OpeningTypes.ViewTitle("west", 0, 100, true));
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
