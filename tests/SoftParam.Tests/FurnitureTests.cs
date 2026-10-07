using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// FU.1 and FU.2: the furniture catalogue is generated in code. Every piece
/// in every listed size builds its 3D parts, its plan symbol and its play
/// points from the same sizes, and a room places it against a wall or free.
/// </summary>
public class FurnitureTests
{
    static readonly Pt[] Room3x4 = { new Pt(0, 0), new Pt(3000, 0), new Pt(3000, 4000), new Pt(0, 4000) };

    [Fact]
    public void EveryPiece_InEveryListedSize_Generates_AndChecks()
    {
        Assert.True(Furniture.All.Count >= 40, Furniture.All.Count.ToString());
        Assert.Equal(Furniture.All.Count, Furniture.All.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var piece in Furniture.All)
        {
            Assert.Null(Furniture.Check(piece));
            var record = Furniture.Record(piece);
            Assert.Equal(Furniture.Schema, record["schema"]!.ToString());
            Assert.Equal(piece.Id, record["id"]!.ToString());
            Assert.NotEmpty(record["room_tags"]!);
            Assert.Equal("FORSK_FU_" + piece.Id, Furniture.BlockName(piece));
            Assert.NotEqual(Furniture.BlockName(piece), Furniture.ModelBlockName(piece));
        }

        // The starting set by room (roadmap FU.1).
        string[] types =
        {
            "bed", "bedside_table", "wardrobe", "desk", "sofa", "armchair", "coffee_table", "tv_bench", "bookshelf",
            "dining_table", "chair", "kitchen_base", "kitchen_tall", "kitchen_wall", "sink", "hob", "fridge", "dishwasher",
            "worktop", "island", "wc", "basin", "shower", "bath", "washing_machine", "office_chair", "shelf",
            "shoe_cabinet", "coat_rack", "bench"
        };
        foreach (var type in types) Assert.Contains(Furniture.All, p => p.Type == type);
        foreach (var w in new[] { 90, 120, 140, 160, 180 })
            Assert.Contains(Furniture.All, p => p.Type == "bed" && p.W == w * 10 && p.D == 2000);
        foreach (var w in new[] { 500, 1000 })
            Assert.Contains(Furniture.All, p => p.Type == "wardrobe" && p.W == w && p.D == 600);
        foreach (var n in new[] { 4, 6, 8 })
            Assert.Contains(Furniture.All, p => p.Type == "dining_table" && p.Seats == n && p.W != p.D);
        foreach (var n in new[] { 4, 6 })
            Assert.Contains(Furniture.All, p => p.Type == "dining_table" && p.Seats == n && p.W == p.D);
        // Kitchen units sit on the 600 module, fixed.
        Assert.All(Furniture.All.Where(p => p.Type.StartsWith("kitchen_")), p => Assert.True(p.Fixed && p.W % 600 == 0));
    }

    [Fact]
    public void ThePlayPoints_MatchTheCapacity_ThatTheRecordStates()
    {
        foreach (var piece in Furniture.All)
        {
            var model = Furniture.Generate(piece);
            var record = Furniture.Record(piece);
            var capacity = (int)record["capacity"]!;
            Assert.Equal(model.Interactions.Count == 0 ? 0 : model.Interactions.Max(i => i.Slots), capacity);
            if (piece.Seats > 0 && piece.Type != "desk")
                Assert.Equal(piece.Seats, capacity);
            foreach (var spot in model.Spots)
                Assert.Matches(@"^(ip|ap):[a-z_]+:\d+$", spot.Name);
        }
        var bed = Furniture.Generate(Furniture.Find("bed.double.160x200")!);
        Assert.Contains(bed.Spots, s => s.Name == "ip:sleep:1" && s.Z == 500);
    }

    [Fact]
    public void ThePlanSymbol_AddsItsDetailAt1To50_AndKeepsTheOutlineAt1To100()
    {
        var bed = Furniture.Find("bed.double.160x200")!;
        var coarse = Furniture.Plan(bed, 100);
        var fine = Furniture.Plan(bed, 50);
        Assert.DoesNotContain(coarse, m => m.Part == "detail");
        Assert.Contains(fine, m => m.Part == "detail");
        Assert.Equal(coarse.Count, fine.Count(m => m.Part != "detail"));
        // Wall units hang above the plan cut: dashed.
        Assert.All(Furniture.Plan(Furniture.Find("kitchen.wall.60")!, 100), m => Assert.True(m.Dashed));
        // Arcs stay under a full turn, so a three-point export keeps them.
        Assert.All(Furniture.All.SelectMany(p => Furniture.Plan(p, 50)).Where(m => m.Shape == "arc"),
            m => Assert.True(m.A1 - m.A0 > 0 && m.A1 - m.A0 < 360));
    }

    [Theory]
    [InlineData("double bed", null, null, "bed.double.160x200")]
    [InlineData("single bed", null, null, "bed.single.90x200")]
    [InlineData("bed", 180.0, null, "bed.double.180x200")]
    [InlineData("bed", 1200.0, null, "bed.single.120x200")]
    [InlineData("beds", null, null, "bed.double.160x200")]
    [InlineData("toilet", null, null, "wc")]
    [InlineData("sofa", null, 2, "sofa.2seat")]
    [InlineData("Sofa", null, null, "sofa.3seat")]
    [InlineData("dining table", null, 8, "dining_table.8seat")]
    [InlineData("round table", null, 4, "dining_table.round.4seat")]
    [InlineData("kitchen.hob.60", null, null, "kitchen.hob.60")]
    [InlineData("wardrobe", 50.0, null, "wardrobe.50")]
    public void ARequest_NamesOnePiece(string item, double? width, int? seats, string id)
    {
        Assert.Equal(id, Furniture.Resolve(item, width, seats)?.Id);
    }

    [Fact]
    public void AnUnknownRequest_NamesNoPiece()
    {
        Assert.Null(Furniture.Resolve("grand piano"));
        Assert.Null(Furniture.Resolve(""));
    }

    [Fact]
    public void AWallPiece_StandsBackToTheLongestWall_InsideTheRoom()
    {
        var bed = Furniture.Find("bed.double.160x200")!;
        var spot = Furniture.Place(bed, Room3x4, new List<Furniture.Footprint>(), null, out var why);
        Assert.Null(why);
        Assert.NotNull(spot);
        // The 4 m walls run along y: the back is on x = 0 or x = 3000, the bed points into the room.
        var foot = Furniture.FootprintOf(bed, spot!.Frame);
        Assert.Null(Furniture.Clash(foot, Room3x4, null));
        Assert.Equal(3000, spot.Frame.Ox, 6);
        Assert.Equal(2000, spot.Frame.Oy, 6);
        Assert.Equal("against the east wall", spot.Where);

        // A wardrobe slides along the same wall, clear of the bed.
        var wardrobe = Furniture.Find("wardrobe.100")!;
        var second = Furniture.Place(wardrobe, Room3x4, new List<Furniture.Footprint> { foot }, null, out why);
        Assert.NotNull(second);
        Assert.Equal(3000, second!.Frame.Ox, 6);
        Assert.Null(Furniture.Clash(Furniture.FootprintOf(wardrobe, second.Frame), Room3x4, new[] { foot }));
        // A second double bed does not fit beside the first.
        Assert.Null(Furniture.Place(bed, Room3x4, new List<Furniture.Footprint> { foot }, null, out why));
        Assert.StartsWith("no free stretch of wall 1.6 m long (it would overlap the double bed 160", why);
    }

    [Fact]
    public void AWallUnit_HangsOverABaseUnit_ButTwoBaseUnitsDoNotShareASpot()
    {
        var baseUnit = Furniture.Find("kitchen.base.60")!;
        var wallUnit = Furniture.Find("kitchen.wall.60")!;
        var frame = new Furniture.Frame(1500, 0, 1, 0);
        var below = Furniture.FootprintOf(baseUnit, frame);
        Assert.Null(Furniture.Clash(Furniture.FootprintOf(wallUnit, frame), Room3x4, new[] { below }));
        Assert.Equal("it would overlap the base unit 60", Furniture.Clash(Furniture.FootprintOf(baseUnit, frame), Room3x4, new[] { below }));
    }

    [Fact]
    public void APieceThatDoesNotFit_IsRefused_WithWhy()
    {
        var tiny = new[] { new Pt(0, 0), new Pt(1000, 0), new Pt(1000, 1000), new Pt(0, 1000) };
        Assert.Null(Furniture.Place(Furniture.Find("bed.double.160x200")!, tiny, new List<Furniture.Footprint>(), null, out var why));
        Assert.Equal("no wall in the room is 1.6 m long", why);
        Assert.Null(Furniture.Place(Furniture.Find("dining_table.8seat")!, tiny, new List<Furniture.Footprint>(), null, out why));
        Assert.StartsWith("no free floor", why);
    }

    [Fact]
    public void AFreePiece_StandsInTheMiddle_AlongTheLongestWall()
    {
        var table = Furniture.Find("dining_table.6seat")!;
        var spot = Furniture.Place(table, Room3x4, new List<Furniture.Footprint>(), null, out var why);
        Assert.Null(why);
        var centre = Furniture.CentreOf(table, spot!.Frame);
        Assert.Equal(1500, centre.X, 6);
        Assert.Equal(2000, centre.Y, 6);
        // Its long side runs along y, like the room's longest wall.
        Assert.True(Math.Abs(spot.Frame.Ux) < 1e-9 && Math.Abs(Math.Abs(spot.Frame.Uy) - 1) < 1e-9);
        Assert.Equal("in the middle of the room", spot.Where);
    }

    [Fact]
    public void ACentredFrame_PutsThePieceCentreThere()
    {
        var sofa = Furniture.Find("sofa.3seat")!;
        foreach (var degrees in new[] { 0.0, 37.0, 90.0, 215.0 })
        {
            var frame = Furniture.Centred(sofa, 1234, -567, degrees);
            var centre = Furniture.CentreOf(sofa, frame);
            Assert.Equal(1234, centre.X, 6);
            Assert.Equal(-567, centre.Y, 6);
            Assert.Equal(degrees, frame.Degrees, 6);
        }
    }

    [Fact]
    public void APieceOffItsPlacement_WasMovedByHand()
    {
        var frame = new Furniture.Frame(1000, 2000, 0, 1);
        var placed = Furniture.FrameText(frame);
        Assert.False(Furniture.MovedByHand(placed, frame));
        Assert.False(Furniture.MovedByHand(placed, new Furniture.Frame(1000.4, 2000, 0, 1)));
        Assert.True(Furniture.MovedByHand(placed, new Furniture.Frame(1100, 2000, 0, 1)));
        Assert.True(Furniture.MovedByHand(placed, new Furniture.Frame(1000, 2000, 1, 0)));
        Assert.True(Furniture.MovedByHand(null, frame));
    }

    [Fact]
    public void ARoomTag_StepsOffTheFurniture_AndStaysInTheRoom()
    {
        var bed = Furniture.Find("bed.double.160x200")!;
        var foot = Furniture.FootprintOf(bed, Furniture.Centred(bed, 1500, 2000, 90));
        var spot = Furniture.TagSpot(new Pt(1500, 2000), Room3x4, 600, 200, new[] { foot.Corners }, 125);
        Assert.NotNull(spot);
        var c = spot!.Value;
        var box = new[] { new Pt(c.X - 600, c.Y - 200), new Pt(c.X + 600, c.Y - 200), new Pt(c.X + 600, c.Y + 200), new Pt(c.X - 600, c.Y + 200) };
        Assert.Null(Furniture.Clash(new Furniture.Footprint { Corners = box, Z0 = 0, Z1 = 1, Label = "tag" }, Room3x4, new[] { foot }));
        // Nothing in the way: the tag point stays.
        Assert.Equal(new Pt(10, 20), Furniture.TagSpot(new Pt(10, 20), Room3x4, 5, 5, new Pt[0][], 125));
    }

    [Fact]
    public void Ids_CountUpFromF01_AndTheReceiptNamesThePiece()
    {
        Assert.Equal("F01", Furniture.NextId(new string[0]));
        Assert.Equal("F03", Furniture.NextId(new[] { "F01", "F02", "S01" }));
        Assert.Equal("Added a double bed 160 to the bedroom, against the north wall.",
            Furniture.Receipt(Furniture.Find("bed.double.160x200")!, "the bedroom", "against the north wall"));
        Assert.Equal("Added an armchair.", Furniture.Receipt(Furniture.Find("armchair")!, null, null));
        Assert.Equal("Added a WC.", Furniture.Receipt(Furniture.Find("wc")!, null, null));
    }
}

/// <summary>Julian, 2026-10-07: furniture belongs in More actions too, not only in chat.</summary>
public class FurnitureActionTests
{
    [Fact]
    public void MoreActions_OffersAddFurnitureAndFurnish_OnceTheFileHasRooms()
    {
        var house = Docs.Facts("house");
        Assert.Contains(RhinoMCPPlugin.Forsk.ForskRegistry.All, a => a.Id == "furniture.add" && a.When(house));
        Assert.Contains(RhinoMCPPlugin.Forsk.ForskRegistry.All, a => a.Id == "furniture.furnish" && a.When(house));
        Assert.DoesNotContain(RhinoMCPPlugin.Forsk.ForskRegistry.All, a => a.Id.StartsWith("furniture.") && a.When(Docs.Facts("walls only")));
        Assert.Equal("Add furniture", RhinoMCPPlugin.Forsk.ForskText.Label("furniture.add"));
        Assert.Equal("Furnish rooms", RhinoMCPPlugin.Forsk.ForskText.Label("furniture.furnish"));
    }

    [Fact]
    public void TheAddCard_ListsTheCatalogueByName_AndTheRooms()
    {
        var facts = Docs.Facts("house");
        var card = RhinoMCPPlugin.Forsk.ForskCards.AddFurniture(facts)!;
        var item = card.Fields!.Single(f => f.Key == "item");
        Assert.Equal(Furniture.All.Count, item.Options!.Count);
        Assert.Equal("Double bed 160", item.Value);
        Assert.Equal(new[] { "add", "cancel" }, card.Pills.Select(p => p.Id));
        if (facts.Rooms.Count > 1)
            Assert.Contains(card.Fields!, f => f.Key == "room");
        Assert.Equal("Stue", RhinoMCPPlugin.Forsk.ForskCards.RoomName("Stue · 24.5 m² (living)"));
    }

    [Fact]
    public void ThePickedRoom_IsNotNamed_AnotherRoomIs()
    {
        var picked = RhinoMCPPlugin.Forsk.ForskText.Get("furniture.add.picked");
        Assert.Equal("{\"item\":\"Sofa\"}", RhinoMCPPlugin.Forsk.ForskCards.AddFurnitureArgs(new Newtonsoft.Json.Linq.JObject { ["item"] = " Sofa ", ["room"] = picked }).ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("Kitchen", RhinoMCPPlugin.Forsk.ForskCards.AddFurnitureArgs(new Newtonsoft.Json.Linq.JObject { ["item"] = "Hob unit 60", ["room"] = "Kitchen" })["room"]!.ToString());
    }
}
