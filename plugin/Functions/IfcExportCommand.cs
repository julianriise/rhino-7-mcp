using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 R4: export_ifc. Reads Forsk's records (the walls' paths, thickness and
/// height, the opening markers, the slabs, the flat roof and the rooms) as
/// the takeoff does, and writes them as IFC4 with IfcExport. Nothing in the
/// document changes.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string IfcNeedsPathMessage = "export_ifc needs an absolute .ifc path.";

    [McpCommand("export_ifc", ReadOnly = true)]
    public JObject ExportIfc(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");
        var path = parameters?["path"]?.ToString()?.Trim() ?? "";
        if (path.Length == 0 || !Path.IsPathRooted(path) || !path.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
            return IfcResult("", null, IfcNeedsPathMessage);

        var model = ReadIfcModel(doc);
        if (model.Walls.Count == 0)
            return IfcResult("", model, NothingToLayOutMessage);
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var db = IfcExport.Build(model);
        if (!db.WriteFile(full) || !File.Exists(full))
            return IfcResult("", model, "IFC write failed.");
        return IfcResult(full, model, IfcExport.Receipt(model, full));
    }

    private static JObject IfcResult(string path, IfcExport.Model model, string message)
    {
        return new JObject
        {
            ["path"] = path ?? "",
            ["walls"] = model?.Walls.Count ?? 0,
            ["doors"] = model?.Openings.Count(o => o.Kind == "door") ?? 0,
            ["windows"] = model?.Openings.Count(o => o.Kind == "window") ?? 0,
            ["slabs"] = model?.Slabs.Count ?? 0,
            ["roofs"] = model?.Roofs.Count ?? 0,
            ["spaces"] = model?.Spaces.Count ?? 0,
            ["stairs"] = model?.Stairs.Count ?? 0,
            ["message"] = message ?? ""
        };
    }

    private static IfcExport.Model ReadIfcModel(RhinoDoc doc)
    {
        var model = new IfcExport.Model();
        var project = ProjectMetaRecord(doc)["project"]?.ToString();
        model.Project = string.IsNullOrWhiteSpace(project) ? Path.GetFileNameWithoutExtension(doc.Name ?? "") : project.Trim();
        var wallObjects = new Dictionary<Guid, IfcExport.Wall>();
        var floorTop = double.MinValue;
        var unnamed = 0;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null) continue;
            var existing = IsExistingUnderlay(doc, obj);
            if (!existing && !IsForskGenerated(obj)) continue;
            var kind = GetForskKind(obj) ?? "";
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            var depth = box.Max.Z - box.Min.Z;
            var rings = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
            if (rings != null && (existing || kind.Equals("wall", StringComparison.OrdinalIgnoreCase)))
            {
                var id = obj.Attributes.GetUserString("forsk:id");
                var wall = new IfcExport.Wall
                {
                    Id = string.IsNullOrWhiteSpace(id) ? "x" + (++unnamed).ToString("00") : id,
                    Rings = rings,
                    Thickness = ParseMm(obj.Attributes.GetUserString("forsk:thickness")) ?? 0,
                    Height = ParseMm(obj.Attributes.GetUserString("forsk:height")) ?? depth,
                    Base = box.Min.Z,
                    Existing = existing
                };
                model.Walls.Add(wall);
                wallObjects[obj.Id] = wall;
                continue;
            }
            if (existing) continue;
            var floor = kind.Equals("floor", StringComparison.OrdinalIgnoreCase);
            if (floor || kind.Equals("roof", StringComparison.OrdinalIgnoreCase))
            {
                var footprint = Footprint(obj.Geometry);
                if (footprint == null) continue;
                var slab = new IfcExport.Slab
                {
                    Id = obj.Attributes.GetUserString("forsk:id") ?? kind,
                    Rings = footprint,
                    Thickness = ParseMm(obj.Attributes.GetUserString("forsk:thickness")) ?? depth,
                    Base = box.Min.Z
                };
                (floor ? model.Slabs : model.Roofs).Add(slab);
                if (floor) floorTop = Math.Max(floorTop, box.Max.Z);
            }
        }
        model.FloorTop = floorTop > double.MinValue ? floorTop : 0;

        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null || !IsForskGenerated(obj)) continue;
            var kind = GetForskKind(obj) ?? "";
            if (kind == "opening_marker")
            {
                var opening = ReadIfcOpening(obj, wallObjects);
                if (opening != null) model.Openings.Add(opening);
                continue;
            }
            var roomId = obj.Attributes.GetUserString("forsk:room_id");
            if (string.IsNullOrWhiteSpace(roomId) || !(obj.Geometry is Curve curve) || !curve.IsClosed) continue;
            if (!curve.TryGetPolyline(out var polyline))
            {
                using (var approx = curve.ToPolyline(0, 0, 0.1, 0, 0, 1.0, 0, 0, true))
                    if (approx == null || !approx.TryGetPolyline(out polyline)) continue;
            }
            var ring = polyline.Take(polyline.Count - (polyline.IsClosed ? 1 : 0)).Select(p => new Pt(p.X, p.Y)).ToList();
            if (ring.Count < 3) continue;
            var area = ParseMm(obj.Attributes.GetUserString("forsk:area")) ?? Math.Abs(RoomDetect.Area(ring));
            model.Spaces.Add(new IfcExport.Space
            {
                Id = roomId.Trim(),
                Name = obj.Attributes.GetUserString("forsk:room_name") ?? "",
                Ring = ring,
                AreaM2 = area / 1e6,
                Base = model.FloorTop,
                Height = model.Walls.Count > 0 ? model.Walls.Max(w => w.Height) : 0
            });
        }
        foreach (var obj in StairObjects(doc))
        {
            if (!TryStairAsBuilt(obj, out var spec, out var flight)) continue;
            model.Stairs.Add(new IfcExport.Stair
            {
                Id = obj.Attributes.GetUserString("forsk:id"),
                X = spec.X,
                Y = spec.Y,
                Base = spec.Z,
                Dx = spec.Dx,
                Dy = spec.Dy,
                Flight = flight
            });
        }
        return model;
    }

    /// <summary>An opening marker as IfcExport takes it: on its host's centreline, along its host.</summary>
    private static IfcExport.Opening ReadIfcOpening(RhinoObject marker, Dictionary<Guid, IfcExport.Wall> walls)
    {
        var attr = marker.Attributes;
        if (!Guid.TryParse(attr.GetUserString("forsk:host"), out var hostId) || !walls.TryGetValue(hostId, out var host)) return null;
        var kind = (attr.GetUserString("forsk:opening_kind") ?? "").Trim().ToLowerInvariant();
        if (kind != "door" && kind != "window") return null;
        var run = WallJoins.MainRun(host.Rings, 1.0);
        var box = marker.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (run == null || !box.IsValid) return null;
        var centre = (run.Near + run.Far) / 2.0;
        var t = box.Center.X * run.Dir.X + box.Center.Y * run.Dir.Y;
        OpeningTypes.TryRead(kind, attr.GetUserString(OpeningTypes.TypeKey), attr.GetUserString(OpeningTypes.HandKey),
            attr.GetUserString(OpeningTypes.SwingKey), out var style, out _);
        var id = attr.GetUserString("forsk:id");
        return new IfcExport.Opening
        {
            Id = string.IsNullOrWhiteSpace(id) ? marker.Id.ToString() : id,
            Host = host.Id,
            Kind = kind,
            Type = style?.Def?.Id,
            Hand = style?.Hand,
            Mark = attr.GetUserString(Schedules.MarkKey),
            Centre = new Pt(run.Normal.X * centre + run.Dir.X * t, run.Normal.Y * centre + run.Dir.Y * t),
            Along = run.Dir,
            Width = ParseMm(attr.GetUserString("forsk:width")) ?? 0,
            Sill = ParseMm(attr.GetUserString("forsk:sill")) ?? box.Min.Z - host.Base,
            Head = ParseMm(attr.GetUserString("forsk:head")) ?? box.Max.Z - host.Base
        };
    }

    /// <summary>A slab's or roof's top face as rings (outer, then holes), in plan. Null when it has none.</summary>
    private static List<List<Pt>> Footprint(GeometryBase geometry)
    {
        var brep = geometry as Brep ?? (geometry as Extrusion)?.ToBrep();
        if (brep == null) return null;
        BrepFace best = null;
        var bestArea = 0.0;
        foreach (var face in brep.Faces)
        {
            if (!face.TryGetPlane(out var plane, 0.01) || Math.Abs(plane.Normal.Z) < 0.99) continue;
            double area;
            using (var single = face.DuplicateFace(false))
            using (var mass = AreaMassProperties.Compute(single))
                area = mass?.Area ?? 0;
            if (area > bestArea)
            {
                best = face;
                bestArea = area;
            }
        }
        if (best == null) return null;
        var rings = new List<List<Pt>>();
        foreach (var loop in best.Loops.OrderBy(l => l.LoopType == BrepLoopType.Outer ? 0 : 1))
        {
            using (var curve = loop.To3dCurve())
            {
                if (curve == null) continue;
                if (!curve.TryGetPolyline(out var polyline))
                {
                    using (var approx = curve.ToPolyline(0, 0, 0.1, 0, 0, 1.0, 0, 0, true))
                        if (approx == null || !approx.TryGetPolyline(out polyline)) continue;
                }
                var ring = polyline.Take(polyline.Count - (polyline.IsClosed ? 1 : 0)).Select(p => new Pt(p.X, p.Y)).ToList();
                if (ring.Count >= 3) rings.Add(ring);
            }
        }
        return rings.Count == 0 ? null : rings;
    }
}
