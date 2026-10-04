using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// D1: a detail is a stored reference to one wall or opening (forsk/details),
/// resolved from the live records at every Print. These pin the store, the
/// ids, the de-duplication, what a gone element does, and the chat words.
/// </summary>
public class DetailsTests
{
    [Fact]
    public void ReadAndWrite_RoundTrip()
    {
        var json = "[{\"id\":\"DET01\",\"wall\":\"w03\"},{\"id\":\"DET02\",\"opening\":\"o-door\"}]";
        var records = Details.Read(json);
        Assert.Equal(new[] { "DET01", "DET02" }, records.Select(r => r.Id));
        Assert.Equal("w03", records[0].Wall);
        Assert.Null(records[0].Opening);
        Assert.Equal("o-door", records[1].Opening);
        Assert.Equal(json, Details.Write(records));
        Assert.Empty(Details.Read(null));
        Assert.Empty(Details.Read("not json"));
        Assert.Empty(Details.Read("[{\"wall\":\"w01\"},{\"id\":\"DET03\"}]"));
    }

    [Fact]
    public void NextId_IsOnePastTheHighest()
    {
        Assert.Equal("DET01", Details.NextId(new List<Details.Record>()));
        Assert.Equal("DET08", Details.NextId(Details.Read("[{\"id\":\"DET01\",\"wall\":\"a\"},{\"id\":\"DET07\",\"wall\":\"b\"}]")));
        Assert.Equal("DET10", Details.NextId(Details.Read("[{\"id\":\"DET09\",\"wall\":\"a\"}]")));
    }

    [Fact]
    public void Add_SkipsAnElementThatHasADetail()
    {
        var stored = Details.Read("[{\"id\":\"DET01\",\"wall\":\"w01\"}]");
        var refs = new[]
        {
            new Details.Ref { Wall = "W01" },
            new Details.Ref { Opening = "o-door" },
            new Details.Ref { Wall = "w03" },
            new Details.Ref { Opening = "o-door" }
        };
        var list = Details.Add(stored, refs, out var added, out var already);
        Assert.Equal(2, added);
        Assert.Equal(2, already);
        Assert.Equal(new[] { "DET01", "DET02", "DET03" }, list.Select(r => r.Id));
        Assert.Equal("o-door", list[1].Opening);
        Assert.Equal("w03", list[2].Wall);
    }

    [Fact]
    public void Remove_DropsTheIdsGiven_OrEveryOne()
    {
        var stored = Details.Read("[{\"id\":\"DET01\",\"wall\":\"w01\"},{\"id\":\"DET02\",\"opening\":\"o-door\"}]");
        Assert.Equal(new[] { "DET01" }, Details.Remove(stored, new[] { "det02" }).Select(r => r.Id));
        Assert.Empty(Details.Remove(stored, null));
        Assert.Equal(2, Details.Remove(stored, new[] { "DET09" }).Count);
    }

    [Fact]
    public void Views_AWallHasAPlanAndASection_AnOpeningAnElevationToo()
    {
        Assert.Equal(new[] { "plan", "section" }, Details.Views(new Details.Record { Id = "DET01", Wall = "w01" }));
        Assert.Equal(new[] { "plan", "elevation", "section" }, Details.Views(new Details.Record { Id = "DET02", Opening = "o-door" }));
    }

    [Fact]
    public void Resolve_TheSouthWall_IsItsRunInTheGarage()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(window: true), wall: "w01");
        Assert.NotNull(facts);
        Assert.Equal("wall", facts.Kind);
        Assert.Equal(0, facts.Run.Near, 6);
        Assert.Equal(200, facts.Run.Far, 6);
        Assert.Equal(0, facts.Run.Lo, 6);
        Assert.Equal(8000, facts.Run.Hi, 6);
        Assert.Equal(200, facts.Thickness, 6);
        Assert.Equal(3000, facts.Height, 6);
        // The Normal points north, into the garage: the outer face is Near.
        Assert.True(facts.Exterior);
        Assert.Equal(-1, facts.Outer);
        Assert.Equal(new[] { "o-door" }, facts.Openings.Select(o => o.Opening.Id));
        Assert.Equal(2000, facts.Openings[0].U, 6);
        Assert.Equal("South wall", Details.Name(facts));

        var north = DetailFixtures.Facts(DetailFixtures.Garage(window: true), wall: "w02");
        Assert.Equal(1, north.Outer);
        Assert.Equal("North wall", Details.Name(north));
        Assert.Equal(new[] { "o-window" }, north.Openings.Select(o => o.Opening.Id));
    }

    [Fact]
    public void Resolve_TheDoor_IsOnTheSouthWallsRun()
    {
        var facts = DetailFixtures.Facts(DetailFixtures.Garage(), opening: "o-door");
        Assert.NotNull(facts);
        Assert.Equal("door", facts.Kind);
        Assert.Equal("w01", facts.Wall.Id);
        Assert.Equal(2000, facts.Opening.U, 6);
        Assert.Equal(900, facts.Opening.Opening.Width, 6);
        Assert.Equal(2100, facts.Opening.Opening.Head, 6);
        Assert.Equal(-1, facts.Outer);
        Assert.Equal("Door D01", Details.Name(facts));
        Assert.Equal("Window W01", Details.Name(DetailFixtures.Facts(DetailFixtures.Garage(window: true), opening: "o-window")));
    }

    [Fact]
    public void Resolve_AGoneElement_IsNull()
    {
        var garage = DetailFixtures.Garage();
        Assert.Null(DetailFixtures.Facts(garage, wall: "w09"));
        Assert.Null(DetailFixtures.Facts(garage, opening: "o-gone"));

        // A split wall is two new records: the old id is gone, and so is its detail.
        var split = DetailFixtures.Garage();
        split.Walls.RemoveAll(w => w.Id == "w01");
        split.Walls.Add(new IfcExport.Wall { Id = "w05", Rings = DetailFixtures.Box(0, 0, 4000, 200), Thickness = 200, Height = 3000 });
        split.Walls.Add(new IfcExport.Wall { Id = "w06", Rings = DetailFixtures.Box(4000, 0, 8000, 200), Thickness = 200, Height = 3000 });
        split.Openings[0].Host = "w05";
        Assert.Null(DetailFixtures.Facts(split, wall: "w01"));
        Assert.NotNull(DetailFixtures.Facts(split, opening: "o-door"));

        // An opening whose host is gone goes with it.
        var hostless = DetailFixtures.Garage();
        hostless.Walls.RemoveAll(w => w.Id == "w01");
        Assert.Null(DetailFixtures.Facts(hostless, opening: "o-door"));
    }

    [Fact]
    public void Names_TwoWallsOfOneName_CarryTheirIds()
    {
        var a = new Details.Facts { Kind = "wall", RunName = "the north wall", Wall = new IfcExport.Wall { Id = "w02" } };
        var b = new Details.Facts { Kind = "wall", RunName = "the north wall", Wall = new IfcExport.Wall { Id = "w07" } };
        var c = new Details.Facts { Kind = "wall", RunName = "the wall at (4000, 2000)", Wall = new IfcExport.Wall { Id = "w03" } };
        Assert.Equal(new[] { "North wall (W02)", "North wall (W07)", "Wall at (4000, 2000)" }, Details.Names(new[] { a, b, c }));
    }

    [Fact]
    public void DroppedLine_SaysWhatWentAndWhy()
    {
        Assert.Equal("", Details.DroppedLine(0));
        Assert.Equal("1 detail dropped: its wall or opening is gone.", Details.DroppedLine(1));
        Assert.Equal("2 details dropped: their walls or openings are gone.", Details.DroppedLine(2));
    }

    [Theory]
    [InlineData("add detail")]
    [InlineData("add details")]
    [InlineData("add a detail of this wall")]
    [InlineData("detail this wall")]
    [InlineData("detail this window")]
    [InlineData("remove the details")]
    [InlineData("legg til detalj")]
    [InlineData("fjern detaljene")]
    public void DetailWords_ClassifyAsSheets(string text)
    {
        Assert.Equal(ForskIntent.Sheets, ForskIntentRouter.Classify(text));
    }

    [Theory]
    [InlineData("explain in detail how the roof is built")]
    [InlineData("make the window 1200 wide")]
    public void ABareDetail_OrAnOpeningsSize_IsNotADetail(string text)
    {
        Assert.NotEqual(ForskIntent.Sheets, ForskIntentRouter.Classify(text));
    }

    [Fact]
    public void TheWindowEdit_StaysAnEdit()
    {
        Assert.Equal(ForskIntent.Edit, ForskIntentRouter.Classify("make the window 1200 wide"));
    }
}
