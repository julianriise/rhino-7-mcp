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
    public void SwapType_OnADoor_ListsFourTypes()
    {
        var card = ForskCards.For("opening.type", Docs.Facts("house, door selected"))!;
        Assert.Equal(new[] { "Hinged door", "Double door", "Sliding door", "Pocket door" }, Choices(card));
        Assert.Equal("Which door type?", card.Question);
        Assert.Equal("selection", card.Depends);
        Assert.Null(card.Data);
        // Each type pill carries its plan-symbol icon; Cancel has none.
        Assert.All(card.Pills.Where(p => p.Id != "cancel"), p => Assert.Equal(p.Id, p.Icon));
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
        Assert.All(card.Pills.Where(p => p.Id != "cancel"), p => Assert.Equal(p.Id, p.Icon));
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
    public void SwapType_OnAWindow_ListsThreeTypes()
    {
        var facts = FileClassifier.Read(Docs.Of(Docs.House().Append(Row.Window(selected: true)).ToArray()));
        var card = ForskCards.For("opening.type", facts)!;
        Assert.Equal(new[] { "Fixed window", "Side-hung window", "Top-hung window" }, Choices(card));
        Assert.Equal(new[] { "window.fixed", "window.side_hung", "window.top_hung" }, card.Pills.Take(3).Select(p => p.Id));
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
        var registry = ForskRegistry.All.Where(a => a.Group != null && a.Shows(facts)).Select(a => a.Id);
        Assert.Equal(registry.OrderBy(i => i), shown.OrderBy(i => i));
    }

    [Fact]
    public void TheInkCard_HasTheThreeNames_AndSaysWhichIsSet()
    {
        var card = ForskCards.For("ink.set", Docs.Facts("bridge down, grey ink"))!;
        Assert.Equal(new[] { "default", "grey", "hatch" }, card.Pills.Select(p => p.Id));
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
        Assert.Equal("", card.Fields![3].Value);
        Assert.Equal("Rev.", card.Fields![6].Label);
        Assert.Equal("B", card.Fields![6].Value);
        Assert.Equal("2020-01-01", card.Fields![5].Value);
        Assert.Null(card.Note);
        Assert.Null(card.Data);
        Assert.Equal(new[] { "save", "cancel" }, card.Pills.Select(p => p.Id));
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
        Assert.Equal(new[] { "scale", "front", "plan", "north", "east", "south", "west", "section_a", "schedules", "takeoff" }, card.Fields!.Select(f => f.Key));
        var scale = card.Fields![0];
        Assert.Equal("Scale", scale.Label);
        Assert.Equal("1:200", scale.Value);
        Assert.Contains("Fit", scale.Options!);
        Assert.Contains("1:125", scale.Options!);
        Assert.Contains("1:1000", scale.Options!);
        Assert.False(scale.Check || scale.Order);
        // Quantities is on the card, unticked.
        Assert.Equal("A-00-050 Quantities", card.Fields!.Last().Label);
        Assert.Equal("0", card.Fields!.Last().Value);
        Assert.All(card.Fields!.Skip(1), f => Assert.True(f.Check && f.Order));
        Assert.Equal("A-40-001 North elevation", card.Fields![3].Label);
        Assert.Equal("0", card.Fields![3].Value);
        Assert.Equal("1", card.Fields![2].Value);
        Assert.Equal("1:200 · A3", card.Note);
        // Print is filled (the first pill); then Save and Reset.
        Assert.Equal(new[] { "print", "save", "reset", "export", "export_ifc" }, card.Pills.Select(p => p.Id));
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
        // The page moved Section A–A above the plan with ↑ and posts the rows in their new order.
        var order = new JArray("front", "section_a", "plan", "north", "east", "south", "west", "section_b", "schedules", "takeoff");
        var args = ForskCards.PagesArgs("save", values, order);
        Assert.Equal(order.Select(t => t.ToString()), args["order"]!.Select(t => t.ToString()));
        Assert.Equal(new[] { "north", "takeoff" }, args["off"]!.Select(t => t.ToString()));
        Assert.DoesNotContain(args["on"]!.Select(t => t.ToString()), id => id == "scale");
        Assert.Equal(75, args["scale"]!.Value<int>());
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
}
