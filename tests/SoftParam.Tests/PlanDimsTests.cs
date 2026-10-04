using System;
using System.Collections.Generic;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;
using Box = RhinoMCPPlugin.Functions.RoomDetect.Box;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// F5.2 plan dimensions, headless: every value is the model's own length (a
/// facade chain's points are its corners and the opening centres the record
/// holds), each chain adds up to its total, the sides' overalls are the
/// outline's extents, and nothing placed touches a tag, a mark, the poché or
/// another dimension. The garage is the garage smoke's wall band and doors;
/// the office is the office smoke's DXF.
/// </summary>
public class PlanDimsTests
{
    static Pt P(double x, double y) => new Pt(x, y);

    static List<Pt> Rect(double x0, double y0, double x1, double y1) =>
        new List<Pt> { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) };

    /// <summary>The garage smoke's plan: an 8 x 4 m band 200 thick, the four doors and the high window on the south wall.</summary>
    static PlanDims.Scene Garage(int scale)
    {
        var scene = new PlanDims.Scene { Scale = scale };
        scene.Outlines.Add(Rect(0, 0, 8000, 4000));
        scene.Walls.Add(new List<List<Pt>> { Rect(0, 0, 8000, 4000), Rect(200, 200, 7800, 3800) });
        foreach (var (id, x) in new[] { ("door", 1200.0), ("flip", 2300.0), ("sliding", 3400.0), ("window", 4800.0), ("pocket", 7280.0) })
            scene.Openings.Add(new PlanDims.Opening { Id = id, Centre = P(x, 100), Along = P(1, 0), HalfThick = 100 });
        scene.Rooms.Add(new PlanDims.Room { Id = "rd-01", Ring = Rect(200, 200, 7800, 3800) });
        return scene;
    }

    static List<int> Values(PlanDims.Chain chain) => chain.Texts.Select(t => t.Value).ToList();

    static PlanDims.Chain One(PlanDims.Result result, string kind, string side) =>
        result.Chains.Single(c => c.Kind == kind && c.Side == side);

    [Fact]
    public void Garage_SouthChain_IsCornersAndOpeningCentres()
    {
        var result = PlanDims.Layout(Garage(30));
        var south = One(result, "facade", "S");
        Assert.True(south.Placed);
        Assert.Equal(new[] { 1200, 1100, 1100, 1400, 2480, 720 }, Values(south));
        Assert.Equal(8000, south.Total);
        Assert.Equal(new string[] { null, "door", "flip", "sliding", "window", "pocket", null }, south.StopIds);
        Assert.Equal(5, result.Openings);
        Assert.Equal(5, result.OpeningsShown);
        // South of the wall, reading left to right.
        Assert.All(south.Texts, t => Assert.True(t.Centre.Y < 0));
        Assert.All(south.Texts, t => Assert.Equal(P(1, 0), t.Reading));
    }

    [Fact]
    public void Garage_Sides_HaveOverallsAndNoJogs_RoomHasWidthAndDepth()
    {
        var result = PlanDims.Layout(Garage(30));
        Assert.Equal(new[] { 8000 }, Values(One(result, "overall", "S")));
        Assert.Equal(new[] { 4000 }, Values(One(result, "overall", "E")));
        Assert.Equal(new[] { 8000 }, Values(One(result, "overall", "N")));
        Assert.Equal(new[] { 4000 }, Values(One(result, "overall", "W")));
        Assert.DoesNotContain(result.Chains, c => c.Kind == "jog");
        Assert.DoesNotContain(result.Chains, c => c.Kind == "facade" && c.Side != "S");
        var room = result.Chains.Where(c => c.Kind == "room").ToList();
        Assert.Equal(new[] { 7600, 3600 }, room.Select(c => c.Total));
        Assert.All(room, c => Assert.True(c.Placed && !c.Witness));
        Assert.Equal(1, result.Rooms);
        Assert.Equal(0, result.Collisions);
        // The facade chain is nearer the wall than the overall on the same side.
        Assert.True(One(result, "facade", "S").Offset < One(result, "overall", "S").Offset);
        AssertNothingTouches(result, new List<PlanDims.Obstacle>(), Garage(30).Walls);
    }

    [Fact]
    public void Garage_ChainStepsOutPastAMarkAndASwing()
    {
        var scene = Garage(30);
        // A mark 2 mm outside the wall and the flipped door's swing out to 900 mm.
        var mark = new Box(1100, -60 - 30, 1300, -60 + 30);
        var swing = new Box(1850, -900, 2750, 0);
        scene.Taken.Add(new PlanDims.Obstacle(mark, PlanDims.Kind.Text));
        scene.Taken.Add(new PlanDims.Obstacle(swing, PlanDims.Kind.Line));
        var result = PlanDims.Layout(scene);
        var south = One(result, "facade", "S");
        Assert.True(south.Placed);
        Assert.True(south.Offset > 900 + PlanDims.WitnessInMm * 30, $"offset {south.Offset}");
        AssertNothingTouches(result, scene.Taken, scene.Walls);
    }

    [Fact]
    public void ShortSegment_ValueGoesAboveOrBeside_AndStaysClear()
    {
        // 150 mm between two openings at 1:100 is 1.5 mm on paper: no room for "150".
        var scene = new PlanDims.Scene { Scale = 100 };
        scene.Outlines.Add(Rect(0, 0, 6000, 3000));
        scene.Walls.Add(new List<List<Pt>> { Rect(0, 0, 6000, 3000), Rect(200, 200, 5800, 2800) });
        scene.Openings.Add(new PlanDims.Opening { Id = "a", Centre = P(2000, 100), Along = P(1, 0), HalfThick = 100 });
        scene.Openings.Add(new PlanDims.Opening { Id = "b", Centre = P(2150, 100), Along = P(1, 0), HalfThick = 100 });
        var result = PlanDims.Layout(scene);
        var south = One(result, "facade", "S");
        Assert.Equal(new[] { 2000, 150, 3850 }, Values(south));
        Assert.Equal(0, south.Texts[0].Level);
        // Not squeezed between its own ticks: lifted a row, or past a tick.
        var along = south.Texts[1].Centre.X;
        Assert.True(south.Texts[1].Level > 0 || along < 2000 || along > 2150, $"150 at {along:0} level {south.Texts[1].Level}");
        Assert.All(south.Texts, t => Assert.True(t.Clear));
        AssertNothingTouches(result, scene.Taken, scene.Walls);
    }

    [Theory]
    [InlineData(125)]
    [InlineData(200)]
    public void BesideValue_KeepsOffTheNextTick_AtEveryScale(int scale)
    {
        // A short segment's value goes past a tick, over the next segment;
        // that one's far tick must not be under it (office 1:200: facade-E-1 1080).
        for (var near = 300; near <= 1400; near += 50)
        for (var next = 600; next <= 2000; next += 50)
        {
            var scene = new PlanDims.Scene { Scale = scale };
            scene.Outlines.Add(Rect(0, 0, 20000, 10000));
            scene.Walls.Add(new List<List<Pt>> { Rect(0, 0, 20000, 10000), Rect(200, 200, 19800, 9800) });
            foreach (var x in new[] { 5000.0, 5000 + near, 5000 + near + next })
                scene.Openings.Add(new PlanDims.Opening { Id = "o" + x, Centre = P(x, 100), Along = P(1, 0), HalfThick = 100 });
            AssertNothingTouches(PlanDims.Layout(scene), scene.Taken, scene.Walls);
        }
    }

    [Fact]
    public void RoomTooSmallForItsValues_GetsNone()
    {
        // A 600 x 500 mm cupboard at 1:200 is 3 x 2.5 mm on paper.
        var scene = new PlanDims.Scene { Scale = 200 };
        scene.Rooms.Add(new PlanDims.Room { Id = "rd-09", Ring = Rect(0, 0, 600, 500) });
        var result = PlanDims.Layout(scene);
        Assert.Equal(1, result.Rooms);
        Assert.All(result.Chains, c => Assert.False(c.Placed));
    }

    [Fact]
    public void RoomDimension_KeepsOffTheTag()
    {
        // The tag fills the room's middle band; the width goes near a wall.
        var scene = new PlanDims.Scene { Scale = 100 };
        scene.Rooms.Add(new PlanDims.Room { Id = "rd-02", Ring = Rect(0, 0, 4000, 3000) });
        var tag = new Box(1000, 1200, 3000, 1800);
        scene.Taken.Add(new PlanDims.Obstacle(tag, PlanDims.Kind.Text));
        var result = PlanDims.Layout(scene);
        var room = result.Chains.Where(c => c.Kind == "room").ToList();
        Assert.Equal(new[] { 4000, 3000 }, room.Select(c => c.Total));
        Assert.All(room, c => Assert.True(c.Placed));
        AssertNothingTouches(result, scene.Taken, scene.Walls);
    }

    [Fact]
    public void Leader_TakesTheNearestClearSideOutsideTheRoom()
    {
        // A 1.3 x 1.1 m wet room at 1:125; its tag is 14 x 6 mm on paper.
        var ring = Rect(0, 0, 1300, 1100);
        var inside = P(650, 550);
        var scale = 125;
        double hx = 7 * scale, hy = 3 * scale;
        var walls = new List<List<List<Pt>>> { new List<List<Pt>> { Rect(-200, -200, 1500, 1300), ring } };
        // Another room's tag to the right.
        var taken = new List<PlanDims.Obstacle> { new PlanDims.Obstacle(new Box(1500, 0, 4000, 1100), PlanDims.Kind.Text) };
        Assert.True(PlanDims.PlaceLeader(inside, ring, hx, hy, scale, taken, walls, out var centre, out var leader));
        var box = new Box(centre.X - hx, centre.Y - hy, centre.X + hx, centre.Y + hy);
        Assert.False(RoomDetect.Contains(ring, centre));
        Assert.DoesNotContain(taken, o => Schedules.Overlaps(box, o.Box, 0));
        Assert.False(Schedules.OnWalls(box, walls));
        Assert.Equal(inside, leader.A);
        // The leader stops short of the tag, on its near edge.
        var gap = Math.Min(
            Math.Min(Math.Abs(leader.B.X - box.MinX), Math.Abs(leader.B.X - box.MaxX)),
            Math.Min(Math.Abs(leader.B.Y - box.MinY), Math.Abs(leader.B.Y - box.MaxY)));
        Assert.True(gap <= PlanDims.TextGapMm * scale + 1e-6);
    }

    [Fact]
    public void Leader_BoxedIn_SaysSo()
    {
        var ring = Rect(0, 0, 1300, 1100);
        var taken = new List<PlanDims.Obstacle> { new PlanDims.Obstacle(new Box(-9000, -9000, 9000, 9000), PlanDims.Kind.Text) };
        Assert.False(PlanDims.PlaceLeader(P(650, 550), ring, 800, 400, 125, taken, new List<List<List<Pt>>>(), out _, out _));
    }

    /// <summary>
    /// The office smoke's rooms whose names did not fit, with the name widths
    /// the live run measured: each gets a leader, none overflows. rd-10's only
    /// clear side is the corridor, about 0.5 mm wider on paper at 1:200 than
    /// the tag and its clearance.
    /// </summary>
    [Theory]
    [InlineData(125, "rd-03 2020 rd-10 1710")]
    [InlineData(200, "rd-03 3230 rd-04 3070 rd-16 4650 rd-10 2730")]
    public void Office_EveryTagThatDoesNotFit_GetsALeader(int scale, string names)
    {
        var (scene, _, _) = Office(scale);
        var (rooms, _, _) = OfficeRoomsTests.Office();
        var found = RoomDetect.Detect(rooms);
        var ids = RoomDetect.Match(found.Rooms, new List<KeyValuePair<string, List<Pt>>>(), "rd-");
        var parts = names.Split(' ');
        var loose = Enumerable.Range(0, parts.Length / 2)
            .Select(i => (Id: parts[2 * i], Width: double.Parse(parts[2 * i + 1]), Ring: found.Rooms[Array.IndexOf(ids, parts[2 * i])].Ring))
            .ToList();
        // Their own tags are not down; the other rooms' are.
        var taken = scene.Taken
            .Where(o => !loose.Any(l => RoomDetect.Contains(l.Ring, P((o.Box.MinX + o.Box.MaxX) / 2, (o.Box.MinY + o.Box.MaxY) / 2))))
            .ToList();
        // A tag is its name over ca. X m², 2.5 mm text 1.15 heights apart.
        var hy = (2.5 * 1.15 + 2.5) * scale / 2.0;
        foreach (var (id, width, ring) in loose)
        {
            Assert.True(RoomDetect.TryInside(new List<List<Pt>> { ring }, out var inside));
            Assert.True(PlanDims.PlaceLeader(inside, ring, width / 2.0, hy, scale, taken, scene.Walls, out var centre, out var leader),
                $"{id} at 1:{scale} has no leader");
            var box = new Box(centre.X - width / 2.0, centre.Y - hy, centre.X + width / 2.0, centre.Y + hy);
            Assert.DoesNotContain(taken, o => Schedules.Overlaps(box, o.Box, 0));
            Assert.False(Schedules.OnWalls(box, scene.Walls), $"{id} on the poché");
            Assert.False(RoomDetect.Contains(ring, centre), $"{id} in its room");
            Assert.True(RoomDetect.Contains(ring, leader.A));
            taken.Add(new PlanDims.Obstacle(box, PlanDims.Kind.Text));
            taken.Add(new PlanDims.Obstacle(PlanDims.SegBox(leader), PlanDims.Kind.Line));
        }
    }

    /// <summary>
    /// The office smoke's plan from its DXF: the outer wall ring, the wall
    /// rings as poché, every window at its insert (the wall centre) and door
    /// rectangle, a tag box in each room and a mark box outside each opening.
    /// </summary>
    static (PlanDims.Scene Scene, List<Pt> Outer, List<PlanDims.Opening> Windows) Office(int scale)
    {
        var (_, _, rings) = OfficeRoomsTests.Office();
        var outer = rings.OrderByDescending(r => Math.Abs(RoomDetect.Area(r))).First();
        var scene = new PlanDims.Scene { Scale = scale };
        scene.Outlines.Add(outer);
        scene.Walls.Add(new[] { outer }.Concat(rings.Where(r => r != outer)).ToList());
        var windows = new List<PlanDims.Opening>();
        var entities = OfficeRoomsTests.Entities(OfficeRoomsTests.OfficePath);
        foreach (var e in entities)
        {
            PlanDims.Opening opening = null;
            if (OfficeRoomsTests.On(e, "window") && e.Type == "INSERT")
            {
                var turn = e.Rotation * Math.PI / 180.0;
                // The WINDOW block is 100 x 10 about its origin: 10 * Y scale is the wall.
                opening = new PlanDims.Opening
                {
                    Id = "V" + (windows.Count + 1), Centre = e.Points[0],
                    Along = P(Math.Round(Math.Cos(turn), 9), Math.Round(Math.Sin(turn), 9)), HalfThick = 5 * e.ScaleY
                };
                windows.Add(opening);
            }
            else if (OfficeRoomsTests.On(e, "door") && e.Type == "LWPOLYLINE")
            {
                var w = e.Points.Max(p => p.X) - e.Points.Min(p => p.X);
                var h = e.Points.Max(p => p.Y) - e.Points.Min(p => p.Y);
                opening = new PlanDims.Opening
                {
                    Id = "D" + scene.Openings.Count, Centre = P(e.Points.Average(p => p.X), e.Points.Average(p => p.Y)),
                    Along = w >= h ? P(1, 0) : P(0, 1), HalfThick = 100
                };
            }
            if (opening == null) continue;
            scene.Openings.Add(opening);
            // Its mark, 1.25 mm text 2 mm outside the wall on the side away from the building.
            var across = P(-opening.Along.Y, opening.Along.X);
            var probe = P(opening.Centre.X + across.X * 400, opening.Centre.Y + across.Y * 400);
            var sign = RoomDetect.Contains(outer, probe) ? -1 : 1;
            var reach = opening.HalfThick + 2.6 * scale;
            var at = P(opening.Centre.X + sign * across.X * reach, opening.Centre.Y + sign * across.Y * reach);
            scene.Taken.Add(new PlanDims.Obstacle(new Box(at.X - 1.5 * scale, at.Y - 0.8 * scale, at.X + 1.5 * scale, at.Y + 0.8 * scale), PlanDims.Kind.Text));
        }
        foreach (var ring in rings.Where(r => r != outer))
        {
            scene.Rooms.Add(new PlanDims.Room { Id = "ring" + scene.Rooms.Count, Ring = ring });
            if (!RoomDetect.TryInside(new List<List<Pt>> { ring }, out var inside)) continue;
            // A two-line tag, 2.5 mm text, about 12 mm wide.
            scene.Taken.Add(new PlanDims.Obstacle(new Box(inside.X - 6 * scale, inside.Y - 2 * scale, inside.X + 6 * scale, inside.Y + 4 * scale), PlanDims.Kind.Text));
        }
        return (scene, outer, windows);
    }

    [Theory]
    [InlineData(125)]
    [InlineData(200)]
    public void Office_EveryValueIsTheModel_AndChainsAddUp(int scale)
    {
        var (scene, outer, windows) = Office(scale);
        var result = PlanDims.Layout(scene);

        // 61 windows sit in the outer walls (two are in inner walls); each is a point of a facade chain.
        var shown = result.Chains.Where(c => c.Kind == "facade" && c.Placed).SelectMany(c => c.StopIds).Where(id => id != null).ToList();
        Assert.Equal(61, windows.Count(w => shown.Contains(w.Id)));
        Assert.Equal(result.Openings, result.OpeningsShown);

        foreach (var chain in result.Chains.Where(c => c.Placed))
        {
            Assert.Equal(chain.Total, Values(chain).Sum());
            // Each value is the distance between the points it spans, to the mm.
            foreach (var label in chain.Texts)
            {
                var span = Math.Sqrt(Math.Pow(label.To.X - label.From.X, 2) + Math.Pow(label.To.Y - label.From.Y, 2));
                Assert.True(Math.Abs(span - label.Value) <= 1.0, $"{chain.Id} {label.Value} spans {span:0.0}");
            }
        }
        // A facade chain's opening points are the window centres, projected on the face.
        foreach (var chain in result.Chains.Where(c => c.Kind == "facade"))
        {
            foreach (var label in chain.Texts)
            {
                foreach (var (id, at) in new[] { (label.FromId, label.From), (label.ToId, label.To) })
                {
                    var window = windows.FirstOrDefault(w => w.Id == id);
                    if (window == null) continue;
                    var off = (window.Centre.X - at.X) * chain.Dir.X + (window.Centre.Y - at.Y) * chain.Dir.Y;
                    Assert.True(Math.Abs(off) < 0.5, $"{id} is {off:0.0} mm along from its point");
                }
            }
        }

        // The overall on every side is the outline's extent; the jogs add up to it.
        var width = outer.Max(p => p.X) - outer.Min(p => p.X);
        var depth = outer.Max(p => p.Y) - outer.Min(p => p.Y);
        Assert.Equal(30243, (int)width);
        Assert.Equal(22510, (int)depth);
        Assert.Equal(new[] { 30243 }, Values(One(result, "overall", "S")));
        Assert.Equal(new[] { 30243 }, Values(One(result, "overall", "N")));
        Assert.Equal(new[] { 22510 }, Values(One(result, "overall", "E")));
        Assert.Equal(new[] { 22510 }, Values(One(result, "overall", "W")));
        Assert.Equal(new[] { 5188, 15065, 9990 }, Values(One(result, "jog", "S")));
        Assert.Equal(new[] { 10010, 12500 }, Values(One(result, "jog", "W")));
        Assert.DoesNotContain(result.Chains, c => c.Kind == "jog" && (c.Side == "N" || c.Side == "E"));
        // The north facade runs the full width, so its chain adds up to the overall.
        Assert.Equal(30243, One(result, "facade", "N").Total);

        // Ten rooms are rectangles (three Kontor, three WC, Bøttekott, Data/arkiv,
        // Wet Room, and the Konferanserom whose ring has straight-through points;
        // the other has a notch). A room's values are its wall-to-wall sizes.
        Assert.Equal(10, result.Rooms);
        foreach (var chain in result.Chains.Where(c => c.Kind == "room" && c.Placed))
        {
            var ring = scene.Rooms.Single(r => r.Id == chain.Side).Ring;
            var w = ring.Max(p => p.X) - ring.Min(p => p.X);
            var h = ring.Max(p => p.Y) - ring.Min(p => p.Y);
            Assert.Contains(chain.Total, new[] { (int)Math.Round(w), (int)Math.Round(h) });
        }
        Assert.Contains(result.Chains, c => c.Kind == "room" && c.Placed);

        var stuck = result.Chains.Where(c => c.Placed && c.Collisions > 0)
            .Select(c => c.Id + " " + string.Join(",", c.Texts.Where(t => !t.Clear).Select(t => t.Text)));
        Assert.True(result.Collisions == 0, string.Join("; ", stuck));
        Assert.All(result.Chains.Where(c => c.Kind != "room"), c => Assert.True(c.Placed, c.Id));
        AssertNothingTouches(result, scene.Taken, scene.Walls);
    }

    /// <summary>
    /// No value touches another value, any dimension line, witness or tick,
    /// a tag, a mark or the poché. No dimension line or witness touches a tag,
    /// a mark or a symbol, or runs along another chain's line; crossing one
    /// (an inner corner, a room's width and depth) is allowed.
    /// </summary>
    static void AssertNothingTouches(PlanDims.Result result, List<PlanDims.Obstacle> drawn, List<List<List<Pt>>> walls)
    {
        var chains = result.Chains.Where(c => c.Placed).ToList();
        var texts = chains.SelectMany(c => c.Texts.Select(t => (Chain: c, Text: t))).ToList();
        var marks = chains.SelectMany(c => c.Lines.Concat(c.Ticks).Select(l => (Chain: c, Box: PlanDims.SegBox(l)))).ToList();
        for (var i = 0; i < texts.Count; i++)
        {
            var box = texts[i].Text.Box;
            Assert.False(Schedules.OnWalls(box, walls), $"{texts[i].Chain.Id} {texts[i].Text.Text} on the poché");
            for (var j = i + 1; j < texts.Count; j++)
                Assert.False(Schedules.Overlaps(box, texts[j].Text.Box, 0), $"{texts[i].Text.Text} on {texts[j].Text.Text}");
            foreach (var line in marks)
                Assert.False(Schedules.Overlaps(box, line.Box, 0), $"{texts[i].Chain.Id} {texts[i].Text.Text} on a line of {line.Chain.Id}");
            foreach (var o in drawn)
                Assert.False(Schedules.Overlaps(box, o.Box, 0), $"{texts[i].Chain.Id} {texts[i].Text.Text} on a drawn {o.Kind}");
        }
        foreach (var chain in chains)
        {
            foreach (var line in chain.Lines.Select(PlanDims.SegBox))
            {
                foreach (var o in drawn.Where(o => o.Kind == PlanDims.Kind.Text || o.Kind == PlanDims.Kind.Line))
                    Assert.False(Schedules.Overlaps(line, o.Box, 0), $"a line of {chain.Id} on a drawn {o.Kind}");
                foreach (var other in chains.Where(c => c != chain))
                    foreach (var theirs in other.Lines.Select(PlanDims.SegBox))
                        Assert.False(Schedules.Overlaps(line, theirs, 0) && PlanDims.Along(line, theirs), $"{chain.Id} runs along {other.Id}");
            }
        }
    }

    [Theory]
    [InlineData("dimension the plan", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("add dimensions", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("målsett planen", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("vis målkjeder", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("print with dimensions", RhinoMCPPlugin.Forsk.ForskIntent.Print)]
    [InlineData("remove the dimensions", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    [InlineData("fjern målene", RhinoMCPPlugin.Forsk.ForskIntent.Sheets)]
    public void DimensionWords_ClassifyAsSheets(string text, RhinoMCPPlugin.Forsk.ForskIntent expected)
    {
        Assert.Equal(expected, RhinoMCPPlugin.Forsk.ForskIntentRouter.Classify(text));
    }

    [Theory]
    [InlineData("change the dimensions of this window")]
    [InlineData("endre målsetting på døren")]
    public void AnOpeningsSize_IsNotThePlansDimensions(string text)
    {
        Assert.NotEqual(RhinoMCPPlugin.Forsk.ForskIntent.Sheets, RhinoMCPPlugin.Forsk.ForskIntentRouter.Classify(text));
    }
}
