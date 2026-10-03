using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// R2: a dimension the user asked for, stored by Forsk ids and resolved at
/// every Print from the live walls and openings. The garage is 8 × 4 m
/// outside, 200 mm walls: south w01, north w02, west w03, east w04.
/// </summary>
public class UserDimsTests
{
    const double Tol = 1.0;

    static List<List<Pt>> Box(double x0, double y0, double x1, double y1) =>
        new() { new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) } };

    static Dictionary<string, List<List<Pt>>> Garage(double northBy = 0) => new()
    {
        ["w01"] = Box(0, 0, 8000, 200),
        ["w02"] = Box(0, 3800 + northBy, 8000, 4000 + northBy),
        ["w03"] = Box(0, 200, 200, 3800 + northBy),
        ["w04"] = Box(7800, 200, 8000, 3800 + northBy),
    };

    static UserDims.Dim Across(params string[] ids) => new()
    {
        Id = "U01",
        Refs = ids.Select(id => id.StartsWith("w") ? new UserDims.Ref { Wall = id } : new UserDims.Ref { Opening = id }).ToList()
    };

    static List<int> Values(PlanDims.UserChain chain) =>
        chain.Stops.Zip(chain.Stops.Skip(1), (a, b) => (int)Math.Round(b - a)).ToList();

    [Fact]
    public void TheRecord_ReadsBackAsWritten_AndIdsCountUp()
    {
        var dims = new List<UserDims.Dim> { Across("w01", "w02", "o-7"), new() { Id = "U03", Refs = { new UserDims.Ref { Wall = "w03" } } } };
        var json = UserDims.Write(dims);
        Assert.Equal("[{\"id\":\"U01\",\"refs\":[{\"wall\":\"w01\"},{\"wall\":\"w02\"},{\"opening\":\"o-7\"}]},{\"id\":\"U03\",\"refs\":[{\"wall\":\"w03\"}]}]", json);
        var back = UserDims.Read(json);
        Assert.Equal(new[] { "U01", "U03" }, back.Select(d => d.Id));
        Assert.Equal("o-7", back[0].Refs[2].Opening);
        Assert.Equal("U04", UserDims.NextId(back));
        Assert.Equal("U01", UserDims.NextId(new List<UserDims.Dim>()));
        Assert.Empty(UserDims.Read("not json"));
        Assert.Empty(UserDims.Read(null));
    }

    [Fact]
    public void Walls_MustBeParallel()
    {
        var g = Garage();
        Assert.Null(UserDims.Check(new[] { g["w01"], g["w02"] }, Tol));
        Assert.Equal("Pick parallel walls to dimension across them.", UserDims.Check(new[] { g["w01"], g["w03"] }, Tol));
        Assert.Equal("Pick parallel walls to dimension across them.", UserDims.Check(new List<List<List<Pt>>>(), Tol));
        // 10° off is still one direction (the F2 15°); 20° is not.
        Assert.Null(UserDims.Check(new[] { g["w01"], Turned(10) }, Tol));
        Assert.NotNull(UserDims.Check(new[] { g["w01"], Turned(20) }, Tol));
    }

    /// <summary>An 8000 × 200 wall from (0, 3800), turned by degrees.</summary>
    static List<List<Pt>> Turned(double degrees)
    {
        var a = degrees * Math.PI / 180;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        Pt At(double x, double y) => new(x * c - y * s, 3800 + x * s + y * c);
        return new() { new() { At(0, 0), At(8000, 0), At(8000, 200), At(0, 200) } };
    }

    [Fact]
    public void TheChain_StopsAtBothFacesOfEachWall()
    {
        var chain = UserDims.Resolve(Across("w01", "w02"), Garage(), null, Tol, out var gone);
        Assert.NotNull(chain);
        Assert.Equal(0, gone.Walls);
        Assert.Equal(new[] { 200, 3600, 200 }, Values(chain));
        Assert.Equal(new[] { 0.0, 200, 3800, 4000 }, chain.Stops.Select(t => Math.Round(t)));
        // It runs across the walls (north), and sits at the middle of their shared span.
        Assert.Equal(1, Math.Abs(chain.Dir.Y), 9);
        Assert.Equal(4000, chain.Origin.X, 6);
        Assert.Equal(4000, chain.Reach, 6);
    }

    [Fact]
    public void AnOpening_OnAHostAlongTheChain_AddsItsCentre_OnACrossingHost_IsSkipped()
    {
        var openings = new Dictionary<string, UserDims.OpeningAt>
        {
            // In the east wall, which runs north like the chain.
            ["o-east"] = new() { Centre = new Pt(7900, 1500), Along = new Pt(0, 1) },
            // In the south wall, square to the chain.
            ["o-south"] = new() { Centre = new Pt(3000, 100), Along = new Pt(1, 0) },
        };
        var chain = UserDims.Resolve(Across("w01", "w02", "o-east", "o-south"), Garage(), openings, Tol, out var gone);
        Assert.Equal(new[] { 200, 1300, 2300, 200 }, Values(chain));
        Assert.Contains("o-east", chain.StopIds);
        Assert.DoesNotContain("o-south", chain.StopIds);
        Assert.Equal(1, gone.Skipped);
    }

    [Fact]
    public void AWallThatMoves_CarriesItsDimension()
    {
        var before = UserDims.Resolve(Across("w01", "w02"), Garage(), null, Tol, out _);
        var after = UserDims.Resolve(Across("w01", "w02"), Garage(northBy: 500), null, Tol, out _);
        Assert.Equal(3600, Values(before)[1]);
        Assert.Equal(4100, Values(after)[1]);
    }

    [Fact]
    public void ADeletedWall_DropsItsStops_ThenTheDimension()
    {
        var three = Across("w01", "w02", "w03");
        three.Refs[2] = new UserDims.Ref { Wall = "w05" };
        var walls = Garage();
        var chain = UserDims.Resolve(three, walls, null, Tol, out var gone);
        Assert.NotNull(chain);
        Assert.Equal(1, gone.Walls);

        walls.Remove("w02");
        var lone = UserDims.Resolve(Across("w01", "w02"), walls, null, Tol, out gone);
        // One wall left still has two faces: the dimension is its thickness alone, which says nothing. It drops.
        Assert.Null(lone);
        Assert.Equal(1, gone.Walls);
        Assert.True(gone.Dropped);
    }

    [Fact]
    public void TheDropNote_SaysWhy()
    {
        Assert.Equal("", UserDims.DroppedLine(0));
        Assert.Equal("1 dimension dropped: a wall it measured is gone.", UserDims.DroppedLine(1));
        Assert.Equal("2 dimensions dropped: a wall they measured is gone.", UserDims.DroppedLine(2));
    }
}
