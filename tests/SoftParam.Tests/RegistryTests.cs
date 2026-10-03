using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F1.1: the registry rules, checked over every fixture document. A new
/// job adds registry entries; these tests keep the bar and the card one
/// read of the same list.
/// </summary>
public class RegistryTests
{
    static IEnumerable<(string Name, FileFacts Facts)> Every() =>
        Docs.All.Select(d => (d.Name, FileClassifier.Read(d.Make())));

    [Fact]
    public void EveryAction_IsTrueInAtLeastOneFixture()
    {
        var never = ForskRegistry.All
            .Where(a => !Every().Any(d => a.Shows(d.Facts)))
            .Select(a => a.Id)
            .ToList();
        Assert.Empty(never);
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void EveryBarAction_AppearsOnTheCard(string name)
    {
        var f = Docs.Facts(name);
        var card = ForskRegistry.Card(f).Actions.Select(a => a.Id).ToList();
        foreach (var slot in ForskRegistry.Bar(f).Slots)
            Assert.Contains(slot.Id, card);
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void NoTwoActionsOnScreen_ShareALabel(string name)
    {
        // Wall and opening Move (and Delete) keep V3.md's label: a selection is
        // a wall or an opening, never both, so the two are never true together.
        var f = Docs.Facts(name);
        var card = ForskRegistry.Card(f).Actions.Select(a => a.Label).ToList();
        Assert.Equal(card.Count, card.Distinct().Count());
        var bar = ForskRegistry.Bar(f).Slots.Select(a => a.Label).ToList();
        Assert.Equal(bar.Count, bar.Distinct().Count());
    }

    [Fact]
    public void ActionsThatShareALabel_AreNeverTrueTogether()
    {
        var shared = ForskRegistry.All.GroupBy(a => a.Label).Where(g => g.Count() > 1).ToList();
        Assert.Equal(new[] { "Move", "Delete" }, shared.Select(g => g.Key).OrderByDescending(k => k));
        foreach (var group in shared)
            foreach (var (name, facts) in Every())
                Assert.True(group.Count(a => a.When(facts)) <= 1, group.Key + " twice in " + name);
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void AskActions_HideWhenTheKeyIsAbsent(string name)
    {
        var f = Docs.Facts(name);
        f.KeyPresent = false;
        Assert.DoesNotContain(ForskRegistry.Bar(f).Slots, a => a.Runs == Runs.Ask);
        Assert.DoesNotContain(ForskRegistry.Card(f).Actions, a => a.Runs == Runs.Ask);
        Assert.Contains("hint.key", ForskRegistry.Card(f).Hints);
    }

    [Fact]
    public void AskActions_ShowWithTheKey()
    {
        Assert.Contains(ForskRegistry.Card(Docs.Facts("house, wall selected")).Actions, a => a.Id == "opening.add_door");
        Assert.Contains(ForskRegistry.Card(Docs.Facts("rooms, no window")).Actions, a => a.Id == "daylight.window");
        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts("rooms, no window, no key")).Actions, a => a.Id == "daylight.window");
        Assert.Contains("hint.windows", ForskRegistry.Card(Docs.Facts("rooms, no window, no key")).Hints);
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void Delete_IsNeverSlot1OrSlot2(string name)
    {
        var slots = ForskRegistry.Bar(Docs.Facts(name)).Slots.Take(2).Select(a => a.Id).ToList();
        Assert.DoesNotContain("opening.delete", slots);
        Assert.DoesNotContain("wall.delete", slots);
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void WhileAWallOrAnOpeningIsSelected_NoFileLevelActionFillsASlot(string name)
    {
        var f = Docs.Facts(name);
        if (f.Picked != Picked.Wall && f.Picked != Picked.Opening) return;
        var fileLevel = new[]
        {
            "edit.undo", "section.add", "file.generate", "file.check", "file.draw", "daylight.again",
            "daylight.hide", "daylight.show", "daylight.run", "daylight.window", "daylight.rooms"
        };
        Assert.DoesNotContain(ForskRegistry.Bar(f).Context, a => fileLevel.Contains(a.Id));
    }

    [Fact]
    public void Delete_IsOnTheCard_WhenSomethingIsSelected()
    {
        Assert.Contains(ForskRegistry.Card(Docs.Facts("house, door selected")).Actions, a => a.Id == "opening.delete");
        Assert.Contains(ForskRegistry.Card(Docs.Facts("house, wall selected")).Actions, a => a.Id == "wall.delete");
    }

    [Fact]
    public void SplitWalls_IsOnTheCard_ForAWholeRecord_ForTheModeller()
    {
        var f = Docs.Facts("one whole wall record");
        Assert.True(f.WholeWalls);
        var group = ForskRegistry.Card(f).Groups.Single(g => g.Actions.Any(a => a.Id == "wall.split"));
        Assert.Equal(ForskText.Get("group.model"), group.Title);
        Assert.Equal("Split walls for picking", ForskText.Label("wall.split"));
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("wall.split"));
        // Walls baked one per run have nothing to split.
        Assert.False(Docs.Facts("house").WholeWalls);
        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts("house")).Actions, a => a.Id == "wall.split");
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void SplitWalls_IsNeverInTheBar(string name)
    {
        Assert.DoesNotContain(ForskRegistry.Bar(Docs.Facts(name)).Slots, a => a.Id == "wall.split");
    }

    [Fact]
    public void DragWall_IsOnTheCard_ForOneWall_AndNeverInTheBar()
    {
        var one = Docs.Facts("house, wall selected");
        var card = ForskRegistry.Card(one).Actions.Select(a => a.Id).ToList();
        Assert.Contains("wall.drag", card);
        Assert.True(card.IndexOf("wall.move") < card.IndexOf("wall.drag"));
        Assert.True(card.IndexOf("wall.drag") < card.IndexOf("wall.delete"));
        Assert.Equal(new[] { "file.print", "wall.move", "opening.add_door" }, ForskRegistry.Bar(one).Slots.Select(a => a.Id));
        Assert.Equal("Drag wall", ForskText.Label("wall.drag"));
        Assert.Equal("Dra vegg", ForskText.Get("wall.drag.nb"));
        Assert.Equal("Drag the wall in the view, or type a distance. Esc cancels.", ForskText.Get("wall.drag.prompt"));
        Assert.Equal("Dra veggen i visningen, eller skriv en avstand. Esc avbryter.", ForskText.Get("wall.drag.prompt.nb"));
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("wall.drag"));
        Assert.Contains(ForskRegistry.Card(Docs.Facts("one whole wall record selected")).Actions, a => a.Id == "wall.drag");

        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts("house")).Actions, a => a.Id == "wall.drag");
        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts("house, room selected")).Actions, a => a.Id == "wall.drag");
        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts("house, door selected")).Actions, a => a.Id == "wall.drag");
        var two = FileClassifier.Read(Docs.Of(Row.Wall(selected: true), Row.Wall(stamp: "w02", selected: true), Row.Floor()));
        Assert.Equal(Picked.Wall, two.Picked);
        Assert.Equal(2, two.PickedCount);
        Assert.DoesNotContain(ForskRegistry.Card(two).Actions, a => a.Id == "wall.drag");
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void DragWall_IsNeverInTheBar(string name)
    {
        Assert.DoesNotContain(ForskRegistry.Bar(Docs.Facts(name)).Slots, a => a.Id == "wall.drag");
        Assert.NotEqual("wall.drag", ForskRegistry.Slot1(Docs.Facts(name)).Id);
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void Slot1_ReadsTheFileOnly(string name)
    {
        var input = Docs.All.Single(d => d.Name == name).Make();
        var slot1 = ForskRegistry.Slot1(FileClassifier.Read(input)).Id;
        foreach (var row in input.Rows) row.Selected = false;
        input.UndoNewest = !input.UndoNewest;
        input.KeyPresent = !input.KeyPresent;
        Assert.Equal(slot1, ForskRegistry.Slot1(FileClassifier.Read(input)).Id);
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void TheBar_IsSlot1AndAtMostTwoMore_NeverTheHelpCard(string name)
    {
        var bar = ForskRegistry.Bar(Docs.Facts(name));
        Assert.NotNull(bar.Slot1);
        Assert.InRange(bar.Context.Count, 0, 2);
        Assert.DoesNotContain(bar.Slots, a => a.Id == "help.card");
        Assert.DoesNotContain(ForskRegistry.Card(Docs.Facts(name)).Actions, a => a.Id == "help.card");
        Assert.False(string.IsNullOrWhiteSpace(bar.Reason));
    }

    [Fact]
    public void EveryAction_HasItsLabelInTheOneList_AndSlot1ActionsAReason()
    {
        foreach (var action in ForskRegistry.All)
            Assert.True(ForskText.Has(action.Id), action.Id + " has no label");
        foreach (var id in new[] { "file.import", "file.use_curves", "file.scale", "file.generate", "file.rebuild", "file.print" })
            Assert.True(ForskText.Has(id + ".reason"), id + " has no reason");
        foreach (var group in ForskRegistry.GroupOrder)
            Assert.True(ForskText.Has(group), group);
        Assert.All(ForskRegistry.All.Where(a => a.Id != "help.card"), a => Assert.Contains(a.Group, ForskRegistry.GroupOrder));
    }

    [Fact]
    public void TheCard_ForAnEmptyFile_A2DPlan_AndA3DModel()
    {
        string[] Ids(string name) => ForskRegistry.Card(Docs.Facts(name)).Actions.Select(a => a.Id).ToArray();

        Assert.Equal(new[] { "file.import", "file.draw", "meta.title" }, Ids("empty"));
        Assert.Equal(new[] { "file.import", "file.draw", "file.generate", "meta.title" }, Ids("plan curves"));
        Assert.Equal(new[] { "file.rebuild", "rooms.list", "area.stats", "file.print", "print.one", "meta.title", "daylight.run", "section.add", "ink.set" },
            Ids("house"));
    }
}
