using System;
using System.IO;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The release package ships prompts, tools and the daylight tracer in a forsk
/// folder beside the plugin. Forsk looks there after FORSK_HOME and before the
/// developer's checkout, so an installed package works on any Mac.
/// </summary>
public class BundledHomeTests
{
    [Fact]
    public void ThePackageFolder_SitsBesideThePlugin_BetweenTheVariableAndTheCheckout()
    {
        var bundled = ForskUv.Bundled();
        Assert.Equal(Path.Combine(Path.GetDirectoryName(typeof(ForskUv).Assembly.Location)!, "forsk"), bundled);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = ForskUv.Roots().ToList();
        Assert.Equal(Path.Combine(home, "Documents", "hobby", "forsk"), roots[roots.Count - 1]);
        Assert.Equal(bundled, roots[roots.Count - 2]);
    }

    [Fact]
    public void ATool_InThePackageFolder_IsFound()
    {
        var dir = Path.Combine(ForskUv.Bundled()!, "tools", "bundled_probe");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "pyproject.toml"), "[project]\nname = \"probe\"\n");
            Assert.Equal(dir, ForskUv.ToolDir("bundled_probe"));
        }
        finally
        {
            Directory.Delete(ForskUv.Bundled()!, true);
        }
    }

    [Fact]
    public void Daylight_WithoutAVenv_RunsThroughUvWithNumpy()
    {
        Assert.Equal("run --no-project --quiet --with numpy python -m forsk_daylight", ForskUv.RunModuleArgs("forsk_daylight"));
    }
}
