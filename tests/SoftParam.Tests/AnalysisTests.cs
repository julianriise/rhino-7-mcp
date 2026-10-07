using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// AN.1 and AN.2: each analysis's Live switch and last result on the file,
/// and the one line a live edit shows.
/// </summary>
public class AnalysisTests
{
    static Analysis.Room R(string id, string name, double m2) => new Analysis.Room { Id = id, Name = name, AreaM2 = m2 };

    [Fact]
    public void AFileWithNothingStored_HasAreasLive_AndDaylightOff()
    {
        var state = Analysis.Read(_ => null);
        Assert.True(state.IsLive(Analysis.Areas));
        Assert.False(state.IsLive(Analysis.Daylight));
        Assert.True(state.AnyLive);
        Assert.Empty(state.Last);
    }

    [Fact]
    public void TheStoredSwitches_LastResults_AndRoomMeans_ReadBack()
    {
        var stored = new Dictionary<string, string>
        {
            ["live.areas"] = "0",
            ["live.daylight"] = "1",
            ["last.daylight"] = Analysis.DaylightLast(2.14, 8),
            [Analysis.RoomsKey] = Analysis.WriteRoomDf(new Dictionary<string, double> { ["R02"] = 2.3, ["R01"] = 1.8 })
        };
        var state = Analysis.Read(k => stored.TryGetValue(k, out var v) ? v : null);
        Assert.False(state.IsLive(Analysis.Areas));
        Assert.True(state.IsLive(Analysis.Daylight));
        Assert.Equal("DF mean 2.1 % in 8 rooms", state.Last[Analysis.Daylight]);
        Assert.Equal(1.8, state.RoomDf["R01"]);
        Assert.Equal(2.3, state.RoomDf["R02"]);
        Assert.Equal("R01=1.8;R02=2.3", stored[Analysis.RoomsKey]);
        Assert.Equal("142.3 m² in 1 room", Analysis.AreasLast(142.26, 1));
    }

    [Fact]
    public void TheLiveLine_NamesTheRoomThatChanged_WithAreaAndDaylight()
    {
        var before = new[] { R("R01", "Living", 30), R("R02", "Bedroom 2", 11.2) };
        var after = new[] { R("R01", "Living", 30.02), R("R02", "Bedroom 2", 12.6) };
        var line = Analysis.LiveLine(before, after,
            new Dictionary<string, double> { ["R01"] = 2.0, ["R02"] = 1.8 },
            new Dictionary<string, double> { ["R01"] = 2.01, ["R02"] = 2.3 });
        Assert.Equal("Bedroom 2: +1.4 m², daylight 1.8 → 2.3 %", line);
    }

    [Fact]
    public void ANewRoom_AGoneRoom_AndNothingChanged()
    {
        var before = new[] { R("R01", "Living", 30), R("R02", "Store", 3) };
        var after = new[] { R("R01", "Living", 27.5), R("R03", "Hall", 4.2) };
        Assert.Equal("Hall: new, 4.2 m²; Store: gone; Living: −2.5 m²", Analysis.LiveLine(before, after, null, null));
        Assert.Null(Analysis.LiveLine(before, before, null, null));
    }

    [Fact]
    public void ManyRooms_ShowTheThreeBiggestChanges_AndCountTheRest()
    {
        var before = Enumerable.Range(1, 6).Select(i => R("R0" + i, "Room " + i, 10)).ToList();
        var after = Enumerable.Range(1, 6).Select(i => R("R0" + i, "Room " + i, 10 + i)).ToList();
        Assert.Equal("Room 6: +6.0 m²; Room 5: +5.0 m²; Room 4: +4.0 m²; and 3 more", Analysis.LiveLine(before, after, null, null));
    }
}
