using System.Collections.Generic;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// The floor and flat roof that belong to a wall, and the sentence that says so.
/// </summary>
public class WallFollowPlanTests
{
    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new List<Pt> { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };

    [Fact]
    public void Sentence_NamesWhatFollowed_AndAStaleDaylightMap()
    {
        Assert.Equal("Floor, roof and 2 rooms updated.", WallFollowPlan.Sentence(true, true, 2, false));
        Assert.Equal(
            "Floor, roof and 2 rooms updated. Daylight is out of date, run it again.",
            WallFollowPlan.Sentence(true, true, 2, true));
        Assert.Equal("1 room updated.", WallFollowPlan.Sentence(false, false, 1, false));
        Assert.Equal("Roof and 2 rooms updated.", WallFollowPlan.Sentence(false, true, 2, false));
        Assert.Equal("Floor and roof updated.", WallFollowPlan.Sentence(true, true, 0, false));
        Assert.Equal("Floor, roof and rooms are unchanged.", WallFollowPlan.Sentence(false, false, 0, false));
        Assert.Equal(
            "Floor, roof and rooms are unchanged. Daylight is out of date, run it again.",
            WallFollowPlan.Sentence(false, false, 0, true));
    }

    [Fact]
    public void Outline_MatchesTheWall_ARoofSitsItsOverhangOutside_AFreeWallDoesNot()
    {
        var wall = Rect(0, 0, 8000, 4000);
        var same = Rect(0, 0, 8000, 4000);
        var roof = Rect(-500, -500, 8500, 4500);
        var free = Rect(2000, 6000, 6000, 6400);
        Assert.True(WallFollowPlan.SameOutline(wall, same, 5));
        Assert.False(WallFollowPlan.SameOutline(wall, free, 5));
        Assert.False(WallFollowPlan.SameOutline(free, wall, 5));
        Assert.True(WallFollowPlan.Covers(wall, roof, 500, 5));
        Assert.False(WallFollowPlan.Covers(wall, same, 500, 5));
        Assert.False(WallFollowPlan.Covers(free, roof, 500, 5));
        Assert.True(WallFollowPlan.Covers(wall, same, 0, 5));
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("the north wall", new[] { "the north wall" })]
    [InlineData("the east and west walls", new[] { "the east wall", "the west wall" })]
    [InlineData("the east and west walls and one inner wall", new[] { "the east wall", "the wall at (4000, 2000)", "the west wall" })]
    [InlineData("the north, east and west walls", new[] { "the north wall", "the east wall", "the west wall", "the east wall" })]
    public void Walls_NamesWhatFollowed_InOnePhrase(string phrase, string[] names)
    {
        Assert.Equal(phrase, WallFollowPlan.Walls(names));
        Assert.DoesNotContain("(", phrase);
    }

    [Fact]
    public void Walls_CountsInnerWalls_AndNeverPrintsCoordinates()
    {
        var names = new List<string> { "the west wall", "the east wall" };
        for (var i = 0; i < 10; i++) names.Add("the wall at (" + (1000 + i) + ", 2000)");
        Assert.Equal("the west and east walls and ten inner walls", WallFollowPlan.Walls(names));
        Assert.Equal("vest- og østveggen og ti innervegger", WallFollowPlan.Walls(names, true));
        Assert.Equal("vest- og østveggen og ti innervegger fulgte", WallFollowPlan.FollowedClause(names, true));
        Assert.Equal("one inner wall", WallFollowPlan.Walls(new[] { "the wall at (1, 2)" }));
        Assert.Equal("én innervegg", WallFollowPlan.Walls(new[] { "wall between Kitchen and Bedroom" }, true));
        Assert.DoesNotContain("11375", WallFollowPlan.Walls(new[] { "the wall at (11375, 21735)" }));
    }

    [Fact]
    public void InnerName_UsesTheRooms_ThenANumber()
    {
        Assert.Equal("wall between Kitchen and Bedroom", WallFollowPlan.InnerName(new[] { "Kitchen", "Bedroom" }, 3, false));
        Assert.Equal("vegg mellom Kitchen og Bedroom", WallFollowPlan.InnerName(new[] { "Kitchen", "Bedroom" }, 3, true));
        Assert.Equal("wall of Kitchen", WallFollowPlan.InnerName(new[] { "Kitchen" }, 3, false));
        Assert.Equal("inner wall 3", WallFollowPlan.InnerName(new string[0], 3, false));
        Assert.Equal("innervegg 3", WallFollowPlan.InnerName(null, 3, true));
    }

    [Fact]
    public void Beside_NamesTheRoomsOnEachFace_WestFirst()
    {
        var records = new List<List<List<Pt>>> { WallJoinsTests.TwoRooms() };
        var graph = WallJoins.Build(records, WallJoins.ClusterOf(records, 0, 1), 1);
        var partition = graph.Names.IndexOf("the wall at (4000, 2000)");
        Assert.True(partition >= 0);
        var rooms = new List<WallFollowPlan.NamedRoom>
        {
            new WallFollowPlan.NamedRoom { Name = "Kitchen", Ring = WallJoinsTests.Rect(200, 200, 3900, 3800) },
            new WallFollowPlan.NamedRoom { Name = "Bedroom", Ring = WallJoinsTests.Rect(4100, 200, 7800, 3800) }
        };
        Assert.Equal(new[] { "Kitchen", "Bedroom" }, WallFollowPlan.Beside(graph.Runs[partition], rooms));
        Assert.Equal(1, WallFollowPlan.InnerOrdinal(graph.Names, partition));
    }

    [Fact]
    public void AfterDelete_APartitionGone_LeavesTheBoxOutline_TheBoxGoneTakesItsSlabs()
    {
        // Julian 2026-10-09: a wall deleted with Rhino's Delete left its doors, floor and roof as they were.
        var records = new List<List<List<Pt>>> { WallJoinsTests.Garage(), new() { Rect(3900, 200, 4100, 3800) } };
        var partition = Assert.Single(WallFollowPlan.AfterDelete(records, new[] { 1 }, 1));
        Assert.NotNull(partition.After);
        Assert.Equal(0, partition.Record);
        Assert.Equal(3, partition.Before.Count);
        Assert.Equal(2, partition.After.Count);
        Assert.True(WallFollowPlan.SameOutline(partition.Before[0], partition.After[0], 1));

        var both = Assert.Single(WallFollowPlan.AfterDelete(records, new[] { 0, 1 }, 1));
        Assert.Null(both.After);
        Assert.NotNull(both.Before);
    }

    [Fact]
    public void AfterDelete_AnOuterWallGone_ShrinksTheOutline()
    {
        // An L of two records: the east leg deleted leaves the west one's outline.
        var west = new List<List<Pt>> { Rect(0, 0, 200, 4000) };
        var south = new List<List<Pt>> { Rect(200, 0, 6000, 200) };
        var records = new List<List<List<Pt>>> { west, south };
        var cluster = Assert.Single(WallFollowPlan.AfterDelete(records, new[] { 1 }, 1));
        Assert.True(WallFollowPlan.SameOutline(west[0], cluster.After[0], 1));
        Assert.False(WallFollowPlan.SameOutline(cluster.Before[0], cluster.After[0], 1));
    }

    [Fact]
    public void Sentence_Norwegian_NamesTheSamePieces()
    {
        Assert.Equal("Gulv, tak og 2 rom oppdatert.", WallFollowPlan.Sentence(true, true, 2, false, true));
        Assert.Equal("Gulv, tak og rom er uendret. Dagslyset er utdatert. Kjør det igjen.", WallFollowPlan.Sentence(false, false, 0, true, true));
    }
}
