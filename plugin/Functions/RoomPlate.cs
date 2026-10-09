using System.Collections.Generic;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Selection S3: the rule for a room's floor plate, the thin solid a click
/// inside a room picks. It stands on the room's marker, 20 mm up from the
/// slab top, so no face of it lies on the slab facing up (the z-fight 758a589
/// removed) and it stays under the daylight mesh, 50 mm up. Only a room the
/// walls close gets one. Pure geometry, no RhinoCommon, so it tests headless.
/// </summary>
public static class RoomPlate
{
    public const string Kind = "room_plate";
    /// <summary>The plate is extruded up this far from the marker's own curve, which lies on the slab top.</summary>
    public const double ThicknessMm = 20.0;

    /// <summary>
    /// Why the room under this marker gets no plate, or null when the walls
    /// close it: a room found from the walls lies under it. An open region
    /// under it gives its reason (gap 0.9 m without a door). An area drawn by
    /// hand is the user's word and always gets its plate, wherever its
    /// outline runs (usually along the walls' inner faces, Julian 2026-10-09).
    /// </summary>
    public static string Open(List<Pt> marker, RoomDetect.Result walls, bool drawn = false)
    {
        if (marker == null || !RoomDetect.TryInside(new List<List<Pt>> { marker }, out var at))
            return "it has no outline";
        if (drawn) return null;
        foreach (var room in walls.Rooms)
            if (RoomDetect.Contains(room.Ring, at) || RoomDetect.Contains(marker, room.Inside)) return null;
        foreach (var open in walls.Open)
            if (open.Ring != null && (RoomDetect.Contains(open.Ring, at) || RoomDetect.Contains(marker, open.At)))
                return open.Reason;
        return "its walls do not close";
    }
}
