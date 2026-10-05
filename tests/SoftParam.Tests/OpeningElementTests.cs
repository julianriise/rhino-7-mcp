using System;
using System.IO;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// A resized opening keeps its frame profile and rebuilds the element.
/// Rhino does not run here: the sizes are pure, and the bake is the source.
/// </summary>
public class OpeningElementTests
{
    const double Thickness = 200;
    const double Pad = 50;

    [Fact]
    public void AResizedWindow_KeepsTheFrameProfile_AndFillsTheGlassToTheFrame()
    {
        // Live: width 970, sill 900, head moved from 2100 to 2900.
        Assert.True(OpeningElement.TryLayout(true, 970, 900, 2100, Thickness, Pad, out var before));
        Assert.True(OpeningElement.TryLayout(true, 970, 900, 2900, Thickness, Pad, out var after));

        Assert.Equal(OpeningElement.FrameFaceMm, before.Face, 6);
        Assert.Equal(before.Left.SpanX, after.Left.SpanX, 6);
        Assert.Equal(before.Right.SpanX, after.Right.SpanX, 6);
        Assert.Equal(before.Head.SpanZ, after.Head.SpanZ, 6);
        Assert.Equal(before.Left.SpanY, after.Left.SpanY, 6);
        Assert.Equal(before.Depth, after.Depth, 6);
        Assert.Equal(before.Face, after.Face, 6);
        Assert.True(after.Left.SpanZ > before.Left.SpanZ);

        Assert.Equal(-after.InnerHalf, after.Glass.X0, 6);
        Assert.Equal(after.InnerHalf, after.Glass.X1, 6);
        Assert.Equal(after.Z0 + after.Face, after.Glass.Z0, 6);
        Assert.Equal(after.Z1 - after.Face, after.Glass.Z1, 6);
        Assert.True(after.Glass.SpanZ > before.Glass.SpanZ);
    }

    [Fact]
    public void AResizedDoor_ResizesTheLeaf_AndTheSwingFollows()
    {
        Assert.True(OpeningElement.TryLayout(false, 900, 0, 2100, Thickness, Pad, out var before));
        Assert.True(OpeningElement.TryLayout(false, 1100, 0, 2900, Thickness, Pad, out var after));

        Assert.Equal(before.Face, after.Face, 6);
        Assert.Equal(before.Left.SpanY, after.Left.SpanY, 6);
        Assert.True(after.Leaf.SpanZ > before.Leaf.SpanZ);
        Assert.True(after.Leaf.SpanX > before.Leaf.SpanX);
        Assert.Equal(before.Leaf.Z0, before.Swing.Z0, 6);
        Assert.Equal(before.Leaf.Z1, before.Swing.Z1, 6);
        Assert.Equal(after.Leaf.Z0, after.Swing.Z0, 6);
        Assert.Equal(after.Leaf.Z1, after.Swing.Z1, 6);
        Assert.Equal(after.Leaf.X1, after.Swing.X1, 6);
        Assert.True(after.Swing.SpanZ > before.Swing.SpanZ);
    }

    [Fact]
    public void ResizeMoveAndSwap_RebuildTheElement_InOneUndo()
    {
        var blocks = Read("OpeningBlocks.cs");
        var facade = Read("FacadeOpenings.cs");
        var host = Read("SoftParam.cs");

        Assert.Contains("OpeningElement.TryLayout", blocks, StringComparison.Ordinal);
        Assert.Contains("OpeningElement.GlassSpan", blocks, StringComparison.Ordinal);
        Assert.Contains("OpeningElement.LeafSpan", blocks, StringComparison.Ordinal);
        Assert.Contains("AddHingeLeaves", blocks, StringComparison.Ordinal);
        Assert.Contains("InstanceDefinitions.Add", blocks, StringComparison.Ordinal);
        Assert.Contains("ObjectsOnLayer(doc, \"A-OPEN\")", blocks, StringComparison.Ordinal);
        Assert.Contains("FindByLayer", Read("LayerLookup.cs"), StringComparison.Ordinal);

        var commit = Slice(facade, "JObject CommitOpeningThenRebuild", "JObject HostOpeningReport");
        Assert.Contains("RebuildHostWall", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginUndoRecord", commit, StringComparison.Ordinal);

        var move = Slice(facade, "JObject MoveOpening", "JObject SetOpening");
        var resize = Slice(facade, "JObject SetOpening", "JObject SetOpeningType");
        var swap = Slice(facade, "JObject SetOpeningType", "private HostUndo SnapshotWholeHost");
        Assert.Contains("CommitOpeningThenRebuild", move, StringComparison.Ordinal);
        Assert.Contains("CommitOpeningThenRebuild", resize, StringComparison.Ordinal);
        Assert.Contains("RebuildHostWall", swap, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginUndoRecord", move, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginUndoRecord", resize, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginUndoRecord", swap, StringComparison.Ordinal);

        var rebuild = Slice(host, "JObject RebuildHostWall", "WallSolid ResolveRebuildHost");
        Assert.DoesNotContain("BeginUndoRecord", rebuild, StringComparison.Ordinal);
        var deleteAt = rebuild.IndexOf("DeleteOpeningBlocks", StringComparison.Ordinal);
        var addAt = rebuild.IndexOf("AddOpeningBlock", StringComparison.Ordinal);
        Assert.True(deleteAt >= 0 && addAt > deleteAt);
        Assert.Contains("ReadOpeningBlockId", rebuild, StringComparison.Ordinal);
        Assert.Contains("WriteOpeningBlockId", rebuild, StringComparison.Ordinal);
        Assert.Contains("OpeningLayerObjects", host, StringComparison.Ordinal);
    }

    static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, start);
        return source.Substring(from, to - from);
    }

    static string Read(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Functions", file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new DirectoryNotFoundException(file);
    }
}
