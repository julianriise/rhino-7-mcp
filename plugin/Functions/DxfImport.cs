using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Forsk's DXF import: Rhino's import, then every imported text rewritten
/// from the DXF source through DxfText. Rhino parses DXF text as RTF and
/// keeps only the result, so an escape such as \U+00F8 is lost on the way in;
/// read from the file it is decoded once and stored on the text itself, where
/// rooms_detect, the plan tags, and get_objects read it. Each rewritten text
/// is stamped forsk:label_source dxf and forsk:dxf_handle. Rhino also takes
/// the drawing's units from its own import setting, not from the file, so the
/// units are read from the DXF (DxfUnits) and what came in is scaled to true
/// size in the model.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string LabelSourceKey = "forsk:label_source";

    [McpCommand("dxf_import")]
    public JObject DxfImport(JObject parameters)
    {
        var path = parameters["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new Exception("path must be an existing DXF file.");

        var warnings = new JArray();
        var bytes = File.ReadAllBytes(path);
        var units = DxfUnits.Read(bytes);
        if (units.Guessed) warnings.Add(DxfUnits.Line(units));
        List<DxfText.Entity> source;
        try
        {
            source = DxfText.Read(bytes);
        }
        catch (Exception ex)
        {
            source = new List<DxfText.Entity>();
            warnings.Add("DXF text not read, texts stay as Rhino imported them: " + ex.Message);
        }

        var doc = RhinoDoc.ActiveDoc;
        var before = new HashSet<Guid>();
        foreach (var obj in EnumerateDocObjects(doc)) before.Add(obj.Id);
        var ran = RhinoApp.RunScript("_-Import \"" + path + "\" _Enter", false);

        var imported = new List<Guid>();
        var curves = BoundingBox.Empty;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (before.Contains(obj.Id)) continue;
            imported.Add(obj.Id);
            if (obj.Geometry is Curve) curves.Union(obj.Geometry.GetBoundingBox(true));
        }

        // True size: the DXF's units into the model's, over whatever Rhino's
        // import already scaled by. Not told (no curves, or no unit step near
        // what is measured), Rhino is taken to have brought it in as drawn.
        var modelMm = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);
        var span = curves.IsValid ? Math.Max(curves.Max.X - curves.Min.X, curves.Max.Y - curves.Min.Y) : 0;
        var rhino = DxfUnits.Applied(span, units.Span, modelMm);
        var scale = units.Mm / modelMm;
        var fix = scale / (rhino > 0 ? rhino : 1.0);
        if (Math.Abs(fix - 1.0) > 1e-9)
        {
            var toSize = Transform.Scale(Point3d.Origin, fix);
            var left = 0;
            for (var i = 0; i < imported.Count; i++)
            {
                var sized = doc.Objects.Transform(imported[i], toSize, true);
                if (sized == Guid.Empty) left++;
                else imported[i] = sized;
            }
            if (left > 0) warnings.Add(left + " object" + (left == 1 ? " was" : "s were") + " not scaled to size.");
        }
        if (rhino <= 0 && units.Span > 0 && span > 0)
            warnings.Add("Rhino's import scale not told: the curves came in "
                + (span / units.Span).ToString("0.###", CultureInfo.InvariantCulture)
                + " times their DXF size, no unit step. Taken as drawn. Check a known length.");
        foreach (var entity in source)
        {
            entity.X *= scale;
            entity.Y *= scale;
            entity.AlignX *= scale;
            entity.AlignY *= scale;
        }

        var added = imported.Count;
        var texts = new List<RhinoObject>();
        var placed = new List<DxfText.Placed>();
        foreach (var id in imported)
        {
            var obj = doc.Objects.FindId(id);
            if (obj == null || !(obj.Geometry is TextEntity text)) continue;
            var index = obj.Attributes.LayerIndex;
            var layer = index >= 0 && index < doc.Layers.Count ? doc.Layers[index].Name : "";
            texts.Add(obj);
            placed.Add(new DxfText.Placed(layer, text.Plane.Origin.X, text.Plane.Origin.Y, text.PlainText));
        }

        var rewritten = DxfText.Rewrite(source, placed, Math.Max(doc.ModelAbsoluteTolerance, 1.0));
        var matched = 0;
        var changed = new JArray();
        var unmatched = new JArray();
        var suspects = new JArray();
        for (var i = 0; i < texts.Count; i++)
        {
            var obj = texts[i];
            if (rewritten[i].Source < 0)
            {
                var near = DxfText.Nearest(source, placed[i]);
                unmatched.Add(placed[i].Layer + " '" + placed[i].Text + "' at "
                    + placed[i].X.ToString("0", CultureInfo.InvariantCulture) + ","
                    + placed[i].Y.ToString("0", CultureInfo.InvariantCulture)
                    + (near < 0 ? ", no DXF text on its layer" : ", nearest DXF text " + near.ToString("0.#", CultureInfo.InvariantCulture) + " mm"));
                if (DxfText.LooksMangled(placed[i].Text)) suspects.Add(placed[i].Text);
                continue;
            }
            matched++;
            var entity = source[rewritten[i].Source];
            if (entity.Text != placed[i].Text && obj.Geometry is TextEntity text)
            {
                var copy = (TextEntity)text.Duplicate();
                copy.PlainText = entity.Text;
                if (doc.Objects.Replace(obj.Id, copy))
                    changed.Add(placed[i].Text + " -> " + entity.Text);
                else
                    warnings.Add("Text " + entity.Handle + " was not rewritten.");
            }
            var attr = doc.Objects.FindId(obj.Id)?.Attributes.Duplicate();
            if (attr == null) continue;
            attr.SetUserString(LabelSourceKey, "dxf");
            attr.SetUserString("forsk:dxf_handle", entity.Handle);
            doc.Objects.ModifyAttributes(obj.Id, attr, true);
        }

        doc.Views.Redraw();
        var message = "Imported " + Path.GetFileName(path) + ": " + added + " object" + (added == 1 ? "" : "s")
            + ". Texts " + matched + "/" + texts.Count + " read from the DXF, " + changed.Count + " rewritten"
            + (unmatched.Count > 0 ? ", " + unmatched.Count + " left as Rhino made them" : "") + ". "
            + DxfUnits.Line(units)
            + (rhino > 0 && Math.Abs(fix - 1.0) > 1e-9 && Math.Abs(rhino - 1.0) > 1e-9
                ? " Rhino's import had it at ×" + rhino.ToString("0.###", CultureInfo.InvariantCulture) + "; set right."
                : "")
            + (suspects.Count > 0
                ? " Labels suspect " + suspects.Count + " (" + string.Join(", ", suspects) + "): a DXF escape lost, and no DXF text matched to restore it."
                : "");
        return new JObject
        {
            ["success"] = ran,
            ["objects"] = added,
            ["texts"] = texts.Count,
            ["matched"] = matched,
            ["rewritten"] = changed,
            ["unmatched"] = unmatched,
            ["labels_suspect"] = suspects,
            ["units"] = units.Unit,
            ["insunits"] = units.InsUnits.HasValue ? (JToken)units.InsUnits.Value : JValue.CreateNull(),
            ["scale"] = units.Mm,
            ["units_guessed"] = units.Guessed,
            ["warnings"] = warnings,
            ["message"] = message
        };
    }
}
