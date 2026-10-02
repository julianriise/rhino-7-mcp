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
    public void ABugOrAFeature_IsAnEditableReport_AQuestionIsNot()
    {
        Assert.Null(ForskReports.Card("how do I print", "office.3dm"));
        Assert.Null(ForskReports.Card("what does daylight do", "office.3dm"));
        Assert.Equal(ForskReports.ReportKind.None, ForskReports.Of("hvordan skriver jeg ut"));

        var bug = ForskReports.Card("this is broken", "office.3dm");
        Assert.Equal("support.report", bug.Kind);
        Assert.Equal("Send this report?", bug.Question);
        Assert.Equal(new[] { "type", "happened", "expected", "steps", "file", "attach" }, bug.Fields.Select(f => f.Key));
        Assert.Equal("Bug", Field(bug, "type"));
        Assert.Equal("this is broken", Field(bug, "happened"));
        Assert.Equal("", Field(bug, "expected"));
        Assert.Equal("", Field(bug, "steps"));
        Assert.Equal("office.3dm", Field(bug, "file"));
        var attach = bug.Fields.Single(f => f.Key == "attach");
        Assert.Equal("Attach debug report", attach.Label);
        Assert.True(attach.Check);
        Assert.Equal("1", attach.Value);
        Assert.True(ForskReports.WantsDebug("1"));
        Assert.True(ForskReports.WantsDebug(null));
        Assert.False(ForskReports.WantsDebug("0"));
        Assert.Equal(new[] { "send", "cancel" }, bug.Pills.Select(p => p.Id));

        var feature = ForskReports.Card("can you add a stair tool", "a.3dm");
        Assert.Equal("Feature", Field(feature, "type"));
        Assert.Equal("", Field(feature, "happened"));
        Assert.Equal("can you add a stair tool", Field(feature, "expected"));
        Assert.Equal("a.3dm", Field(feature, "file"));

        var thread = new DocThread { Serial = 1 };
        thread.BeginReply(ForskRoles.Mark(ForskIntent.Support, ForskRole.None));
        var item = thread.AddCard(bug, null);
        thread.EndReply();
        Assert.Equal("Support", item["mark"]!.ToString());
        var posted = item["fields"]!.Last;
        Assert.True(posted["check"]!.Value<bool>());
        Assert.Equal("1", posted["value"]!.ToString());
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
                ["happened"] = "this is broken",
                ["expected"] = "a pdf",
                ["steps"] = "print",
                ["file"] = "office.3dm"
            }, "plugin line", at);
            var feature = ForskReports.Record(new JObject
            {
                ["type"] = "Feature",
                ["happened"] = "",
                ["expected"] = "curved walls",
                ["steps"] = "",
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
            Assert.Equal("this is broken", first["happened"]!.ToString());
            Assert.Equal("a pdf", first["expected"]!.ToString());
            Assert.Equal("print", first["steps"]!.ToString());
            Assert.Equal("office.3dm", first["file"]!.ToString());
            Assert.Equal("plugin line", first["debug"]!.ToString());
            var second = JObject.Parse(lines[1]);
            Assert.Equal("Feature", second["type"]!.ToString());
            Assert.Null(second["debug"]);
            Assert.EndsWith(Path.Combine("Library", "Application Support", "Forsk", "reports.jsonl"), ForskReports.DefaultPath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
