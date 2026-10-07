using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D2: card contents from the fixtures. A card is a one-time question in
/// the thread: answered once, or grey and dead once the state moved.
/// </summary>
public class CardTests
{
    static string[] Choices(CardSpec card) => card.Pills.Where(p => p.Id != "cancel" && p.Id != "done").Select(p => p.Label).ToArray();

    [Fact]
    public void SwapType_OnADoor_ListsDoorTypes_ThenWindowTypes()
    {
        var card = ForskCards.For("opening.type", Docs.Facts("house, door selected"))!;
        Assert.Equal(new[] { "Hinged door", "Double door", "Sliding door", "Pocket door", "Fixed window", "Side-hung window", "Top-hung window" }, Choices(card));
        Assert.Equal("Change to:", card.Question);
        Assert.Equal("selection", card.Depends);
        Assert.Null(card.Data);
        // Each type pill carries its plan-symbol icon; Confirm and Cancel have none.
        Assert.All(card.Pills.Where(p => p.Id != "cancel" && p.Id != "done"), p => Assert.Equal(p.Id, p.Icon));
        Assert.Null(card.Pills.Single(p => p.Id == "cancel").Icon);
    }

    /// <summary>Nothing picked: one card with every type of the kinds in the file, and a pill means all of that kind.</summary>
    [Fact]
    public void ChangeType_WithNothingPicked_ListsBothKinds_ForAll()
    {
        var facts = Docs.Facts("house");
        Assert.Equal(Picked.None, facts.Picked);
        var card = ForskCards.For("opening.type", facts)!;
        Assert.Equal("Change all windows or all doors to:", card.Question);
        Assert.Equal(OpeningTypes.All.Select(t => t.Label), Choices(card));
        Assert.All(card.Pills.Where(p => p.Id != "cancel" && p.Id != "done"), p => Assert.Equal(p.Id, p.Icon));
        Assert.True(card.Data!["all"]!.Value<bool>());
        Assert.Equal("selection", card.Depends);

        // A file with doors only lists door types.
        var doors = ForskCards.For("opening.type", Docs.Facts("rooms, no window"))!;
        Assert.Equal("Change all doors to:", doors.Question);
        Assert.Equal(new[] { "Hinged door", "Double door", "Sliding door", "Pocket door" }, Choices(doors));

        Assert.Null(ForskCards.For("opening.type", Docs.Facts("walls only")));
    }

    [Fact]
    public void ThePillIcon_ReachesThePage()
    {
        var thread = new DocThread();
        var facts = Docs.Facts("house, door selected");
        var card = thread.AddCard(ForskCards.SwapType(facts)!, facts);
        var pills = (JArray)card["pills"]!;
        Assert.Equal("door.sliding", pills.Single(p => p["id"]!.ToString() == "door.sliding")["icon"]!.ToString());
        Assert.Null(pills.Single(p => p["id"]!.ToString() == "cancel")["icon"]);
    }

    [Fact]
    public void SwapType_OnWindows_ListsWindowTypes_ThenDoorTypes()
    {
        var facts = FileClassifier.Read(Docs.Of(Docs.House().Append(Row.Window(selected: true)).ToArray()));
        var card = ForskCards.For("opening.type", facts)!;
        Assert.Equal(new[] { "Fixed window", "Side-hung window", "Top-hung window", "Hinged door", "Double door", "Sliding door", "Pocket door" }, Choices(card));
        Assert.Equal(new[] { "window.fixed", "window.side_hung", "window.top_hung" }, card.Pills.Take(3).Select(p => p.Id));
        Assert.Equal("Change to:", card.Question);
    }

    [Fact]
    public void SwapType_IsNotOffered_ForADoorAndAWindowTogether()
    {
        var facts = FileClassifier.Read(Docs.Of(Docs.House(doorSelected: true).Append(Row.Window(selected: true)).ToArray()));
        Assert.Null(facts.PickedOpeningKind);
        Assert.False(ForskRegistry.Find("opening.type")!.When(facts));
        Assert.Null(ForskCards.SwapType(facts));

        // Walls with openings is not an opening pick either.
        var mixed = FileClassifier.Read(Docs.Of(Docs.House(wallSelected: true, doorSelected: true)));
        Assert.False(ForskRegistry.Find("opening.type")!.When(mixed));
        Assert.Null(ForskCards.SwapType(mixed));
    }

    [Fact]
    public void TheReviewCard_IsAbsentWhenNoReviewIsStored()
    {
        Assert.Null(ForskCards.Review(Docs.Facts("scaled, no curves")));
        Assert.Null(ForskCards.Review(Docs.Facts("plan curves")));
        var card = ForskCards.Review(Docs.Facts("scaled, reviewed"))!;
        Assert.Equal(new[] { "2 doors without a swing", "the kitchen is open" }, card.Rows);
        Assert.Equal("file.check", card.Kind);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("plan curves")]
    [InlineData("house")]
    public void TheHelpRows_ForAnEmptyFileA2DPlanAndA3DModel_MatchTheRegistry(string fixture)
    {
        var facts = Docs.Facts(fixture);
        var view = WindowView.Build(new DocThread { Serial = 1 }, facts, helpOpen: true);
        var shown = view["help"]!["groups"]!.SelectMany(g => g["actions"]!).Select(a => a["id"]!.ToString());
        var registry = ForskRegistry.All.Where(a => a.Group != null && !ForskRegistry.OffSheet.Contains(a.Group) && a.Shows(facts)).Select(a => a.Id);
        Assert.Equal(registry.OrderBy(i => i), shown.OrderBy(i => i));
    }

    [Fact]
    public void TheInkCard_HasTheThreeNames_AndSaysWhichIsSet()
    {
        var card = ForskCards.For("ink.set", Docs.Facts("bridge down, grey ink"))!;
        Assert.Equal(new[] { "default", "grey", "hatch", "done" }, card.Pills.Select(p => p.Id));
        Assert.Equal("Now: grey.", card.Note);
    }

    [Fact]
    public void TheProjectInfoCard_HasTheSevenFields_AndIgnoresTheScaleLabel()
    {
        var facts = FileClassifier.Read(Docs.Of(Docs.House()).With(d => d.Meta = new Dictionary<string, string>
        {
            ["project"] = "Tilbygg Holmen",
            ["client"] = "Holmen",
            ["revision"] = "B",
            ["date"] = "2020-01-01",
            ["scale_label"] = "1:50"
        }));
        var card = ForskCards.For("meta.title", facts)!;
        Assert.Equal(new[] { "project", "project_no", "client", "address", "architect", "date", "revision" }, card.Fields!.Select(f => f.Key));
        Assert.Equal("Tilbygg Holmen", card.Fields![0].Value);
Assert.Equal("Project name", card.Fields![0].Placeholder);
        Assert.Equal("", card.Fields![3].Value);
        Assert.Equal("Address", card.Fields![3].Placeholder);
        Assert.Equal("Rev.", card.Fields![6].Label);
        Assert.Null(card.Fields![6].Placeholder);
        Assert.Equal("B", card.Fields![6].Value);
        Assert.Equal("2020-01-01", card.Fields![5].Value);
        Assert.Null(card.Note);
        Assert.Null(card.Data);
        Assert.Equal(new[] { "save", "logo", "cancel" }, card.Pills.Select(p => p.Id));
        Assert.Equal("Choose logo", card.Pills[1].Label);
    }

    /// <summary>A file with a logo: the card names it, offers Change logo and Remove logo.</summary>
    [Fact]
    public void TheProjectInfoCard_NamesTheLogo_AndOffersChangeAndRemove()
    {
        var facts = House(new Dictionary<string, string> { ["project"] = "Holmen", [OfficeLogo.NameKey] = "office.png" });
        var card = ForskCards.For("meta.title", facts)!;
        Assert.Equal(new[] { "save", "logo", "logo_remove", "cancel" }, card.Pills.Select(p => p.Id));
        Assert.Equal("Change logo", card.Pills[1].Label);
        Assert.Equal("Remove logo", card.Pills[2].Label);
        Assert.Equal("Logo: office.png", card.Note);
        // The ask-once card before a Print keeps its own pills.
        Assert.DoesNotContain(ForskCards.AskInfoFirst(House(new()), "file.print")!.Pills, p => p.Id == "logo");
    }

    static FileFacts House(Dictionary<string, string> meta, string firm = null) =>
        FileClassifier.Read(Docs.Of(Docs.House()).With(d => { d.Meta = meta; d.FirmArchitect = firm; }));

    /// <summary>N1: no project name and never asked: Print posts the Project info card first, with the action it stands in front of.</summary>
    [Fact]
    public void APrint_WithNoProjectAndNoFlag_AsksForTheProjectInfo_First()
    {
        var card = ForskCards.AskInfoFirst(House(new()), "file.print")!;
        Assert.Equal("meta.title", card.Kind);
        Assert.Equal("Project info for the title blocks. Asked once.", card.Question);
        Assert.Equal(7, card.Fields!.Count);
        Assert.Equal(new[] { "Save and print", "Print without", "Cancel" }, card.Pills.Select(p => p.Label));
        Assert.Equal(new[] { "save", "skip", "cancel" }, card.Pills.Select(p => p.Id));
        Assert.Equal("file.print", card.Data!["pending"]!.ToString());

        var one = ForskCards.AskInfoFirst(House(new()), "print.one", "plan")!;
        Assert.Equal("plan", one.Data!["view"]!.ToString());
        var export = ForskCards.AskInfoFirst(House(new()), "export.dwg")!;
        Assert.Equal(new[] { "Save and export", "Export without", "Cancel" }, export.Pills.Select(p => p.Label));
    }

    [Fact]
    public void APrint_RunsDirectly_WithAProjectName_OrOnceAsked()
    {
        Assert.Null(ForskCards.AskInfoFirst(House(new() { ["project"] = "Garage" }), "file.print"));
        Assert.Null(ForskCards.AskInfoFirst(House(new() { ["info_asked"] = "1" }), "file.print"));
        Assert.Null(ForskCards.AskInfoFirst(House(new() { ["info_asked"] = "1" }), "export.dwg"));
        Assert.NotNull(ForskCards.AskInfoFirst(House(new() { ["client"] = "Holmen" }), "file.print"));
    }

    [Fact]
    public void TheArchitect_PrefillsFromTheFirm_UnlessTheFileHasOne()
    {
        string Architect(CardSpec card) => card.Fields!.Single(f => f.Key == "architect").Value;
        Assert.Equal("Riise Arkitekter", Architect(ForskCards.AskInfoFirst(House(new(), "Riise Arkitekter"), "file.print")!));
        Assert.Equal("Holmen Ark", Architect(ForskCards.For("meta.title", House(new() { ["architect"] = "Holmen Ark" }, "Riise Arkitekter"))!));
    }

    [Fact]
    public void TheTitleBlock_ShowsEnglishHints_AndTreatsTheNorwegianSeedsAsEmpty()
    {
        var today = new DateTime(2026, 10, 5);
        var empty = ForskCards.For("meta.title", Docs.Facts("empty"), today)!;
        Assert.Equal(new[] { "", "", "", "", "", "2026-10-05", "" }, empty.Fields!.Select(f => f.Value));
        Assert.Equal(new[] { "Project name", null, "Client", "Address", null, null, null }, empty.Fields!.Select(f => f.Placeholder));

        var seeded = FileClassifier.Read(Docs.Of(Docs.House()).With(d => d.Meta = new Dictionary<string, string>
        {
            ["project"] = "Min tittel",
            ["client"] = "Klient",
            ["address"] = "Adresse"
        }));
        var card = ForskCards.For("meta.title", seeded, today)!;
        Assert.Equal(new[] { "", "", "", "", "", "2026-10-05", "" }, card.Fields!.Select(f => f.Value));
        Assert.Equal("Project name", card.Fields![0].Placeholder);
        Assert.Equal("Client", card.Fields![2].Placeholder);
        Assert.Equal("Address", card.Fields![3].Placeholder);
        Assert.Null(card.Fields!.Single(f => f.Key == "date").Placeholder);
    }

    [Fact]
    public void TheCard_ShowsTheNameAndToday_AsValues()
    {
        var today = new DateTime(2026, 10, 5);
        var card = ForskCards.AskInfoFirst(House(new(), "Julian Riise"), "file.print", today: today)!;
        var architect = card.Fields!.Single(f => f.Key == "architect");
        var date = card.Fields!.Single(f => f.Key == "date");
        Assert.Equal("Julian Riise", architect.Value);
        Assert.Null(architect.Placeholder);
        Assert.Equal("2026-10-05", date.Value);
        Assert.Null(date.Placeholder);
        Assert.Equal("Project name", card.Fields!.Single(f => f.Key == "project").Placeholder);
        Assert.Equal("", card.Fields!.Single(f => f.Key == "project").Value);

        var stored = ForskCards.For("meta.title", House(new() { ["date"] = "2020-01-01" }, "Julian Riise"), today)!;
        Assert.Equal("2020-01-01", stored.Fields!.Single(f => f.Key == "date").Value);
    }

    [Fact]
    public void ASavedForm_CollapsesToOneReceipt_WithoutTheSavePill()
    {
        var info = new JObject { ["project"] = "Test house", ["project_no"] = "2026-07", ["architect"] = "Julian Riise", ["date"] = "2026-10-05" };
        Assert.Equal("Project info saved · Test house, 2026-07", ForskCards.FormReceipt("meta.title", "save", info));
        Assert.Equal("Project info skipped", ForskCards.FormReceipt("meta.title", "skip", info));
        Assert.Null(ForskCards.FormReceipt("opening.type", "save", info));

        var stair = new JObject { ["width"] = "1000", ["riser_max"] = "180", ["going"] = "270" };
        Assert.Equal("Stair sizes saved · 1000 × 180 × 270", ForskCards.FormReceipt("stair.edit", "save", stair));
        Assert.Equal("Stair flipped", ForskCards.FormReceipt("stair.edit", "flip", stair));

        var sheets = new JObject { ["scale"] = "1:200", ["plan"] = "1" };
        Assert.Equal("Sheets saved · 1:200", ForskCards.FormReceipt("print.pages", "save", sheets));
        Assert.Equal("Sheets saved · 1:200", ForskCards.FormReceipt("print.pages", "print", sheets));
        Assert.Equal("Sheet set reset", ForskCards.FormReceipt("print.pages", "reset", sheets));
        Assert.Equal("Export IFC", ForskCards.FormReceipt("print.pages", "export_ifc", sheets));
    }

    [Fact]
    public void PrintOneSheet_ListsTheSet_InSetOrder_WithNumberAndTitle()
    {
        var card = ForskCards.For("print.one", Docs.Facts("sheet cache, sections"))!;
        Assert.Equal(new[] { "front", "plan", "north", "east", "south", "west", "section_a", "section_b", "schedules", "takeoff" },
            card.Pills.Where(p => p.Id != "cancel").Select(p => p.Id));
        Assert.Equal("A-00-001 Drawing list and areas", card.Pills[0].Label);
        Assert.Equal("A-20-001 Ground floor plan", card.Pills[1].Label);
        Assert.Equal("A-40-101 Section A–A", card.Pills[6].Label);
    }

    [Fact]
    public void ChooseSheets_ListsTheSet_OneTickPerSheet_InSetOrder_WithOrderArrows()
    {
        var facts = FileClassifier.Read(Docs.Of(Docs.House()).With(d =>
        {
            d.SectionLetters = new List<string> { "A" };
            d.PrintScale = 200;
            d.PrintPages = "[{\"id\":\"front\",\"on\":true},{\"id\":\"plan\",\"on\":true},{\"id\":\"north\",\"on\":false}]";
        }));
        var card = ForskCards.For("print.pages", facts)!;
        Assert.Equal("print.pages", card.Kind);
        Assert.Equal("model", card.Depends);
        Assert.Equal(new[] { "scale", "paper", "front", "plan", "north", "east", "south", "west", "section_a", "schedules", "takeoff" }, card.Fields!.Select(f => f.Key));
        var scale = card.Fields![0];
        Assert.Equal("Scale", scale.Label);
        Assert.Equal("1:200", scale.Value);
        Assert.Contains("Fit", scale.Options!);
        Assert.Contains("1:125", scale.Options!);
        Assert.Contains("1:1000", scale.Options!);
        Assert.False(scale.Check || scale.Order);
        var paper = card.Fields![1];
        Assert.Equal("Paper", paper.Label);
        Assert.Equal("A3", paper.Value);
        Assert.Equal(new[] { "A4", "A3", "A2", "A1" }, paper.Options!);
        Assert.False(paper.Check || paper.Order);
        // Quantities is on the card, unticked.
        Assert.Equal("A-00-050 Quantities", card.Fields!.Last().Label);
        Assert.Equal("0", card.Fields!.Last().Value);
        Assert.All(card.Fields!.Skip(2), f => Assert.True(f.Check && f.Order));
        Assert.Equal("A-40-001 North elevation", card.Fields![4].Label);
        Assert.Equal("0", card.Fields![4].Value);
        Assert.Equal("1", card.Fields![3].Value);
        Assert.Equal("1:200 · A3", card.Note);
        // Print is filled (the first pill); then Save and Reset.
        Assert.Equal(new[] { "print", "save", "reset", "export", "export_ifc", "export_csv" }, card.Pills.Select(p => p.Id));
        // No coordinates and no ids beyond the sheet number.
        Assert.DoesNotContain(card.Fields!, f => f.Label.Contains("section_") || f.Label.Contains("("));
    }

    [Fact]
    public void ChooseSheets_ListsTheDetailSheets_BetweenTheSectionsAndTheLists()
    {
        var facts = FileClassifier.Read(Docs.Of(Docs.House()).With(d =>
        {
            d.SectionLetters = new List<string> { "A" };
            d.DetailSheets = new List<string> { "detail_20_2", "detail_20_1" };
        }));
        var card = ForskCards.For("print.pages", facts)!;
        var keys = card.Fields!.Select(f => f.Key).ToList();
        Assert.Equal(new[] { "section_a", "detail_20_1", "detail_20_2", "schedules" }, keys.Skip(keys.IndexOf("section_a")).Take(4));
        Assert.Equal("A-50-001 Details 1:20", card.Fields!.Single(f => f.Key == "detail_20_1").Label);
        Assert.Equal("A-50-002 Details 1:20", card.Fields!.Single(f => f.Key == "detail_20_2").Label);
        Assert.Equal("1", card.Fields!.Single(f => f.Key == "detail_20_2").Value);
    }

    [Fact]
    public void ChooseSheets_WithNoScaleKnown_SaysItFits()
    {
        var card = ForskCards.For("print.pages", Docs.Facts("house"))!;
        Assert.Equal("Scale picked to fit · A3", card.Note);
        Assert.Equal("Fit", card.Fields![0].Value);
        Assert.Null(ForskCards.For("print.pages", Docs.Facts("empty")));
    }

    [Fact]
    public void ChooseSheets_PostingAReorderedList_WritesThatOrder()
    {
        var facts = Docs.Facts("sheet cache, sections");
        var values = new JObject();
        foreach (var sheet in ForskCards.Set(facts)) values[sheet.Id] = "1";
        values["north"] = "0";
        values["takeoff"] = "0";
        values["scale"] = "1:75";
        values["paper"] = "A2";
        // The page moved Section A–A above the plan with ↑ and posts the rows in their new order.
        var order = new JArray("front", "section_a", "plan", "north", "east", "south", "west", "section_b", "schedules", "takeoff");
        var args = ForskCards.PagesArgs("save", values, order);
        Assert.Equal(order.Select(t => t.ToString()), args["order"]!.Select(t => t.ToString()));
        Assert.Equal(new[] { "north", "takeoff" }, args["off"]!.Select(t => t.ToString()));
        Assert.DoesNotContain(args["on"]!.Select(t => t.ToString()), id => id == "scale");
        Assert.Equal(75, args["scale"]!.Value<int>());
        Assert.Equal("A2", args["paper"]!.ToString());
        Assert.DoesNotContain(args["on"]!.Select(t => t.ToString()), id => id == "paper");
        Assert.Equal(0, ForskCards.PagesArgs("save", new JObject { ["scale"] = "Fit", ["plan"] = "1" }, null)["scale"]!.Value<int>());
        var applied = SheetSet.Apply(ForskCards.Set(facts), args["on"]!.Select(t => t.ToString()).ToList(),
            args["off"]!.Select(t => t.ToString()).ToList(), args["order"]!.Select(t => t.ToString()).ToList(), out _);
        Assert.Equal(order.Select(t => t.ToString()), applied.Select(s => s.Id));
        Assert.False(applied.Single(s => s.Id == "north").On);
        // Reset clears the stored set.
        Assert.True(ForskCards.PagesArgs("reset", values, order)["reset"]!.Value<bool>());
    }

    [Fact]
    public void TheTakeoffCard_OneRowPerLine_AndDone()
    {
        var envelope = JObject.Parse("{\"status\":\"success\",\"result\":{\"message\":\"Quantities, approx.: exterior walls 23,2 m.\","
            + "\"rows\":[\"Exterior wall 200 mm · 23,2 m · 67,7 m² · 13,5 m³\",\"Hinged door 900 × 2100 · 1 no. · 1,9 m²\"]}}");
        var card = ForskCards.Takeoff(envelope)!;
        Assert.Equal("takeoff", card.Kind);
        Assert.Equal(new[] { "Exterior wall 200 mm · 23,2 m · 67,7 m² · 13,5 m³", "Hinged door 900 × 2100 · 1 no. · 1,9 m²" }, card.Rows);
        Assert.Equal("model", card.Depends);
        Assert.Equal(new[] { "done" }, card.Pills.Select(p => p.Id));
        Assert.Null(ForskCards.Takeoff(JObject.Parse("{\"status\":\"error\",\"message\":\"No active document.\"}")));
    }

    [Fact]
    public void PrintOneSheet_ListsASheetThatIsOff_AndKeepsTheStoredOrder()
    {
        var facts = FileClassifier.Read(Docs.Of(Docs.House()).With(d =>
            d.PrintPages = "[{\"id\":\"north\",\"on\":false},{\"id\":\"plan\",\"on\":true}]"));
        var card = ForskCards.For("print.one", facts)!;
        Assert.Equal(new[] { "front", "north", "plan", "east", "south", "west", "schedules", "takeoff" },
            card.Pills.Where(p => p.Id != "cancel").Select(p => p.Id));
    }

    [Fact]
    public void RemoveASection_OffersEachLetter_AndAll()
    {
        var card = ForskCards.For("section.remove", Docs.Facts("sheet cache, sections"))!;
        Assert.Equal(new[] { "A", "B", "all", "cancel" }, card.Pills.Select(p => p.Id));
        Assert.Null(ForskCards.For("section.remove", Docs.Facts("house")));
    }

    [Fact]
    public void ListRooms_ShowsNamesAndAreas()
    {
        var rooms = new[] { Row.Wall(), Row.Room(name: "Stue"), Row.Room(name: "Bad") };
        rooms[1].Area = "24500000";
        var card = ForskCards.For("rooms.list", FileClassifier.Read(Docs.Of(rooms)))!;
        Assert.Equal(new[] { "Stue · 24.5 m²", "Bad" }, card.Rows);
    }

    [Theory]
    [InlineData(3, 3, null)]
    [InlineData(30, 24, "Showing the first 24 pages.")]
    public void APdfOfSeveralPages_AsksWhichIsThePlan(int pages, int pills, string? note)
    {
        var card = ForskCards.PdfPage("/plans/holmen.pdf", pages);
        Assert.Equal("holmen.pdf has " + pages + " pages. Which is the plan?", card.Question);
        Assert.Equal(pills, card.Pills.Count(p => p.Id != "cancel"));
        Assert.Equal("Page 1", card.Pills[0].Label);
        Assert.Equal(note, card.Note);
        Assert.Equal("/plans/holmen.pdf", card.Data!["pdf_path"]!.ToString());
    }

    [Fact]
    public void EveryCardAction_HasItsCardInAFixture()
    {
        var cards = ForskRegistry.All.Where(a => a.Runs == Runs.Card && a.Id != "help.card" && a.Id != "bridge.start");
        foreach (var action in cards)
            Assert.True(Docs.All.Any(d => { var f = FileClassifier.Read(d.Make()); return action.Shows(f) && ForskCards.For(action.Id, f) != null; }), action.Id);
    }

    [Fact]
    public void AnUnansweredCard_TurnsGreyWhenTheStateChanges_AndTakesNoClick()
    {
        var thread = new DocThread();
        var doorSelected = Docs.Facts("house, door selected");
        var card = thread.AddCard(ForskCards.SwapType(doorSelected)!, doorSelected);
        var id = card["id"]!.ToString();

        Assert.Equal(0, thread.StaleCards(doorSelected));
        Assert.Equal("open", card["state"]!.ToString());

        var nothingSelected = FileClassifier.Read(Docs.Of(Docs.House()));
        Assert.Equal(1, thread.StaleCards(nothingSelected));
        Assert.Equal("stale", card["state"]!.ToString());
        Assert.Null(thread.Answer(id, "door.sliding"));
    }

    [Fact]
    public void ACardThatDependsOnNothing_StaysOpenWhenTheModelChanges()
    {
        var thread = new DocThread();
        var before = Docs.Facts("house");
        var card = thread.AddCard(ForskCards.TitleBlock(before)!, before);
        var after = FileClassifier.Read(Docs.Of(Docs.House().Append(Row.Map()).ToArray()));
        Assert.Equal(0, thread.StaleCards(after));
        Assert.Equal("open", card["state"]!.ToString());
    }

    [Fact]
    public void ACardOnTheModel_GoesStaleWhenAnObjectChanges()
    {
        var thread = new DocThread();
        var rows = Docs.House();
        var before = FileClassifier.Read(Docs.Of(rows));
        var card = thread.AddCard(ForskCards.PrintOne(before)!, before);
        rows[0].Stamp = "w01 moved";
        Assert.Equal(1, thread.StaleCards(FileClassifier.Read(Docs.Of(rows))));
        Assert.Equal("stale", card["state"]!.ToString());
    }

    [Fact]
    public void TheCardsJson_CarriesFieldsRowsNoteAndData()
    {
        var thread = new DocThread();
        var item = thread.AddCard(ForskCards.TitleBlock(Docs.Facts("house"))!, Docs.Facts("house"));
Assert.Equal(new[] { "Project", "Project no.", "Client", "Address", "Architect", "Date", "Rev." }, ((JArray)item["fields"]!).Select(f => f!["label"]!.ToString()));
        Assert.Equal("Project name", item["fields"]![0]!["placeholder"]!.ToString());
        Assert.True(item["fields"]![6]!["placeholder"] == null);
        Assert.Equal("Project", item["fields"]![0]!["label"]!.ToString());
        Assert.Equal("none", item["depends"]!.ToString());
        var review = thread.AddCard(ForskCards.Review(Docs.Facts("scaled, reviewed"))!, null);
        Assert.Equal(2, ((JArray)review["rows"]!).Count);
    }

    [Fact]
    public void TheStatusLine_HasHooksForARenderJobAndAGrade_EmptyForNow()
    {
        var facts = Docs.Facts("house");
        Assert.Equal("", ForskRegistry.Status(facts));
        facts.RenderJob = "Render: 2 of 2";
        facts.Grade = "Grade: concept";
        Assert.Equal("Render: 2 of 2 · Grade: concept", ForskRegistry.Status(facts));
    }

    /// <summary>A recorded move_wall envelope: four records, the north one moved, east and west followed.</summary>
    static JObject MoveResult(JArray followed, params string[] records) => new JObject
    {
        ["status"] = "success",
        ["result"] = new JObject
        {
            ["forsk_id"] = "w01",
            ["wall"] = "the north wall",
            ["followed"] = followed,
            ["records"] = new JArray(records),
            ["rebuilt"] = "Floor, roof and 1 room updated.",
            ["message"] = "Moved the north wall of w01 500 mm north; the east and west walls followed. Floor, roof and 1 room updated."
        }
    };

    static JArray EastAndWest() => new JArray
    {
        new JObject { ["forsk_id"] = "w04", ["wall"] = "the east wall", ["change_mm"] = 500 },
        new JObject { ["forsk_id"] = "w03", ["wall"] = "the west wall", ["change_mm"] = -250 }
    };

    [Fact]
    public void WallReview_ListsTheWallsThatFollowed_ThenWhatWasRebuilt()
    {
        var card = ForskCards.WallReview(MoveResult(EastAndWest(), "w01", "w03", "w04"))!;
        Assert.Equal("wall.review", card.Kind);
        Assert.Equal("What followed the wall", card.Question);
        Assert.Equal(new[] { "The east wall · 500 mm longer", "The west wall · 250 mm shorter", "Floor, roof and 1 room updated." }, card.Rows);
        Assert.Equal("model", card.Depends);
        Assert.Equal(new[] { "done" }, card.Pills.Select(p => p.Id));
        Assert.Equal("Done", card.Pills[0].Label);
        Assert.DoesNotContain("w04", string.Join("\n", card.Rows));
    }

    [Fact]
    public void WallReview_NamesInnerWallsByRoom_AndNeverPrintsCoordinates()
    {
        var followed = new JArray
        {
            new JObject { ["forsk_id"] = "w02", ["wall"] = "the wall at (4000, 2000)", ["label"] = "wall between Kitchen and Bedroom", ["change_mm"] = 0 },
            new JObject { ["forsk_id"] = "w05", ["wall"] = "the wall at (11375, 21735)", ["change_mm"] = 100 }
        };
        var card = ForskCards.WallReview(MoveResult(followed, "w01", "w02"))!;
        Assert.Equal(new[]
        {
            "Wall between Kitchen and Bedroom",
            "Inner wall 2 · 100 mm longer",
            "Floor, roof and 1 room updated."
        }, card.Rows);
        Assert.DoesNotContain("11375", string.Join("\n", card.Rows));
        Assert.DoesNotContain("(w0", string.Join("\n", card.Rows));
    }

    [Fact]
    public void WallReview_Norwegian_NamesSides_AndKeepsDone()
    {
        var card = ForskCards.WallReview(MoveResult(EastAndWest(), "w01", "w03", "w04"), true)!;
        Assert.Equal("Hva som fulgte veggen", card.Question);
        Assert.Equal("Done", card.Pills[0].Label);
        Assert.Equal(new[] { "Østveggen · 500 mm lengre", "Vestveggen · 250 mm kortere", "Floor, roof and 1 room updated." }, card.Rows);
    }

    [Fact]
    public void WallReview_IsAbsent_WhenOnlyTheWallChanged_OrTheEditFailed()
    {
        Assert.Null(ForskCards.WallReview(MoveResult(new JArray(), "w01")));
        Assert.Null(ForskCards.WallReview(new JObject { ["status"] = "error", ["message"] = "Not moved." }));
        Assert.Null(ForskCards.WallReview(null));
        // Two records written with no named neighbour still changed more than the wall.
        Assert.NotNull(ForskCards.WallReview(MoveResult(new JArray(), "w01", "w02")));
    }

    [Fact]
    public void MoveReceipt_StaysTwoSentences_WithTheRecordInBold()
    {
        var receipt = ForskReceipt.From("move_wall", MoveResult(EastAndWest(), "w01", "w03", "w04"));
        Assert.True(receipt.Ok);
        Assert.Equal("w01", receipt.Subject);
        Assert.Equal("Moved the north wall of w01 500 mm north; the east and west walls followed. Floor, roof and 1 room updated.", receipt.Text);
    }

    /// <summary>One tick per detail, named as the model names its element; Save removes the unticked ones.</summary>
    [Fact]
    public void TheDetailList_HasARowPerDetail()
    {
        var card = ForskCards.For("detail.list", Docs.Facts("house, two details"));
        Assert.Equal("detail.list", card.Kind);
        Assert.Equal("model", card.Depends);
        Assert.Equal(new[] { "DET01", "DET02" }, card.Fields.Select(f => f.Key));
        Assert.Equal(new[] { "North wall · plan, section", "Door D01 · plan, elevation, section" }, card.Fields.Select(f => f.Label));
        Assert.All(card.Fields, f => Assert.True(f.Check && f.Value == "1"));
        Assert.Equal(new[] { "save", "remove_all", "cancel" }, card.Pills.Select(p => p.Id));
        Assert.Equal(new[] { "Save", "Remove all", "Cancel" }, card.Pills.Select(p => p.Label));
        Assert.Null(ForskCards.For("detail.list", Docs.Facts("house")));
        Assert.Equal(new JArray("DET02"), ForskCards.Unticked(new JObject { ["DET01"] = "1", ["DET02"] = "0" }));
    }

    /// <summary>
    /// Settings → Grok API key: one masked field, Save, Remove only while
    /// ~/.forsk/grok.env holds a key, and Cancel. Only the last four show.
    /// </summary>
    [Fact]
    public void GrokKey_HasAMaskedField_SaveRemoveCancel_AndShowsOnlyTheLastFour()
    {
        var none = ForskCards.GrokKey(null, stored: false);
        Assert.Equal("grok.key", none.Kind);
        Assert.Equal("Your own xAI Grok API key for chat. It stays on this Mac.", none.Question);
        Assert.Equal("No key is set. Get one at https://console.x.ai.", none.Note);
        Assert.Equal(new[] { "save", "cancel" }, none.Pills.Select(p => p.Id));
        var field = Assert.Single(none.Fields);
        Assert.Equal("key", field.Key);
        Assert.Equal("Key", field.Label);
        Assert.Equal("", field.Value);
        Assert.True(field.Secret);

        var set = ForskCards.GrokKey("xai-abcdefgh9xYz", stored: true);
        Assert.Equal("A key ending in 9xYz is set. Get a new one at https://console.x.ai.", set.Note);
        Assert.Equal(new[] { "save", "remove", "cancel" }, set.Pills.Select(p => p.Id));
        Assert.Equal(new[] { "Save", "Remove", "Cancel" }, set.Pills.Select(p => p.Label));
        // A key from the environment is set but not in the file: there is nothing to remove.
        Assert.Equal(new[] { "save", "cancel" }, ForskCards.GrokKey("xai-abcdefgh9xYz", stored: false).Pills.Select(p => p.Id));

        var item = new DocThread().AddCard(set, null);
        Assert.True(item["fields"]![0]!["secret"]!.Value<bool>());
        Assert.Equal("", item["fields"]![0]!["value"]!.ToString());
        Assert.DoesNotContain("abcdefgh", item.ToString());
        Assert.Null(new DocThread().AddCard(ForskCards.TitleBlock(Docs.Facts("house"))!, null)["fields"]![0]!["secret"]);

        Assert.Equal("Grok key saved", ForskCards.FormReceipt("grok.key", "save", new JObject { ["key"] = "xai-abcdefgh9xYz" }));
        Assert.Equal("Grok key removed", ForskCards.FormReceipt("grok.key", "remove", new JObject { ["key"] = "" }));
        Assert.Null(ForskCards.FormReceipt("grok.key", "cancel", new JObject()));
    }

    /// <summary>
    /// Settings → Set up Forsk: a row for chat and a row for daylight and AI
    /// detection, each with its state. The pills are what is still open, the
    /// most needed first, then Done. No key: Add Grok key and Get a key.
    /// </summary>
    [Fact]
    public void Setup_HasAChatRowAndAToolsRow_AndThePillsStillOpen()
    {
        var fresh = ForskCards.Setup(null, SetupState.From(uvFound: false));
        Assert.Equal("forsk.setup", fresh.Kind);
        Assert.Equal("Set up Forsk on this Mac. Each part works on its own.", fresh.Question);
        Assert.Equal(new[] { "Chat · Needs your Grok key", "Daylight & AI detection · Not set up", "Account · Not connected" }, fresh.Rows);
        Assert.Equal(new[] { "uv", "key", "get_key", "connect", "done" }, fresh.Pills.Select(p => p.Id));
        Assert.Equal(new[] { "Set up", "Add Grok key", "Get a key", "Connect", "Done" }, fresh.Pills.Select(p => p.Label));
        Assert.Equal("Chat uses your own xAI Grok key from https://console.x.ai. Set up downloads uv and Python once (about 40 MB) for daylight and AI detection. Connect links this Mac to your forsk.app account; it is optional.", fresh.Note);

        var ready = ForskCards.Setup("xai-abcdefgh9xYz", SetupState.From(uvFound: true));
        Assert.Equal(new[] { "Chat · Ready · key ending in 9xYz", "Daylight & AI detection · Ready", "Account · Not connected" }, ready.Rows);
        Assert.Equal(new[] { "Change Grok key", "Connect", "Done" }, ready.Pills.Select(p => p.Label));
        Assert.DoesNotContain("abcdefgh", string.Join(" ", ready.Rows) + ready.Note);

        var running = ForskCards.Setup("xai-abcdefgh9xYz", SetupState.From(false).Start().Step("Checking the download"));
        Assert.Equal("Daylight & AI detection · Setting up… Checking the download", running.Rows[1]);
        Assert.Equal(new[] { "key", "connect", "done" }, running.Pills.Select(p => p.Id));

        var failed = ForskCards.Setup("xai-abcdefgh9xYz", SetupState.From(false).Start().Fail("No internet connection: try again when online."));
        Assert.Equal("Daylight & AI detection · Failed: No internet connection: try again when online.", failed.Rows[1]);
        Assert.Equal(new[] { "Try again", "Change Grok key", "Connect", "Done" }, failed.Pills.Select(p => p.Label));

        // Account: the code while the browser confirms, then who is connected, with Disconnect instead of Connect.
        var waiting = ForskCards.Setup("xai-abcdefgh9xYz", SetupState.From(true), AccountState.From(null).Start("K7M2-QX4P"));
        Assert.Equal("Account · Code K7M2-QX4P · confirm it in your browser", waiting.Rows[2]);
        Assert.Equal(new[] { "key", "done" }, waiting.Pills.Select(p => p.Id));
        var connected = ForskCards.Setup("xai-abcdefgh9xYz", SetupState.From(true), AccountState.From(new AccountFile { Token = "t", Email = "julian@forsk.app", Plan = "Early tester" }));
        Assert.Equal("Account · Connected as julian@forsk.app · Early tester", connected.Rows[2]);
        Assert.Equal(new[] { "Change Grok key", "Disconnect", "Done" }, connected.Pills.Select(p => p.Label));
        var expired = ForskCards.Setup("xai-abcdefgh9xYz", SetupState.From(true), AccountState.From(null).Start("K7M2-QX4P").Fail("the code expired, click Connect again."));
        Assert.Equal("Account · Not connected: the code expired, click Connect again.", expired.Rows[2]);
        Assert.Equal(new[] { "Change Grok key", "Connect again", "Done" }, expired.Pills.Select(p => p.Label));
    }

    /// <summary>An install step rewrites the open setup card in place: rows, pills and note. Answered cards keep what they showed.</summary>
    [Fact]
    public void Refresh_RewritesOpenCardsOfTheKind_Only()
    {
        var thread = new DocThread();
        var open = thread.AddCard(ForskCards.Setup(null, SetupState.From(false)), null);
        var answered = thread.AddCard(ForskCards.Setup(null, SetupState.From(false)), null);
        thread.Answer(answered["id"]!.ToString(), "done");
        var other = thread.AddCard(ForskCards.GrokKey(null, stored: false), null);

        Assert.Equal(1, thread.Refresh(ForskCards.Setup("xai-abcdefgh9xYz", SetupState.From(false).Start())));
        Assert.Equal(new JArray("Chat · Ready · key ending in 9xYz", "Daylight & AI detection · Setting up… Downloading uv", "Account · Not connected"), open["rows"]);
        Assert.Equal(new JArray("key", "connect", "done"), new JArray(((JArray)open["pills"]!).Select(p => p["id"])));
        Assert.Equal("open", open["state"]!.ToString());
        Assert.Equal("Daylight & AI detection · Not set up", answered["rows"]![1]!.ToString());
        Assert.Null(other["rows"]);
    }
}
