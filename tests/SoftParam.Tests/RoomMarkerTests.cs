using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// F2.6: Make rooms drew each room marker as a planar Brep on the plan Z,
/// the floor slab's top face, so Rendered mode z-fought (grey/white faceted
/// flicker). A room is its boundary curve and tag data; nothing in the room
/// commands may add a surface, mesh or hatch. RhinoCommon geometry does not
/// run headless, so this reads the commands' source.
/// </summary>
public class RoomMarkerTests
{
    static readonly string[] RoomCommands = { "RoomsFromLayer.cs", "RoomsDetect.cs" };

    static readonly string[] SurfaceCalls =
    {
        "CreatePlanarBreps", "AddBrep", "AddSurface", "AddExtrusion", "AddMesh", "AddHatch", "Hatch.Create"
    };

    static string FunctionsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Functions");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("plugin/Functions above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void MakeRooms_AddsNoSurface_OnlyCurves()
    {
        foreach (var file in RoomCommands)
        {
            var source = File.ReadAllText(Path.Combine(FunctionsDir(), file));
            var found = SurfaceCalls.Where(call => source.Contains(call, StringComparison.Ordinal)).ToArray();
            Assert.True(found.Length == 0, file + " adds a surface: " + string.Join(", ", found));
        }
    }
}
