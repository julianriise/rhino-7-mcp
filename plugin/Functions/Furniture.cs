using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// FU.1 and FU.2: the residential furniture catalogue (forsk.furniture.v1),
/// generated in code. One generator per piece type builds the 3D parts, the
/// 2D plan symbol and the named play points from the same sizes, so the
/// symbol and the model never drift apart. No RhinoCommon.
///
/// A piece's own frame: the origin is the middle of its back edge on the
/// finished floor, x runs along the back, y points out from the wall into
/// the room, z is up. The footprint is x in [-W/2, W/2], y in [0, D].
/// </summary>
public static class Furniture
{
    public const string Kind = "furniture";
    public const string Schema = "forsk.furniture.v1";
    public const int Rev = 1;
    public const string LayerName = "A-FURN";
    public const string FixedLayerName = "A-FURN-FIXD";
    /// <summary>The play points inside each block: hidden, never printed (spec §4).</summary>
    public const string PlayLayerName = "A-FURN-PLAY";
    public const string CatalogKey = "forsk:catalog_id";
    public const string SchemaKey = "forsk:schema";
    public const string RevKey = "forsk:catalog_rev";
    public const string RoomKey = "forsk:room";
    /// <summary>The 2D symbol's block in a DWG sheet (FU.2).</summary>
    public const string BlockPrefix = "FORSK_FU_";
    /// <summary>The 3D block in the model: another name, so a sheet export can add its 2D block beside it.</summary>
    public const string ModelBlockPrefix = "FORSK_FU3D_";
    /// <summary>The plan symbol adds its detail lines at this scale and finer (1:50).</summary>
    public const int DetailScale = 50;

    public const string Wall = "wall";
    public const string Centre = "centre";

    /// <summary>One catalogue entry: a type at one size.</summary>
    public sealed class Piece
    {
        public string Id;
        public string Type;
        public string Name;
        public double W, D, H;
        /// <summary>Kitchen units and bathroom fixtures: on A-FURN-FIXD.</summary>
        public bool Fixed;
        /// <summary>wall: back to a wall. centre: free in the room.</summary>
        public string Place = Wall;
        /// <summary>The variant a bare type name picks.</summary>
        public bool Default;
        public int Seats;
        public string[] Rooms;
        public string[] Aliases = new string[0];
        /// <summary>The bottom of the piece above the floor: wall units and racks hang.</summary>
        public double Z0;
        internal Action<Piece, Model> Build;
    }

    /// <summary>A solid part: a box, or an upright elliptic prism (round when Rx = Ry).</summary>
    public sealed class Part
    {
        public string Shape;
        public string Name;
        public double X0, Y0, Z0, X1, Y1, Z1;
        public double Cx, Cy, Rx, Ry;
    }

    /// <summary>One plan stroke: a line (X0 Y0 X1 Y1) or an arc (centre, radius, A0 to A1 degrees counter-clockwise, under 360).</summary>
    public sealed class Mark
    {
        public string Shape;
        public double X0, Y0, X1, Y1;
        public double Cx, Cy, R, A0, A1;
        /// <summary>outline or detail. Detail draws at 1:50 and finer only.</summary>
        public string Part = "outline";
        /// <summary>Above the plan cut: wall units, hat shelves.</summary>
        public bool Dashed;
    }

    /// <summary>A named play point (spec §4): ip:&lt;interaction&gt;:&lt;slot&gt; or ap:…, with the facing in the piece's frame.</summary>
    public sealed class Spot
    {
        public string Name;
        public double X, Y, Z;
        public double Fx, Fy;
    }

    public sealed class Interaction
    {
        public string Name;
        public int Slots;
    }

    public sealed class Model
    {
        public Piece Piece;
        public readonly List<Part> Parts = new List<Part>();
        public readonly List<Mark> Marks = new List<Mark>();
        public readonly List<Spot> Spots = new List<Spot>();
        public readonly List<Interaction> Interactions = new List<Interaction>();

        internal void Box(string name, double x0, double y0, double z0, double x1, double y1, double z1) =>
            Parts.Add(new Part { Shape = "box", Name = name, X0 = Math.Min(x0, x1), Y0 = Math.Min(y0, y1), Z0 = Math.Min(z0, z1), X1 = Math.Max(x0, x1), Y1 = Math.Max(y0, y1), Z1 = Math.Max(z0, z1) });

        internal void Prism(string name, double cx, double cy, double rx, double ry, double z0, double z1) =>
            Parts.Add(new Part { Shape = "prism", Name = name, Cx = cx, Cy = cy, Rx = rx, Ry = ry, Z0 = z0, Z1 = z1, X0 = cx - rx, X1 = cx + rx, Y0 = cy - ry, Y1 = cy + ry });

        internal void Line(double x0, double y0, double x1, double y1, string part = "outline", bool dashed = false) =>
            Marks.Add(new Mark { Shape = "line", X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Part = part, Dashed = dashed });

        internal void Rect(double x0, double y0, double x1, double y1, string part = "outline", bool dashed = false)
        {
            Line(x0, y0, x1, y0, part, dashed);
            Line(x1, y0, x1, y1, part, dashed);
            Line(x1, y1, x0, y1, part, dashed);
            Line(x0, y1, x0, y0, part, dashed);
        }

        internal void Arc(double cx, double cy, double r, double a0, double a1, string part = "outline") =>
            Marks.Add(new Mark { Shape = "arc", Cx = cx, Cy = cy, R = r, A0 = a0, A1 = a1, Part = part });

        /// <summary>A full circle as two half arcs: a closed arc does not survive a three-point export.</summary>
        internal void Circle(double cx, double cy, double r, string part = "outline")
        {
            Arc(cx, cy, r, 0, 180, part);
            Arc(cx, cy, r, 180, 360, part);
        }

        /// <summary>An ellipse as a closed polyline of 32 pieces.</summary>
        internal void Ellipse(double cx, double cy, double rx, double ry, string part = "outline")
        {
            const int n = 32;
            for (var i = 0; i < n; i++)
            {
                var a = 2 * Math.PI * i / n;
                var b = 2 * Math.PI * (i + 1) / n;
                Line(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a), cx + rx * Math.Cos(b), cy + ry * Math.Sin(b), part);
            }
        }

        /// <summary>A rectangle with round corners of radius r.</summary>
        internal void RoundRect(double x0, double y0, double x1, double y1, double r, string part = "outline")
        {
            r = Math.Min(r, Math.Min(x1 - x0, y1 - y0) / 2);
            Line(x0 + r, y0, x1 - r, y0, part);
            Line(x1, y0 + r, x1, y1 - r, part);
            Line(x1 - r, y1, x0 + r, y1, part);
            Line(x0, y1 - r, x0, y0 + r, part);
            Arc(x1 - r, y0 + r, r, 270, 360, part);
            Arc(x1 - r, y1 - r, r, 0, 90, part);
            Arc(x0 + r, y1 - r, r, 90, 180, part);
            Arc(x0 + r, y0 + r, r, 180, 270, part);
        }

        /// <summary>One interaction with its interaction and approach points, slot by slot.</summary>
        internal void Use(string name, params Spot[] pairs)
        {
            var slots = pairs.Length / 2;
            for (var slot = 0; slot < slots; slot++)
            {
                var ip = pairs[2 * slot];
                var ap = pairs[2 * slot + 1];
                ip.Name = "ip:" + name + ":" + slot.ToString(CultureInfo.InvariantCulture);
                ap.Name = "ap:" + name + ":" + slot.ToString(CultureInfo.InvariantCulture);
                Spots.Add(ip);
                Spots.Add(ap);
            }
            Interactions.Add(new Interaction { Name = name, Slots = slots });
        }
    }

    static Spot At(double x, double y, double z, double fx, double fy) => new Spot { X = x, Y = y, Z = z, Fx = fx, Fy = fy };

    /// <summary>Standing in front of a piece, facing it, and the approach point behind.</summary>
    static Spot[] Front(Piece p, double x = 0) => new[] { At(x, p.D + 300, 0, 0, -1), At(x, p.D + 600, 0, 0, -1) };

    static readonly List<Piece> all = BuildCatalog();

    public static IReadOnlyList<Piece> All => all;

    public static Piece Find(string id) =>
        id == null ? null : all.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string BlockName(Piece piece) => BlockPrefix + piece.Id;

    public static string ModelBlockName(Piece piece) => ModelBlockPrefix + piece.Id;

    public static string LayerFor(Piece piece) => piece.Fixed ? FixedLayerName : LayerName;

    /// <summary>The print role: fixed pieces export to A-FURN-FIXD.</summary>
    public static string RoleFor(Piece piece) => piece.Fixed ? "furniture_fixed" : "furniture";

    public static Model Generate(Piece piece)
    {
        var model = new Model { Piece = piece };
        piece.Build(piece, model);
        return model;
    }

    /// <summary>The plan symbol at 1:scale: the outline always, the detail lines at 1:50 and finer.</summary>
    public static List<Mark> Plan(Piece piece, int scale) =>
        Generate(piece).Marks.Where(m => m.Part != "detail" || scale <= DetailScale).ToList();

    // ---- The catalogue ----------------------------------------------------

    static List<Piece> BuildCatalog()
    {
        var list = new List<Piece>();
        void Add(Piece p) => list.Add(p);
        string[] R(params string[] rooms) => rooms;

        // Bedroom
        foreach (var w in new[] { 90, 120 })
            Add(new Piece { Id = "bed.single." + w + "x200", Type = "bed", Name = "Single bed " + w, W = w * 10, D = 2000, H = 900, Default = false, Seats = 1, Rooms = R(RoomTypes.Bedroom), Aliases = new[] { "single bed", "bed single" }, Build = Bed });
        foreach (var w in new[] { 140, 160, 180 })
            Add(new Piece { Id = "bed.double." + w + "x200", Type = "bed", Name = "Double bed " + w, W = w * 10, D = 2000, H = 900, Default = w == 160, Seats = 2, Rooms = R(RoomTypes.Bedroom), Aliases = new[] { "double bed", "bed double", "bed" }, Build = Bed });
        list.First(p => p.Id == "bed.single.90x200").Default = true;
        Add(new Piece { Id = "bedside_table.45", Type = "bedside_table", Name = "Bedside table", W = 450, D = 400, H = 550, Default = true, Rooms = R(RoomTypes.Bedroom), Aliases = new[] { "bedside table", "nightstand", "night table" }, Build = Cabinet });
        foreach (var w in new[] { 50, 100 })
            Add(new Piece { Id = "wardrobe." + w, Type = "wardrobe", Name = "Wardrobe " + w, W = w * 10, D = 600, H = 2100, Default = w == 100, Rooms = R(RoomTypes.Bedroom, RoomTypes.Hall), Aliases = new[] { "closet" }, Build = Wardrobe });
        foreach (var w in new[] { 120, 140 })
            Add(new Piece { Id = "desk." + w, Type = "desk", Name = "Desk " + w, W = w * 10, D = 600, H = 740, Default = w == 120, Seats = 1, Rooms = R(RoomTypes.Bedroom, RoomTypes.Office), Build = Desk });

        // Living
        foreach (var n in new[] { 2, 3 })
            Add(new Piece { Id = "sofa." + n + "seat", Type = "sofa", Name = n + "-seat sofa", W = n == 2 ? 1600 : 2200, D = 900, H = 800, Default = n == 3, Seats = n, Rooms = R(RoomTypes.Living), Aliases = new[] { "couch" }, Build = Sofa });
        Add(new Piece { Id = "armchair", Type = "armchair", Name = "Armchair", W = 850, D = 850, H = 800, Default = true, Seats = 1, Rooms = R(RoomTypes.Living), Aliases = new[] { "lounge chair", "easy chair" }, Build = Sofa });
        Add(new Piece { Id = "coffee_table.120x60", Type = "coffee_table", Name = "Coffee table", W = 1200, D = 600, H = 420, Default = true, Place = Centre, Rooms = R(RoomTypes.Living), Build = Table });
        Add(new Piece { Id = "tv_bench.180", Type = "tv_bench", Name = "TV bench", W = 1800, D = 400, H = 450, Default = true, Rooms = R(RoomTypes.Living), Aliases = new[] { "tv unit", "media bench" }, Build = Cabinet });
        Add(new Piece { Id = "bookshelf.80", Type = "bookshelf", Name = "Bookshelf", W = 800, D = 350, H = 2000, Default = true, Rooms = R(RoomTypes.Living, RoomTypes.Office), Aliases = new[] { "bookcase" }, Build = Shelf });

        // Dining
        foreach (var (n, w, d) in new[] { (4, 1200, 800), (6, 1800, 900), (8, 2400, 1000) })
            Add(new Piece { Id = "dining_table." + n + "seat", Type = "dining_table", Name = "Dining table for " + n, W = w, D = d, H = 740, Default = n == 6, Seats = n, Place = Centre, Rooms = R(RoomTypes.Dining, RoomTypes.Kitchen, RoomTypes.Living), Aliases = new[] { "table", "kitchen table" }, Build = Table });
        foreach (var (n, w) in new[] { (4, 1100), (6, 1400) })
            Add(new Piece { Id = "dining_table.round." + n + "seat", Type = "dining_table", Name = "Round dining table for " + n, W = w, D = w, H = 740, Seats = n, Place = Centre, Rooms = R(RoomTypes.Dining, RoomTypes.Kitchen, RoomTypes.Living), Aliases = new[] { "round table" }, Build = RoundTable });
        Add(new Piece { Id = "chair", Type = "chair", Name = "Chair", W = 450, D = 500, H = 850, Default = true, Seats = 1, Place = Centre, Rooms = R(RoomTypes.Dining, RoomTypes.Kitchen), Aliases = new[] { "dining chair" }, Build = Chair });

        // Kitchen, on the 600 module. Units with a top finish at 900.
        Add(new Piece { Id = "kitchen.base.60", Type = "kitchen_base", Name = "Base unit 60", W = 600, D = 600, H = 900, Fixed = true, Default = true, Rooms = R(RoomTypes.Kitchen), Aliases = new[] { "base unit", "base cabinet", "kitchen unit" }, Build = BaseUnit });
        Add(new Piece { Id = "kitchen.tall.60", Type = "kitchen_tall", Name = "Tall unit 60", W = 600, D = 600, H = 2100, Fixed = true, Default = true, Rooms = R(RoomTypes.Kitchen), Aliases = new[] { "tall unit", "larder", "pantry unit" }, Build = TallUnit });
        Add(new Piece { Id = "kitchen.wall.60", Type = "kitchen_wall", Name = "Wall unit 60", W = 600, D = 350, H = 700, Z0 = 1450, Fixed = true, Default = true, Rooms = R(RoomTypes.Kitchen), Aliases = new[] { "wall unit", "wall cabinet", "upper cabinet" }, Build = WallUnit });
        Add(new Piece { Id = "kitchen.sink.80", Type = "sink", Name = "Sink unit 80", W = 800, D = 600, H = 900, Fixed = true, Default = true, Rooms = R(RoomTypes.Kitchen), Aliases = new[] { "sink unit", "kitchen sink" }, Build = SinkUnit });
        Add(new Piece { Id = "kitchen.hob.60", Type = "hob", Name = "Hob unit 60", W = 600, D = 600, H = 905, Fixed = true, Default = true, Rooms = R(RoomTypes.Kitchen), Aliases = new[] { "stove", "cooktop", "cooker" }, Build = HobUnit });
        Add(new Piece { Id = "kitchen.fridge.60", Type = "fridge", Name = "Fridge 60", W = 600, D = 650, H = 1850, Fixed = true, Default = true, Rooms = R(RoomTypes.Kitchen), Aliases = new[] { "refrigerator", "fridge freezer" }, Build = Appliance });
        Add(new Piece { Id = "kitchen.dishwasher.60", Type = "dishwasher", Name = "Dishwasher 60", W = 600, D = 600, H = 900, Fixed = true, Default = true, Rooms = R(RoomTypes.Kitchen), Build = Appliance });
        foreach (var w in new[] { 120, 240 })
            Add(new Piece { Id = "kitchen.worktop." + w, Type = "worktop", Name = "Worktop " + w, W = w * 10, D = 600, H = 40, Z0 = 860, Fixed = true, Default = w == 120, Rooms = R(RoomTypes.Kitchen), Aliases = new[] { "counter", "countertop" }, Build = Worktop });
        Add(new Piece { Id = "kitchen.island.180", Type = "island", Name = "Kitchen island", W = 1800, D = 900, H = 900, Fixed = true, Default = true, Place = Centre, Rooms = R(RoomTypes.Kitchen), Aliases = new[] { "kitchen island" }, Build = BaseUnit });

        // Bathroom
        Add(new Piece { Id = "wc", Type = "wc", Name = "WC", W = 380, D = 600, H = 800, Fixed = true, Default = true, Rooms = R(RoomTypes.Bathroom, RoomTypes.Wc), Aliases = new[] { "toilet", "loo" }, Build = Wc });
        Add(new Piece { Id = "basin.60", Type = "basin", Name = "Basin with vanity 60", W = 600, D = 450, H = 850, Fixed = true, Default = true, Rooms = R(RoomTypes.Bathroom, RoomTypes.Wc), Aliases = new[] { "washbasin", "vanity", "sink basin" }, Build = Basin });
        foreach (var w in new[] { 80, 90 })
            Add(new Piece { Id = "shower." + w, Type = "shower", Name = "Shower " + w + " × " + w, W = w * 10, D = w * 10, H = 40, Fixed = true, Default = w == 90, Rooms = R(RoomTypes.Bathroom), Aliases = new[] { "shower tray" }, Build = Shower });
        Add(new Piece { Id = "bath.170", Type = "bath", Name = "Bath 170", W = 1700, D = 750, H = 550, Fixed = true, Default = true, Rooms = R(RoomTypes.Bathroom), Aliases = new[] { "bathtub", "tub" }, Build = Bath });
        Add(new Piece { Id = "washing_machine", Type = "washing_machine", Name = "Washing machine", W = 600, D = 600, H = 850, Fixed = true, Default = true, Rooms = R(RoomTypes.Bathroom, RoomTypes.Laundry), Aliases = new[] { "washer" }, Build = Appliance });

        // Home office
        Add(new Piece { Id = "office_chair", Type = "office_chair", Name = "Office chair", W = 600, D = 600, H = 1000, Default = true, Seats = 1, Place = Centre, Rooms = R(RoomTypes.Office, RoomTypes.Bedroom), Aliases = new[] { "desk chair" }, Build = OfficeChair });
        Add(new Piece { Id = "shelf.80", Type = "shelf", Name = "Shelf 80", W = 800, D = 300, H = 1800, Default = true, Rooms = R(RoomTypes.Office, RoomTypes.Storage), Aliases = new[] { "shelving" }, Build = Shelf });

        // Hall
        Add(new Piece { Id = "shoe_cabinet.80", Type = "shoe_cabinet", Name = "Shoe cabinet", W = 800, D = 300, H = 1000, Default = true, Rooms = R(RoomTypes.Hall), Aliases = new[] { "shoe rack" }, Build = Cabinet });
        Add(new Piece { Id = "coat_rack.100", Type = "coat_rack", Name = "Coat rack", W = 1000, D = 300, H = 300, Z0 = 1500, Default = true, Rooms = R(RoomTypes.Hall), Aliases = new[] { "coat hooks", "hat shelf" }, Build = CoatRack });
        Add(new Piece { Id = "bench.100", Type = "bench", Name = "Bench", W = 1000, D = 400, H = 450, Default = true, Seats = 2, Rooms = R(RoomTypes.Hall), Build = Bench });
        return list;
    }

    // ---- Generators -------------------------------------------------------

    static void Bed(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("base", -hw, 0, 0, hw, d, 300);
        m.Box("mattress", -hw + 10, 60, 300, hw - 10, d - 10, 500);
        m.Box("headboard", -hw, 0, 0, hw, 60, 900);
        m.Rect(-hw, 0, hw, d);
        m.Line(-hw, 60, hw, 60, "detail");
        // Pillows: one for a single, two from 140 up.
        var pillows = p.Seats;
        var pw = (p.W - 120 - 40 * (pillows - 1)) / pillows;
        for (var i = 0; i < pillows; i++)
        {
            var x0 = -hw + 60 + i * (pw + 40);
            m.Box("pillow", x0, 110, 500, x0 + pw, 460, 580);
            m.RoundRect(x0, 110, x0 + pw, 460, 60);
        }
        // The turned-down duvet: a line across, and the folded corner at 1:50.
        m.Line(-hw, 620, hw, 620);
        m.Line(hw - 350, 620, hw, 970, "detail");
        if (pillows == 1)
            m.Use("sleep", At(0, d * 0.55, 500, 0, 1), At(hw + 300, d * 0.5, 0, -1, 0));
        else
            m.Use("sleep", At(-p.W / 4, d * 0.55, 500, 0, 1), At(-hw - 300, d * 0.5, 0, 1, 0),
                At(p.W / 4, d * 0.55, 500, 0, 1), At(hw + 300, d * 0.5, 0, -1, 0));
    }

    /// <summary>A plain carcass: bedside table, TV bench, shoe cabinet.</summary>
    static void Cabinet(Piece p, Model m)
    {
        double hw = p.W / 2;
        m.Box("carcass", -hw, 0, p.Z0, hw, p.D, p.Z0 + p.H);
        m.Rect(-hw, 0, hw, p.D);
        m.Rect(-hw + 20, 20, hw - 20, p.D - 20, "detail");
        if (p.Type == "shoe_cabinet") m.Use("store", Front(p));
    }

    static void Wardrobe(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("carcass", -hw, 0, 0, hw, d, p.H);
        m.Rect(-hw, 0, hw, d);
        // The hanging rail, dashed: the usual wardrobe mark.
        m.Line(-hw + 50, d / 2, hw - 50, d / 2, "outline", true);
        var doors = Math.Max(1, (int)Math.Round(p.W / 500));
        for (var i = 1; i < doors; i++)
        {
            var x = -hw + i * p.W / doors;
            m.Line(x, d - 20, x, d, "detail");
        }
        m.Line(-hw, d - 20, hw, d - 20, "detail");
        m.Use("dress", Front(p));
    }

    static void Desk(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("top", -hw, 0, p.H - 25, hw, d, p.H);
        m.Box("side", -hw, 0, 0, -hw + 25, d, p.H - 25);
        m.Box("side", hw - 25, 0, 0, hw, d, p.H - 25);
        m.Rect(-hw, 0, hw, d);
        m.Use("work", At(0, d + 250, 450, 0, -1), At(0, d + 700, 0, 0, -1));
    }

    /// <summary>Sofa and armchair: a back, two arms and the seats between.</summary>
    static void Sofa(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        const double arm = 150, back = 200, seat = 420;
        m.Box("back", -hw, 0, 0, hw, back, p.H);
        m.Box("arm", -hw, back, 0, -hw + arm, d, 600);
        m.Box("arm", hw - arm, back, 0, hw, d, 600);
        m.Box("seat", -hw + arm, back, 0, hw - arm, d, seat);
        m.Rect(-hw, 0, hw, d);
        m.Line(-hw + arm, back, hw - arm, back);
        m.Line(-hw + arm, back, -hw + arm, d);
        m.Line(hw - arm, back, hw - arm, d);
        var inner = p.W - 2 * arm;
        var spots = new List<Spot>();
        for (var i = 0; i < p.Seats; i++)
        {
            var x = -hw + arm + inner * (i + 0.5) / p.Seats;
            if (i > 0)
            {
                var cut = -hw + arm + inner * i / p.Seats;
                m.Line(cut, back, cut, d, "detail");
            }
            spots.Add(At(x, (back + d) / 2, seat, 0, 1));
            spots.Add(At(x, d + 400, 0, 0, -1));
        }
        m.Use("sit", spots.ToArray());
    }

    /// <summary>A rectangular table on four legs: coffee table and dining tables.</summary>
    static void Table(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        const double leg = 50, inset = 40, top = 30;
        m.Box("top", -hw, 0, p.H - top, hw, d, p.H);
        foreach (var x in new[] { -hw + inset, hw - inset - leg })
            foreach (var y in new[] { inset, d - inset - leg })
                m.Box("leg", x, y, 0, x + leg, y + leg, p.H - top);
        m.Rect(-hw, 0, hw, d);
        if (p.Seats == 0) return;
        // Seats on both long sides, and one at each end from eight.
        var spots = new List<Spot>();
        var ends = p.Seats >= 8 ? 1 : 0;
        var perSide = (p.Seats - 2 * ends) / 2;
        for (var i = 0; i < perSide; i++)
        {
            var x = -hw + p.W * (i + 0.5) / perSide;
            spots.Add(At(x, -350, 450, 0, 1));
            spots.Add(At(x, -750, 0, 0, 1));
            spots.Add(At(x, d + 350, 450, 0, -1));
            spots.Add(At(x, d + 750, 0, 0, -1));
        }
        if (ends == 1)
        {
            spots.Add(At(-hw - 350, d / 2, 450, 1, 0));
            spots.Add(At(-hw - 750, d / 2, 0, 1, 0));
            spots.Add(At(hw + 350, d / 2, 450, -1, 0));
            spots.Add(At(hw + 750, d / 2, 0, -1, 0));
        }
        m.Use("eat", spots.ToArray());
    }

    static void RoundTable(Piece p, Model m)
    {
        var r = p.W / 2;
        m.Prism("top", 0, r, r, r, p.H - 30, p.H);
        m.Prism("column", 0, r, 50, 50, 30, p.H - 30);
        m.Prism("foot", 0, r, 300, 300, 0, 30);
        m.Circle(0, r, r);
        m.Circle(0, r, 300, "detail");
        var spots = new List<Spot>();
        for (var i = 0; i < p.Seats; i++)
        {
            var a = Math.PI / 2 + 2 * Math.PI * i / p.Seats;
            double cx = Math.Cos(a), cy = Math.Sin(a);
            spots.Add(At((r + 350) * cx, r + (r + 350) * cy, 450, -cx, -cy));
            spots.Add(At((r + 750) * cx, r + (r + 750) * cy, 0, -cx, -cy));
        }
        m.Use("eat", spots.ToArray());
    }

    static void Chair(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        const double leg = 30, seat = 450, back = 50;
        foreach (var x in new[] { -hw, hw - leg })
            foreach (var y in new[] { 0.0, d - leg })
                m.Box("leg", x, y, 0, x + leg, y + leg, seat - 30);
        m.Box("seat", -hw, 0, seat - 30, hw, d, seat);
        m.Box("back", -hw, 0, seat, hw, back, p.H);
        m.Rect(-hw, 0, hw, d);
        m.Line(-hw, back, hw, back);
        m.Use("sit", At(0, d * 0.55, seat, 0, 1), At(0, d + 350, 0, 0, -1));
    }

    /// <summary>Plinth, carcass and a 40 top finishing at 900: base units and the island.</summary>
    static void CarcassWithTop(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("plinth", -hw, 0, 0, hw, d - 60, 100);
        m.Box("carcass", -hw, 0, 100, hw, d - 20, 860);
        m.Box("top", -hw, 0, 860, hw, d, 900);
        m.Rect(-hw, 0, hw, d);
    }

    static void BaseUnit(Piece p, Model m)
    {
        CarcassWithTop(p, m);
        if (p.Type == "island")
        {
            m.Line(-p.W / 2, p.D - 300, p.W / 2, p.D - 300, "detail");
            m.Use("prepare", Front(p));
        }
        else m.Use("store", Front(p));
    }

    static void TallUnit(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("plinth", -hw, 0, 0, hw, d - 60, 100);
        m.Box("carcass", -hw, 0, 100, hw, d, p.H);
        m.Rect(-hw, 0, hw, d);
        m.Line(-hw, 0, hw, d);
        m.Line(-hw, d, hw, 0);
        m.Use("store", Front(p));
    }

    static void WallUnit(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("carcass", -hw, 0, p.Z0, hw, d, p.Z0 + p.H);
        // Above the plan cut: dashed.
        m.Rect(-hw, 0, hw, d, "outline", true);
        m.Use("store", Front(p));
    }

    static void SinkUnit(Piece p, Model m)
    {
        CarcassWithTop(p, m);
        double hw = p.W / 2;
        m.RoundRect(-hw + 100, 100, hw - 100, 500, 60);
        m.Circle(0, 300, 25, "detail");
        m.Circle(0, 60, 20, "detail");
        m.Use("wash_up", Front(p));
    }

    static void HobUnit(Piece p, Model m)
    {
        CarcassWithTop(p, m);
        double hw = p.W / 2;
        m.Box("hob", -hw + 30, 40, 900, hw - 30, p.D - 40, 905);
        m.Rect(-hw + 30, 40, hw - 30, p.D - 40, "detail");
        foreach (var (x, y, r) in new[] { (-140.0, 180.0, 90.0), (140.0, 180.0, 70.0), (-140.0, 420.0, 70.0), (140.0, 420.0, 90.0) })
            m.Circle(x, y, r);
        m.Use("cook", Front(p));
    }

    /// <summary>Fridge, dishwasher and washing machine: one box, each with its own mark.</summary>
    static void Appliance(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        if (p.Type == "dishwasher") CarcassWithTop(p, m);
        else
        {
            m.Box("body", -hw, 0, 0, hw, d, p.H);
            m.Rect(-hw, 0, hw, d);
        }
        switch (p.Type)
        {
            case "fridge":
                m.Line(-hw, 0, hw, d);
                m.Use("get_food", Front(p));
                break;
            case "dishwasher":
                m.Rect(-hw + 50, 50, hw - 50, d - 50);
                m.Use("load", Front(p));
                break;
            default:
                m.Circle(0, d / 2, 0.35 * p.W);
                m.Circle(0, d / 2, 0.25 * p.W, "detail");
                m.Use("laundry", Front(p));
                break;
        }
    }

    static void Worktop(Piece p, Model m)
    {
        double hw = p.W / 2;
        m.Box("top", -hw, 0, p.Z0, hw, p.D, p.Z0 + p.H);
        m.Rect(-hw, 0, hw, p.D);
        m.Use("prepare", Front(p));
    }

    static void Wc(Piece p, Model m)
    {
        double hw = p.W / 2;
        const double cistern = 180;
        m.Box("cistern", -hw, 0, 0, hw, cistern, p.H);
        var ry = (p.D - cistern) / 2;
        m.Prism("bowl", 0, cistern + ry, hw - 10, ry, 0, 400);
        m.Rect(-hw, 0, hw, cistern);
        m.Ellipse(0, cistern + ry, hw - 10, ry);
        m.Ellipse(0, cistern + ry + 20, hw - 70, ry - 70, "detail");
        m.Use("use_wc", At(0, cistern + ry, 400, 0, 1), At(0, p.D + 400, 0, 0, -1));
    }

    static void Basin(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("vanity", -hw, 0, 300, hw, d - 20, 800);
        m.Box("top", -hw, 0, 800, hw, d, p.H);
        m.Rect(-hw, 0, hw, d);
        m.Ellipse(0, d / 2 + 20, hw - 80, d / 2 - 90);
        m.Circle(0, d / 2 + 20, 20, "detail");
        m.Use("wash", Front(p));
    }

    static void Shower(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("tray", -hw, 0, 0, hw, d, p.H);
        m.Rect(-hw, 0, hw, d);
        m.Line(-hw, 0, hw, d);
        m.Line(-hw, d, hw, 0);
        m.Circle(0, d / 2, 40, "detail");
        m.Use("shower", At(0, d / 2, p.H, 0, 1), At(0, d + 300, 0, 0, -1));
    }

    static void Bath(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("body", -hw, 0, 0, hw, d, p.H);
        m.Rect(-hw, 0, hw, d);
        m.RoundRect(-hw + 60, 60, hw - 60, d - 60, 150);
        m.Circle(-hw + 200, d / 2, 25, "detail");
        m.Use("bathe", At(0, d / 2, 100, 1, 0), At(0, d + 350, 0, 0, -1));
    }

    static void Shelf(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("back", -hw, 0, 0, hw, 15, p.H);
        m.Box("side", -hw, 0, 0, -hw + 18, d, p.H);
        m.Box("side", hw - 18, 0, 0, hw, d, p.H);
        for (var z = 0.0; z <= p.H - 18; z += Math.Floor((p.H - 18) / 5))
            m.Box("shelf", -hw + 18, 15, z, hw - 18, d, z + 18);
        m.Rect(-hw, 0, hw, d);
        m.Line(-hw + 18, 15, hw - 18, 15, "detail");
        m.Line(-hw + 18, 15, -hw + 18, d, "detail");
        m.Line(hw - 18, 15, hw - 18, d, "detail");
        m.Use("browse", Front(p));
    }

    static void OfficeChair(Piece p, Model m)
    {
        var c = p.D / 2;
        m.Prism("base", 0, c, 300, 300, 0, 60);
        m.Prism("column", 0, c, 25, 25, 60, 420);
        m.Prism("seat", 0, c, 240, 240, 420, 480);
        m.Box("back", -220, 30, 480, 220, 90, p.H);
        m.Circle(0, c, 240);
        m.Rect(-220, 30, 220, 90);
        m.Circle(0, c, 300, "detail");
        m.Use("sit", At(0, c, 450, 0, 1), At(0, p.D + 350, 0, 0, -1));
    }

    static void CoatRack(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("board", -hw, 0, p.Z0, hw, 20, p.Z0 + 200);
        m.Box("shelf", -hw, 0, p.Z0 + p.H - 30, hw, d, p.Z0 + p.H);
        // Hung above the plan cut: dashed.
        m.Rect(-hw, 0, hw, d, "outline", true);
        m.Use("hang", Front(p));
    }

    static void Bench(Piece p, Model m)
    {
        double hw = p.W / 2, d = p.D;
        m.Box("seat", -hw, 0, p.H - 40, hw, d, p.H);
        m.Box("end", -hw, 0, 0, -hw + 40, d, p.H - 40);
        m.Box("end", hw - 40, 0, 0, hw, d, p.H - 40);
        m.Rect(-hw, 0, hw, d);
        m.Use("sit", At(-p.W / 4, d / 2, p.H, 0, 1), At(-p.W / 4, d + 400, 0, 0, -1),
            At(p.W / 4, d / 2, p.H, 0, 1), At(p.W / 4, d + 400, 0, 0, -1));
    }

    // ---- What a request means -----------------------------------------------

    static string Norm(string text) =>
        string.Join(" ", (text ?? "").Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ')
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The piece a request names: an exact catalogue id, else a type, a name
    /// or an alias ("double bed", "toilet"). width (cm under 300, else mm)
    /// and seats pick the nearest size. Null when nothing matches.
    /// </summary>
    public static Piece Resolve(string item, double? width = null, int? seats = null)
    {
        var exact = Find(item);
        if (exact != null) return exact;
        var text = Norm(item);
        if (text.Length == 0) return null;
        if (text.EndsWith("s", StringComparison.Ordinal) && text.Length > 3 && !all.Any(p => Matches(p, text)))
            text = text.Substring(0, text.Length - 1);
        var found = all.Where(p => Matches(p, text)).ToList();
        if (found.Count == 0) return null;
        // An alias that names a kind of a type ("double bed") keeps that kind.
        // A bare type name ("bed") keeps every size of it.
        var aliased = found.Where(p => p.Aliases.Any(a => Norm(a) == text)).ToList();
        if (aliased.Count > 0 && aliased.Count < found.Count && !found.Any(p => Norm(p.Type) == text)) found = aliased;
        IEnumerable<Piece> pick = found;
        if (seats.HasValue)
            pick = pick.OrderBy(p => Math.Abs(p.Seats - seats.Value));
        if (width.HasValue)
        {
            var mm = width.Value < 300 ? width.Value * 10 : width.Value;
            pick = pick is IOrderedEnumerable<Piece> ordered
                ? ordered.ThenBy(p => Math.Abs(p.W - mm))
                : pick.OrderBy(p => Math.Abs(p.W - mm));
        }
        if (!seats.HasValue && !width.HasValue)
            return found.FirstOrDefault(p => p.Default && p.Aliases.Any(a => Norm(a) == text))
                ?? found.FirstOrDefault(p => p.Default) ?? found[0];
        return pick.First();
    }

    static bool Matches(Piece p, string text) =>
        Norm(p.Type) == text || Norm(p.Name) == text || Norm(p.Id) == text
        || p.Aliases.Any(a => Norm(a) == text);

    /// <summary>The catalogue's types, for a refusal that lists them.</summary>
    public static IEnumerable<string> Types() => all.Select(p => p.Type.Replace('_', ' ')).Distinct();

    // ---- The record ------------------------------------------------------------

    /// <summary>The catalogue record (spec §3), in forsk.furniture.v1.</summary>
    public static JObject Record(Piece piece)
    {
        var model = Generate(piece);
        var interactions = new JArray();
        foreach (var use in model.Interactions)
        {
            var slots = new JArray();
            for (var i = 0; i < use.Slots; i++) slots.Add(i);
            interactions.Add(new JObject { ["name"] = use.Name, ["slots"] = slots });
        }
        return new JObject
        {
            ["id"] = piece.Id,
            ["type"] = piece.Type,
            ["schema"] = Schema,
            ["rev"] = Rev,
            ["name"] = piece.Name,
            ["footprint_mm"] = new JArray(piece.W, piece.D, piece.Z0 + piece.H),
            ["room_tags"] = new JArray(piece.Rooms),
            ["fixed"] = piece.Fixed,
            ["capacity"] = model.Interactions.Count == 0 ? 0 : model.Interactions.Max(i => i.Slots),
            ["interactions"] = interactions,
            ["analysis"] = new JObject { ["wet"] = IsWet(piece) }
        };
    }

    static bool IsWet(Piece piece) =>
        new[] { "sink", "dishwasher", "wc", "basin", "shower", "bath", "washing_machine" }.Contains(piece.Type);

    /// <summary>
    /// Why a generated piece is not a valid catalogue entry, else null: an id
    /// that does not read, a part outside the footprint, no symbol, a play
    /// point without its pair (spec §4, fail closed).
    /// </summary>
    public static string Check(Piece piece)
    {
        if (string.IsNullOrEmpty(piece.Id) || piece.Id.Any(c => !(char.IsLetterOrDigit(c) || c == '.' || c == '_')))
            return "id " + piece.Id + " does not read";
        var model = Generate(piece);
        if (model.Parts.Count == 0) return "no 3D parts";
        if (Plan(piece, 100).Count(m => m.Part == "outline") < 2) return "no plan outline";
        const double slack = 1;
        double hw = piece.W / 2;
        foreach (var part in model.Parts)
            if (part.X0 < -hw - slack || part.X1 > hw + slack || part.Y0 < -slack || part.Y1 > piece.D + slack
                || part.Z0 < piece.Z0 - slack || part.Z1 > piece.Z0 + piece.H + slack)
                return "part " + part.Name + " leaves the footprint";
        foreach (var mark in model.Marks)
        {
            var box = MarkBox(mark);
            if (box[0] < -hw - slack || box[2] > hw + slack || box[1] < -slack || box[3] > piece.D + slack)
                return "a plan mark leaves the footprint";
        }
        foreach (var use in model.Interactions)
            for (var slot = 0; slot < use.Slots; slot++)
            {
                var tail = ":" + use.Name + ":" + slot.ToString(CultureInfo.InvariantCulture);
                if (!model.Spots.Any(s => s.Name == "ip" + tail) || !model.Spots.Any(s => s.Name == "ap" + tail))
                    return "slot " + tail.Substring(1) + " lacks its points";
            }
        return null;
    }

    /// <summary>A mark's bounds [x0 y0 x1 y1] in the piece's frame.</summary>
    public static double[] MarkBox(Mark mark)
    {
        if (mark.Shape == "line")
            return new[] { Math.Min(mark.X0, mark.X1), Math.Min(mark.Y0, mark.Y1), Math.Max(mark.X0, mark.X1), Math.Max(mark.Y0, mark.Y1) };
        var xs = new List<double>();
        var ys = new List<double>();
        for (var a = mark.A0; a <= mark.A1 + 1e-9; a += Math.Min(5, mark.A1 - mark.A0))
        {
            xs.Add(mark.Cx + mark.R * Math.Cos(a * Math.PI / 180));
            ys.Add(mark.Cy + mark.R * Math.Sin(a * Math.PI / 180));
            if (mark.A1 - mark.A0 <= 0) break;
        }
        return new[] { xs.Min(), ys.Min(), xs.Max(), ys.Max() };
    }

    // ---- Placement -----------------------------------------------------------

    /// <summary>Where a piece stands: its origin and its x axis (unit). y is x turned a quarter left.</summary>
    public struct Frame
    {
        public double Ox, Oy, Ux, Uy;

        public Frame(double ox, double oy, double ux, double uy)
        {
            var len = Math.Sqrt(ux * ux + uy * uy);
            Ox = ox;
            Oy = oy;
            Ux = len > 0 ? ux / len : 1;
            Uy = len > 0 ? uy / len : 0;
        }

        public Pt ToWorld(double x, double y) => new Pt(Ox + x * Ux - y * Uy, Oy + x * Uy + y * Ux);

        /// <summary>Degrees of the x axis from world x, counter-clockwise, 0 to 360.</summary>
        public double Degrees
        {
            get
            {
                var a = Math.Atan2(Uy, Ux) * 180 / Math.PI;
                return a < 0 ? a + 360 : a;
            }
        }
    }

    /// <summary>Something placed, as a footprint in plan with its height range.</summary>
    public sealed class Footprint
    {
        public Pt[] Corners;
        public double Z0, Z1;
        public string Label;
    }

    public static Footprint FootprintOf(Piece piece, Frame frame, string label = null) => new Footprint
    {
        Corners = new[]
        {
            frame.ToWorld(-piece.W / 2, 0), frame.ToWorld(piece.W / 2, 0),
            frame.ToWorld(piece.W / 2, piece.D), frame.ToWorld(-piece.W / 2, piece.D)
        },
        Z0 = piece.Z0,
        Z1 = piece.Z0 + piece.H,
        Label = label ?? piece.Name.ToLowerInvariant()
    };

    /// <summary>The frame that puts a piece's centre at (x, y), turned by degrees.</summary>
    public static Frame Centred(Piece piece, double x, double y, double degrees)
    {
        var a = degrees * Math.PI / 180;
        double ux = Math.Cos(a), uy = Math.Sin(a);
        // The centre sits D/2 along y from the origin.
        return new Frame(x + uy * piece.D / 2, y - ux * piece.D / 2, ux, uy);
    }

    public static Pt CentreOf(Piece piece, Frame frame) => frame.ToWorld(0, piece.D / 2);

    /// <summary>The ring counter-clockwise, its closing duplicate dropped.</summary>
    public static List<Pt> Ccw(IList<Pt> ring)
    {
        var pts = ring.ToList();
        if (pts.Count > 1 && Dist(pts[0], pts[pts.Count - 1]) < 1e-6) pts.RemoveAt(pts.Count - 1);
        if (RoomDetect.Area(pts) < 0) pts.Reverse();
        return pts;
    }

    static double Dist(Pt a, Pt b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// <summary>
    /// Null when the footprint fits: inside the room, clear of every other
    /// footprint whose heights overlap. Else the plain reason.
    /// </summary>
    public static string Clash(Footprint piece, IList<Pt> room, IEnumerable<Footprint> others)
    {
        var ring = Ccw(room);
        var shrunk = Shrink(piece.Corners, 5);
        if (shrunk.Any(c => !RoomDetect.Contains(ring, c)))
            return "it would stand outside the room";
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (RoomDetect.Contains(shrunk, a)) return "a wall corner is in the way";
            for (var j = 0; j < 4; j++)
                if (Cross(a, b, shrunk[j], shrunk[(j + 1) % 4])) return "it would cross a wall";
        }
        foreach (var other in others ?? Enumerable.Empty<Footprint>())
        {
            if (other.Z1 <= piece.Z0 + 1 || other.Z0 >= piece.Z1 - 1) continue;
            if (Overlap(shrunk, other.Corners)) return "it would overlap the " + other.Label;
        }
        return null;
    }

    static Pt[] Shrink(Pt[] corners, double by)
    {
        double cx = corners.Average(c => c.X), cy = corners.Average(c => c.Y);
        return corners.Select(c =>
        {
            var d = Dist(c, new Pt(cx, cy));
            var k = d > by ? (d - by) / d : 0;
            return new Pt(cx + (c.X - cx) * k, cy + (c.Y - cy) * k);
        }).ToArray();
    }

    /// <summary>Two segments cross where each one's ends lie on both sides of the other.</summary>
    static bool Cross(Pt a, Pt b, Pt c, Pt d)
    {
        double Side(Pt p, Pt q, Pt r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        var d1 = Side(a, b, c);
        var d2 = Side(a, b, d);
        var d3 = Side(c, d, a);
        var d4 = Side(c, d, b);
        return (d1 > 1e-6 && d2 < -1e-6 || d1 < -1e-6 && d2 > 1e-6) && (d3 > 1e-6 && d4 < -1e-6 || d3 < -1e-6 && d4 > 1e-6);
    }

    /// <summary>Convex quads overlap unless one of their edges separates them.</summary>
    static bool Overlap(IList<Pt> a, IList<Pt> b)
    {
        foreach (var poly in new[] { a, b })
            for (var i = 0; i < poly.Count; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % poly.Count];
                double nx = q.Y - p.Y, ny = p.X - q.X;
                double minA = double.MaxValue, maxA = double.MinValue, minB = double.MaxValue, maxB = double.MinValue;
                foreach (var c in a) { var v = nx * c.X + ny * c.Y; minA = Math.Min(minA, v); maxA = Math.Max(maxA, v); }
                foreach (var c in b) { var v = nx * c.X + ny * c.Y; minB = Math.Min(minB, v); maxB = Math.Max(maxB, v); }
                if (maxA <= minB + 1e-6 || maxB <= minA + 1e-6) return false;
            }
        return true;
    }

    /// <summary>A placement and the words for the receipt.</summary>
    public sealed class Placement
    {
        public Frame Frame;
        public string Where;
    }

    /// <summary>
    /// Where a piece goes in a room, or null with why. A wall piece stands
    /// back to the longest wall that holds it, centred, sliding along it in
    /// 50 mm steps when the middle is taken; a free piece stands at the
    /// room's middle, turned along its longest wall, then steps outward.
    /// near, when given, picks the wall or spot nearest to it first.
    /// </summary>
    public static Placement Place(Piece piece, IList<Pt> room, IList<Footprint> others, Pt? near, out string why)
    {
        why = null;
        var ring = Ccw(room);
        if (ring.Count < 3) { why = "the room has no outline"; return null; }
        var edges = Enumerable.Range(0, ring.Count)
            .Select(i => new { A = ring[i], B = ring[(i + 1) % ring.Count], I = i })
            .Where(e => Dist(e.A, e.B) > 1)
            .ToList();
        var longest = edges.OrderByDescending(e => Dist(e.A, e.B)).First();
        string last = null;

        if (piece.Place == Wall)
        {
            var order = near.HasValue
                ? edges.OrderBy(e => SegDist(near.Value, e.A, e.B)).ToList()
                : edges.OrderByDescending(e => Dist(e.A, e.B)).ToList();
            foreach (var e in order)
            {
                var len = Dist(e.A, e.B);
                if (len < piece.W - 1) continue;
                double ux = (e.B.X - e.A.X) / len, uy = (e.B.Y - e.A.Y) / len;
                var start = near.HasValue
                    ? Math.Max(piece.W / 2, Math.Min(len - piece.W / 2, (near.Value.X - e.A.X) * ux + (near.Value.Y - e.A.Y) * uy))
                    : len / 2;
                foreach (var s in Slide(start, piece.W / 2, len - piece.W / 2, 50))
                {
                    var frame = new Frame(e.A.X + ux * s, e.A.Y + uy * s, ux, uy);
                    last = Clash(FootprintOf(piece, frame), ring, others);
                    if (last == null) return new Placement { Frame = frame, Where = "against " + Facing(ux, uy) + " wall" };
                }
            }
            why = last == null
                ? "no wall in the room is " + Metres(piece.W) + " long"
                : "no free stretch of wall " + Metres(piece.W) + " long (" + last + ")";
            return null;
        }

        var lx = longest.B.X - longest.A.X;
        var ly = longest.B.Y - longest.A.Y;
        var degrees = Math.Atan2(ly, lx) * 180 / Math.PI;
        var middle = near ?? Middle(ring);
        foreach (var offset in Spiral(50, 4000))
        {
            var frame = Centred(piece, middle.X + offset.X, middle.Y + offset.Y, degrees);
            last = Clash(FootprintOf(piece, frame), ring, others);
            if (last == null) return new Placement { Frame = frame, Where = near.HasValue ? "where you pointed" : "in the middle of the room" };
        }
        why = last == null ? "the room is too small" : "no free floor for it (" + last + ")";
        return null;
    }

    /// <summary>The middle, then alternately each side, step by step, within [lo, hi].</summary>
    static IEnumerable<double> Slide(double start, double lo, double hi, double step)
    {
        if (hi < lo) yield break;
        start = Math.Max(lo, Math.Min(hi, start));
        yield return start;
        for (var k = 1; ; k++)
        {
            var any = false;
            if (start + k * step <= hi + 1e-6) { any = true; yield return start + k * step; }
            if (start - k * step >= lo - 1e-6) { any = true; yield return start - k * step; }
            if (!any) yield break;
        }
    }

    /// <summary>Square rings of offsets around the origin, nearest first.</summary>
    static IEnumerable<Pt> Spiral(double step, double reach)
    {
        yield return new Pt(0, 0);
        for (var r = 1; r * step <= reach; r++)
            for (var i = -r; i <= r; i++)
                for (var j = -r; j <= r; j++)
                    if (Math.Abs(i) == r || Math.Abs(j) == r)
                        yield return new Pt(i * step, j * step);
    }

    /// <summary>The ring's area centroid, else its vertex mean.</summary>
    public static Pt Middle(IList<Pt> ring)
    {
        double a = 0, cx = 0, cy = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            var cross = p.X * q.Y - q.X * p.Y;
            a += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }
        if (Math.Abs(a) < 1e-9) return new Pt(ring.Average(p => p.X), ring.Average(p => p.Y));
        return new Pt(cx / (3 * a), cy / (3 * a));
    }

    static double SegDist(Pt p, Pt a, Pt b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
        return Dist(p, new Pt(a.X + t * dx, a.Y + t * dy));
    }

    /// <summary>A wall named by the way the room looks at it: the wall along x with the room above it is "the south".</summary>
    static string Facing(double ux, double uy)
    {
        // The room is to the left of the wall's direction; the wall is on the opposite side.
        double nx = uy, ny = -ux;
        if (Math.Abs(nx) > Math.Abs(ny)) return nx > 0 ? "the east" : "the west";
        return ny > 0 ? "the north" : "the south";
    }

    public static string Metres(double mm) => (mm / 1000).ToString("0.0#", CultureInfo.InvariantCulture) + " m";

    /// <summary>"Added a double bed 160 to the bedroom, against the north wall."</summary>
    public static string Receipt(Piece piece, string room, string where)
    {
        var text = "Added " + Article(piece.Name) + " " + Lower(piece.Name);
        if (!string.IsNullOrEmpty(room)) text += " to " + room;
        if (!string.IsNullOrEmpty(where)) text += ", " + where;
        return text + ".";
    }

    static string Lower(string name) => name.Length > 1 && char.IsUpper(name[0]) && !char.IsUpper(name[1]) ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name;

    static string Article(string name) => "aeiou".IndexOf(char.ToLowerInvariant(name[0])) >= 0 ? "an" : "a";

    /// <summary>The next free id: F01, F02, …</summary>
    public static string NextId(IEnumerable<string> taken)
    {
        var used = new HashSet<int>();
        foreach (var id in taken ?? Enumerable.Empty<string>())
            if (id != null && id.Length > 1 && (id[0] == 'F' || id[0] == 'f')
                && int.TryParse(id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                used.Add(n);
        var next = 1;
        while (used.Contains(next)) next++;
        return "F" + next.ToString("00", CultureInfo.InvariantCulture);
    }
}
