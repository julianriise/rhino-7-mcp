using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>The foot of Settings says which Forsk runs: the Package Manager release or the test copy.</summary>
public class ForskBuildTests
{
    [Fact]
    public void ThePackageFolder_IsTheRelease_ElseTheTestCopy()
    {
        Assert.Equal("Forsk 1.1.0 · Package Manager", ForskBuild.Describe(new Version(1, 1, 0, 0),
            "/Users/jr/Library/Application Support/McNeel/Rhinoceros/packages/7.0/forsk/1.1.0/rhinomcp.rhp"));
        Assert.Equal("Forsk 1.2.0 · test copy", ForskBuild.Describe(new Version(1, 2, 0, 0),
            "/Users/jr/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/rhinomcp.rhp/rhinomcp.rhp"));
        Assert.StartsWith("Forsk ", ForskBuild.Line());
    }

    [Fact]
    public void TheBuildLine_GoesOnTheModel_AndThePageDrawsIt()
    {
        var facts = Docs.Facts("house");
        facts.Build = "Forsk 1.1.0 · test copy";
        Assert.Equal("Forsk 1.1.0 · test copy", WindowView.Build(new DocThread(), facts)["build"]!.ToString());
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("el('div', 'build', model.build)", js);
    }
}
