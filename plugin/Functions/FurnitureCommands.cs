using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// FU.1 to FU.3: add_furniture, move_furniture, delete_furniture. Each piece
/// is an instance of one block per catalogue id (FORSK_FU3D_bed.double.160x200)
/// whose definition holds the 3D parts on A-FURN or A-FURN-FIXD and the play
/// points on the hidden A-FURN-PLAY. The instance carries the record
/// (forsk:kind=furniture, forsk:id F01, forsk:catalog_id, forsk:room). Plan
/// sheets draw the piece's 2D symbol from the same catalogue, never its 3D.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string NoFurnitureMessage = "No furniture yet. Add a piece first.";
    private const string WhichFurnitureMessage = "Click the piece first, then say it again.";

    [McpCommand("add_furniture", ModelView = true)]
    public JObject AddFurniture(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var item = parameters?["item"]?.ToString();
        if (string.IsNullOrWhiteSpace(item)) throw new ArgumentException("Say which piece: a bed, a sofa, a WC …");
        var piece = Furniture.Resolve(item, ReadSize(parameters, "width"), ReadCount(parameters, "seats"))
            ?? throw new InvalidOperationException("Forsk has no " + item.Trim() + ". It has: " + string.Join(", ", Furniture.Types()) + ".");

        Pt? at = null;
        if (parameters?["at"] != null && parameters["at"].Type != JTokenType.Null)
        {
            if (!TryReadPoint(parameters["at"], out var point)) throw new ArgumentException("at is [x, y] in mm.");
            at = point;
        }
        var rooms = FurnitureRooms(doc);
        var room = PickFurnitureRoom(doc, rooms, parameters?["room"]?.ToString(), at);
        var others = FurnitureFootprints(doc, null);
        Furniture.Placement spot;
        var rotation = ReadSize(parameters, "rotation");
        if (at.HasValue && rotation.HasValue)
        {
            var frame = Furniture.Centred(piece, at.Value.X, at.Value.Y, rotation.Value);
            var why = Furniture.Clash(Furniture.FootprintOf(piece, frame), room.Outline, others);
            if (why != null) throw new InvalidOperationException(Refusal(piece, room, why));
            spot = new Furniture.Placement { Frame = frame, Where = "where you pointed" };
        }
        else
        {
            spot = Furniture.Place(piece, room.Outline, others, at, out var why);
            if (spot == null) throw new InvalidOperationException(Refusal(piece, room, why));
        }

        var (id, forskId) = AddFurniturePiece(doc, piece, spot.Frame, room, StairFloorTop(doc));
        doc.Views.Redraw();
        return FurnitureResult(id, forskId, piece, room, spot.Frame,
            Furniture.Receipt(piece, RoomWords(room), spot.Where));
    }

    /// <summary>One instance of the piece's block at frame, standing on floor, with its record. Throws when Rhino will not add it.</summary>
    private (Guid Id, string ForskId) AddFurniturePiece(RhinoDoc doc, Furniture.Piece piece, Furniture.Frame frame, PlanRoom room, double floor)
    {
        var index = FurnitureDefinition(doc, piece);
        var forskId = Furniture.NextId(FurnitureObjects(doc).Select(o => o.Attributes.GetUserString("forsk:id")));
        var layer = EnsureLayer(doc, Furniture.LayerFor(piece), FurnitureColour(piece));
        var attr = new ObjectAttributes
        {
            Name = piece.Type.Replace('_', '-') + "-" + forskId.Substring(1),
            LayerIndex = layer.Index,
            ColorSource = ObjectColorSource.ColorFromLayer,
            MaterialSource = ObjectMaterialSource.MaterialFromLayer
        };
        StampForskTags(attr, new ForskStamp { Kind = Furniture.Kind, Level = "0", Id = forskId });
        attr.SetUserString(Furniture.CatalogKey, piece.Id);
        attr.SetUserString(Furniture.RoomKey, room?.ScheduleId ?? "");
        attr.SetUserString(Furniture.PlacedKey, Furniture.FrameText(frame));
        var id = doc.Objects.AddInstanceObject(index, FurnitureXform(frame, floor), attr);
        if (id == Guid.Empty) throw new InvalidOperationException("Could not add the " + piece.Name.ToLowerInvariant() + ".");
        return (id, forskId);
    }

    [McpCommand("move_furniture", ModelView = true)]
    public JObject MoveFurniture(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var obj = ResolveFurniture(doc, parameters, false)[0];
        if (!TryFurniture(obj, out var piece, out var frame, out var floor))
            throw new InvalidOperationException("This piece's record does not read. Delete it and add it again.");
        var centre = Furniture.CentreOf(piece, frame);
        double x = centre.X, y = centre.Y, degrees = frame.Degrees;
        var moved = false;
        if (parameters?["to"] != null && parameters["to"].Type != JTokenType.Null)
        {
            if (!TryReadPoint(parameters["to"], out var to)) throw new ArgumentException("to is [x, y] in mm.");
            x = to.X;
            y = to.Y;
            moved = true;
        }
        if (parameters?["by"] != null && parameters["by"].Type != JTokenType.Null)
        {
            if (!TryReadPoint(parameters["by"], out var by)) throw new ArgumentException("by is [dx, dy] in mm.");
            x += by.X;
            y += by.Y;
            moved = true;
        }
        var turn = ReadSize(parameters, "rotate");
        if (turn.HasValue)
        {
            degrees += turn.Value;
            moved = true;
        }
        if (!moved) throw new ArgumentException("Say where: to [x, y], by [dx, dy], or rotate in degrees.");

        var next = Furniture.Centred(piece, x, y, degrees);
        var rooms = FurnitureRooms(doc);
        var at = Furniture.CentreOf(piece, next);
        var room = rooms.FirstOrDefault(r => RoomDetect.Contains(r.Outline, at))
            ?? throw new InvalidOperationException("That would put the " + piece.Name.ToLowerInvariant() + " outside every room.");
        var why = Furniture.Clash(Furniture.FootprintOf(piece, next), room.Outline, FurnitureFootprints(doc, obj.Id));
        if (why != null) throw new InvalidOperationException("Did not move the " + piece.Name.ToLowerInvariant() + ": " + why + ".");
        var change = FurnitureXform(next, floor) * InverseOf(FurnitureXform(frame, floor));
        var movedId = doc.Objects.Transform(obj.Id, change, true);
        var fresh = movedId == Guid.Empty ? null : doc.Objects.FindId(movedId);
        if (fresh == null) throw new InvalidOperationException("Could not move the " + piece.Name.ToLowerInvariant() + ".");
        var attr = fresh.Attributes.Duplicate();
        attr.SetUserString(Furniture.RoomKey, room.ScheduleId);
        doc.Objects.ModifyAttributes(fresh.Id, attr, true);
        doc.Views.Redraw();
        var text = turn.HasValue && x == centre.X && y == centre.Y
            ? "Turned the " + piece.Name.ToLowerInvariant() + " " + Math.Round(turn.Value).ToString(CultureInfo.InvariantCulture) + "°."
            : "Moved the " + piece.Name.ToLowerInvariant() + (room.ScheduleId != obj.Attributes.GetUserString(Furniture.RoomKey) ? " to " + RoomWords(room) : "") + ".";
        return FurnitureResult(fresh.Id, fresh.Attributes.GetUserString("forsk:id"), piece, room, next, text);
    }

    [McpCommand("delete_furniture", ModelView = true)]
    public JObject DeleteFurniture(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var targets = ResolveFurniture(doc, parameters, true);
        var deleted = new JArray();
        foreach (var obj in targets)
            if (doc.Objects.Delete(obj.Id, true)) deleted.Add(obj.Id.ToString());
        if (deleted.Count == 0) throw new InvalidOperationException("Could not delete the furniture.");
        doc.Views.Redraw();
        string message;
        if (deleted.Count == 1 && TryFurniture(targets[0], out var piece, out _, out _))
            message = "Removed the " + piece.Name.ToLowerInvariant() + ".";
        else message = "Removed " + deleted.Count.ToString(CultureInfo.InvariantCulture) + " pieces of furniture.";
        return new JObject { ["deleted"] = deleted, ["count"] = deleted.Count, ["message"] = message };
    }

    // ---- Rooms ---------------------------------------------------------------

    /// <summary>Every room with an outline to place in.</summary>
    private static List<PlanRoom> FurnitureRooms(RhinoDoc doc) =>
        PlanRooms(doc).Where(r => r.Outline != null && r.Outline.Count >= 3).ToList();

    /// <summary>
    /// The room a piece goes in: the one named (id, name, or a type with one
    /// room), else the one holding at, else the picked one, else the only one.
    /// </summary>
    private static PlanRoom PickFurnitureRoom(RhinoDoc doc, List<PlanRoom> rooms, string named, Pt? at)
    {
        if (rooms.Count == 0) throw new InvalidOperationException("No rooms yet. Find the rooms first, then add furniture.");
        if (!string.IsNullOrWhiteSpace(named))
        {
            var hit = MatchRoom(rooms, named);
            if (hit == null)
            {
                // A type word: "the toilet" is the WC, whatever it is named.
                var typed = rooms.Where(r => RoomTypes.Matches(named, r.RoomType, r.Name)).ToList();
                if (typed.Count > 1) throw new InvalidOperationException("There are " + typed.Count + " rooms of that type. Say which: " + string.Join(", ", typed.Select(RoomWords)) + ".");
                hit = typed.FirstOrDefault();
            }
            return hit ?? throw new InvalidOperationException("No room called " + named.Trim() + ".");
        }
        if (at.HasValue)
            return rooms.FirstOrDefault(r => RoomDetect.Contains(r.Outline, at.Value))
                ?? throw new InvalidOperationException("That point is not inside a room.");
        var picked = new HashSet<string>(RoomTargets(doc, new JObject())
            .Select(o => o.Attributes.GetUserString(RoomIdKey) ?? ""), StringComparer.OrdinalIgnoreCase);
        var pick = rooms.Where(r => picked.Contains(r.RoomId)).ToList();
        if (pick.Count == 1) return pick[0];
        if (rooms.Count == 1) return rooms[0];
        throw new InvalidOperationException("Say which room, or click it first.");
    }

    /// <summary>"the bedroom" from a room called Bedroom; the id when it has no name.</summary>
    private static string RoomWords(PlanRoom room)
    {
        if (!string.IsNullOrEmpty(room.Name)) return "the " + room.Name.ToLowerInvariant();
        return string.IsNullOrEmpty(room.ScheduleId) ? "the room" : room.ScheduleId;
    }

    private static string Refusal(Furniture.Piece piece, PlanRoom room, string why) =>
        "No room for " + ("aeiou".IndexOf(char.ToLowerInvariant(piece.Name[0])) >= 0 ? "an " : "a ")
        + piece.Name.ToLowerInvariant() + " in " + RoomWords(room) + ": " + why + ".";

    // ---- The pieces in the file ---------------------------------------------

    private static List<RhinoObject> FurnitureObjects(RhinoDoc doc) =>
        EnumerateDocObjects(doc)
            .Where(o => o is InstanceObject && IsForskGenerated(o)
                && string.Equals(GetForskKind(o), Furniture.Kind, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>The piece, its frame in plan and its floor height, read from the instance. False when it does not read.</summary>
    private static bool TryFurniture(RhinoObject obj, out Furniture.Piece piece, out Furniture.Frame frame, out double floor)
    {
        piece = Furniture.Find(obj?.Attributes?.GetUserString(Furniture.CatalogKey));
        frame = default;
        floor = 0;
        if (piece == null || !(obj is InstanceObject instance)) return false;
        var xform = instance.InstanceXform;
        frame = new Furniture.Frame(xform.M03, xform.M13, xform.M00, xform.M10);
        floor = xform.M23;
        return true;
    }

    /// <summary>Every other piece's footprint, for the overlap check.</summary>
    private static List<Furniture.Footprint> FurnitureFootprints(RhinoDoc doc, Guid? except)
    {
        var list = new List<Furniture.Footprint>();
        foreach (var obj in FurnitureObjects(doc))
        {
            if (except.HasValue && obj.Id == except.Value) continue;
            if (TryFurniture(obj, out var piece, out var frame, out _))
                list.Add(Furniture.FootprintOf(piece, frame));
        }
        return list;
    }

    /// <summary>id (a guid or F01), else the picked pieces, else every piece in room, else the only one. many: more may come back.</summary>
    private static List<RhinoObject> ResolveFurniture(RhinoDoc doc, JObject parameters, bool many)
    {
        var all = FurnitureObjects(doc);
        if (all.Count == 0) throw new InvalidOperationException(NoFurnitureMessage);
        var id = parameters?["id"]?.ToString()?.Trim();
        if (!string.IsNullOrEmpty(id))
        {
            var hit = Guid.TryParse(id, out var guid)
                ? all.FirstOrDefault(o => o.Id == guid)
                : all.FirstOrDefault(o => string.Equals(o.Attributes.GetUserString("forsk:id"), id, StringComparison.OrdinalIgnoreCase));
            return hit != null ? new List<RhinoObject> { hit } : throw new InvalidOperationException("Not a piece of furniture.");
        }
        var room = parameters?["room"]?.ToString()?.Trim();
        if (many && !string.IsNullOrEmpty(room))
        {
            var target = PickFurnitureRoom(doc, FurnitureRooms(doc), room, null);
            var inRoom = all.Where(o => string.Equals(o.Attributes.GetUserString(Furniture.RoomKey), target.ScheduleId, StringComparison.OrdinalIgnoreCase)).ToList();
            return inRoom.Count > 0 ? inRoom : throw new InvalidOperationException(RoomWords(target) + " has no furniture.");
        }
        if (many && parameters?["all"]?.Type == JTokenType.Boolean && parameters["all"].Value<bool>()) return all;
        var picked = ListSelected(doc).Where(o => all.Any(f => f.Id == o.Id)).ToList();
        if (picked.Count == 1 || picked.Count > 1 && many) return picked;
        if (picked.Count > 1) throw new InvalidOperationException("Pick one piece.");
        if (all.Count == 1) return all;
        throw new InvalidOperationException(WhichFurnitureMessage);
    }

    // ---- The block -------------------------------------------------------------

    private static Transform FurnitureXform(Furniture.Frame frame, double floor)
    {
        var t = Transform.Identity;
        t.M00 = frame.Ux;
        t.M10 = frame.Uy;
        t.M01 = -frame.Uy;
        t.M11 = frame.Ux;
        t.M03 = frame.Ox;
        t.M13 = frame.Oy;
        t.M23 = floor;
        return t;
    }

    private static Transform InverseOf(Transform t) => t.TryGetInverse(out var inverse) ? inverse : Transform.Identity;

    private static Color FurnitureColour(Furniture.Piece piece) =>
        piece.Fixed ? Color.FromArgb(200, 200, 205) : Color.FromArgb(196, 170, 140);

    /// <summary>The piece's block definition, made from the catalogue the first time it is used.</summary>
    private int FurnitureDefinition(RhinoDoc doc, Furniture.Piece piece)
    {
        var name = Furniture.ModelBlockName(piece);
        var existing = doc.InstanceDefinitions.Find(name);
        if (existing != null && !existing.IsDeleted) return existing.Index;

        var tol = Math.Max(doc.ModelAbsoluteTolerance, 0.01);
        var model = Furniture.Generate(piece);
        var partLayer = EnsureLayer(doc, Furniture.LayerFor(piece), FurnitureColour(piece));
        InteriorMaterials(doc);
        var playLayer = EnsureLayer(doc, Furniture.PlayLayerName, Color.FromArgb(230, 120, 40));
        if (playLayer.IsVisible)
        {
            playLayer.IsVisible = false;
            doc.Layers.Modify(playLayer, playLayer.Index, true);
        }
        var geometry = new List<GeometryBase>();
        var attrs = new List<ObjectAttributes>();
        foreach (var part in model.Parts)
        {
            var brep = FurniturePart(part, tol);
            if (brep == null) continue;
            var attr = new ObjectAttributes { Name = part.Name, LayerIndex = partLayer.Index, ColorSource = ObjectColorSource.ColorFromLayer };
            attr.SetUserString("forsk:part", part.Name);
            geometry.Add(brep);
            attrs.Add(attr);
        }
        if (geometry.Count == 0) throw new InvalidOperationException("Could not build the " + piece.Name.ToLowerInvariant() + ".");
        foreach (var spot in model.Spots)
        {
            var attr = new ObjectAttributes { Name = spot.Name, LayerIndex = playLayer.Index };
            attr.SetUserString("forsk:facing", string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},0", spot.Fx, spot.Fy));
            attr.SetUserString("forsk:root_z_mm", spot.Z.ToString("0.###", CultureInfo.InvariantCulture));
            geometry.Add(new Rhino.Geometry.Point(new Point3d(spot.X, spot.Y, spot.Z)));
            attrs.Add(attr);
        }
        var index = doc.InstanceDefinitions.Add(name, piece.Name, Point3d.Origin, geometry, attrs);
        if (index < 0) throw new InvalidOperationException("Could not make the block for the " + piece.Name.ToLowerInvariant() + ".");
        // The definition's strings say what it is (spec §2); the instance's catalogue id stays the source of truth.
        var strings = doc.InstanceDefinitions[index].UserDictionary;
        strings.Set("forsk:kind", Furniture.Kind);
        strings.Set(Furniture.CatalogKey, piece.Id);
        strings.Set(Furniture.SchemaKey, Furniture.Schema);
        strings.Set(Furniture.RevKey, Furniture.Rev.ToString(CultureInfo.InvariantCulture));
        return index;
    }

    private static Brep FurniturePart(Furniture.Part part, double tol)
    {
        if (part.Shape == "box")
            return new Box(new BoundingBox(part.X0, part.Y0, part.Z0, part.X1, part.Y1, part.Z1)).ToBrep();
        var plane = new Plane(new Point3d(part.Cx, part.Cy, part.Z0), Vector3d.ZAxis);
        var section = Math.Abs(part.Rx - part.Ry) < 1e-9
            ? (Curve)new ArcCurve(new Circle(plane, part.Rx))
            : new Ellipse(plane, part.Rx, part.Ry).ToNurbsCurve();
        using (section)
        {
            var surface = Surface.CreateExtrusion(section, new Vector3d(0, 0, part.Z1 - part.Z0));
            var brep = surface?.ToBrep()?.CapPlanarHoles(tol);
            if (brep == null || !brep.IsValid) return null;
            if (brep.SolidOrientation == BrepSolidOrientation.Inward) brep.Flip();
            return brep;
        }
    }

    private static int? ReadCount(JObject parameters, string key)
    {
        var token = parameters?[key];
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type != JTokenType.Integer) throw new ArgumentException(key + " is a whole number.");
        return token.Value<int>();
    }

    private static JObject FurnitureResult(Guid id, string forskId, Furniture.Piece piece, PlanRoom room, Furniture.Frame frame, string message)
    {
        var centre = Furniture.CentreOf(piece, frame);
        return new JObject
        {
            ["id"] = id.ToString(),
            ["forsk_id"] = forskId ?? "",
            ["catalog_id"] = piece.Id,
            ["name"] = piece.Name,
            ["room"] = room?.ScheduleId ?? "",
            ["centre"] = new JArray(Math.Round(centre.X, 1), Math.Round(centre.Y, 1)),
            ["rotation"] = Math.Round(frame.Degrees, 3),
            ["size"] = new JArray(piece.W, piece.D, piece.Z0 + piece.H),
            ["message"] = message
        };
    }
}
