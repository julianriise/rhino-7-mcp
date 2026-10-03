using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// R5 in the window: the pick line, the bar for a picked stair, the Edit
/// stair card and its arguments, and the chat routing of stair phrases.
/// </summary>
public class StairWindowTests
{
    [Fact]
    public void PickLine_NamesTheStairAndItsRisers()
    {
        var f = Docs.Facts("house, stair selected");
        Assert.Equal(Picked.Stair, f.Picked);
        Assert.True(f.HasStairs);
        Assert.Equal("Stair · 16 risers", ForskPick.Line(f.Selected, false));
        Assert.Equal("Trapp · 16 opptrinn", ForskPick.Line(f.Selected, true));
        Assert.Equal("2 stairs", ForskPick.Line(Docs.Facts("house, two stairs selected").Selected, false));
    }

    [Fact]
    public void PickedStair_BarIsEditThenDelete()
    {
        var bar = ForskRegistry.Bar(Docs.Facts("house, stair selected"));
        Assert.Equal(new[] { "file.print", "stair.edit", "stair.delete" }, bar.Slots.Select(a => a.Id));
        Assert.Equal(new[] { "Print PDF", "Edit stair", "Delete stair" }, bar.Slots.Select(a => a.Label));
        // Two stairs: no card for both, Delete still goes.
        var two = ForskRegistry.Bar(Docs.Facts("house, two stairs selected"));
        Assert.DoesNotContain(two.Slots, a => a.Id == "stair.edit");
        Assert.Contains(two.Slots, a => a.Id == "stair.delete");
    }

    [Fact]
    public void AddStair_IsOnTheCard_WithNothingOrOneWallPicked()
    {
        Assert.Contains(ForskRegistry.Card(Docs.Facts("house")).Actions, a => a.Id == "stair.add");
        Assert.Contains(ForskRegistry.Card(Docs.Facts("house, wall selected")).Actions, a => a.Id == "stair.add");
        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts("house, door selected")).Actions, a => a.Id == "stair.add");
        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts("plan curves")).Actions, a => a.Id == "stair.add");
        // Not a bar suggestion: the bar keeps what it had.
        Assert.DoesNotContain(ForskRegistry.Bar(Docs.Facts("house")).Slots, a => a.Id == "stair.add");
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("stair.add"));
    }

    [Fact]
    public void EditCard_HoldsTheSizes_SaveFlipCancel()
    {
        var card = ForskCards.For("stair.edit", Docs.Facts("house, stair selected"));
        Assert.Equal("stair.edit", card.Kind);
        Assert.Equal("selection", card.Depends);
        Assert.Equal(new[] { "width", "riser_max", "going" }, card.Fields.Select(x => x.Key));
        Assert.Equal(new[] { "900", "180", "260" }, card.Fields.Select(x => x.Value));
        Assert.Equal(new[] { "save", "flip", "cancel" }, card.Pills.Select(p => p.Id));
        Assert.Null(ForskCards.For("stair.edit", Docs.Facts("house")));
    }

    [Fact]
    public void EditCard_SendsOnlyWhatChanged()
    {
        var fields = JArray.FromObject(new[]
        {
            new { key = "width", value = "900" }, new { key = "riser_max", value = "180" }, new { key = "going", value = "260" }
        });
        var args = ForskCards.StairArgs("save", new JObject { ["width"] = "1000", ["riser_max"] = "180", ["going"] = "280 mm" }, fields);
        Assert.Equal(1000, args["width"]!.Value<double>());
        Assert.Equal(280, args["going"]!.Value<double>());
        Assert.Null(args["riser_max"]);
        Assert.Null(ForskCards.StairArgs("save", new JObject { ["width"] = "900", ["riser_max"] = "x" }, fields));
        Assert.True(ForskCards.StairArgs("flip", null, fields)!["flip"]!.Value<bool>());
        Assert.Null(ForskCards.StairArgs("cancel", null, fields));
    }

    [Theory]
    [InlineData("add a straight stair")]
    [InlineData("add stair along this wall")]
    [InlineData("legg til en trapp")]
    [InlineData("make the stair 1000 wide")]
    [InlineData("steps 170 high")]
    [InlineData("going 280")]
    [InlineData("flip the stair")]
    [InlineData("fjern trappa")]
    [InlineData("opptrinn 170")]
    public void StairPhrases_RouteToEdit(string text)
    {
        Assert.Equal(ForskIntent.Edit, ForskIntentRouter.Classify(text));
    }

    [Fact]
    public void PickedStair_ItRoutesToEdit_AndAQuestionStaysSupport()
    {
        Assert.Equal(ForskIntent.Edit, ForskIntentRouter.Classify("make it wider", Picked.Stair));
        Assert.Equal(ForskIntent.Support, ForskIntentRouter.Classify("how do I add a stair"));
        Assert.NotEqual(ForskIntent.Edit, ForskIntentRouter.Classify("what is going on"));
    }

    [Fact]
    public void EditPack_CarriesTheStairTools()
    {
        var edit = ForskToolPacks.For(ForskIntent.Edit);
        Assert.Contains("add_stair", edit);
        Assert.Contains("edit_stair", edit);
        Assert.Contains("delete_stair", edit);
        Assert.Equal(3, ForskToolPacks.Schemas(new[] { "add_stair", "edit_stair", "delete_stair" }).Count);
    }

    [Fact]
    public void Receipt_HasNoIdOrCoordinates()
    {
        var envelope = new JObject
        {
            ["status"] = "success",
            ["result"] = new JObject { ["forsk_id"] = "S01", ["message"] = "Added a straight stair along the north wall, 16 steps of 180." }
        };
        var receipt = ForskReceipt.From("add_stair", envelope);
        Assert.True(receipt.Ok);
        Assert.Equal("Stair", receipt.Subject);
        Assert.Equal("Added a straight stair along the north wall, 16 steps of 180.", receipt.Text);
    }
}
