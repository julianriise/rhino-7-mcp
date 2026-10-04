using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>Draw wall in the window: the card, the typed phrase, the role, the receipt.</summary>
public class DrawToolsWindowTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("plan curves")]
    [InlineData("house")]
    [InlineData("house, wall selected")]
    public void DrawWall_IsOnTheHelpCard_InEveryFile(string file)
    {
        var action = ForskRegistry.Card(Docs.Facts(file)).Actions.Single(a => a.Id == "wall.draw");
        Assert.Equal("Draw wall", action.Label);
        Assert.Equal(Runs.Run, action.Runs);
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("wall.draw"));
    }

    [Fact]
    public void DrawWall_DoesNotMoveTheBar()
    {
        foreach (var file in new[] { "empty", "plan curves", "house", "house, wall selected" })
            Assert.DoesNotContain(ForskRegistry.Bar(Docs.Facts(file)).Slots, a => a.Id == "wall.draw");
    }

    [Theory]
    [InlineData("draw wall")]
    [InlineData("Draw a wall")]
    [InlineData("  draw   walls. ")]
    public void TheTypedPhrase_FiresDrawWall(string text)
    {
        Assert.Equal("wall.draw", ForskRegistry.ByDrawPhrase(text)?.Id);
    }

    [Theory]
    [InlineData("draw a wall 4 m long")]
    [InlineData("draw")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherText_FiresNothing(string text)
    {
        Assert.Null(ForskRegistry.ByDrawPhrase(text));
    }

    [Fact]
    public void TheReceipt_IsTheOneLine_WithNoWallId()
    {
        var envelope = new JObject
        {
            ["status"] = "success",
            ["result"] = new JObject { ["message"] = "Drew 4 walls, 200 mm thick, 14.0 m in all, closed.", ["count"] = 4 }
        };
        var receipt = ForskReceipt.From("add_wall", envelope);
        Assert.True(receipt.Ok);
        Assert.Equal("Drew 4 walls, 200 mm thick, 14.0 m in all, closed.", receipt.Text);
        Assert.Equal("Wall", receipt.Subject);
    }
}
