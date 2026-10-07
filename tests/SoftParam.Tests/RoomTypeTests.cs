using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;
using Box = RhinoMCPPlugin.Functions.RoomDetect.Box;

namespace SoftParam.Tests;

/// <summary>
/// Room types: the English key, the label map, the first-pass guess, and the
/// rule that a user or label type is not replaced by a guess.
/// </summary>
public class RoomTypeTests
{
    static RoomTypes.GuessInput Facts(int doors, int windows, double areaM2, double aspect) =>
        new RoomTypes.GuessInput(doors, windows, areaM2, aspect);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nope")]
    [InlineData("stue")]
    public void Read_MissingOrUnknown_IsUnassigned(string stored)
    {
        Assert.Equal(RoomTypes.Unassigned, RoomTypes.Read(stored));
        Assert.Equal("", RoomTypes.ReadSource(stored));
    }

    [Theory]
    [InlineData("bedroom", "bedroom")]
    [InlineData("Bedroom", "bedroom")]
    [InlineData("  WC  ", "wc")]
    [InlineData("unassigned", "unassigned")]
    public void Read_KnownKey_FoldsCase(string stored, string key)
    {
        Assert.Equal(key, RoomTypes.Read(stored));
        Assert.True(RoomTypes.TryParse(stored, out var parsed));
        Assert.Equal(key, parsed);
    }

    [Theory]
    [InlineData("stue")]
    [InlineData("living room")]
    public void TryParse_RejectsALabelThatIsNotAKey(string text)
    {
        Assert.False(RoomTypes.TryParse(text, out _));
    }

    [Theory]
    [InlineData("Stue", "living")]
    [InlineData("LIVING", "living")]
    [InlineData("Kjøkken", "kitchen")]
    [InlineData("kjokken", "kitchen")]
    [InlineData("Soverom 2", "bedroom")]
    [InlineData("Sov 3", "bedroom")]
    [InlineData("Bad", "bathroom")]
    [InlineData("baderom", "bathroom")]
    [InlineData("bath", "bathroom")]
    [InlineData("WC", "wc")]
    [InlineData("Toalett", "wc")]
    [InlineData("Gang", "hall")]
    [InlineData("Entré", "hall")]
    [InlineData("entrance hall", "hall")]
    [InlineData("corridor", "hall")]
    [InlineData("Bod", "storage")]
    [InlineData("Vaskerom", "laundry")]
    [InlineData("Teknisk rom", "technical")]
    [InlineData("Kontor", "office")]
    [InlineData("Garasje", "garage")]
    [InlineData("Balkong", "balcony")]
    [InlineData("Terrasse", "balcony")]
    [InlineData("Trapp", "stair")]
    [InlineData("Dining room", "dining")]
    [InlineData("Spisestue", "dining")]
    [InlineData("Stue/kjøkken", "living")]
    public void FromLabel_MapsNorwegianAndEnglishWords(string name, string type)
    {
        Assert.Equal(type, RoomTypes.FromLabel(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Room")]
    [InlineData("Rom")]
    [InlineData("Rom 2")]
    [InlineData("stuebord")]
    [InlineData("badstu")]
    [InlineData("balkongbad")]
    public void FromLabel_DefaultOrUnbrokenWords_StayUnassigned(string name)
    {
        Assert.Equal(RoomTypes.Unassigned, RoomTypes.FromLabel(name));
    }

    [Fact]
    public void FromLabel_SovIsAWord_AndDoesNotEatSoveromByAccident()
    {
        Assert.Equal(RoomTypes.Bedroom, RoomTypes.FromLabel("sov"));
        Assert.Equal(RoomTypes.Bedroom, RoomTypes.FromLabel("soverom"));
        Assert.Equal(RoomTypes.Unassigned, RoomTypes.FromLabel("sover"));
    }

    [Fact]
    public void Guess_NoDoor_StaysUnassigned()
    {
        Assert.Equal(RoomTypes.Unassigned, RoomTypes.Guess(Facts(0, 0, 3, 1)));
        Assert.Equal(RoomTypes.Unassigned, RoomTypes.Guess(Facts(0, 2, 6, 4)));
    }

    [Fact]
    public void Guess_SmallDarkRoomWithOneDoor_IsStorage()
    {
        Assert.Equal(RoomTypes.Storage, RoomTypes.Guess(Facts(1, 0, 3.2, 1.2)));
        Assert.Equal(RoomTypes.Unassigned, RoomTypes.Guess(Facts(1, 1, 3.2, 1.2)));
        // Two doors is not the storage shape. With no window it falls through to a bathroom.
        Assert.Equal(RoomTypes.Bathroom, RoomTypes.Guess(Facts(2, 0, 3.2, 1.2)));
    }

    [Fact]
    public void Guess_DarkRoomBetweenTwoAndAHalfAndEight_IsABathroom()
    {
        Assert.Equal(RoomTypes.Bathroom, RoomTypes.Guess(Facts(1, 0, 6, 1.4)));
        // 2.5 m² with one door is the storage rule (under 4 m²). Two doors keeps the bathroom band.
        Assert.Equal(RoomTypes.Storage, RoomTypes.Guess(Facts(1, 0, 2.5, 1)));
        Assert.Equal(RoomTypes.Bathroom, RoomTypes.Guess(Facts(2, 0, 2.5, 1)));
        Assert.Equal(RoomTypes.Bathroom, RoomTypes.Guess(Facts(1, 0, 8, 1)));
        Assert.Equal(RoomTypes.Unassigned, RoomTypes.Guess(Facts(1, 1, 6, 1.4)));
        Assert.Equal(RoomTypes.Unassigned, RoomTypes.Guess(Facts(1, 0, 9, 1.2)));
    }

    [Fact]
    public void Guess_LongAndNarrowWithSeveralDoors_IsAHall()
    {
        Assert.Equal(RoomTypes.Hall, RoomTypes.Guess(Facts(3, 1, 8, 4)));
        Assert.Equal(RoomTypes.Hall, RoomTypes.Guess(Facts(2, 0, 6, 3)));
        Assert.Equal(RoomTypes.Bathroom, RoomTypes.Guess(Facts(2, 0, 6, 1.5)));
    }

    [Fact]
    public void Choose_KeepsAUserType_OverALabelAndAGuess()
    {
        var decision = RoomTypes.Choose(RoomTypes.Bedroom, RoomTypes.User, "Kjøkken", Facts(1, 0, 3, 1));
        Assert.Equal(RoomTypes.Bedroom, decision.Type);
        Assert.Equal(RoomTypes.User, decision.Source);
    }

    [Fact]
    public void Choose_KeepsALabelType_WhenTheNameNoLongerMaps()
    {
        var decision = RoomTypes.Choose(RoomTypes.Kitchen, RoomTypes.Label, "Rom", Facts(1, 0, 3, 1));
        Assert.Equal(RoomTypes.Kitchen, decision.Type);
        Assert.Equal(RoomTypes.Label, decision.Source);
    }

    [Fact]
    public void Choose_ANewLabel_ReplacesAGuess_AndAnEarlierLabel()
    {
        var fromGuess = RoomTypes.Choose(RoomTypes.Storage, RoomTypes.Guessed, "Soverom", Facts(1, 0, 3, 1));
        Assert.Equal(RoomTypes.Bedroom, fromGuess.Type);
        Assert.Equal(RoomTypes.Label, fromGuess.Source);

        var fromLabel = RoomTypes.Choose(RoomTypes.Bedroom, RoomTypes.Label, "Kjøkken", Facts(1, 0, 12, 1));
        Assert.Equal(RoomTypes.Kitchen, fromLabel.Type);
        Assert.Equal(RoomTypes.Label, fromLabel.Source);
    }

    [Fact]
    public void Choose_GuessesOnlyWhenNothingWasSet()
    {
        var guessed = RoomTypes.Choose(null, null, "Rom", Facts(1, 0, 3, 1.2));
        Assert.Equal(RoomTypes.Storage, guessed.Type);
        Assert.Equal(RoomTypes.Guessed, guessed.Source);

        var none = RoomTypes.Choose(null, null, "Room", Facts(0, 0, 20, 1));
        Assert.Equal(RoomTypes.Unassigned, none.Type);
        Assert.Equal("", none.Source);
    }

    [Fact]
    public void Measure_CountsADoorOnTheBoundary_AndNotOneAcrossThePlan()
    {
        var ring = new List<Pt> { new(0, 0), new(4000, 0), new(4000, 3000), new(0, 3000) };
        var doors = new List<Box> { new(1800, -100, 2700, 100), new(20000, 20000, 21000, 21000) };
        var windows = new List<Box> { new(100, 1000, 200, 2200) };
        var facts = RoomTypes.Measure(ring, 12_000_000, doors, windows);
        Assert.Equal(1, facts.Doors);
        Assert.Equal(1, facts.Windows);
        Assert.Equal(12, facts.AreaM2, 3);
        Assert.Equal(4000.0 / 3000.0, facts.Aspect, 3);
        Assert.Equal(RoomTypes.Unassigned, RoomTypes.Guess(facts));
    }

    [Fact]
    public void Colour_IsAFlatPastel_AndUnassignedIsLightGrey()
    {
        var seen = new HashSet<string>();
        foreach (var key in RoomTypes.All)
        {
            var colour = RoomTypes.Colour(key);
            Assert.InRange(colour.R, 0, 255);
            Assert.InRange(colour.G, 0, 255);
            Assert.InRange(colour.B, 0, 255);
            Assert.True(seen.Add(colour.R + "," + colour.G + "," + colour.B), key);
            Assert.True(colour.R > 160 && colour.G > 140 && colour.B > 140, key);
        }
        var plain = RoomTypes.Colour(null);
        Assert.Equal(236, plain.R);
        Assert.Equal(236, plain.G);
        Assert.Equal(236, plain.B);
        Assert.Equal("Unassigned", RoomTypes.English(null));
        Assert.Equal("Bedroom", RoomTypes.English("bedroom"));
        Assert.Equal("", RoomTypes.LineSuffix(null));
        Assert.Equal(" · Bedroom", RoomTypes.LineSuffix("bedroom"));
    }

    [Fact]
    public void ShowInView_IsPerspectiveOnly_AndOffMeansNone()
    {
        Assert.True(RoomTypes.ShowInView(true, false, 0.3, 0.2, -0.4));
        Assert.False(RoomTypes.ShowInView(true, true, 0, 0, -1));
        Assert.False(RoomTypes.ShowInView(true, true, 1, 0, 0));
        Assert.False(RoomTypes.ShowInView(false, false, 0.3, 0.2, -0.4));
    }

    [Fact]
    public void ATypedRoom_GroupsTheAreaScheduleInEnglish()
    {
        var result = AreaStats.Compute(new[]
        {
            new AreaStats.Room { Id = "rd-01", Name = "Stue", AreaMm2 = 20_000_000, RoomType = RoomTypes.Living },
            new AreaStats.Room { Id = "rd-02", Name = "Rom", AreaMm2 = 4_000_000 }
        });
        Assert.Equal(RoomTypes.Living, result.Rooms.Single(r => r.Id == "rd-01").RoomType);
        Assert.Equal("Living", result.Rooms.Single(r => r.Id == "rd-01").Use);
        Assert.Equal(RoomTypes.Unassigned, result.Rooms.Single(r => r.Id == "rd-02").RoomType);
        Assert.Contains("Living", result.Summary);
        Assert.DoesNotContain("Stue", result.Summary);

        var table = Schedules.RoomTable(new[]
        {
            new Schedules.Room { Id = "rd-01", Name = "Stue", AreaMm2 = 20_000_000, RoomType = RoomTypes.Living }
        });
        Assert.Equal(new[] { "Room", "Area", "Type" }, table.Heads);
        Assert.Equal("Stue", table.Rows[0][0]);
        Assert.Equal("Living", table.Rows[0][2]);
        Assert.Equal(new[] { "Sum", "20.0 m²" }, table.Total);
    }

    /// <summary>Furnish and Add furniture: a room asked for by any of its type's words, English or Norwegian.</summary>
    [Theory]
    [InlineData("toilet", "wc", "Bathroom", true)]
    [InlineData("the toilet", "wc", "Bathroom", true)]
    [InlineData("toalett", "wc", "", true)]
    [InlineData("wc", "wc", "Bad", true)]
    [InlineData("bath", "unassigned", "Baderom", true)]
    [InlineData("soverom", "bedroom", "Master", true)]
    [InlineData("toilet", "bathroom", "Bathroom", false)]
    [InlineData("bath", "wc", "Bad", false)]
    [InlineData("kitchen", "living", "Stue", false)]
    [InlineData("room", "bedroom", "Room", false)]
    [InlineData("", "bedroom", "Bedroom", false)]
    public void ARoomAskedForBy_ATypeWord_MatchesItsType(string asked, string type, string name, bool expected)
    {
        Assert.Equal(expected, RoomTypes.Matches(asked, type, name));
    }
}
