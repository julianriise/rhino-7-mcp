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
        var walls = new List<(ChipRow Row, List<List<RoomDetect.Pt>> Rings)>();
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
            var row = new ChipRow
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
                Runs = wallRings == null ? 0 : WallJoins.Runs(wallRings, Math.Max(doc.ModelAbsoluteTolerance, 1.0)).Count,
                Marker = obj.Attributes.GetUserString("forsk:marker") ?? obj.Attributes.GetUserString("forsk:marker_id"),
                ForskId = obj.Attributes.GetUserString("forsk:id"),
                Thickness = obj.Attributes.GetUserString("forsk:thickness"),
                Mark = obj.Attributes.GetUserString(Schedules.MarkKey),
                Width = obj.Attributes.GetUserString("forsk:width"),
                Sill = obj.Attributes.GetUserString("forsk:sill"),
                Head = obj.Attributes.GetUserString("forsk:head"),
                Part = obj.Attributes.GetUserString("forsk:part"),
                Group = GroupKey(obj),
                Risers = obj.Attributes.GetUserString(Stairs.RisersKey),
                RiserMax = obj.Attributes.GetUserString(Stairs.RiserMaxKey),
                Going = obj.Attributes.GetUserString(Stairs.GoingKey)
            };
            rows.Add(row);
            if (wallRings != null && !row.Existing) walls.Add((row, wallRings));
        }
        // An opening's block carries no mark; its marker does once Print has scheduled it.
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.Mark) || string.IsNullOrEmpty(row.Marker)) continue;
            var marker = rows.Find(r => r.Id == row.Marker);
            if (marker != null) row.Mark = marker.Mark;
        }
        NameSelectedRuns(walls, Math.Max(doc.ModelAbsoluteTolerance, 1.0));
        return rows;
    }

    /// <summary>Sorted Rhino group indexes, comma-separated. Null when the object is in none.</summary>
    private static string GroupKey(RhinoObject obj)
    {
        var groups = obj?.GetGroupList();
        if (groups == null || groups.Length == 0) return null;
        Array.Sort(groups);
        var text = new string[groups.Length];
        for (var i = 0; i < groups.Length; i++)
            text[i] = groups[i].ToString();
        return string.Join(",", text);
    }

    /// <summary>
    /// Each selected wall of one run gets its name and first way in the join
    /// graph (F2): the north wall, or the wall at (x, y). The graph is read
    /// for the selected walls' clusters only.
    /// </summary>
    private static void NameSelectedRuns(List<(ChipRow Row, List<List<RoomDetect.Pt>> Rings)> walls, double tol)
    {
        if (!walls.Exists(w => w.Row.Selected && w.Row.Runs == 1)) return;
        var records = walls.ConvertAll(w => w.Rings);
        for (var i = 0; i < walls.Count; i++)
        {
            var row = walls[i].Row;
            if (!row.Selected || row.Runs != 1) continue;
            var graph = WallJoins.Build(records, WallJoins.ClusterOf(records, i, tol), tol);
            var run = graph == null ? -1 : WallJoins.RunIn(graph, records[i]);
            if (run < 0) continue;
            row.RunName = graph.Names[run];
            row.RunToward = WallJoins.Toward(graph, run);
        }
    }

    /// <summary>The document for the classifier. The window adds the key, the listener and Undo.</summary>
    public static DocInput ReadDocInput(RhinoDoc doc)
    {
        var input = new DocInput { Rows = ChipRows(doc) };
        if (doc == null) return input;
        input.StoredFingerprint = doc.Strings.GetValue(LayoutMetaSection, SheetFingerprintKey);
        input.UserDims = doc.Strings.GetValue(UserDims.Section, UserDims.Entry);
        input.Layouts = MatchingForskPages(doc, null).Count;
        foreach (var def in ReadSectionDefs(doc))
            if (!string.IsNullOrEmpty(def.Letter)) input.SectionLetters.Add(def.Letter);
        foreach (var key in new[] { "project", "client", "address", "revision", "date", "scale_label" })
        {
            var value = doc.Strings.GetValue(LayoutMetaSection, key);
            if (!string.IsNullOrWhiteSpace(value)) input.Meta[key] = value.Trim();
        }
        input.PrintPages = doc.Strings.GetValue(SheetSet.MetaSection, SheetSet.MetaEntry);
        input.PrintScale = KnownPrintScale(doc);
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
        doc.Strings.SetString(LayoutMetaSection, SheetFingerprintKey,
            SheetFingerprint.Of(ChipRows(doc), doc.Strings.GetValue(UserDims.Section, UserDims.Entry)));
    }
}
