using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Daylight (F4) geometry I/O only. The daylight factor estimate runs in the
/// Python MCP server: daylight_scene reads tagged walls, openings, roofs, and
/// rooms in mm, and daylight_paint draws the rooms as one coloured mesh on A-ANALYSE.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string AnalysisLayerName = "A-ANALYSE";
    private const string AnalysisKind = "analysis";

    [McpCommand("daylight_scene", ReadOnly = true)]
    public JObject DaylightScene(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1e-6);
        var walls = new JArray();
        var openings = new JArray();
        var rooms = new JArray();
        var roofs = new JArray();
        var warnings = new JArray();
        var wallIdByGuid = new Dictionary<Guid, string>();
        var markers = new List<RhinoObject>();
        var roomRings = new List<KeyValuePair<string, List<RoomDetect.Pt>>>();

        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj) || IsExistingUnderlay(doc, obj)) continue;
            var kind = GetForskKind(obj) ?? "";
            if (kind.Equals("wall", StringComparison.OrdinalIgnoreCase))
            {
                var id = obj.Attributes.GetUserString("forsk:id");
                if (string.IsNullOrEmpty(id)) id = obj.Id.ToString();
                var path = obj.Attributes.GetUserString("forsk:path");
                if (!TryDecodeWallPath(path, out var outer, out var holes))
                {
                    warnings.Add($"Wall {id} has no param path. Bake the wall again.");
                    continue;
                }
                var rings = new JArray { XyRing(LoopPoints(outer, tol)) };
                foreach (var hole in holes)
                    rings.Add(XyRing(LoopPoints(hole, tol)));
                wallIdByGuid[obj.Id] = id;
                var extent = obj.Geometry.GetBoundingBox(true);
                walls.Add(new JObject
                {
                    ["id"] = id,
                    ["thickness"] = ParseMm(obj.Attributes.GetUserString("forsk:thickness")) ?? 200.0,
                    ["z0"] = extent.Min.Z,
                    ["z1"] = extent.Max.Z,
                    ["rings"] = rings
                });
            }
            else if (kind.Equals("roof", StringComparison.OrdinalIgnoreCase))
            {
                // The eave's underside, and how far it reaches past the walls, shade the windows.
                roofs.Add(new JObject
                {
                    ["z0"] = obj.Geometry.GetBoundingBox(true).Min.Z,
                    ["overhang"] = ParseMm(obj.Attributes.GetUserString("forsk:overhang")) ?? 0.0
                });
            }
            else if (kind.Equals("opening_marker", StringComparison.OrdinalIgnoreCase))
            {
                markers.Add(obj);
            }
            else if (kind.Equals("room", StringComparison.OrdinalIgnoreCase))
            {
                var loop = RoomMarkerOutline(obj);
                var pts = loop == null ? null : LoopPoints(loop, tol);
                if (pts == null || pts.Count < 3)
                {
                    warnings.Add($"Room {obj.Attributes.Name} has no outline.");
                    continue;
                }
                roomRings.Add(new KeyValuePair<string, List<RoomDetect.Pt>>(obj.Id.ToString(), PlanPoints(pts)));
                rooms.Add(new JObject
                {
                    ["id"] = obj.Id.ToString(),
                    ["name"] = obj.Attributes.Name ?? "",
                    ["ring"] = XyRing(pts),
                    ["z"] = loop.GetBoundingBox(true).Min.Z
                });
            }
        }

        foreach (var marker in markers)
        {
            var host = marker.Attributes.GetUserString("forsk:host_id");
            if (string.IsNullOrEmpty(host)
                && Guid.TryParse(marker.Attributes.GetUserString("forsk:host"), out var hostGuid))
                wallIdByGuid.TryGetValue(hostGuid, out host);
            var box = marker.Geometry.GetBoundingBox(true);
            var width = ParseMm(marker.Attributes.GetUserString("forsk:width"));
            // Sill and head are world Z, as the opening cutter uses them.
            openings.Add(new JObject
            {
                ["id"] = marker.Attributes.Name ?? marker.Id.ToString(),
                ["host_id"] = host ?? "",
                ["kind"] = marker.Attributes.GetUserString("forsk:opening_kind") ?? "",
                ["width"] = width ?? Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y),
                ["center"] = new JArray(box.Center.X, box.Center.Y),
                ["sill"] = ParseMm(marker.Attributes.GetUserString("forsk:sill")) ?? box.Min.Z,
                ["head"] = ParseMm(marker.Attributes.GetUserString("forsk:head")) ?? box.Max.Z
            });
        }

        var selected = new JArray();
        var roomLayer = ResolveRoomSourceLayer(doc, "A-ROOM");
        foreach (var obj in ListSelected(doc))
        {
            if (IsRoomMarker(obj))
            {
                selected.Add(obj.Id.ToString());
                continue;
            }
            // A marker is a curve on its room's outline, so a click may pick the
            // outline instead: the room whose marker holds it is selected.
            if (roomLayer == null || !ObjectOnLayer(doc, obj, roomLayer)
                || !(obj.Geometry is Curve curve) || !curve.IsClosed) continue;
            var outline = LoopPoints(curve, tol);
            if (outline == null || outline.Count < 3
                || !RoomDetect.TryInside(new List<List<RoomDetect.Pt>> { PlanPoints(outline) }, out var at)) continue;
            var room = roomRings.Find(r => RoomDetect.Contains(r.Value, at));
            if (room.Key != null && !selected.Any(t => t.ToString() == room.Key)) selected.Add(room.Key);
        }

        return new JObject
        {
            ["walls"] = walls,
            ["openings"] = openings,
            ["rooms"] = rooms,
            ["roofs"] = roofs,
            ["selected_room_ids"] = selected,
            ["warnings"] = warnings
        };
    }

    [McpCommand("daylight_paint")]
    public JObject DaylightPaint(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var z = parameters["z"]?.ToObject<double?>() ?? 0;
        var vertices = parameters["vertices"] as JArray;
        var colors = parameters["colors"] as JArray;
        var faces = parameters["faces"] as JArray;
        if (vertices == null || colors == null || faces == null)
            throw new ArgumentException("vertices, colors, and faces are required.");
        if (colors.Count != vertices.Count)
            throw new ArgumentException("colors must have one entry per vertex.");

        var deleted = DeleteAnalysisOverlay(doc);
        var layer = EnsureLayer(doc, AnalysisLayerName, Color.FromArgb(16, 118, 128));
        KeepAnalysisOffPrint(doc, layer);
        var mode = AnalysisDisplayMode();
        var box = BoundingBox.Empty;

        var id = Guid.Empty;
        var vertexCount = 0;
        if (faces.Count > 0)
        {
            var mesh = new Mesh();
            for (var i = 0; i < vertices.Count; i++)
            {
                var v = (JArray)vertices[i];
                mesh.Vertices.Add(v[0].ToObject<double>(), v[1].ToObject<double>(), z);
                mesh.VertexColors.Add(RgbOf(colors[i]));
            }
            foreach (var token in faces)
            {
                var f = (JArray)token;
                if (f.Count == 3)
                    mesh.Faces.AddFace(f[0].ToObject<int>(), f[1].ToObject<int>(), f[2].ToObject<int>());
                else
                    mesh.Faces.AddFace(f[0].ToObject<int>(), f[1].ToObject<int>(), f[2].ToObject<int>(), f[3].ToObject<int>());
            }
            mesh.Normals.ComputeNormals();
            vertexCount = mesh.Vertices.Count;
            id = doc.Objects.AddMesh(mesh, AnalysisAttributes(layer, "mesh", "daylight", mode));
            if (id == Guid.Empty)
                throw new InvalidOperationException("Could not add the daylight mesh.");
            box.Union(mesh.GetBoundingBox(true));
        }

        doc.Views.Redraw();
        var bbox = box.IsValid
            ? new JArray(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y)
            : new JArray();
        var wiresOff = mode != null && !mode.DisplayAttributes.MeshSpecificAttributes.ShowMeshWires;
        return new JObject
        {
            ["id"] = id == Guid.Empty ? "" : id.ToString(),
            ["faces"] = faces.Count,
            ["vertices"] = vertexCount,
            ["wires"] = wiresOff ? "off" : "on",
            ["layer"] = AnalysisLayerName,
            ["deleted"] = deleted,
            ["bbox"] = bbox,
            ["message"] = $"Painted {faces.Count} daylight faces on {AnalysisLayerName}."
        };
    }

    private const string AnalysisDisplayName = "Forsk Analysis";

    /// <summary>
    /// Shaded copy with mesh wires and vertices off and unlit vertex colours,
    /// so the overlay shows the ramp colours in any viewport display mode.
    /// </summary>
    private static DisplayModeDescription AnalysisDisplayMode()
    {
        var mode = DisplayModeDescription.FindByName(AnalysisDisplayName);
        if (mode == null)
        {
            var copied = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.ShadedId, AnalysisDisplayName);
            mode = DisplayModeDescription.GetDisplayMode(copied);
        }
        if (mode == null) return null;
        var attrs = mode.DisplayAttributes;
        attrs.MeshSpecificAttributes.ShowMeshWires = false;
        attrs.MeshSpecificAttributes.ShowMeshVertices = false;
        attrs.ShadeVertexColors = false;
        DisplayModeDescription.UpdateDisplayMode(mode);
        return DisplayModeDescription.GetDisplayMode(mode.Id) ?? mode;
    }

    private static ObjectAttributes AnalysisAttributes(Layer layer, string role, string name, DisplayModeDescription mode)
    {
        var attr = new ObjectAttributes { LayerIndex = layer.Index, Name = name };
        StampForskTags(attr, new ForskStamp { Kind = AnalysisKind });
        attr.SetUserString("forsk:role", role);
        if (mode != null)
            attr.SetDisplayModeOverride(mode);
        return attr;
    }

    private static Color RgbOf(JToken token)
    {
        var c = (JArray)token;
        return Color.FromArgb(c[0].ToObject<int>(), c[1].ToObject<int>(), c[2].ToObject<int>());
    }

    [McpCommand("daylight_clear")]
    public JObject DaylightClear(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var deleted = DeleteAnalysisOverlay(doc);
        doc.Views.Redraw();
        return new JObject
        {
            ["count"] = deleted,
            ["remaining"] = AnalysisOverlays(doc).Count,
            ["message"] = $"Cleared {deleted} daylight overlay(s) from {AnalysisLayerName}."
        };
    }

    private static List<RhinoObject> AnalysisOverlays(RhinoDoc doc)
    {
        var list = new List<RhinoObject>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (IsForskGenerated(obj)
                && string.Equals(GetForskKind(obj), AnalysisKind, StringComparison.OrdinalIgnoreCase))
                list.Add(obj);
        }
        return list;
    }

    private static int DeleteAnalysisOverlay(RhinoDoc doc)
    {
        var count = 0;
        foreach (var obj in AnalysisOverlays(doc))
        {
            if (doc.Objects.Delete(obj.Id, true)) count++;
        }
        return count;
    }

    /// <summary>
    /// Print never shows the overlay: off in every layout detail, and plot weight
    /// -1 (do not print). Layout packs made later keep it off because it is not clay.
    /// </summary>
    private static void KeepAnalysisOffPrint(RhinoDoc doc, Layer layer)
    {
        layer.PlotWeight = -1;
        foreach (var page in doc.Views.GetPageViews())
        {
            var details = page.GetDetailViews();
            if (details == null) continue;
            foreach (var detail in details)
            {
                if (detail?.Viewport != null)
                    layer.SetPerViewportVisible(detail.Viewport.Id, false);
            }
        }
        doc.Layers.Modify(layer, layer.Index, true);
    }

    private static JArray XyRing(List<Point3d> points)
    {
        var ring = new JArray();
        if (points == null) return ring;
        foreach (var p in points)
            ring.Add(new JArray(p.X, p.Y));
        return ring;
    }
}
