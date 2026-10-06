using System.Linq;
using Newtonsoft.Json.Linq;
using RhinoMCPPlugin.Forsk;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The first-run house: eight named rooms, a door cutter that hits a wall,
/// the empty-file chip, the hint, and the English A3 default.
/// </summary>
public class SampleHouseTests
{
    const double M2 = 1000000.0;

    static double Overlap(SampleHouse.Rect a, SampleHouse.Rect b)
    {
        var w = System.Math.Min(a.X1, b.X1) - System.Math.Max(a.X0, b.X0);
        var h = System.Math.Min(a.Y1, b.Y1) - System.Math.Max(a.Y0, b.Y0);
        if (w <= 0 || h <= 0) return 0;
        return w * h;
    }

    [Fact]
    public void ThePlan_IsOneStorey_Between100And120_WithEightNamedRooms()
    {
        Assert.InRange(SampleHouse.GrossM2, 100, 120);
        Assert.InRange(SampleHouse.Rooms.Sum(room => room.Area) / M2, 100, 120);
        Assert.Equal(8, SampleHouse.Rooms.Length);
        Assert.Equal(
            new[] { "Bathroom", "Bedroom 1", "Bedroom 2", "Hall", "Kitchen", "Living", "Storage", "WC" },
            SampleHouse.Rooms.Select(room => room.Name).OrderBy(name => name, System.StringComparer.Ordinal).ToArray());

        foreach (var wall in SampleHouse.Walls)
        {
            var edge = 2 * (System.Math.Abs(wall.X1 - wall.X0) + System.Math.Abs(wall.Y1 - wall.Y0));
            Assert.True(RoomDetect.IsWallRun(wall.Area, edge), wall.Name);
        }

        var found = RoomDetect.Detect(SampleHouse.Scene());
        Assert.True(found.Open.Count == 0, string.Join("; ", found.Open.Select(open => open.Reason + " @" + open.At.X + "," + open.At.Y)));
        var labels = SampleHouse.Labels
            .Select(label => new RoomDetect.Label(label.Text, SampleHouse.LabelHeightMm, new RoomDetect.Pt(label.X, label.Y)))
            .ToList();
        var named = found.Rooms.Select(room => RoomDetect.Name(labels, room.Ring) + " " + (room.Area / M2).ToString("0.00")).ToArray();
        Assert.True(found.Rooms.Count == 8, found.Slivers + " slivers; " + string.Join(" | ", named));

        var names = found.Rooms.Select(room => RoomDetect.Name(labels, room.Ring)).OrderBy(name => name, System.StringComparer.Ordinal).ToArray();
        Assert.Equal(SampleHouse.Rooms.Select(room => room.Name).OrderBy(name => name, System.StringComparer.Ordinal).ToArray(), names);
        foreach (var room in SampleHouse.Rooms)
        {
            var detected = found.Rooms.Single(hit => RoomDetect.Name(labels, hit.Ring) == room.Name);
            Assert.Equal(room.Area / M2, detected.Area / M2, 2);
        }
    }

    [Fact]
    public void ADoorSitsInAGap_AndItsCutterHitsAWall_AWindowSitsOnOneWall()
    {
        foreach (var door in SampleHouse.Doors)
        {
            Assert.Equal(0, SampleHouse.Walls.Sum(wall => Overlap(door, wall)));
            Assert.Contains(SampleHouse.Walls, wall => Overlap(SampleHouse.Cutter(door), wall) > 0);
        }
        foreach (var window in SampleHouse.Windows)
        {
            var hosts = SampleHouse.Walls.Where(wall => Overlap(window, wall) > 0).ToList();
            Assert.Single(hosts);
            Assert.Equal(window.Area, Overlap(window, hosts[0]));
            Assert.Contains(SampleHouse.Walls, wall => Overlap(SampleHouse.Cutter(window), wall) > 0);
        }
    }

    [Fact]
    public void TheSample_TakesAFileHoldingOnlyThePlanCut()
    {
        // A new file holds Forsk's hidden plan cut; that is not the user's geometry.
        Assert.True(SampleHouse.FileIsEmpty(new string[0]));
        Assert.True(SampleHouse.FileIsEmpty(new[] { ForskPlanCut.TagValue }));
        Assert.False(SampleHouse.FileIsEmpty(new[] { ForskPlanCut.TagValue, null }));
        Assert.False(SampleHouse.FileIsEmpty(new string[] { null }));
    }

    [Fact]
    public void AnEmptyFile_OffersTheSample_AndTheThreeLines_UntilItIsOff()
    {
        var bar = ForskRegistry.Bar(Docs.Facts("empty"));
        Assert.Equal(new[] { "file.import", "file.sample", "file.draw" }, bar.Slots.Select(action => action.Id).ToArray());
        Assert.Equal("Open sample house", ForskRegistry.Find("file.sample").Label);
        Assert.Equal(ForskRole.Planner, ForskRoles.OfAction("file.sample"));
        Assert.Equal("Nothing is in this file yet.", bar.Reason);
        Assert.DoesNotContain(ForskRegistry.Bar(Docs.Facts("plan curves")).Slots, action => action.Id == "file.sample");

        var facts = Docs.Facts("empty");
        var view = WindowView.Build(new DocThread { Serial = 1 }, facts);
        Assert.Equal("This file is empty.", view["thread"]![0]!["text"]!.ToString());
        Assert.Equal(FirstRun.Lines, ((JArray)view["guide"]!["lines"]!).Select(line => line.ToString()).ToArray());
        Assert.Equal(FirstRun.DismissId, view["guide"]!["dismiss"]!.ToString());
        facts.GuideOff = true;
        Assert.Null(WindowView.Build(new DocThread { Serial = 1 }, facts)["guide"]);
        Assert.Null(WindowView.Build(new DocThread { Serial = 1 }, Docs.Facts("house"))["guide"]);
        Assert.False(FirstRun.Show(FileKind.Plan, false));
    }

    /// <summary>
    /// With no Grok key, or no uv, the hint adds the setup line and its Set up
    /// Forsk button: chat and daylight wait for it, the three steps do not.
    /// </summary>
    [Fact]
    public void AnEmptyFileWithNoKeyOrNoUv_AddsTheSetupLineAndButton()
    {
        var lines = new[] { "Open sample house", "Generate 3D", "Print", "Set up chat, daylight and AI detection" };
        var noKey = WindowView.Build(new DocThread { Serial = 1 }, Docs.Facts("empty, no key"));
        Assert.Equal(lines, ((JArray)noKey["guide"]!["lines"]!).Select(line => line.ToString()).ToArray());
        Assert.Equal("forsk.setup", noKey["guide"]!["setup"]!["id"]!.ToString());
        Assert.Equal("Set up Forsk", noKey["guide"]!["setup"]!["label"]!.ToString());

        var facts = Docs.Facts("empty");
        Assert.Null(WindowView.Build(new DocThread { Serial = 1 }, facts)["guide"]!["setup"]);
        facts.ToolsReady = false;
        var noUv = WindowView.Build(new DocThread { Serial = 1 }, facts);
        Assert.Equal(lines, ((JArray)noUv["guide"]!["lines"]!).Select(line => line.ToString()).ToArray());
        Assert.Equal("forsk.setup", noUv["guide"]!["setup"]!["id"]!.ToString());
        // The input carries it: the window polls uv like the key.
        Assert.False(FileClassifier.Read(new DocInput { ToolsReady = false }).ToolsReady);
        Assert.True(FileClassifier.Read(new DocInput()).ToolsReady);

        // The page draws the button and sends the same action as the Settings row.
        var js = System.IO.File.ReadAllText(PageJs());
        Assert.Contains("if (spec.setup) box.appendChild(pill(spec.setup.label, true", js);
        Assert.Contains("sender.send({ kind: 'action', id: spec.setup.id });", js);
    }

    [Fact]
    public void APrintedSheetCounts_ACancelDoesNot_AndThePageDismissesTheHint()
    {
        Assert.True(FirstRun.CountsAsPrinted(ForskReceipt.PrintLine(1, 100, "House.pdf", null, null, "")));
        Assert.False(FirstRun.CountsAsPrinted("Print PDF · cancelled"));
        Assert.False(FirstRun.CountsAsPrinted("Print PDF · error · Document units must be millimetres."));
        Assert.False(FirstRun.CountsAsPrinted(null));

        var js = System.IO.File.ReadAllText(PageJs());
        Assert.Contains("model.guide", js);
        Assert.Contains("guide.dismiss", js);
    }

    [Fact]
    public void NoTemplate_IsTheEnglishA3Sheet()
    {
        Assert.Equal(PrintTemplate.EnglishA3, PrintTemplate.Resolve(null));
        Assert.Equal(PrintTemplate.EnglishA3, PrintTemplate.Resolve(""));
        Assert.Equal(PrintTemplate.EnglishA3, PrintTemplate.Resolve("nope"));
        Assert.Equal(PrintTemplate.EnglishA3, PrintTemplate.Resolve(PrintTemplate.EnglishA3));
        Assert.Equal("A3", PrintTemplate.Stored(null).Name);
        Assert.Equal("A3", PrintTemplate.Stored("nope").Name);
        Assert.Equal(420, PrintTemplate.WidthMm);
        Assert.Equal(297, PrintTemplate.HeightMm);
        Assert.Equal("Sheet no.", TitleBlock.Cells(new TitleBlock.Fields { Number = "A-20-001" }).Single(cell => cell.Key == "number").Caption);
        Assert.Equal("Sample house, 118.1 m², one floor. Generate 3D is next.", SampleHouse.Receipt());
    }

    static string PageJs()
    {
        for (var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = System.IO.Path.Combine(dir.FullName, "plugin", "Forsk", "Page", "window.js");
            if (System.IO.File.Exists(path)) return path;
        }
        throw new System.IO.DirectoryNotFoundException("window.js above " + System.AppContext.BaseDirectory);
    }
}
