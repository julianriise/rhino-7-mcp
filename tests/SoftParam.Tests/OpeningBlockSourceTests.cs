using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Selection S2: an opening is one block instance, so a click picks the whole
/// door or window. Its parts (frame, leaf, sash, glass, sill) live only in the
/// block definition. When Rhino does not take the block, nothing is added:
/// no loose parts a click could pick one at a time. RhinoCommon does not run
/// headless, so this reads the source.
/// </summary>
public class OpeningBlockSourceTests
{
    static readonly string[] TopLevelAdds = { "Objects.AddBrep", "Objects.AddExtrusion", "Objects.AddMesh", "Objects.AddSurface" };

    [Fact]
    public void OpeningParts_AreAddedOnlyInsideTheBlockDefinition()
    {
        var source = File.ReadAllText(Path.Combine(FunctionsDir(), "OpeningBlocks.cs"));
        var found = TopLevelAdds.Where(call => source.Contains(call, StringComparison.Ordinal)).ToArray();
        Assert.True(found.Length == 0, "OpeningBlocks.cs adds a part outside a block: " + string.Join(", ", found));
        Assert.DoesNotContain("AddLooseOpeningParts", source);
        Assert.Contains("InstanceDefinitions.Add", source);
    }

    static string FunctionsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Functions");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("plugin/Functions above " + AppContext.BaseDirectory);
    }
}
