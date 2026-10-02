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
    [InlineData("import plan.pdf", ForskIntent.Import, "Planner")]
    [InlineData("import plan.dxf", ForskIntent.Dxf, "Planner")]
    [InlineData("generate the model with walls 2700", ForskIntent.Build, "Modeller")]
    [InlineData("move the window 200 along the wall", ForskIntent.Edit, "Modeller")]
    [InlineData("skriv ut", ForskIntent.Print, "Plotter")]
    [InlineData("add a door schedule", ForskIntent.Sheets, "Plotter")]
    public void EachIntent_MapsToItsRole(string sentence, ForskIntent intent, string role)
    {
        Assert.Equal(intent, ForskIntentRouter.Classify(sentence, ""));
        Assert.Equal(role, ForskRoles.Mark(intent, ForskRole.None));
    }

    [Fact]
    public void Daylight_HasNoRoleOfItsOwn_TheRoutersLabelNamesIt()
    {
        Assert.Equal(ForskRole.None, ForskRoles.Of(ForskIntent.Daylight));
        Assert.Equal("Daylight", ForskRoles.Mark(ForskIntentRouter.Classify("dagslys", ""), ForskRole.None));
    }

    [Fact]
    public void AGeneralTurn_HasNoMark_AndNoIntentIsRenderYet()
    {
        Assert.Null(ForskRoles.Mark(ForskIntentRouter.Classify("hello", ""), ForskRole.None));
        Assert.DoesNotContain(Enum.GetValues(typeof(ForskIntent)).Cast<ForskIntent>(), i => ForskRoles.Of(i) == ForskRole.Render);
        Assert.DoesNotContain(ForskRole.Render, ForskRoles.Pickable);
    }

    [Fact]
    public void AnOverride_NamesTheAnswer_AndClearingItHandsTheNextAnswerBackToTheRouter()
    {
        var import = ForskIntentRouter.Classify("import plan.pdf", "");
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
    }

    [Fact]
    public void TheDrawnBar_IsTheBarTheShortcutsFire()
    {
        // Cmd+3 and a typed label read the bar with the role boost, as the page draws it.
        var thread = new DocThread { Serial = 1, Override = ForskRole.Plotter };
        var facts = Docs.Facts("forsk undo newest");
        var drawn = ((JArray)WindowView.Build(thread, facts, "")["bar"]!["slots"]!).Select(s => s["id"]!.ToString());
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
        thread.BeginReply(ForskRoles.Mark(ForskIntentRouter.Classify("project is Tilbygg Holmen", ""), thread.Override));
        history.Add(new JObject { ["role"] = "user", ["content"] = "project is Tilbygg Holmen" });
        history.Add(new JObject { ["role"] = "assistant", ["content"] = "Stored the project name." });
        thread.Add("assistant", "Stored the project name.");
        thread.EndReply();

        thread.Override = ForskRole.None;
        thread.Add("user", "move the north wall 500 mm north");
        thread.BeginReply(ForskRoles.Mark(ForskIntentRouter.Classify("move the north wall 500 mm north", ""), thread.Override));
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
    }

    [Fact]
    public void TheControl_OffersAutoAndThePickableRoles_AndShowsTheOverride()
    {
        var auto = ForskRoles.Control(ForskRole.None);
        Assert.Equal("auto", auto["value"]!.ToString());
        Assert.Equal(new[] { "Auto", "Planner", "Modeller", "Plotter" }, ((JArray)auto["options"]!).Select(o => o["label"]!.ToString()));
        Assert.Equal("modeller", ForskRoles.Control(ForskRole.Modeller)["value"]!.ToString());

        var thread = new DocThread { Serial = 1, Override = ForskRole.Modeller };
        var view = WindowView.Build(thread, Docs.Facts("house"), "");
        Assert.Equal("modeller", view["role"]!["value"]!.ToString());
        Assert.Equal("Print PDF", view["bar"]!["slots"]![0]!["label"]!.ToString());
    }

    [Fact]
    public void EveryRegistryAction_HasARoleOrIsDaylightOrTheBridge()
    {
        var roleless = ForskRegistry.All.Where(a => ForskRoles.OfAction(a.Id) == ForskRole.None).Select(a => a.Id).OrderBy(i => i);
        Assert.Equal(new[] { "bridge.start", "daylight.again", "daylight.hide", "daylight.room", "daylight.run", "daylight.show", "help.card" }, roleless);
    }
}
