using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F4.3: the panel classifies daylight from chat, and the Daylight chip under
/// Print runs, clears, or says what is missing. Print stays the primary chip.
/// </summary>
public class PanelDaylightTests
{
    [Theory]
    [InlineData("run daylight")]
    [InlineData("Run daylight analysis")]
    [InlineData("is this room dark?")]
    [InlineData("clear daylight")]
    [InlineData("fjern dagslys")]
    [InlineData("which room is brightest")]
    public void DaylightWords_ClassifyAsDaylight(string text)
    {
        Assert.Equal(ForskIntent.Daylight, ForskIntentRouter.Classify(text, ""));
    }

    [Theory]
    [InlineData("print pdf", ForskIntent.Print)]
    [InlineData("make sheets", ForskIntent.Sheets)]
    [InlineData("move the window 200 along the wall", ForskIntent.Edit)]
    [InlineData("rebuild", ForskIntent.Build)]
    [InlineData("hello", ForskIntent.General)]
    public void OtherTurns_KeepTheirIntent(string text, ForskIntent expected)
    {
        Assert.Equal(expected, ForskIntentRouter.Classify(text, ""));
    }

    [Fact]
    public void NoWalls_HidesTheDaylightChip()
    {
        var chip = new BakeChip { HasPlan = true };
        Assert.False(chip.ShowDaylight);
        Assert.Equal("Generate 3D model", chip.Label);
    }

    [Fact]
    public void WallsWindowsRooms_RunDaylightUnderPrint()
    {
        var chip = new BakeChip { HasWalls = true, HasWindows = true, HasRooms = true };
        Assert.Equal("Print PDF", chip.Label);
        Assert.True(chip.ShowDaylight);
        Assert.True(chip.DaylightEnabled);
        Assert.False(chip.DaylightClears);
        Assert.Equal("Daylight", chip.DaylightLabel);
    }

    [Fact]
    public void Overlay_ClearsDaylight()
    {
        var chip = new BakeChip { HasWalls = true, HasWindows = true, HasRooms = true, HasOverlay = true };
        Assert.True(chip.DaylightEnabled);
        Assert.True(chip.DaylightClears);
        Assert.Equal("Clear daylight", chip.DaylightLabel);
    }

    [Fact]
    public void NoRooms_DisablesWithTheA_RoomHint()
    {
        var chip = new BakeChip { HasWalls = true, HasWindows = true };
        Assert.True(chip.ShowDaylight);
        Assert.False(chip.DaylightEnabled);
        Assert.Equal("Needs rooms (A-ROOM)", chip.DaylightLabel);
    }

    [Fact]
    public void NoWindows_DisablesWithTheWindowHint()
    {
        var chip = new BakeChip { HasWalls = true, HasRooms = true };
        Assert.False(chip.DaylightEnabled);
        Assert.Equal("Needs windows", chip.DaylightLabel);
    }
}
