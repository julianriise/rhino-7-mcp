using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The Analysis set's daylight map sheet on the layout and in the PDF. The
/// page is the plan's drawing in a detail framed as the plan sheet frames it,
/// with a legend column at the right and each room's DF mean under its tag.
/// The daylight mesh never prints (it is off in every detail, and the plan
/// drawing sits where its bake moved it, not at the model's place), so the
/// PDF draws the mesh's cells as a picture under the lines, through the plan
/// bake's model-to-drawing map and the detail's drawing-to-page map. The
/// layout in Rhino shows the plan, the legend and the labels without colour.
/// </summary>
public partial class RhinoMCPFunctions
{
    /// <summary>A daylight mesh with faces is on the file, shown or hidden.</summary>
    internal static bool HasDaylightMap(RhinoDoc doc) => DaylightMeshes(doc).Count > 0;

    /// <summary>Print runs daylight first: it is in the Analysis set and never ran (Analysis.NeedsDaylightRun).</summary>
    internal static bool DaylightMissing(RhinoDoc doc) =>
        doc != null && Analysis.NeedsDaylightRun(ReadAnalysis(doc), HasDaylightMap(doc));

    private static List<Mesh> DaylightMeshes(RhinoDoc doc)
    {
        var meshes = new List<Mesh>();
        if (doc == null) return meshes;
        foreach (var obj in AnalysisOverlays(doc))
            if (obj?.Geometry is Mesh mesh && mesh.Faces.Count > 0) meshes.Add(mesh);
        return meshes;
    }

    /// <summary>The plan bake's model-to-drawing map, kept on the file for the map sheet: three points through ToDrawing.</summary>
    private static void NotePlanMap(RhinoDoc doc, Transform worldToHld, Vector3d delta)
    {
        if (doc == null) return;
        var o = ToDrawing(Point3d.Origin, worldToHld, delta);
        var x = ToDrawing(new Point3d(1000, 0, 0), worldToHld, delta);
        var y = ToDrawing(new Point3d(0, 1000, 0), worldToHld, delta);
        var map = new SheetFlat.Affine
        {
            A = (x.X - o.X) / 1000,
            B = (y.X - o.X) / 1000,
            C = o.X,
            D = (x.Y - o.Y) / 1000,
            E = (y.Y - o.Y) / 1000,
            F = o.Y
        };
        doc.Strings.SetString(SheetFlat.CentreSection, DaylightPrint.PlanMapEntry, DaylightPrint.FormatAffine(map));
    }

    /// <summary>The page's detail map from drawing space to paper mm, as FlattenPage draws the plan. False with no detail.</summary>
    private static bool TryDrawingToPaper(RhinoDoc doc, RhinoPageView page, out SheetFlat.Affine map)
    {
        map = SheetFlat.Affine.Identity;
        var detail = page?.GetDetailViews()?.FirstOrDefault(d => d != null);
        var pageMm = doc == null ? 0 : MmToPage(doc, 1.0);
        if (detail == null || pageMm <= 0) return false;
        var t = Transform.Scale(Point3d.Origin, 1.0 / pageMm) * detail.WorldToPageTransform;
        map = new SheetFlat.Affine { A = t.M00, B = t.M01, C = t.M03, D = t.M10, E = t.M11, F = t.M13 };
        return true;
    }

    /// <summary>The daylight cells on this page as one picture under the plan's lines, or null with no map or no plan bake.</summary>
    private static SheetPdf.Image DaylightMapImage(RhinoDoc doc, RhinoPageView page)
    {
        if (!TryDrawingToPaper(doc, page, out var toPaper)
            || !DaylightPrint.TryAffine(doc.Strings.GetValue(SheetFlat.CentreSection, DaylightPrint.PlanMapEntry), out var toDrawing))
            return null;
        var map = DaylightPrint.Then(toDrawing, toPaper);
        var none = DaylightPrint.PrintRgb(0.5);
        var tris = new List<DaylightPrint.Tri>();
        foreach (var mesh in DaylightMeshes(doc))
        {
            var coloured = mesh.VertexColors.Count == mesh.Vertices.Count;
            for (var i = 0; i < mesh.Faces.Count; i++)
            {
                var face = mesh.Faces[i];
                var corners = face.IsQuad ? new[] { face.A, face.B, face.C, face.D } : new[] { face.A, face.B, face.C };
                // The tracer paints a cell one colour; the corners' mean stands for any other mesh.
                var rgb = none;
                if (coloured)
                {
                    var colours = corners.Select(c => mesh.VertexColors[c]).ToList();
                    rgb = new[]
                    {
                        DaylightPrint.Tint((byte)Math.Round(colours.Average(c => c.R))),
                        DaylightPrint.Tint((byte)Math.Round(colours.Average(c => c.G))),
                        DaylightPrint.Tint((byte)Math.Round(colours.Average(c => c.B)))
                    };
                }
                var points = corners.Select(c =>
                {
                    var v = mesh.Vertices[c];
                    map.Apply(v.X, v.Y, out var px, out var py);
                    return (X: px, Y: py);
                }).ToList();
                for (var k = 1; k + 1 < points.Count; k++)
                {
                    tris.Add(new DaylightPrint.Tri
                    {
                        X0 = points[0].X, Y0 = points[0].Y,
                        X1 = points[k].X, Y1 = points[k].Y,
                        X2 = points[k + 1].X, Y2 = points[k + 1].Y,
                        R = rgb[0], G = rgb[1], B = rgb[2]
                    });
                }
            }
        }
        var raster = DaylightPrint.Rasterise(tris);
        return raster == null
            ? null
            : new SheetPdf.Image { Picture = raster.Picture, X0 = raster.X0, Y0 = raster.Y0, X1 = raster.X1, Y1 = raster.Y1, Under = true };
    }

    /// <summary>The PDF's pictures on the daylight map page: the map under the plan, and the legend's ramp in its frame.</summary>
    private static List<SheetPdf.Image> DaylightPageImages(RhinoDoc doc, RhinoPageView page)
    {
        var images = new List<SheetPdf.Image>();
        if (!Analysis.IsMapSheet(ViewKeyForPage(page))) return images;
        var map = DaylightMapImage(doc, page);
        if (map != null) images.Add(map);
        var settings = new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = false,
            ViewportFilter = page.MainViewport
        };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (obj?.Attributes == null || obj.Attributes.Space != ActiveSpace.PageSpace) continue;
            if (obj.Attributes.GetUserString("forsk:role") != DaylightPrint.RampRole) continue;
            if (OfficeLogo.TryParseBox(obj.Attributes.GetUserString(DaylightPrint.RampBoxKey), out var x0, out var y0, out var x1, out var y1))
                images.Add(new SheetPdf.Image { Picture = DaylightPrint.RampPicture(), X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Under = true });
        }
        return images;
    }

    /// <summary>
    /// The daylight map page's own parts, after its footer: the legend in the
    /// column right of the detail (title, the ramp's frame, a tick and label
    /// per DF, the last run's line) and each room's DF mean under its tag on
    /// the plan. The ramp and the map are pictures the PDF draws.
    /// </summary>
    private JObject AddDaylightMapParts(RhinoDoc doc, RhinoPageView page, LayoutViewSpec spec, string stableId, string sheetNo, JArray ids)
    {
        var layer = EnsureLayer(doc, "A-ANNO", Color.FromArgb(200, 160, 40));
        var pageId = page.MainViewport.Id;
        ObjectAttributes Attr(string role)
        {
            var attr = LayoutAttr(layer.Index, pageId, spec.View, stableId);
            attr.SetUserString("forsk:role", role);
            attr.SetUserString("forsk:sheet_id", spec.View);
            attr.SetUserString("forsk:sheet_no", sheetNo ?? "");
            return attr;
        }
        var state = ReadAnalysis(doc);
        var legend = DaylightPrint.LegendAt(SheetWidthMm - LayoutMarginMm - DaylightPrint.ColumnMm, SheetHeightMm - LayoutMarginMm);
        const double text = DaylightPrint.TextMm;
        AddPaperText(doc, ids, SheetLang.Pick(false, "Daylight factor", "Dagslysfaktor"), legend.TitleX, legend.TitleY, text,
            TextHorizontalAlignment.Left, TextVerticalAlignment.Bottom, Attr("daylight_legend"));
        var ramp = Attr(DaylightPrint.RampRole);
        ramp.SetUserString(DaylightPrint.RampBoxKey, OfficeLogo.FormatBox(legend.BarX0, legend.BarY0, legend.BarX1, legend.BarY1));
        AddPaperRect(doc, ids, legend.BarX0, legend.BarY0, legend.BarX1, legend.BarY1, ramp);
        foreach (var tick in legend.Ticks)
        {
            AddPaperLine(doc, ids, legend.BarX1, tick.Y, legend.BarX1 + 1.5, tick.Y, Attr("daylight_legend"));
            AddPaperText(doc, ids, tick.Label, legend.BarX1 + 2.5, tick.Y, text,
                TextHorizontalAlignment.Left, TextVerticalAlignment.Middle, Attr("daylight_legend"));
        }
        // "DF mean 2.1 % in 8 rooms" on two lines: the column is narrow.
        state.Last.TryGetValue(Analysis.Daylight, out var last);
        var note = string.IsNullOrWhiteSpace(last) ? new[] { "Daylight has not run." } : last.Split(new[] { " in " }, 2, StringSplitOptions.None);
        for (var i = 0; i < note.Length; i++)
            AddPaperText(doc, ids, i == 0 ? note[i] : "in " + note[i], legend.TitleX, legend.NoteY - i * text * SheetPdf.LinePitch, text,
                TextHorizontalAlignment.Left, TextVerticalAlignment.Top, Attr("daylight_legend"));

        var labelled = 0;
        if (TryDrawingToPaper(doc, page, out var toPaper))
        {
            foreach (var tag in PlanRoomTags(doc))
            {
                if (!state.RoomDf.TryGetValue(tag.Key, out var mean)) continue;
                var box = tag.Value;
                toPaper.Apply((box.Min.X + box.Max.X) / 2, box.Min.Y, out var x, out var y);
                if (AddPaperText(doc, ids, DaylightPrint.RoomLabel(mean), x, y - 0.8, text,
                        TextHorizontalAlignment.Center, TextVerticalAlignment.Top, Attr("daylight_room")) != Guid.Empty)
                    labelled++;
            }
        }
        var meshes = DaylightMeshes(doc);
        return new JObject
        {
            ["faces"] = meshes.Sum(m => m.Faces.Count),
            ["rooms"] = state.RoomDf.Count,
            ["rooms_labelled"] = labelled,
            ["plan_map"] = doc.Strings.GetValue(SheetFlat.CentreSection, DaylightPrint.PlanMapEntry) != null
        };
    }

    /// <summary>Each tagged room's tag on the plan drawing, by its room id: the area line's box, else the name's.</summary>
    private static Dictionary<string, BoundingBox> PlanRoomTags(RhinoDoc doc)
    {
        var tags = new Dictionary<string, BoundingBox>(StringComparer.Ordinal);
        var drawLayer = FindDrawLayer(doc, SheetSet.PlanId);
        if (drawLayer == null) return tags;
        var areas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in doc.Objects.FindByLayer(drawLayer) ?? new RhinoObject[0])
        {
            if (obj?.Attributes?.GetUserString("forsk:role") != "room_tag") continue;
            var room = obj.Attributes.GetUserString(RoomIdKey);
            if (string.IsNullOrEmpty(room)) continue;
            var area = obj.Attributes.GetUserString("forsk:tag") == "area";
            if (!area && (areas.Contains(room) || tags.ContainsKey(room))) continue;
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            tags[room] = box;
            if (area) areas.Add(room);
        }
        return tags;
    }
}
