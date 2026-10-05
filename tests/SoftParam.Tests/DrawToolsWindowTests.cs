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

    [Theory]
    [InlineData("house")]
    [InlineData("house, wall selected")]
    public void DrawStair_IsOnTheHelpCard_OfAFileWithWalls(string file)
    {
        var action = ForskRegistry.Card(Docs.Facts(file)).Actions.Single(a => a.Id == "stair.draw");
        Assert.Equal("Draw stair", action.Label);
        Assert.Equal(Runs.Run, action.Runs);
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("stair.draw"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("plan curves")]
    public void DrawStair_NeedsWalls(string file)
    {
        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts(file)).Actions, a => a.Id == "stair.draw");
    }

    /// <summary>AI imports miss rooms: Draw area is on the card once there are walls; Redraw area needs one picked room.</summary>
    [Fact]
    public void DrawArea_NeedsWalls_AndRedrawArea_NeedsOnePickedRoom()
    {
        var house = ForskRegistry.Card(Docs.Facts("house")).Actions;
        var draw = house.Single(a => a.Id == "room.draw");
        Assert.Equal("Draw area", draw.Label);
        Assert.Equal(Runs.Run, draw.Runs);
        Assert.DoesNotContain(house, a => a.Id == "room.redraw");
        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts("plan curves")).Actions, a => a.Id == "room.draw");

        var picked = ForskRegistry.Card(Docs.Facts("house, room selected")).Actions.Single(a => a.Id == "room.redraw");
        Assert.Equal("Redraw area", picked.Label);
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("room.draw"));
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("room.redraw"));
    }

    [Fact]
    public void TheDrawTools_DoNotMoveTheBar()
    {
        foreach (var file in new[] { "empty", "plan curves", "house", "house, wall selected" })
            Assert.DoesNotContain(ForskRegistry.Bar(Docs.Facts(file)).Slots, a => a.Id == "wall.draw" || a.Id == "stair.draw");
    }

    [Theory]
    [InlineData("draw wall")]
    [InlineData("Draw a wall")]
    [InlineData("  draw   walls. ")]
    public void TheTypedPhrase_FiresDrawWall(string text)
    {
        Assert.Equal("wall.draw", ForskRegistry.ByDrawPhrase(text)?.Id);
    }

    [Fact]
    public void DrawAWall_OnAnEmptyBar_IsDrawWall_NotThePolyline()
    {
        var bar = ForskRegistry.Bar(Docs.Facts("empty"));
        Assert.Contains(bar.Slots, a => a.Id == "file.draw");
        Assert.Equal("Trace walls", ForskRegistry.Find("file.draw").Label);
        Assert.Null(ForskRegistry.ByLabel(bar, "Draw a wall"));
        var hit = ForskRegistry.ByLabel(bar, "Draw a wall") ?? ForskRegistry.ByDrawPhrase("Draw a wall");
        Assert.Equal("wall.draw", hit.Id);
        Assert.Equal("file.draw", ForskRegistry.ByLabel(bar, "Trace walls").Id);
    }

    [Theory]
    [InlineData("draw stair")]
    [InlineData("Draw a stair.")]
    [InlineData("draw stairs")]
    public void TheTypedPhrase_FiresDrawStair(string text)
    {
        Assert.Equal("stair.draw", ForskRegistry.ByDrawPhrase(text)?.Id);
    }

    [Fact]
    public void AddStair_TakesAgainstInItsSchema()
    {
        var schema = ForskToolPacks.Schemas(new[] { "add_stair" }).Single();
        var props = (JObject)schema["function"]["parameters"]["properties"];
        Assert.NotNull(props["against"]);
        Assert.NotNull(props["going"]);
        Assert.NotNull(props["width"]);
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
    public void TheStairReceipt_IsTheToolsLine()
    {
        var envelope = new JObject
        {
            ["status"] = "success",
            ["result"] = new JObject { ["forsk_id"] = "S01", ["message"] = "Added a straight stair, 19 steps of 179." }
        };
        var receipt = ForskReceipt.From("add_stair", envelope);
        Assert.True(receipt.Ok);
        Assert.Equal("Added a straight stair, 19 steps of 179.", receipt.Text);
        Assert.Equal("Stair", receipt.Subject);
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
