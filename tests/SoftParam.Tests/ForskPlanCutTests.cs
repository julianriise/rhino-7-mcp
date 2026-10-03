using System.Collections.Generic;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>The plan cut is floor top plus 1200 mm, facing down, on the Top view only.</summary>
public class ForskPlanCutTests
{
    [Fact]
    public void Cut_sits_one_point_two_metres_above_the_floor()
    {
        var none = ForskPlanCut.Describe(null);
        Assert.Equal(1200, none.Z);
        Assert.Equal(0, none.Nx);
        Assert.Equal(0, none.Ny);
        Assert.Equal(-1, none.Nz);
        Assert.Equal(-50000, none.OriginX);
        Assert.Equal(50000, none.OriginY);

        Assert.Equal(1600, ForskPlanCut.Describe(new[] { 400.0 }).Z);
        Assert.Equal(4200, ForskPlanCut.Describe(new[] { 0.0, 3000.0 }).Z);
    }

    [Fact]
    public void Viewport_list_is_the_top_view_only()
    {
        var views = new[]
        {
            new ForskPlanCut.ViewSlot("top", "Top", "RhinoView"),
            new ForskPlanCut.ViewSlot("persp", "Perspective", "RhinoView"),
            new ForskPlanCut.ViewSlot("front", "Front", "RhinoView"),
            new ForskPlanCut.ViewSlot("page", "Top", ForskWhite.PageViewType),
            new ForskPlanCut.ViewSlot("detail", "Top", ForskWhite.DetailType),
            new ForskPlanCut.ViewSlot("topo", "Topography", "RhinoView"),
            new ForskPlanCut.ViewSlot("topic", "Topic", "RhinoView"),
            new ForskPlanCut.ViewSlot("top2", "Top 01", "RhinoView"),
            new ForskPlanCut.ViewSlot("top3", "top1", "RhinoView")
        };
        var ids = ForskPlanCut.TopIds(views);
        Assert.Equal(new[] { "top", "top2", "top3" }, ids);

        List<string> add, remove;
        ForskPlanCut.ViewportEdits(
            new[] { "top", "persp", "front" },
            ids,
            out add,
            out remove);
        Assert.Equal(new[] { "top2", "top3" }, add);
        Assert.Equal(new[] { "persp", "front" }, remove);
    }

    [Fact]
    public void Stamp_survives_clear_generated()
    {
        Assert.Equal("forsk-plan-cut", ForskPlanCut.ObjectName);
        Assert.Equal("forsk:plan_cut", ForskPlanCut.TagKey);
        Assert.True(ForskPlanCut.SurvivesClear(ForskPlanCut.ObjectName, null, null));
        Assert.False(ForskPlanCut.SurvivesClear("wall-a", null, null));
        Assert.False(ForskPlanCut.SurvivesClear(ForskPlanCut.ObjectName, "1", "wall"));
        Assert.False(ForskPlanCut.SurvivesClear(ForskPlanCut.ObjectName, "1", "analysis"));
    }
}
