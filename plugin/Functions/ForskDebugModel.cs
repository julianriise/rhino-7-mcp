using System;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

/// <summary>The model half of the debug report: counts from the document, no text.</summary>
public partial class RhinoMCPFunctions
{
    public static void FillDebugModel(RhinoDoc doc, DebugSnapshot snap)
    {
        if (doc == null || snap == null) return;
        try
        {
            ReadModel(doc, snap);
        }
        catch (Exception e)
        {
            snap.ModelError = "model read failed: " + e.Message;
        }
    }

    static void ReadModel(RhinoDoc doc, DebugSnapshot snap)
    {
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var markers = 0;
        var solids = 0;
        var meshes = new List<Mesh>();
        var rings = new List<List<RoomDetect.Pt>>();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (obj?.Attributes == null) continue;
            if (snap.Review == null)
            {
                var rows = FileClassifier.ReviewRows(obj.Attributes.GetUserString(ImportReviewRowsKey));
                if (rows != null && rows.Count > 0) snap.Review = string.Join("\n", rows);
            }
            if (IsExistingUnderlay(doc, obj) || !IsForskGenerated(obj)) continue;
            var kind = GetForskKind(obj) ?? "";
            if (kind.Equals("wall", StringComparison.OrdinalIgnoreCase))
            {
                snap.WallRecords++;
                try
                {
                    var path = WallEdit.Rings(obj.Attributes.GetUserString("forsk:path"));
                    if (path != null) snap.WallRuns += WallJoins.Runs(path, tol)?.Count ?? 0;
                }
                catch (Exception)
                {
                    // The record still counts. Its path did not read as runs.
                }
            }
            else if (kind.Equals("opening_marker", StringComparison.OrdinalIgnoreCase)) markers++;
            else if (kind.Equals("opening", StringComparison.OrdinalIgnoreCase)) solids++;
            else if (kind.Equals("floor", StringComparison.OrdinalIgnoreCase)) snap.Floors++;
            else if (kind.Equals("roof", StringComparison.OrdinalIgnoreCase)) snap.Roofs++;
            else if (kind.Equals("room", StringComparison.OrdinalIgnoreCase))
            {
                var name = obj.Attributes.GetUserString("forsk:room_name");
                if (string.IsNullOrWhiteSpace(name)) name = obj.Name;
                var closed = obj.Geometry is Curve curve ? curve.IsClosed : obj.Geometry is Brep brep && brep.IsSolid;
                snap.Rooms.Add(new DebugRoom
                {
                    Name = name,
                    Area = obj.Attributes.GetUserString("forsk:area"),
                    Closed = closed
                });
                var outline = RoomMarkerOutline(obj);
                rings.Add(outline == null ? null : PathPoints(outline));
            }
            else if (kind.Equals("analysis", StringComparison.OrdinalIgnoreCase) && obj.Geometry is Mesh mesh)
                meshes.Add(mesh);
        }
        snap.Openings = markers > 0 ? markers : solids;
        if (meshes.Count == 0) return;
        snap.Daylight = true;
        var cells = new List<RoomDetect.Pt>();
        foreach (var mesh in meshes)
            foreach (var centre in FaceCentres(mesh))
                cells.Add(centre);
        var counts = ForskDebug.AssignCells(cells, rings, out var unassigned);
        for (var i = 0; i < snap.Rooms.Count && i < counts.Count; i++)
            snap.Rooms[i].Cells = counts[i];
        snap.UnassignedCells = unassigned;
    }

    static IEnumerable<RoomDetect.Pt> FaceCentres(Mesh mesh)
    {
        if (mesh?.Faces == null || mesh.Vertices == null) yield break;
        for (var i = 0; i < mesh.Faces.Count; i++)
        {
            var face = mesh.Faces[i];
            if (face.A < 0 || face.B < 0 || face.C < 0) continue;
            if (face.A >= mesh.Vertices.Count || face.B >= mesh.Vertices.Count || face.C >= mesh.Vertices.Count) continue;
            var a = mesh.Vertices[face.A];
            var b = mesh.Vertices[face.B];
            var c = mesh.Vertices[face.C];
            if (face.IsQuad)
            {
                if (face.D < 0 || face.D >= mesh.Vertices.Count) continue;
                var d = mesh.Vertices[face.D];
                yield return new RoomDetect.Pt((a.X + b.X + c.X + d.X) / 4.0, (a.Y + b.Y + c.Y + d.Y) / 4.0);
            }
            else
                yield return new RoomDetect.Pt((a.X + b.X + c.X) / 3.0, (a.Y + b.Y + c.Y) / 3.0);
        }
    }
}
