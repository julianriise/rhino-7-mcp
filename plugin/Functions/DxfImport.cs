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
/// is stamped forsk:label_source dxf and forsk:dxf_handle.
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
        List<DxfText.Entity> source;
        try
        {
            source = DxfText.Read(path);
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

        var added = 0;
        var texts = new List<RhinoObject>();
        var placed = new List<DxfText.Placed>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (before.Contains(obj.Id)) continue;
            added++;
            if (!(obj.Geometry is TextEntity text)) continue;
            var index = obj.Attributes.LayerIndex;
            var layer = index >= 0 && index < doc.Layers.Count ? doc.Layers[index].Name : "";
            texts.Add(obj);
            placed.Add(new DxfText.Placed(layer, text.Plane.Origin.X, text.Plane.Origin.Y, text.PlainText));
        }

        var rewritten = DxfText.Rewrite(source, placed, Math.Max(doc.ModelAbsoluteTolerance, 1.0));
        var matched = 0;
        var changed = new JArray();
        var unmatched = new JArray();
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
            + (unmatched.Count > 0 ? ", " + unmatched.Count + " left as Rhino made them" : "") + ".";
        return new JObject
        {
            ["success"] = ran,
            ["objects"] = added,
            ["texts"] = texts.Count,
            ["matched"] = matched,
            ["rewritten"] = changed,
            ["unmatched"] = unmatched,
            ["warnings"] = warnings,
            ["message"] = message
        };
    }
}
