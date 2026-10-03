using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>Chrome is closed only when that piece is open, and the snapshot puts it back.</summary>
public class ForskChromeTests
{
    [Fact]
    public void Hide_closes_only_what_is_open_and_keeps_the_command_field()
    {
        var live = new[]
        {
            new ForskChrome.Piece(ForskChrome.Palettes, true),
            new ForskChrome.Piece(ForskChrome.RightSidebar, false),
            new ForskChrome.Piece(ForskChrome.Toolbar, true),
            new ForskChrome.Piece(ForskChrome.StatusBar, true),
            new ForskChrome.Piece(ForskChrome.CommandField, true)
        };
        var steps = ForskChrome.HidePlan(live);
        Assert.Equal(new[] { "palettes", "toolbar", "status" }, Ids(steps));
        foreach (var step in steps)
            Assert.False(step.Open);
        Assert.DoesNotContain(steps, step => step.Id == ForskChrome.CommandField);
        Assert.DoesNotContain(steps, step => step.Id == ForskChrome.RightSidebar);
    }

    [Fact]
    public void Restore_puts_the_snapshot_back_and_skips_a_match()
    {
        var snapshot = new[]
        {
            new ForskChrome.Piece(ForskChrome.Palettes, true),
            new ForskChrome.Piece(ForskChrome.RightSidebar, false),
            new ForskChrome.Piece(ForskChrome.Toolbar, true),
            new ForskChrome.Piece(ForskChrome.StatusBar, true)
        };
        var hidden = new[]
        {
            new ForskChrome.Piece(ForskChrome.Palettes, false),
            new ForskChrome.Piece(ForskChrome.RightSidebar, false),
            new ForskChrome.Piece(ForskChrome.Toolbar, false),
            new ForskChrome.Piece(ForskChrome.StatusBar, false)
        };
        var steps = ForskChrome.RestorePlan(snapshot, hidden);
        Assert.Equal(new[] { "palettes", "toolbar", "status" }, Ids(steps));
        foreach (var step in steps)
            Assert.True(step.Open);

        var stored = ForskChrome.Format(snapshot);
        Assert.Equal("palettes=1,right=0,toolbar=1,status=1", stored);
        var round = ForskChrome.Parse(stored + ",command=1");
        Assert.Equal(4, round.Count);
        Assert.DoesNotContain(round, piece => piece.Id == ForskChrome.CommandField);
        Assert.Empty(ForskChrome.RestorePlan(snapshot, snapshot));
    }

    static string[] Ids(IReadOnlyList<ForskChrome.Piece> steps)
    {
        var ids = new string[steps.Count];
        for (var i = 0; i < steps.Count; i++) ids[i] = steps[i].Id;
        return ids;
    }
}
