using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Hovering an element in the viewport lights the receipt rows in the chat that
/// name its id. The mapping is a dictionary read once per render; a move costs
/// one lookup and sends nothing until the hovered element changes.
/// </summary>
public class ForskHoverLinkTests
{
    static JObject Receipt(string id, string subject, string text) =>
        new JObject { ["role"] = "receipt", ["id"] = id, ["ok"] = true, ["subject"] = subject, ["text"] = text };

    static List<JObject> Thread() => new List<JObject>
    {
        new JObject { ["role"] = "user", ["id"] = "m1", ["text"] = "draw w05 please" },
        Receipt("m2", "w05", "Added w05, a 200 mm wall standing on its own, 3400 mm long."),
        Receipt("m3", "w05", "Drew 3 walls (w05, w06, w07), 200 mm thick, 9.8 m in all."),
        Receipt("m4", "D02", "D02 moved 400 mm along w06"),
        Receipt("m5", "Generate 3D", "ok"),
        new JObject { ["role"] = "assistant", ["id"] = "m6", ["text"] = "w05 is the north wall" }
    };

    static ForskHoverLink Built()
    {
        var link = new ForskHoverLink();
        link.Rebuild(Thread());
        return link;
    }

    [Fact]
    public void AnElement_LightsEveryReceiptThatNamesIt_InThreadOrder()
    {
        var link = Built();
        Assert.Equal(new[] { "m2", "m3" }, link.Hover(true, new[] { "w05" }));
        Assert.Equal(new[] { "m3", "m4" }, link.Hover(true, new[] { "w06" }));
        Assert.Equal(new[] { "m4" }, link.Hover(true, new[] { "D02" }));
    }

    [Fact]
    public void OnlyReceiptsCount_NotTheUsersWordsOrAnAnswer()
    {
        var link = Built();
        Assert.DoesNotContain("m1", link.Rows(new[] { "w05" }));
        Assert.DoesNotContain("m6", link.Rows(new[] { "w05" }));
        Assert.Equal(4, link.Elements);
    }

    [Fact]
    public void AnObjectNamedByAnIdAndAMark_LightsTheRowsOfBoth()
    {
        var link = Built();
        Assert.Equal(new[] { "m2", "m3", "m4" }, link.Hover(true, new[] { "w05", "D02" }));
    }

    [Fact]
    public void TheIdsAreMatchedWithoutCase()
    {
        Assert.Equal(new[] { "m4" }, Built().Hover(true, new[] { "d02" }));
    }

    [Fact]
    public void TheSameElementAgain_SendsNothing()
    {
        var link = Built();
        Assert.NotNull(link.Hover(true, new[] { "w05" }));
        for (var i = 0; i < 1000; i++) Assert.Null(link.Hover(true, new[] { "w05" }));
    }

    [Fact]
    public void AnotherElementThatLightsTheSameRows_SendsNothing()
    {
        var thread = new List<JObject> { Receipt("m1", "w01", "Moved w01 and w02 400 mm") };
        var link = new ForskHoverLink();
        link.Rebuild(thread);
        Assert.Equal(new[] { "m1" }, link.Hover(true, new[] { "w01" }));
        Assert.Null(link.Hover(true, new[] { "w02" }));
    }

    [Fact]
    public void AnElementTheChatNeverNamed_ClearsWhatWasLit()
    {
        var link = Built();
        link.Hover(true, new[] { "w05" });
        Assert.Empty(link.Hover(true, new[] { "w99" }));
        Assert.Null(link.Hover(true, new[] { "w98" }));
    }

    [Fact]
    public void NothingUnderThePointer_ClearsOnce()
    {
        var link = Built();
        Assert.Null(link.Hover(true, new string[0]));
        link.Hover(true, new[] { "D02" });
        Assert.Empty(link.Hover(true, new string[0]));
        Assert.Null(link.Hover(true, null));
    }

    [Fact]
    public void WithTheSettingOff_NothingLights_AndWhatWasLitClears()
    {
        var link = Built();
        Assert.Null(link.Hover(false, new[] { "w05" }));
        Assert.NotNull(link.Hover(true, new[] { "w05" }));
        Assert.Empty(link.Hover(false, new[] { "w05" }));
        Assert.Null(link.Hover(false, new[] { "w06" }));
    }

    [Fact]
    public void LeavingTheViewport_Clears()
    {
        var link = Built();
        link.Hover(true, new[] { "w06" });
        Assert.Empty(link.Clear());
        Assert.Null(link.Clear());
    }

    [Fact]
    public void ANewReceipt_LightsUnderAPointerThatHasNotMoved()
    {
        var thread = Thread();
        var link = new ForskHoverLink();
        link.Rebuild(thread);
        link.Hover(true, new[] { "D02" });
        thread.Add(Receipt("m7", "D02", "D02 moved 100 mm"));
        Assert.Equal(new[] { "m4", "m7" }, link.Rebuild(thread));
        Assert.Null(link.Rebuild(thread));
    }

    [Fact]
    public void ADeletedThread_ForgetsItsElements()
    {
        var link = Built();
        link.Hover(true, new[] { "w05" });
        Assert.Empty(link.Rebuild(null));
        Assert.Equal(0, link.Elements);
    }

    [Fact]
    public void TheDebounce_IsTheMoveRestingBriefly_AndTheStyleIsOneClass()
    {
        Assert.InRange(ForskHoverLink.DebounceMs, 50, 300);
        Assert.Equal("hl", ForskHoverLink.Style);
    }

    [Fact]
    public void TheIdsInAText_AreTheReceiptsOwnRule()
    {
        Assert.Equal(new[] { "w05", "w06", "D02", "rd-01" }, ForskReceipt.IdsIn("w05 and w06, D02 next to rd-01, w05 again"));
        Assert.Empty(ForskReceipt.IdsIn("2024 x w5 Dw05"));
    }

    [Fact]
    public void ThePage_DrawsTheHighlightWithTheSameClass_AndKeepsItAcrossARender()
    {
        var js = File.ReadAllText(Path.Combine(PageDir(), "window.js"));
        var html = File.ReadAllText(Path.Combine(PageDir(), "window.html"));
        Assert.Contains("Forsk.hoverStyle = '" + ForskHoverLink.Style + "'", js, StringComparison.Ordinal);
        Assert.Contains("Forsk.hover = function (ids)", js, StringComparison.Ordinal);
        Assert.Contains("'data-item', item.id", js, StringComparison.Ordinal);
        Assert.Contains(".receipt." + ForskHoverLink.Style, html, StringComparison.Ordinal);
        var render = js.Substring(js.IndexOf("Forsk.render = function", StringComparison.Ordinal));
        Assert.Contains("showHover();", render.Substring(0, render.IndexOf("Forsk.hover = function", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheViewportSide_AsksOnceTheMoveHasRested_AndObeysTheSameSetting()
    {
        var view = File.ReadAllText(Path.Combine(ForskDir(), "ForskViewHover.cs"));
        var window = File.ReadAllText(Path.Combine(ForskDir(), "ForskWindow.cs"));
        var glue = File.ReadAllText(Path.Combine(ForskDir(), "ForskWindowHover.cs"));
        Assert.Contains("ForskHoverLink.DebounceMs", view, StringComparison.Ordinal);
        Assert.Contains("new ForskViewHover(HoverOn, ViewHovered)", window, StringComparison.Ordinal);
        Assert.Contains("_link.Hover(HoverOn(), elementIds)", glue, StringComparison.Ordinal);
        Assert.Contains("_link.Rebuild(thread?.Items)", window, StringComparison.Ordinal);
        var move = view.Substring(view.IndexOf("OnMouseMove", StringComparison.Ordinal));
        move = move.Substring(0, move.IndexOf("Forget()", StringComparison.Ordinal));
        Assert.DoesNotContain("PickObjects", move, StringComparison.Ordinal);
    }

    static string ForskDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Forsk");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("plugin/Forsk above " + AppContext.BaseDirectory);
    }

    static string PageDir() => Path.Combine(ForskDir(), "Page");
}
