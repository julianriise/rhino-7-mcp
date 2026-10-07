using RhinoMCPPlugin.Forsk;
using Xunit;

public class ForskGrokModelTests
{
    [Fact]
    public void DefaultIsGrok43()
    {
        Assert.Equal("grok-4.3", ForskGrokModel.Default);
        Assert.Equal("grok-4.3", ForskGrokModel.Resolve(null));
        Assert.Equal("grok-4.3", ForskGrokModel.Resolve("  "));
    }

    [Fact]
    public void OverrideWins()
    {
        Assert.Equal("grok-4.7", ForskGrokModel.Resolve(" grok-4.7 "));
    }
}
