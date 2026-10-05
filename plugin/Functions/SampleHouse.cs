using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The first-run house, in millimetres: one storey, 14.4 × 8.2 m, drawn at
/// runtime. Interior walls stop at each door so detection can close the gap.
/// A window sits on the wall, because a gap there would open the room and a
/// window does not close one. No A-ROOM curves: rooms_detect names the rooms
/// from the labels.
/// </summary>
public static class SampleHouse
{
    public const double WidthMm = 14400;
    public const double DepthMm = 8200;
    public const double WallMm = 200;
    public const double LabelHeightMm = 200;
    /// <summary>openings_from_layer defaults. The cutter must reach a wall solid.</summary>
    public const double CutterPadMm = 50;
    public const double CutterMinDepthMm = 250;

    public static double GrossM2 => WidthMm * DepthMm / 1000000.0;

    public readonly struct Rect
    {
        public readonly string Name;
        public readonly double X0, Y0, X1, Y1;

        public Rect(string name, double x0, double y0, double x1, double y1)
        {
            Name = name;
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
        }

        public double Area => Math.Abs((X1 - X0) * (Y1 - Y0));
    }

    public readonly struct Room
    {
        public readonly string Name;
        public readonly double X0, Y0, X1, Y1;

        public Room(string name, double x0, double y0, double x1, double y1)
        {
            Name = name;
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
        }

        public double Area => Math.Abs((X1 - X0) * (Y1 - Y0));
        public double Cx => 0.5 * (X0 + X1);
        public double Cy => 0.5 * (Y0 + Y1);
    }

    public readonly struct Label
    {
        public readonly string Text;
        public readonly double X, Y;

        public Label(string text, double x, double y)
        {
            Text = text;
            X = x;
            Y = y;
        }
    }

    /// <summary>Interiors, inside the 200 mm walls. Net is 101.76 m². Gross is 118.08.</summary>
    public static readonly Room[] Rooms =
    {
        new Room("Kitchen", 200, 200, 5800, 3200),
        new Room("Living", 200, 3400, 5800, 8000),
        new Room("Hall", 6000, 200, 10400, 2600),
        new Room("Bathroom", 6000, 2800, 10400, 4800),
        new Room("Bedroom 1", 6000, 5000, 10400, 8000),
        new Room("Bedroom 2", 10600, 200, 14200, 3400),
        new Room("WC", 10600, 3600, 14200, 5200),
        new Room("Storage", 10600, 5400, 14200, 8000)
    };

    public static IReadOnlyList<Label> Labels =>
        Rooms.Select(room => new Label(room.Name, room.Cx, room.Cy)).ToList();

    /// <summary>Closed wall rectangles. Corners overlap; a door is a gap, not a second outline inside one.</summary>
    public static readonly Rect[] Walls =
    {
        new Rect("south-l", 0, 0, 7750, 200),
        new Rect("south-r", 8650, 0, 14400, 200),
        new Rect("north", 0, 8000, 14400, 8200),
        new Rect("west", 0, 0, 200, 8200),
        new Rect("east", 14200, 0, 14400, 8200),
        new Rect("ab-a", 5800, 200, 6000, 950),
        new Rect("ab-b", 5800, 1850, 6000, 8000),
        new Rect("bc-a", 10400, 200, 10600, 950),
        new Rect("bc-b", 10400, 1850, 10600, 6250),
        new Rect("bc-c", 10400, 7150, 10600, 8000),
        new Rect("kl-l", 200, 3200, 2550, 3400),
        new Rect("kl-r", 3450, 3200, 5800, 3400),
        new Rect("hb-l", 6000, 2600, 7750, 2800),
        new Rect("hb-r", 8650, 2600, 10400, 2800),
        new Rect("bb-l", 6000, 4800, 7750, 5000),
        new Rect("bb-r", 8650, 4800, 10400, 5000),
        new Rect("bw-l", 10600, 3400, 11950, 3600),
        new Rect("bw-r", 12850, 3400, 14200, 3600),
        new Rect("ws", 10600, 5200, 14200, 5400)
    };

    /// <summary>Door rectangles fill those gaps. The bake cuts them out of the wall solids.</summary>
    public static readonly Rect[] Doors =
    {
        new Rect("entry", 7750, 0, 8650, 200),
        new Rect("hall-kitchen", 5800, 950, 6000, 1850),
        new Rect("hall-bath", 7750, 2600, 8650, 2800),
        new Rect("bath-bed", 7750, 4800, 8650, 5000),
        new Rect("hall-bed", 10400, 950, 10600, 1850),
        new Rect("bed-wc", 11950, 3400, 12850, 3600),
        new Rect("kitchen-living", 2550, 3200, 3450, 3400),
        new Rect("bed-storage", 10400, 6250, 10600, 7150)
    };

    /// <summary>Windows lie on a wall. They are not gaps.</summary>
    public static readonly Rect[] Windows =
    {
        new Rect("living", 0, 5000, 200, 6200),
        new Rect("kitchen", 0, 1100, 200, 2300),
        new Rect("bed1", 7600, 8000, 8800, 8200),
        new Rect("bed2", 14200, 1200, 14400, 2400),
        new Rect("wc", 14200, 3800, 14400, 5000),
        new Rect("storage", 11800, 8000, 13000, 8200)
    };

    public static List<RoomDetect.Pt> Ring(Rect rect) => new List<RoomDetect.Pt>
    {
        new RoomDetect.Pt(rect.X0, rect.Y0),
        new RoomDetect.Pt(rect.X1, rect.Y0),
        new RoomDetect.Pt(rect.X1, rect.Y1),
        new RoomDetect.Pt(rect.X0, rect.Y1)
    };

    public static RoomDetect.Scene Scene()
    {
        var scene = new RoomDetect.Scene();
        foreach (var wall in Walls)
            scene.Walls.Add(new List<List<RoomDetect.Pt>> { Ring(wall) });
        foreach (var door in Doors)
            scene.Doors.Add(new RoomDetect.Box(door.X0, door.Y0, door.X1, door.Y1));
        return scene;
    }

    /// <summary>The opening cutter openings_from_layer builds from a plan rectangle.</summary>
    public static Rect Cutter(Rect opening)
    {
        var dx = opening.X1 - opening.X0;
        var dy = opening.Y1 - opening.Y0;
        if (dx >= dy)
        {
            var cy = 0.5 * (opening.Y0 + opening.Y1);
            var y0 = dy < CutterMinDepthMm ? cy - CutterMinDepthMm * 0.5 : opening.Y0;
            var y1 = dy < CutterMinDepthMm ? cy + CutterMinDepthMm * 0.5 : opening.Y1;
            return new Rect(opening.Name, opening.X0 - CutterPadMm, y0, opening.X1 + CutterPadMm, y1);
        }
        var cx = 0.5 * (opening.X0 + opening.X1);
        var x0 = dx < CutterMinDepthMm ? cx - CutterMinDepthMm * 0.5 : opening.X0;
        var x1 = dx < CutterMinDepthMm ? cx + CutterMinDepthMm * 0.5 : opening.X1;
        return new Rect(opening.Name, x0, opening.Y0 - CutterPadMm, x1, opening.Y1 + CutterPadMm);
    }

    public static string Receipt() =>
        "Sample house, " + GrossM2.ToString("0.0", CultureInfo.InvariantCulture)
        + " m², one floor. Generate 3D is next.";
}
