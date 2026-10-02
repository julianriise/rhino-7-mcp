using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
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
        var view = WindowView.Build(new DocThread { Serial = 1 }, facts, "", helpOpen: true);
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
    public void TheTitleBlock_HasFiveFields_FilledFromTheDocument()
    {
        var facts = FileClassifier.Read(Docs.Of(Docs.House()).With(d => d.Meta = new Dictionary<string, string> { ["project"] = "Tilbygg Holmen", ["client"] = "Holmen" }));
        var card = ForskCards.For("meta.title", facts)!;
        Assert.Equal(new[] { "project", "client", "address", "date", "scale_label" }, card.Fields!.Select(f => f.Key));
        Assert.Equal("Tilbygg Holmen", card.Fields![0].Value);
        Assert.Equal("", card.Fields![2].Value);
        Assert.Equal("save", card.Pills[0].Id);
    }

    [Fact]
    public void PrintOneSheet_ListsThePlanElevationsSchedulesAndStoredSections()
    {
        var card = ForskCards.For("print.one", Docs.Facts("sheet cache, sections"))!;
        Assert.Equal(new[] { "plan", "north", "east", "south", "west", "schedules", "section_a", "section_b" },
            card.Pills.Where(p => p.Id != "cancel").Select(p => p.Id));
        Assert.Equal("Section A", card.Pills[6].Label);
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
        Assert.Equal(5, ((JArray)item["fields"]!).Count);
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
        Assert.Equal(new[] { "The east wall (w04) · 500 mm longer", "The west wall (w03) · 250 mm shorter", "Floor, roof and 1 room updated." }, card.Rows);
        Assert.Equal("model", card.Depends);
        Assert.Equal(new[] { "done" }, card.Pills.Select(p => p.Id));
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
}
