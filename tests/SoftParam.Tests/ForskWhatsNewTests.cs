using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// What's new: once, the first time Forsk opens on a new version, never on a
/// fresh install. The release edits plugin/Forsk/whats_new.json only; the
/// tests below hold it to 2 to 4 items and to the release number.
/// </summary>
public class ForskWhatsNewTests
{
    static string Notes(int items, string version = "1.1.0") =>
        new JObject
        {
            ["version"] = version,
            ["items"] = new JArray(Enumerable.Range(1, items).Select(i => new JObject { ["title"] = "Thing " + i, ["how"] = "Click " + i }))
        }.ToString();

    [Fact]
    public void Parse_TakesTwoToFourItems_EachWithATitleAndAHow()
    {
        Assert.Null(ForskWhatsNew.Parse(Notes(1)));
        Assert.Equal(2, ForskWhatsNew.Parse(Notes(2))!.Items.Count);
        Assert.Equal(4, ForskWhatsNew.Parse(Notes(4))!.Items.Count);
        Assert.Null(ForskWhatsNew.Parse(Notes(5)));
        Assert.Null(ForskWhatsNew.Parse(Notes(3, "next")));
        Assert.Null(ForskWhatsNew.Parse("{\"version\":\"1.1.0\",\"items\":[{\"title\":\"A\",\"how\":\"\"},{\"title\":\"B\",\"how\":\"b\"}]}"));
        Assert.Null(ForskWhatsNew.Parse("not json"));
        Assert.Null(ForskWhatsNew.Parse(null!));
    }

    [Theory]
    [InlineData("1.1.0", "1.0.0", true, true)]   // updated from 1.1's predecessor
    [InlineData("1.1.0", "", true, true)]        // updated from 1.0.0, which stored no version
    [InlineData("1.1.0", "", false, false)]      // fresh install: never
    [InlineData("1.1.0", "1.1.0", true, false)]  // already seen
    [InlineData("1.2.0", "1.1.0", true, false)]  // downgrade
    [InlineData("1.0.9", "1.0.0", true, false)]  // the notes are for another release
    public void Show_OnlyOnceAfterAnUpdate(string current, string seen, bool usedBefore, bool show)
    {
        Assert.Equal(show, ForskWhatsNew.Show(ForskWhatsNew.Parse(Notes(2)), current, seen, usedBefore));
    }

    [Fact]
    public void Card_IsTheItemsAsAList_WithGotIt()
    {
        var card = ForskWhatsNew.Card(ForskWhatsNew.Parse(Notes(3))!);
        Assert.Equal(ForskWhatsNew.Kind, card.Kind);
        Assert.Equal("What's new in Forsk 1.1.0", card.Question);
        Assert.Equal(new[] { "Thing 1", "Thing 2", "Thing 3" }, card.Features!.Select(f => f.Title));
        Assert.Equal(new[] { "done" }, card.Pills.Select(p => p.Id));

        var thread = new DocThread { Serial = 1 };
        thread.BeginReply("Support");
        var item = thread.AddCard(card, null);
        thread.EndReply();
        Assert.Equal("Support", item["mark"]!.ToString());
        Assert.Equal("Click 2", item["features"]![1]!["how"]!.ToString());
    }

    /// <summary>The shipped file reads, and is not behind the csproj: bump both together each release.</summary>
    [Fact]
    public void TheShippedFile_Reads_AndMatchesOrLeadsTheRelease()
    {
        var notes = ForskWhatsNew.Parse(ReadUnder(Path.Combine("plugin", "Forsk", "whats_new.json")));
        Assert.NotNull(notes);
        var csproj = ReadUnder(Path.Combine("plugin", "rhinomcp.csproj"));
        var version = Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.False(ForskUpdate.IsNewer(version, notes!.Version), "whats_new.json is for " + notes.Version + " but the release is " + version);
        Assert.Contains("rhinomcp.Forsk.whats_new.json", csproj);
    }

    [Fact]
    public void UpdateConfirm_SaysRhinoMustQuit_AndNamesAnUnsavedFile()
    {
        var plain = ForskUpdate.ConfirmText(null);
        Assert.Contains("quit Rhino 7 and open it again", plain);
        Assert.Contains("Save your work", plain);
        Assert.DoesNotContain("unsaved", plain);
        Assert.Contains("holmen.3dm has unsaved changes", ForskUpdate.ConfirmText("holmen.3dm"));
    }

    static string ReadUnder(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }
}
