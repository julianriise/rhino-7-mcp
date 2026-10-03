using System;
using System.Collections.Generic;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Box = RhinoMCPPlugin.Functions.RoomDetect.Box;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// F5.3 sections, headless: what counts as cut and what as beyond, the level
/// values and the free height read off the cut, the line through a room,
/// and the plan marker clear of tags and marks. The garage is the garage
/// smoke's model, drawn here on a 400 slab whose top is at 0: an 8 x 4 m wall
/// band 200 thick and 3000 high under a flat 200 roof with a 500 overhang. The
/// live garage has no slab (see FloorTops_stand_in_with_the_room_floors).
/// </summary>
public class SectionsTests
{
    static Pt P(double x, double y) => new Pt(x, y);

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new List<Pt> { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) };

    /// <summary>The garage's long section in section coordinates (u along the line, z up): slab, both walls, roof.</summary>
    static List<List<Pt>> GarageCut() => new List<List<Pt>>
    {
        Rect(0, -400, 8000, 0),
        Rect(0, 0, 200, 3000),
        Rect(7800, 0, 8000, 3000),
        Rect(-500, 2800, 8500, 3000)
    };

    [Fact]
    public void An_edge_on_a_cut_loop_is_cut()
    {
        var cut = GarageCut();
        // The inner face of the west wall, where the plane cuts it.
        Assert.True(Sections.IsCut(new[] { P(200, 0), P(200, 1500), P(200, 3000) }, cut, 0.5));
        // The roof's underside across the room.
        Assert.True(Sections.IsCut(new[] { P(200, 2800), P(4000, 2800), P(7800, 2800) }, cut, 0.5));
    }

    [Fact]
    public void An_edge_seen_past_the_plane_is_beyond()
    {
        var cut = GarageCut();
        // A window's frame in the far wall, seen through the room.
        Assert.False(Sections.IsCut(new[] { P(4200, 900), P(4800, 900), P(5400, 900) }, cut, 0.5));
        // A door jamb that starts on the slab top and rises into the room:
        // one end on a loop is not enough.
        Assert.False(Sections.IsCut(new[] { P(1200, 0), P(1200, 1050), P(1200, 2100) }, cut, 0.5));
        // An edge on two different loops (slab top to the wall) is not one cut face.
        Assert.False(Sections.IsCut(new[] { P(100, 0), P(100, 1500) }, new List<List<Pt>> { Rect(0, -400, 8000, 0), Rect(300, 0, 500, 3000) }, 0.5));
    }

    [Fact]
    public void FloorTops_stand_in_with_the_room_floors_when_there_is_no_slab()
    {
        Assert.Equal(new[] { 0.0, 3200.0 }, Sections.FloorTops(new[] { 0.0, 3200.0 }, new[] { 10.0 }));
        Assert.Equal(new[] { 0.0 }, Sections.FloorTops(new double[0], new[] { 0.0 }));
        Assert.Empty(Sections.FloorTops(new double[0], new double[0]));
        // The floor mark of a slabless garage sits at the room floor, with the ground at the wall base.
        var levels = Sections.Levels(Sections.FloorTops(new double[0], new[] { 0.0 }), 0, 3000, 3000);
        Assert.Equal(new[] { "floor", "ground", "gesims,mone" }, levels.Select(l => l.Kind));
        Assert.Equal("Ground floor ±0", levels.First(l => l.Kind == "floor").Text);
    }

    [Fact]
    public void Levels_read_the_model_heights_above_the_lowest_floor()
    {
        var levels = Sections.Levels(new[] { 0.0 }, -400, 3000, 3000);
        Assert.Equal(new[] { "ground", "floor", "gesims,mone" }, levels.Select(l => l.Kind));
        Assert.Equal(new[] { -400, 0, 3000 }, levels.Select(l => l.Value));
        Assert.Equal(new[] { "Ground -400", "Ground floor ±0", "Eaves/Ridge +3000" }, levels.Select(l => l.Text));
    }

    [Fact]
    public void Floors_at_one_height_are_one_level_and_a_pitched_roof_has_two_marks()
    {
        var levels = Sections.Levels(new[] { 3200.4, 100.0, 100.5 }, null, 6000, 8200);
        Assert.Equal(new[] { "floor", "floor", "gesims", "mone" }, levels.Select(l => l.Kind));
        Assert.Equal(new[] { 0, 3100, 5900, 8100 }, levels.Select(l => l.Value));
        Assert.Equal("1st floor +3100", levels[1].Text);
    }

    [Fact]
    public void Free_height_is_floor_to_the_underside_over_it()
    {
        var cut = GarageCut();
        Assert.True(Sections.RoomSpan(Rect(200, 200, 7800, 3800), Sections.Along("A", P(0, 2000), P(8000, 2000), null), out var u0, out var u1));
        Assert.Equal(200, u0, 6);
        Assert.Equal(7800, u1, 6);
        Assert.Equal(2800, Sections.FreeHeight(cut, 0.5 * (u0 + u1), 0, 0.5));
        // Nothing over a floor outside the roof.
        Assert.Null(Sections.FreeHeight(cut, 9000, 0, 0.5));
    }

    [Fact]
    public void Free_height_stops_at_a_lower_ceiling_slab()
    {
        var cut = GarageCut();
        cut.Add(Rect(200, 2400, 4000, 2600));
        Assert.Equal(2400, Sections.FreeHeight(cut, 2000, 0, 0.5));
        Assert.Equal(2800, Sections.FreeHeight(cut, 6000, 0, 0.5));
    }

    [Fact]
    public void Roof_heights_from_the_cut_roof()
    {
        Assert.True(Sections.RoofHeights(new List<List<Pt>> { Rect(-500, 2800, 8500, 3000) }, out var gesims, out var mone));
        Assert.Equal(3000, gesims, 6);
        Assert.Equal(3000, mone, 6);
        // A gable: eaves at 3000 and 3100, ridge at 5000.
        var gable = new List<Pt> { P(-500, 2800), P(-500, 3000), P(4000, 5000), P(8500, 3100), P(8500, 2900), P(4000, 4800) };
        Assert.True(Sections.RoofHeights(new List<List<Pt>> { gable }, out gesims, out mone));
        Assert.Equal(3000, gesims, 6);
        Assert.Equal(5000, mone, 6);
        Assert.False(Sections.RoofHeights(new List<List<Pt>>(), out _, out _));
    }

    [Fact]
    public void A_room_section_runs_through_the_room_across_the_footprint()
    {
        var footprint = new Box(0, 0, 8000, 4000);
        var cross = Sections.Through("A", P(3000, 1500), footprint, "cross", null);
        Assert.Equal(new[] { 3000.0, 4000, 3000, 0 }, new[] { cross.A.X, cross.A.Y, cross.B.X, cross.B.Y });
        Assert.Equal(1, cross.Look.X, 9);
        Assert.Equal("cross", cross.Axis);
        var along = Sections.Through("B", P(3000, 1500), footprint, "long", null);
        Assert.Equal(new[] { 0.0, 1500, 8000, 1500 }, new[] { along.A.X, along.A.Y, along.B.X, along.B.Y });
        Assert.Equal(1, along.Look.Y, 9);
        // A deep building: long runs in Y.
        var deep = Sections.Through("C", P(1000, 5000), new Box(0, 0, 4000, 9000), "long", null);
        Assert.Equal(1000, deep.A.X, 9);
        Assert.Equal(1000, deep.B.X, 9);
    }

    [Fact]
    public void A_compass_word_picks_the_side_the_section_looks_at()
    {
        Assert.True(Sections.TryLookAcross(P(0, 0), P(1000, 0), "south", out var look));
        Assert.Equal(-1, look.Y, 9);
        Assert.True(Sections.TryLookAcross(P(0, 0), P(1000, 0), "east", out look));
        Assert.Equal(1, look.Y, 9);
        Assert.True(Sections.TryLookAcross(P(0, 0), P(1000, 1000), "vest", out look));
        Assert.True(look.X < 0 && look.Y > 0);
        Assert.False(Sections.TryLookAcross(P(0, 0), P(0.5, 0), null, out _));
    }

    [Fact]
    public void The_marker_ends_sit_past_the_building_clear_of_tags_and_marks()
    {
        const int scale = 50;
        var outline = Rect(0, 0, 8000, 4000);
        // A room tag on the line's path past the east facade (a leader tag),
        // a mark just past the west facade, and a dimension chain along it.
        var taken = new List<Box>
        {
            new Box(8100, 1700, 9200, 2300),
            new Box(-300, 1900, -150, 2100),
            new Box(-700, 0, -650, 4000)
        };
        var hx = 0.35 * Sections.LetterMm * scale;
        var hy = 0.5 * Sections.LetterMm * scale;
        var marker = Sections.PlaceMarker(P(0, 2000), P(8000, 2000), P(0, 1), new List<List<Pt>> { outline }, taken, scale, hx, hy);
        Assert.True(marker.Clear);
        Assert.Equal(2, marker.Ends.Count);
        Assert.True(marker.Ends[0].StrokeA.X < -700 && marker.Ends[0].StrokeB.X < marker.Ends[0].StrokeA.X);
        Assert.True(marker.Ends[1].StrokeA.X > 9200 && marker.Ends[1].StrokeB.X > marker.Ends[1].StrokeA.X);
        foreach (var end in marker.Ends)
        {
            Assert.Equal(2000, end.StrokeA.Y, 6);
            // The arrow and the letter are on the look side.
            Assert.True(end.Arrow[2].Y > 2000 && end.LetterAt.Y > end.Arrow[2].Y);
            foreach (var box in end.Boxes)
                Assert.DoesNotContain(taken, t => Schedules.Overlaps(box, t, 0));
        }
        Assert.DoesNotContain(marker.Ends[0].Boxes, a => marker.Ends[1].Boxes.Any(b => Schedules.Overlaps(a, b, 0)));
    }

    [Fact]
    public void A_marker_with_no_clear_spot_says_so()
    {
        var wall = new List<Box> { new Box(-1e6, -1e6, 1e6, 1e6) };
        var marker = Sections.PlaceMarker(P(0, 0), P(1000, 0), P(0, 1), new List<List<Pt>> { Rect(0, -100, 1000, 100) }, wall, 100, 30, 60);
        Assert.False(marker.Clear);
        Assert.Equal(2, marker.Ends.Count);
    }

    [Fact]
    public void Close_level_values_step_sideways()
    {
        var levels = Sections.Levels(new[] { 0.0 }, -100, 3000, 3000);
        var marks = Sections.PlaceLevels(levels, z => z, 8000, 100, text => 0.6 * Sections.ValueMm * text.Length);
        Assert.Equal(3, marks.Count);
        for (var i = 0; i < marks.Count; i++)
            for (var j = i + 1; j < marks.Count; j++)
                Assert.False(Schedules.Overlaps(marks[i].TextBox, marks[j].TextBox, 0));
        // Terreng -100 and 1. etg ±0 are 1 mm apart on paper: the second steps right.
        Assert.True(marks[1].TextBox.MinX > marks[0].TextBox.MaxX);
        Assert.Equal(marks[0].TextBox.MinX, marks[2].TextBox.MinX, 6);
        Assert.All(marks, m => Assert.Equal(m.Level.Z, m.Y, 6));
    }

    [Fact]
    public void Floor_and_ground_at_zero_stack()
    {
        // A garage with no slab has both marks at 0. Side by side they read as one line.
        var levels = Sections.Levels(new[] { 0.0 }, 0.0, 3000, 3000);
        var marks = Sections.PlaceLevels(levels, z => z, 8000, 100, text => 0.6 * Sections.ValueMm * text.Length);
        var zero = marks.Where(m => m.Level.Z == 0).ToList();
        Assert.Equal(2, zero.Count);
        Assert.Equal(new[] { "floor", "ground" }, zero.Select(m => m.Level.Kind));
        Assert.Equal(zero[0].TextBox.MinX, zero[1].TextBox.MinX, 6);
        Assert.True(zero[1].TextBox.MinY >= zero[0].TextBox.MaxY);
        Assert.False(Schedules.Overlaps(zero[0].TextBox, zero[1].TextBox, 0));
        Assert.All(zero, m => Assert.Equal(0, m.Y, 6));
        Assert.All(zero, m => Assert.Equal(m.Y, m.LineA.Y, 6));
    }

    [Fact]
    public void Sections_round_trip_through_the_document_string()
    {
        var defs = new List<Sections.Def>
        {
            Sections.Along("B", P(0, 1500), P(8000, 1500), null, "long"),
            Sections.Through("A", P(3000, 1500), new Box(0, 0, 8000, 4000), "cross", null)
        };
        defs[0].Room = "rd-01";
        var back = Sections.Read(Sections.Write(defs));
        Assert.Equal(new[] { "A", "B" }, back.Select(d => d.Letter));
        Assert.Equal("rd-01", back[1].Room);
        Assert.Equal(1500, back[1].A.Y, 6);
        Assert.Equal("C", Sections.NextLetter(back));
        Assert.Empty(Sections.Read("not json"));
    }

    [Fact]
    public void Section_views_name_their_letter()
    {
        Assert.True(Sections.TryLetter("section_a", out var letter));
        Assert.Equal("A", letter);
        Assert.True(Sections.TryLetter(" Section_B ", out letter));
        Assert.Equal("B", letter);
        Assert.False(Sections.TryLetter("section", out _));
        Assert.False(Sections.TryLetter("section_ab", out _));
        Assert.False(Sections.TryLetter("plan", out _));
        Assert.Equal("section_c", Sections.View("C"));
        Assert.Equal("Section A–A", Sections.Title("A"));
        Assert.Equal("Section B–B", OpeningTypes.ViewTitle("section_b", 0));
        Assert.Equal("Forsk — Section A", Sections.PageName("A"));
    }

    [Theory]
    [InlineData("section A through the living room", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("add a long section through the garage", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("tverrsnitt gjennom stua", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("lag snitt A-A", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("print the sections", RhinoMCPPlugin.Forsk.ForskIntent.Print)]
    public void Section_words_classify_as_sheets(string text, RhinoMCPPlugin.Forsk.ForskIntent expected)
    {
        Assert.Equal(expected, RhinoMCPPlugin.Forsk.ForskIntentRouter.Classify(text));
    }

    [Theory]
    [InlineData(5000, 100, 5000, 0)]
    [InlineData(100, 5000, 0, 5000)]
    [InlineData(100, 100, 100, 0)]
    [InlineData(-2000, -2000, -2000, 0)]
    public void A_drag_locks_to_the_nearer_axis(double x, double y, double sx, double sy)
    {
        Assert.True(Sections.TrySnapAxis(P(0, 0), P(x, y), out var snapped));
        Assert.Equal(sx, snapped.X, 6);
        Assert.Equal(sy, snapped.Y, 6);
    }

    [Fact]
    public void A_drag_from_another_point_keeps_the_start_on_the_locked_axis()
    {
        Assert.True(Sections.TrySnapAxis(P(1000, 2000), P(1100, 7000), out var snapped));
        Assert.Equal(1000, snapped.X, 6);
        Assert.Equal(7000, snapped.Y, 6);
        Assert.False(Sections.TrySnapAxis(P(0, 0), P(0.4, 0.9), out _));
    }

    [Fact]
    public void Section_names_count_A_numbers_and_leave_the_letter_alone()
    {
        Assert.Equal("A1", Sections.NextSectionName(null));
        Assert.Equal("A1", Sections.NextSectionName(new[]
        {
            new Sections.Def { Letter = "A", Name = "A" },
            new Sections.Def { Name = "A0" }
        }));
        Assert.Equal("A2", Sections.NextSectionName(new[]
        {
            new Sections.Def { Letter = "B", Name = "A1" },
            new Sections.Def { Name = "a3" }
        }));
        Assert.Equal("A2", Sections.NextSectionName(new[]
        {
            new Sections.Def { Name = "A1" },
            new Sections.Def { Name = "A10" }
        }));
        var oneThroughTen = new List<Sections.Def>();
        for (var i = 1; i <= 10; i++)
            oneThroughTen.Add(new Sections.Def { Name = "A" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        Assert.Equal("A11", Sections.NextSectionName(oneThroughTen));
        var existing = new[] { new Sections.Def { Letter = "B", Name = "A1" } };
        Assert.Equal("A", Sections.LetterFor("A1", existing));
        Assert.Equal("A", Sections.LetterFor("Kitchen", existing));
        Assert.Equal("B", Sections.LetterFor("b", existing));
        Assert.False(Sections.TryLetter("section_ab", out _));

        var stored = Sections.Along("A", P(0, 0), P(1000, 0), null);
        stored.Name = "A1";
        var back = Sections.Read(Sections.Write(new[] { stored }));
        Assert.Equal("A", back[0].Letter);
        Assert.Equal("A1", back[0].Name);
        Assert.Equal("A2", Sections.NextSectionName(back));
        var legacy = Sections.Read("[{\"letter\":\"C\",\"a\":[0,0],\"b\":[0,2000],\"look\":[-1,0]}]");
        Assert.Equal("", legacy[0].Name);
        Assert.Equal("A1", Sections.NextSectionName(legacy));
    }

    [Theory]
    [InlineData("add cross section", true)]
    [InlineData("Please add a cross section now", true)]
    [InlineData("Add  Cross   Section", true)]
    [InlineData("new cross section", true)]
    [InlineData("draw cross section", true)]
    [InlineData("section A through the living room", false)]
    [InlineData("add a long section through the garage", false)]
    [InlineData("tverrsnitt gjennom stua", false)]
    public void Add_cross_section_is_the_viewport_phrase(string text, bool pick)
    {
        Assert.Equal(pick, Sections.IsPickPhrase(text));
    }

    [Fact]
    public void Cancelling_the_line_or_the_name_stores_nothing()
    {
        var defs = new List<Sections.Def> { new Sections.Def { Letter = "A", Name = "A1" } };
        Assert.Null(Sections.Decide(false, "A2", defs, P(0, 0), P(4000, 10)));
        Assert.Null(Sections.Decide(true, null, defs, P(0, 0), P(4000, 10)));
        var blank = Sections.Decide(true, "  ", defs, P(0, 0), P(10, 4000));
        Assert.Equal("A2", blank.Name);
        Assert.Equal("B", blank.Letter);
        Assert.Equal(0, blank.B.X, 6);
        Assert.Equal(4000, blank.B.Y, 6);
        var shortLine = Sections.Decide(true, "A2", defs, P(0, 0), P(0.2, 0));
        Assert.Equal(Sections.TooShortMessage, shortLine.Error);
        Assert.Null(shortLine.Letter);

        var full = new List<Sections.Def>();
        for (var c = 'A'; c <= 'Z'; c++)
            full.Add(new Sections.Def { Letter = c.ToString() });
        var blocked = Sections.Decide(true, "Kitchen", full, P(0, 0), P(4000, 10));
        Assert.Equal(Sections.LettersFullMessage, blocked.Error);
        Assert.Null(blocked.Letter);
        var replaced = Sections.Decide(true, "C", full, P(0, 0), P(10, 4000));
        Assert.Equal("C", replaced.Letter);
        Assert.Null(replaced.Error);
    }
}
