using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Saved views in the view picker (Julian, 2026-10-09): Save current view
/// keeps the viewport as a Rhino named view, the picker lists the file's
/// named views under the built-in ones, and a card renames or deletes them.
/// </summary>
public class SavedViewsTests
{
    [Fact]
    public void ANewView_TakesTheFirstFreeNumber()
    {
        Assert.Equal("View 1", SavedViews.NextName(new string[0]));
        Assert.Equal("View 2", SavedViews.NextName(new[] { "view 1", "Living" }));
        Assert.Equal("View 2", SavedViews.NextName(new[] { "View 1", "View 3" }));
    }

    [Fact]
    public void ThePicker_ListsNamedViews_ButNotTheRenderViews()
    {
        var listed = SavedViews.Listed(new[] { "Kitchen corner", "Living", " ", "kitchen corner", "From the south" }, new[] { "living", "From the south" });
        Assert.Equal(new[] { "Kitchen corner" }, listed);
    }

    [Fact]
    public void APickedId_NamesItsSavedView()
    {
        Assert.True(SavedViews.TryName("saved:View 1", out var name));
        Assert.Equal("View 1", name);
        Assert.False(SavedViews.TryName("saved: ", out _));
        Assert.False(SavedViews.TryName("plan", out _));
        Assert.False(SavedViews.TryName("interior:Living", out _));
    }

    [Fact]
    public void TheControl_ListsSavedViews_WithSaveAndRename()
    {
        var control = ViewPicker.Control("plan", saved: new[] { "View 1", "Entrance" });
        Assert.Equal(new[] { "saved:View 1", "saved:Entrance" }, control["saved"]!.Select(o => o["id"]!.ToString()));
        Assert.Equal(new[] { "View 1", "Entrance" }, control["saved"]!.Select(o => o["label"]!.ToString()));
        Assert.Equal(SavedViews.SaveId, control["save"]!["id"]!.ToString());
        Assert.Equal("Save current view", control["save"]!["label"]!.ToString());
        Assert.Equal(SavedViews.EditId, control["edit"]!["id"]!.ToString());
    }

    [Fact]
    public void WithNoSavedView_OnlySaveIsOffered()
    {
        var control = ViewPicker.Control("plan");
        Assert.Empty(control["saved"]!);
        Assert.NotNull(control["save"]);
        Assert.Null(control["edit"]);
    }

    [Fact]
    public void ASavedViewShownNow_IsTheValueAndTheLabel()
    {
        var control = ViewPicker.Control("saved:Entrance", saved: new[] { "Entrance" });
        Assert.Equal("saved:Entrance", control["value"]!.ToString());
        Assert.Equal("Entrance", control["label"]!.ToString());
        // A name no longer saved is no value.
        var gone = ViewPicker.Control("saved:Entrance", saved: new string[0]);
        Assert.Equal("", gone["value"]!.ToString());
    }

    [Fact]
    public void TheWindow_CarriesTheSavedViews()
    {
        var facts = Docs.Facts("house");
        facts.SavedViews = new List<string> { "View 1" };
        facts.View = "saved:View 1";
        var view = WindowView.Build(new DocThread(), facts)["view"]!;
        Assert.Equal("View 1", view["label"]!.ToString());
        Assert.Equal("saved:View 1", view["saved"]![0]!["id"]!.ToString());
    }

    [Fact]
    public void TheCard_HasOneNameFieldPerView_SaveAndCancel()
    {
        var card = ForskCards.SavedViews(new[] { "View 1", "Entrance" })!;
        Assert.Equal(SavedViews.CardKind, card.Kind);
        Assert.Equal(new[] { "view0", "view1" }, card.Fields.Select(f => f.Key));
        Assert.Equal(new[] { "View 1", "Entrance" }, card.Fields.Select(f => f.Value));
        Assert.Equal(new[] { "save", "cancel" }, card.Pills.Select(p => p.Id));
        Assert.Equal(new[] { "View 1", "Entrance" }, card.Data!["names"]!.Select(n => n.ToString()));
        Assert.False(string.IsNullOrEmpty(card.Note));
        Assert.Null(ForskCards.SavedViews(new string[0]));
    }

    [Fact]
    public void AnEmptiedName_Deletes_AndANewName_Renames()
    {
        var changes = SavedViews.Edits(new[] { "View 1", "View 2", "View 3" }, new[] { "  Kitchen   corner ", "", "View 3" }, null, out var refused);
        Assert.Empty(refused);
        Assert.Equal(2, changes.Count);
        Assert.Equal("View 1", changes[0].From);
        Assert.Equal("Kitchen corner", changes[0].To);
        Assert.Equal("View 2", changes[1].From);
        Assert.True(changes[1].Deletes);
    }

    [Fact]
    public void ATakenName_IsRefused_AndThatViewKeepsItsName()
    {
        // Another kept view has it, a render view has it, or two views were given it.
        var changes = SavedViews.Edits(new[] { "View 1", "View 2" }, new[] { "view 2", "View 2" }, null, out var refused);
        Assert.Empty(changes);
        Assert.Equal(new[] { "view 2" }, refused);

        changes = SavedViews.Edits(new[] { "View 1" }, new[] { "Living" }, new[] { "Living" }, out refused);
        Assert.Empty(changes);
        Assert.Equal(new[] { "Living" }, refused);

        changes = SavedViews.Edits(new[] { "View 1", "View 2" }, new[] { "Hall", "Hall" }, null, out refused);
        Assert.Single(changes);
        Assert.Equal(new[] { "Hall" }, refused);
    }

    [Fact]
    public void ADeletedViewsName_IsFreeForAnother_AndCaseAloneRenames()
    {
        var changes = SavedViews.Edits(new[] { "Hall", "View 2" }, new[] { "", "Hall" }, null, out var refused);
        Assert.Empty(refused);
        Assert.Equal(2, changes.Count);
        Assert.True(changes[0].Deletes);
        Assert.Equal("Hall", changes[1].To);

        changes = SavedViews.Edits(new[] { "hall" }, new[] { "Hall" }, null, out refused);
        Assert.Empty(refused);
        Assert.Equal("Hall", changes.Single().To);
    }

    [Fact]
    public void ThePage_SendsSaveAndRename_AsViewPicks()
    {
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("view && view.saved", js);
        Assert.Contains("[view && view.save, view && view.edit]", js);
        Assert.Contains("sender.send({ kind: 'view', view: row.id })", js);
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.html"));
        Assert.Contains("id=\"icon-bookmark-plus\"", html);
        Assert.Contains("id=\"icon-pencil\"", html);
    }
}
