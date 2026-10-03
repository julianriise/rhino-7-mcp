using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3 P4: a facade at section quality. The elevation's own lines are drawn
/// by the hidden-line pass onto facade role layers (outline, line, opening).
/// Over them this draws a heavy ground line, the only cut-weight stroke,
/// across the facade and 1 m past each side, and thin level marks right of
/// the facade: the ground, ±0 at the ground floor's top, eaves and ridge.
/// The heights come from Sections.ModelHeights, the rule the section sheet
/// prints, so a facade and a section never disagree. No elevation dims and
/// no opening chevrons (F5.0 SHOULD, later).
/// </summary>
public partial class RhinoMCPFunctions
{
    /// <summary>What a facade sheet drew besides its lines. Null on the plan and the sections.</summary>
    private sealed class FacadeStats
    {
        public double? GroundZ;
        public JArray Levels = new JArray();
        /// <summary>Level marks Rhino would not write (their text failed).</summary>
        public int LevelsDropped;
    }

    private FacadeStats BakeFacadeMarks(
        RhinoDoc doc, Layer layer, int scale, string view, Transform worldToHld, Vector3d delta,
        IList<RhinoObject> sources, double tol, ref BoundingBox box, ref int index, ref int count)
    {
        var stats = new FacadeStats();
        var pattern = SolidPatternIndex(doc);
        if (doc == null || layer == null || scale < 1 || pattern < 0 || !box.IsValid) return stats;
        var stamp = new Dictionary<string, string> { ["forsk:view"] = view };

        // A facade looks along a world axis with Z up, so a height maps to one sheet Y.
        var clay = BoundingBox.Empty;
        foreach (var obj in sources ?? new List<RhinoObject>())
        {
            var b = obj?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (b.IsValid) clay.Union(b);
        }
        if (!clay.IsValid) return stats;
        var centre = clay.Center;
        double SheetY(double z) => ToSheet(new Point3d(centre.X, centre.Y, z), worldToHld, delta).Y;

        // The roof as the facade sees it: each corner along the sheet, at its height.
        var roof = new List<List<RoomDetect.Pt>>();
        foreach (var obj in sources ?? new List<RhinoObject>())
        {
            if (!string.Equals(GetForskKind(obj), "roof", StringComparison.OrdinalIgnoreCase)) continue;
            roof.Add(RoofCorners(obj.Geometry).Select(p => new RoomDetect.Pt(ToSheet(p, worldToHld, delta).X, p.Z)).ToList());
        }
        var floors = PlanRooms(doc).Where(r => r.Ring != null && r.Ring.Count > 0).Select(r => r.Ring[0].Z);
        // The same heights the section sheet prints (TryBakeSectionLinework).
        var heights = Sections.ModelHeights(HeightSolids(sources), floors, roof);

        var left = box.Min.X;
        var right = box.Max.X;
        var groundLayer = EnsureFacadeLayer(doc, layer, FacadeLines.Ground) ?? layer;
        var levelLayer = EnsureFacadeLayer(doc, layer, FacadeLines.Level) ?? layer;
        if (heights.Ground.HasValue)
        {
            var y = SheetY(heights.Ground.Value);
            var (x0, x1) = Sections.FacadeGround(left, right);
            using (var ground = new LineCurve(new Point3d(x0, y, 0), new Point3d(x1, y, 0)))
            {
                var extra = new SymbolStamp { Extra = new Dictionary<string, string>(stamp) { ["forsk:z"] = Mm(heights.Ground.Value) } };
                if (AddStroke(doc, groundLayer, ground, Sections.GroundPen(PrintProfiles.Active), scale, false, pattern, tol,
                        "ground_line", null, null, null, ref box, ref index, ref count, extra) > 0)
                    stats.GroundZ = heights.Ground.Value;
            }
        }

        var valueHeight = Sections.ValueMm * scale;
        var marks = Sections.PlaceLevels(heights.Levels, SheetY, right, scale,
            text => ModelTextWidth(doc, text, valueHeight) / scale);
        foreach (var mark in marks)
        {
            var extra = new Dictionary<string, string>(stamp)
            {
                ["forsk:level_kind"] = mark.Level.Kind,
                ["forsk:level_z"] = Mm(mark.Level.Z),
                ["forsk:level_value"] = mark.Level.Value.ToString(CultureInfo.InvariantCulture)
            };
            using (var line = new LineCurve(Sheet(mark.LineA), Sheet(mark.LineB)))
                AddStroke(doc, levelLayer, line, Sections.LevelPen(PrintProfiles.Active), scale, false, pattern, tol,
                    "level", "line", null, null, ref box, ref index, ref count, new SymbolStamp { Extra = extra });
            AddSolidTriangle(doc, levelLayer, mark.Triangle, pattern, tol, "level", extra, ref box, ref index, ref count);
            if (!AddSheetText(doc, levelLayer, mark.Level.Text, new Plane(Sheet(mark.TextAt), Vector3d.XAxis, Vector3d.YAxis),
                    valueHeight, scale, "level", extra, ref box, ref index, ref count))
            {
                stats.LevelsDropped++;
                continue;
            }
            stats.Levels.Add(new JObject
            {
                ["kind"] = mark.Level.Kind,
                ["z"] = Math.Round(mark.Level.Z, 3),
                ["value"] = mark.Level.Value,
                ["text"] = mark.Level.Text
            });
        }
        return stats;
    }

    /// <summary>A roof's corners: its vertices, or its box's corners when it is not a solid Rhino can list.</summary>
    private static IEnumerable<Point3d> RoofCorners(GeometryBase geometry)
    {
        var brep = geometry as Brep ?? (geometry as Extrusion)?.ToBrep();
        if (brep != null && brep.Vertices.Count > 0)
            return brep.Vertices.Select(v => v.Location).ToList();
        var bbox = geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
        return bbox.IsValid ? bbox.GetCorners() : new Point3d[0];
    }

    /// <summary>A facade page's record: the ground and the level marks it printed.</summary>
    private static JObject FacadePageRecord(FacadeStats stats)
    {
        return new JObject
        {
            ["ground_z"] = stats?.GroundZ.HasValue == true ? new JValue(Math.Round(stats.GroundZ.Value, 3)) : JValue.CreateNull(),
            ["levels"] = stats?.Levels ?? new JArray(),
            ["levels_dropped"] = stats?.LevelsDropped ?? 0
        };
    }
}
