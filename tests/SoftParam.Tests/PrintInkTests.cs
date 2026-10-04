using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Print colour of each Forsk layer, and of a model object versus a sheet
/// object. Display colour is not a print source. A-ANALYSE and X-PLAN do not print.
/// </summary>
public class PrintInkTests
{
    static readonly string[] Expected =
    {
        "A-WALL", "A-FLOR", "A-ROOF", "A-OPEN", "A-ROOM", "A-STRU", "A-ANNO",
        "A-ANALYSE", "X-EXIST", "X-PLAN", "S-DRAW", "S-PLAN",
        "S-ELEV-N", "S-ELEV-E", "S-ELEV-S", "S-ELEV-W", "cross-sections",
        "facade-ground", "facade-outline", "facade-line", "facade-opening", "facade-level"
    };

    [Fact]
    public void Every_Forsk_layer_prints_grey_and_daylight_stays_off()
    {
        Assert.Equal(Expected.OrderBy(n => n), PrintInk.Names.OrderBy(n => n));
        foreach (var name in Expected)
        {
            Assert.True(PrintInk.TryResolve(name, name, out var spec), name);
            Assert.True(spec.Greyscale, name);
            if (name == "A-ANALYSE" || name == "X-PLAN")
                Assert.False(spec.Prints, name);
            else
                Assert.True(spec.Prints, name);
        }
    }

    [Fact]
    public void A_child_of_the_drawing_or_opening_layer_takes_its_parent_ink()
    {
        Assert.True(PrintInk.TryResolve("S-DRAW::Plan", "Plan", out var plan));
        Assert.True(PrintInk.TryResolve("S-DRAW", "S-DRAW", out var draw));
        Assert.Equal(draw.R, plan.R);
        Assert.Equal(draw.WeightMm, plan.WeightMm);
        Assert.True(PrintInk.TryResolve("A-OPEN::Block", "Block", out var block));
        Assert.True(PrintInk.TryResolve("A-OPEN", "A-OPEN", out var open));
        Assert.Equal(open.R, block.R);
        Assert.False(PrintInk.TryResolve(null, "Block", out _));
    }

    [Fact]
    public void A_model_object_prints_its_layer_and_a_sheet_object_prints_its_pen()
    {
        Assert.True(PrintInk.TryResolve("A-WALL", "A-WALL", out var wall));
        var model = PrintInk.ForModel(wall);
        Assert.Equal("layer", model.Source);
        Assert.Equal(wall.R, model.R);
        Assert.True(model.Greyscale);
        var sheet = PrintInk.ForSheet(30, 30, 30);
        Assert.Equal("object", sheet.Source);
        Assert.Equal(30, sheet.R);
        Assert.True(sheet.Greyscale);
        foreach (var profile in PrintProfiles.All)
        {
            foreach (var pen in new[] { profile.Cut.Color, profile.Beyond.Color, profile.Thin.Color, profile.Dashed, profile.Poche })
            {
                var ink = PrintInk.ForSheet(pen.R, pen.G, pen.B);
                Assert.Equal("object", ink.Source);
                Assert.True(ink.Greyscale);
            }
        }
    }
}
