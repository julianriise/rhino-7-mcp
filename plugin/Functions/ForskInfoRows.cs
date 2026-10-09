using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using RhinoMCPPlugin.Forsk;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The info panel's records (ForskInfo), read for the picked things only: a
/// picked object and the marker it stands for get their forsk:* strings, a
/// wall's height when it has none stored, an opening's host wall by its id,
/// and a room's ceiling height (the walls' top above the room's floor). A
/// file with nothing picked reads nothing extra.
/// </summary>
public partial class RhinoMCPFunctions
{
    private static void ReadPickedInfo(RhinoDoc doc, List<ChipRow> rows)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!row.Selected || !row.Generated) continue;
            if (!string.IsNullOrEmpty(row.Id)) wanted.Add(row.Id);
            if (!string.IsNullOrEmpty(row.Marker)) wanted.Add(row.Marker);
        }
        if (wanted.Count == 0) return;
        double? wallTop = null;
        var wallFoot = 0.0;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Id) || !wanted.Contains(row.Id) || !Guid.TryParse(row.Id, out var id)) continue;
            var obj = doc.Objects.FindId(id);
            if (obj?.Attributes == null) continue;
            var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var strings = obj.Attributes.GetUserStrings();
            foreach (var key in strings.AllKeys)
                if (key != null && key.StartsWith("forsk:", StringComparison.OrdinalIgnoreCase)) info[key] = strings[key];
            var box = obj.Geometry?.GetBoundingBox(true);
            var kind = ForskInfo.KindOf(row);
            if (kind == "wall" && box.HasValue && box.Value.IsValid)
                info[ForskInfo.HeightKey] = InfoMm(box.Value.Max.Z - box.Value.Min.Z);
            if ((kind == "door" || kind == "window") && Guid.TryParse(info.TryGetValue("forsk:host", out var host) ? host : null, out var hostId))
            {
                var wall = doc.Objects.FindId(hostId);
                var name = wall?.Attributes.GetUserString("forsk:id");
                if (!string.IsNullOrEmpty(name)) info[ForskInfo.HostKey] = name;
            }
            if (kind == "room" && box.HasValue && box.Value.IsValid)
            {
                wallTop = wallTop ?? WallTop(doc, rows, out wallFoot);
                if (wallTop.HasValue && wallTop.Value > box.Value.Min.Z)
                {
                    info[ForskInfo.CeilingKey] = InfoMm(wallTop.Value - box.Value.Min.Z);
                    // A ceiling typed in the panel is the walls' height less this.
                    info[ForskInfo.CeilingLiftKey] = InfoMm(box.Value.Min.Z - wallFoot);
                }
            }
            if (kind == "furniture" && TryFurniture(obj, out _, out var frame, out _))
                info[ForskInfo.RotationKey] = frame.Degrees.ToString("0.###", CultureInfo.InvariantCulture);
            row.Info = info;
        }
        // The thing a part stands for is its marker: the marker's record is the one the panel reads.
        foreach (var row in rows)
        {
            if (row.Info != null || string.IsNullOrEmpty(row.Marker)) continue;
            var marker = rows.Find(r => string.Equals(r.Id, row.Marker, StringComparison.OrdinalIgnoreCase));
            if (marker?.Info != null) row.Info = marker.Info;
        }
    }

    /// <summary>
    /// The highest wall top in the file: one storey, so the ceiling every room
    /// has. foot is the lowest wall foot (set_wall height_mm measures from it).
    /// Null with no walls.
    /// </summary>
    private static double? WallTop(RhinoDoc doc, List<ChipRow> rows, out double foot)
    {
        double? top = null;
        double? low = null;
        foreach (var row in rows.Where(r => r.Generated && string.Equals(r.Kind, "wall", StringComparison.OrdinalIgnoreCase)))
        {
            if (!Guid.TryParse(row.Id, out var id)) continue;
            var box = doc.Objects.FindId(id)?.Geometry?.GetBoundingBox(true);
            if (!box.HasValue || !box.Value.IsValid) continue;
            if (!top.HasValue || box.Value.Max.Z > top.Value) top = box.Value.Max.Z;
            if (!low.HasValue || box.Value.Min.Z < low.Value) low = box.Value.Min.Z;
        }
        foot = low ?? 0;
        return top;
    }

    private static string InfoMm(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
}
