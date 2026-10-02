using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// One block definition per opening size. Rhino does not run here: the key,
/// the name, the mirror and the copy-on-write catalog are pure, and the bake
/// is the source. The office count is the plan DXF, not a live 3dm.
/// </summary>
public class OpeningBlockShareTests
{
    const double Pad = 50;

    [Fact]
    public void AMillimetreEitherWay_IsTheSameKey_AndHandIsNotInIt()
    {
        var wide = OpeningBlockShare.Key.From("window", 1200.2, 900.4, 2100.4, 199.6, Pad, "window.side_hung", "in");
        var narrow = OpeningBlockShare.Key.From("WINDOW", 1199.6, 900.4, 2100.4, 200.4, Pad, "window.side_hung", "in");
        Assert.Equal(1200, wide.WidthMm);
        Assert.Equal(1200, wide.HeightMm);
        Assert.Equal(900, wide.SillMm);
        Assert.Equal(200, wide.FrameMm);
        Assert.Equal(wide, narrow);
        Assert.Equal(wide.GetHashCode(), narrow.GetHashCode());
        Assert.Equal(wide.Token, narrow.Token);

        var mid = OpeningBlockShare.Key.From("window", 1200.5, 900.5, 2100.5, 199.5, Pad, "window.side_hung", "in");
        Assert.Equal(1201, mid.WidthMm);
        Assert.Equal(901, mid.SillMm);
        Assert.Equal(1200, mid.HeightMm);
        Assert.Equal(200, mid.FrameMm);

        var facing = OpeningBlockShare.Key.From("window", 1200, 900, 2100, 200, Pad, "window.side_hung", "out");
        Assert.NotEqual(wide, facing);
        Assert.Equal(9, wide.Token.Split('|').Length);
        Assert.DoesNotContain("|L|", wide.Token, StringComparison.Ordinal);
        Assert.DoesNotContain("|R|", wide.Token, StringComparison.Ordinal);
        Assert.StartsWith(OpeningBlockShare.TokenPrefix, wide.Token, StringComparison.Ordinal);

        var door = OpeningBlockShare.Key.From("door", 900, 0, 2100, 200, Pad, "door.hinged_single", "in");
        Assert.Equal("door", door.Kind);
        Assert.Equal(2100, door.HeightMm);
        Assert.Equal("Window 1200x1200", OpeningBlockShare.Readable(wide));
        Assert.Equal("Door 900x2100", OpeningBlockShare.Readable(door));
        Assert.Equal(0, OpeningBlockShare.Key.From("window", -4, 0, 100, -1, 0, null, null).WidthMm);
    }

    [Fact]
    public void Placement_MirrorsHandAlongTheWall_AndLeavesDepthAndHeight()
    {
        var placed = OpeningBlockShare.Placement.On(10, 20, 1, 0, 0, 1, false);
        placed.Map(100, 40, 900, out var x, out var y, out var z);
        Assert.Equal(110, x, 6);
        Assert.Equal(60, y, 6);
        Assert.Equal(900, z, 6);

        var mirrored = OpeningBlockShare.Placement.On(10, 20, 1, 0, 0, 1, true);
        mirrored.Map(100, 40, 900, out var mx, out var my, out var mz);
        Assert.Equal(-90, mx, 6);
        Assert.Equal(60, my, 6);
        Assert.Equal(900, mz, 6);
    }

    [Fact]
    public void AnEdit_RepointsOneInstance_AndPurgeDropsWhatNothingUses()
    {
        var catalog = new OpeningBlockShare.Catalog();
        var a = OpeningBlockShare.Key.From("window", 1200, 900, 2100, 200, Pad, "window.side_hung", "in");
        var b = OpeningBlockShare.Key.From("window", 1200, 900, 2100, 225, Pad, "window.side_hung", "in");
        Assert.Equal("Window 1200x1200", catalog.Place("window-06", a));
        Assert.Equal("Window 1200x1200", catalog.Place("window-07", a));
        Assert.Equal(1, catalog.Definitions);
        Assert.Equal(a, catalog.KeyOf("window-06"));

        var renamed = catalog.Retarget("window-06", b);
        Assert.Contains(" · ", renamed, StringComparison.Ordinal);
        Assert.Contains(OpeningBlockShare.Suffix(b), renamed, StringComparison.Ordinal);
        Assert.Equal(2, catalog.Definitions);
        Assert.Equal(b, catalog.KeyOf("window-06"));
        Assert.Equal(a, catalog.KeyOf("window-07"));

        catalog.Retarget("window-06", a);
        Assert.Equal(2, catalog.Definitions);
        catalog.Purge();
        Assert.Equal(1, catalog.Definitions);
        Assert.Null(catalog.NameOf(b));
        Assert.Equal("Window 1200x1200", catalog.NameOf(a));
    }

    [Fact]
    public void OfficePlan_SharesSixteenWindowDefinitions()
    {
        // The WINDOW block is 100 by 10. Insert X scale is the width, and
        // 10 * Y scale is the wall (PlanDims). Sill 900, head 2100, pad 50,
        // type window.side_hung. Swing stays "in" when the wall's +Y is inward.
        // A facing that opposes +Y is a second definition; the DXF does not say.
        var tags = Read("ForskTags.cs");
        Assert.Contains("public const double WindowSill = 900.0", tags, StringComparison.Ordinal);
        Assert.Contains("public const double WindowHead = 2100.0", tags, StringComparison.Ordinal);
        Assert.Contains("public const double DoorSill = 0.0", tags, StringComparison.Ordinal);
        Assert.Contains("public const double DoorHead = 2100.0", tags, StringComparison.Ordinal);
        var window = OpeningTypes.DefaultRecord("window");
        var door = OpeningTypes.DefaultRecord("door");
        Assert.Equal("window.side_hung", window.TypeId);
        Assert.Equal("in", window.Swing);
        Assert.Equal("door.hinged_single", door.TypeId);

        var (blockW, blockT) = WindowBlockExtents(OfficeRoomsTests.OfficePath);
        Assert.Equal(100, blockW, 6);
        Assert.Equal(10, blockT, 6);
        var entities = Entities(OfficeRoomsTests.OfficePath);
        var inserts = entities.Where(e => e.Layer.Equals("window", StringComparison.OrdinalIgnoreCase) && e.Type == "INSERT").ToList();
        var rects = entities.Where(e => e.Layer.Equals("door", StringComparison.OrdinalIgnoreCase) && e.Type == "LWPOLYLINE").ToList();
        Assert.Equal(63, inserts.Count);
        Assert.Equal(14, rects.Count);

        var windows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var insert in inserts)
        {
            var key = OpeningBlockShare.Key.From(
                "window", blockW * insert.Sx, 900, 2100, blockT * insert.Sy, Pad, window.TypeId, window.Swing);
            windows.Add(key.WidthMm + "x" + key.FrameMm);
        }
        Assert.Equal(new[]
        {
            "430x200", "860x225", "870x200", "900x200", "900x225", "910x200", "920x200", "920x225",
            "930x200", "940x200", "950x200", "950x225", "960x200", "960x225", "970x225", "1000x200"
        }, BySize(windows));

        var drawn = new HashSet<string>(StringComparer.Ordinal);
        var widths = new HashSet<int>();
        foreach (var rect in rects)
        {
            var dx = rect.Pts.Max(p => p.X) - rect.Pts.Min(p => p.X);
            var dy = rect.Pts.Max(p => p.Y) - rect.Pts.Min(p => p.Y);
            var longSide = Math.Max(dx, dy);
            var shortSide = Math.Min(dx, dy);
            var key = OpeningBlockShare.Key.From(
                "door", longSide, 0, 2100, shortSide, Pad, door.TypeId, door.Swing);
            drawn.Add(key.WidthMm + "x" + key.FrameMm);
            widths.Add(key.WidthMm);
        }
        // The drawn short side is the plan gap. The bake's frame is the host
        // wall, so these 8 are the gap pairs, and the 6 widths are the door
        // definitions when each width has one wall thickness.
        Assert.Equal(new[]
        {
            "800x150", "850x100", "850x150", "900x100", "900x200", "950x100", "1000x225", "1050x200"
        }, BySize(drawn));
        Assert.Equal(new[] { 800, 850, 900, 950, 1000, 1050 }, widths.OrderBy(n => n).ToArray());

        var catalog = new OpeningBlockShare.Catalog();
        foreach (var pair in windows)
            catalog.Place("w" + pair, KeyOf("window", pair, window));
        foreach (var pair in drawn)
            catalog.Place("d" + pair, KeyOf("door", pair, door));
        catalog.Purge();
        Assert.Equal(24, catalog.Definitions);
        Assert.Equal(22, windows.Count + widths.Count);
        Assert.Equal("Window 930x1200", catalog.NameOf(KeyOf("window", "930x200", window)));
        var door900 = new[]
        {
            catalog.NameOf(KeyOf("door", "900x100", door)),
            catalog.NameOf(KeyOf("door", "900x200", door))
        };
        Assert.Contains("Door 900x2100", door900);
        Assert.Contains(door900, name => name != null && name.Contains(" · ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBake_SharesTheDefinition_AndUndoStaysTheCallersRecord()
    {
        var blocks = Read("OpeningBlocks.cs");
        var commit = Slice(blocks, "Guid CommitOpeningBlock", "void BindOpeningPartAttributes");
        Assert.Contains("new ObjectAttributes", commit, StringComparison.Ordinal);
        Assert.Contains("forsk:part", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate()", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("forsk:marker_id", commit, StringComparison.Ordinal);
        Assert.Contains("FindOpeningDefinition", commit, StringComparison.Ordinal);
        Assert.Contains("key.Token", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginUndoRecord", commit, StringComparison.Ordinal);

        var add = Slice(blocks, "Guid AddOpeningBlock", "Record DefinitionStyle");
        Assert.Contains("markerName + \"-block\"", add, StringComparison.Ordinal);
        Assert.Contains("OpeningBlockShare.Key.From", add, StringComparison.Ordinal);
        Assert.Contains("HandSign", add, StringComparison.Ordinal);

        var delete = Slice(blocks, "void DeleteOpeningBlocks", "int CollapseOpeningBlocks");
        Assert.Contains("DeleteOpeningDefinitionIfUnused", delete, StringComparison.Ordinal);
        Assert.DoesNotContain("InstanceDefinitions.Delete", delete, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginUndoRecord", delete, StringComparison.Ordinal);

        var collapse = Slice(blocks, "int CollapseOpeningBlocks", "void CopyOpeningString");
        var added = collapse.IndexOf("AddOpeningBlock", StringComparison.Ordinal);
        var removed = collapse.IndexOf("Objects.Delete(oldId", StringComparison.Ordinal);
        Assert.True(added >= 0 && removed > added);
        Assert.DoesNotContain("DeleteOpeningBlocks", collapse, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginUndoRecord", collapse, StringComparison.Ordinal);
        Assert.Contains("TokenPrefix", collapse, StringComparison.Ordinal);

        var host = Read("SoftParam.cs");
        var rebuild = Slice(host, "JObject RebuildHostWall", "WallSolid ResolveRebuildHost");
        var addAt = rebuild.IndexOf("AddOpeningBlock", StringComparison.Ordinal);
        var purgeAt = rebuild.IndexOf("PurgeOpeningBlockDefinitions", StringComparison.Ordinal);
        Assert.True(addAt >= 0 && purgeAt > addAt);
        Assert.DoesNotContain("BeginUndoRecord", rebuild, StringComparison.Ordinal);

        var openings = Read("OpeningsFromLayer.cs");
        var from = openings.IndexOf("PurgeOpeningBlockDefinitions(doc);", StringComparison.Ordinal);
        var redraw = openings.IndexOf("doc.Views.Redraw();", from, StringComparison.Ordinal);
        Assert.True(from >= 0 && redraw > from);

        var facade = Read("FacadeOpenings.cs");
        var track = Slice(facade, "bool TrackDelete", "void UndeletePieces");
        var unset = track.IndexOf("DefinitionIndex = -1", StringComparison.Ordinal);
        var drop = track.IndexOf("DeleteOpeningDefinitionIfUnused", StringComparison.Ordinal);
        var set = track.IndexOf("piece.DefinitionIndex = def", StringComparison.Ordinal);
        Assert.True(unset >= 0 && drop > unset && set > drop);
        Assert.DoesNotContain("BeginUndoRecord", track, StringComparison.Ordinal);

        var roll = Slice(facade, "void RollbackCommittedHost", "JObject DeleteOpeningResult");
        Assert.Contains("DeleteOpeningDefinitionIfUnused", roll, StringComparison.Ordinal);
        Assert.DoesNotContain("InstanceDefinitions.Delete", roll, StringComparison.Ordinal);

        var chat = ReadForsk("ForskChat.cs");
        var bake = Slice(chat, "Opening(\"door\", lines);", "static int? ResultCount");
        var rooms = bake.IndexOf("rooms_detect", StringComparison.Ordinal);
        var folded = bake.IndexOf("CollapseOpeningBlocks", StringComparison.Ordinal);
        Assert.True(rooms >= 0 && folded > rooms);
        Assert.DoesNotContain("BeginUndoRecord", bake, StringComparison.Ordinal);
    }

    static string[] BySize(IEnumerable<string> pairs) =>
        pairs
            .OrderBy(s => int.Parse(s.Split('x')[0], CultureInfo.InvariantCulture))
            .ThenBy(s => int.Parse(s.Split('x')[1], CultureInfo.InvariantCulture))
            .ToArray();

    static OpeningBlockShare.Key KeyOf(string kind, string pair, OpeningTypes.Record style)
    {
        var parts = pair.Split('x');
        var width = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var frame = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var sill = kind == "door" ? 0 : 900;
        return OpeningBlockShare.Key.From(kind, width, sill, sill + (kind == "door" ? 2100 : 1200), frame, Pad, style.TypeId, style.Swing);
    }

    sealed class Ent
    {
        public string Type = "";
        public string Layer = "";
        public double Sx = 1;
        public double Sy = 1;
        public readonly List<(double X, double Y)> Pts = new List<(double, double)>();
    }

    static List<Ent> Entities(string path)
    {
        var list = new List<Ent>();
        Ent current = null;
        string section = null;
        var previous = ("", "");
        foreach (var (code, value) in Pairs(path))
        {
            if (code == "2" && previous == ("0", "SECTION")) section = value;
            previous = (code, value);
            if (section != "ENTITIES") continue;
            if (code == "0")
            {
                if (current != null) list.Add(current);
                current = new Ent { Type = value };
                continue;
            }
            if (current == null) continue;
            var number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
            switch (code)
            {
                case "8": current.Layer = value; break;
                case "10": current.Pts.Add((number, 0)); break;
                case "20":
                    if (current.Pts.Count > 0)
                    {
                        var last = current.Pts[current.Pts.Count - 1];
                        current.Pts[current.Pts.Count - 1] = (last.X, number);
                    }
                    break;
                case "41" when current.Type == "INSERT": current.Sx = number; break;
                case "42" when current.Type == "INSERT": current.Sy = number; break;
            }
        }
        if (current != null) list.Add(current);
        return list;
    }

    static (double Width, double Thick) WindowBlockExtents(string path)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        string section = null;
        var previous = ("", "");
        var wantName = false;
        var capture = false;
        foreach (var (code, value) in Pairs(path))
        {
            if (code == "2" && previous == ("0", "SECTION")) section = value;
            previous = (code, value);
            if (section != "BLOCKS") continue;
            if (code == "0" && value == "BLOCK") { wantName = true; capture = false; continue; }
            if (code == "0" && value == "ENDBLK") { capture = false; wantName = false; continue; }
            if (wantName && code == "2") { capture = value == "WINDOW"; wantName = false; continue; }
            if (!capture) continue;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) continue;
            if (code == "10") { minX = Math.Min(minX, n); maxX = Math.Max(maxX, n); }
            if (code == "20") { minY = Math.Min(minY, n); maxY = Math.Max(maxY, n); }
        }
        return (maxX - minX, maxY - minY);
    }

    static IEnumerable<(string Code, string Value)> Pairs(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i + 1 < lines.Length; i += 2)
            yield return (lines[i].Trim(), lines[i + 1].Trim());
    }

    static string Slice(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, start);
        return source.Substring(from, to - from);
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
        throw new DirectoryNotFoundException(relative);
    }
}
