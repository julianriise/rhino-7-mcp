using RhinoMCPPlugin.Forsk;
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

    [Fact]
    public void Envelope_OfARectangle_IsTheOuterFace_AndTheInnerFace()
    {
        // 10 m by 8 m outside, walls 200 mm. BTA is the outer face. BRA is inset by the thickness.
        var ring = Rect(0, 0, 10000, 8000);
        Assert.True(AreaStats.TryEnvelope(ring, 200, out var bta, out var bra, out var reason), reason);
        Assert.Equal(80_000_000, bta, 1);
        Assert.Equal(9_600.0 * 7_600.0, bra, 1);
        Assert.Equal("80,0 m²", OpeningTypes.AreaText(bta));
        Assert.Equal("73,0 m²", OpeningTypes.AreaText(bra));

        // The same ring walked clockwise, or closed by repeating the first point, is the same pair.
        ring.Reverse();
        Assert.True(AreaStats.TryEnvelope(ring, 200, out var btaCw, out var braCw, out reason), reason);
        Assert.Equal(bta, btaCw, 1);
        Assert.Equal(bra, braCw, 1);
        ring.Add(ring[0]);
        Assert.True(AreaStats.TryEnvelope(ring, 200, out var btaClosed, out var braClosed, out reason), reason);
        Assert.Equal(bta, btaClosed, 1);
        Assert.Equal(bra, braClosed, 1);
    }

    [Fact]
    public void Envelope_OfAnL_InsetsTheOuterFace_IncludingTheNotch()
    {
        var ring = new List<RoomDetect.Pt>
        {
            new RoomDetect.Pt(0, 0),
            new RoomDetect.Pt(10000, 0),
            new RoomDetect.Pt(10000, 4000),
            new RoomDetect.Pt(6000, 4000),
            new RoomDetect.Pt(6000, 8000),
            new RoomDetect.Pt(0, 8000)
        };
        Assert.True(AreaStats.TryEnvelope(ring, 200, out var bta, out var bra, out var reason), reason);
        Assert.Equal(64_000_000, bta, 1);
        Assert.Equal(56_960_000, bra, 1);
        Assert.Equal("64,0 m²", OpeningTypes.AreaText(bta));
        Assert.Equal("57,0 m²", OpeningTypes.AreaText(bra));
    }

    [Fact]
    public void Envelope_LeavesTheFigureOut_WhenItCannotBeDerived()
    {
        var ring = Rect(0, 0, 10000, 8000);
        Assert.False(AreaStats.TryEnvelope(ring, 0, out _, out _, out var reason));
        Assert.Equal("wall thickness is missing", reason);
        Assert.False(AreaStats.TryEnvelope(null, 200, out _, out _, out reason));
        Assert.Equal("the wall outline is missing", reason);
        Assert.False(AreaStats.TryEnvelope(ring, 5000, out _, out _, out reason));
        Assert.Equal("the walls are thicker than the outline", reason);
    }

    [Fact]
    public void Gross_SumsSeparateOutlines_AndOmitsAFloorItCannotRead()
    {
        var result = AreaStats.Compute(new[]
        {
            Room("a", "Stue", "0", 10_000_000),
            Room("b", "Soverom", "1", 8_000_000)
        });
        AreaStats.ApplyGross(result, new[]
        {
            Wall("0", 200, Rect(0, 0, 10000, 8000)),
            Wall("0", 200, Rect(20000, 0, 24000, 3000))
        }, 1);
        var ground = result.Gross.Single(g => g.Level == "0");
        Assert.Null(ground.Note);
        Assert.Equal(80_000_000 + 12_000_000, ground.BtaMm2.Value, 1);
        Assert.Equal(9_600.0 * 7_600.0 + 3_600.0 * 2_600.0, ground.BraMm2.Value, 1);
        Assert.Equal("no wall outline on this floor", result.Gross.Single(g => g.Level == "1").Note);
        Assert.Contains("BRA 82,3 m², BTA 92,0 m²", result.Summary);
        Assert.Contains("Floor 1: 8,0 m² (no wall outline on this floor)", result.Summary);

        var mixed = AreaStats.Compute(new[] { Room("a", "Stue", "0", 10_000_000) });
        AreaStats.ApplyGross(mixed, new[]
        {
            Wall("0", 200, Rect(0, 0, 10000, 8000)),
            Wall("0", 300, Rect(0, 0, 10000, 8000))
        }, 1);
        Assert.Equal("the outer walls do not share one thickness", mixed.Gross.Single().Note);
        Assert.Null(mixed.Gross.Single().BraMm2);
        Assert.DoesNotContain("BRA", mixed.Summary);

        var json = AreaStats.ToJson(result);
        Assert.Equal(result.Summary, json["message"]!.ToString());
        Assert.Equal("82,3 m²", json["floors"]![0]!["bra"]!.ToString());
        Assert.Equal("92,0 m²", json["floors"]![0]!["bta"]!.ToString());
        Assert.Equal("no wall outline on this floor", json["floors"]![1]!["note"]!.ToString());
        var text = json.ToString();
        Assert.DoesNotContain("\"id\"", text);
        Assert.DoesNotContain("\"x\"", text);
        Assert.DoesNotContain("\"y\"", text);
    }

    [Fact]
    public void AreaStats_IsListed_WithADescriptionThatDoesNotGuess()
    {
        var tool = ForskToolPacks.Catalog["area_stats"];
        var description = tool["function"]!["description"]!.ToString();
        Assert.Contains("Romliste", description);
        Assert.Contains("BRA", description);
        Assert.Contains("rooms_detect", description);
        Assert.Contains("Read only", description);
    }

    static AreaStats.Wall Wall(string level, double thickness, List<RoomDetect.Pt> outer)
    {
        return new AreaStats.Wall
        {
            Level = level,
            ThicknessMm = thickness,
            Rings = new List<List<RoomDetect.Pt>> { outer }
        };
    }

    static List<RoomDetect.Pt> Rect(double x0, double y0, double x1, double y1)
    {
        return new List<RoomDetect.Pt>
        {
            new RoomDetect.Pt(x0, y0),
            new RoomDetect.Pt(x1, y0),
            new RoomDetect.Pt(x1, y1),
            new RoomDetect.Pt(x0, y1)
        };
    }

    [Theory]
    [InlineData("how big is the flat?", ForskIntent.Area)]
    [InlineData("areal per etasje", ForskIntent.Area)]
    [InlineData("BRA?", ForskIntent.Area)]
    [InlineData("hvor stor er leiligheten", ForskIntent.Area)]
    [InlineData("what is the BTA", ForskIntent.Area)]
    [InlineData("bruksareal", ForskIntent.Area)]
    [InlineData("kvm", ForskIntent.Area)]
    [InlineData("the flat is 64 m²", ForskIntent.Area)]
    [InlineData("kvadratmeter", ForskIntent.Area)]
    [InlineData("print the room schedule", ForskIntent.Print)]
    [InlineData("romliste", ForskIntent.Sheets)]
    [InlineData("run daylight", ForskIntent.Daylight)]
    [InlineData("how big is the daylight", ForskIntent.Daylight)]
    [InlineData("the area is wrong", ForskIntent.Support)]
    [InlineData("problem med arealene", ForskIntent.Support)]
    [InlineData("how do I find the area", ForskIntent.Support)]
    [InlineData("det ser bra ut", ForskIntent.General)]
    [InlineData("add a door schedule", ForskIntent.Sheets)]
    public void Router_SendsAreaQuestionsToAnalyser_AndLeavesTheOthers(string said, ForskIntent intent)
    {
        Assert.Equal(intent, ForskIntentRouter.Classify(said));
        if (intent == ForskIntent.Area)
            Assert.Equal("Analyser", ForskRoles.Mark(intent, ForskRole.None));
    }

    [Fact]
    public void AreaBias_AsksForTotalsFirst_AndNotForAGuess()
    {
        var bias = ForskArea.Bias;
        Assert.Contains("Turn bias: Area.", bias);
        Assert.Contains("at most 8 rooms", bias);
        Assert.Contains("No coordinates", bias);
        Assert.Contains("rooms_detect", bias);
        Assert.Contains("Do not invent a figure", bias);
    }

    [Theory]
    [MemberData(nameof(Docs.Names), MemberType = typeof(Docs))]
    public void AreaStats_IsNeverSlot1(string name)
    {
        var bar = ForskRegistry.Bar(Docs.Facts(name));
        Assert.NotEqual("area.stats", bar.Slot1.Id);
        Assert.DoesNotContain(bar.Slots, a => a.Id == "area.stats");
    }

    static AreaStats.Room Room(string id, string name, string level, double areaMm2, string use = null)
    {
        return new AreaStats.Room { Id = id, Name = name, Level = level, AreaMm2 = areaMm2, Use = use };
    }
}
