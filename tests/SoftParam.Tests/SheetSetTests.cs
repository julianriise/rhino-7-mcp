using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// v3 P1: the set Print PDF writes is one stored list. Forsk infers it from
/// the model in number order; the user's order and switches survive a model
/// that gains or loses a sheet.
/// </summary>
public class SheetSetTests
{
    /// <summary>The garage: walls on level 0, one door, no rooms, no section.</summary>
    static SheetSet.SetFacts Garage(params string[] sections) => new SheetSet.SetFacts
    {
        Walls = true,
        Level = 0,
        Sections = sections.ToList(),
        Lists = new List<string> { "door" }
    };

    static string[] Numbers(IEnumerable<SheetSet.Sheet> set) => set.Select(s => SheetSet.Number(s.Id, 0)).ToArray();

    static string[] Ids(IEnumerable<SheetSet.Sheet> set) => set.Select(s => s.Id).ToArray();

    [Fact]
    public void Garage_GivesTheFrontSheetThePlanTheFacadesAndTheLists_InNumberOrder()
    {
        // The set opens on the front sheet; the lists close it.
        Assert.Equal(new[] { "A-00-001", "A-20-001", "A-40-001", "A-40-002", "A-40-003", "A-40-004", "A-00-002" },
            Numbers(SheetSet.Infer(Garage())));
        Assert.All(SheetSet.Infer(Garage()), s => Assert.True(s.On));
    }

    [Fact]
    public void Sections_FollowTheFacades_ByLetter()
    {
        var set = SheetSet.Infer(Garage("B", "A"));
        Assert.Equal(new[] { "A-00-001", "A-20-001", "A-40-001", "A-40-002", "A-40-003", "A-40-004", "A-40-101", "A-40-102", "A-00-002" },
            Numbers(set));
        Assert.Equal(new[] { "front", "plan", "north", "east", "south", "west", "section_a", "section_b", "schedules" }, Ids(set));
    }

    [Fact]
    public void NoWalls_NoSet_AndNoLists_NoListsSheet()
    {
        Assert.Empty(SheetSet.Infer(new SheetSet.SetFacts { Walls = false, Lists = new List<string> { "door" } }));
        Assert.DoesNotContain("schedules", Ids(SheetSet.Infer(new SheetSet.SetFacts { Walls = true })));
    }

    [Fact]
    public void Numbers_FollowTheTypeCodes()
    {
        Assert.Equal("A-20-001", SheetSet.Number("plan", 0));
        Assert.Equal("A-20-002", SheetSet.Number("plan", 1));
        Assert.Equal("A-40-004", SheetSet.Number("west", 0));
        Assert.Equal("A-40-103", SheetSet.Number("section_c", 0));
        Assert.Equal("A-00-002", SheetSet.Number("schedules", 0));
        // A list that flows onto three pages numbers each page.
        Assert.Equal("A-00-004", SheetSet.Number("schedules", 0, 2));
        Assert.Equal("A-00-001", SheetSet.Number("front", 0));
        Assert.Equal("", SheetSet.Number("nonsense", 0));
    }

    [Fact]
    public void Titles_AreTheSheetsOwnNorwegianTitles()
    {
        Assert.Equal("Plan 1. etg", SheetSet.Title("plan", 0));
        Assert.Equal("Fasade mot øst", SheetSet.Title("east", 0));
        Assert.Equal("Snitt B–B", SheetSet.Title("section_b", 0));
        Assert.Equal("Dør-, vindus- og romliste", SheetSet.Title("schedules", 0));
        Assert.Equal("Dørliste", SheetSet.Title("schedules", 0, new[] { "door" }));
        Assert.Equal("Tegningsliste og arealer", SheetSet.Title("front", 0));
        // With no rooms there is no Arealtabell.
        Assert.Equal("Tegningsliste", SheetSet.Title("front", 0, new[] { "door" }));
    }

    [Fact]
    public void Merge_KeepsTheStoredOrder_AddsANewSectionAtItsPlace_DropsADeletedOne_KeepsOff()
    {
        // Stored when the model had A and B: section A moved before the plan, the facades' east off.
        var stored = new List<SheetSet.Sheet>
        {
            new SheetSet.Sheet("section_a", true),
            new SheetSet.Sheet("plan", true),
            new SheetSet.Sheet("north", true),
            new SheetSet.Sheet("east", false),
            new SheetSet.Sheet("south", true),
            new SheetSet.Sheet("west", true),
            new SheetSet.Sheet("section_b", true),
            new SheetSet.Sheet("schedules", true)
        };
        // Now B is gone and C is new.
        var merged = SheetSet.Merge(SheetSet.Infer(Garage("A", "C")), stored);
        // The front sheet, new since that set was stored, opens it.
        Assert.Equal(new[] { "front", "section_a", "plan", "north", "east", "south", "west", "section_c", "schedules" }, Ids(merged));
        Assert.False(merged.Single(s => s.Id == "east").On);
        Assert.True(merged.Single(s => s.Id == "section_c").On);
    }

    [Fact]
    public void Merge_WithNothingStored_IsTheInferredSet()
    {
        var inferred = SheetSet.Infer(Garage("A"));
        Assert.Equal(Ids(inferred), Ids(SheetSet.Merge(inferred, null)));
    }

    [Fact]
    public void Merge_TheFirstSheetGoesFirst_AndEachNewSheetBeforeItsNext()
    {
        var stored = new List<SheetSet.Sheet> { new SheetSet.Sheet("north", true), new SheetSet.Sheet("plan", true) };
        var merged = SheetSet.Merge(new List<SheetSet.Sheet> { new SheetSet.Sheet("plan", true), new SheetSet.Sheet("north", true) }, stored);
        Assert.Equal(new[] { "north", "plan" }, Ids(merged));
        var withSchedules = SheetSet.Merge(SheetSet.Infer(Garage()), new List<SheetSet.Sheet> { new SheetSet.Sheet("west", true) });
        Assert.Equal(new[] { "front", "plan", "north", "east", "south", "west", "schedules" }, Ids(withSchedules));
    }

    [Fact]
    public void ReadAndWrite_RoundTrip_AndBadJsonReadsAsNothingStored()
    {
        var set = new List<SheetSet.Sheet> { new SheetSet.Sheet("plan", true), new SheetSet.Sheet("north", false) };
        var json = SheetSet.Write(set);
        Assert.Equal("[{\"id\":\"plan\",\"on\":true},{\"id\":\"north\",\"on\":false}]", json);
        var back = SheetSet.Read(json)!;
        Assert.Equal(new[] { "plan", "north" }, Ids(back));
        Assert.False(back[1].On);
        Assert.Null(SheetSet.Read(""));
        Assert.Null(SheetSet.Read("not json"));
        // A row without an id is skipped, a repeated id kept once.
        Assert.Equal(new[] { "plan" }, Ids(SheetSet.Read("[{\"on\":true},{\"id\":\"plan\",\"on\":true},{\"id\":\"plan\",\"on\":false}]")!));
    }

    [Fact]
    public void Order_SortsShuffledPagesIntoSetOrder_SchedulesPagesKeepTheirOrder()
    {
        var set = SheetSet.Infer(Garage("A"));
        var pages = new[] { "schedules", "west", "section_a", "plan", "schedules", "front", "north", "east", "south" };
        var order = SheetSet.Order(pages, set);
        Assert.Equal(new[] { "front", "plan", "north", "east", "south", "west", "section_a", "schedules", "schedules" },
            order.Select(i => pages[i]).ToArray());
        // The first schedules page stays before the second.
        Assert.Equal(new[] { 0, 4 }, order.Where(i => pages[i] == "schedules").ToArray());
    }

    [Fact]
    public void Order_PutsAPageThatIsNotInTheSet_Last()
    {
        var set = SheetSet.Infer(Garage());
        var pages = new[] { "section_z", "north", "plan" };
        Assert.Equal(new[] { "plan", "north", "section_z" }, SheetSet.Order(pages, set).Select(i => pages[i]).ToArray());
    }

    [Fact]
    public void TheFrontSheetAndTheLists_AreListSheets_WithNoDetail()
    {
        Assert.True(SheetSet.IsListSheet("front"));
        Assert.True(SheetSet.IsListSheet("schedules"));
        Assert.False(SheetSet.IsListSheet("plan"));
        Assert.False(SheetSet.IsListSheet("section_a"));
    }

    [Fact]
    public void RePrintingOneSheet_DoesNotMoveTheOthers()
    {
        // Rhino lists the pages as they were added: the re-printed plan comes last.
        var set = SheetSet.Infer(Garage("A"));
        var before = new[] { "plan", "north", "east", "south", "west", "section_a", "schedules" };
        var after = new[] { "north", "east", "south", "west", "section_a", "schedules", "plan" };
        Assert.Equal(SheetSet.Order(before, set).Select(i => before[i]), SheetSet.Order(after, set).Select(i => after[i]));
    }
}
