using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Area statistics from the rooms' own net areas. The sums add up in mm².
/// The printed figure is AreaText of that sum, the same rounding as the plan tag.
/// </summary>
public class AreaStatsTests
{
    [Fact]
    public void Totals_AddUp_AcrossFloorsAndUses()
    {
        var result = AreaStats.Compute(new[]
        {
            Room("rd-01", "Stue", "0", 20_160_000),
            Room("rd-02", "Bedroom", "1", 12_400_000),
            Room("rd-03", "Bad", "0", 4_800_000),
            Room("rd-04", "Kontor", "1", 8_000_000, "Annet")
        });

        Assert.Equal(20_160_000 + 12_400_000 + 4_800_000 + 8_000_000, result.NetMm2);
        Assert.Equal(result.NetMm2, result.Floors.Sum(f => f.AreaMm2));
        Assert.Equal(result.NetMm2, result.Uses.Sum(u => u.AreaMm2));
        Assert.Equal(result.Rooms.Count, result.Floors.Sum(f => f.Count));
        Assert.Equal(result.Rooms.Count, result.Uses.Sum(u => u.Count));

        Assert.Equal(new[] { "0", "1" }, result.Floors.Select(f => f.Key));
        Assert.Equal(20_160_000 + 4_800_000, result.Floors[0].AreaMm2);
        Assert.Equal(12_400_000 + 8_000_000, result.Floors[1].AreaMm2);

        Assert.Equal("Stue", result.Uses[0].Key);
        Assert.Equal(20_160_000, result.Uses.Single(u => u.Key == "Stue").AreaMm2);
        Assert.Equal(12_400_000, result.Uses.Single(u => u.Key == "Soverom").AreaMm2);
        Assert.Equal(4_800_000, result.Uses.Single(u => u.Key == "Bad").AreaMm2);
        Assert.Equal(8_000_000, result.Uses.Single(u => u.Key == "Annet").AreaMm2);

        Assert.Equal(new[] { "Stue", "Bedroom", "Kontor", "Bad" }, result.Rooms.Select(r => r.Name));
        Assert.Equal("45,4 m²", OpeningTypes.AreaText(result.NetMm2));
    }

    [Fact]
    public void Rounding_MatchesAreaText_OfTheSum_NotTheSumOfTheLines()
    {
        // 1.45 m² prints as 1,5. Two of them are 2.9 m², which prints as 2,9, not 3,0.
        var result = AreaStats.Compute(new[]
        {
            Room("a", "Bod", "0", 1_450_000),
            Room("b", "Bod 2", "0", 1_450_000)
        });
        Assert.Equal("1,5 m²", OpeningTypes.AreaText(1_450_000));
        Assert.Equal("2,9 m²", OpeningTypes.AreaText(result.NetMm2));
        Assert.Equal("2,9 m²", OpeningTypes.AreaText(result.NetMm2));
        Assert.Contains("Net 2,9 m², estimate.", result.Summary);
        Assert.Contains("Bod 1,5 m²", result.Summary);
        Assert.Equal(result.NetMm2, result.Rooms.Sum(r => r.AreaMm2));
    }

    [Theory]
    [InlineData("Soverom", "Soverom")]
    [InlineData("soverom 2", "Soverom")]
    [InlineData("Bedroom", "Soverom")]
    [InlineData("Master bedroom", "Soverom")]
    [InlineData("Bad", "Bad")]
    [InlineData("WC", "Bad")]
    [InlineData("baderom", "Bad")]
    [InlineData("Kjøkken", "Kjøkken")]
    [InlineData("kitchen", "Kjøkken")]
    [InlineData("Stue", "Stue")]
    [InlineData("Living room", "Stue")]
    [InlineData("Bod", "Bod")]
    [InlineData("storage", "Bod")]
    [InlineData("Gang", "Gang")]
    [InlineData("Entré", "Gang")]
    [InlineData("Hall", "Gang")]
    [InlineData("hallway", "Gang")]
    [InlineData("Rom", "Annet")]
    [InlineData("Room", "Annet")]
    [InlineData("", "Annet")]
    [InlineData("Gjesterom", "Annet")]
    [InlineData("Kontor", "Annet")]
    [InlineData("Stue/kjøkken", "Stue")]
    public void UseOf_MapsNorwegianAndEnglish(string name, string use)
    {
        Assert.Equal(use, AreaStats.UseOf(name));
        var result = AreaStats.Compute(new[] { Room("rd-01", name, "0", 2_000_000) });
        Assert.Equal(use, result.Rooms[0].Use);
    }

    [Fact]
    public void ASetUse_WinsOverTheName()
    {
        var result = AreaStats.Compute(new[] { Room("rd-01", "Stue", "0", 2_000_000, "Bod") });
        Assert.Equal("Bod", result.Rooms[0].Use);
        Assert.Equal("Bod", result.Uses.Single().Key);
    }

    [Fact]
    public void AnEmptyLevel_IsFloor0()
    {
        var result = AreaStats.Compute(new[] { Room("rd-01", "Stue", null, 2_000_000) });
        Assert.Equal("0", result.Rooms[0].Level);
        Assert.Equal("0", result.Floors.Single().Key);
    }

    [Fact]
    public void Empty_HasNoTotals_AndOffersMakeRooms()
    {
        foreach (var rooms in new[] { null, new AreaStats.Room[0] })
        {
            var result = AreaStats.Compute(rooms);
            Assert.Empty(result.Rooms);
            Assert.Empty(result.Floors);
            Assert.Empty(result.Uses);
            Assert.Equal(0, result.NetMm2);
            Assert.Equal("No rooms. Make rooms finds them from the walls.", result.Summary);
        }
    }

    [Fact]
    public void Summary_LeadsWithTheTotals_ThenEightRooms_LargestFirst()
    {
        var rooms = new List<AreaStats.Room>();
        for (var i = 1; i <= 9; i++)
            rooms.Add(Room("rd-" + i.ToString("00"), "Rom " + i, "0", i * 1_000_000));
        var result = AreaStats.Compute(rooms);
        Assert.StartsWith("Net 45,0 m², estimate. Floor 0: 45,0 m². By use: Annet 45,0 m². ", result.Summary);
        Assert.Contains("Rom 9 9,0 m²", result.Summary);
        Assert.DoesNotContain("Rom 1 ", result.Summary);
        Assert.EndsWith(", +1 more.", result.Summary);
        Assert.Equal(8, AreaStats.SummaryRooms);
    }

    static AreaStats.Room Room(string id, string name, string level, double areaMm2, string use = null)
    {
        return new AreaStats.Room { Id = id, Name = name, Level = level, AreaMm2 = areaMm2, Use = use };
    }
}
