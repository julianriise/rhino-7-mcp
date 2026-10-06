using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// AN.5 core: a saved option (forsk.option.v1) writes, reads back equal,
/// refuses another version, and compares to another option in one line.
/// The records come from the smoke garage and the office fixture.
/// </summary>
public class OptionSnapshotTests
{
    /// <summary>A box wall's centreline: along its long side, through the middle.</summary>
    static List<Pt> Centreline(List<Pt> ring)
    {
        double x0 = ring.Min(p => p.X), x1 = ring.Max(p => p.X), y0 = ring.Min(p => p.Y), y1 = ring.Max(p => p.Y);
        return x1 - x0 >= y1 - y0
            ? new List<Pt> { new(x0, (y0 + y1) / 2), new(x1, (y0 + y1) / 2) }
            : new List<Pt> { new((x0 + x1) / 2, y0), new((x0 + x1) / 2, y1) };
    }

    /// <summary>The smoke garage: four walls, four doors and the high window, plus the office's sixteen rooms.</summary>
    static OptionSnapshot.Snapshot Garage(string name = "A")
    {
        var model = DetailFixtures.SmokeGarage();
        var snapshot = new OptionSnapshot.Snapshot { Name = name, Saved = "2026-10-06T22:00:00Z" };
        snapshot.Project["project"] = "Garasje Holmen";
        snapshot.Project["address"] = "Holmenveien 1, Oslo";
        snapshot.Analysis["daylight.quality"] = "medium";
        snapshot.Analysis["ink"] = "grey";
        foreach (var wall in model.Walls)
            snapshot.Walls.Add(new OptionSnapshot.Wall
            {
                Id = wall.Id, Level = "Plan 1", Path = Centreline(wall.Rings[0]), Thickness = wall.Thickness, Height = wall.Height,
                Existing = wall.Id == "w04",
            });
        foreach (var o in model.Openings)
            snapshot.Openings.Add(new OptionSnapshot.Opening
            {
                Id = o.Id, Host = o.Host, Kind = o.Kind, Mark = o.Mark, Centre = o.Centre, Width = o.Width, Sill = o.Sill, Head = o.Head,
                Type = o.Kind == "door" ? "door.hinged_single" : "window.fixed",
                Hand = o.Kind == "door" ? "left" : null, Swing = o.Kind == "door" ? "in" : null,
            });
        var (scene, labels, _) = OfficeRoomsTests.Office();
        var i = 0;
        foreach (var room in RoomDetect.Detect(scene).Rooms)
            snapshot.Rooms.Add(new OptionSnapshot.Room { Id = $"r{++i:D2}", Name = RoomDetect.Name(labels, room.Ring), Type = i % 2 == 0 ? "bedroom" : null, Outline = room.Ring });
        return snapshot;
    }

    static OptionSnapshot.Snapshot RoundTrip(OptionSnapshot.Snapshot snapshot)
    {
        var back = OptionSnapshot.Read(OptionSnapshot.Write(snapshot), out var error);
        Assert.True(back != null, error);
        return back!;
    }

    [Fact]
    public void WriteThenRead_GivesBackTheSameRecords()
    {
        var a = Garage();
        var back = RoundTrip(a);
        Assert.True(OptionSnapshot.Compare(a, back).Same);
        Assert.Equal(OptionSnapshot.Write(a), OptionSnapshot.Write(back));
        Assert.Equal(4, back.Walls.Count);
        Assert.Equal(5, back.Openings.Count);
        Assert.Equal(16, back.Rooms.Count);
        Assert.Equal("Garasje Holmen", back.Project["project"]);
        Assert.Equal("grey", back.Analysis["ink"]);
        Assert.True(back.Walls.Single(w => w.Id == "w04").Existing);
        var window = back.Openings.Single(o => o.Id == "o-w01");
        Assert.Equal((4800.0, 100.0, 1200.0, 1300.0, 2100.0), (window.Centre.X, window.Centre.Y, window.Width, window.Sill, window.Head));
        Assert.Null(window.Hand);
    }

    [Fact]
    public void TheSameModel_WritesTheSameBytes_WhateverTheRecordOrder()
    {
        var a = Garage();
        var shuffled = Garage();
        shuffled.Walls.Reverse();
        shuffled.Openings.Reverse();
        shuffled.Rooms.Reverse();
        Assert.Equal(OptionSnapshot.Write(a), OptionSnapshot.Write(shuffled));
    }

    [Fact]
    public void TheFile_IsVersioned_AndCoordinatesSurviveTheCommaCulture()
    {
        // The whole test run is nb-NO (CommaCulture), where 1200.5 prints as 1200,5.
        var a = Garage();
        a.Walls[0].Thickness = 200.5;
        var json = OptionSnapshot.Write(a).Replace("\r\n", "\n");
        Assert.StartsWith("{\n  \"schema\": \"forsk.option.v1\",", json);
        Assert.Contains("\"thickness\": 200.5", json);
        Assert.True(OptionSnapshot.Compare(a, RoundTrip(a)).Same);
    }

    [Fact]
    public void AnotherVersion_IsRefusedWithTheReason()
    {
        var json = OptionSnapshot.Write(Garage()).Replace("forsk.option.v1", "forsk.option.v2");
        Assert.Null(OptionSnapshot.Read(json, out var error));
        Assert.Equal("The option file is forsk.option.v2; this Forsk reads forsk.option.v1.", error);
        Assert.Null(OptionSnapshot.Read("{\"walls\": []}", out error));
        Assert.Equal("The option file is unversioned; this Forsk reads forsk.option.v1.", error);
    }

    [Fact]
    public void ADamagedFile_IsRefused_NotHalfRead()
    {
        Assert.Null(OptionSnapshot.Read("{ \"schema\": ", out var error));
        Assert.StartsWith("The option file is not JSON", error);
        var noId = OptionSnapshot.Write(Garage()).Replace("\"id\": \"w01\"", "\"id\": \"\"");
        Assert.Null(OptionSnapshot.Read(noId, out error));
        Assert.Equal("The option file is damaged: a wall has no id", error);
        var badPoint = OptionSnapshot.Write(Garage()).Replace("\"centre\": [", "\"centre\": [1, ");
        Assert.Null(OptionSnapshot.Read(badPoint, out error));
        Assert.Equal("The option file is damaged: a point is not [x, y]", error);
    }

    [Fact]
    public void ThreeWallsMoved_AndAWindowAdded_IsOneLine()
    {
        var a = Garage();
        var b = RoundTrip(a);
        foreach (var wall in b.Walls.Take(3))
            wall.Path = wall.Path.Select(p => new Pt(p.X, p.Y + 400)).ToList();
        b.Openings.Add(new OptionSnapshot.Opening { Id = "o-w02", Host = "w02", Kind = "window", Type = "window.fixed", Centre = new Pt(2000, 3900), Width = 1200, Sill = 900, Head = 2100 });
        var diff = OptionSnapshot.Compare(a, b);
        Assert.Equal("3 walls moved, 1 window added", diff.Summary());
        Assert.Equal(new[] { "w01", "w02", "w03" }, diff.Changes.Where(c => c.Verb == "moved").Select(c => c.Id));
    }

    [Fact]
    public void EachRecordCountsOnce_UnderItsBiggestChange()
    {
        var a = Garage();
        var b = RoundTrip(a);
        var door = b.Openings.Single(o => o.Id == "o-d01");
        door.Centre = new Pt(door.Centre.X + 300, door.Centre.Y);
        door.Width = 1000;
        b.Openings.Single(o => o.Id == "o-d02").Swing = "out";
        b.Openings.RemoveAll(o => o.Id == "o-d04");
        b.Walls.Single(w => w.Id == "w04").Thickness = 300;
        b.Rooms[0].Name = "Stue";
        b.Rooms.RemoveAt(1);
        b.Analysis["ink"] = "black";
        Assert.Equal("1 wall changed, 1 door removed, 1 door moved, 1 door changed, 1 room removed, 1 room renamed, analysis settings changed",
            OptionSnapshot.Compare(a, b).Summary());
    }

    [Fact]
    public void LessThanHalfAMillimetre_IsNoChange()
    {
        var a = Garage();
        var b = RoundTrip(a);
        b.Walls[0].Path = b.Walls[0].Path.Select(p => new Pt(p.X + 0.4, p.Y)).ToList();
        b.Openings[0].Width += 0.3;
        Assert.Equal("No changes.", OptionSnapshot.Compare(a, b).Summary());
        b.Project["client"] = "Holmen";
        Assert.Equal("project info changed", OptionSnapshot.Compare(a, b).Summary());
    }

    [Theory]
    [InlineData("/Users/jr/Projects/Holmen.3dm", "A", "/Users/jr/Projects/Holmen.forsk/options/A.json")]
    [InlineData("/Users/jr/Projects/Holmen hytte.3dm", "B 2", "/Users/jr/Projects/Holmen hytte.forsk/options/B 2.json")]
    public void OptionFiles_LiveBesideThe3dm(string file, string name, string expected)
    {
        Assert.Equal(expected, OptionSnapshot.PathFor(file, name).Replace('\\', '/'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../A")]
    [InlineData("A/B")]
    [InlineData(" A")]
    [InlineData("an option name that is far too long")]
    public void ANameTheFolderCannotHold_IsRefused(string name)
    {
        Assert.False(OptionSnapshot.IsName(name));
        Assert.Throws<ArgumentException>(() => OptionSnapshot.PathFor("/x/Holmen.3dm", name));
    }

    [Fact]
    public void AnUnsavedFile_HasNoOptionFolder()
    {
        Assert.Throws<ArgumentException>(() => OptionSnapshot.PathFor("", "A"));
    }
}
