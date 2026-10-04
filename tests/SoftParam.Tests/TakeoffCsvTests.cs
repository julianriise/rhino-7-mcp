using System.Globalization;
using System.Text;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// v3 N2: the takeoff as a CSV on the smoke garage (DetailFixtures.SmokeGarage):
/// the project info, then rooms, walls, doors, windows and the stair, each
/// figure the takeoff's own. The suite runs under nb-NO, so a bare ToString
/// would write 12,40.
/// </summary>
public class TakeoffCsvTests
{
    const double Tol = 1.0;
    static readonly DateTime Exported = new(2026, 10, 4, 21, 30, 0);

    public static List<Takeoff.Wall> Walls() => DetailFixtures.SmokeGarage().Walls
        .Select(w => new Takeoff.Wall { Id = w.Id, Level = "0", Rings = w.Rings, ThicknessMm = w.Thickness, HeightMm = w.Height }).ToList();

    public static List<Schedules.Opening> Openings() => DetailFixtures.SmokeGarage().Openings.Select(o =>
    {
        Assert.True(OpeningTypes.TryRead(o.Kind, null, null, null, out var record, out _));
        return new Schedules.Opening
        {
            Id = o.Id, Mark = o.Mark, Record = record, X = o.Centre.X, Y = o.Centre.Y, Width = o.Width, Sill = o.Sill, Head = o.Head, HostId = o.Host
        };
    }).ToList();

    static readonly List<Pt> GarageRing = new() { new(200, 200), new(7800, 200), new(7800, 3800), new(200, 3800) };

    static AreaStats.Result Rooms() => AreaStats.Compute(new List<AreaStats.Room>
    {
        new() { Id = "rd-01", Name = "Garasje og bod", Level = "0", AreaMm2 = Math.Abs(RoomDetect.Area(GarageRing)), PerimeterMm = RoomDetect.Perimeter(GarageRing) },
        new() { Id = "rd-02", Name = "Bod, \"lille\"", Level = "0", AreaMm2 = 2.4e6, PerimeterMm = 6400 }
    });

    static List<TakeoffCsv.Stair> StairList() => new() { new() { Id = "st01", Flight = Stairs.Plan(2880, 180, 260, 900) } };

    static string Csv(ProjectInfo.Record info = null) => TakeoffCsv.Write(info ?? ProjectInfoTests.Read(ProjectInfoTests.Smoke), Rooms().Rooms,
        Takeoff.Runs(Walls(), Openings(), Tol), Openings(), StairList(), Exported);

    static string[] Lines(string csv) => csv.Split("\r\n");

    static string[] Table(string csv) => Lines(csv).SkipWhile(l => l.Length > 0).Skip(1).Where(l => l.Length > 0).ToArray();

    /// <summary>A row's fields by column name (no quoted commas in the rows this reads).</summary>
    static Dictionary<string, string> Fields(string line) =>
        TakeoffCsv.Columns.Zip(line.Split(','), (c, v) => (c, v)).ToDictionary(p => p.c, p => p.v);

    /// <summary>
    /// Today's card rows and figures, pinned before Takeoff.Runs went public:
    /// the CSV's walls come from the same runs, and the card must not move.
    /// </summary>
    [Fact]
    public void TheTakeoffCardRows_AreByteIdenticalToBefore()
    {
        string Pinned(Takeoff.Line l) => Takeoff.Row(l) + " | " + string.Join(" ",
            new[] { l.LengthM, l.AreaM2, l.VolumeM3 }.Select(v => v?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "-"));
        var garage = Takeoff.Compute(Walls(), Openings(), new[] { new Takeoff.Slab { AreaMm2 = 32e6, ThicknessMm = 200 } },
            new[] { new Takeoff.Slab { AreaMm2 = 45e6, ThicknessMm = 200 } }, null, Tol, stairs: new[] { Stairs.Plan(2880, 180, 260, 900) });
        Assert.Equal(new[]
        {
            "Exterior wall 200 mm · 23,2 m · 61,1 m² · 12,2 m³ | 23.200000 61.080000 12.216000",
            "Slab 200 mm · 32,0 m² · 6,4 m³ | - 32.000000 6.400000",
            "Roof 200 mm · 45,0 m² · 9,0 m³ | - 45.000000 9.000000",
            "Hinged door 900 × 2100 · 4 no. · 7,6 m² | - 7.560000 -",
            "Side-hung window 1200 × 800 · 1 no. · 1,0 m² | - 0.960000 -",
            "Straight stair 16 × 180/260, 900 wide · 1 no. | - - -"
        }, garage.Lines.Select(Pinned));
        var walls = new List<Takeoff.Wall>
        {
            new() { Rings = WallJoinsTests.TwoRooms(), ThicknessMm = 200, HeightMm = 3000 },
            new() { Rings = new List<List<Pt>> { WallJoinsTests.Rect(20000, 0, 24000, 200) }, ThicknessMm = 200, HeightMm = 2400, Existing = true }
        };
        Assert.Equal(new[]
        {
            "Exterior wall 200 mm · 23,2 m · 69,6 m² · 13,9 m³ | 23.200000 69.600000 13.920000",
            "Interior wall 200 mm · 3,6 m · 10,8 m² · 2,2 m³ | 3.600000 10.800000 2.160000",
            "Existing exterior wall 200 mm · 4,0 m · 9,6 m² · 1,9 m³ | 4.000000 9.600000 1.920000"
        }, Takeoff.Compute(walls, null, null, null, null, Tol).Lines.Select(Pinned));
    }

    [Fact]
    public void TheFile_StartsWithTheBom_AndEndsLinesWithCrLf()
    {
        var csv = Csv();
        var bytes = TakeoffCsv.Bytes(csv);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal(csv, new UTF8Encoding(false).GetString(bytes.Skip(3).ToArray()));
        Assert.EndsWith("\r\n", csv);
        Assert.DoesNotContain("\n", csv.Replace("\r\n", ""));
        Assert.Contains("Garasje og bod", csv);
    }

    [Fact]
    public void TheHeaderBlock_HoldsTheStoredProjectInfo()
    {
        var lines = Lines(Csv());
        Assert.Equal(new[]
        {
            "Project,Garage",
            "Project no.,2026-07",
            "Client,Ola Nordmann",
            "Address,\"Storgata 1, 0150 Oslo\"",
            "Architect,Riise Arkitekter",
            "Date,2026-10-04",
            "Rev.,B",
            "Exported,2026-10-04 21:30",
            "Source,Forsk takeoff (quantities are approximate)",
            ""
        }, lines.Take(10));
        Assert.Equal(new[] { "Project,Garage", "Exported,2026-10-04 21:30" },
            Lines(Csv(ProjectInfoTests.Read(new() { ["project"] = "Garage" }))).Take(2));
    }

    [Fact]
    public void TheTableHeader_IsExact()
    {
        Assert.Equal("Kind,Id,Name,Type,Level,Wall,Length (m),Perimeter (m),Area (m²),Height (mm),Thickness (mm),Width (mm),Sill (mm),Risers,Riser (mm),Going (mm)",
            Table(Csv())[0]);
    }

    [Fact]
    public void TheRowKinds_ComeInOrder()
    {
        var kinds = Table(Csv()).Skip(1).Select(l => l.Split(',')[0]).ToList();
        Assert.Equal(new[] { "Room", "Room", "Wall", "Wall", "Wall", "Wall", "Door", "Door", "Door", "Door", "Window", "Stair" }, kinds);
    }

    [Fact]
    public void ARoom_HasAreaStatsArea_AndItsOutlinesPerimeter()
    {
        var room = Fields(Table(Csv()).Single(l => l.StartsWith("Room,rd-01,")));
        var line = Rooms().Rooms.Single(r => r.Id == "rd-01");
        Assert.Equal((line.AreaMm2 / 1e6).ToString("0.00", CultureInfo.InvariantCulture), room["Area (m²)"]);
        Assert.Equal("27.36", room["Area (m²)"]);
        Assert.Equal("22.40", room["Perimeter (m)"]);
        Assert.Equal("0", room["Level"]);
        Assert.Equal("", room["Thickness (mm)"]);
    }

    [Fact]
    public void AWall_HasTheRunsLengthHeightThicknessAndArea()
    {
        var runs = Takeoff.Runs(Walls(), Openings(), Tol);
        var rows = Table(Csv()).Where(l => l.StartsWith("Wall,")).Select(Fields).ToList();
        Assert.Equal(runs.Count, rows.Count);
        for (var i = 0; i < runs.Count; i++)
        {
            Assert.Equal(runs[i].Id, rows[i]["Id"]);
            Assert.Equal("Exterior", rows[i]["Type"]);
            Assert.Equal((runs[i].Length / 1000).ToString("0.00", CultureInfo.InvariantCulture), rows[i]["Length (m)"]);
            Assert.Equal(runs[i].Height.ToString("0", CultureInfo.InvariantCulture), rows[i]["Height (mm)"]);
            Assert.Equal(runs[i].Thickness.ToString("0", CultureInfo.InvariantCulture), rows[i]["Thickness (mm)"]);
            Assert.Equal((runs[i].Area / 1e6).ToString("0.00", CultureInfo.InvariantCulture), rows[i]["Area (m²)"]);
        }
        // The runs' sums are the card's line.
        Assert.Equal(23.2, runs.Sum(r => r.Length) / 1000, 6);
        Assert.Equal(61.08, runs.Sum(r => r.Area) / 1e6, 6);
        // The south run is the mean of its faces, 7.8 m, less its five openings.
        var south = rows.Single(r => r["Id"] == "w01");
        Assert.Equal("7.80", south["Length (m)"]);
        Assert.Equal((7.8 * 3.0 - 4 * 0.9 * 2.1 - 1.2 * 0.8).ToString("0.00", CultureInfo.InvariantCulture), south["Area (m²)"]);
    }

    [Fact]
    public void ARecordWithMoreThanOneRun_NumbersThem_AndAnExistingWallKeepsItsId()
    {
        var walls = new List<Takeoff.Wall>
        {
            new() { Id = "w03", Rings = WallJoinsTests.Garage(), ThicknessMm = 200, HeightMm = 3000 },
            new() { Id = "x01", Rings = new List<List<Pt>> { WallJoinsTests.Rect(20000, 0, 24000, 200) }, ThicknessMm = 200, HeightMm = 2400, Existing = true }
        };
        var runs = Takeoff.Runs(walls, null, Tol);
        Assert.Equal(new[] { "w03.1", "w03.2", "w03.3", "w03.4", "x01" }, runs.Select(r => r.Id));
        Assert.Equal("Existing exterior", TakeoffCsv.WallType(runs.Last()));
        Assert.Equal(2400, runs.Last().Height);
    }

    [Fact]
    public void ADoor_HasItsHostWall_AndHeightIsHeadLessSill()
    {
        var table = Table(Csv());
        var door = Fields(table.Single(l => l.StartsWith("Door,D01,")));
        Assert.Equal("w01", door["Wall"]);
        Assert.Equal("Hinged door", door["Type"]);
        Assert.Equal("900", door["Width (mm)"]);
        Assert.Equal("2100", door["Height (mm)"]);
        Assert.Equal("0", door["Sill (mm)"]);
        var window = Fields(table.Single(l => l.StartsWith("Window,W01,")));
        Assert.Equal("800", window["Height (mm)"]);
        Assert.Equal("1300", window["Sill (mm)"]);
    }

    [Fact]
    public void TheStair_HasRisersRiserAndGoing()
    {
        var stair = Fields(Table(Csv()).Single(l => l.StartsWith("Stair,")));
        Assert.Equal("st01", stair["Id"]);
        Assert.Equal("straight", stair["Type"]);
        Assert.Equal("900", stair["Width (mm)"]);
        Assert.Equal("16", stair["Risers"]);
        Assert.Equal("180", stair["Riser (mm)"]);
        Assert.Equal("260", stair["Going (mm)"]);
        Assert.Equal("3.90", stair["Length (m)"]);
    }

    [Fact]
    public void ANameWithACommaAndAQuote_IsQuoted()
    {
        Assert.Contains("Room,rd-02,\"Bod, \"\"lille\"\"\",,0,", Csv());
        Assert.Equal("plain", TakeoffCsv.Quote("plain"));
        Assert.Equal("\"two\r\nlines\"", TakeoffCsv.Quote("two\r\nlines"));
    }

    [Fact]
    public void UnderNbNo_EveryNumberHasADecimalPoint()
    {
        Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
        var table = Table(Csv()).Skip(1);
        Assert.DoesNotMatch(@"\d,\d\d(,|$)", string.Join("\n", table.Select(l => l.Replace("\"Bod, \"\"lille\"\"\"", "Bod"))));
        Assert.Contains(table, l => System.Text.RegularExpressions.Regex.IsMatch(l, @",\d+\.\d\d,"));
    }

    [Fact]
    public void TheReceipt_CountsWhatWasWritten()
    {
        Assert.Equal("✓ Exported takeoff CSV · 6 rooms, 14 walls, 5 doors and windows, 1 stair · Garage Takeoff.csv",
            TakeoffCsv.Receipt(6, 14, 5, 1, "/Users/jr/Desktop/Garage Takeoff.csv"));
        Assert.Equal("Garage Takeoff.csv", TakeoffCsv.FileName("Garage"));
        Assert.Equal("Forsk Takeoff.csv", TakeoffCsv.FileName(" "));
    }
}
