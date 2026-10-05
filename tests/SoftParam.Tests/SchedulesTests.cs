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
        Assert.Equal(new[] { "D03", "W01", "D01", "W02", "D02" }, marks);
    }

    [Fact]
    public void Marks_StayThroughDeleteAndAdd_AndAreNotReused()
    {
        var next = new Dictionary<string, int>();
        var openings = Enumerable.Range(0, 5).Select(i => Opening("window", i * 2000, 0)).ToList();
        var first = Schedules.AssignMarks(openings, next);
        for (var i = 0; i < openings.Count; i++) openings[i].Mark = first[i];
        Assert.Equal(new[] { "W01", "W02", "W03", "W04", "W05" }, first);
        Assert.Equal(6, next["W"]);

        // Delete W02 and W05, then add a window at the far left of the row.
        openings.RemoveAt(4);
        openings.RemoveAt(1);
        openings.Add(Opening("window", -3000, 0));
        Assert.Equal(new[] { "W01", "W03", "W04", "W06" }, Schedules.AssignMarks(openings, next));
        Assert.Equal(7, next["W"]);
    }

    [Fact]
    public void Marks_AFreshBake_StartsAtOne()
    {
        // clear_generated took every window; the stored counter does not carry over.
        var next = new Dictionary<string, int> { ["W"] = 64, ["D"] = 15 };
        var marks = Schedules.AssignMarks(new[] { Opening("window", 0, 0), Opening("window", 1000, 0) }, next);
        Assert.Equal(new[] { "W01", "W02" }, marks);
        Assert.Equal(3, next["W"]);
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
            Opening("window", 3000, 0, "W7"),  // kept, written as W07
            Opening("window", 4000, 0, "V01")  // a Norwegian mark is foreign in English
        };
        Assert.Equal(new[] { "D04", "D05", "D06", "W07", "W08" }, Schedules.AssignMarks(openings, null));
        Assert.Equal(new[] { "V07" }, Schedules.AssignMarks(new[] { Opening("window", 0, 0, "V7") }, null, true));
        Assert.Equal("D01", Schedules.AssignMarks(new[] { Opening("door", 0, 0) }, null, true)[0]);
    }

    [Fact]
    public void MarkSide_OutsideFirst_ForEveryDoorAndWindow()
    {
        // The garage wall: every opening has its room on one side only. The
        // flipped door swings out, and its mark still goes outside with the rest.
        Assert.True(OpeningTypes.TryRead("door", "door.hinged_single", "L", "in", out var swingIn, out _));
        Assert.True(OpeningTypes.TryRead("door", "door.hinged_single", "L", "out", out var swingOut, out _));
        Assert.True(OpeningTypes.TryRead("door", "door.sliding", null, null, out var sliding, out _));
        Assert.True(OpeningTypes.TryRead("window", null, null, null, out var window, out _));
        var roomAbove = new[] { null, "Rom" };
        foreach (var record in new[] { swingIn, swingOut, sliding, window })
        {
            Assert.Equal(-1, Schedules.MarkSide(record, 1, roomAbove));
            Assert.Equal(-1, Schedules.MarkSide(record, -1, roomAbove));
            Assert.Equal(1, Schedules.MarkSide(record, 1, new[] { "Rom", null }));
        }

        // Between two rooms a hinged door keeps its mark off the leaf's side.
        var between = new[] { "Kontor", "Gang" };
        Assert.Equal(-1, Schedules.MarkSide(swingIn, 1, between));
        Assert.Equal(1, Schedules.MarkSide(swingOut, 1, between));
        Assert.Equal(1, Schedules.MarkSide(sliding, -1, between));
    }

    // A door in a wall along x at y = 0, 200 thick, 900 wide, at 1:125: gap
    // 1 mm on paper is 125, a 1.25 mm mark about 280 x 160 drawing mm.
    static Schedules.MarkSpot Door() => new Schedules.MarkSpot
    {
        At = new RoomDetect.Pt(0, 0),
        Along = new RoomDetect.Pt(1, 0),
        Out = new RoomDetect.Pt(0, -1),
        HalfThick = 100,
        HalfWidth = 450,
        Hx = 140,
        Hy = 80,
        Gap = 125
    };

    static bool Touches(RoomDetect.Pt centre, Schedules.MarkSpot spot, RoomDetect.Box tag) =>
        Schedules.Overlaps(Schedules.MarkBox(centre, spot.Hx, spot.Hy), tag, spot.Gap);

    [Fact]
    public void MarkIsHalfTheRoomTagHeight()
    {
        Assert.Equal(2 * Schedules.MarkMm, OpeningTypes.PlanAnnotationHeight(1), 9);
    }

    [Fact]
    public void PlaceMark_NothingNear_SitsOutsideAtTheOpeningCentre()
    {
        var spot = Door();
        Assert.True(Schedules.PlaceMark(spot, new List<RoomDetect.Box>(), null, out var at));
        Assert.Equal(0, at.X, 9);
        Assert.Equal(-(100 + 125 + 80), at.Y, 9);
    }

    [Fact]
    public void PlaceMark_ATagInTheWay_SlidesAlongTheWall_ClearOfIt()
    {
        // Wet Room's name overflows its room and sits where D08 would go.
        var spot = Door();
        var tag = new RoomDetect.Box(-300, -500, 0, -250);
        Assert.True(Schedules.PlaceMark(spot, new[] { tag }, null, out var at));
        Assert.False(Touches(at, spot, tag));
        Assert.True(at.Y < 0, "still outside");
        Assert.True(Math.Abs(at.X) <= spot.HalfWidth + spot.Hx, "still beside its opening");
    }

    [Fact]
    public void PlaceMark_TagAcrossTheWholeOpening_TakesTheOtherFace()
    {
        var spot = Door();
        var wide = new RoomDetect.Box(-1200, -600, 1200, -200);
        Assert.True(Schedules.PlaceMark(spot, new[] { wide }, null, out var at));
        Assert.False(Touches(at, spot, wide));
        Assert.Equal(100 + 125 + 80, at.Y, 9);
    }

    [Fact]
    public void PlaceMark_BothFacesBlockedAtTheWall_StepsOut_OwnFaceFirst()
    {
        var spot = Door();
        var tags = new[] { new RoomDetect.Box(-1200, -600, 1200, -200), new RoomDetect.Box(-1200, 200, 1200, 600) };
        Assert.True(Schedules.PlaceMark(spot, tags, null, out var at));
        Assert.All(tags, tag => Assert.False(Touches(at, spot, tag)));
        Assert.True(at.Y < -600, "a step further out, on its own face");
    }

    [Fact]
    public void PlaceMark_KeepsOffWallPoche()
    {
        // A cross wall 100 thick just past the first choice on the own face.
        var spot = Door();
        var cross = new List<List<List<RoomDetect.Pt>>>
        {
            new List<List<RoomDetect.Pt>>
            {
                new List<RoomDetect.Pt>
                {
                    new RoomDetect.Pt(-2000, -350), new RoomDetect.Pt(2000, -350),
                    new RoomDetect.Pt(2000, -250), new RoomDetect.Pt(-2000, -250)
                }
            }
        };
        Assert.True(Schedules.PlaceMark(spot, new List<RoomDetect.Box>(), cross, out var at));
        Assert.False(Schedules.OnWalls(Schedules.MarkBox(at, spot.Hx, spot.Hy), cross));
        Assert.True(at.Y > 0, "the other face");
        Assert.True(Schedules.OnWalls(new RoomDetect.Box(-100, -400, 100, -200), cross));
        Assert.True(Schedules.OnWalls(new RoomDetect.Box(-10, -310, 10, -290), cross), "inside the wall");
    }

    [Fact]
    public void PlaceMark_NoClearSpot_KeepsTheFirstChoice_AndSaysSo()
    {
        var spot = Door();
        var everywhere = new[] { new RoomDetect.Box(-5000, -5000, 5000, 5000) };
        Assert.False(Schedules.PlaceMark(spot, everywhere, null, out var at));
        Assert.Equal(-(100 + 125 + 80), at.Y, 9);
    }

    [Fact]
    public void PlaceMark_KeepsClearOfAMarkAlreadyPlaced()
    {
        var spot = Door();
        Assert.True(Schedules.PlaceMark(spot, new List<RoomDetect.Box>(), null, out var first));
        var taken = new List<RoomDetect.Box> { Schedules.MarkBox(first, spot.Hx, spot.Hy) };
        Assert.True(Schedules.PlaceMark(spot, taken, null, out var second));
        Assert.False(Touches(second, spot, taken[0]));
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
        Assert.Equal("Door schedule", table.Title);
        Assert.Equal(new[] { "D01", "D02" }, table.Ids);
        Assert.Equal(new[] { "D01", "Sliding door", "900 × 2100", "L", "Gang" }, table.Rows[0]);
        Assert.Equal(new[] { "D02", "Hinged door", "900 × 2100", "R out", "Kontor / Gang" }, table.Rows[1]);
        Assert.Null(table.Total);
    }

    [Fact]
    public void WindowTable_PrintsSillAndRoom()
    {
        var window = Opening("window", 0, 0, "V01", "window.top_hung");
        window.Sill = 1300;
        window.Rooms = new[] { "Stue", null };
        var row = Schedules.WindowTable(new[] { window }).Rows.Single();
        Assert.Equal(new[] { "V01", "Top-hung window", "1200 × 800", "1300", "Stue" }, row);
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
            Assert.Equal(OpeningTypes.RoomTag(room.AreaMm2), "≈ " + table.Rows[i][1]);
            Assert.Equal(room.Name, table.Rows[i][0]);
        }
        Assert.Equal(new[] { "Sum", "32.6 m²" }, table.Total);
        Assert.Equal(3, table.Lines);
    }

    [Fact]
    public void RoomTable_IsTheRoomsAndTheSum_NoFootRows()
    {
        var rooms = new[]
        {
            new Schedules.Room { Id = "rd-02", Name = "Gang", AreaMm2 = 20160000 },
            new Schedules.Room { Id = "rd-01", Name = "Kontor", AreaMm2 = 12400000 }
        };
        var table = Schedules.RoomTable(rooms);
        Assert.Equal(new[] { "Sum", "32.6 m²" }, table.Line(2));
        Assert.Equal("total", table.LineId(2));
        // BRA and BTA live once, in the Arealtabell on the front sheet.
        Assert.Equal(3, table.Lines);
        Assert.Null(table.Line(3));
        Assert.Null(table.Note);
    }

    /// <summary>
    /// The office smoke's model: its 16 detected rooms on level 0, and BRA
    /// and BTA asked of its wall rings at 200 mm. Headless, those rings give
    /// no figure (the outline does not inset cleanly), so the floor shows
    /// net area and the note says gross and usable area are missing.
    /// </summary>
    static AreaStats.Result OfficeAreas()
    {
        var (scene, labels, _) = OfficeRoomsTests.Office();
        var found = RoomDetect.Detect(scene);
        var rooms = found.Rooms.Select((room, i) => new AreaStats.Room
        {
            Id = "rd-" + (i + 1).ToString("00"),
            Name = RoomDetect.Name(labels, room.Ring),
            Level = "0",
            AreaMm2 = room.Area
        }).ToList();
        var result = AreaStats.Compute(rooms);
        AreaStats.ApplyGross(result, new[] { new AreaStats.Wall { Level = "0", ThicknessMm = 200, Rings = scene.Walls[0] } }, 1.0);
        return result;
    }

    [Fact]
    public void AreaTable_Office_OneFloorBlock_ThenTheUsesLargestFirst()
    {
        var result = OfficeAreas();
        var table = Schedules.AreaTable(result);
        Assert.Equal("area", table.Kind);
        Assert.Equal("Areas", table.Title);
        // One floor block: its heading, the gross and usable area it has, its net area.
        var floor = result.Gross.Single();
        var block = table.Ids.TakeWhile(id => id != "uses").ToList();
        var gross = new[] { floor.BtaMm2 == null ? null : "bta-0", floor.BraMm2 == null ? null : "bra-0" }.Where(id => id != null);
        Assert.Equal(new[] { "floor-0" }.Concat(gross).Append("net-0"), block);
        Assert.Equal(new[] { "Ground floor", "" }, table.Rows[0]);
        Assert.Equal(new[] { "Net area", OpeningTypes.AreaText(result.Floors[0].AreaMm2, false) }, table.Rows[block.Count - 1]);
        Assert.Equal(new[] { "Net area by use", "" }, table.Rows[block.Count]);
        var after = table.Rows.Skip(block.Count + 1).ToList();
        var typeAt = after.FindIndex(r => r[0] == "By room type");
        var uses = typeAt < 0 ? after : after.Take(typeAt).ToList();
        Assert.Equal(result.Uses.Select(u => AreaStats.UseLabel(u.Key)), uses.Select(r => r[0]));
        Assert.Equal("Unassigned", table.Rows[table.Rows.Count - 2][0]);
        Assert.Equal(new[] { "Total", OpeningTypes.AreaText(result.NetMm2, false) }, table.Rows[table.Rows.Count - 1]);
        Assert.Equal(result.Uses.Select(u => OpeningTypes.AreaText(u.AreaMm2, false)), uses.Select(r => r[1]));
        var areas = result.Uses.Select(u => u.AreaMm2).ToList();
        Assert.Equal(areas.OrderByDescending(a => a), areas);
        Assert.StartsWith(Schedules.AreaNote, table.Note);
        Assert.Equal(floor.BtaMm2 == null && floor.BraMm2 == null, table.Note.EndsWith("Ground floor: gross area (BTA) and usable area (BRA) are missing."));
    }

    [Fact]
    public void AreaTable_LeavesOutAMissingBraAndBta_WithItsNote()
    {
        var result = AreaStats.Compute(new[]
        {
            new AreaStats.Room { Id = "a", Name = "Stue", Level = "0", AreaMm2 = 20_000_000 },
            new AreaStats.Room { Id = "b", Name = "Kontor", Level = "1", AreaMm2 = 12_000_000 }
        });
        result.Gross = new List<AreaStats.FloorGross>
        {
            new AreaStats.FloorGross { Level = "0", BraMm2 = 22_000_000, BtaMm2 = 25_000_000 },
            new AreaStats.FloorGross { Level = "1", Note = "no wall outline on this floor" }
        };
        var table = Schedules.AreaTable(result);
        Assert.Equal(new[] { "floor-0", "bta-0", "bra-0", "net-0", "floor-1", "net-1", "uses", "use-Stue", "use-Kontor", "types", "type-unassigned", "total" }, table.Ids);
        Assert.Equal(new[] { "Gross area (BTA)", "25.0 m²" }, table.Rows[1]);
        Assert.Equal(new[] { "Usable area (BRA)", "22.0 m²" }, table.Rows[2]);
        Assert.Equal(new[] { "Net area", "20.0 m²" }, table.Rows[3]);
        Assert.Equal("Areas are approximate, from the model, not measured to NS 3940. 1st floor: gross area (BTA) and usable area (BRA) are missing.", table.Note);
        // With no rooms there is no table.
        Assert.Equal(0, Schedules.AreaTable(AreaStats.Compute(new AreaStats.Room[0])).Lines);
    }

    [Fact]
    public void DrawingList_ListsTheSheetsInSetOrder_WithTheSetScale()
    {
        var rows = new[]
        {
            new Schedules.Drawing { Number = "A-00-001", Title = "Drawing list and areas" },
            new Schedules.Drawing { Number = "A-20-001", Title = "Ground floor plan", Scale = 200 },
            new Schedules.Drawing { Number = "A-40-001", Title = "North elevation", Scale = 200 },
            new Schedules.Drawing { Number = "A-40-101", Title = "Section A–A", Scale = 500 },
            new Schedules.Drawing { Number = "A-00-002", Title = "Door, window and room schedule" }
        };
        var table = Schedules.DrawingList(rows);
        Assert.Equal("drawings", table.Kind);
        Assert.Equal("Drawing list", table.Title);
        Assert.Equal(new[] { "No.", "Drawing", "Scale" }, table.Heads);
        Assert.Equal(new[] { "A-00-001", "A-20-001", "A-40-001", "A-40-101", "A-00-002" }, table.Ids);
        Assert.Equal(new[] { "A-20-001", "Ground floor plan", "1:200" }, table.Rows[1]);
        Assert.Equal(new[] { "A-40-101", "Section A–A", "1:500" }, table.Rows[3]);
        Assert.Equal("", table.Rows[4][2]);
    }

    [Fact]
    public void DrawingList_GivesADetailSheet_ItsOwnScale()
    {
        var table = Schedules.DrawingList(new[]
        {
            new Schedules.Drawing { Number = "A-20-001", Title = "Ground floor plan", Scale = 100 },
            new Schedules.Drawing { Number = "A-50-001", Title = Details.SheetTitle(20), Scale = 20 }
        });
        Assert.Equal(new[] { "A-50-001", "Details 1:20", "1:20" }, table.Rows[1]);
    }

    [Fact]
    public void FrontSheet_FlowsOntoOneA3Page_ForTheOffice()
    {
        var sheets = new List<Schedules.Drawing> { new Schedules.Drawing { Number = "A-00-001", Title = "Drawing list and areas" } };
        sheets.Add(new Schedules.Drawing { Number = "A-20-001", Title = "Ground floor plan", Scale = 200 });
        foreach (var (facade, i) in new[] { "North", "East", "South", "West" }.Select((f, i) => (f, i)))
            sheets.Add(new Schedules.Drawing { Number = $"A-40-00{i + 1}", Title = facade + " elevation", Scale = 200 });
        sheets.Add(new Schedules.Drawing { Number = "A-40-101", Title = "Section A–A", Scale = 200 });
        sheets.Add(new Schedules.Drawing { Number = "A-40-102", Title = "Section B–B", Scale = 200 });
        sheets.Add(new Schedules.Drawing { Number = "A-00-002", Title = "Door, window and room schedule (1/2)" });
        sheets.Add(new Schedules.Drawing { Number = "A-00-003", Title = "Door, window and room schedule (2/2)" });
        var tables = new[] { Schedules.DrawingList(sheets), Schedules.AreaTable(OfficeAreas()) };
        var blocks = Schedules.Flow(tables, 400, 254);
        AssertFlowed(tables, blocks, 400, 254);
        Assert.Equal(1, Schedules.Pages(blocks));
        // The note goes under the last block of its table, never in a column of its own.
        Assert.Single(blocks.Where(b => b.Note));
        Assert.Same(tables[1], blocks.Single(b => b.Note).Table);
    }

    [Fact]
    public void Flow_KeepsTheNoteWithTheLastRow_WhenTheColumnRunsOut()
    {
        var table = Rows("window", 10);
        table.Note = "Merknad.";
        // Title, head and exactly ten rows fit, the note does not: the last row goes with the note.
        var height = Schedules.TitleSpaceMm + Schedules.RowMm * 11;
        var blocks = Schedules.Flow(new[] { table }, 400, height);
        AssertFlowed(new[] { table }, blocks, 400, height);
        Assert.Equal(new[] { 9, 1 }, blocks.Select(b => b.Count));
        Assert.Equal(new[] { false, true }, blocks.Select(b => b.Note));
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
        Assert.Equal("Hinged door with glass", Schedules.DoorTable(new[] { glazed }).Rows.Single()[1]);
    }

    [Fact]
    public void EveryType_HasANorwegianScheduleLabel()
    {
        foreach (var def in OpeningTypes.All)
            Assert.False(string.IsNullOrWhiteSpace(def.ScheduleLabel), def.Id);
    }

    [Theory]
    [InlineData(new[] { "door" }, "Door schedule")]
    [InlineData(new[] { "door", "window" }, "Door and window schedule")]
    [InlineData(new[] { "window", "room" }, "Window and room schedule")]
    [InlineData(new[] { "door", "window", "room" }, "Door, window and room schedule")]
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

    /// <summary>Every line of every table exactly once, each block inside its page, none overlapping.</summary>
    static void AssertFlowed(IList<Schedules.Table> tables, List<Schedules.Block> blocks, double width, double height)
    {
        Assert.NotNull(blocks);
        foreach (var table in tables)
        {
            var mine = blocks.Where(b => b.Table == table).ToList();
            Assert.Equal(Enumerable.Range(0, table.Lines), mine.SelectMany(b => Enumerable.Range(b.First, b.Count)));
            Assert.False(mine[0].Continued);
            Assert.All(mine.Skip(1), b => Assert.EndsWith("(cont.)", b.Title));
        }
        foreach (var block in blocks)
        {
            Assert.True(block.Top + block.Height <= height + 1e-9, $"{block.Title} runs off the column");
            Assert.True(block.X + block.Table.Width <= width + 1e-9, $"{block.Title} runs off page {block.Page + 1}");
        }
        foreach (var a in blocks)
            foreach (var b in blocks.Where(b => b != a && b.Page == a.Page && b.X == a.X))
                Assert.True(a.Top + a.Height <= b.Top || b.Top + b.Height <= a.Top, "two blocks overlap");
    }

    [Fact]
    public void Flow_OfficeLists_FitOnePage_EveryLineOnce()
    {
        // 14 doors, 62 windows and 16 rooms above the footer of an A3 sheet.
        var rooms = Enumerable.Range(1, 16)
            .Select(i => new Schedules.Room { Id = $"rd-{i:00}", Name = "Konferanserom", AreaMm2 = 20000000 })
            .ToList();
        var tables = new[] { Rows("door", 14), Rows("window", 62), Schedules.RoomTable(rooms) };
        var blocks = Schedules.Flow(tables, 400, 254);
        AssertFlowed(tables, blocks, 400, 254);
        Assert.Equal(1, Schedules.Pages(blocks));
    }

    [Fact]
    public void Flow_LongLists_RunOntoMorePages()
    {
        // 40 doors and 400 windows do not fit one A3: the lists carry on over pages.
        var tables = new[] { Rows("door", 40), Rows("window", 400) };
        var blocks = Schedules.Flow(tables, 400, 254);
        AssertFlowed(tables, blocks, 400, 254);
        Assert.True(Schedules.Pages(blocks) >= 2, $"{Schedules.Pages(blocks)} page");
        Assert.Equal(Enumerable.Range(0, Schedules.Pages(blocks)), blocks.Select(b => b.Page).Distinct().OrderBy(p => p));
    }

    [Fact]
    public void Flow_GarageLists_StackInOneColumn()
    {
        var rooms = new[] { new Schedules.Room { Id = "rd-01", Name = "Rom", AreaMm2 = 27400000 } };
        var tables = new[] { Rows("door", 4), Rows("window", 1), Schedules.RoomTable(rooms) };
        var blocks = Schedules.Flow(tables, 400, 254);
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(0, b.X));
        Assert.Equal(1, Schedules.Pages(blocks));
    }

    [Fact]
    public void Flow_TooShortForOneLine_OrTooWide_IsNull()
    {
        Assert.Null(Schedules.Flow(new[] { Rows("door", 2) }, 400, Schedules.TitleSpaceMm + Schedules.RowMm));
        Assert.Null(Schedules.Flow(new[] { Rows("door", 2) }, 20, 254));
    }

    [Fact]
    public void Fit_ColumnsHoldTheWidestTextAsMeasured()
    {
        // A measured type name wider than the character estimate sets the column.
        var table = Rows("window", 1);
        Schedules.Fit(table, text => text == "Side-hung window" ? 17.9 : text.Length * 1.0);
        Assert.Equal(Math.Ceiling(17.9 + 2 * Schedules.PadMm), table.Widths[1]);
        // No measure: the estimate still clears that 17.9 mm.
        Schedules.Fit(table, null);
        Assert.True(table.Widths[1] >= 17.9 + 2 * Schedules.PadMm, $"type column {table.Widths[1]} mm");
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
        Assert.Equal(expected, RhinoMCPPlugin.Forsk.ForskIntentRouter.Classify(text));
    }
}
