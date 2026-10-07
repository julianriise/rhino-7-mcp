using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// View picker part 1: Perspective, Plan and the four elevations from the
/// top left of the window or from chat. The active viewport takes the
/// camera, the look and the zoom; the plan cut follows any plan view.
/// </summary>
public class ViewPickerTests
{
    [Fact]
    public void ThePicker_HasPerspectivePlanAndTheFourElevations()
    {
        Assert.Equal(new[] { "perspective", "plan", "north", "east", "south", "west" }, ViewPicker.Ids);
        Assert.Equal("Perspective", ViewPicker.Label("perspective"));
        Assert.Equal("Plan", ViewPicker.Label("plan"));
        Assert.Equal("South elevation", ViewPicker.Label("south"));
        Assert.Null(ViewPicker.For("section_a"));
    }

    [Theory]
    [InlineData("plan", true, 0, 0, -1, 0, 1, 0)]
    [InlineData("north", true, 0, -1, 0, 0, 0, 1)]
    [InlineData("east", true, -1, 0, 0, 0, 0, 1)]
    [InlineData("south", true, 0, 1, 0, 0, 0, 1)]
    [InlineData("west", true, 1, 0, 0, 0, 0, 1)]
    public void EachView_LooksAsItsSheetDoes(string id, bool parallel, double dx, double dy, double dz, double ux, double uy, double uz)
    {
        var camera = ViewPicker.For(id)!;
        Assert.Equal(parallel, camera.Parallel);
        Assert.Equal(new[] { dx, dy, dz, ux, uy, uz }, new[] { camera.Dx, camera.Dy, camera.Dz, camera.Ux, camera.Uy, camera.Uz });
    }

    [Fact]
    public void ThePerspective_LooksInAndDown()
    {
        var camera = ViewPicker.For("perspective")!;
        Assert.False(camera.Parallel);
        Assert.True(camera.Dz < 0);
        Assert.Equal("perspective", ViewPicker.Current(false, camera.Dx, camera.Dy, camera.Dz));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("north")]
    [InlineData("east")]
    [InlineData("south")]
    [InlineData("west")]
    public void EachCamera_ReadsBackAsItsView(string id)
    {
        var camera = ViewPicker.For(id)!;
        Assert.Equal(id, ViewPicker.Current(camera.Parallel, camera.Dx, camera.Dy, camera.Dz));
    }

    [Fact]
    public void ATiltedParallelView_IsNoPickerView()
    {
        Assert.Null(ViewPicker.Current(true, 1, 1, -1));
        Assert.Equal("perspective", ViewPicker.Current(false, 0, 0, -1));
    }

    [Theory]
    [InlineData("show the south elevation", "south")]
    [InlineData("Show me the north facade", "north")]
    [InlineData("go to the east elevation", "east")]
    [InlineData("west elevation", "west")]
    [InlineData("vis sørfasaden", "south")]
    [InlineData("vis fasade nord", "north")]
    [InlineData("show the plan", "plan")]
    [InlineData("plan view", "plan")]
    [InlineData("top view", "plan")]
    [InlineData("vis plantegningen", "plan")]
    [InlineData("show perspective", "perspective")]
    [InlineData("3d view", "perspective")]
    [InlineData("vis perspektiv", "perspective")]
    [InlineData("print the plan", null)]
    [InlineData("add a window to the south facade", null)]
    [InlineData("move the north wall 300 mm", null)]
    [InlineData("show the daylight", null)]
    [InlineData("plan", null)]
    [InlineData("how do i show the south elevation", null)]
    public void ATypedView_IsPicked(string said, string? expected)
    {
        Assert.Equal(expected, ForskIntentRouter.ViewPick(said));
    }

    [Fact]
    public void ThePlanCut_ClipsAnyPlanView_NotOnlyOneNamedTop()
    {
        var views = new[]
        {
            new ForskPlanCut.ViewSlot("persp", "Perspective", "RhinoView", plan: true),
            new ForskPlanCut.ViewSlot("front", "Front", "RhinoView"),
            new ForskPlanCut.ViewSlot("top", "Top", "RhinoView"),
            new ForskPlanCut.ViewSlot("page", "Plan", ForskWhite.PageViewType, plan: true)
        };
        Assert.Equal(new[] { "persp", "top" }, ForskPlanCut.TopIds(views));
    }

    [Fact]
    public void TheWindow_ShowsTheActiveViewport_AndOffersEveryView()
    {
        var facts = Docs.Facts("house");
        facts.View = "south";
        var control = ViewPicker.Control(facts.View);
        Assert.Equal("south", control["value"]!.ToString());
        Assert.Equal("South elevation", control["label"]!.ToString());
        Assert.Equal(new[] { "perspective", "plan", "north", "east", "south", "west" }, control["options"]!.Select(o => o["id"]!.ToString()));
        Assert.Equal("View", ViewPicker.Control(null)["label"]!.ToString());
        Assert.Equal("south", WindowView.Build(new DocThread(), facts)["view"]!["value"]!.ToString());
    }

    [Fact]
    public void ThePage_SendsThePickedView_AndChecksTheShownOne()
    {
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("sender.send({ kind: 'view', view: option.id })", js);
        Assert.Contains("function renderView(model)", js);
        Assert.Contains("'view-menu'", js);
    }
}
