using Xunit;

namespace SoftParam.Tests;

/// <summary>Instant state (Julian, 2026-10-07): a pill shows its new state as it is clicked, before Rhino answers.</summary>
public class InstantStateTests
{
    [Fact]
    public void AChoicePick_FillsAtOnce_AndOtherAnswersPressAndWait()
    {
        var engine = PageScript.Load();
        Assert.Equal("high|null|false", engine.Evaluate("var s = Forsk.pressState({choice:true}, 'high'); s.fill + '|' + s.press + '|' + s.wait").ToString());
        Assert.Equal("null|done|true", engine.Evaluate("var s = Forsk.pressState({choice:true}, 'done'); s.fill + '|' + s.press + '|' + s.wait").ToString());
        Assert.Equal("null|yes|true", engine.Evaluate("var s = Forsk.pressState({}, 'yes'); s.fill + '|' + s.press + '|' + s.wait").ToString());
    }

    [Fact]
    public void ThePage_DrawsAClickBeforeSendingIt()
    {
        var js = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.js"));
        Assert.Contains("showPress(pills, button, Forsk.pressState(item, p.id));\n        sender.send(", js.Replace("\r", ""));
        Assert.Contains("pressed(button); sender.send({ kind: 'action', id: slot.id })", js);
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "page", "window.html"));
        Assert.Contains(".pill.pressed", html);
    }
}
