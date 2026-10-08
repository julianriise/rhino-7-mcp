using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Exterior render (Julian, 2026-10-08): the camera stands outside and looks
/// straight at the building from the north, east, south or west, at eye height
/// and level, like architectural photography. From the north it stands north
/// of the building and looks south at the north facade, as the elevations do.
/// It stands back far enough that the whole facade and the roof fit the frame
/// of a 24 mm lens, with a margin. No RhinoCommon.
/// </summary>
public static class ExteriorCamera
{
    /// <summary>A standing person's eye, above the ground.</summary>
    public const double EyeMm = 1600;
    public const double LensMm = 24;
    /// <summary>Room around the building in the frame: 1.25 is a quarter more than a tight fit.</summary>
    public const double Margin = 1.25;
    /// <summary>A 24 mm lens on the 36 × 24 mm frame: half the width over the lens, half the height over the lens.</summary>
    const double HalfWide = 18.0 / LensMm;
    const double HalfHigh = 12.0 / LensMm;

    public sealed class Shot
    {
        public double EyeX, EyeY, EyeZ;
        public double TargetX, TargetY, TargetZ;
        /// <summary>"from the north".</summary>
        public string From;
        /// <summary>The way the camera stands: north, east, south or west.</summary>
        public string Side;
    }

    /// <summary>
    /// The shot from <paramref name="side"/> (north when null or unknown) of the
    /// building's box: its plan extent and its ground and top heights, in mm.
    /// Null for an empty box.
    /// </summary>
    public static Shot For(double minX, double minY, double maxX, double maxY, double groundZ, double topZ, string side)
    {
        if (!(maxX > minX) || !(maxY > minY)) return null;
        var way = InteriorCamera.Directions.Contains((side ?? "").Trim().ToLowerInvariant()) ? side.Trim().ToLowerInvariant() : "north";
        // The camera stands on this side, so it looks the other way.
        double sx = way == "east" ? 1 : way == "west" ? -1 : 0;
        double sy = way == "north" ? 1 : way == "south" ? -1 : 0;
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        var width = sx != 0 ? maxY - minY : maxX - minX;
        var depth = sx != 0 ? maxX - minX : maxY - minY;
        var eyeZ = groundZ + EyeMm;
        // Far enough back that the facade fits across the frame and its top fits above the eye.
        var back = Math.Max(width / 2 / HalfWide, Math.Max(topZ - eyeZ, 0) / HalfHigh) * Margin;
        var reach = depth / 2 + back;
        return new Shot
        {
            EyeX = cx + sx * reach,
            EyeY = cy + sy * reach,
            EyeZ = eyeZ,
            TargetX = cx,
            TargetY = cy,
            TargetZ = eyeZ,
            From = "from the " + way,
            Side = way
        };
    }

    /// <summary>The named view an exterior render is saved as: "Exterior north".</summary>
    public static string ViewName(string side) => "Exterior " + side;
}
