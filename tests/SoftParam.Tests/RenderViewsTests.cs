using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Interior render and Exterior render (Julian, 2026-10-08): Jump inside is
/// renamed Interior render; Exterior render looks at the building from the
/// north, east, south or west in its own look; the view picker lists the saved
/// render views of each kind, and one click shows one with its look.
/// </summary>
public class RenderViewsTests
{
    [Fact]
    public void BothRenders_AreInMoreActions_UnderTheirNewNames()
    {
        var house = Docs.Facts("house");
        Assert.Equal("Interior render", ForskRegistry.Find("room.inside")!.Label);
        Assert.Equal("Exterior render", ForskRegistry.Find("view.exterior")!.Label);
        Assert.True(ForskRegistry.Find("view.exterior")!.Shows(house));
        Assert.False(ForskRegistry.Find("view.exterior")!.Shows(Docs.Facts("empty")));
    }

    [Fact]
    public void ExteriorRender_IsAChoiceCard_ThatEndsInConfirm()
    {
        var card = ForskCards.For("view.exterior", Docs.Facts("house"))!;
        Assert.True(card.Choice);
        Assert.Equal("Which side should the camera stand on?", card.Question);
        Assert.Equal(new[] { "north", "east", "south", "west", "done" }, card.Pills.Select(p => p.Id));
        Assert.Equal("From the north. Confirm saves it as a named view.", card.Note);
    }

    [Fact]
    public void ThePicker_ListsTheSavedRenderViews_InteriorThenExterior()
    {
        var control = ViewPicker.Control("perspective", new[] { "Living", "Bedroom 1" }, new[] { "Exterior south" });
        var groups = (JArray)control["groups"]!;
        Assert.Equal(new[] { "Interior", "Exterior" }, groups.Select(g => g["label"]!.ToString()));
        Assert.Equal(new[] { "interior:Living", "interior:Bedroom 1" }, groups[0]["items"]!.Select(i => i["id"]!.ToString()));
        Assert.Equal("Exterior south", groups[1]["items"]![0]!["label"]!.ToString());
        // No saved renders: no lists, the six views as before.
        Assert.Null(ViewPicker.Control("plan")["groups"]);
        Assert.Null(ViewPicker.Control("plan", new string[0], null)["groups"]);
    }

    [Theory]
    [InlineData("interior:Living", true, "Living", false)]
    [InlineData("exterior:Exterior north", true, "Exterior north", true)]
    [InlineData("south", false, null, false)]
    [InlineData("interior:", false, null, false)]
    public void APickedRenderView_IsReadBackByItsKind(string id, bool ok, string? name, bool exterior)
    {
        Assert.Equal(ok, ViewPicker.TryRender(id, out var got, out var isExterior));
        Assert.Equal(name, got);
        Assert.Equal(exterior, isExterior);
    }

    [Fact]
    public void ForskExterior_IsTheSameLookUnderItsOwnName_AndForskWhiteLeavesItAlone()
    {
        const string export = "[DisplayMode\\cae60bae-2d51-4299-abf7-a339fca86f3b]\nName=Rendered\n[DisplayMode\\cae60bae-2d51-4299-abf7-a339fca86f3b\\Lighting]\nCastShadows=n\nShadowIntensity=100\n";
        var ini = ForskInterior.PatchExterior(export);
        Assert.Equal("Forsk Exterior", ForskWhite.Read(ini, "", "Name"));
        Assert.Equal(("y", "25"), (ForskWhite.Read(ini, "Lighting", "CastShadows"), ForskWhite.Read(ini, "Lighting", "ShadowIntensity")));
        Assert.False(ForskWhite.NeedsAssign(true, ForskInterior.ExteriorModeName, ForskWhite.ModeName, "RhinoView"));
        Assert.False(RoomTypes.ShowInView(true, false, 0.3, 0.2, -0.4, ForskInterior.ExteriorModeName));
        Assert.True(ForskInterior.IsRenderMode("Forsk Exterior") && ForskInterior.IsRenderMode("Forsk Interior"));
        Assert.False(ForskInterior.IsRenderMode("Rendered"));
    }

    [Fact]
    public void TheChat_KnowsBothRenders()
    {
        Assert.Contains("exterior_render", ForskToolPacks.For(ForskIntent.Edit));
    }
}
