using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Support's report card and the jsonl delivery. The Rhino walk that fills
/// the debug report is not in this project. No network.
/// </summary>
public class ReportTests
{
    static string Field(CardSpec card, string key) => card.Fields.Single(f => f.Key == key).Value;

    [Theory]
    [InlineData("how do I print", ForskIntent.Support)]
    [InlineData("what does the scale do", ForskIntent.Support)]
    [InlineData("hvordan lager jeg rom", ForskIntent.Support)]
    [InlineData("this is broken", ForskIntent.Support)]
    [InlineData("the print is broken", ForskIntent.Support)]
    [InlineData("feil", ForskIntent.Support)]
    [InlineData("there is an issue with areas, when i click an area in rhino, it selects multiple area", ForskIntent.Support)]
    [InlineData("there is an issue", ForskIntent.Support)]
    [InlineData("issue with the areas", ForskIntent.Support)]
    [InlineData("problem with the print", ForskIntent.Support)]
    [InlineData("it doesn't work", ForskIntent.Support)]
    [InlineData("daylight is not working", ForskIntent.Support)]
    [InlineData("this is a bug", ForskIntent.Support)]
    [InlineData("it selects multiple rooms", ForskIntent.Support)]
    [InlineData("the area is wrong", ForskIntent.Support)]
    [InlineData("problem med arealene", ForskIntent.Support)]
    [InlineData("det funker ikke", ForskIntent.Support)]
    [InlineData("vinduet virker ikke", ForskIntent.Support)]
    [InlineData("it would be nice if walls could curve", ForskIntent.Support)]
    [InlineData("can you add a door", ForskIntent.Support)]
    [InlineData("add a door", ForskIntent.Edit)]
    [InlineData("add a door schedule", ForskIntent.Sheets)]
    [InlineData("skriv ut", ForskIntent.Print)]
    [InlineData("run daylight", ForskIntent.Daylight)]
    public void Routing_SendsQuestionsBugsAndRequestsToSupport(string text, ForskIntent intent)
    {
        Assert.Equal(intent, ForskIntentRouter.Classify(text));
    }

    [Fact]
    public void ClickingAnAreaThatSelectsTwo_IsABugForSupport()
    {
        const string said = "there is an issue with areas, when i click an area in rhino, it selects multiple area";
        Assert.Equal(ForskIntent.Support, ForskIntentRouter.Classify(said));
        Assert.True(ForskIntentRouter.IsBug(said));
        Assert.False(ForskIntentRouter.IsQuestion(said));
        Assert.Equal(ForskReports.ReportKind.Bug, ForskReports.Of(said));
    }

    [Fact]
    public void ABugOrAFeature_IsAnEditableReport_AQuestionIsNot()
    {
        Assert.Null(ForskReports.Card("how do I print", "office.3dm"));
        Assert.Null(ForskReports.Card("what does daylight do", "office.3dm"));
        Assert.Equal(ForskReports.ReportKind.None, ForskReports.Of("hvordan skriver jeg ut"));

        var bug = ForskReports.Card("this is broken", "office.3dm");
        Assert.Equal("support.report", bug.Kind);
        Assert.Equal("Send this report?", bug.Question);
        Assert.Equal(new[] { "type", "description", "attach" }, bug.Fields.Select(f => f.Key));
        Assert.DoesNotContain(bug.Fields, f => f.Key == "file" || f.Key == "happened" || f.Key == "expected" || f.Key == "steps");
        var type = bug.Fields.Single(f => f.Key == "type");
        Assert.Equal("Bug", type.Value);
        Assert.Equal(new[] { "Bug", "Question", "Feature request" }, type.Options);
        Assert.Equal("this is broken", Field(bug, "description"));
        Assert.True(bug.Fields.Single(f => f.Key == "description").Long);
        Assert.Equal("office.3dm", bug.Data["file"]!.ToString());
        var attach = bug.Fields.Single(f => f.Key == "attach");
        Assert.Equal("Attach debug report", attach.Label);
        Assert.True(attach.Check);
        Assert.Equal("1", attach.Value);
        Assert.True(ForskReports.WantsDebug("1"));
        Assert.True(ForskReports.WantsDebug(null));
        Assert.False(ForskReports.WantsDebug("0"));
        Assert.Equal(new[] { "send", "cancel" }, bug.Pills.Select(p => p.Id));

        var feature = ForskReports.Card("can you add a stair tool", "a.3dm");
        Assert.Equal("Feature request", Field(feature, "type"));
        Assert.Equal("can you add a stair tool", Field(feature, "description"));
        Assert.Equal("a.3dm", feature.Data["file"]!.ToString());

        var thread = new DocThread { Serial = 1 };
        thread.BeginReply(ForskRoles.Mark(ForskIntent.Support, ForskRole.None));
        var item = thread.AddCard(bug, null);
        thread.EndReply();
        Assert.Equal("Support", item["mark"]!.ToString());
        var posted = item["fields"]!.Last;
        Assert.True(posted["check"]!.Value<bool>());
        Assert.Equal("1", posted["value"]!.ToString());
        Assert.Equal(new[] { "Bug", "Question", "Feature request" }, item["fields"]![0]!["options"]!.Select(o => o.ToString()));
        Assert.True(item["fields"]![1]!["long"]!.Value<bool>());
        Assert.Equal("office.3dm", item["data"]!["file"]!.ToString());
    }

    [Fact]
    public void Support_EndsABugOnTheReportCard_PrefilledWithWhatItFound()
    {
        const string said = "there is an issue with areas, when i click an area in rhino, it selects multiple area";
        Assert.True(ForskReports.EndsWithReport(ForskRole.Support, said));
        Assert.True(ForskReports.EndsWithReport(ForskRole.None, said));
        Assert.False(ForskReports.EndsWithReport(ForskRole.Modeller, said));
        Assert.False(ForskReports.EndsWithReport(ForskRole.Support, "how do I print"));
        Assert.False(ForskReports.EndsWithReport(ForskRole.Support, "hello"));
        Assert.True(ForskReports.EndsWithReport(ForskRole.Support, "can you add a stair tool"));

        var thread = new DocThread { Serial = 1, Override = ForskRole.Support };
        thread.Add("user", said);
        thread.BeginReply(ForskRoles.Mark(ForskIntent.Support, thread.Override));
        var answer = thread.Add("assistant", "A-ROOM has two outlines on each room.");
        thread.AddReceipt(true, "Document", "16 rooms");
        thread.Add("assistant", "done");
        thread.AddReceipt(true, "Debug report", "Checked `office_2D.3dm` and the debug report.");

        var card = ForskReports.Card(said, "office.3dm");
        Assert.Equal(said, Field(card, "description"));
        Assert.DoesNotContain("done", Field(card, "description"));
        Assert.DoesNotContain("Debug report", Field(card, "description"));
        Assert.DoesNotContain("A-ROOM", Field(card, "description"));
        var glued = ForskReports.Card("id like to report a bug", "office_2D.3dm");
        Assert.Equal("id like to report a bug", Field(glued, "description"));
        var attach = card.Fields.Single(f => f.Key == "attach");
        Assert.True(attach.Check);
        Assert.Equal("1", attach.Value);

        var item = thread.AddCard(card, null);
        thread.EndReply();
        Assert.Equal("Support", answer["mark"]!.ToString());
        Assert.Equal("support.report", item["kind"]!.ToString());
        Assert.Equal(thread.Items[thread.Items.Count - 1], item);
        Assert.Equal("1", item["fields"]!.Last["value"]!.ToString());
        Assert.True(item["fields"]!.Last["check"]!.Value<bool>());

        var feature = ForskReports.Card("can you add a stair tool", "a.3dm");
        Assert.Equal("can you add a stair tool", Field(feature, "description"));
        Assert.DoesNotContain("No stair tool", Field(feature, "description"));
        Assert.True(feature.Fields.Single(f => f.Key == "attach").Check);

        var empty = ForskReports.Card("this is broken", "a.3dm");
        Assert.Equal("this is broken", Field(empty, "description"));

        var window = ForskSource("ForskWindowActions.cs");
        Assert.Contains("ForskReports.EndsWithReport", window);
        Assert.DoesNotContain("ForskReports.Findings", window);
        Assert.Contains("The window adds the report.", ForskSource("ForskChat.cs"));
    }

    [Fact]
    public void Support_ReadsTheModelWhenNothingIsSelected_ThenEndsOnTheCard()
    {
        const string said = "there is an issue with areas, when i click an area in rhino, it selects multiple area";
        Assert.True(ForskReports.EndsWithReport(ForskRole.Support, said));
        Assert.True(ForskReports.EndsWithReport(ForskRole.None, said));
        Assert.Contains("ForskReports.EndsWithReport", ForskSource("ForskWindowActions.cs"));

        var chat = ForskSource("ForskChat.cs");
        var start = chat.IndexOf("Turn bias: Support.", StringComparison.Ordinal);
        var end = chat.IndexOf("Turn bias: Build.", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var bias = chat.Substring(start, end - start);
        Assert.Contains("Nothing selected is not a refusal.", bias);
        Assert.Contains("get_document_summary", bias);
        Assert.Contains("get_objects", bias);
        Assert.Contains("the layer the report is about", bias);
        Assert.Contains("Do not say that nothing is selected.", bias);
        Assert.Contains("The window adds the report.", bias);
        Assert.DoesNotContain("Empty selection uses the refuse copy.", bias);

        var header = chat.Substring(chat.IndexOf("static string PanelHeader", StringComparison.Ordinal));
        Assert.Contains("Empty selection uses the refuse copy.", header);
        Assert.Contains("Nothing selected is not a refusal.", header);
        Assert.Contains("intent == ForskIntent.Support", header);

        var support = ForskToolPacks.For(ForskIntent.Support);
        Assert.Contains("get_document_summary", support);
        Assert.Contains("get_objects", support);
        Assert.Contains("get_object_info", support);
        Assert.DoesNotContain("select_objects", support);
        Assert.True(ForskToolPacks.Allows(ForskIntent.Support, "get_objects"));
        Assert.False(ForskToolPacks.Allows(ForskIntent.Support, "select_objects"));
    }

    static string ForskSource(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Forsk", file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new DirectoryNotFoundException("plugin/Forsk/" + file + " above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Send_AppendsOneJsonLine_AndLeavesTheDebugReportOffWhenUnticked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "forsk-reports-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, "reports.jsonl");
        try
        {
            var at = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
            var bug = ForskReports.Record(new JObject
            {
                ["type"] = "Bug",
                ["description"] = "this is broken",
                ["file"] = "office.3dm"
            }, "plugin line", at);
            var feature = ForskReports.Record(new JObject
            {
                ["type"] = "Feature request",
                ["description"] = "curved walls",
                ["file"] = "office.3dm"
            }, null, at);

            IReportDelivery delivery = new JsonlReportDelivery(path);
            delivery.Deliver(bug);
            delivery.Deliver(feature);

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"at\":\"2026-10-02T12:00:00Z\"", lines[0]);
            var first = JObject.Parse(lines[0]);
            Assert.Equal("Bug", first["type"]!.ToString());
            Assert.Equal("this is broken", first["description"]!.ToString());
            Assert.Null(first["happened"]);
            Assert.Equal("office.3dm", first["file"]!.ToString());
            Assert.Equal("plugin line", first["debug"]!.ToString());
            var second = JObject.Parse(lines[1]);
            Assert.Equal("Feature request", second["type"]!.ToString());
            Assert.Null(second["debug"]);

            var old = ForskReports.Read(new JObject
            {
                ["type"] = "Bug",
                ["happened"] = "id like to report a bug",
                ["expected"] = "Something i expected",
                ["steps"] = "I did a, b, c",
                ["file"] = "office_2D.3dm"
            });
            Assert.Equal("id like to report a bug\n\nSomething i expected\n\nI did a, b, c", old["description"]!.ToString());
            Assert.Equal("office_2D.3dm", old["file"]!.ToString());
            Assert.Equal("curved walls", ForskReports.Read(second)["description"]!.ToString());
            Assert.EndsWith(Path.Combine("Library", "Application Support", "Forsk", "reports.jsonl"), ForskReports.DefaultPath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
