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
    public void WindowAndDoorLayers_StayOn_MarkersStayHidden()
    {
        // Frames, leaves and glass are the block on A-OPEN::Block. A hidden
        // parent hides that child, so A-OPEN is created on and Generate turns
        // it on. Markers stay object-hidden. Print still skips the marker
        // layer in clay and keeps the A-OPEN pen.
        var blocks = File.ReadAllText(Path.Combine(FunctionsDir(), "OpeningBlocks.cs"));
        var lookup = File.ReadAllText(Path.Combine(FunctionsDir(), "LayerLookup.cs"));
        var hiddenStart = lookup.IndexOf("bool LayerHiddenByDefault", StringComparison.Ordinal);
        var hiddenEnd = lookup.IndexOf("private Layer EnsureLayer", StringComparison.Ordinal);
        Assert.True(hiddenStart >= 0 && hiddenEnd > hiddenStart);
        var hidden = lookup.Substring(hiddenStart, hiddenEnd - hiddenStart);
        Assert.Contains("A-ROOF", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("A-OPEN", hidden, StringComparison.Ordinal);

        var wrapperStart = blocks.IndexOf("Layer EnsureOpeningBlockLayer(RhinoDoc doc)", StringComparison.Ordinal);
        var coreStart = blocks.IndexOf("Layer EnsureOpeningBlockLayerCore(", StringComparison.Ordinal);
        Assert.True(wrapperStart >= 0 && coreStart > wrapperStart);
        var wrapper = blocks.Substring(wrapperStart, coreStart - wrapperStart);
        Assert.True(
            wrapper.IndexOf("HideOpeningMarkers(doc)", StringComparison.Ordinal) >
            wrapper.IndexOf("EnsureOpeningBlockLayerCore(doc)", StringComparison.Ordinal));

        var hideStart = blocks.IndexOf("internal static void HideOpeningMarkers", StringComparison.Ordinal);
        var core = blocks.Substring(coreStart, hideStart - coreStart);
        Assert.Contains("ShowLayer(doc, parent)", core, StringComparison.Ordinal);
        Assert.Contains("IsVisible = true", core, StringComparison.Ordinal);

        var hideEnd = blocks.IndexOf("private static void ShowLayer", StringComparison.Ordinal);
        var hide = blocks.Substring(hideStart, hideEnd - hideStart);
        Assert.Contains("Objects.Hide", hide, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible", hide, StringComparison.Ordinal);

        var openings = File.ReadAllText(Path.Combine(FunctionsDir(), "OpeningsFromLayer.cs"));
        var genStart = openings.IndexOf("OpeningsFromLayer(JObject parameters)", StringComparison.Ordinal);
        var genEnd = openings.IndexOf("private sealed class WallSolid", StringComparison.Ordinal);
        var gen = openings.Substring(genStart, genEnd - genStart);
        var made = gen.IndexOf("EnsureLayer(doc, \"A-OPEN\"", StringComparison.Ordinal);
        var shown = gen.IndexOf("EnsureOpeningBlockLayer(doc)", StringComparison.Ordinal);
        Assert.True(made >= 0 && shown > made);
        Assert.Contains("HideOpeningMarker", openings, StringComparison.Ordinal);

        var layout = File.ReadAllText(Path.Combine(FunctionsDir(), "LayoutPack.cs"));
        Assert.Contains("layer.Name.Equals(\"A-OPEN\", StringComparison.OrdinalIgnoreCase)", layout, StringComparison.Ordinal);
        var ink = File.ReadAllText(Path.Combine(FunctionsDir(), "PrintInk.cs"));
        Assert.Contains("[\"A-OPEN\"] = new Spec(70, 70, 70, 0.18)", ink, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrameResolvesToItsMarkerWithFindId()
    {
        // Objects.Find misses a hidden marker, so a selected frame came back
        // as itself and move said "Not an opening marker."
        var blocks = File.ReadAllText(Path.Combine(FunctionsDir(), "OpeningBlocks.cs"));
        var start = blocks.IndexOf("RhinoObject ResolveOpeningHandle", StringComparison.Ordinal);
        var end = blocks.IndexOf("int OpeningBlockDefIndex", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "resolve method is present");
        var body = blocks.Substring(start, end - start);
        Assert.Contains("OpeningResolve.MarkerOf", body, StringComparison.Ordinal);
        Assert.Contains("Objects.FindId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Objects.Find(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AHiddenMarkerIsShownBeforeReplace()
    {
        // Objects.Replace(Guid, Brep) misses an object-hidden marker, so a
        // selected frame resolved and then move threw "Opening marker not found."
        var blocks = File.ReadAllText(Path.Combine(FunctionsDir(), "OpeningBlocks.cs"));
        var start = blocks.IndexOf("bool ReplaceOpeningMarker", StringComparison.Ordinal);
        var end = blocks.IndexOf("private Guid AddOpeningBlock", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "replace helper is present");
        var body = blocks.Substring(start, end - start);
        Assert.True(
            body.IndexOf("Objects.Show", StringComparison.Ordinal) >= 0
            && body.IndexOf("Objects.Show", StringComparison.Ordinal) < body.IndexOf("Objects.Replace", StringComparison.Ordinal),
            "show the marker before replace");
        Assert.True(
            body.IndexOf("HideOpeningMarker", StringComparison.Ordinal) > body.IndexOf("Objects.Replace", StringComparison.Ordinal),
            "hide the marker after replace");

        foreach (var file in new[] { "FacadeOpenings.cs", "WallEditCommands.cs", "SoftParam.cs" })
        {
            var source = File.ReadAllText(Path.Combine(FunctionsDir(), file));
            Assert.Contains("ReplaceOpeningMarker", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Objects.Replace(rec.MarkerId", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Objects.Replace(item.Marker.Id", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Objects.Replace(id, copy)", source, StringComparison.Ordinal);
        }
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
