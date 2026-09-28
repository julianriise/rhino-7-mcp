using System;
using System.Collections.Generic;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F5.1: marks tie a plan opening to its schedule row and stay put through
/// edits; the tables print the model's own records; the room list prints what
/// the plan tags print; the tables flow into columns without losing a line.
/// </summary>
public class SchedulesTests
{
    static Schedules.Opening Opening(string kind, double x, double y, string mark = null, string type = null)
    {
        Assert.True(OpeningTypes.TryRead(kind, type, null, null, out var record, out _));
        return new Schedules.Opening
        {
            Id = Guid.NewGuid().ToString(),
            Mark = mark,
            Record = record,
            X = x,
            Y = y,
            Width = kind == "window" ? 1200 : 900,
            Sill = kind == "window" ? 900 : 0,
            Head = 2100
        };
    }

    [Fact]
    public void Marks_NewOpenings_NumberInReadingOrder_DoorsAndWindowsApart()
    {
        var openings = new List<Schedules.Opening>
        {
            Opening("door", 5000, 0),
            Opening("window", 0, 4000),
            Opening("door", 1000, 4000),
            Opening("window", 3000, 4000),
            Opening("door", 0, 0)
        };
        var marks = Schedules.AssignMarks(openings, null);
        // Top row first (y 4000), left to right; then the bottom row.
        Assert.Equal(new[] { "D03", "V01", "D01", "V02", "D02" }, marks);
    }

    [Fact]
    public void Marks_StayThroughDeleteAndAdd_AndAreNotReused()
    {
        var next = new Dictionary<string, int>();
        var openings = Enumerable.Range(0, 5).Select(i => Opening("window", i * 2000, 0)).ToList();
        var first = Schedules.AssignMarks(openings, next);
        for (var i = 0; i < openings.Count; i++) openings[i].Mark = first[i];
        Assert.Equal(new[] { "V01", "V02", "V03", "V04", "V05" }, first);
        Assert.Equal(6, next["V"]);

        // Delete V02 and V05, then add a window at the far left of the row.
        openings.RemoveAt(4);
        openings.RemoveAt(1);
        openings.Add(Opening("window", -3000, 0));
        Assert.Equal(new[] { "V01", "V03", "V04", "V06" }, Schedules.AssignMarks(openings, next));
        Assert.Equal(7, next["V"]);
    }

    [Fact]
    public void Marks_AFreshBake_StartsAtOne()
    {
        // clear_generated took every window; the stored counter does not carry over.
        var next = new Dictionary<string, int> { ["V"] = 64, ["D"] = 15 };
        var marks = Schedules.AssignMarks(new[] { Opening("window", 0, 0), Opening("window", 1000, 0) }, next);
        Assert.Equal(new[] { "V01", "V02" }, marks);
        Assert.Equal(3, next["V"]);
        Assert.False(next.ContainsKey("D"));
    }

    [Fact]
    public void Marks_CopiedOrForeignMarks_AreReplaced()
    {
        var openings = new List<Schedules.Opening>
        {
            Opening("door", 0, 0, "D04"),
            Opening("door", 1000, 0, "D04"),   // a copied marker
            Opening("door", 2000, 0, "V01"),   // a window mark on a door
            Opening("window", 3000, 0, "V7")   // kept, written as V07
        };
        Assert.Equal(new[] { "D04", "D05", "D06", "V07" }, Schedules.AssignMarks(openings, null));
    }

    [Fact]
    public void MarkSide_HingedAwayFromTheSwing_OthersOutside()
    {
        Assert.True(OpeningTypes.TryRead("door", "door.hinged_single", "L", "in", out var swingIn, out _));
        Assert.True(OpeningTypes.TryRead("door", "door.hinged_single", "L", "out", out var swingOut, out _));
        var between = new[] { "Kontor", "Gang" };
        // The leaf swings to +yInward for "in": the mark sits on the other side.
        Assert.Equal(-1, Schedules.MarkSide(swingIn, 1, between));
        Assert.Equal(1, Schedules.MarkSide(swingOut, 1, between));
        Assert.Equal(1, Schedules.MarkSide(swingIn, -1, between));

        Assert.True(OpeningTypes.TryRead("window", null, null, null, out var window, out _));
        Assert.Equal(-1, Schedules.MarkSide(window, 1, new[] { null, "Stue" }));
        Assert.Equal(1, Schedules.MarkSide(window, 1, new[] { "Stue", null }));
        Assert.True(OpeningTypes.TryRead("door", "door.sliding", null, null, out var sliding, out _));
        Assert.Equal(1, Schedules.MarkSide(sliding, -1, between));
    }

    [Fact]
    public void DoorTable_PrintsTheRecord_InMarkOrder()
    {
        var hinged = Opening("door", 0, 0, "D02");
        hinged.Rooms = new[] { "Kontor", "Gang" };
        var sliding = Opening("door", 0, 0, "D01", "door.sliding");
        sliding.Rooms = new[] { null, "Gang" };
        Assert.True(OpeningTypes.TryRead("door", "door.hinged_single", "R", "out", out var outward, out _));
        hinged.Record = outward;

        var table = Schedules.DoorTable(new[] { hinged, sliding });
        Assert.Equal("Dørliste", table.Title);
        Assert.Equal(new[] { "D01", "D02" }, table.Ids);
        Assert.Equal(new[] { "D01", "Skyvedør", "900 × 2100", "V", "Gang" }, table.Rows[0]);
        Assert.Equal(new[] { "D02", "Slagdør", "900 × 2100", "H ut", "Kontor / Gang" }, table.Rows[1]);
        Assert.Null(table.Total);
    }

    [Fact]
    public void WindowTable_PrintsSillAndRoom()
    {
        var window = Opening("window", 0, 0, "V01", "window.top_hung");
        window.Sill = 1300;
        window.Rooms = new[] { "Stue", null };
        var row = Schedules.WindowTable(new[] { window }).Rows.Single();
        Assert.Equal(new[] { "V01", "Topphengslet", "1200 × 800", "1300", "Stue" }, row);
    }

    [Fact]
    public void RoomTable_AreasAreThePlanTagsAreas_ThenTheSum()
    {
        var rooms = new[]
        {
            new Schedules.Room { Id = "rd-02", Name = "Gang", AreaMm2 = 20160000 },
            new Schedules.Room { Id = "rd-01", Name = "Kontor", AreaMm2 = 12400000 }
        };
        var table = Schedules.RoomTable(rooms);
        Assert.Equal(new[] { "rd-01", "rd-02" }, table.Ids);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var room = rooms.Single(r => r.Id == table.Ids[i]);
            Assert.Equal(OpeningTypes.RoomTag(room.AreaMm2), "ca. " + table.Rows[i][1]);
            Assert.Equal(room.Name, table.Rows[i][0]);
        }
        Assert.Equal(new[] { "Sum", "32,6 m²" }, table.Total);
    }

    [Fact]
    public void GlazedDoor_IsARecordKey_KeptThroughATypeSwap()
    {
        var keys = new Dictionary<string, string>
        {
            [OpeningTypes.TypeKey] = "door.hinged_single",
            [OpeningTypes.GlazedKey] = "true"
        };
        Assert.True(OpeningTypes.TryReadKeys(keys, "door", out var door, out _));
        Assert.True(door.Glazed);
        Assert.Equal("true", OpeningTypes.ToKeys(door)[OpeningTypes.GlazedKey]);
        Assert.True(OpeningTypes.TryApply(door, "door.sliding", null, null, out var edit, out _));
        Assert.True(edit.After.Glazed);

        Assert.True(OpeningTypes.TryRead("door", null, null, null, out var solid, out _));
        Assert.False(solid.Glazed);
        Assert.False(OpeningTypes.ToKeys(solid).ContainsKey(OpeningTypes.GlazedKey));
        Assert.True(OpeningTypes.TryRead("window", "window.fixed", null, null, "false", out var window, out _));
        Assert.True(window.Glazed);

        var glazed = Opening("door", 0, 0, "D01");
        glazed.Record = door;
        Assert.Equal("Slagdør m/glass", Schedules.DoorTable(new[] { glazed }).Rows.Single()[1]);
    }

    [Fact]
    public void EveryType_HasANorwegianScheduleLabel()
    {
        foreach (var def in OpeningTypes.All)
            Assert.False(string.IsNullOrWhiteSpace(def.ScheduleLabel), def.Id);
    }

    [Theory]
    [InlineData(new[] { "door" }, "Dørliste")]
    [InlineData(new[] { "door", "window" }, "Dør- og vindusliste")]
    [InlineData(new[] { "window", "room" }, "Vindus- og romliste")]
    [InlineData(new[] { "door", "window", "room" }, "Dør-, vindus- og romliste")]
    public void SheetTitle_NamesTheLists(string[] kinds, string title)
    {
        Assert.Equal(title, Schedules.SheetTitle(kinds));
    }

    static Schedules.Table Rows(string kind, int count)
    {
        var openings = Enumerable.Range(1, count)
            .Select(i => Opening(kind, i, 0, Schedules.Format(Schedules.Prefix(kind), i)))
            .ToList();
        return kind == "window" ? Schedules.WindowTable(openings) : Schedules.DoorTable(openings);
    }

    [Fact]
    public void Flow_OfficeLists_FitOneSheet_EveryLineOnce()
    {
        // 14 doors, 62 windows and 16 rooms above the footer of an A3 sheet.
        var rooms = Enumerable.Range(1, 16)
            .Select(i => new Schedules.Room { Id = $"rd-{i:00}", Name = "Konferanserom", AreaMm2 = 20000000 })
            .ToList();
        var tables = new[] { Rows("door", 14), Rows("window", 62), Schedules.RoomTable(rooms) };
        const double height = 254;
        var blocks = Schedules.Flow(tables, height, out var width);
        Assert.NotNull(blocks);
        Assert.True(width <= 400, $"schedules {width} mm wide");
        foreach (var table in tables)
        {
            var mine = blocks.Where(b => b.Table == table).ToList();
            Assert.Equal(Enumerable.Range(0, table.Lines), mine.SelectMany(b => Enumerable.Range(b.First, b.Count)));
            Assert.False(mine[0].Continued);
            Assert.All(mine.Skip(1), b => Assert.EndsWith("(forts.)", b.Title));
        }
        foreach (var block in blocks)
            Assert.True(block.Top + block.Height <= height + 1e-9, $"{block.Title} runs off the column");
        foreach (var a in blocks)
            foreach (var b in blocks.Where(b => b != a && b.X == a.X))
                Assert.True(a.Top + a.Height <= b.Top || b.Top + b.Height <= a.Top, "two blocks overlap");
    }

    [Fact]
    public void Flow_GarageLists_StackInOneColumn()
    {
        var rooms = new[] { new Schedules.Room { Id = "rd-01", Name = "Rom", AreaMm2 = 27400000 } };
        var tables = new[] { Rows("door", 4), Rows("window", 1), Schedules.RoomTable(rooms) };
        var blocks = Schedules.Flow(tables, 254, out var width);
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(0, b.X));
        Assert.Equal(tables.Max(t => t.Width), width);
    }

    [Fact]
    public void Flow_TooShortForOneLine_IsNull()
    {
        Assert.Null(Schedules.Flow(new[] { Rows("door", 2) }, Schedules.TitleSpaceMm + Schedules.RowMm, out _));
    }
}

/// <summary>F5.1 in chat: schedule words bias the turn toward sheets; print keeps print.</summary>
public class ScheduleIntentTests
{
    [Theory]
    [InlineData("add a door schedule", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("put the schedules on the plan", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("lag dørliste og vindusliste", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("romliste", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("print the schedules", RhinoMCPPlugin.Forsk.ForskIntent.Print)]
    public void ScheduleWords_ClassifyAsSheets(string text, RhinoMCPPlugin.Forsk.ForskIntent expected)
    {
        Assert.Equal(expected, RhinoMCPPlugin.Forsk.ForskIntentRouter.Classify(text, ""));
    }
}
