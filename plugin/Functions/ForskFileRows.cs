using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The document as Forsk's chips and the v3 classifier read it: one row per
/// object from one enumeration (hidden objects included), and the facts that
/// are not objects. The classifier itself is pure (ForskFile.cs). Also the
/// two writes the classifier depends on: the daylight map after an edit, and
/// the sheet fingerprint at Print.
/// </summary>
public partial class RhinoMCPFunctions
{
    internal const string DaylightStaleKey = "forsk:daylight_stale";
    internal const string ImportReviewRowsKey = "forsk:import_review_rows";
    internal const string SheetFingerprintKey = "sheet_fingerprint";

    public static List<ChipRow> ChipRows(RhinoDoc doc)
    {
        var rows = new List<ChipRow>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null) continue;
            var index = obj.Attributes.LayerIndex;
            var geometry = obj.Geometry;
            var generated = IsForskGenerated(obj);
            var wallRings = generated && string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase)
                ? WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"))
                : null;
            var solid = geometry is Brep brep && brep.IsSolid
                || geometry is Extrusion extrusion && extrusion.IsSolid
                || geometry is Mesh mesh && mesh.IsClosed;
            rows.Add(new ChipRow
            {
                Id = obj.Id.ToString(),
                Generated = generated,
                Kind = GetForskKind(obj),
                OpeningKind = obj.Attributes.GetUserString("forsk:opening_kind"),
                Layer = index >= 0 && index < doc.Layers.Count ? doc.Layers[index].Name : "",
                Visible = obj.Visible,
                ImportKind = obj.Attributes.GetUserString(ImportKindKey),
                ScaleStatus = obj.Attributes.GetUserString(ImportScaleStatusKey),
                Existing = IsExistingUnderlay(doc, obj),
                Selected = obj.IsSelected(false) > 0,
                Curve = geometry is Curve,
                Closed = geometry is Curve curve ? curve.IsClosed : solid,
                Solid = solid,
                Stale = obj.Attributes.GetUserString(DaylightStaleKey) == "1",
                Review = obj.Attributes.GetUserString(ImportReviewRowsKey),
                Stamp = generated ? EditStamp(obj) : null,
                Name = obj.Attributes.GetUserString("forsk:room_name") ?? obj.Name,
                Area = obj.Attributes.GetUserString("forsk:area"),
                PathReads = wallRings != null,
                Runs = wallRings == null ? 0 : WallJoins.Runs(wallRings, Math.Max(doc.ModelAbsoluteTolerance, 1.0)).Count
            });
        }
        return rows;
    }

    /// <summary>The document for the classifier. The window adds the key, the listener and Undo.</summary>
    public static DocInput ReadDocInput(RhinoDoc doc)
    {
        var input = new DocInput { Rows = ChipRows(doc) };
        if (doc == null) return input;
        input.StoredFingerprint = doc.Strings.GetValue(LayoutMetaSection, SheetFingerprintKey);
        input.Layouts = MatchingForskPages(doc, null).Count;
        foreach (var def in ReadSectionDefs(doc))
            if (!string.IsNullOrEmpty(def.Letter)) input.SectionLetters.Add(def.Letter);
        foreach (var key in new[] { "project", "client", "address", "date", "scale_label" })
        {
            var value = doc.Strings.GetValue(LayoutMetaSection, key);
            if (!string.IsNullOrWhiteSpace(value)) input.Meta[key] = value.Trim();
        }
        input.Ink = ReadPrintProfile(doc).Name;
        var units = doc.ModelUnitSystem;
        input.Millimetres = units == UnitSystem.Millimeters;
        return input;
    }

    /// <summary>
    /// A generated object's edit stamp: its geometry's CRC and its forsk
    /// strings, so a move, a resize, a new type or a renamed room changes it.
    /// The daylight flag is not part of the model.
    /// </summary>
    private static string EditStamp(RhinoObject obj)
    {
        uint crc = 0;
        try
        {
            crc = obj.Geometry?.DataCRC(0) ?? 0;
        }
        catch (Exception)
        {
            // An object without a CRC still has its strings.
        }
        var sb = new StringBuilder(crc.ToString("x8"));
        var strings = obj.Attributes.GetUserStrings();
        var keys = strings.AllKeys
            .Where(k => k != null && k.StartsWith("forsk:", StringComparison.Ordinal) && k != DaylightStaleKey)
            .OrderBy(k => k, StringComparer.Ordinal);
        foreach (var key in keys)
            sb.Append('|').Append(key).Append('=').Append(strings[key]);
        return sb.ToString();
    }

    /// <summary>
    /// After a successful wall or opening edit, inside the same undo record:
    /// each daylight mesh is marked out of date; a wall edit also hides it
    /// (DaylightMap holds the rule). Returns how many meshes were marked.
    /// </summary>
    internal static int MarkMapAfterEdit(RhinoDoc doc, MapEdit edit)
    {
        if (doc == null || edit == MapEdit.None) return 0;
        var marked = 0;
        foreach (var mesh in AnalysisOverlays(doc))
        {
            var visible = !mesh.IsHidden;
            var stale = mesh.Attributes.GetUserString(DaylightStaleKey) == "1";
            DaylightMap.AfterEdit(edit, ref visible, ref stale);
            var attr = mesh.Attributes.Duplicate();
            attr.SetUserString(DaylightStaleKey, stale ? "1" : null);
            doc.Objects.ModifyAttributes(mesh.Id, attr, true);
            if (!visible && !mesh.IsHidden) doc.Objects.Hide(mesh.Id, false);
            marked++;
        }
        return marked;
    }

    /// <summary>At Print: the model the sheets were drawn from, so the classifier can tell when they are older.</summary>
    private static void StampSheetFingerprint(RhinoDoc doc)
    {
        doc.Strings.SetString(LayoutMetaSection, SheetFingerprintKey, SheetFingerprint.Of(ChipRows(doc)));
    }
}
