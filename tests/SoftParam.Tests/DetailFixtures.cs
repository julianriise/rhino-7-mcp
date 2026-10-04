using RhinoMCPPlugin.Functions;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// The detail slices' garage: 8000 × 4000 outside, 200 walls 3000 high as
/// four records (south w01, north w02, west w03, east w04), a 400 floor slab
/// under them with its top at 0, and a 900 × 2100 door D01 at sill 0 in the
/// south wall, its centre 2000 from the west outer corner. The window W01
/// (1200 × 1200 at sill 900, centre x 4000 in the north wall) and a 300
/// flat roof on the walls are there when asked for.
/// </summary>
static class DetailFixtures
{
    public const double Tol = 1.0;

    public static List<List<Pt>> Box(double x0, double y0, double x1, double y1) =>
        new() { new() { new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1) } };

    static IfcExport.Wall Wall(string id, List<List<Pt>> rings) =>
        new() { Id = id, Rings = rings, Thickness = 200, Height = 3000, Base = 0 };

    public static IfcExport.Opening Door() => new()
    {
        Id = "o-door", Host = "w01", Kind = "door", Mark = "D01",
        Centre = new Pt(2000, 100), Along = new Pt(1, 0), Width = 900, Sill = 0, Head = 2100
    };

    public static IfcExport.Opening Window() => new()
    {
        Id = "o-window", Host = "w02", Kind = "window", Mark = "W01",
        Centre = new Pt(4000, 3900), Along = new Pt(1, 0), Width = 1200, Sill = 900, Head = 2100
    };

    public static IfcExport.Model Garage(bool window = false, bool roof = false)
    {
        var model = new IfcExport.Model
        {
            Project = "Garage",
            FloorTop = 0,
            Walls =
            {
                Wall("w01", Box(0, 0, 8000, 200)),
                Wall("w02", Box(0, 3800, 8000, 4000)),
                Wall("w03", Box(0, 200, 200, 3800)),
                Wall("w04", Box(7800, 200, 8000, 3800)),
            },
            Openings = { Door() },
            Slabs = { new() { Id = "floor", Rings = Box(0, 0, 8000, 4000), Thickness = 400, Base = -400 } },
        };
        if (window) model.Openings.Add(Window());
        if (roof) model.Roofs.Add(new() { Id = "roof", Rings = Box(0, 0, 8000, 4000), Thickness = 300, Base = 3000 });
        return model;
    }

    public static Details.Facts Facts(IfcExport.Model model, string wall = null, string opening = null) =>
        Details.Resolve(new Details.Record { Id = "DET01", Wall = wall, Opening = opening }, model, Tol);
}
