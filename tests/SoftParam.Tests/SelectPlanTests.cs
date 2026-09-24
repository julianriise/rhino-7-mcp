using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// select_objects must skip a row with no attributes and a null name.
/// A wall and a window frame still match by name.
/// </summary>
public class SelectPlanTests
{
    [Fact]
    public void NullAttributesAndNullName_DoNotThrow()
    {
        var rows = new List<SelectPlan.Row>
        {
            new SelectPlan.Row { Id = "bare", Name = "wall-01", HasAttributes = false },
            new SelectPlan.Row { Id = "noname", Name = null, HasAttributes = true },
            null
        };

        var hits = SelectPlan.Match(rows, new[] { "wall-01" }, false, 0, 0, 0, null, false);

        Assert.Empty(hits);
    }

    [Fact]
    public void Name_SelectsTheWallAndTheFrame()
    {
        var rows = Sample();

        var wall = SelectPlan.Match(rows, new[] { "wall-01" }, false, 0, 0, 0, null, false);
        var frame = SelectPlan.Match(rows, new[] { "window-63-block" }, false, 0, 0, 0, null, false);
        var missing = SelectPlan.Match(rows, new[] { "no-such" }, false, 0, 0, 0, null, false);

        Assert.Equal(new[] { "wall" }, wall);
        Assert.Equal(new[] { "frame" }, frame);
        Assert.Empty(missing);
    }

    [Fact]
    public void NoFilter_KeepsOnlyRowsWithAttributes()
    {
        var hits = SelectPlan.Match(Sample(), null, false, 0, 0, 0, null, false);

        Assert.Equal(new[] { "noname", "wall", "frame" }, hits);
    }

    private static List<SelectPlan.Row> Sample()
    {
        return new List<SelectPlan.Row>
        {
            new SelectPlan.Row { Id = "bare", Name = "wall-01", HasAttributes = false },
            new SelectPlan.Row { Id = "noname", Name = null, HasAttributes = true },
            new SelectPlan.Row { Id = "wall", Name = "wall-01", HasAttributes = true },
            new SelectPlan.Row
            {
                Id = "frame",
                Name = "window-63-block",
                HasAttributes = true,
                Strings = new Dictionary<string, string> { ["forsk:kind"] = "opening" }
            }
        };
    }
}
