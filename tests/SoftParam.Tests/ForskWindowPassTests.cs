using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The chat is a normal form owned by the active document's Rhino window,
/// and follows it when File > New or Open replaces that window. Hovering the viewport
/// does not select or light a receipt, and hovering the chat does not take the
/// keyboard. A click still selects, the slot chips still fire, and Tab is still
/// the only way a button gets a ring.
/// </summary>
public class ForskWindowPassTests
{
    [Theory]
    [InlineData("ForskWindow.cs", "sealed partial class ForskWindow : Form")]
    [InlineData("ForskWindow.cs", "Topmost = false")]
    [InlineData("ForskWindow.cs", "ShowActivated = false")]
    [InlineData("ForskWindow.cs", "MainWindowForDocument(doc)")]
    [InlineData("ForskWindow.cs", "RhinoEtoApp.MainWindow")]
    [InlineData("ForskWindow.cs", "void OwnTo(RhinoDoc doc)")]
    [InlineData("ForskWindow.cs", "serial == _ownerDoc")]
    [InlineData("ForskWindow.cs", "Owner = null;")]
    [InlineData("ForskWindow.cs", "static void FollowSoon()")]
    [InlineData("ForskWindow.cs", "open.OwnTo(doc);")]
    [InlineData("ForskWindow.cs", "HandToRhino()")]
    [InlineData("ForskWindow.cs", "FocusComposer()")]
    [InlineData("ForskWindow.cs", "RhinoDoc.SelectObjects +=")]
    [InlineData("ForskWindow.cs", "RhinoDoc.DeselectObjects +=")]
    [InlineData("ForskWindow.cs", "RhinoDoc.DeselectAllObjects +=")]
    public void TheWindow_StaysANormalOwnedForm_AndClickSelectionStays(string file, string needle)
    {
        Assert.Contains(needle, Read(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ForskWindow.cs", "FloatingForm")]
    [InlineData("ForskWindow.cs", "if (Owner != null) return;")]
    [InlineData("ForskWindow.cs", "MakeKeyWindow")]
    [InlineData("ForskWindow.cs", "ForskHover")]
    [InlineData("ForskWindow.cs", "ForskViewHover")]
    [InlineData("ForskWindow.cs", "ForskHoverLink")]
    [InlineData("ForskWindow.cs", "ForskHoverFocus")]
    [InlineData("ForskWindow.cs", "hoverFocus")]
    [InlineData("ForskWindow.cs", "case \"hover\"")]
    [InlineData("ForskWindow.cs", "FinishHover")]
    [InlineData("ForskWindow.cs", "hidesOnDeactivate")]
    [InlineData("ForskWindow.cs", "NSPanel")]
    [InlineData("ForskWindow.cs", "NSWindow")]
    [InlineData("ForskWindow.cs", "objc_msgSend")]
    [InlineData("ForskWindow.cs", "AddChildWindow")]
    [InlineData("ForskWindow.cs", "sel_registerName")]
    [InlineData("ForskWindow.cs", "PerformSelector")]
    public void TheWindow_HasNoHoverSelect_AndNoNativeWindowHook(string file, string needle)
    {
        Assert.DoesNotContain(needle, Read(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("window.js", "hoverAction")]
    [InlineData("window.js", "Forsk.hover")]
    [InlineData("window.js", "hoverFocus")]
    [InlineData("window.js", "kind: 'hover'")]
    [InlineData("window.html", ".receipt.hl")]
    public void ThePage_DoesNotLightARowOrTakeTheKeyboardOnHover(string file, string needle)
    {
        Assert.DoesNotContain(needle, Read(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("window.js", "if (e.key === 'Tab') document.documentElement.setAttribute('data-kbd', '')")]
    [InlineData("window.js", "document.documentElement.removeAttribute('data-kbd')")]
    [InlineData("window.js", "Forsk.keyAction = function")]
    [InlineData("window.js", "sender.send({ kind: 'action', id: slot.id })")]
    [InlineData("window.html", "html[data-kbd] button:focus-visible { outline: 1.5px solid rgba(41, 72, 245, 0.55); outline-offset: 2px; }")]
    [InlineData("window.html", "button:focus, textarea:focus, input:focus, select:focus { outline: none; }")]
    public void Tab_StillDrawsTheOnlyRing_AndTheSlotChipsStillFire(string file, string needle)
    {
        Assert.Contains(needle, Read(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ForskHover.cs")]
    [InlineData("ForskHoverLink.cs")]
    [InlineData("ForskViewHover.cs")]
    [InlineData("ForskWindowHover.cs")]
    public void TheHoverSelectFiles_AreGone(string name)
    {
        Assert.False(File.Exists(Path.Combine(ForskDir(), name)), name);
    }

    [Fact]
    public void ReceiptIds_StillReadAWallADoorAndARoom_AndSkipADimension()
    {
        Assert.Equal(new[] { "w05", "w06", "D02", "rd-01" }, ForskReceipt.IdsIn("w05 and w06, D02 next to rd-01, w05 again"));
        Assert.Empty(ForskReceipt.IdsIn("2024 x w5 Dw05"));
    }

    static string Read(string name)
    {
        var dir = name.EndsWith(".js", StringComparison.Ordinal) || name.EndsWith(".html", StringComparison.Ordinal)
            ? Path.Combine(ForskDir(), "Page")
            : ForskDir();
        return File.ReadAllText(Path.Combine(dir, name));
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
}
