using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GeometryGym.Ifc;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 R4: the model as IFC4, from Forsk's own records, with GeometryGym.
/// One storey per level (there is one today). A wall record that is one
/// straight run is a standard wall (IfcWall STANDARD; IFC4 deprecates
/// IfcWallStandardCase, and GeometryGym writes it so) with its axis and a material
/// layer set usage of its thickness; any other is an IfcWall extruded from
/// its path. Pset_WallCommon says IsExternal (a face on the outline) and
/// Status EXISTING for X-EXIST walls. Each door and window fills an
/// IfcOpeningElement that voids its host; doors carry their operation,
/// windows a single-panel type with the panel's operation. Slabs are
/// IfcSlab FLOOR, the flat roof an IfcRoof aggregating an IfcSlab ROOF,
/// rooms IfcSpace with Qto_SpaceBaseQuantities.NetFloorArea. Pure: records
/// in, a DatabaseIfc out; no RhinoCommon.
/// </summary>
public static class IfcExport
{
    /// <summary>Openings are cut this much past each wall face, so the void goes clean through.</summary>
    public const double OpeningPastFaceMm = 10;
    /// <summary>A door leaf or a window frame as a box this deep, in the middle of the wall.</summary>
    public const double FillDepthMm = 60;
    const double Tol = 1.0;

    public sealed class Wall
    {
        public string Id;
        public List<List<Pt>> Rings;
        public double Thickness;
        public double Height;
        /// <summary>The wall's foot, model Z.</summary>
        public double Base;
        public bool Existing;
    }

    public sealed class Opening
    {
        public string Id;
        /// <summary>The host wall's forsk:id.</summary>
        public string Host;
        public string Kind;
        public string Type;
        public string Hand;
        public string Mark;
        /// <summary>The middle of the opening on the wall's centreline, and the unit along the wall.</summary>
        public Pt Centre;
        public Pt Along;
        public double Width;
        public double Sill;
        public double Head;
    }

    public sealed class Slab
    {
        public string Id;
        public List<List<Pt>> Rings;
        public double Thickness;
        /// <summary>The slab's underside, model Z.</summary>
        public double Base;
    }

    public sealed class Space
    {
        public string Id;
        public string Name;
        public List<Pt> Ring;
        public double AreaM2;
        public double Base;
        public double Height;
    }

    public sealed class Model
    {
        public string Project = "Forsk";
        /// <summary>The storey's floor top, model Z.</summary>
        public double FloorTop;
        public List<Wall> Walls = new List<Wall>();
        public List<Opening> Openings = new List<Opening>();
        public List<Slab> Slabs = new List<Slab>();
        public List<Slab> Roofs = new List<Slab>();
        public List<Space> Spaces = new List<Space>();
    }

    public static DatabaseIfc Build(Model model)
    {
        model = model ?? new Model();
        var db = new DatabaseIfc(ModelView.Ifc4NotAssigned);
        db.Factory.Options.GenerateOwnerHistory = false;
        var site = new IfcSite(db, "Site");
        var project = new IfcProject(site, string.IsNullOrWhiteSpace(model.Project) ? "Forsk" : model.Project.Trim(), IfcUnitAssignment.Length.Millimetre);
        var building = new IfcBuilding(site, string.IsNullOrWhiteSpace(model.Project) ? "Building" : model.Project.Trim());
        var storey = new IfcBuildingStorey(building, "Ground floor", model.FloorTop);
        var z0 = model.FloorTop;

        var outlines = WallJoins.Outlines(model.Walls.Where(w => w.Rings != null).Select(w => w.Rings).ToList(), Tol);
        var hosts = new Dictionary<string, (IfcWall Element, Wall Record)>(StringComparer.OrdinalIgnoreCase);
        foreach (var wall in model.Walls)
        {
            if (wall?.Rings == null || wall.Rings.Count == 0 || wall.Height <= 0) continue;
            var element = AddWall(db, storey, wall, z0);
            if (element == null) continue;
            element.Tag = wall.Id;
            element.Name = wall.Id;
            var props = new List<IfcProperty> { new IfcPropertySingleValue(db, "IsExternal", OnOutline(wall.Rings, outlines)) };
            if (wall.Existing)
                props.Add(new IfcPropertyEnumeratedValue(db, "Status", new IfcLabel("EXISTING")));
            new IfcPropertySet(element, "Pset_WallCommon", props);
            if (!string.IsNullOrEmpty(wall.Id)) hosts[wall.Id] = (element, wall);
        }

        var windowTypes = new Dictionary<string, IfcWindowType>(StringComparer.Ordinal);
        foreach (var opening in model.Openings)
        {
            if (opening?.Host == null || !hosts.TryGetValue(opening.Host, out var host)) continue;
            if (opening.Width <= 0 || opening.Head <= opening.Sill) continue;
            AddOpening(db, storey, host.Element, host.Record, opening, z0, windowTypes);
        }

        // Hosted on the storey, an element is contained in it.
        foreach (var slab in model.Slabs)
            AddSlab(db, storey, slab, z0, IfcSlabTypeEnum.FLOOR);
        foreach (var slab in model.Roofs)
        {
            var roof = new IfcRoof(storey, new IfcLocalPlacement(storey.ObjectPlacement, new IfcAxis2Placement3D(new IfcCartesianPoint(db, 0, 0, 0))), null)
            {
                PredefinedType = IfcRoofTypeEnum.FLAT_ROOF,
                Name = "Roof",
                Tag = slab?.Id
            };
            var part = AddSlab(db, roof, slab, z0, IfcSlabTypeEnum.ROOF);
            if (part != null && part.Decomposes == null) roof.AddAggregated(part);
        }

        foreach (var room in model.Spaces)
        {
            if (room?.Ring == null || room.Ring.Count < 3 || room.Height <= 0) continue;
            var placement = new IfcLocalPlacement(storey.ObjectPlacement, new IfcAxis2Placement3D(new IfcCartesianPoint(db, 0, 0, room.Base - z0)));
            var body = Extrusion(db, new List<List<Pt>> { room.Ring }, room.Height);
            var space = new IfcSpace(storey, room.Id ?? "", placement, body) { LongName = room.Name ?? "" };
            new IfcRelDefinesByProperties(space, new IfcElementQuantity("Qto_SpaceBaseQuantities", new IfcQuantityArea(db, "NetFloorArea", room.AreaM2)));
        }
        return db;
    }

    /// <summary>"✓ Exported IFC · 4 walls, 1 door, 1 window, 1 space · Garage.ifc".</summary>
    public static string Receipt(Model model, string path)
    {
        var parts = new List<string>();
        void Add(int n, string noun)
        {
            if (n > 0) parts.Add(n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s"));
        }
        Add(model?.Walls.Count ?? 0, "wall");
        Add(model?.Openings.Count(o => o.Kind == "door") ?? 0, "door");
        Add(model?.Openings.Count(o => o.Kind == "window") ?? 0, "window");
        Add(model?.Spaces.Count ?? 0, "space");
        return "✓ Exported IFC · " + (parts.Count == 0 ? "nothing" : string.Join(", ", parts)) + " · " + Path.GetFileName(path ?? "");
    }

    /// <summary>
    /// The IFC operation for a Forsk type and hand: a door's
    /// IfcDoorTypeOperationEnum, a window panel's IfcWindowPanelOperationEnum.
    /// </summary>
    public static string Operation(string type, string hand)
    {
        var right = string.Equals(hand, "right", StringComparison.OrdinalIgnoreCase);
        switch (type)
        {
            case "door.hinged_single": return right ? "SINGLE_SWING_RIGHT" : "SINGLE_SWING_LEFT";
            case "door.hinged_double": return "DOUBLE_PANEL_SINGLE_SWING";
            case "door.sliding":
            case "door.pocket": return right ? "SLIDING_TO_RIGHT" : "SLIDING_TO_LEFT";
            case "window.fixed": return "FIXEDCASEMENT";
            case "window.side_hung": return right ? "SIDEHUNGRIGHTHAND" : "SIDEHUNGLEFTHAND";
            case "window.top_hung": return "TOPHUNG";
            default: return "NOTDEFINED";
        }
    }

    static IfcWall AddWall(DatabaseIfc db, IfcBuildingStorey storey, Wall wall, double z0)
    {
        var runs = OneRun(wall.Rings);
        if (runs != null)
        {
            var run = runs;
            var thickness = run.Thickness;
            var centre = (run.Near + run.Far) / 2.0;
            var start = new Pt(run.Normal.X * centre + run.Dir.X * run.Lo, run.Normal.Y * centre + run.Dir.Y * run.Lo);
            var material = new IfcMaterial(db, "Wall");
            var set = new IfcMaterialLayerSet(new IfcMaterialLayer(material, thickness, "Core"), "Wall " + Mm(thickness));
            var usage = new IfcMaterialLayerSetUsage(set, IfcLayerSetDirectionEnum.AXIS2, IfcDirectionSenseEnum.POSITIVE, -thickness / 2.0);
            var placement = new IfcAxis2Placement3D(new IfcCartesianPoint(db, start.X, start.Y, wall.Base - z0),
                db.Factory.ZAxis, new IfcDirection(db, run.Dir.X, run.Dir.Y, 0));
            // IFC4's IfcWallStandardCase is deprecated: a standard wall is IfcWall STANDARD with its axis and layer set usage.
            return new IfcWall(storey, usage, placement, run.Length, wall.Height) { PredefinedType = IfcWallTypeEnum.STANDARD };
        }
        var local = new IfcLocalPlacement(storey.ObjectPlacement, new IfcAxis2Placement3D(new IfcCartesianPoint(db, 0, 0, wall.Base - z0)));
        return new IfcWall(storey, local, Extrusion(db, wall.Rings, wall.Height));
    }

    /// <summary>The record's run when its path is one rectangle of one straight run, else null.</summary>
    static WallEdit.Run OneRun(List<List<Pt>> rings)
    {
        if (rings == null || rings.Count != 1) return null;
        var ring = RoomDetect.Simplify(new List<Pt>(rings[0]), Tol);
        if (ring.Count != 4) return null;
        List<WallEdit.Run> runs;
        try { runs = WallJoins.Runs(rings, Tol); }
        catch (Exception) { return null; }
        if (runs == null || runs.Count != 1) return null;
        var run = runs[0];
        // A rectangle exactly: its area is the run's length times its thickness.
        return Math.Abs(Math.Abs(RoomDetect.Area(ring)) - run.Length * run.Thickness) <= Tol * (run.Length + run.Thickness) ? run : null;
    }

    static void AddOpening(DatabaseIfc db, IfcBuildingStorey storey, IfcWall host, Wall wall, Opening opening, double z0,
        Dictionary<string, IfcWindowType> windowTypes)
    {
        var along = Unit(opening.Along);
        var height = opening.Head - opening.Sill;
        IfcLocalPlacement At(double up) => new IfcLocalPlacement(storey.ObjectPlacement, new IfcAxis2Placement3D(
            new IfcCartesianPoint(db, opening.Centre.X, opening.Centre.Y, wall.Base - z0 + up),
            db.Factory.ZAxis, new IfcDirection(db, along.X, along.Y, 0)));
        IfcProductDefinitionShape Box(double width, double depth, double tall) =>
            new IfcProductDefinitionShape(new IfcShapeRepresentation(new IfcExtrudedAreaSolid(
                new IfcRectangleProfileDef(db, "Box", width, depth), tall)));

        var cut = new IfcOpeningElement(host, At(opening.Sill), Box(opening.Width, wall.Thickness + 2 * OpeningPastFaceMm, height))
        {
            Name = opening.Mark ?? opening.Id,
            Tag = opening.Id
        };
        if (cut.VoidsElement == null) new IfcRelVoidsElement(host, cut);

        var depth = Math.Min(FillDepthMm, wall.Thickness);
        IfcElement fill;
        if (string.Equals(opening.Kind, "window", StringComparison.OrdinalIgnoreCase))
        {
            var window = new IfcWindow(storey, At(opening.Sill), Box(opening.Width, depth, height))
            {
                PredefinedType = IfcWindowTypeEnum.WINDOW,
                PartitioningType = IfcWindowTypePartitioningEnum.SINGLE_PANEL,
                OverallWidth = opening.Width,
                OverallHeight = height
            };
            var operation = Operation(opening.Type, opening.Hand);
            if (!windowTypes.TryGetValue(operation, out var type))
            {
                var name = OpeningTypes.TryGet(opening.Type, out var def) ? def.Label : "Window";
                type = new IfcWindowType(db, name, IfcWindowTypeEnum.WINDOW);
                var panel = new IfcWindowPanelProperties(db,
                    (IfcWindowPanelOperationEnum)Enum.Parse(typeof(IfcWindowPanelOperationEnum), operation), IfcWindowPanelPositionEnum.MIDDLE);
                type.HasPropertySets.Add(panel);
                windowTypes[operation] = type;
            }
            window.setRelatingType(type);
            fill = window;
        }
        else
        {
            fill = new IfcDoor(storey, At(opening.Sill), Box(opening.Width, depth, height))
            {
                PredefinedType = IfcDoorTypeEnum.DOOR,
                OperationType = (IfcDoorTypeOperationEnum)Enum.Parse(typeof(IfcDoorTypeOperationEnum), Operation(opening.Type, opening.Hand)),
                OverallWidth = opening.Width,
                OverallHeight = height
            };
        }
        fill.Name = opening.Mark ?? opening.Id;
        fill.Tag = opening.Id;
        if (fill.FillsVoids == null) new IfcRelFillsElement(cut, fill);
    }

    static IfcSlab AddSlab(DatabaseIfc db, IfcObjectDefinition host, Slab slab, double z0, IfcSlabTypeEnum kind)
    {
        if (slab?.Rings == null || slab.Rings.Count == 0 || slab.Thickness <= 0) return null;
        IfcObjectPlacement relative = (host as IfcProduct)?.ObjectPlacement;
        var placement = new IfcLocalPlacement(relative, new IfcAxis2Placement3D(new IfcCartesianPoint(db, 0, 0, slab.Base - z0)));
        var element = new IfcSlab(host, placement, Extrusion(db, slab.Rings, slab.Thickness))
        {
            PredefinedType = kind,
            Name = kind == IfcSlabTypeEnum.ROOF ? "Roof slab" : "Floor",
            Tag = slab.Id
        };
        return element;
    }

    /// <summary>The rings (outer, then holes) as a profile, extruded straight up.</summary>
    static IfcProductDefinitionShape Extrusion(DatabaseIfc db, List<List<Pt>> rings, double depth)
    {
        IfcPolyline Loop(List<Pt> ring)
        {
            var points = ring.Select(p => new IfcCartesianPoint(db, p.X, p.Y)).ToList();
            points.Add(points[0]);
            return new IfcPolyline(points);
        }
        var outer = Loop(rings[0]);
        var holes = rings.Skip(1).Where(r => r != null && r.Count >= 3).Select(Loop).ToList();
        IfcProfileDef profile = holes.Count == 0
            ? new IfcArbitraryClosedProfileDef("Outline", outer)
            : new IfcArbitraryProfileDefWithVoids("Outline", outer, holes.Cast<IfcCurve>());
        return new IfcProductDefinitionShape(new IfcShapeRepresentation(new IfcExtrudedAreaSolid(profile, depth)));
    }

    /// <summary>A wall with two of its corners on the building's outline has a face outside.</summary>
    static bool OnOutline(List<List<Pt>> rings, List<List<Pt>> outlines)
    {
        var on = 0;
        foreach (var p in rings[0])
        {
            foreach (var ring in outlines)
            {
                var hit = false;
                for (var i = 0; i < ring.Count && !hit; i++)
                    hit = SegmentDistance(p, ring[i], ring[(i + 1) % ring.Count]) <= Tol;
                if (!hit) continue;
                on++;
                break;
            }
        }
        return on >= 2;
    }

    static double SegmentDistance(Pt p, Pt a, Pt b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = dx * dx + dy * dy;
        var t = length <= 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length));
        var x = a.X + t * dx - p.X;
        var y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    static Pt Unit(Pt v)
    {
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return length < 1e-9 ? new Pt(1, 0) : new Pt(v.X / length, v.Y / length);
    }

    static string Mm(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture) + " mm";
}
