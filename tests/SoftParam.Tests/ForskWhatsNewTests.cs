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

    /// <summary>The list shape: one release per entry, newest first.</summary>
    static string Releases(params string[] versions) =>
        new JObject
        {
            ["_edit"] = "note",
            ["releases"] = new JArray(versions.Select(v => JObject.Parse(Notes(2, v))))
        }.ToString();

    static readonly string Three = Releases("1.3.0", "1.2.0", "1.1.0");

    [Fact]
    public void Parse_TakesTwoToFourItems_EachWithATitleAndAHow()
    {
        Assert.Null(ForskWhatsNew.Parse(Notes(1)));
        Assert.Equal(2, ForskWhatsNew.Parse(Notes(2))!.Single().Items.Count);
        Assert.Equal(4, ForskWhatsNew.Parse(Notes(4))!.Single().Items.Count);
        Assert.Null(ForskWhatsNew.Parse(Notes(5)));
        Assert.Null(ForskWhatsNew.Parse(Notes(3, "next")));
        Assert.Null(ForskWhatsNew.Parse("{\"version\":\"1.1.0\",\"items\":[{\"title\":\"A\",\"how\":\"\"},{\"title\":\"B\",\"how\":\"b\"}]}"));
        Assert.Null(ForskWhatsNew.Parse("not json"));
        Assert.Null(ForskWhatsNew.Parse(null!));
    }

    [Fact]
    public void Parse_ReadsTheReleases_NewestFirst_EachOnce()
    {
        Assert.Equal(new[] { "1.3.0", "1.2.0", "1.1.0" }, ForskWhatsNew.Parse(Three)!.Select(r => r.Version));
        Assert.Null(ForskWhatsNew.Parse(Releases("1.2.0", "1.3.0")));
        Assert.Null(ForskWhatsNew.Parse(Releases("1.2.0", "1.2.0")));
        Assert.Null(ForskWhatsNew.Parse(Releases()));
        // One release that does not read spoils the file: the shipped-file test catches it before a release.
        var broken = JObject.Parse(Three);
        broken["releases"]![1]!["items"] = new JArray();
        Assert.Null(ForskWhatsNew.Parse(broken.ToString()));
    }

    [Fact]
    public void Parse_StillReadsTheSingleReleaseShape_AsAListOfOne()
    {
        var releases = ForskWhatsNew.Parse(Notes(3, "1.2.0"))!;
        Assert.Equal("1.2.0", releases.Single().Version);
        Assert.Equal("Thing 3", releases.Single().Items[2].Title);
    }

    [Theory]
    [InlineData("1.3.0", "", true, "1.3.0 1.2.0 1.1.0")] // updated from 1.0.0, which stored no version: every release
    [InlineData("1.3.0", "1.0.0", true, "1.3.0 1.2.0 1.1.0")]
    [InlineData("1.3.0", "1.1.0", true, "1.3.0 1.2.0")]
    [InlineData("1.3.0", "1.1.1", true, "1.3.0 1.2.0")]  // a patch with no notes of its own
    [InlineData("1.3.0", "1.2.0", true, "1.3.0")]        // one release: no arrows
    [InlineData("1.3.0", "", false, "")]                 // fresh install: never
    [InlineData("1.3.0", "1.3.0", true, "")]             // already seen
    [InlineData("1.2.0", "1.3.0", true, "")]             // downgrade
    [InlineData("1.3.1", "1.3.0", true, "")]             // a patch with nothing written down
    [InlineData("1.3.1", "1.2.0", true, "1.3.0")]
    [InlineData("1.2.0", "", true, "1.2.0 1.1.0")]       // notes ahead of the running build wait for it
    [InlineData("1.0.9", "1.0.0", true, "")]
    [InlineData("", "1.0.0", true, "")]
    public void Due_IsEveryReleaseSinceTheLastSeen_UpToTheRunningOne(string current, string seen, bool usedBefore, string due)
    {
        var shown = ForskWhatsNew.Due(ForskWhatsNew.Parse(Three), current, seen, usedBefore);
        Assert.Equal(due, string.Join(" ", shown.Select(r => r.Version)));
        Assert.Empty(ForskWhatsNew.Due(null!, current, seen, usedBefore));
    }

    [Fact]
    public void Card_IsTheItemsAsAList_WithGotIt()
    {
        var card = ForskWhatsNew.Card(ForskWhatsNew.Parse(Notes(3))!);
        Assert.Equal(ForskWhatsNew.Kind, card.Kind);
        Assert.Equal("What's new in Forsk 1.1.0", card.Question);
        Assert.Equal(new[] { "Thing 1", "Thing 2", "Thing 3" }, card.Slides!.Single().Items.Select(f => f.Title));
        Assert.Equal(new[] { "done" }, card.Pills.Select(p => p.Id));

        var thread = new DocThread { Serial = 1 };
        thread.BeginReply("Support");
        var item = thread.AddCard(card, null);
        thread.EndReply();
        Assert.Equal("Support", item["mark"]!.ToString());
        Assert.Equal("Click 2", item["slides"]![0]!["features"]![1]!["how"]!.ToString());
    }

    [Fact]
    public void Card_CarriesASlidePerRelease_OpeningOnTheNewest()
    {
        var card = ForskWhatsNew.Card(ForskWhatsNew.Due(ForskWhatsNew.Parse(Three), "1.3.0", "", true));
        Assert.Equal("What's new in Forsk 1.3.0", card.Question);
        var item = new DocThread { Serial = 1 }.AddCard(card, null);
        var slides = (JArray)item["slides"]!;
        Assert.Equal(new[] { "1.3.0", "1.2.0", "1.1.0" }, slides.Select(s => s["version"]!.ToString()));
        Assert.Equal("What's new in Forsk 1.2.0", slides[1]["title"]!.ToString());
        Assert.Equal("Click 1", slides[2]["features"]![0]!["how"]!.ToString());
        Assert.Null(item["features"]);
    }

    /// <summary>The shipped file reads, and its newest release is not behind the csproj: bump both together each release.</summary>
    [Fact]
    public void TheShippedFile_Reads_AndMatchesOrLeadsTheRelease()
    {
        var releases = ForskWhatsNew.Parse(ReadUnder(Path.Combine("plugin", "Forsk", "whats_new.json")));
        Assert.NotNull(releases);
        var csproj = ReadUnder(Path.Combine("plugin", "rhinomcp.csproj"));
        var version = Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.False(ForskUpdate.IsNewer(version, releases![0].Version), "whats_new.json is for " + releases[0].Version + " but the release is " + version);
        Assert.Contains("rhinomcp.Forsk.whats_new.json", csproj);
    }

    /// <summary>A Mac on 1.0.0 that updates straight to 1.3.0 steps through every release since.</summary>
    [Fact]
    public void TheShippedFile_GivesA100User_EveryReleaseSince()
    {
        var releases = ForskWhatsNew.Parse(ReadUnder(Path.Combine("plugin", "Forsk", "whats_new.json")))!;
        Assert.Equal(new[] { "1.3.0", "1.2.0", "1.1.0" }, ForskWhatsNew.Due(releases, "1.3.0", "", true).Select(r => r.Version));
        Assert.Equal(new[] { "1.3.0" }, ForskWhatsNew.Due(releases, "1.3.0", "1.2.0", true).Select(r => r.Version));
        // A step names what is there now: 1.2.0's Jump inside is Interior render.
        Assert.DoesNotContain(releases.SelectMany(r => r.Items), i => i.Title.Contains("Jump inside") || i.How.Contains("Jump inside"));
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

    [Fact]
    public void AnItemsIcon_IsALucideIconTheWindowHas_ElseSparkles()
    {
        var notes = ForskWhatsNew.Parse(@"{""version"":""1.1.0"",""items"":[
            {""title"":""A"",""how"":""a"",""icon"":""armchair""},
            {""title"":""B"",""how"":""b"",""icon"":""unicorn""},
            {""title"":""C"",""how"":""c""}]}")!.Single();
        Assert.Equal(new[] { "armchair", "sparkles", "sparkles" }, notes.Items.Select(i => i.Icon));
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.html"));
        foreach (var icon in ForskWhatsNew.Icons.Concat(ForskWhatsNew.SlideIcons))
            Assert.Contains("<div id=\"icon-" + icon + "\"><svg", html);
    }

    [Fact]
    public void TheShippedNotes_GiveEveryItemAnIcon()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !Directory.Exists(Path.Combine(root, "plugin", "Forsk"))) root = Path.GetDirectoryName(root);
        var releases = ForskWhatsNew.Parse(File.ReadAllText(Path.Combine(root!, "plugin", "Forsk", "whats_new.json")))!;
        Assert.All(releases.SelectMany(r => r.Items), i => Assert.NotEqual(ForskWhatsNew.DefaultIcon, i.Icon));
    }

    /// <summary>The slider steps on the page alone: arrows clamp at the ends, Left and Right step from a focused button.</summary>
    [Fact]
    public void ThePageSlider_StepsWithArrowsAndKeys_AndHoldsAtTheEnds()
    {
        var engine = PageScript.Load();
        Assert.Equal("1,2,2,0,0", engine.Evaluate("[Forsk.slideStep(0,3,1), Forsk.slideStep(1,3,1), Forsk.slideStep(2,3,1), Forsk.slideStep(1,3,-1), Forsk.slideStep(0,3,-1)].join()").ToString());
        Assert.Equal("-1,1,0,0", engine.Evaluate("[Forsk.slideKey({key:'ArrowLeft'}), Forsk.slideKey({key:'ArrowRight'}), Forsk.slideKey({key:'ArrowRight', metaKey:true}), Forsk.slideKey({key:'Enter'})].join()").ToString());
        Assert.Equal("1.3.0|1.2.0", engine.Evaluate("Forsk.newsSlides({slides:[{version:'1.3.0',features:[{}]},{version:'1.2.0',features:[{}]},{version:'x',features:[]}]}).map(function (s) { return s.version; }).join('|')").ToString());
        Assert.Equal("0", engine.Evaluate("String(Forsk.newsSlides({features:[{}]}).length)").ToString());
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("arrow('chevron-left', 'Newer release', -1)", js);
        Assert.Contains("arrow('chevron-right', 'Older release', 1)", js);
        Assert.Contains("if (slides.length > 1) head.appendChild(slider(box, title, slides));", js);
        // A step is page-only: no send, and the card is not marked touched.
        var slider = js.Substring(js.IndexOf("function slider("), js.IndexOf("return nav;") - js.IndexOf("function slider("));
        Assert.DoesNotContain("send", slider);
        Assert.DoesNotContain("data-touched", slider);
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.html"));
        Assert.Contains(".card.news .features.fade { animation: none; }", html);
    }
}
