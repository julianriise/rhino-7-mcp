using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Choice cards (Julian, 2026-10-07): a pill is an option that applies at
/// once and keeps the card open, filled; Confirm closes the card and adds no
/// chat message. Daylight quality and Jump inside work this way.
/// </summary>
public class ChoiceCardTests
{
    static string[] Ids(CardSpec card) => card.Pills.Select(p => p.Id).ToArray();

    [Fact]
    public void DaylightQuality_IsAChoiceCard_WithConfirm()
    {
        var card = ForskCards.Quality(Docs.Facts("house"));
        Assert.True(card.Choice);
        Assert.Equal(new[] { "low", "medium", "high", "done" }, Ids(card));
        Assert.Equal("Confirm", card.Pills[3].Label);
    }

    [Fact]
    public void JumpInside_IsAChoiceCard_WithConfirm()
    {
        var card = ForskCards.JumpInside(Docs.Facts("house, room selected"));
        Assert.True(card.Choice);
        Assert.Equal(new[] { "north", "east", "south", "west", "done", "cancel" }, Ids(card));
        Assert.Equal("Confirm", card.Pills[4].Label);
        Assert.Equal("Looking north. Confirm saves it as a named view.", card.Note);
    }

    [Fact]
    public void AnOption_IsHeldAndFilled_ConfirmAndCancelAreNoOptions()
    {
        var facts = Docs.Facts("house");
        var card = new DocThread().AddCard(ForskCards.Quality(facts), facts);
        Assert.Equal("low", ForskCards.HeldChoice(card));
        Assert.True(ForskCards.IsChoiceOption(card, "high"));
        Assert.False(ForskCards.IsChoiceOption(card, "done"));
        Assert.False(ForskCards.IsChoiceOption(card, "cancel"));
        ForskCards.HoldChoice(card, "high", "Now: High.");
        Assert.Equal("high", ForskCards.HeldChoice(card));
        Assert.Equal(new[] { "high" }, ((Newtonsoft.Json.Linq.JArray)card["pills"]!).Where(p => (bool?)p["primary"] == true).Select(p => p["id"]!.ToString()));
        Assert.Equal("Now: High.", card["note"]!.ToString());
    }

    [Fact]
    public void ACardThatIsNoChoice_HasNoOptions()
    {
        var facts = Docs.Facts("house, door selected");
        var card = new DocThread().AddCard(ForskCards.SwapType(facts)!, facts);
        Assert.False(ForskCards.IsChoiceOption(card, card["pills"]![0]!["id"]!.ToString()));
    }

    [Fact]
    public void Confirm_ClosesTheCard_WithTheHeldOption_AndAddsNoMessage()
    {
        var facts = Docs.Facts("house");
        var thread = new DocThread();
        var card = thread.AddCard(ForskCards.Quality(facts), facts);
        ForskCards.HoldChoice(card, "medium", "Now: Medium.");
        var count = thread.Items.Count;
        Assert.True(thread.Settle(card["id"]!.ToString(), "Medium"));
        Assert.Equal(count, thread.Items.Count);
        Assert.Equal("answered", card["state"]!.ToString());
        Assert.Equal("Medium", card["answer"]!.ToString());
        Assert.False(thread.Settle(card["id"]!.ToString(), "Medium"));
    }

    /// <summary>Julian, 2026-10-07: Jump inside with no room picked says to pick one first.</summary>
    [Fact]
    public void JumpInside_WithNoRoomPicked_AsksForARoomFirst()
    {
        var house = Docs.Facts("house");
        Assert.True(ForskRegistry.Find("room.inside")!.Shows(house));
        Assert.Equal("Click a room's floor first, then Jump inside.", ForskCards.JumpInsideNeedsPick(house));
        Assert.Null(ForskCards.JumpInsideNeedsPick(Docs.Facts("house, room selected")));
        Assert.False(ForskRegistry.Find("room.inside")!.Shows(Docs.Facts("walls only")));
    }
}
