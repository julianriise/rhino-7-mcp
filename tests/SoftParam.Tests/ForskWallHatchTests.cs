using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>The hatch draws for a selected wall, in the selection colour.</summary>
public class ForskWallHatchTests
{
    [Fact]
    public void Draw_runs_only_for_a_selected_wall()
    {
        var objects = new[]
        {
            new ForskWallHatch.Candidate(true, "wall"),
            new ForskWallHatch.Candidate(true, "Wall"),
            new ForskWallHatch.Candidate(true, "floor"),
            new ForskWallHatch.Candidate(false, "wall"),
            new ForskWallHatch.Candidate(true, "opening"),
            new ForskWallHatch.Candidate(true, null)
        };
        Assert.Equal(new[] { 0, 1 }, ForskWallHatch.DrawIndexes(objects));
        Assert.False(ForskWallHatch.Draws(true, "roof"));
        Assert.False(ForskWallHatch.Draws(false, "WALL"));
    }

    [Fact]
    public void Colour_is_the_selection_colour()
    {
        Assert.Equal(41, ForskWallHatch.Red);
        Assert.Equal(72, ForskWallHatch.Green);
        Assert.Equal(245, ForskWallHatch.Blue);
    }
}
