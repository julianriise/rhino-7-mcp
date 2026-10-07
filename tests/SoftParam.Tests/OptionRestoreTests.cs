using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// AN.5 restore, first cut: what restoring an option does to the model now.
/// Walls whose record changed are rewritten from the record saved with the
/// option; doors and windows move and resize back. A wall or opening added or
/// removed since, or a changed type, is refused with the reason.
/// </summary>
public class OptionRestoreTests
{
    static OptionSnapshot.Snapshot Model(string w01, double doorX, double doorWidth = 900, string doorType = "door.hinged_single")
    {
        var s = new OptionSnapshot.Snapshot { Name = "A" };
        s.Walls.Add(new OptionSnapshot.Wall { Id = "w01", Record = w01, Path = new List<Pt> { new(0, 0), new(1, 0) } });
        s.Walls.Add(new OptionSnapshot.Wall { Id = "w02", Record = "0,0;1,1", Path = new List<Pt> { new(0, 0), new(1, 1) } });
        s.Openings.Add(new OptionSnapshot.Opening { Id = "d1", Kind = "door", Type = doorType, Centre = new Pt(doorX, 0), Width = doorWidth, Sill = 0, Head = 2100 });
        return s;
    }

    [Fact]
    public void TheWallRecord_IsKeptWithTheOption()
    {
        var back = OptionSnapshot.Read(OptionSnapshot.Write(Model("0,0;5000,0;5000,200;0,200|10,10;20,10;20,20", 1000)), out var error);
        Assert.True(back != null, error);
        Assert.Equal("0,0;5000,0;5000,200;0,200|10,10;20,10;20,20", back!.Walls.Single(w => w.Id == "w01").Record);
    }

    [Fact]
    public void Restore_RewritesTheChangedWalls_AndMovesAndResizesTheOpenings()
    {
        var plan = OptionRestore.Plan(Model("A-path", 1000), Model("B-path", 1300, 1000));
        Assert.Null(plan.Refusal);
        Assert.Equal(new[] { ("w01", "A-path") }, plan.Walls.Select(w => (w.Id, w.Record)));
        var move = plan.Moves.Single();
        Assert.Equal(("d1", -300.0, 0.0), (move.Id, move.Dx, move.Dy));
        var size = plan.Sizes.Single();
        Assert.Equal(("d1", 900.0, 0.0, 2100.0), (size.Id, size.Width, size.Sill, size.Head));
        Assert.False(plan.Nothing);
    }

    [Fact]
    public void Restore_OfTheSameModel_HasNothingToDo()
    {
        var plan = OptionRestore.Plan(Model("A", 1000), Model("A", 1000));
        Assert.True(plan.Nothing);
        Assert.Null(plan.Refusal);
    }

    [Fact]
    public void Restore_RefusesWhatItCannotPutBack_AndSaysWhy()
    {
        var now = Model("A", 1000);
        now.Walls.Add(new OptionSnapshot.Wall { Id = "w03", Record = "x" });
        now.Openings.Clear();
        Assert.Equal("Option A cannot be restored yet: 1 wall added and 1 door removed since. Undo those first, or save the model as a new option.",
            OptionRestore.Plan(Model("A", 1000), now).Refusal);
        Assert.Equal("Option A cannot be restored yet: 1 door changed type since. Undo those first, or save the model as a new option.",
            OptionRestore.Plan(Model("A", 1000), Model("A", 1000, doorType: "door.sliding")).Refusal);
        var old = Model("A", 1000);
        old.Walls[0].Record = null;
        Assert.Equal("Option A was saved before restore was possible: save it again to restore it.", OptionRestore.Plan(old, Model("B", 1000)).Refusal);
    }

    /// <summary>Rooms are detected again after a restore and can start at another corner: the same room.</summary>
    [Fact]
    public void ARoomThatStartsAtAnotherCorner_IsTheSameRoom()
    {
        var a = new OptionSnapshot.Snapshot();
        a.Rooms.Add(new OptionSnapshot.Room { Id = "rd-01", Outline = new List<Pt> { new(200, 200), new(7800, 200), new(7800, 3800), new(200, 3800), new(200, 200) } });
        var b = new OptionSnapshot.Snapshot();
        b.Rooms.Add(new OptionSnapshot.Room { Id = "rd-01", Outline = new List<Pt> { new(200, 3800), new(200, 200), new(7800, 200), new(7800, 3800), new(200, 3800) } });
        Assert.True(OptionSnapshot.Compare(a, b).Same);
        b.Rooms[0].Outline.Reverse();
        Assert.True(OptionSnapshot.Compare(a, b).Same);
        b.Rooms[0].Outline[1] = new Pt(7800, 4100);
        Assert.False(OptionSnapshot.Compare(a, b).Same);
    }
}
