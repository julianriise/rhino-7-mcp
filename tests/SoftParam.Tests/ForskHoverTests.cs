using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Hovering the chat takes the keyboard; leaving it returns the keyboard to Rhino.
/// The setting ForskHoverFocus defaults on. Typing or a drag does not steal focus.
/// </summary>
public class ForskHoverTests
{
    [Fact]
    public void Enter_FocusesTheChat_AndLeave_ReturnsToRhino()
    {
        Assert.Equal(ForskHover.Chat, ForskHover.Decide(true, "enter", false, false));
        Assert.Equal(ForskHover.Rhino, ForskHover.Decide(true, "leave", false, false));
        Assert.Equal(ForskHover.None, ForskHover.Decide(true, "other", false, false));
    }

    [Theory]
    [InlineData(true, "enter", true, false)]
    [InlineData(true, "leave", true, false)]
    [InlineData(true, "enter", false, true)]
    [InlineData(true, "leave", false, true)]
    [InlineData(false, "enter", false, false)]
    [InlineData(false, "leave", false, false)]
    public void Typing_ADrag_OrTheSetting_LeavesFocusAlone(bool enabled, string edge, bool typing, bool dragging)
    {
        Assert.Equal(ForskHover.None, ForskHover.Decide(enabled, edge, typing, dragging));
    }

    [Fact]
    public void TheSetting_DefaultsOn_AndLeave_HandsBackToRhino()
    {
        Assert.Equal("ForskHoverFocus", ForskHover.SettingKey);
        var window = File.ReadAllText(Path.Combine(ForskDir(), "ForskWindow.cs"));
        Assert.Contains("GetBool(ForskHover.SettingKey, true)", window, StringComparison.Ordinal);
        Assert.Contains("HandToRhino()", window, StringComparison.Ordinal);
        Assert.Contains("MakeKeyWindow", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandThatIsReadingKeys_KeepsThem()
    {
        Assert.Equal(ForskHover.None, ForskHover.Decide(true, "enter", false, false, true));
        Assert.Equal(ForskHover.None, ForskHover.Decide(true, "leave", false, false, true));
        Assert.Equal(ForskHover.Chat, ForskHover.Decide(true, "enter", false, false, false));
    }

    [Fact]
    public void TheChat_AcceptsTheClickThatActivatesIt_AndKeysOnEnterUnlessACommandIsRunning()
    {
        var window = File.ReadAllText(Path.Combine(ForskDir(), "ForskWindow.cs"));
        var aqua = File.ReadAllText(Path.Combine(ForskDir(), "ForskAqua.cs"));
        Assert.Contains("acceptsFirstMouse:", aqua, StringComparison.Ordinal);
        Assert.Contains("ArmClick", window, StringComparison.Ordinal);
        Assert.Contains("MouseEnter", window, StringComparison.Ordinal);
        Assert.Contains("Command.InCommand()", window, StringComparison.Ordinal);
    }

    static string ForskDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Forsk");
            if (Directory.Exists(path)) return path;
            path = Path.Combine(dir.FullName, "Forsk");
            if (File.Exists(Path.Combine(path, "ForskWindow.cs"))) return path;
        }
        throw new DirectoryNotFoundException("plugin/Forsk above " + AppContext.BaseDirectory);
    }
}
