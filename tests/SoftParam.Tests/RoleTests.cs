using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D3: the role on each answer comes from the router; a manual pick is an
/// override the user can see and clear. Slot 1 and the one history do not
/// change with it.
/// </summary>
public class RoleTests
{
    [Theory]
    [InlineData("ta bort døra")]
    [InlineData("remove the door")]
    [InlineData("move the window 200 along the wall")]
    public void ASetWord_NeedsASheetWord_AnEditStaysOffTheSheetsTurn(string sentence)
    {
        Assert.NotEqual(ForskIntent.Sheets, ForskIntentRouter.Classify(sentence));
    }

    [Theory]
    [InlineData("import plan.pdf", ForskIntent.Import, "Planner")]
    [InlineData("import plan.dxf", ForskIntent.Dxf, "Planner")]
    [InlineData("generate the model with walls 2700", ForskIntent.Build, "Modeller")]
    [InlineData("move the window 200 along the wall", ForskIntent.Edit, "Modeller")]
    [InlineData("skriv ut", ForskIntent.Print, "Plotter")]
    [InlineData("add a door schedule", ForskIntent.Sheets, "Plotter")]
    [InlineData("drop the facades from the set", ForskIntent.Sheets, "Plotter")]
    [InlineData("ta bort fasadene", ForskIntent.Sheets, "Plotter")]
    [InlineData("fjern snitt A fra settet", ForskIntent.Sheets, "Plotter")]
    [InlineData("put section A before the plan", ForskIntent.Sheets, "Plotter")]
    [InlineData("endre rekkefølgen på arkene", ForskIntent.Sheets, "Plotter")]
    [InlineData("run daylight", ForskIntent.Daylight, "Analyser")]
    [InlineData("clear daylight", ForskIntent.Daylight, "Analyser")]
    [InlineData("is this room dark", ForskIntent.Daylight, "Analyser")]
    [InlineData("dagslys", ForskIntent.Daylight, "Analyser")]
    [InlineData("how big is the flat", ForskIntent.Area, "Analyser")]
    [InlineData("areal per etasje", ForskIntent.Area, "Analyser")]
    [InlineData("BRA", ForskIntent.Area, "Analyser")]
    [InlineData("hvor mange m² yttervegg", ForskIntent.Takeoff, "Plotter")]
    [InlineData("hvor mye vegg er det", ForskIntent.Takeoff, "Plotter")]
    [InlineData("how much wall is there", ForskIntent.Takeoff, "Plotter")]
    [InlineData("m² of wall", ForskIntent.Takeoff, "Plotter")]
    [InlineData("takeoff", ForskIntent.Takeoff, "Plotter")]
    [InlineData("lag en mengdeliste", ForskIntent.Takeoff, "Plotter")]
    [InlineData("ta med mengdelisten", ForskIntent.Sheets, "Plotter")]
    [InlineData("hvor stor er leiligheten", ForskIntent.Area, "Analyser")]
    [InlineData("how do I print", ForskIntent.Support, "Support")]
    [InlineData("how do I set the scale", ForskIntent.Support, "Support")]
    [InlineData("what does daylight do", ForskIntent.Support, "Support")]
    [InlineData("hvordan skriver jeg ut", ForskIntent.Support, "Support")]
    [InlineData("this is broken", ForskIntent.Support, "Support")]
    [InlineData("feil", ForskIntent.Support, "Support")]
    [InlineData("it would be nice if the walls hatched", ForskIntent.Support, "Support")]
    [InlineData("can you add a stair tool", ForskIntent.Support, "Support")]
    public void EachIntent_MapsToItsRole(string sentence, ForskIntent intent, string role)
    {
        Assert.Equal(intent, ForskIntentRouter.Classify(sentence));
        Assert.Equal(role, ForskRoles.Mark(intent, ForskRole.None));
        Assert.Equal(role, ForskRoles.Label(ForskRoles.Of(intent)));
    }

    [Theory]
    [InlineData("Make this window 2000 mm tall")]
    [InlineData("resize this window")]
    [InlineData("swap this door")]
    [InlineData("delete the opening")]
    [InlineData("add a window")]
    [InlineData("move the door")]
    [InlineData("lag dette vinduet 2000 mm høyt")]
    [InlineData("flytt vinduet")]
    [InlineData("bytt døren")]
    [InlineData("slett vinduet")]
    [InlineData("legg til et vindu")]
    [InlineData("gjør vinduet høyere")]
    public void AnEditSentence_GoesToModeller(string said)
    {
        var intent = ForskIntentRouter.Classify(said);
        Assert.Equal(ForskIntent.Edit, intent);
        Assert.False(ForskIntentRouter.IsBug(said));
        Assert.Equal("Modeller", ForskRoles.Mark(intent, ForskRole.None));
        Assert.Equal("Modeller", ForskRoles.Mark(intent, ForskRole.Support));
        Assert.Equal(ForskRole.None, ForskRoles.Turn(ForskRole.Support, intent));
        Assert.False(ForskReports.EndsWithReport(ForskRole.Support, said));
    }

    [Fact]
    public void Support_KeepsAQuestionABugAndAFeatureRequest()
    {
        Assert.Equal(ForskIntent.Support, ForskIntentRouter.Classify("how do I print"));
        Assert.Equal(ForskIntent.Support, ForskIntentRouter.Classify("this is broken"));
        Assert.Equal(ForskIntent.Support, ForskIntentRouter.Classify("can you add a stair tool"));
        Assert.Equal("Support", ForskRoles.Mark(ForskIntent.Support, ForskRole.Support));
        Assert.Equal(ForskRole.Support, ForskRoles.Turn(ForskRole.Support, ForskIntent.Support));
        Assert.Equal("Modeller", ForskRoles.Mark(ForskIntent.Import, ForskRole.Modeller));
    }

    [Fact]
    public void AGeneralTurn_HasNoMark_AndNoIntentIsRenderYet()
    {
        Assert.Null(ForskRoles.Mark(ForskIntentRouter.Classify("hello"), ForskRole.None));
        Assert.DoesNotContain(Enum.GetValues(typeof(ForskIntent)).Cast<ForskIntent>(), i => ForskRoles.Of(i) == ForskRole.Render);
        // No intent routes to Render. The picker still offers it, as a label and an avatar.
        Assert.Contains(ForskRole.Render, ForskRoles.Pickable);
    }

    [Fact]
    public void AnOverride_NamesTheAnswer_AndClearingItHandsTheNextAnswerBackToTheRouter()
    {
        var import = ForskIntentRouter.Classify("import plan.pdf");
        Assert.Equal("Modeller", ForskRoles.Mark(import, ForskRole.Modeller));
        Assert.Equal("Planner", ForskRoles.Mark(import, ForskRoles.Parse("auto")));
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void OverrideOnAndOff_LeavesSlot1Unchanged(string fixture)
    {
        var facts = Docs.Facts(fixture);
        var slot1 = ForskRegistry.Bar(facts).Slot1.Id;
        foreach (var role in Enum.GetValues(typeof(ForskRole)).Cast<ForskRole>())
        {
            Assert.Equal(slot1, ForskRegistry.Bar(facts, role).Slot1.Id);
            Assert.Equal(ForskRegistry.Bar(facts).Reason, ForskRegistry.Bar(facts, role).Reason);
        }
    }

    [Fact]
    public void TheOverride_IsASmallBoost_OnlySlot3CanChange()
    {
        var facts = Docs.Facts("forsk undo newest");
        string[] Ids(ForskRole role) => ForskRegistry.Bar(facts, role).Slots.Select(a => a.Id).ToArray();

        Assert.Equal(new[] { "file.print", "edit.undo", "daylight.run" }, Ids(ForskRole.None));
        Assert.Equal(new[] { "file.print", "edit.undo", "section.add" }, Ids(ForskRole.Plotter));
        Assert.Equal(new[] { "file.print", "edit.undo", "daylight.run" }, Ids(ForskRole.Modeller));
        // Analyser knows daylight.run, which is already slot 3. Print stays slot 1.
        Assert.Equal(new[] { "file.print", "edit.undo", "daylight.run" }, Ids(ForskRole.Analyser));
        // Support has no bar action, so slot 3 stays. Print stays slot 1.
        Assert.Equal(new[] { "file.print", "edit.undo", "daylight.run" }, Ids(ForskRole.Support));
    }

    [Fact]
    public void TheDrawnBar_IsTheBarTheShortcutsFire()
    {
        // Cmd+3 and a typed label read the bar with the role boost, as the page draws it.
        var thread = new DocThread { Serial = 1, Override = ForskRole.Plotter };
        var facts = Docs.Facts("forsk undo newest");
        var drawn = ((JArray)WindowView.Build(thread, facts)["bar"]!["slots"]!).Select(s => s["id"]!.ToString());
        Assert.Equal(ForskRegistry.Bar(facts, thread.Override).Slots.Select(a => a.Id), drawn);
        Assert.Equal("section.add", ForskRegistry.ByLabel(ForskRegistry.Bar(facts, thread.Override), "Add a section")!.Id);
        Assert.Null(ForskRegistry.ByLabel(ForskRegistry.Bar(facts, thread.Override), "Daylight"));
    }

    [Fact]
    public void StaleSheets_SetPrintsReason_WhateverTheRole()
    {
        var facts = Docs.Facts("printed, then edited");
        var bar = ForskRegistry.Bar(facts, ForskRole.Modeller);
        Assert.Equal("file.print", bar.Slot1.Id);
        Assert.Equal("Sheets are older than the model.", bar.Reason);
    }

    [Fact]
    public void TheHistory_IsOneList_AcrossRoles()
    {
        var thread = new DocThread { Serial = 1, File = "holmen.3dm" };
        var history = thread.History;

        thread.Override = ForskRole.Plotter;
        thread.Add("user", "project is Tilbygg Holmen");
        thread.BeginReply(ForskRoles.Mark(ForskIntentRouter.Classify("project is Tilbygg Holmen"), thread.Override));
        history.Add(new JObject { ["role"] = "user", ["content"] = "project is Tilbygg Holmen" });
        history.Add(new JObject { ["role"] = "assistant", ["content"] = "Stored the project name." });
        thread.Add("assistant", "Stored the project name.");
        thread.EndReply();

        thread.Override = ForskRole.None;
        thread.Add("user", "move the north wall 500 mm north");
        thread.BeginReply(ForskRoles.Mark(ForskIntentRouter.Classify("move the north wall 500 mm north"), thread.Override));
        // The window hands the turn a copy of this one list: what Plotter heard, Modeller hears.
        var forTheTurn = new List<JObject>(thread.History);
        Assert.Contains(forTheTurn, m => m["content"]!.ToString() == "project is Tilbygg Holmen");

        Assert.Same(history, thread.History);
        var marks = thread.Items.Where(i => i["mark"] != null).Select(i => i["mark"]!.ToString());
        Assert.Equal(new[] { "Plotter" }, marks);
        Assert.Equal("Modeller", thread.TurnMark);
        Assert.Equal(2, thread.History.Count);
    }

    [Fact]
    public void OnlyTheFirstReplyOfATurn_CarriesTheMark_AndTheThinkingLineShowsIt()
    {
        var thread = new DocThread { Serial = 1 };
        thread.Add("user", "move this window 500 mm along the wall");
        thread.BeginReply("Modeller");
        thread.Thinking = true;
        Assert.Equal("Modeller", thread.ToJson()["busy"]!["mark"]!.ToString());
        thread.Add(new ForskReceipt { Ok = true, Subject = "V01", Text = "Moved V01 500 mm along w01." });
        thread.Add("assistant", "Moved the window.");
        thread.EndReply();

        Assert.Equal("Modeller", thread.Items[1]["mark"]!.ToString());
        Assert.Null(thread.Items[2]["mark"]);
        Assert.Null(thread.Items[0]["mark"]);
        Assert.Null(thread.TurnMark);
        Assert.Equal("Modeller", thread.Shown);
        Assert.Null(thread.ToJson()["turn"]);
        Assert.Equal("Modeller", thread.ToJson()["shown"]!.ToString());
    }

    [Fact]
    public void ATurn_NamesItsRoleBeforeTheReply()
    {
        var thread = new DocThread { Serial = 1 };
        thread.Add("user", "Generate 3D");
        thread.BeginReply(ForskRoles.MarkForAction("file.generate"));
        var running = thread.ToJson();
        Assert.Equal("Modeller", running["turn"]!.ToString());
        Assert.Equal("Modeller", running["shown"]!.ToString());
        Assert.Null(running["busy"]);
        thread.Busy = "Generating";
        Assert.Equal("Modeller", thread.ToJson()["busy"]!["mark"]!.ToString());
        thread.EndReply();
        Assert.Null(thread.ToJson()["turn"]);
        Assert.Equal("Modeller", thread.ToJson()["shown"]!.ToString());
    }

    /// <summary>OfAction is the one map from a click to the role on the header and the message.</summary>
    [Theory]
    [InlineData("file.generate", ForskRole.Modeller)]
    [InlineData("file.rebuild", ForskRole.Modeller)]
    [InlineData("file.sample", ForskRole.Planner)]
    [InlineData("file.draw", ForskRole.Planner)]
    [InlineData("file.import", ForskRole.Planner)]
    [InlineData("rooms.list", ForskRole.Planner)]
    [InlineData("file.print", ForskRole.Plotter)]
    [InlineData("print.one", ForskRole.Plotter)]
    [InlineData("print.pages", ForskRole.Plotter)]
    [InlineData("export.dwg", ForskRole.Plotter)]
    [InlineData("export.ifc", ForskRole.Plotter)]
    [InlineData("export.csv", ForskRole.Plotter)]
    [InlineData("daylight.run", ForskRole.Analyser)]
    [InlineData("daylight.rooms", ForskRole.Analyser)]
    [InlineData("area.stats", ForskRole.Analyser)]
    [InlineData("bridge.start", ForskRole.None)]
    [InlineData("help.card", ForskRole.None)]
    public void AnAction_MapsToOneRole(string id, ForskRole role)
    {
        Assert.Equal(role, ForskRoles.OfAction(id));
        Assert.Equal(role == ForskRole.None ? null : ForskRoles.Label(role), ForskRoles.MarkForAction(id));
    }

    [Fact]
    public void TheControl_OffersAutoAndThePickableRoles_AndShowsTheOverride()
    {
        var auto = ForskRoles.Control(ForskRole.None);
        Assert.Equal("auto", auto["value"]!.ToString());
        Assert.Equal(new[] { "Planner", "Modeller", "Plotter", "Analyser", "Support", "Render", "Auto" }, ((JArray)auto["options"]!).Select(o => o["label"]!.ToString()));
        Assert.Equal("modeller", ForskRoles.Control(ForskRole.Modeller)["value"]!.ToString());

        var thread = new DocThread { Serial = 1, Override = ForskRole.Modeller };
        var view = WindowView.Build(thread, Docs.Facts("house"));
        Assert.Equal("modeller", view["role"]!["value"]!.ToString());
        Assert.Equal("Print PDF", view["bar"]!["slots"]![0]!["label"]!.ToString());
    }

    [Fact]
    public void EveryRegistryAction_HasARoleOrIsTheBridgeOrHelp()
    {
        var roleless = ForskRegistry.All.Where(a => ForskRoles.OfAction(a.Id) == ForskRole.None).Select(a => a.Id).OrderBy(i => i);
        Assert.Equal(new[] { "bridge.start", "help.card" }, roleless);
        foreach (var id in new[] { "daylight.rooms", "daylight.run", "daylight.again", "daylight.hide", "daylight.show", "daylight.room", "area.stats" })
            Assert.Equal(ForskRole.Analyser, ForskRoles.OfAction(id));
        // A window is geometry. Listing rooms stays with the plan. Area statistics are Analyser.
        Assert.Equal(ForskRole.Modeller, ForskRoles.OfAction("daylight.window"));
        Assert.Equal(ForskRole.Planner, ForskRoles.OfAction("rooms.list"));
    }
}
