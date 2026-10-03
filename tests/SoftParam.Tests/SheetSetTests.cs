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
        // The set opens on the front sheet; the lists close it, then the Mengdeliste, off.
        Assert.Equal(new[] { "A-00-001", "A-20-001", "A-40-001", "A-40-002", "A-40-003", "A-40-004", "A-00-002", "A-00-050" },
            Numbers(SheetSet.Infer(Garage())));
        Assert.All(SheetSet.Infer(Garage()).Where(s => s.Id != "takeoff"), s => Assert.True(s.On));
        Assert.False(SheetSet.Infer(Garage()).Single(s => s.Id == "takeoff").On);
    }

    [Fact]
    public void Sections_FollowTheFacades_ByLetter()
    {
        var set = SheetSet.Infer(Garage("B", "A"));
        Assert.Equal(new[] { "A-00-001", "A-20-001", "A-40-001", "A-40-002", "A-40-003", "A-40-004", "A-40-101", "A-40-102", "A-00-002", "A-00-050" },
            Numbers(set));
        Assert.Equal(new[] { "front", "plan", "north", "east", "south", "west", "section_a", "section_b", "schedules", "takeoff" }, Ids(set));
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
        // The Mengdeliste keeps its number however many pages the lists take.
        Assert.Equal("A-00-050", SheetSet.Number("takeoff", 0));
        Assert.Equal("", SheetSet.Number("nonsense", 0));
    }

    [Fact]
    public void Titles_AreEnglish()
    {
        Assert.Equal("Ground floor plan", SheetSet.Title("plan", 0));
        Assert.Equal("1st floor plan", SheetSet.Title("plan", 1));
        Assert.Equal("East elevation", SheetSet.Title("east", 0));
        Assert.Equal("Section B–B", SheetSet.Title("section_b", 0));
        Assert.Equal("Door, window and room schedule", SheetSet.Title("schedules", 0));
        Assert.Equal("Door schedule", SheetSet.Title("schedules", 0, new[] { "door" }));
        Assert.Equal("Drawing list and areas", SheetSet.Title("front", 0));
        // With no rooms there is no Areas table.
        Assert.Equal("Drawing list", SheetSet.Title("front", 0, new[] { "door" }));
        Assert.Equal("Quantities", SheetSet.Title("takeoff", 0));
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
        Assert.Equal(new[] { "front", "section_a", "plan", "north", "east", "south", "west", "section_c", "schedules", "takeoff" }, Ids(merged));
        // A sheet new to a stored set comes in at its default: the Mengdeliste, off.
        Assert.False(merged.Single(s => s.Id == "takeoff").On);
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
        Assert.Equal(new[] { "front", "plan", "north", "east", "south", "west", "schedules", "takeoff" }, Ids(withSchedules));
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
        Assert.True(SheetSet.IsListSheet("takeoff"));
        Assert.False(SheetSet.IsListSheet("plan"));
        Assert.False(SheetSet.IsListSheet("section_a"));
    }

    [Fact]
    public void Apply_TurnsSheetsOffAndOn_AndMovesTheNamedOnes_TheRestKeepTheirOrder()
    {
        var set = SheetSet.Infer(Garage("A"));
        var applied = SheetSet.Apply(set, new[] { "north" }, new[] { "east", "west" }, new[] { "section_a", "plan" }, out var unknown);
        Assert.Empty(unknown);
        // Section A goes where the earliest of the two stood, before the plan.
        Assert.Equal(new[] { "front", "section_a", "plan", "north", "east", "south", "west", "schedules", "takeoff" }, Ids(applied));
        Assert.Equal(new[] { "east", "west", "takeoff" }, applied.Where(s => !s.On).Select(s => s.Id));
        // A whole order is the whole order.
        var all = new[] { "takeoff", "schedules", "west", "south", "east", "north", "section_a", "plan", "front" };
        Assert.Equal(all, Ids(SheetSet.Apply(set, null, null, all, out _)));
    }

    [Fact]
    public void Apply_NamesAnIdThatIsNoSheet_AndChangesNothingForIt()
    {
        var set = SheetSet.Infer(Garage());
        var applied = SheetSet.Apply(set, null, new[] { "section_q", "north" }, null, out var unknown);
        Assert.Equal(new[] { "section_q" }, unknown);
        Assert.False(applied.Single(s => s.Id == "north").On);
    }

    [Fact]
    public void Summary_IsOneLine_FacadesByName()
    {
        var set = SheetSet.Infer(Garage("A"));
        Assert.Equal("Set: 8 sheets.", SheetSet.Summary(set));
        var noFacades = SheetSet.Apply(set, null, SheetSet.Facades.ToList(), null, out _);
        Assert.Equal("Set: 4 sheets, facades off.", SheetSet.Summary(noFacades));
        var oneOff = SheetSet.Apply(set, null, new[] { "section_a" }, null, out _);
        Assert.Equal("Set: 7 sheets, Section A–A off.", SheetSet.Summary(oneOff));
        // Quantities is off by default: named only when it is on.
        Assert.Equal("Set: 9 sheets, with Quantities.", SheetSet.Summary(SheetSet.Apply(set, new[] { "takeoff" }, null, null, out _)));
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

    /// <summary>Bokmål stays available. Production omits the flag and prints English.</summary>
    [Fact]
    public void Norwegian_StaysAvailable_WhenAsked()
    {
        Assert.Equal("Plan 1. etg", SheetSet.Title("plan", 0, null, true));
        Assert.Equal("Fasade mot sør", OpeningTypes.ViewTitle("south", 0, true));
        Assert.Equal("Snitt A–A", Sections.Title("A", true));
        Assert.Equal("Tegningsliste og arealer", SheetSet.Title("front", 0, null, true));
        Assert.Equal("Tegningsliste", SheetSet.Title("front", 0, new[] { "door" }, true));
        Assert.Equal("Mengdeliste", SheetSet.Title("takeoff", 0, null, true));
        Assert.Equal("Dørliste", Schedules.SheetTitle(new[] { "door" }, true));
        Assert.Equal("Dør-, vindus- og romliste", Schedules.SheetTitle(new[] { "door", "window", "room" }, true));
        Assert.Equal("ca. 12,4 m²", OpeningTypes.RoomTag(12_400_000, true));
        Assert.Equal("Fri høyde 2400", Sections.ClearHeightText(2400, true));
        var levels = Sections.Levels(new[] { 0.0 }, -400, 3000, 3000, true);
        Assert.Contains(levels, l => l.Text == "1. etg ±0");
        Assert.Contains(levels, l => l.Text == "Terreng -400");
        Assert.Contains(levels, l => l.Text == "Gesims/møne +3000");

        var area = AreaStats.Compute(new[]
        {
            new AreaStats.Room { Id = "a", Name = "Stue", Level = "0", AreaMm2 = 20_000_000 }
        });
        var table = Schedules.AreaTable(area, true);
        Assert.Equal("Arealer", table.Title);
        Assert.Equal("1. etasje", table.Rows[0][0]);
        Assert.StartsWith(Schedules.AreaNoteNb, table.Note);

        var takeoff = Takeoff.Compute(
            new[] { new Takeoff.Wall { Rings = WallJoinsTests.Garage(), ThicknessMm = 200, HeightMm = 3000 } },
            null, null, null, null, 1.0, true);
        Assert.Equal("Yttervegger", takeoff.Lines[0].Group);
        Assert.Equal("Mengdeliste", Takeoff.Table(takeoff, true).Title);
        Assert.Contains("(forts.)", Takeoff.Table(takeoff, true).ContinuedSuffix);
    }
}
