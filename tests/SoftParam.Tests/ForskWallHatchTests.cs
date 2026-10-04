using System.Drawing;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>Selection is a colour on the mesh. No face hatch is drawn over it.</summary>
public class ForskWallHatchTests
{
    [Fact]
    public void Colour_is_the_selection_colour_and_no_hatch_is_drawn()
    {
        Assert.Equal(41, ForskWallHatch.Red);
        Assert.Equal(72, ForskWallHatch.Green);
        Assert.Equal(245, ForskWallHatch.Blue);
        Assert.Equal(Color.FromArgb(41, 72, 245).ToArgb(), ForskWallHatch.Colour.ToArgb());

        var host = File.ReadAllText(Path.Combine(PluginDir(), "Functions", "ForskWallHatchHost.cs"));
        Assert.Contains("SelectedObjectColor", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Hatch.Create", host, StringComparison.Ordinal);
        Assert.DoesNotContain("DrawHatch", host, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayConduit", host, StringComparison.Ordinal);
    }

    static string PluginDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin");
            if (Directory.Exists(Path.Combine(path, "Functions"))) return path;
        }
        throw new DirectoryNotFoundException("plugin above " + AppContext.BaseDirectory);
    }
}
