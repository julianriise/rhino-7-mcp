using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 R3: the sheet set as DWG or DXF, one file per page, in paper
/// millimetres at 1:1. Rhino 7 cannot write a layout, so each page is
/// flattened first: its own objects (title block, lists) as they are, and the
/// detail's S-DRAW drawing through the detail's world-to-page map. Each piece
/// goes to an export layer by its forsk:role (SheetFlat), a stroke ribbon
/// comes back as its centreline at its pen, and Rhino's own exporter writes
/// the file: scripted from the active document with only the flat sheet
/// selected and the scheme named (SheetFlat.AcadScheme), or, when that writes
/// nothing of AutoCAD 2013 or later, from a headless document. The version
/// the file has is read back and returned.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string ExportNeedsFolderMessage = "export_sheets needs an absolute folder.";
    private const string ExportFormatMessage = "format is dwg or dxf.";
    private const string ExportWriteFailedPrefix = "Sheet export failed";
    private const string WriterHeadless = "headless";
    private const string WriterActiveDoc = "active_doc";

    /// <summary>One piece of a flat sheet, in paper mm, on its export layer.</summary>
    private sealed class FlatPiece
    {
        public string Layer;
        /// <summary>Its own lineweight in mm, or null for the layer's.</summary>
        public double? Weight;
        /// <summary>A curve or a hatch, already in paper mm. Null for a text.</summary>
        public GeometryBase Geometry;
        public string HatchPattern;
        public string Text;
        public Plane TextPlane;
        public double TextMm;
        public string Justify;
        public string Font;
    }

    [McpCommand("export_sheets", ModelView = true)]
    public JObject ExportSheets(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");

        var folder = parameters?["folder"]?.ToString()?.Trim() ?? "";
        var format = (parameters?["format"]?.ToString() ?? "dwg").Trim().ToLowerInvariant();
        if (format != "dwg" && format != "dxf")
            return SheetExportResult("", "dwg", new JArray(), "", 0, ExportFormatMessage);
        if (folder.Length == 0 || !Path.IsPathRooted(folder))
            return SheetExportResult("", format, new JArray(), "", 0, ExportNeedsFolderMessage);

        // The set as Print writes it: every sheet that is on, drawn from the model now.
        var pack = LayoutPack(new JObject());
        if ((pack["count"]?.Value<int>() ?? 0) == 0)
            return SheetExportResult("", format, new JArray(), "", 0, pack["message"]?.ToString() ?? NothingToLayOutMessage);
        UseModelView(doc);

        var full = Path.GetFullPath(folder);
        Directory.CreateDirectory(full);
        var project = ProjectMetaRecord(doc)["project"]?.ToString();
        var files = new JArray();
        var misc = new SortedSet<string>(StringComparer.Ordinal);
        string writer = null;
        string version = null;
        string failed = null;
        foreach (var page in InSetOrder(doc, MatchingForskPages(doc, null)))
        {
            LeaveDetail(page);
            var cells = PageTitleCells(doc, page);
            cells.TryGetValue("number", out var number);
            cells.TryGetValue("drawing", out var title);
            var name = SheetFlat.FileName(project, number, string.IsNullOrWhiteSpace(title) ? PageViewName(page) : title, format);
            var path = Path.Combine(full, name);
            var pieces = FlattenPage(doc, page, misc);
            try
            {
                // The scripted write first. Once it writes nothing modern, the rest of the set skips it.
                string wrote;
                if (writer != WriterHeadless && WriteFromActiveDoc(doc, pieces, path)
                    && SheetFlat.ModernAcad(wrote = FileAcadVersion(path)))
                    writer = WriterActiveDoc;
                else if (WriteHeadless(doc, pieces, path))
                {
                    writer = WriterHeadless;
                    wrote = FileAcadVersion(path);
                }
                else
                {
                    failed = name;
                    break;
                }
                // The set's version is its oldest file's.
                if (version == null || string.CompareOrdinal(wrote, version) < 0) version = wrote;
                files.Add(name);
            }
            finally
            {
                foreach (var piece in pieces)
                    piece.Geometry?.Dispose();
            }
        }
        UseModelView(doc);
        LogPrint(full, files.Count, "sheets " + format + " writer " + (writer ?? "none") + " " + (version ?? "")
            + (misc.Count > 0 ? " misc " + string.Join(",", misc) : "") + (failed != null ? " failed " + failed : ""));

        var label = format.ToUpperInvariant();
        var message = failed != null
            ? ExportWriteFailedPrefix + " at " + failed + ". " + files.Count.ToString(CultureInfo.InvariantCulture) + " written."
            : "Exported " + files.Count.ToString(CultureInfo.InvariantCulture) + (files.Count == 1 ? " sheet" : " sheets")
              + " as " + label + " to " + full + ".";
        var result = SheetExportResult(full, format, files, writer ?? "", misc.Count, message);
        result["misc_roles"] = new JArray(misc.ToArray());
        result["acad_version"] = version ?? "";
        return result;
    }

    private static JObject SheetExportResult(string folder, string format, JArray files, string writer, int misc, string message)
    {
        return new JObject
        {
            ["folder"] = folder ?? "",
            ["format"] = format ?? "",
            ["count"] = files?.Count ?? 0,
            ["files"] = files ?? new JArray(),
            ["writer"] = writer ?? "",
            ["misc"] = misc,
            ["message"] = message ?? ""
        };
    }

    /// <summary>"Plan" from "Forsk — Plan": a file name when the title block has no title.</summary>
    private static string PageViewName(RhinoPageView page)
    {
        var name = page?.PageName ?? "";
        return name.StartsWith(LayoutPagePrefix, StringComparison.Ordinal) ? name.Substring(LayoutPagePrefix.Length) : name;
    }

    /// <summary>
    /// One page as flat pieces in paper mm: the page's own objects, then the
    /// detail's drawing layer and the role layers under it (an elevation's
    /// lines) through the detail's world-to-page map.
    /// Roles with no export layer are added to <paramref name="misc"/>.
    /// </summary>
    private static List<FlatPiece> FlattenPage(RhinoDoc doc, RhinoPageView page, ISet<string> misc)
    {
        var pieces = new List<FlatPiece>();
        var pageMm = MmToPage(doc, 1.0);
        var toMm = pageMm > 0 ? Transform.Scale(Point3d.Origin, 1.0 / pageMm) : Transform.Identity;

        var settings = new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = false,
            ViewportFilter = page.MainViewport
        };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (obj == null || obj is DetailViewObject) continue;
            if (obj.Attributes.Space != ActiveSpace.PageSpace) continue;
            AddFlat(doc, obj, toMm, pieces, misc);
        }

        var detail = page.GetDetailViews()?.FirstOrDefault(d => d != null);
        var drawLayer = detail == null ? null : FindDrawLayer(doc, ViewKeyForPage(page));
        if (drawLayer != null)
        {
            var map = toMm * detail.WorldToPageTransform;
            foreach (var layer in doc.Layers)
            {
                if (layer == null || layer.IsDeleted || !SheetFlat.InDrawing(layer.FullPath, drawLayer.FullPath)) continue;
                foreach (var obj in doc.Objects.FindByLayer(layer) ?? new RhinoObject[0])
                {
                    if (obj == null || !IsPrintDrawing(doc, obj) || obj.IsHidden) continue;
                    if (obj.Attributes.Space != ActiveSpace.ModelSpace) continue;
                    AddFlat(doc, obj, map, pieces, misc);
                }
            }
        }
        return pieces;
    }

    private static void AddFlat(RhinoDoc doc, RhinoObject obj, Transform map, List<FlatPiece> pieces, ISet<string> misc)
    {
        var attr = obj.Attributes;
        var role = attr.GetUserString("forsk:role");
        var layer = SheetFlat.LayerFor(role);
        if (layer == SheetFlat.Misc) misc.Add(string.IsNullOrEmpty(role) ? "(none)" : role);
        var affine = new SheetFlat.Affine { A = map.M00, B = map.M01, C = map.M03, D = map.M10, E = map.M11, F = map.M13 };
        var fromObject = attr.PlotWeightSource == ObjectPlotWeightSource.PlotWeightFromObject;
        var geometry = obj.Geometry;

        if (geometry is TextEntity text)
        {
            var origin = text.Plane.Origin;
            origin.Transform(map);
            var x = text.Plane.XAxis;
            x.Transform(map);
            if (!x.Unitize()) x = Vector3d.XAxis;
            var plane = new Plane(origin, x, Vector3d.CrossProduct(Vector3d.ZAxis, x));
            pieces.Add(new FlatPiece
            {
                Layer = layer,
                Text = text.PlainText ?? "",
                TextPlane = plane,
                TextMm = SheetFlat.TextMm(text.TextHeight, affine),
                Justify = SheetFlat.Justification(text.TextHorizontalAlignment.ToString(), text.TextVerticalAlignment.ToString()),
                Font = text.Font?.QuartetName
            });
            return;
        }

        if (geometry is Hatch hatch)
        {
            var pen = attr.GetUserString(SheetFlat.PenKey);
            var stroke = attr.GetUserString(SheetFlat.StrokeKey);
            var how = SheetFlat.HowToDraw(true, !string.IsNullOrEmpty(pen), !string.IsNullOrEmpty(stroke));
            if (how == SheetFlat.Draw.Skip) return;
            if (how == SheetFlat.Draw.Stroke)
            {
                double.TryParse(pen, NumberStyles.Float, CultureInfo.InvariantCulture, out var penMm);
                var weight = SheetFlat.Weight(how, penMm, false, 0);
                foreach (var seg in SheetFlat.Decode(stroke).Select(s => SheetFlat.Map(s, affine)))
                {
                    var p = seg.P;
                    Curve curve = seg.Arc
                        ? new ArcCurve(new Arc(new Point3d(p[0], p[1], 0), new Point3d(p[2], p[3], 0), new Point3d(p[4], p[5], 0)))
                        : new LineCurve(new Point3d(p[0], p[1], 0), new Point3d(p[2], p[3], 0));
                    if (!curve.IsValid)
                    {
                        curve.Dispose();
                        continue;
                    }
                    pieces.Add(new FlatPiece { Layer = layer, Weight = weight, Geometry = curve });
                }
                return;
            }
            var copy = (Hatch)hatch.Duplicate();
            copy.Transform(map);
            copy.ScalePattern(map);
            pieces.Add(new FlatPiece
            {
                Layer = layer,
                Weight = SheetFlat.Weight(how, 0, fromObject, attr.PlotWeight),
                Geometry = copy,
                HatchPattern = doc.HatchPatterns.FindIndex(hatch.PatternIndex)?.Name ?? "Solid"
            });
            return;
        }

        if (geometry is Curve source)
        {
            var copy = source.DuplicateCurve();
            if (copy == null) return;
            copy.Transform(map);
            pieces.Add(new FlatPiece
            {
                Layer = layer,
                Weight = SheetFlat.Weight(SheetFlat.Draw.AsIs, 0, fromObject, attr.PlotWeight),
                Geometry = copy
            });
        }
    }

    /// <summary>The flat sheet in a document of its own, written by Rhino's exporter. False when no file came out.</summary>
    private static bool WriteHeadless(RhinoDoc source, List<FlatPiece> pieces, string path)
    {
        try
        {
            using (var target = RhinoDoc.CreateHeadless(null))
            {
                if (target == null) return false;
                target.ModelUnitSystem = UnitSystem.Millimeters;
                var layers = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var def in SheetFlat.Layers)
                    layers[def.Name] = target.Layers.Add(ExportLayer(def));
                AddPieces(source, target, pieces, layers, null);
                return WriteAndCheck(target, path);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The fallback: the flat sheet added to the active document on the export
    /// layers, written with only it selected, then deleted, with the user's
    /// selection put back. Layers this made are deleted again.
    /// </summary>
    private static bool WriteFromActiveDoc(RhinoDoc doc, List<FlatPiece> pieces, string path)
    {
        var picked = doc.Objects.GetSelectedObjects(false, false)?.Select(o => o.Id).ToList() ?? new List<Guid>();
        var made = new List<int>();
        var added = new List<Guid>();
        try
        {
            var layers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var def in SheetFlat.Layers)
            {
                var found = doc.Layers.FindByFullPath(def.Name, -1);
                if (found < 0)
                {
                    found = doc.Layers.Add(ExportLayer(def));
                    if (found >= 0) made.Add(found);
                }
                layers[def.Name] = found;
            }
            doc.Objects.UnselectAll(true);
            AddPieces(doc, doc, pieces, layers, added);
            doc.Objects.Select(added, true);
            if (File.Exists(path)) File.Delete(path);
            RhinoApp.RunScript(SheetFlat.ExportScript(path), false);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            foreach (var id in added)
                doc.Objects.Delete(id, true);
            foreach (var index in made)
                doc.Layers.Delete(index, true);
            doc.Objects.UnselectAll(true);
            if (picked.Count > 0) doc.Objects.Select(picked, true);
        }
    }

    private static bool WriteAndCheck(RhinoDoc doc, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using (var options = new FileWriteOptions
        {
            SuppressAllInput = true,
            SuppressDialogBoxes = true
        })
        {
            if (!doc.WriteFile(path, options)) return false;
        }
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    /// <summary>The written file's AutoCAD version off its first 4 KB; empty when it does not read.</summary>
    private static string FileAcadVersion(string path)
    {
        try
        {
            using (var stream = File.OpenRead(path))
            {
                var head = new byte[4096];
                var read = stream.Read(head, 0, head.Length);
                Array.Resize(ref head, read);
                return SheetFlat.AcadVersion(head);
            }
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static Layer ExportLayer(SheetFlat.LayerDef def)
    {
        return new Layer
        {
            Name = def.Name,
            Color = Color.Black,
            PlotColor = Color.Black,
            PlotWeight = def.WeightMm
        };
    }

    private static void AddPieces(RhinoDoc source, RhinoDoc target, List<FlatPiece> pieces, Dictionary<string, int> layers, List<Guid> added)
    {
        var patterns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in pieces)
        {
            if (!layers.TryGetValue(piece.Layer, out var layer) || layer < 0) continue;
            var attr = new ObjectAttributes
            {
                LayerIndex = layer,
                ColorSource = ObjectColorSource.ColorFromLayer,
                PlotColorSource = ObjectPlotColorSource.PlotColorFromLayer,
                PlotWeightSource = piece.Weight.HasValue ? ObjectPlotWeightSource.PlotWeightFromObject : ObjectPlotWeightSource.PlotWeightFromLayer,
                PlotWeight = piece.Weight ?? 0
            };
            var id = Guid.Empty;
            if (piece.Text != null)
            {
                if (piece.Text.Length == 0 || piece.TextMm <= 0) continue;
                var justify = (TextJustification)Enum.Parse(typeof(TextJustification), piece.Justify);
                id = target.Objects.AddText(piece.Text, piece.TextPlane, piece.TextMm,
                    string.IsNullOrEmpty(piece.Font) ? "Arial" : piece.Font, false, false, justify, attr);
            }
            else if (piece.Geometry is Hatch hatch)
            {
                var index = PatternIn(source, target, piece.HatchPattern, patterns);
                if (index < 0) continue;
                var copy = (Hatch)hatch.Duplicate();
                copy.PatternIndex = index;
                id = target.Objects.AddHatch(copy, attr);
                copy.Dispose();
            }
            else if (piece.Geometry is Curve curve)
                id = target.Objects.AddCurve(curve, attr);
            if (id != Guid.Empty) added?.Add(id);
        }
    }

    /// <summary>The fill's pattern in the target document: there by name, or copied from the source.</summary>
    private static int PatternIn(RhinoDoc source, RhinoDoc target, string name, Dictionary<string, int> known)
    {
        name = string.IsNullOrEmpty(name) ? "Solid" : name;
        if (known.TryGetValue(name, out var cached)) return cached;
        var found = target.HatchPatterns.FindName(name);
        var index = found != null && !found.IsDeleted ? found.Index : -1;
        if (index < 0)
        {
            var from = source.HatchPatterns.FindName(name);
            try { index = from == null ? -1 : target.HatchPatterns.Add(from); }
            catch (Exception) { index = -1; }
        }
        if (index < 0) index = SolidPatternIndex(target);
        known[name] = index;
        return index;
    }
}
