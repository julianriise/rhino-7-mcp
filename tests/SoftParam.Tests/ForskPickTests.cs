using RhinoMCPPlugin.Forsk;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Selection S4: the pick line, one fixture per row of the brief's table, in
/// English and Norwegian.
/// </summary>
public class ForskPickTests
{
    static ChipRow Wall(string run = null, int runs = 1, string t = "200", string id = "w03") =>
        new() { Id = "wall-" + id, Generated = true, Kind = "wall", Selected = true, Runs = runs, RunName = run, Thickness = t, ForskId = id };

    static ChipRow Room(string name, string area, string id = "room-1") =>
        new() { Id = id, Generated = true, Kind = "room", Selected = true, Name = name, Area = area };

    static ChipRow Opening(string kind, string mark, string width, string sill, string head, string id = "block-1", string marker = null) =>
        new() { Id = id, Generated = true, Kind = "opening", OpeningKind = kind, Selected = true, Mark = mark, Width = width, Sill = sill, Head = head, Marker = marker };

    [Theory]
    [InlineData(false, "North wall · 200 mm")]
    [InlineData(true, "Veggen i nord · 200 mm")]
    public void OneRunWall_NamedBySide(bool nb, string line) =>
        Assert.Equal(line, ForskPick.Line(new[] { Wall("the north wall") }, nb));

    [Theory]
    [InlineData(false, "Wall at (4000, 2000) · 100 mm")]
    [InlineData(true, "Vegg ved (4000, 2000) · 100 mm")]
    public void OneRunWall_NamedByItsMiddle(bool nb, string line) =>
        Assert.Equal(line, ForskPick.Line(new[] { Wall("the wall at (4000, 2000)", t: "100") }, nb));

    [Theory]
    [InlineData(false, "Wall w01 · 7 runs")]
    [InlineData(true, "Vegg w01 · 7 deler")]
    public void WholeRecord(bool nb, string line) =>
        Assert.Equal(line, ForskPick.Line(new[] { Wall(runs: 7, id: "w01") }, nb));

    [Theory]
    [InlineData(false, "Bedroom · 12.4 m²")]
    [InlineData(true, "Bedroom · 12,4 m²")]
    public void OneRoom_ByItsMarkerOrItsPlate(bool nb, string line)
    {
        var marker = Room("Bedroom", "12400000");
        Assert.Equal(line, ForskPick.Line(new[] { marker }, nb));
        var plate = new ChipRow { Id = "plate-1", Generated = true, Kind = "room_plate", Selected = true, Name = "Bedroom", Area = "12400000", Marker = marker.Id };
        marker.Selected = false;
        Assert.Equal(line, ForskPick.Line(new[] { marker, plate }, nb));
        marker.Selected = true;
        Assert.Equal(line, ForskPick.Line(new[] { marker, plate }, nb));
    }

    [Theory]
    [InlineData(false, "Door D03 · 900 × 2100", "Window V02 · 1200 × 1200")]
    [InlineData(true, "Dør D03 · 900 × 2100", "Vindu V02 · 1200 × 1200")]
    public void OneDoorOrWindow(bool nb, string door, string window)
    {
        Assert.Equal(door, ForskPick.Line(new[] { Opening("door", "D03", "900", "0", "2100") }, nb));
        Assert.Equal(window, ForskPick.Line(new[] { Opening("window", "V02", "1200", "900", "2100") }, nb));
    }

    [Fact]
    public void ADoorBeforePrint_HasNoMarkYet_AndItsBlockAndMarkerAreOne()
    {
        var marker = new ChipRow { Id = "marker-1", Generated = true, Kind = "opening_marker", OpeningKind = "door", Selected = true, Width = "900", Sill = "0", Head = "2100" };
        var block = Opening("door", null, "900", "0", "2100", marker: "marker-1");
        Assert.Equal("Door · 900 × 2100", ForskPick.Line(new[] { marker, block }, false));
    }

    [Theory]
    [InlineData(false, "3 walls", "2 rooms · 31.0 m²")]
    [InlineData(true, "3 vegger", "2 rom · 31,0 m²")]
    public void SeveralOfOneKind(bool nb, string walls, string rooms)
    {
        Assert.Equal(walls, ForskPick.Line(new[] { Wall(id: "w01"), Wall(id: "w02"), Wall(id: "w03") }, nb));
        Assert.Equal(rooms, ForskPick.Line(new[] { Room("Stue", "18600000", "a"), Room("Kjøkken", "12400000", "b") }, nb));
    }

    [Theory]
    [InlineData(false, "4 objects", "Click something in the model.")]
    [InlineData(true, "4 objekter", "Klikk noe i modellen.")]
    public void MixedAndNothing(bool nb, string mixed, string none)
    {
        var loose = new ChipRow { Id = "curve", Selected = true, Curve = true };
        Assert.Equal(mixed, ForskPick.Line(new[] { Wall(id: "w01"), Room("Stue", "1", "r"), Opening("door", "D01", "900", "0", "2100"), loose }, nb));
        Assert.Equal(none, ForskPick.Line(new ChipRow[0], nb));
    }

    [Fact]
    public void OneRunWall_IsThePick_AWholeRecordIsNot()
    {
        Assert.NotNull(ForskPick.OneRunWall(new[] { Wall("the north wall") }));
        Assert.Null(ForskPick.OneRunWall(new[] { Wall(runs: 7) }));
        Assert.Null(ForskPick.OneRunWall(new[] { Wall("the north wall", id: "w01"), Wall("the south wall", id: "w02") }));
        Assert.Equal("the north wall", ForskPick.InSentence("the north wall", false));
        Assert.Equal("veggen i nord", ForskPick.InSentence("the north wall", true));
        Assert.Equal("veggen ved (4000, 2000)", ForskPick.InSentence("the wall at (4000, 2000)", true));
    }
}
