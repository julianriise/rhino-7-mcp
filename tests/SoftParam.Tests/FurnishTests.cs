using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// FU.7: furnishing a room is rules, not guesses. The same room gives the
/// same layout, nothing overlaps, every piece stands inside the room, and no
/// piece stands in a door's swing.
/// </summary>
public class FurnishTests
{
    static Pt[] Box(double w, double d) => new[] { new Pt(0, 0), new Pt(w, 0), new Pt(w, d), new Pt(0, d) };

    static Furnish.Opening Door(double x, double y, double w = 900) => new Furnish.Opening { Centre = new Pt(x, y), Width = w, Door = true };

    static Furnish.Opening Window(double x, double y, double w = 1200) => new Furnish.Opening { Centre = new Pt(x, y), Width = w };

    static Furnish.Layout Plan(string type, Pt[] room, params Furnish.Opening[] openings) =>
        Furnish.Plan(type, room, openings, new List<Furnish.Item>());

    /// <summary>Inside the room, nothing overlapping, and no body in front of a door (900 square on the room side).</summary>
    static void Sound(Furnish.Layout layout, Pt[] room, params Furnish.Opening[] openings)
    {
        Assert.Null(layout.Why);
        var placed = new List<Furniture.Footprint>();
        foreach (var item in layout.Items)
        {
            var body = Furniture.FootprintOf(item.Piece, item.Frame);
            Assert.Null(Furniture.Clash(body, room, placed));
            placed.Add(body);
        }
        foreach (var door in openings.Where(o => o.Door))
        {
            // The door's square on the inside: the room's edge it sits on, the room side.
            var inside = room.Average(p => p.X) > door.Centre.X ? 1 : -1;
            var swing = new Furniture.Footprint
            {
                Corners = door.Centre.Y == 0
                    ? new[] { new Pt(door.Centre.X - 450, 0), new Pt(door.Centre.X + 450, 0), new Pt(door.Centre.X + 450, 900), new Pt(door.Centre.X - 450, 900) }
                    : new[] { new Pt(door.Centre.X, door.Centre.Y - 450), new Pt(door.Centre.X + inside * 900, door.Centre.Y - 450), new Pt(door.Centre.X + inside * 900, door.Centre.Y + 450), new Pt(door.Centre.X, door.Centre.Y + 450) },
                Z0 = 0,
                Z1 = 2100,
                Label = "door"
            };
            foreach (var item in layout.Items)
                Assert.Null(Furniture.Clash(Furniture.FootprintOf(item.Piece, item.Frame), room, new[] { swing }));
        }
    }

    static string Key(Furnish.Layout layout) =>
        string.Join("|", layout.Items.Select(i => i.Piece.Id + "@" + i.Frame.Ox.ToString("0.#") + "," + i.Frame.Oy.ToString("0.#") + "," + i.Frame.Degrees.ToString("0.#")));

    [Fact]
    public void ABedroom_GetsADoubleBed_BedsideTables_AndAWardrobe_ClearOfTheDoor_TheSameEveryTime()
    {
        var room = Box(3200, 3800);
        var openings = new[] { Door(700, 0), Window(1600, 3800) };
        var layout = Plan(RoomTypes.Bedroom, room, openings);
        Sound(layout, room, openings);
        var bed = layout.Items.First(i => i.Piece.Type == "bed");
        Assert.Equal("bed.double.160x200", bed.Piece.Id);
        Assert.Equal(2, layout.Items.Count(i => i.Piece.Type == "bedside_table"));
        Assert.Contains(layout.Items, i => i.Piece.Type == "wardrobe");
        // The head is not on the door's wall or the window's.
        Assert.True(bed.Frame.Oy > 1 && bed.Frame.Oy < 3799, bed.Frame.Oy.ToString());
        Assert.Equal(Key(layout), Key(Plan(RoomTypes.Bedroom, room, openings)));
    }

    [Fact]
    public void TheCreativeVariant_PutsTheBedOnAnotherWall()
    {
        var room = Box(3200, 3800);
        var openings = new[] { Door(700, 0) };
        var usual = Plan(RoomTypes.Bedroom, room, openings);
        var other = Furnish.Plan(RoomTypes.Bedroom, room, openings, new List<Furnish.Item>(), Furnish.Relaxed, Furnish.Creative);
        Sound(other, room, openings);
        var a = usual.Items.First(i => i.Piece.Type == "bed").Frame;
        var b = other.Items.First(i => i.Piece.Type == "bed").Frame;
        Assert.NotEqual(Math.Round(a.Degrees), Math.Round(b.Degrees));
    }

    [Fact]
    public void ABedroomTooSmallForABed_IsRefused_WithWhy()
    {
        var layout = Plan(RoomTypes.Bedroom, Box(2000, 2400), Door(500, 0));
        Assert.StartsWith("no wall in the room holds even a 90 bed with 0.6 m beside it and at its foot", layout.Why);
        Assert.Empty(layout.Items);
    }

    [Fact]
    public void ACompactDensity_TakesASmallerBed()
    {
        var room = Box(3200, 3800);
        var layout = Furnish.Plan(RoomTypes.Bedroom, room, new[] { Door(700, 0) }, new List<Furnish.Item>(), Furnish.Compact);
        Assert.Equal("bed.double.140x200", layout.Items.First(i => i.Piece.Type == "bed").Piece.Id);
    }

    [Fact]
    public void AKitchen_GetsARunWithFridgeHobAndSink_OnAWallWithoutTheDoor()
    {
        var room = Box(3600, 3000);
        var openings = new[] { Door(0, 1500), Window(1800, 3000) };
        var layout = Plan(RoomTypes.Kitchen, room, openings);
        Sound(layout, room, openings);
        foreach (var type in new[] { "fridge", "hob", "sink", "dishwasher", "kitchen_base", "kitchen_wall" })
            Assert.Contains(layout.Items, i => i.Piece.Type == type);
        // The run is one wall: every unit's back on the same line.
        var run = layout.Items.Where(i => i.Piece.Fixed && i.Piece.Type != "kitchen_wall").ToList();
        Assert.Single(run.Select(i => Math.Round(i.Frame.Degrees)).Distinct());
    }

    [Fact]
    public void ABathroom_GetsAShowerAWcAndABasin_WithTheWcSideSpaceKept()
    {
        var room = Box(2000, 2400);
        var openings = new[] { Door(1000, 0, 800) };
        var layout = Plan(RoomTypes.Bathroom, room, openings);
        Sound(layout, room, openings);
        Assert.Contains(layout.Items, i => i.Piece.Type == "shower");
        Assert.Contains(layout.Items, i => i.Piece.Type == "wc");
        // 0.2 m each side of the WC is free of other bodies.
        var wc = layout.Items.First(i => i.Piece.Type == "wc");
        var sides = new Furniture.Footprint
        {
            Corners = new[] { wc.Frame.ToWorld(-wc.Piece.W / 2 - 195, 5), wc.Frame.ToWorld(wc.Piece.W / 2 + 195, 5), wc.Frame.ToWorld(wc.Piece.W / 2 + 195, wc.Piece.D), wc.Frame.ToWorld(-wc.Piece.W / 2 - 195, wc.Piece.D) },
            Z0 = 0, Z1 = 1000, Label = "wc sides"
        };
        foreach (var item in layout.Items.Where(i => i != wc))
            Assert.Null(Furniture.Clash(Furniture.FootprintOf(item.Piece, item.Frame), room, new[] { sides }));
    }

    [Fact]
    public void ALivingRoom_GetsASofaFacingTheWindow_AndACoffeeTableInFront()
    {
        var room = Box(4200, 5000);
        var openings = new[] { Door(4200, 800), Window(2100, 5000, 2000) };
        var layout = Plan(RoomTypes.Living, room, openings);
        Sound(layout, room, openings);
        var sofa = layout.Items.First(i => i.Piece.Type == "sofa");
        // The window is on the north wall: the sofa's back is on the south wall, looking north.
        Assert.Equal(0, sofa.Frame.Oy, 6);
        Assert.Contains(layout.Items, i => i.Piece.Type == "coffee_table");
    }

    [Fact]
    public void ADiningRoom_GetsATableWithAChairAtEverySeat()
    {
        var room = Box(3800, 4200);
        var openings = new[] { Door(0, 600) };
        var layout = Plan(RoomTypes.Dining, room, openings);
        Sound(layout, room, openings);
        var table = layout.Items.First(i => i.Piece.Type == "dining_table");
        Assert.Equal(table.Piece.Seats, layout.Items.Count(i => i.Piece.Type == "chair"));
    }

    [Fact]
    public void AnOffice_PutsTheDeskEndAtTheWindow_WithItsChair()
    {
        var room = Box(2800, 3400);
        var openings = new[] { Door(1400, 0), Window(1400, 3400) };
        var layout = Plan(RoomTypes.Office, room, openings);
        Sound(layout, room, openings);
        var desk = layout.Items.First(i => i.Piece.Type == "desk");
        var far = desk.Frame.ToWorld(desk.Piece.W / 2, 0);
        var near = desk.Frame.ToWorld(-desk.Piece.W / 2, 0);
        Assert.True(Math.Max(far.Y, near.Y) > 3399, "the desk's end reaches the window wall");
        Assert.Contains(layout.Items, i => i.Piece.Type == "office_chair");
    }

    [Fact]
    public void AGarage_IsNotFurnished_AndSaysWhichRoomsAre()
    {
        var layout = Plan(RoomTypes.Garage, Box(6000, 4000));
        Assert.StartsWith("Forsk furnishes bedroom, living", layout.Why);
    }

    [Fact]
    public void APieceTheRoomHas_IsNotAddedAgain_AndTheRestGoesAroundIt()
    {
        var room = Box(3200, 3800);
        var bed = Furniture.Find("bed.double.160x200")!;
        var frame = new Furniture.Frame(1600, 3800, -1, 0);
        var layout = Furnish.Plan(RoomTypes.Bedroom, room, new[] { Door(700, 0) },
            new List<Furnish.Item> { new Furnish.Item { Piece = bed, Frame = frame } });
        Assert.Null(layout.Why);
        Assert.DoesNotContain(layout.Items, i => i.Piece.Type == "bed");
        var wardrobe = layout.Items.First(i => i.Piece.Type == "wardrobe");
        Assert.Null(Furniture.Clash(Furniture.FootprintOf(wardrobe.Piece, wardrobe.Frame), room, new[] { Furniture.FootprintOf(bed, frame) }));
    }

    /// <summary>A room furnished once is furnished: a second pass adds nothing, its pieces' free floor still counts.</summary>
    [Fact]
    public void FurnishingAgain_AddsNothing()
    {
        var room = Box(3600, 4200);
        var openings = new[] { Door(0, 1500), Window(1800, 4200) };
        var first = Plan(RoomTypes.Kitchen, room, openings);
        Assert.Null(first.Why);
        var again = Furnish.Plan(RoomTypes.Kitchen, room, openings, first.Items);
        Assert.Null(again.Why);
        Assert.Empty(again.Items);
    }

    [Fact]
    public void TheReceipt_CountsThePieces_AndSaysWhatWasLeftOut()
    {
        var layout = new Furnish.Layout();
        layout.Items.Add(new Furnish.Item { Piece = Furniture.Find("chair")!, Frame = new Furniture.Frame(0, 0, 1, 0) });
        layout.Items.Add(new Furnish.Item { Piece = Furniture.Find("chair")!, Frame = new Furniture.Frame(0, 0, 1, 0) });
        layout.Items.Add(new Furnish.Item { Piece = Furniture.Find("dining_table.4seat")!, Frame = new Furniture.Frame(0, 0, 1, 0) });
        layout.Skipped.Add("a TV bench (no free wall for it)");
        Assert.Equal("Furnished the dining room: 2 × chair, dining table for 4. Left out a TV bench (no free wall for it).",
            Furnish.Receipt("the dining room", layout));
    }
}
