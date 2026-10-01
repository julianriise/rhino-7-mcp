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

    static ChipRow Wall() => new ChipRow { Generated = true, Kind = "wall", Layer = "A-WALL" };
    static ChipRow Window() => new ChipRow { Generated = true, Kind = "opening_marker", OpeningKind = "window", Layer = "A-OPEN" };
    static ChipRow Door() => new ChipRow { Generated = true, Kind = "opening_marker", OpeningKind = "door", Layer = "A-OPEN" };
    static ChipRow Room() => new ChipRow { Generated = true, Kind = "room", Layer = "A-ROOM" };
    static ChipRow Overlay(bool visible = true) => new ChipRow { Generated = true, Kind = "analysis", Layer = "A-ANALYSE", Visible = visible };

    static BakeChip Chip(params ChipRow[] rows)
    {
        var chip = new BakeChip { HasWalls = true };
        chip.ReadDaylight(rows);
        return chip;
    }

    [Fact]
    public void NoWalls_HidesTheDaylightChip()
    {
        var chip = new BakeChip { HasPlan = true };
        chip.ReadDaylight(new ChipRow[0]);
        Assert.False(chip.ShowDaylight);
        Assert.Equal("Generate 3D model", chip.Label);
    }

    [Fact]
    public void WallsWindowsRooms_RunDaylightUnderPrint()
    {
        var chip = Chip(Wall(), Window(), Room());
        Assert.Equal("Print PDF", chip.Label);
        Assert.True(chip.ShowDaylight);
        Assert.Equal(DaylightAction.Run, chip.Daylight);
        Assert.True(chip.DaylightEnabled);
        Assert.Equal("Daylight", chip.DaylightLabel);
        Assert.Null(chip.DaylightHint);
    }

    [Fact]
    public void OverlayVisible_HidesTheMap()
    {
        var chip = Chip(Wall(), Window(), Room(), Overlay());
        Assert.Equal(DaylightAction.Hide, chip.Daylight);
        Assert.True(chip.DaylightEnabled);
        Assert.Equal("Hide daylight map", chip.DaylightLabel);
    }

    [Fact]
    public void OverlayHidden_ShowsTheMap()
    {
        var chip = Chip(Wall(), Window(), Room(), Overlay(false));
        Assert.True(chip.HasOverlay);
        Assert.False(chip.OverlayVisible);
        Assert.Equal(DaylightAction.Show, chip.Daylight);
        Assert.Equal("Show daylight map", chip.DaylightLabel);
    }

    [Fact]
    public void HiddenMap_StaysAheadOfMakeRooms()
    {
        var chip = Chip(Wall(), Window(), Overlay(false));
        Assert.False(chip.HasRooms);
        Assert.Equal(DaylightAction.Show, chip.Daylight);
    }

    [Fact]
    public void OverlayGone_ReadsFromTheRowsNotAStaleFlag()
    {
        var chip = Chip(Wall(), Window(), Room(), Overlay());
        chip.ReadDaylight(new[] { Wall(), Window(), Room() });
        Assert.False(chip.HasOverlay);
        Assert.False(chip.OverlayVisible);
        Assert.Equal("Daylight", chip.DaylightLabel);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public void AutoRooms_OnlyWhenNoneWereFound(int? count, bool detect)
    {
        Assert.Equal(detect, BakeChip.ShouldDetectRooms(count));
    }

    [Fact]
    public void AutoRooms_SkipsWhenTheRoomStepFailed()
    {
        Assert.False(BakeChip.ShouldDetectRooms(null));
    }

    [Theory]
    [InlineData("clear daylight", DaylightAction.Hide)]
    [InlineData("fjern dagslys", DaylightAction.Hide)]
    [InlineData("Hide daylight map", DaylightAction.Hide)]
    [InlineData("Show daylight map", DaylightAction.Show)]
    [InlineData("run daylight", DaylightAction.None)]
    [InlineData("is this room dark?", DaylightAction.None)]
    public void MapToggle_HidesOrShowsWithoutARerun(string text, DaylightAction action)
    {
        Assert.Equal(action, BakeChip.MapToggle(text));
    }

    [Fact]
    public void NoRooms_OffersMakeRooms()
    {
        var chip = Chip(Wall(), Window());
        Assert.Equal(DaylightAction.MakeRooms, chip.Daylight);
        Assert.True(chip.DaylightEnabled);
        Assert.Equal("Make rooms", chip.DaylightLabel);
    }

    [Theory]
    [InlineData("make rooms")]
    [InlineData("Find rooms please")]
    [InlineData("detect rooms")]
    public void MakeRoomsWords_ClassifyAsBuild(string text)
    {
        Assert.Equal(ForskIntent.Build, ForskIntentRouter.Classify(text, ""));
    }

    [Fact]
    public void NoWindows_DisablesWithATooltip()
    {
        var chip = Chip(Wall(), Door(), Room());
        Assert.Equal(DaylightAction.None, chip.Daylight);
        Assert.False(chip.DaylightEnabled);
        Assert.Equal("Needs windows", chip.DaylightLabel);
        Assert.Equal(BakeChip.NeedsWindowsHint, chip.DaylightHint);
    }

    [Fact]
    public void UntaggedRows_DoNotCountAsRoomsOrWindows()
    {
        var chip = Chip(
            new ChipRow { Kind = "room", Layer = "A-ROOM" },
            new ChipRow { Kind = "opening_marker", OpeningKind = "window" });
        Assert.False(chip.HasRooms);
        Assert.False(chip.HasWindows);
    }
}
