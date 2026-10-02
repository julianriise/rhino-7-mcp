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

    [Fact]
    public void MarkersAreHiddenAfterTheBlockLayerIsShown()
    {
        // S2 left markers as normal objects on parent A-OPEN while A-OPEN::Block
        // is a visible child. Shaded view then draws that blue marker in front
        // of the frame. The layer is turned off again after the child is shown,
        // and each marker is object-hidden.
        var blocks = File.ReadAllText(Path.Combine(FunctionsDir(), "OpeningBlocks.cs"));
        var wrapperStart = blocks.IndexOf("Layer EnsureOpeningBlockLayer(RhinoDoc doc)", StringComparison.Ordinal);
        var coreStart = blocks.IndexOf("Layer EnsureOpeningBlockLayerCore(", StringComparison.Ordinal);
        Assert.True(wrapperStart >= 0 && coreStart > wrapperStart, "block layer wrapper calls the core");
        var wrapper = blocks.Substring(wrapperStart, coreStart - wrapperStart);
        var coreCall = wrapper.IndexOf("EnsureOpeningBlockLayerCore(doc)", StringComparison.Ordinal);
        var hideCall = wrapper.IndexOf("HideOpeningMarkers(doc)", StringComparison.Ordinal);
        Assert.True(coreCall >= 0 && hideCall > coreCall, "markers are hidden after the block layer is shown");
        var show = blocks.IndexOf("IsVisible = true", StringComparison.Ordinal);
        var parentOff = blocks.IndexOf("parent.IsVisible = false", StringComparison.Ordinal);
        Assert.True(show >= 0 && parentOff > show, "A-OPEN is turned off after the child is shown");
        Assert.Contains("Objects.Hide", blocks, StringComparison.Ordinal);
        var openings = File.ReadAllText(Path.Combine(FunctionsDir(), "OpeningsFromLayer.cs"));
        Assert.Contains("HideOpeningMarker", openings, StringComparison.Ordinal);
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
