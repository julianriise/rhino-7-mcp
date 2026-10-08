using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 R5: add_stair, edit_stair, delete_stair. One closed solid per stair on
/// A-STAIR, its sawtooth profile (Stairs.Profile) extruded across the width.
/// The record (forsk:kind=stair, forsk:id S01…) holds what the user set; a
/// stair whose rise is auto re-plans when walls_from_layer bakes new walls.
/// The plan symbol is drawn at print from the same record.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string NoStairMessage = "No stair. Add one first.";
    private const string WhichStairMessage = "Click the stair first, then say it again.";

    [McpCommand("add_stair", ModelView = true)]
    public JObject AddStair(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var spec = new Stairs.Spec
        {
            Width = ReadSize(parameters, "width") ?? Stairs.WidthDefault,
            RiserMax = ReadSize(parameters, "riser_max") ?? Stairs.RiserMaxDefault,
            Going = ReadSize(parameters, "going") ?? Stairs.GoingDefault,
            Rise = ReadRise(parameters?["rise"], null),
            Z = StairFloorTop(doc)
        };
        var auto = StairAutoRise(doc);
        // Plan first: a size that cannot make a stair is refused before anything is placed.
        var flight = Stairs.Plan(spec, auto);
        var where = PlaceStair(doc, parameters, spec, flight, tol);

        var layer = EnsureLayer(doc, Stairs.LayerName, Color.FromArgb(160, 140, 120));
        InteriorMaterials(doc);
        var solid = StairSolid(spec, flight, tol)
            ?? throw new InvalidOperationException("Could not build the stair solid.");
        var forskId = Stairs.NextId(StairObjects(doc).Select(o => o.Attributes.GetUserString("forsk:id")));
        var attr = new ObjectAttributes
        {
            Name = "stair-" + forskId.Substring(1),
            LayerIndex = layer.Index,
            MaterialSource = ObjectMaterialSource.MaterialFromLayer
        };
        StampForskTags(attr, new ForskStamp { Kind = Stairs.Kind, Level = "0", Id = forskId });
        foreach (var pair in Stairs.Write(spec, flight))
            attr.SetUserString(pair.Key, pair.Value);
        var id = doc.Objects.AddBrep(solid, attr);
        if (id == Guid.Empty) throw new InvalidOperationException("Could not add the stair.");
        doc.Views.Redraw();
        return StairResult(id, forskId, spec, flight, Stairs.Receipt("Added", flight, where));
    }

    [McpCommand("edit_stair", ModelView = true)]
    public JObject EditStair(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var obj = ResolveStairs(doc, parameters, false)[0];
        // As built: a flip turns about the footprint the solid has now.
        if (!TryStairAsBuilt(obj, out var spec, out var before))
            throw new InvalidOperationException("This stair's record does not read. Delete it and add it again.");
        var width = ReadSize(parameters, "width");
        if (width.HasValue) spec = Stairs.Resized(spec, width.Value);
        spec.RiserMax = ReadSize(parameters, "riser_max") ?? spec.RiserMax;
        spec.Going = ReadSize(parameters, "going") ?? spec.Going;
        if (parameters?["rise"] != null) spec.Rise = ReadRise(parameters["rise"], spec.Rise);
        var flip = parameters?["flip"]?.Type == JTokenType.Boolean && parameters["flip"].Value<bool>();
        if (!width.HasValue && parameters?["riser_max"] == null && parameters?["going"] == null && parameters?["rise"] == null && !flip)
            throw new ArgumentException("Say what to change: width, step height, going, rise or flip.");
        var flight = Stairs.Plan(spec, StairAutoRise(doc));
        // The flipped stair starts where the old one ended.
        if (flip) spec = Stairs.Flipped(spec, before);
        RewriteStair(doc, obj, spec, flight, tol);
        var sizes = parameters?["riser_max"] != null || parameters?["going"] != null || parameters?["rise"] != null;
        var text = Stairs.EditReceipt(flight, flip, width.HasValue, sizes);
        doc.Views.Redraw();
        return StairResult(obj.Id, obj.Attributes.GetUserString("forsk:id"), spec, flight, text);
    }

    [McpCommand("delete_stair", ModelView = true)]
    public JObject DeleteStair(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var targets = ResolveStairs(doc, parameters, true);
        var deleted = new JArray();
        foreach (var obj in targets)
            if (doc.Objects.Delete(obj.Id, true)) deleted.Add(obj.Id.ToString());
        if (deleted.Count == 0) throw new InvalidOperationException("Could not delete the stair.");
        doc.Views.Redraw();
        return new JObject
        {
            ["deleted"] = deleted,
            ["count"] = deleted.Count,
            ["message"] = deleted.Count == 1 ? "Removed the stair." : "Removed " + deleted.Count + " stairs."
        };
    }

    /// <summary>
    /// After new walls: every stair whose rise is auto takes the new floor to
    /// floor, a new count and equal risers. Null when none changed, else the
    /// receipt's clause.
    /// </summary>
    private string ReplanAutoStairs(RhinoDoc doc)
    {
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var auto = StairAutoRise(doc);
        var changed = new List<Stairs.Flight>();
        foreach (var obj in StairObjects(doc))
        {
            var spec = ReadStairSpec(obj);
            if (spec == null || spec.Rise.HasValue) continue;
            Stairs.Flight flight;
            try { flight = Stairs.Plan(spec, auto); }
            catch (ArgumentException) { continue; }
            var risers = obj.Attributes.GetUserString(Stairs.RisersKey);
            var riser = ParseMm(obj.Attributes.GetUserString(Stairs.RiserKey));
            if (risers == flight.Risers.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && riser.HasValue && Math.Abs(riser.Value - flight.Riser) < 1e-6)
                continue;
            if (RewriteStair(doc, obj, spec, flight, tol)) changed.Add(flight);
        }
        if (changed.Count == 0) return null;
        if (changed.Count == 1)
            return "The stair now has " + changed[0].Risers + " steps of " + Stairs.Mm(changed[0].Riser) + ".";
        return changed.Count + " stairs re-planned for the new height.";
    }

    /// <summary>The solid and the record, in place: the object keeps its id, so one undo takes the edit back.</summary>
    private bool RewriteStair(RhinoDoc doc, RhinoObject obj, Stairs.Spec spec, Stairs.Flight flight, double tol)
    {
        var solid = StairSolid(spec, flight, tol)
            ?? throw new InvalidOperationException("Could not build the stair solid.");
        if (!doc.Objects.Replace(obj.Id, solid))
            throw new InvalidOperationException("Could not change the stair.");
        var fresh = doc.Objects.FindId(obj.Id) ?? obj;
        var attr = fresh.Attributes.Duplicate();
        attr.SetUserString("forsk:width", FormatMm(spec.Width));
        foreach (var pair in Stairs.Write(spec, flight))
            attr.SetUserString(pair.Key, pair.Value);
        return doc.Objects.ModifyAttributes(obj.Id, attr, true);
    }

    /// <summary>The sawtooth profile on the stair's right side, extruded across to its left, capped and outward.</summary>
    private static Brep StairSolid(Stairs.Spec spec, Stairs.Flight flight, double tol)
    {
        var dir = new Vector3d(spec.Dx, spec.Dy, 0);
        var left = new Vector3d(-spec.Dy, spec.Dx, 0);
        var origin = new Point3d(spec.X, spec.Y, spec.Z) - left * (flight.Width / 2.0);
        var points = Stairs.Profile(flight).Select(p => origin + dir * p.X + Vector3d.ZAxis * p.Y).ToList();
        points.Add(points[0]);
        using (var profile = new PolylineCurve(points))
        {
            var surface = Surface.CreateExtrusion(profile, left * flight.Width);
            var open = surface?.ToBrep();
            if (open == null) return null;
            // One face per riser and tread, so every nosing is an edge a section or a facade draws.
            open.Faces.SplitKinkyFaces(RhinoMath.DefaultAngleTolerance, true);
            var brep = open.CapPlanarHoles(tol);
            if (brep == null || !brep.IsValid || !brep.IsSolid) return null;
            if (brep.SolidOrientation == BrepSolidOrientation.Inward) brep.Flip();
            return brep;
        }
    }

    /// <summary>
    /// Where the stair goes, written into spec. from and to: the foot, then a
    /// point it climbs toward. wall_id, or along_wall with one wall picked:
    /// that wall's room face, from the end nearer at. Nothing given: the
    /// longest room face that holds the run. Returns the plain words for the
    /// receipt ("along the north wall"), or null.
    /// </summary>
    private string PlaceStair(RhinoDoc doc, JObject parameters, Stairs.Spec spec, Stairs.Flight flight, double tol)
    {
        if (parameters?["from"] != null || parameters?["to"] != null)
        {
            if (!TryReadPoint(parameters?["from"], out var from) || !TryReadPoint(parameters?["to"], out var to))
                throw new ArgumentException("from and to are [x, y] in mm.");
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 10) throw new ArgumentException("Pick the direction farther from the start.");
            spec.X = from.X;
            spec.Y = from.Y;
            spec.Dx = dx / length;
            spec.Dy = dy / length;
            var side = parameters["against"]?.ToString()?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(side))
            {
                if (side != "left" && side != "right") throw new ArgumentException("against is left or right.");
                spec.Against = side;
            }
            return null;
        }

        Pt? near = null;
        if (parameters?["at"] != null)
        {
            if (!TryReadPoint(parameters["at"], out var at)) throw new ArgumentException("at is [x, y] in mm.");
            near = at;
        }
        List<Stairs.Face> faces;
        var wallId = parameters?["wall_id"]?.ToString();
        var alongWall = parameters?["along_wall"]?.Type == JTokenType.Boolean && parameters["along_wall"].Value<bool>();
        if (!string.IsNullOrWhiteSpace(wallId) || alongWall)
        {
            var pick = PickWallRun(doc, new JObject { ["id"] = wallId, ["side"] = parameters?["side"], ["at"] = parameters?["at"] }, tol);
            var index = pick.Graph.Find(pick.Run, tol);
            faces = Stairs.InteriorFaces(pick.Graph).Where(f => f.Run == index).ToList();
            if (faces.Count == 0) throw new InvalidOperationException("That wall has no side inside a room. Pick an inside wall, or two points.");
            foreach (var face in faces) face.Name = pick.Label;
        }
        else
        {
            faces = new List<Stairs.Face>();
            var records = new List<List<List<Pt>>>();
            foreach (var obj in EnumerateDocObjects(doc))
            {
                if (!IsHostWall(doc, obj)) continue;
                var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
                if (rings != null) records.Add(rings);
            }
            if (records.Count == 0) throw new InvalidOperationException("No walls yet. Generate the model, or pick two points for the stair.");
            foreach (var cluster in WallJoins.Clusters(records, tol))
                faces.AddRange(Stairs.InteriorFaces(WallJoins.Build(records, cluster, tol)));
            if (faces.Count == 0) throw new InvalidOperationException("No wall closes a room yet. Pick two points for the stair.");
        }
        var chosen = Stairs.LongestFace(faces, flight.Run);
        Stairs.AlongFace(chosen, near, flight.Width, out var start, out var dir, out var against);
        spec.X = start.X;
        spec.Y = start.Y;
        spec.Dx = dir.X;
        spec.Dy = dir.Y;
        spec.Against = against;
        var where = Stairs.Where(chosen);
        return chosen.Length < flight.Run - 1 ? where + " (it runs past the wall's end)" : where;
    }

    private static List<RhinoObject> StairObjects(RhinoDoc doc)
    {
        return EnumerateDocObjects(doc)
            .Where(o => IsForskGenerated(o) && string.Equals(GetForskKind(o), Stairs.Kind, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static Stairs.Spec ReadStairSpec(RhinoObject obj)
    {
        return obj?.Attributes == null ? null : Stairs.Read(obj.Attributes.GetUserString);
    }

    /// <summary>
    /// The stair as its solid was last built: the record's spec, and the flight
    /// from its stored risers × riser, so the plan, the takeoff and the IFC agree
    /// with the 3D. False when the record does not read.
    /// </summary>
    private static bool TryStairAsBuilt(RhinoObject obj, out Stairs.Spec spec, out Stairs.Flight flight)
    {
        flight = null;
        spec = ReadStairSpec(obj);
        if (spec == null) return false;
        var risers = ParseMm(obj.Attributes.GetUserString(Stairs.RisersKey)) ?? 0;
        var riser = ParseMm(obj.Attributes.GetUserString(Stairs.RiserKey)) ?? 0;
        try { flight = Stairs.Plan(spec, risers * riser); }
        catch (ArgumentException) { return false; }
        return true;
    }

    /// <summary>id (a guid or S01), else the picked stairs, else the only stair. many: more than one may come back.</summary>
    private static List<RhinoObject> ResolveStairs(RhinoDoc doc, JObject parameters, bool many)
    {
        var all = StairObjects(doc);
        if (all.Count == 0) throw new InvalidOperationException(NoStairMessage);
        var id = parameters?["id"]?.ToString()?.Trim();
        if (!string.IsNullOrEmpty(id))
        {
            var hit = Guid.TryParse(id, out var guid)
                ? all.FirstOrDefault(o => o.Id == guid)
                : all.FirstOrDefault(o => string.Equals(o.Attributes.GetUserString("forsk:id"), id, StringComparison.OrdinalIgnoreCase));
            return hit != null ? new List<RhinoObject> { hit } : throw new InvalidOperationException("Not a stair.");
        }
        var picked = ListSelected(doc).Where(o => all.Any(s => s.Id == o.Id)).ToList();
        if (picked.Count == 1 || picked.Count > 1 && many) return picked;
        if (picked.Count > 1) throw new InvalidOperationException("Pick one stair.");
        if (all.Count == 1) return all;
        throw new InvalidOperationException(WhichStairMessage);
    }

    /// <summary>The floor the stair stands on: the slabs' top, else the walls' foot, else 0.</summary>
    private static double StairFloorTop(RhinoDoc doc)
    {
        double? floor = null;
        double? walls = null;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj)) continue;
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            var kind = GetForskKind(obj);
            if (string.Equals(kind, "floor", StringComparison.OrdinalIgnoreCase)) floor = Math.Max(floor ?? double.MinValue, box.Max.Z);
            else if (IsHostWall(doc, obj)) walls = Math.Min(walls ?? double.MaxValue, box.Min.Z);
        }
        return floor ?? walls ?? 0;
    }

    /// <summary>
    /// Floor to floor: the walls' most common height. The slab hangs below
    /// the walking surface, so the upper floor is the wall top.
    /// </summary>
    private static double StairAutoRise(RhinoDoc doc)
    {
        var heights = new List<double>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj) || !IsHostWall(doc, obj)) continue;
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            var depth = box.Max.Z - box.Min.Z;
            heights.Add(ParseMm(obj.Attributes.GetUserString("forsk:height")) ?? depth);
        }
        return Stairs.AutoRise(heights, ForskDefaults.WallHeight);
    }

    private static double? ReadSize(JObject parameters, string key)
    {
        var token = parameters?[key];
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            throw new ArgumentException(key + " is a number in mm.");
        return token.Value<double>();
    }

    /// <summary>A number, or "auto" (null). Anything else keeps the current value.</summary>
    private static double? ReadRise(JToken token, double? current)
    {
        if (token == null || token.Type == JTokenType.Null) return current;
        if (token.Type == JTokenType.String && string.Equals(token.ToString().Trim(), Stairs.Auto, StringComparison.OrdinalIgnoreCase))
            return null;
        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<double>();
        throw new ArgumentException("rise is a number in mm, or \"auto\".");
    }

    private static JObject StairResult(Guid id, string forskId, Stairs.Spec spec, Stairs.Flight flight, string message)
    {
        return new JObject
        {
            ["id"] = id.ToString(),
            ["forsk_id"] = forskId ?? "",
            ["risers"] = flight.Risers,
            ["riser"] = flight.Riser,
            ["going"] = flight.Going,
            ["width"] = flight.Width,
            ["rise"] = flight.Rise,
            ["rise_auto"] = !spec.Rise.HasValue,
            ["run"] = flight.Run,
            ["rule"] = flight.Rule,
            ["comfort"] = Stairs.Comfort(flight) ?? "",
            ["message"] = message
        };
    }
}
