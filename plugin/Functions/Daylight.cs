using System;
using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Daylight (F4) geometry I/O only. The sky-vis proxy runs in the Python MCP
/// server: daylight_scene reads tagged walls, openings, and rooms in mm, and
/// daylight_paint draws the scored cells as one coloured mesh on A-ANALYSE.
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
        var warnings = new JArray();
        var wallIdByGuid = new Dictionary<Guid, string>();
        var markers = new List<RhinoObject>();

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
                walls.Add(new JObject
                {
                    ["id"] = id,
                    ["thickness"] = ParseMm(obj.Attributes.GetUserString("forsk:thickness")) ?? 200.0,
                    ["rings"] = rings
                });
            }
            else if (kind.Equals("opening_marker", StringComparison.OrdinalIgnoreCase))
            {
                markers.Add(obj);
            }
            else if (kind.Equals("room", StringComparison.OrdinalIgnoreCase))
            {
                var brep = obj.Geometry as Brep;
                var loop = brep != null && brep.Faces.Count > 0
                    ? brep.Faces[0].OuterLoop?.To3dCurve()
                    : null;
                var pts = loop == null ? null : LoopPoints(loop, tol);
                if (pts == null || pts.Count < 3)
                {
                    warnings.Add($"Room {obj.Attributes.Name} has no outline.");
                    continue;
                }
                rooms.Add(new JObject
                {
                    ["id"] = obj.Id.ToString(),
                    ["name"] = obj.Attributes.Name ?? "",
                    ["ring"] = XyRing(pts),
                    ["z"] = brep.GetBoundingBox(true).Min.Z
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
            openings.Add(new JObject
            {
                ["id"] = marker.Attributes.Name ?? marker.Id.ToString(),
                ["host_id"] = host ?? "",
                ["kind"] = marker.Attributes.GetUserString("forsk:opening_kind") ?? "",
                ["width"] = width ?? Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y),
                ["center"] = new JArray(box.Center.X, box.Center.Y)
            });
        }

        var selected = new JArray();
        foreach (var obj in ListSelected(doc))
        {
            if (string.Equals(GetForskKind(obj), "room", StringComparison.OrdinalIgnoreCase))
                selected.Add(obj.Id.ToString());
        }

        return new JObject
        {
            ["walls"] = walls,
            ["openings"] = openings,
            ["rooms"] = rooms,
            ["selected_room_ids"] = selected,
            ["warnings"] = warnings
        };
    }

    [McpCommand("daylight_paint")]
    public JObject DaylightPaint(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var cell = parameters["cell"]?.ToObject<double?>() ?? 0;
        var z = parameters["z"]?.ToObject<double?>() ?? 0;
        var cells = parameters["cells"] as JArray;
        if (cell <= 0)
            throw new ArgumentException("cell must be positive.");
        if (cells == null)
            throw new ArgumentException("cells is required.");

        var deleted = DeleteAnalysisOverlay(doc);
        var layer = EnsureLayer(doc, AnalysisLayerName, Color.FromArgb(16, 118, 128));
        KeepAnalysisOffPrint(doc, layer);

        var id = Guid.Empty;
        if (cells.Count > 0)
        {
            var mesh = new Mesh();
            var half = cell / 2.0;
            foreach (var token in cells)
            {
                var c = (JArray)token;
                double x = c[0].ToObject<double>(), y = c[1].ToObject<double>();
                var color = Color.FromArgb(c[2].ToObject<int>(), c[3].ToObject<int>(), c[4].ToObject<int>());
                var first = mesh.Vertices.Count;
                mesh.Vertices.Add(x - half, y - half, z);
                mesh.Vertices.Add(x + half, y - half, z);
                mesh.Vertices.Add(x + half, y + half, z);
                mesh.Vertices.Add(x - half, y + half, z);
                for (var i = 0; i < 4; i++)
                    mesh.VertexColors.Add(color);
                mesh.Faces.AddFace(first, first + 1, first + 2, first + 3);
            }
            mesh.Normals.ComputeNormals();
            mesh.Compact();

            var attr = new ObjectAttributes { LayerIndex = layer.Index, Name = "daylight" };
            StampForskTags(attr, new ForskStamp { Kind = AnalysisKind });
            // Vertex colours need a shaded mode; the overlay reads in Wireframe views too.
            var shaded = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.ShadedId);
            if (shaded != null)
                attr.SetDisplayModeOverride(shaded);
            id = doc.Objects.AddMesh(mesh, attr);
            if (id == Guid.Empty)
                throw new InvalidOperationException("Could not add the daylight mesh.");
        }

        doc.Views.Redraw();
        return new JObject
        {
            ["id"] = id == Guid.Empty ? "" : id.ToString(),
            ["cells"] = cells.Count,
            ["layer"] = AnalysisLayerName,
            ["deleted"] = deleted,
            ["message"] = $"Painted {cells.Count} daylight cells on {AnalysisLayerName}."
        };
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
