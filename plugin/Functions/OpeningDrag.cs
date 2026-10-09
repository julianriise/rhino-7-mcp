using System;
using System.Collections.Generic;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// An opening block moved by Rhino's own Drag, Move or gumball (FS-Z383WAWP):
/// its marker, which the plan symbol, the wall cut, daylight and the schedules
/// read, stayed behind. Each move adds the block's shift to its marker; at the
/// next idle the marker's opening is moved by the sum along its wall.
/// </summary>
public sealed class OpeningDrag
{
    /// <summary>A shift shorter than this is a click, not a move.</summary>
    public const double MinMm = 1.0;

    readonly Dictionary<string, (double X, double Y)> _shift = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _markersMoved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool Pending => _shift.Count > 0 || _markersMoved.Count > 0;

    /// <summary>The block of marker moved from (x0, y0) to (x1, y1), its bounding box centre in plan.</summary>
    public void Moved(string marker, double x0, double y0, double x1, double y1)
    {
        if (string.IsNullOrEmpty(marker)) return;
        _shift.TryGetValue(marker, out var sum);
        _shift[marker] = (sum.X + x1 - x0, sum.Y + y1 - y0);
    }

    /// <summary>The marker moved with its block (both were selected): it is already where the block is.</summary>
    public void MarkerMoved(string marker)
    {
        if (!string.IsNullOrEmpty(marker)) _markersMoved.Add(marker);
    }

    /// <summary>Each marker to move and by how much, once. A move that came back to its start is dropped.</summary>
    public List<(string Marker, double Dx, double Dy)> Take()
    {
        var moves = new List<(string Marker, double Dx, double Dy)>();
        foreach (var pair in _shift)
            if (!_markersMoved.Contains(pair.Key)
                && Math.Sqrt(pair.Value.X * pair.Value.X + pair.Value.Y * pair.Value.Y) >= MinMm)
                moves.Add((pair.Key, pair.Value.X, pair.Value.Y));
        _shift.Clear();
        _markersMoved.Clear();
        return moves;
    }
}
