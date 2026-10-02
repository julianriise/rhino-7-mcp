using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// D0: the window's remembered place is clamped to a screen that is there
/// now, on every open. A second screen that was unplugged does not leave the
/// window off-screen.
/// </summary>
public class WindowBoundsTests
{
    // A laptop screen with the menu bar, and a larger screen to its right.
    static readonly ScreenRect Laptop = new ScreenRect(0, 25, 1512, 957);
    static readonly ScreenRect Second = new ScreenRect(1512, -200, 2560, 1415);

    static void Inside(ScreenRect place, ScreenRect screen)
    {
        Assert.True(place.X >= screen.X && place.Y >= screen.Y, "Top-left off the screen: " + place);
        Assert.True(place.Right <= screen.Right && place.Bottom <= screen.Bottom, "Bottom-right off the screen: " + place);
    }

    [Fact]
    public void FirstOpen_DefaultSize_RightSideOfTheMainScreen()
    {
        var place = WindowBounds.Clamp(null, new[] { Laptop, Second });
        Assert.Equal(WindowBounds.DefaultWidth, place.Width);
        Assert.Equal(WindowBounds.DefaultHeight, place.Height);
        Inside(place, Laptop);
        Assert.True(place.X > Laptop.Width / 2);
    }

    [Fact]
    public void OnTheSecondScreen_StaysThere()
    {
        var saved = new ScreenRect(3000, 100, 500, 900);
        var place = WindowBounds.Clamp(saved, new[] { Laptop, Second });
        Assert.Equal(saved.ToString(), place.ToString());
    }

    [Fact]
    public void SecondScreenUnplugged_OpensOnTheMainScreen_SameSize()
    {
        var saved = new ScreenRect(3000, 100, 500, 900);
        var place = WindowBounds.Clamp(saved, new[] { Laptop });
        Inside(place, Laptop);
        Assert.Equal(500, place.Width);
        Assert.Equal(900, place.Height);
    }

    [Fact]
    public void PartlyOffTheEdge_IsPulledBackOn()
    {
        var saved = new ScreenRect(1300, 700, 420, 680);
        var place = WindowBounds.Clamp(saved, new[] { Laptop });
        Inside(place, Laptop);
        Assert.Equal(420, place.Width);
    }

    [Fact]
    public void TallerThanTheScreen_ShrinksToFit()
    {
        var saved = new ScreenRect(100, 25, 420, 1400);
        var place = WindowBounds.Clamp(saved, new[] { Laptop });
        Inside(place, Laptop);
        Assert.Equal(Laptop.Height, place.Height);
    }

    [Fact]
    public void TooSmall_GrowsToTheMinimum()
    {
        var place = WindowBounds.Clamp(new ScreenRect(200, 200, 100, 80), new[] { Laptop });
        Assert.Equal(WindowBounds.MinWidth, place.Width);
        Assert.Equal(WindowBounds.MinHeight, place.Height);
        Inside(place, Laptop);
    }

    [Fact]
    public void StraddlingTwoScreens_GoesToTheOneItCoversMost()
    {
        var saved = new ScreenRect(1400, 100, 420, 680); // 112 pt on the laptop, 308 on the second screen
        var place = WindowBounds.Clamp(saved, new[] { Laptop, Second });
        Inside(place, Second);
    }

    [Theory]
    [InlineData("1512.5,30,420,680", "1513,30 420x680")] // ToString rounds; the store keeps the half point.
    [InlineData(" 10 , 20 , 300 , 400 ", "10,20 300x400")]
    public void StoredText_ReadsBack(string text, string expected)
    {
        Assert.Equal(expected, WindowBounds.Parse(text)!.Value.ToString());
        Assert.Equal(WindowBounds.Parse(text)!.Value.ToString(), WindowBounds.Parse(WindowBounds.Format(WindowBounds.Parse(text)!.Value))!.Value.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10,20,300")]
    [InlineData("a,b,c,d")]
    [InlineData("10,20,0,400")]
    [InlineData("NaN,20,300,400")]
    public void BadStoredText_IsAFirstOpen(string text)
    {
        Assert.Null(WindowBounds.Parse(text));
        Inside(WindowBounds.Clamp(WindowBounds.Parse(text), new[] { Laptop }), Laptop);
    }

    [Fact]
    public void NoScreenReported_KeepsThePlace()
    {
        var saved = new ScreenRect(40, 60, 420, 680);
        Assert.Equal(saved.ToString(), WindowBounds.Clamp(saved, new ScreenRect[0]).ToString());
    }
}
