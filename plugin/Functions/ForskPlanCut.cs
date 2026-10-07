using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The plan cut: floor top plus 1200 mm, normal (0,0,-1), in every model
/// view that shows a plan (parallel, straight down), whatever its name: the
/// view picker's Plan, or Rhino's Top. A layout page and a detail are out even
/// when they are named Top. The stamp is not forsk:generated and the name
/// misses the clear_generated prefixes, so a rebuild leaves the plane.
/// White fill, the black outline, and the hidden widget are the Forsk White
/// mode. A draw conduit is not the substitute until a live check fails.
/// </summary>
public static class ForskPlanCut
{
    public const double AboveFloorMm = 1200;
    /// <summary>Widget size. The mode hides the widget; the size is not the cut.</summary>
    public const double SizeMm = 100000;
    public const string TagKey = "forsk:plan_cut";
    public const string TagValue = "1";
    public const string ObjectName = "forsk-plan-cut";
    public const string LayerName = "Forsk Cut";

    public readonly struct Cut
    {
        public Cut(double z, double nx, double ny, double nz, double originX, double originY)
        {
            Z = z;
            Nx = nx;
            Ny = ny;
            Nz = nz;
            OriginX = originX;
            OriginY = originY;
        }

        public double Z { get; }
        public double Nx { get; }
        public double Ny { get; }
        public double Nz { get; }
        /// <summary>Corner so a +X by -Y rectangle of <see cref="SizeMm"/> covers the origin.</summary>
        public double OriginX { get; }
        public double OriginY { get; }
    }

    public readonly struct ViewSlot
    {
        public ViewSlot(string id, string name, string typeName, bool plan = false)
        {
            Id = id;
            Name = name;
            TypeName = typeName;
            Plan = plan;
        }

        public string Id { get; }
        public string Name { get; }
        public string TypeName { get; }
        /// <summary>A parallel view looking straight down: the view picker's Plan, whatever the viewport is named.</summary>
        public bool Plan { get; }
    }

    /// <summary>Highest floor top, or 0 when the file has no floor, plus 1200 mm.</summary>
    public static Cut Describe(IEnumerable<double> floorTops)
    {
        double? top = null;
        if (floorTops != null)
        {
            foreach (var z in floorTops)
            {
                if (double.IsNaN(z) || double.IsInfinity(z)) continue;
                if (!top.HasValue || z > top.Value) top = z;
            }
        }
        var half = SizeMm / 2.0;
        return new Cut((top ?? 0) + AboveFloorMm, 0, 0, -1, -half, half);
    }

    /// <summary>A model view showing a plan: the cut follows the view, whatever the viewport is named.</summary>
    public static bool Clips(string typeName, bool plan)
    {
        return ForskWhite.AssignsDisplayMode(typeName) && plan;
    }

    public static IReadOnlyList<string> TopIds(IEnumerable<ViewSlot> views)
    {
        var ids = new List<string>();
        if (views == null) return ids;
        foreach (var view in views)
        {
            if (string.IsNullOrEmpty(view.Id)) continue;
            if (!Clips(view.TypeName, view.Plan)) continue;
            if (ids.Contains(view.Id)) continue;
            ids.Add(view.Id);
        }
        return ids;
    }

    /// <summary>Ids to add and ids to drop so the plane clips the wanted list and nothing else.</summary>
    public static void ViewportEdits(
        IEnumerable<string> current,
        IEnumerable<string> wanted,
        out List<string> add,
        out List<string> remove)
    {
        var want = new HashSet<string>(StringComparer.Ordinal);
        if (wanted != null)
        {
            foreach (var id in wanted)
                if (!string.IsNullOrEmpty(id)) want.Add(id);
        }
        var have = new HashSet<string>(StringComparer.Ordinal);
        add = new List<string>();
        remove = new List<string>();
        if (current != null)
        {
            foreach (var id in current)
            {
                if (string.IsNullOrEmpty(id) || !have.Add(id)) continue;
                if (!want.Contains(id)) remove.Add(id);
            }
        }
        foreach (var id in want)
            if (!have.Contains(id)) add.Add(id);
    }

    static readonly string[] ClearKinds =
    {
        "wall", "floor", "roof", "opening", "opening_marker", "room", "room_plate", "analysis"
    };

    static readonly string[] ClearPrefixes =
    {
        "wall-", "floor-", "roof-", "door-", "window-", "room-"
    };

    /// <summary>
    /// True when clear_generated keeps the object on its default kinds and on
    /// the untagged name prefixes. The plane sets neither forsk:generated nor a kind.
    /// </summary>
    public static bool SurvivesClear(string name, string generated, string kind)
    {
        if (generated == "1")
        {
            foreach (var item in ClearKinds)
                if (string.Equals(kind, item, StringComparison.OrdinalIgnoreCase))
                    return false;
        }
        var text = name ?? "";
        foreach (var prefix in ClearPrefixes)
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
