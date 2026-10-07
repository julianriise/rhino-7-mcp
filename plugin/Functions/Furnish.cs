using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// FU.7: furnish a room by rules, not by guessing, so the same room always
/// gives the same layout. Ported from planwire's interior solvers (Julian's
/// own repo): the OBB inside-room and overlap checks, a free safe zone in
/// front of every piece that must stay clear, the wall furthest from the
/// door, the corner a desk snaps into, a 1D slide along a wall, and the
/// spacious / relaxed / compact densities. The rules are residential, one
/// per room type. Doors keep their swing clear and tall pieces keep off the
/// windows. Millimetres. No RhinoCommon.
/// </summary>
public static class Furnish
{
    public const string Spacious = "spacious";
    public const string Relaxed = "relaxed";
    public const string Compact = "compact";
    public static readonly string[] Densities = { Spacious, Relaxed, Compact };

    /// <summary>The usual layout, and a second one that takes the next-best wall for the room's main piece.</summary>
    public const string Consistent = "consistent";
    public const string Creative = "creative";

    /// <summary>A door's swing and approach: a square this deep at least, in front of the opening, on the room side.</summary>
    public const double DoorZoneMinMm = 900;
    /// <summary>In front of a window, nothing taller than a worktop.</summary>
    public const double WindowZoneMm = 300;
    public const double WindowZoneFromMm = 950;
    /// <summary>Circulation zones keep the floor clear up to here; a wall unit above it may hang over them.</summary>
    public const double ZoneTopMm = 1400;

    /// <summary>A door or a window in the room's outline: its middle and its width.</summary>
    public sealed class Opening
    {
        public Pt Centre;
        public double Width;
        public bool Door;
    }

    /// <summary>One piece of a layout and where it goes.</summary>
    public sealed class Item
    {
        public Furniture.Piece Piece;
        public Furniture.Frame Frame;
    }

    public sealed class Layout
    {
        public readonly List<Item> Items = new List<Item>();
        /// <summary>Pieces the rules wanted but left out, with why: "no bedside tables (no room beside the bed)".</summary>
        public readonly List<string> Skipped = new List<string>();
        /// <summary>Null when the room is furnished, else why not.</summary>
        public string Why;
    }

    /// <summary>The room types Forsk furnishes.</summary>
    public static readonly string[] Types =
    {
        RoomTypes.Bedroom, RoomTypes.Living, RoomTypes.Dining, RoomTypes.Kitchen, RoomTypes.Bathroom,
        RoomTypes.Wc, RoomTypes.Office, RoomTypes.Hall, RoomTypes.Laundry, RoomTypes.Storage
    };

    /// <summary>Free floor in front of a piece, by density: where you stand at a wardrobe or a basin.</summary>
    public static double Front(string density) => density == Spacious ? 800 : density == Compact ? 600 : 700;

    /// <summary>Beside a bed and at its foot.</summary>
    public static double BedSide(string density) => density == Spacious ? 700 : 600;

    /// <summary>Behind a dining chair: pulled out and walked past.</summary>
    public static double ChairPull(string density) => density == Spacious ? 900 : density == Compact ? 600 : 750;

    sealed class Edge
    {
        public Pt A, B;
        public double Len, Ux, Uy;
        public int Index;
        public bool HasDoor, HasWindow;
        public double DoorDist;
        public Pt Mid => new Pt((A.X + B.X) / 2, (A.Y + B.Y) / 2);
    }

    /// <summary>The room being furnished: what stands, and what must stay clear.</summary>
    sealed class Ctx
    {
        public List<Pt> Ring;
        public List<Edge> Edges;
        public string Density;
        public readonly List<Furniture.Footprint> Bodies = new List<Furniture.Footprint>();
        public readonly List<Furniture.Footprint> Zones = new List<Furniture.Footprint>();
        public List<Pt> Doors;
        public Layout Out;
        public string Last;
    }

    /// <summary>A rectangle in the piece's own frame that must stay free: x0 y0 x1 y1.</summary>
    struct Zone
    {
        public double X0, Y0, X1, Y1;
        public Zone(double x0, double y0, double x1, double y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
    }

    static Furniture.Piece P(string id) => Furniture.Find(id) ?? throw new InvalidOperationException("No catalogue piece " + id);

    /// <summary>
    /// The layout for a room of this type, or Why. existing pieces (the whole
    /// file's) stay where they are, each with the free floor in front of it,
    /// and are furnished around; a kind the room already has is not added again.
    /// </summary>
    public static Layout Plan(string roomType, IList<Pt> room, IList<Opening> openings, IList<Item> existing,
        string density = Relaxed, string variant = Consistent)
    {
        var layout = new Layout();
        density = Densities.Contains(density) ? density : Relaxed;
        var ring = Furniture.Ccw(room);
        if (ring.Count < 3) { layout.Why = "the room has no outline"; return layout; }
        var ctx = new Ctx { Ring = ring, Density = density, Out = layout };
        existing = existing ?? new List<Item>();
        foreach (var item in existing)
        {
            ctx.Bodies.Add(Furniture.FootprintOf(item.Piece, item.Frame));
            // A piece against a wall keeps the floor in front of it, as it did when it was placed.
            if (item.Piece.Place == Furniture.Wall && item.Piece.Z0 < ZoneTopMm)
                ctx.Zones.Add(ZoneFoot(item.Piece, item.Frame, FrontZone(ctx).Invoke(item.Piece)[0], "the space in front of the " + item.Piece.Name.ToLowerInvariant()));
        }
        ctx.Edges = BuildEdges(ring);
        ctx.Doors = new List<Pt>();
        foreach (var opening in openings ?? new List<Opening>())
            AddOpening(ctx, opening);
        var have = new HashSet<string>(existing
            .Where(i => RoomDetect.Contains(ring, Furniture.CentreOf(i.Piece, i.Frame)))
            .Select(i => i.Piece.Type));
        var skip = variant == Creative ? 1 : 0;

        switch (roomType)
        {
            case RoomTypes.Bedroom: Bedroom(ctx, have, skip); break;
            case RoomTypes.Living: Living(ctx, have, skip); break;
            case RoomTypes.Dining: Dining(ctx, have, skip); break;
            case RoomTypes.Kitchen: Kitchen(ctx, have, skip); break;
            case RoomTypes.Bathroom: Bathroom(ctx, have, skip, true); break;
            case RoomTypes.Wc: Bathroom(ctx, have, skip, false); break;
            case RoomTypes.Office: Office(ctx, have, skip); break;
            case RoomTypes.Hall: Hall(ctx, have, skip); break;
            case RoomTypes.Laundry: Laundry(ctx, have, skip); break;
            case RoomTypes.Storage: Storage(ctx, have, skip); break;
            default:
                layout.Why = "Forsk furnishes " + string.Join(", ", Types.Take(Types.Length - 1)) + " and " + Types.Last()
                    + " rooms; set the room's type first";
                break;
        }
        return layout;
    }

    // ---- Room rules ------------------------------------------------------------

    static void Bedroom(Ctx ctx, HashSet<string> have, int skip)
    {
        var side = BedSide(ctx.Density);
        if (!have.Contains("bed"))
        {
            var sizes = ctx.Density == Spacious ? new[] { 180, 160, 140, 120, 90 }
                : ctx.Density == Compact ? new[] { 140, 120, 90 } : new[] { 160, 140, 120, 90 };
            // The head to a solid wall, as far from the door as the room allows.
            var walls = ctx.Edges.Where(e => !e.HasDoor).OrderBy(e => e.HasWindow).ThenByDescending(e => e.DoorDist).ToList();
            Item bed = null;
            foreach (var w in sizes)
            {
                var piece = P(w >= 140 ? "bed.double." + w + "x200" : "bed.single." + w + "x200");
                var zones = w >= 140
                    ? new[] { new Zone(-piece.W / 2 - side, 450, -piece.W / 2, piece.D), new Zone(piece.W / 2, 450, piece.W / 2 + side, piece.D), new Zone(-piece.W / 2, piece.D, piece.W / 2, piece.D + side) }
                    : new[] { new Zone(piece.W / 2, 450, piece.W / 2 + side, piece.D), new Zone(-piece.W / 2, piece.D, piece.W / 2 + side, piece.D + side) };
                bed = OnWall(ctx, piece, walls, zones, w >= 140 ? Along.Middle : Along.Start, skip);
                if (bed != null) break;
            }
            if (bed == null)
            {
                ctx.Out.Why = "no wall in the room holds even a 90 bed with " + Furniture.Metres(side) + " beside it and at its foot ("
                    + (ctx.Last ?? "too small") + ")";
                return;
            }
            if (bed.Piece.W >= 1400)
            {
                // A bedside table each side of the head.
                var table = P("bedside_table.45");
                foreach (var dx in new[] { -1, 1 })
                {
                    var x = dx * (bed.Piece.W / 2 + table.W / 2 + 20);
                    var origin = bed.Frame.ToWorld(x, 0);
                    if (TryAt(ctx, table, new Furniture.Frame(origin.X, origin.Y, bed.Frame.Ux, bed.Frame.Uy), new Zone[0]) == null)
                        Skip(ctx, "a bedside table on one side (no room beside the bed)");
                }
            }
        }
        if (!have.Contains("wardrobe"))
            Optional(ctx, new[] { "wardrobe.100", "wardrobe.50" }, ctx.Edges.Where(e => !e.HasWindow).OrderByDescending(e => e.Len),
                FrontZone(ctx), Along.Start, "a wardrobe");
    }

    static void Living(Ctx ctx, HashSet<string> have, int skip)
    {
        Item sofa = null;
        if (!have.Contains("sofa"))
        {
            // The sofa looks at the window, else it stands on the longest solid wall.
            var window = ctx.Edges.Where(e => e.HasWindow).OrderByDescending(e => e.Len).FirstOrDefault();
            var walls = ctx.Edges.Where(e => !e.HasDoor && !e.HasWindow)
                .OrderByDescending(e => window == null ? e.Len : Facing(e, window))
                .ThenByDescending(e => e.Len).ToList();
            var tableGap = 450.0;
            foreach (var id in new[] { "sofa.3seat", "sofa.2seat" })
            {
                var piece = P(id);
                var reach = piece.D + tableGap + 600 + ctx.Density switch { Spacious => 700, Compact => 450, _ => 600 };
                sofa = OnWall(ctx, piece, walls, new[] { new Zone(-piece.W / 2, piece.D, piece.W / 2, reach) }, Along.Middle, skip);
                if (sofa != null) break;
            }
            if (sofa == null)
            {
                ctx.Out.Why = "no solid wall holds a 2-seat sofa with room in front of it (" + (ctx.Last ?? "too small") + ")";
                return;
            }
            // The zone in front was for the table and the walk past it: the table goes in it.
            ctx.Zones.RemoveAt(ctx.Zones.Count - 1);
            var coffee = P("coffee_table.120x60");
            var y = sofa.Piece.D + tableGap;
            var origin = sofa.Frame.ToWorld(0, y);
            if (TryAt(ctx, coffee, new Furniture.Frame(origin.X, origin.Y, sofa.Frame.Ux, sofa.Frame.Uy), new Zone[0]) == null)
                Skip(ctx, "a coffee table (no free floor in front of the sofa)");
            else
            {
                // An armchair at one end of the table, turned to it.
                var chair = P("armchair");
                var centreY = y + coffee.D / 2;
                var placed = false;
                foreach (var dx in new[] { 1, -1 })
                {
                    var c = sofa.Frame.ToWorld(dx * (coffee.W / 2 + 300 + chair.D / 2), centreY);
                    // Facing the table: its y axis points back along -dx.
                    double fx = -dx * sofa.Frame.Ux, fy = -dx * sofa.Frame.Uy;
                    var degrees = Math.Atan2(fy, fx) * 180 / Math.PI - 90;
                    if (TryAt(ctx, chair, Furniture.Centred(chair, c.X, c.Y, degrees), new Zone[0]) != null) { placed = true; break; }
                }
                if (!placed) Skip(ctx, "an armchair (no room beside the coffee table)");
            }
            // The TV bench on the wall the sofa looks at.
            var opposite = ctx.Edges.Where(e => !e.HasDoor && Facing(e, EdgeOf(ctx, sofa)) > 0.9).OrderByDescending(e => e.Len).ToList();
            if (!have.Contains("tv_bench"))
                Optional(ctx, new[] { "tv_bench.180" }, opposite, FrontZone(ctx), Along.Middle, "a TV bench");
        }
        if (!have.Contains("bookshelf"))
            Optional(ctx, new[] { "bookshelf.80" }, ctx.Edges.Where(e => !e.HasWindow).OrderByDescending(e => e.Len), FrontZone(ctx), Along.End, "a bookshelf");
    }

    static void Dining(Ctx ctx, HashSet<string> have, int skip)
    {
        if (have.Contains("dining_table")) return;
        if (!Table(ctx, new[] { "dining_table.6seat", "dining_table.4seat", "dining_table.round.4seat" }, skip))
            ctx.Out.Why = "no free floor for a table for 4 with " + Furniture.Metres(ChairPull(ctx.Density)) + " to pull the chairs out (" + (ctx.Last ?? "too small") + ")";
    }

    /// <summary>A table in the middle with its chairs at its seats, and the pull-back zone around them.</summary>
    static bool Table(Ctx ctx, string[] ids, int skip)
    {
        var pull = ChairPull(ctx.Density);
        var chair = P("chair");
        var middle = Furniture.Middle(ctx.Ring);
        var longest = ctx.Edges.OrderByDescending(e => e.Len).First();
        var along = Math.Atan2(longest.Uy, longest.Ux) * 180 / Math.PI;
        foreach (var id in ids)
        {
            var table = P(id);
            var model = Furniture.Generate(table);
            var seats = model.Spots.Where(s => s.Name.StartsWith("ip:eat:", StringComparison.Ordinal)).ToList();
            var found = 0;
            foreach (var degrees in new[] { along, along + 90 })
                foreach (var offset in Spiral(100, 2000))
                {
                    var frame = Furniture.Centred(table, middle.X + offset.X, middle.Y + offset.Y, degrees);
                    var body = Furniture.FootprintOf(table, frame);
                    if (Furniture.Clash(body, ctx.Ring, ctx.Bodies.Concat(ctx.Zones)) != null) continue;
                    // Every chair and the space behind it must fit too.
                    var chairs = new List<Item>();
                    var zones = new List<Furniture.Footprint>();
                    string why = null;
                    foreach (var seat in seats)
                    {
                        var at = frame.ToWorld(seat.X, seat.Y);
                        var fx = frame.Ux * seat.Fx - frame.Uy * seat.Fy;
                        var fy = frame.Uy * seat.Fx + frame.Ux * seat.Fy;
                        var cf = Furniture.Centred(chair, at.X, at.Y, Math.Atan2(fy, fx) * 180 / Math.PI - 90);
                        var cb = Furniture.FootprintOf(chair, cf);
                        why = Furniture.Clash(cb, ctx.Ring, ctx.Bodies.Concat(ctx.Zones).Concat(chairs.Select(c => Furniture.FootprintOf(c.Piece, c.Frame))));
                        if (why != null) break;
                        var zone = ZoneFoot(chair, cf, new Zone(-chair.W / 2, -pull, chair.W / 2, 0), "the space behind a chair");
                        why = Furniture.Clash(zone, ctx.Ring, ctx.Bodies);
                        if (why != null) break;
                        chairs.Add(new Item { Piece = chair, Frame = cf });
                        zones.Add(zone);
                    }
                    if (why != null) { ctx.Last = why; continue; }
                    if (found++ < skip) continue;
                    Commit(ctx, table, frame, new Zone[0]);
                    foreach (var c in chairs) Commit(ctx, c.Piece, c.Frame, new Zone[0]);
                    ctx.Zones.AddRange(zones);
                    return true;
                }
            if (found > 0 && skip > 0)
            {
                // No second spot: the first one it is.
                return Table(ctx, new[] { id }, 0);
            }
        }
        return false;
    }

    static void Kitchen(Ctx ctx, HashSet<string> have, int skip)
    {
        if (!have.Contains("kitchen_base") && !have.Contains("sink") && !have.Contains("hob"))
        {
            // The run along the longest wall without a door, a wall without a window first.
            var walls = ctx.Edges.Where(e => !e.HasDoor).OrderBy(e => e.HasWindow).ThenByDescending(e => e.Len).ToList();
            if (skip > 0 && walls.Count > 1) walls = walls.Skip(1).Concat(walls.Take(1)).ToList();
            var placed = false;
            foreach (var wall in walls)
            {
                if (Run(ctx, wall)) { placed = true; break; }
            }
            if (!placed)
            {
                ctx.Out.Why = "no wall without a door holds a kitchen run of fridge, hob and sink (2.0 m) with "
                    + Furniture.Metres(Front(ctx.Density)) + " in front (" + (ctx.Last ?? "too short") + ")";
                return;
            }
        }
        // A table for four when the floor allows.
        if (!have.Contains("dining_table") && RoomDetect.Area(ctx.Ring) > 12e6)
            if (!Table(ctx, new[] { "dining_table.4seat", "dining_table.round.4seat" }, 0))
                Skip(ctx, "a table (no free floor for it)");
    }

    /// <summary>
    /// Fridge at the end, then (as the wall allows) a base unit, the sink, the
    /// dishwasher next to it, a base unit and the hob, base units to the
    /// wall's end, on the 600 module, with a wall unit over each base unit
    /// clear of the windows.
    /// </summary>
    static bool Run(Ctx ctx, Edge wall)
    {
        var before = ctx.Out.Items.Count;
        var bodies = ctx.Bodies.Count;
        var zones = ctx.Zones.Count;
        // The longest run the wall takes, from the wall's start, skipping what is in the way.
        var order = wall.Len >= 3800
            ? new List<string> { "kitchen.fridge.60", "kitchen.base.60", "kitchen.sink.80", "kitchen.dishwasher.60", "kitchen.base.60", "kitchen.hob.60" }
            : wall.Len >= 3200
                ? new List<string> { "kitchen.fridge.60", "kitchen.base.60", "kitchen.sink.80", "kitchen.dishwasher.60", "kitchen.hob.60" }
                : wall.Len >= 2600
                    ? new List<string> { "kitchen.fridge.60", "kitchen.sink.80", "kitchen.dishwasher.60", "kitchen.hob.60" }
                    : new List<string> { "kitchen.fridge.60", "kitchen.sink.80", "kitchen.hob.60" };
        var s = 0.0;
        var core = new HashSet<string> { "kitchen.fridge.60", "kitchen.hob.60", "kitchen.sink.80" };
        var got = new HashSet<string>();
        foreach (var id in order)
        {
            var piece = P(id);
            var ok = false;
            for (; s + piece.W <= wall.Len + 1e-6; s += 100)
            {
                var frame = OnEdge(wall, s + piece.W / 2);
                var zone = new[] { new Zone(-piece.W / 2, piece.D, piece.W / 2, piece.D + Front(ctx.Density)) };
                if (TryAt(ctx, piece, frame, zone) != null) { ok = true; s += piece.W; break; }
            }
            if (ok) got.Add(id);
            else if (core.Contains(id)) break;
        }
        if (!core.All(got.Contains))
        {
            // Undo the partial run.
            ctx.Out.Items.RemoveRange(before, ctx.Out.Items.Count - before);
            ctx.Bodies.RemoveRange(bodies, ctx.Bodies.Count - bodies);
            ctx.Zones.RemoveRange(zones, ctx.Zones.Count - zones);
            return false;
        }
        // Base units fill what is left of the wall.
        var basePiece = P("kitchen.base.60");
        for (; s + basePiece.W <= wall.Len + 1e-6; s += 100)
        {
            var frame = OnEdge(wall, s + basePiece.W / 2);
            if (TryAt(ctx, basePiece, frame, new[] { new Zone(-300, 600, 300, 600 + Front(ctx.Density)) }) != null) s += basePiece.W - 100;
        }
        // A wall unit over every base unit, clear of the windows.
        var wallUnit = P("kitchen.wall.60");
        foreach (var item in ctx.Out.Items.Skip(before).Where(i => i.Piece.Id == "kitchen.base.60").ToList())
            TryAt(ctx, wallUnit, item.Frame, new Zone[0]);
        return true;
    }

    static void Bathroom(Ctx ctx, HashSet<string> have, int skip, bool full)
    {
        var front = Front(ctx.Density);
        // The wet wall: the longest wall without a door, filled from the end away from the door.
        var walls = ctx.Edges.Where(e => !e.HasDoor).OrderByDescending(e => e.Len).ToList();
        if (skip > 0 && walls.Count > 1) walls = walls.Skip(1).Concat(walls.Take(1)).ToList();
        walls.AddRange(ctx.Edges.Where(e => e.HasDoor));
        if (full && !have.Contains("shower") && !have.Contains("bath"))
        {
            var big = RoomDetect.Area(ctx.Ring) >= 5e6;
            var ids = big ? new[] { "bath.170", "shower.90", "shower.80" } : new[] { "shower.90", "shower.80" };
            Item wet = null;
            foreach (var id in ids)
            {
                var piece = P(id);
                wet = OnWall(ctx, piece, walls, new[] { new Zone(-piece.W / 2, piece.D, piece.W / 2, piece.D + front) }, Along.FarFromDoor, 0);
                if (wet != null) break;
            }
            if (wet == null)
            {
                ctx.Out.Why = "no wall holds an 80 shower with " + Furniture.Metres(front) + " in front of it (" + (ctx.Last ?? "too small") + ")";
                return;
            }
        }
        if (!have.Contains("wc"))
        {
            // TEK17 §12-9 asks 0.2 m beside the WC at least (0.9 m on one side in an accessible bathroom: FU.5).
            var wc = P("wc");
            var spare = ctx.Density == Spacious ? 900 : 200;
            var zones = new[] { new Zone(-wc.W / 2 - 200, 0, -wc.W / 2, wc.D), new Zone(wc.W / 2, 0, wc.W / 2 + spare, wc.D), new Zone(-wc.W / 2 - 200, wc.D, wc.W / 2 + spare, wc.D + front) };
            if (OnWall(ctx, wc, walls, zones, Along.FarFromDoor, 0) == null)
            {
                ctx.Out.Why = "no wall holds a WC with 0.2 m beside it and " + Furniture.Metres(front) + " in front (" + (ctx.Last ?? "too small") + ")";
                return;
            }
        }
        if (!have.Contains("basin"))
        {
            var basin = P("basin.60");
            if (OnWall(ctx, basin, walls, new[] { new Zone(-basin.W / 2, basin.D, basin.W / 2, basin.D + front) }, Along.FarFromDoor, 0) == null)
                Skip(ctx, "a basin (no free wall for it)");
        }
        if (full && !have.Contains("washing_machine"))
            Optional(ctx, new[] { "washing_machine" }, walls, FrontZone(ctx), Along.FarFromDoor, "a washing machine");
    }

    static void Office(Ctx ctx, HashSet<string> have, int skip)
    {
        if (!have.Contains("desk"))
        {
            // planwire's enclosed office: the desk's end at the window, along a side wall, in the corner away from the door.
            Item desk = null;
            foreach (var id in new[] { "desk.140", "desk.120" })
            {
                var piece = P(id);
                var zone = new[] { new Zone(-piece.W / 2, piece.D, piece.W / 2, piece.D + 900) };
                var sides = ctx.Edges.Where(e => !e.HasDoor && !e.HasWindow && Neighbours(ctx, e).Any(n => n.HasWindow))
                    .OrderByDescending(e => e.DoorDist).ToList();
                foreach (var side in sides)
                {
                    var atStart = Neighbour(ctx, side, false).HasWindow;
                    desk = OnWall(ctx, piece, new List<Edge> { side }, zone, atStart ? Along.Start : Along.End, skip);
                    if (desk != null) break;
                }
                desk = desk ?? OnWall(ctx, piece, ctx.Edges.Where(e => !e.HasDoor).OrderByDescending(e => e.DoorDist).ToList(), zone, Along.Middle, skip);
                if (desk != null) break;
            }
            if (desk == null)
            {
                ctx.Out.Why = "no wall holds a 1.2 m desk with room for the chair (" + (ctx.Last ?? "too small") + ")";
                return;
            }
            // The chair at the desk, facing it.
            var chair = P("office_chair");
            var at = desk.Frame.ToWorld(0, desk.Piece.D + chair.D / 2 + 20);
            // The chair faces the desk: its y axis is the desk's -y. It stands in the desk's own zone.
            var cf = Furniture.Centred(chair, at.X, at.Y, desk.Frame.Degrees + 180);
            if (TryAt(ctx, chair, cf, new Zone[0], ignoreZones: true) == null) Skip(ctx, "an office chair");
        }
        if (!have.Contains("shelf"))
            Optional(ctx, new[] { "shelf.80" }, ctx.Edges.Where(e => !e.HasWindow).OrderByDescending(e => e.Len), FrontZone(ctx), Along.Middle, "a shelf");
    }

    static void Hall(Ctx ctx, HashSet<string> have, int skip)
    {
        var walls = ctx.Edges.Where(e => !e.HasWindow).OrderByDescending(e => e.Len).ToList();
        if (!have.Contains("shoe_cabinet"))
        {
            var piece = P("shoe_cabinet.80");
            if (OnWall(ctx, piece, walls, FrontZone(ctx).Invoke(piece), Along.Start, skip) == null)
            {
                ctx.Out.Why = "no free wall for a shoe cabinet (" + (ctx.Last ?? "too small") + ")";
                return;
            }
        }
        if (!have.Contains("coat_rack"))
            Optional(ctx, new[] { "coat_rack.100" }, walls, p => new Zone[0], Along.Start, "a coat rack");
    }

    static void Laundry(Ctx ctx, HashSet<string> have, int skip)
    {
        if (!have.Contains("washing_machine"))
        {
            var piece = P("washing_machine");
            if (OnWall(ctx, piece, ctx.Edges.OrderByDescending(e => e.Len).ToList(), FrontZone(ctx).Invoke(piece), Along.Start, skip) == null)
            {
                ctx.Out.Why = "no free wall for a washing machine (" + (ctx.Last ?? "too small") + ")";
                return;
            }
        }
        if (!have.Contains("shelf"))
            Optional(ctx, new[] { "shelf.80" }, ctx.Edges.OrderByDescending(e => e.Len), FrontZone(ctx), Along.End, "a shelf");
    }

    static void Storage(Ctx ctx, HashSet<string> have, int skip)
    {
        if (have.Contains("shelf")) return;
        var piece = P("shelf.80");
        if (OnWall(ctx, piece, ctx.Edges.OrderByDescending(e => e.Len).ToList(), FrontZone(ctx).Invoke(piece), Along.Start, skip) == null)
            ctx.Out.Why = "no free wall for a shelf (" + (ctx.Last ?? "too small") + ")";
    }

    // ---- Placing -----------------------------------------------------------------

    enum Along { Middle, Start, End, FarFromDoor }

    static Func<Furniture.Piece, Zone[]> FrontZone(Ctx ctx) =>
        p => new[] { new Zone(-p.W / 2, p.D, p.W / 2, p.D + Front(ctx.Density)) };

    static void Optional(Ctx ctx, string[] ids, IEnumerable<Edge> walls, Func<Furniture.Piece, Zone[]> zones, Along along, string what)
    {
        var list = walls.ToList();
        foreach (var id in ids)
        {
            var piece = P(id);
            if (OnWall(ctx, piece, list, zones(piece), along, 0) != null) return;
        }
        Skip(ctx, what + " (no free wall for it)");
    }

    static void Skip(Ctx ctx, string what) => ctx.Out.Skipped.Add(what);

    /// <summary>
    /// The piece back to the first wall that holds it with its zones clear,
    /// sliding along the wall in 50 mm steps from the end asked for. skip
    /// passes over that many good walls first (the creative variant), and
    /// falls back to the first good one.
    /// </summary>
    static Item OnWall(Ctx ctx, Furniture.Piece piece, IList<Edge> walls, Zone[] zones, Along along, int skip)
    {
        Item first = null;
        var found = 0;
        foreach (var wall in walls)
        {
            if (wall.Len < piece.W - 1) continue;
            foreach (var s in Positions(ctx, wall, piece.W, along))
            {
                var frame = OnEdge(wall, s);
                if (!Fits(ctx, piece, frame, zones)) continue;
                if (found++ < skip)
                {
                    first = first ?? new Item { Piece = piece, Frame = frame };
                    break;
                }
                return Commit(ctx, piece, frame, zones);
            }
        }
        return first == null ? null : Commit(ctx, first.Piece, first.Frame, zones);
    }

    static IEnumerable<double> Positions(Ctx ctx, Edge wall, double width, Along along)
    {
        double lo = width / 2, hi = wall.Len - width / 2;
        if (hi < lo - 1e-6) yield break;
        var step = 50.0;
        var fromStart = along == Along.Start
            || along == Along.FarFromDoor && Near(ctx, wall.B) <= Near(ctx, wall.A);
        if (along == Along.Middle)
        {
            var mid = wall.Len / 2;
            yield return mid;
            for (var k = 1; ; k++)
            {
                var any = false;
                if (mid + k * step <= hi + 1e-6) { any = true; yield return mid + k * step; }
                if (mid - k * step >= lo - 1e-6) { any = true; yield return mid - k * step; }
                if (!any) yield break;
            }
        }
        if (fromStart)
            for (var s = lo; s <= hi + 1e-6; s += step) yield return s;
        else
            for (var s = hi; s >= lo - 1e-6; s -= step) yield return s;
    }

    /// <summary>How close a point is to the nearest door: far is good for a bed's head or a shower.</summary>
    static double Near(Ctx ctx, Pt p) => ctx.Doors.Count == 0 ? 0 : ctx.Doors.Min(d => Dist(d, p));

    static Furniture.Frame OnEdge(Edge wall, double s) =>
        new Furniture.Frame(wall.A.X + wall.Ux * s, wall.A.Y + wall.Uy * s, wall.Ux, wall.Uy);

    /// <summary>Its body inside the room, clear of every body and zone; its zones inside the room, clear of every body.</summary>
    static bool Fits(Ctx ctx, Furniture.Piece piece, Furniture.Frame frame, Zone[] zones, bool ignoreZones = false)
    {
        var body = Furniture.FootprintOf(piece, frame);
        ctx.Last = Furniture.Clash(body, ctx.Ring, ignoreZones ? ctx.Bodies : ctx.Bodies.Concat(ctx.Zones));
        if (ctx.Last != null) return false;
        foreach (var zone in zones)
        {
            var foot = ZoneFoot(piece, frame, zone, "the space in front of the " + piece.Name.ToLowerInvariant());
            ctx.Last = Furniture.Clash(foot, ctx.Ring, ctx.Bodies);
            if (ctx.Last != null)
            {
                ctx.Last = ctx.Last.Replace("it would", "the space it needs would");
                return false;
            }
        }
        return true;
    }

    static Item TryAt(Ctx ctx, Furniture.Piece piece, Furniture.Frame frame, Zone[] zones, bool ignoreZones = false) =>
        Fits(ctx, piece, frame, zones, ignoreZones) ? Commit(ctx, piece, frame, zones) : null;

    static Item Commit(Ctx ctx, Furniture.Piece piece, Furniture.Frame frame, Zone[] zones)
    {
        var item = new Item { Piece = piece, Frame = frame };
        ctx.Out.Items.Add(item);
        ctx.Bodies.Add(Furniture.FootprintOf(piece, frame));
        foreach (var zone in zones)
            ctx.Zones.Add(ZoneFoot(piece, frame, zone, "the space in front of the " + piece.Name.ToLowerInvariant()));
        return item;
    }

    static Furniture.Footprint ZoneFoot(Furniture.Piece piece, Furniture.Frame frame, Zone zone, string label) => new Furniture.Footprint
    {
        Corners = new[] { frame.ToWorld(zone.X0, zone.Y0), frame.ToWorld(zone.X1, zone.Y0), frame.ToWorld(zone.X1, zone.Y1), frame.ToWorld(zone.X0, zone.Y1) },
        Z0 = 0,
        Z1 = ZoneTopMm,
        Label = label
    };

    // ---- The room's walls, doors and windows ------------------------------------

    static List<Edge> BuildEdges(List<Pt> ring)
    {
        var edges = new List<Edge>();
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var len = Dist(a, b);
            if (len < 1) continue;
            edges.Add(new Edge { A = a, B = b, Len = len, Ux = (b.X - a.X) / len, Uy = (b.Y - a.Y) / len, Index = edges.Count });
        }
        return edges;
    }

    /// <summary>
    /// An opening on the outline: a door keeps a square the door's width
    /// (900 at least) clear in front of it; a window keeps tall pieces 300 off it.
    /// </summary>
    static void AddOpening(Ctx ctx, Opening opening)
    {
        var edge = ctx.Edges.OrderBy(e => SegDist(opening.Centre, e.A, e.B)).FirstOrDefault();
        if (edge == null || SegDist(opening.Centre, edge.A, edge.B) > 400) return;
        var s = (opening.Centre.X - edge.A.X) * edge.Ux + (opening.Centre.Y - edge.A.Y) * edge.Uy;
        var half = opening.Width / 2;
        var frame = new Furniture.Frame(edge.A.X + edge.Ux * s, edge.A.Y + edge.Uy * s, edge.Ux, edge.Uy);
        if (opening.Door)
        {
            edge.HasDoor = true;
            var depth = Math.Max(DoorZoneMinMm, opening.Width);
            ctx.Doors.Add(frame.ToWorld(0, 0));
            ctx.Zones.Add(new Furniture.Footprint
            {
                Corners = new[] { frame.ToWorld(-half, 0), frame.ToWorld(half, 0), frame.ToWorld(half, depth), frame.ToWorld(-half, depth) },
                Z0 = 0,
                Z1 = 2100,
                Label = "door's swing"
            });
        }
        else
        {
            edge.HasWindow = true;
            ctx.Zones.Add(new Furniture.Footprint
            {
                Corners = new[] { frame.ToWorld(-half, 0), frame.ToWorld(half, 0), frame.ToWorld(half, WindowZoneMm), frame.ToWorld(-half, WindowZoneMm) },
                Z0 = WindowZoneFromMm,
                Z1 = 2100,
                Label = "window"
            });
        }
        foreach (var e in ctx.Edges)
            e.DoorDist = ctx.Doors.Count == 0 ? 0 : ctx.Doors.Min(d => SegDist(d, e.A, e.B));
    }

    static Edge EdgeOf(Ctx ctx, Item item) =>
        ctx.Edges.OrderBy(e => SegDist(new Pt(item.Frame.Ox, item.Frame.Oy), e.A, e.B)).First();

    /// <summary>1 when two walls face each other across the room, -1 when they run the same way.</summary>
    static double Facing(Edge a, Edge b) => -(a.Ux * b.Ux + a.Uy * b.Uy);

    static IEnumerable<Edge> Neighbours(Ctx ctx, Edge e) => new[] { Neighbour(ctx, e, false), Neighbour(ctx, e, true) };

    /// <summary>The wall before this one at its start, or after it at its end.</summary>
    static Edge Neighbour(Ctx ctx, Edge e, bool after) =>
        ctx.Edges[(e.Index + (after ? 1 : ctx.Edges.Count - 1)) % ctx.Edges.Count];

    static IEnumerable<Pt> Spiral(double step, double reach)
    {
        yield return new Pt(0, 0);
        for (var r = 1; r * step <= reach; r++)
            for (var i = -r; i <= r; i++)
                for (var j = -r; j <= r; j++)
                    if (Math.Abs(i) == r || Math.Abs(j) == r)
                        yield return new Pt(i * step, j * step);
    }

    static double Dist(Pt a, Pt b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    static double SegDist(Pt p, Pt a, Pt b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
        return Dist(p, new Pt(a.X + t * dx, a.Y + t * dy));
    }

    /// <summary>"Furnished the bedroom: double bed 160, 2 bedside tables, wardrobe 100."</summary>
    public static string Receipt(string room, Layout layout)
    {
        var groups = layout.Items.GroupBy(i => i.Piece.Name).Select(g =>
            g.Count() == 1 ? g.Key.ToLowerInvariant() : g.Count().ToString(CultureInfo.InvariantCulture) + " × " + g.Key.ToLowerInvariant());
        var text = "Furnished " + room + ": " + string.Join(", ", groups) + ".";
        if (layout.Skipped.Count > 0) text += " Left out " + string.Join(", ", layout.Skipped) + ".";
        return text;
    }
}
