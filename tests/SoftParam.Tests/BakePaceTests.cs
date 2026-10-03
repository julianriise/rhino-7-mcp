using System.Globalization;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Generate 3D times each phase, adds the wall solids together, and keeps
/// Rhino's redraw off until the pass ends. A slice stays under the 250 ms
/// shimmer freeze. The office file is not opened here.
/// </summary>
public class BakePaceTests
{
    [Fact]
    public void Phases_AreExclusive_AndTheLogNamesEachOne()
    {
        long now = 0;
        var phases = new BakePhases(() => now);
        using (phases.Time(BakePhases.Walls))
        {
            now = 100;
            using (phases.Time(BakePhases.Layers))
                now = 140;
            now = 180;
        }
        using (phases.Time(BakePhases.Openings))
        {
            now = 400;
            using (phases.Time(BakePhases.Attributes))
                now = 450;
        }

        Assert.Equal(140, phases.Milliseconds(BakePhases.Walls));
        Assert.Equal(40, phases.Milliseconds(BakePhases.Layers));
        Assert.Equal(220, phases.Milliseconds(BakePhases.Openings));
        Assert.Equal(50, phases.Milliseconds(BakePhases.Attributes));
        Assert.Equal(0, phases.Milliseconds(BakePhases.Redraws));

        var line = phases.Line();
        Assert.StartsWith("bake · ", line);
        foreach (var name in BakePhases.Names)
            Assert.Contains(name + " ", line);
        Assert.Contains("wall solids 140 ms", line);
        Assert.Contains("layer lookups 40 ms", line);
        Assert.Contains("openings and blocks 220 ms", line);
        Assert.Contains("attribute writes 50 ms", line);
        Assert.EndsWith("total 450 ms", line);
        Assert.Equal(450, phases.Milliseconds(BakePhases.Walls)
            + phases.Milliseconds(BakePhases.Layers)
            + phases.Milliseconds(BakePhases.Openings)
            + phases.Milliseconds(BakePhases.Attributes));
    }

    [Fact]
    public void ASlice_StaysUnderTheShimmerFreeze()
    {
        Assert.InRange(BakePhases.SliceMs, 1, 249);
        Assert.True(BakePhases.SliceMs < 2000);
    }

    [Fact]
    public void Generate3D_HoldsRedraw_BatchesWalls_AndYields()
    {
        var pace = Read("BakePace.cs");
        Assert.Contains("doc.Views.RedrawEnabled = false", pace);
        Assert.Contains("RedrawEnabled = restore", pace);
        Assert.Contains("Building walls\u2026", pace);
        Assert.Contains("Placing windows\u2026", pace);
        Assert.Contains("AddBreps", pace);
        Assert.Contains("RunIteration", pace);
        Assert.Contains("SliceMs", pace);

        var walls = Read("WallsFromLayer.cs");
        Assert.Contains("BakePace.AddBreps", walls);
        Assert.Contains("BakePace.BuildingWalls", walls);

        var openings = Read("OpeningsFromLayer.cs");
        Assert.Contains("BakePace.PlacingWindows", openings);
        Assert.Contains("BakePace.Markers", openings);
        var purge = openings.IndexOf("PurgeOpeningBlockDefinitions(doc);", StringComparison.Ordinal);
        var redraw = openings.IndexOf("doc.Views.Redraw();", purge, StringComparison.Ordinal);
        Assert.True(purge >= 0 && redraw > purge);

        var window = ReadForsk("ForskWindowActions.cs");
        Assert.Contains("BakePace.Begin", window);
        Assert.Contains("BakePace.Hold", window);
        Assert.Contains("BakePace.End", window);

        var undo = ReadForsk("ForskUndo.cs");
        Assert.Contains("BakePhases.Undo", undo);
    }

    static string Read(string file) => ReadUnder(Path.Combine("plugin", "Functions", file));

    static string ReadForsk(string file) => ReadUnder(Path.Combine("plugin", "Forsk", file));

    static string ReadUnder(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(relative);
    }
}
