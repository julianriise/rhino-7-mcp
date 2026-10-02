using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// A rephrase or a hand role switch appends one misroute line, and that file
/// becomes Suite B candidates. The recorded 30 still score 30.
/// </summary>
public class MisrouteTests
{
    static readonly DateTimeOffset At = new DateTimeOffset(2026, 10, 2, 19, 55, 56, TimeSpan.Zero);

    [Fact]
    public void ARephrase_RecordsThePreviousSentence_WhenTheRoleChanges()
    {
        var entry = ForskMisroutes.Rephrase("run daylight", Picked.None, "print PDF", At);
        Assert.Equal("run daylight", entry["sentence"]!.ToString());
        Assert.Equal("Analyser", entry["routed_role"]!.ToString());
        Assert.Equal("Plotter", entry["corrected_role"]!.ToString());
        Assert.Equal("2026-10-02T19:55:56Z", entry["time"]!.ToString());

        Assert.Null(ForskMisroutes.Rephrase("print PDF", Picked.None, "skriv ut tegningene", At));
        Assert.Null(ForskMisroutes.Rephrase("", Picked.None, "print PDF", At));
    }

    [Fact]
    public void AHandSwitch_RecordsTheTurnJustSent_AndIgnoresTheSameRole()
    {
        var entry = ForskMisroutes.Hand("hello", Picked.None, ForskRole.Modeller, At);
        Assert.Equal("hello", entry["sentence"]!.ToString());
        Assert.Equal("General", entry["routed_role"]!.ToString());
        Assert.Equal("Modeller", entry["corrected_role"]!.ToString());
        Assert.Equal("2026-10-02T19:55:56Z", entry["time"]!.ToString());

        Assert.Null(ForskMisroutes.Hand("print PDF", Picked.None, ForskRole.Plotter, At));
        var cleared = ForskMisroutes.Hand("print PDF", Picked.None, ForskRole.None, At);
        Assert.Equal("Plotter", cleared["routed_role"]!.ToString());
        Assert.Equal("General", cleared["corrected_role"]!.ToString());
    }

    [Fact]
    public void TheAnsweredSentence_IsTheOneWithAReplyAfterIt()
    {
        var thread = new DocThread();
        Assert.Null(ForskMisroutes.AnsweredSentence(thread.Items));
        thread.Add("user", "run daylight");
        Assert.Null(ForskMisroutes.AnsweredSentence(thread.Items));
        thread.Add("assistant", "Daylight is on.");
        Assert.Equal("run daylight", ForskMisroutes.AnsweredSentence(thread.Items));
        thread.Add("user", "print PDF");
        Assert.Null(ForskMisroutes.AnsweredSentence(thread.Items));
        thread.Add("receipt", "Printed.");
        Assert.Equal("print PDF", ForskMisroutes.AnsweredSentence(thread.Items));
    }

    [Fact]
    public void TheLog_BecomesSuiteBFixtures_AndTheRecordedThirtyStillScore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "forsk-misroutes-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, "misroutes.jsonl");
        try
        {
            ForskMisroutes.Append(ForskMisroutes.Rephrase("run daylight", Picked.None, "what makes a living room feel bright?", At), path);
            ForskMisroutes.Append(ForskMisroutes.Hand("hello", Picked.None, ForskRole.Modeller, At), path);
            File.AppendAllText(path, "\nnot json\n");

            var fixtures = ForskMisroutes.ToFixtures(path);
            Assert.Equal(2, fixtures.Count);
            Assert.Equal("B31", fixtures[0]["id"]!.ToString());
            Assert.Equal("run daylight", fixtures[0]["utterance"]!.ToString());
            Assert.Equal("empty", fixtures[0]["selection"]!.ToString());
            Assert.Equal("general", fixtures[0]["router"]!.ToString());
            Assert.Equal("Analyser", fixtures[0]["routed_role"]!.ToString());
            Assert.Equal("General", fixtures[0]["corrected_role"]!.ToString());
            Assert.Equal("B32", fixtures[1]["id"]!.ToString());
            Assert.Equal("Modeller", fixtures[1]["expect_role"]!.ToString());
            Assert.Null(fixtures[1]["router"]);

            var recorded = ((JArray)JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "recorded_prompts.json")))["cases"]!).Cast<JObject>();
            var score = ForskMisroutes.Score(recorded, out var counted);
            Assert.Equal(30, counted);
            Assert.Equal(30, score);

            var extra = new JObject { ["utterance"] = "run daylight", ["selection"] = "empty", ["router"] = "daylight" };
            var better = ForskMisroutes.Score(recorded.Concat(new[] { extra }), out var more);
            Assert.Equal(31, more);
            Assert.Equal(31, better);

            Assert.EndsWith(Path.Combine("Library", "Application Support", "Forsk", "misroutes.jsonl"), ForskMisroutes.DefaultPath);
            var window = File.ReadAllText(WindowSource());
            Assert.Contains("ForskMisroutes.Rephrase", window);
            Assert.Contains("ForskMisroutes.Hand", window);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    static string WindowSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Forsk", "ForskWindowActions.cs");
            if (File.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("ForskWindowActions.cs above " + AppContext.BaseDirectory);
    }
}
