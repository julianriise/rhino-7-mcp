using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Settings → Update available. forsk.app's answer is parsed and compared
/// here; nothing calls forsk.app. A newer version dots the gear and adds the
/// row; the same version, an older one, or any odd answer shows nothing.
/// </summary>
public class ForskUpdateTests
{
    const string Answer = "{\"ok\":true,\"version\":\"1.0.1\",\"released\":\"2026-10-20\",\"updateUrl\":\"https://docs.forsk.app/install#update\"}";

    [Fact]
    public void Endpoint_IsForskAppsLatestPlugin()
    {
        Assert.Equal("https://forsk.app/api/plugin/latest", ForskUpdate.Endpoint);
        Assert.Equal("https://docs.forsk.app/install#update", ForskUpdate.HowTo);
    }

    [Fact]
    public void Version_ReadsForskAppsAnswer_AndNothingElse()
    {
        Assert.Equal("1.0.1", ForskUpdate.Version(Answer));
        Assert.Null(ForskUpdate.Version("{\"ok\":false,\"version\":\"1.0.1\"}"));
        Assert.Null(ForskUpdate.Version("{\"version\":\"1.0.1\"}"));
        Assert.Null(ForskUpdate.Version("{\"ok\":true,\"version\":\"soon\"}"));
        Assert.Null(ForskUpdate.Version("{\"ok\":true,\"version\":101}"));
        Assert.Null(ForskUpdate.Version("<html>Bad gateway</html>"));
        Assert.Null(ForskUpdate.Version(""));
        Assert.Null(ForskUpdate.Version(null));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("2.0.0", "1.0.0+abc123", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.0.0", "1.0.0", true)] // four parts reads higher; the site keeps three.
    [InlineData("1.0.1-beta", "1.0.0", false)]
    [InlineData("1.0.1", "", false)]
    [InlineData(null, "1.0.0", false)]
    public void IsNewer_ComparesVersions_AndJunkIsNeverNewer(string? latest, string current, bool newer)
    {
        Assert.Equal(newer, ForskUpdate.IsNewer(latest!, current));
    }

    [Fact]
    public void Newer_IsTheVersionToShow_OrNull()
    {
        Assert.Equal("1.0.1", ForskUpdate.Newer(Answer, "1.0.0"));
        Assert.Null(ForskUpdate.Newer(Answer, "1.0.1"));
        Assert.Null(ForskUpdate.Newer(null, "1.0.0"));
    }

    [Fact]
    public void Settings_ShowUpdateFirst_WithADot_OnlyWhileThereIsOne()
    {
        JObject View(string? update)
        {
            var facts = Docs.Facts("house");
            facts.UpdateVersion = update;
            return WindowView.Build(new DocThread { Serial = 1, File = "holmen.3dm" }, facts);
        }

        var none = View(null);
        Assert.DoesNotContain(ForskUpdate.MenuId, ((JArray)none["settings"]!).Select(s => s["id"]!.ToString()));
        Assert.DoesNotContain(ForskUpdate.MenuId, ((JArray)none["attention"]!).Select(a => a["id"]!.ToString()));

        var some = View("1.0.1");
        var first = ((JArray)some["settings"]!)[0]!;
        Assert.Equal(ForskUpdate.MenuId, first["id"]!.ToString());
        Assert.Equal("Update available: Forsk 1.0.1", first["label"]!.ToString());
        var row = ((JArray)some["attention"]!).Single(a => a["id"]!.ToString() == ForskUpdate.MenuId);
        Assert.True(row["needs"]!.Value<bool>());
        Assert.Equal("Update available", row["label"]!.ToString());
    }

    [Fact]
    public void Card_NamesBothVersions_ThePackageManagerSteps_AndFood4Rhino()
    {
        var card = ForskCards.Update("1.0.1", "1.0.0");
        Assert.Equal(ForskUpdate.MenuId, card.Kind);
        Assert.Equal("Forsk 1.0.1 is available. You have 1.0.0.", card.Question);
        Assert.Equal(new[]
        {
            "Package Manager: Installed → forsk → pick 1.0.1 under Version → Install, then Apply.",
            "Then quit and reopen Rhino 7."
        }, card.Rows);
        Assert.Contains("Food4Rhino", card.Note);
        Assert.Equal(new[] { "open", "howto", "done" }, card.Pills.Select(p => p.Id));
        Assert.Equal(new[] { "Update now", "How to update", "Done" }, card.Pills.Select(p => p.Label));
    }
}
