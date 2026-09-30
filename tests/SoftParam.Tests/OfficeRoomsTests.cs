using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// F2.5 on the office smoke's own DXF, headless: the same wall rings,
/// space_divider line, and label texts the live bake reads. Detection and
/// naming must give 16 regions, each named by the label inside it, so the
/// office smoke's rooms and name tags cannot drift without a failing test.
/// </summary>
public class OfficeRoomsTests
{
    /// <summary>Same file as the office smoke (OFFICE_DXF_SHA256 in opening_edit_smoke.py).</summary>
    const string OfficeSha256 = "53a6791c04b7602327ccce28653944ad206a0f3eaa7c1629aa7655b754066f93";

    internal sealed class Entity
    {
        public string Type = "";
        public string Layer = "";
        public string Text = "";
        public double Height;
        public double Rotation;
        public double ScaleY = 1;
        public readonly List<Pt> Points = new List<Pt>();
        public Pt End;
    }

    internal static readonly string OfficePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "office_2D.dxf");

    /// <summary>ENTITIES of a DXF: layer, 10/20 points, 11/21 end, text, height, insert rotation and Y scale.</summary>
    internal static List<Entity> Entities(string path)
    {
        var lines = File.ReadAllLines(path);
        var list = new List<Entity>();
        Entity current = null;
        string section = null;
        var previous = ("", "");
        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            var code = lines[i].Trim();
            var value = lines[i + 1].Trim();
            if (code == "2" && previous == ("0", "SECTION")) section = value;
            previous = (code, value);
            if (section != "ENTITIES") continue;
            if (code == "0")
            {
                if (current != null) list.Add(current);
                current = new Entity { Type = value };
                continue;
            }
            if (current == null) continue;
            var number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
            switch (code)
            {
                case "8": current.Layer = value; break;
                case "1": current.Text = value; break;
                case "40": current.Height = number; break;
                case "42": current.ScaleY = number; break;
                case "50": current.Rotation = number; break;
                case "10": current.Points.Add(new Pt(number, 0)); break;
                case "20":
                    if (current.Points.Count > 0)
                        current.Points[current.Points.Count - 1] = new Pt(current.Points[current.Points.Count - 1].X, number);
                    break;
                case "11": current.End = new Pt(number, current.End.Y); break;
                case "21": current.End = new Pt(current.End.X, number); break;
            }
        }
        if (current != null) list.Add(current);
        return list;
    }

    /// <summary>A closed ring without repeated vertices, as the wall path keeps it.</summary>
    internal static List<Pt> OpenRing(List<Pt> points)
    {
        var ring = new List<Pt>();
        foreach (var p in points)
            if (ring.Count == 0 || Math.Abs(p.X - ring[^1].X) > 0.01 || Math.Abs(p.Y - ring[^1].Y) > 0.01)
                ring.Add(p);
        while (ring.Count > 1 && Math.Abs(ring[0].X - ring[^1].X) <= 0.01 && Math.Abs(ring[0].Y - ring[^1].Y) <= 0.01)
            ring.RemoveAt(ring.Count - 1);
        return ring;
    }

    internal static bool On(Entity e, string layer) => e.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The text Rhino's DXF import stores for a DXF string: it parses the string
    /// as RTF and keeps the result. ON_RtfParser::ReadTag takes \U as a control
    /// word and eats the + that ends it, so B\U+00F8ttekott is stored as
    /// B00F8ttekott (the live office smoke printed exactly that, as rd-10).
    /// </summary>
    static string RhinoPlainText(string raw) => Regex.Replace(raw, @"\\[A-Za-z]+[0-9.\-]*[^\\{}]?", "");

    /// <summary>
    /// The office labels as the plugin reads them after Forsk's DXF import:
    /// Rhino's import makes each text object, then dxf_import rewrites it from
    /// the DXF source through DxfText. Nothing here decodes the DXF itself.
    /// </summary>
    static List<RoomDetect.Label> ImportedLabels(List<Entity> entities)
    {
        var texts = entities.Where(e => (e.Type == "TEXT" || e.Type == "MTEXT") && e.Points.Count > 0).ToList();
        var placed = texts
            .Select(e => new DxfText.Placed(e.Layer, e.Points[0].X, e.Points[0].Y, RhinoPlainText(e.Text)))
            .ToList();
        var rewritten = DxfText.Rewrite(DxfText.Read(OfficePath), placed, 1.0);
        return texts
            .Select((e, i) => (Entity: e, Text: rewritten[i].Text))
            .Where(t => On(t.Entity, "label"))
            .Select(t => new RoomDetect.Label(t.Text, t.Entity.Height, t.Entity.Points[0]))
            .ToList();
    }

    /// <summary>
    /// The scene rooms_detect builds after the office bake: walls_from_layer
    /// makes one wall of the outer ring and every ring inside it, the holes in
    /// document order, which is newest first, so the DXF's order reversed. That
    /// order gives the live run's ids. The divider is the space_divider line.
    /// Doors are cut in 3D; the wall rings have no gaps.
    /// </summary>
    internal static (RoomDetect.Scene Scene, List<RoomDetect.Label> Labels, List<List<Pt>> Rings) Office()
    {
        var entities = Entities(OfficePath);
        var scene = new RoomDetect.Scene();
        var rings = entities
            .Where(e => On(e, "wall") && e.Type == "LWPOLYLINE")
            .Select(e => OpenRing(e.Points))
            .Where(r => r.Count >= 3)
            .ToList();
        var outer = rings.OrderByDescending(r => Math.Abs(RoomDetect.Area(r))).First();
        scene.Walls.Add(new[] { outer }.Concat(rings.Where(r => r != outer).Reverse()).ToList());
        foreach (var e in entities.Where(e => On(e, "space_divider") && e.Type == "LINE"))
            scene.Dividers.Add(new List<Pt> { e.Points[0], e.End });
        return (scene, ImportedLabels(entities), rings);
    }

    static double Clearance(double x, double y, List<Pt> ring)
    {
        var best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var dx = ring[i].X - ring[j].X;
            var dy = ring[i].Y - ring[j].Y;
            var t = Math.Max(0, Math.Min(1, ((x - ring[j].X) * dx + (y - ring[j].Y) * dy) / (dx * dx + dy * dy)));
            best = Math.Min(best, Math.Sqrt(Math.Pow(ring[j].X + dx * t - x, 2) + Math.Pow(ring[j].Y + dy * t - y, 2)));
        }
        return best;
    }

    [Fact]
    public void Office_TagPoints_SitWellInsideEveryRoom()
    {
        // The tag sits at the room's interior point. Hall is an L: its centroid
        // lies 200 mm from the inner corner, and its 0.68 m name did not fit.
        var (scene, _, _) = Office();
        foreach (var room in RoomDetect.Detect(scene).Rooms)
        {
            var at = room.Inside;
            var clear = Clearance(at.X, at.Y, room.Ring);
            var hall = RoomDetect.Contains(room.Ring, new Pt(7618.35, 17752.5));
            Assert.True(clear >= (hall ? 1500 : 500), $"tag at {at.X:0},{at.Y:0} is {clear:0} mm from the room edge");
        }
    }

    [Fact]
    public void Office_AfterForskImport_Rd10IsBottekott()
    {
        // The only escaped label. Rhino's import alone stores it as B00F8ttekott.
        var raw = Entities(OfficePath).Single(e => On(e, "label") && e.Text.Contains("\\U+"));
        Assert.Equal("B\\U+00F8ttekott", raw.Text);
        Assert.Equal("B00F8ttekott", RhinoPlainText(raw.Text));

        // The live run's ids, room by room (rd-10 is the room holding that label).
        var (scene, labels, _) = Office();
        var found = RoomDetect.Detect(scene);
        var ids = RoomDetect.Match(found.Rooms, new List<KeyValuePair<string, List<Pt>>>(), "rd-");
        var named = found.Rooms.Select((room, i) => ids[i] + "=" + RoomDetect.Name(labels, room.Ring));
        Assert.Equal(
            "rd-01=Kontor rd-02=Konferanserom rd-03=Wet Room rd-04=Data/arkiv rd-05=WC rd-06=Hall "
            + "rd-07=Open Office rd-08=Rom rd-09=Fax/kopi/printer rd-10=Bøttekott rd-11=WC rd-12=WC "
            + "rd-13=Kontor rd-14=Kontor rd-15=Kontorplasser rd-16=Konferanserom",
            string.Join(" ", named));
        Assert.True(RoomDetect.Contains(found.Rooms[Array.IndexOf(ids, "rd-10")].Ring, raw.Points[0]));
    }

    [Fact]
    public void Fixture_IsTheOfficeSmokeDxf()
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(File.ReadAllBytes(OfficePath));
        Assert.Equal(OfficeSha256, Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>
    /// walls_from_layer keeps a slender lone outline as a wall, and reads an
    /// outline inside a room face as a wall run standing in that room. The
    /// office has neither: every ring but the outer face is a room face
    /// directly inside it, so its bake is the one wall band it always was.
    /// </summary>
    [Fact]
    public void Office_HasNoWallRunStandingFree()
    {
        var rings = Office().Rings;
        var outer = rings.OrderByDescending(r => Math.Abs(RoomDetect.Area(r))).First();
        foreach (var ring in rings.Where(r => r != outer))
        {
            Assert.True(RoomDetect.Contains(outer, ring[0]));
            Assert.DoesNotContain(rings, other => other != outer && other != ring && RoomDetect.Contains(other, ring[0]));
        }
    }

    [Fact]
    public void Office_SixteenRegions_EachNamedByTheLabelInsideIt()
    {
        var (scene, labels, _) = Office();
        var found = RoomDetect.Detect(scene);

        // The 15 labelled rooms, plus the corridor split in two by the divider.
        // The live office smoke reports the same: regions 16 area 399.5 open 0.
        Assert.Equal(16, found.Rooms.Count);
        Assert.Empty(found.Open);
        Assert.Equal(0, found.Slivers);
        Assert.Equal(399.5, Math.Round(found.Rooms.Sum(r => r.Area) / 1e6, 1));

        // Every label sits in exactly one room, and every room holds one label.
        foreach (var label in labels)
            Assert.Single(found.Rooms, room => RoomDetect.Contains(room.Ring, label.At));

        // Each room is named by its label, except the corridor half whose only
        // label is Brannskap: a cupboard does not name a 92.6 m² room, so Rom.
        var names = new List<string>();
        foreach (var room in found.Rooms)
        {
            var inside = labels.Single(label => RoomDetect.Contains(room.Ring, label.At));
            var name = RoomDetect.Name(labels, room.Ring);
            var expected = inside.Text == "Brannskap" ? RoomDetect.DefaultRoomName : inside.Text;
            Assert.True(expected == name, $"room at {room.Inside.X:0},{room.Inside.Y:0} holds {inside.Text}, named {name}");
            names.Add(name);
        }
        names.Sort(StringComparer.Ordinal);
        Assert.Equal(new[]
        {
            "Bøttekott", "Data/arkiv", "Fax/kopi/printer", "Hall", "Konferanserom", "Konferanserom",
            "Kontor", "Kontor", "Kontor", "Kontorplasser", "Open Office", "Rom", "WC", "WC", "WC", "Wet Room"
        }, names.ToArray());
    }

    /// <summary>
    /// rooms_detect as the handler runs it: a marker per outline (detected
    /// rings, then the ones drawn by hand), each with its record, and the tag
    /// the plan reads back from the marker's stamps with no labels in reach.
    /// </summary>
    internal static List<(RoomDetect.Tag Reported, string Name, Pt At, string Untagged)> ReportedAndTagged(
        RoomDetect.Scene scene, List<RoomDetect.Label> labels)
    {
        var found = RoomDetect.Detect(scene);
        var ids = RoomDetect.Match(found.Rooms, new List<KeyValuePair<string, List<Pt>>>(), "rd-");
        var names = found.Rooms.Select(room => RoomDetect.Name(labels, room.Ring)).ToArray();
        var rows = new List<(RoomDetect.Tag, string, Pt, string)>();
        var outlines = found.Rooms.Select(room => room.Ring).Concat(scene.Keep).ToList();
        for (var i = 0; i < outlines.Count; i++)
        {
            var outline = outlines[i];
            var tag = RoomDetect.Report(outline, "room-" + (i + 1).ToString("00"), Math.Abs(RoomDetect.Area(outline)),
                found.Rooms, ids, names, labels);
            Assert.NotNull(tag);
            Assert.True(RoomDetect.TryTag(tag.Name, RoomDetect.StampAt(outline, tag.At), tag.Area,
                new List<RoomDetect.Label>(), outline, out var name, out var at, out var untagged));
            rows.Add((tag, name, at, untagged));
        }
        return rows;
    }

    [Fact]
    public void Office_PlanTags_ShowWhatRoomsDetectReported()
    {
        // All 16 rooms are tagged with the name and at the point rooms_detect
        // reported, the two corridor halves along the space divider included.
        // Labels read at print do not rename a room; no wall is probed.
        var (scene, labels, _) = Office();
        var rows = ReportedAndTagged(scene, labels);
        Assert.Equal(16, rows.Count);
        foreach (var row in rows)
        {
            Assert.True(row.Reported.Detected);
            Assert.Equal(row.Reported.Name, row.Name);
            Assert.Equal(row.Reported.At.X, row.At.X, 6);
            Assert.Equal(row.Reported.At.Y, row.At.Y, 6);
            Assert.Null(row.Untagged);
        }
        Assert.Equal(new[] { "Fax/kopi/printer", "Rom" }, rows
            .Where(row => row.Reported.Id == "rd-08" || row.Reported.Id == "rd-09")
            .Select(row => row.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // No office room has a wall standing in it, so each reported point is
        // the one print worked out from the outline alone before F2.6, to a
        // thousandth of a millimetre.
        foreach (var room in RoomDetect.Detect(scene).Rooms)
        {
            Assert.Equal(0, room.Holes);
            Assert.True(RoomDetect.TryInside(new List<List<Pt>> { room.Ring }, out var before));
            Assert.Equal(before.X, room.Inside.X, 3);
            Assert.Equal(before.Y, room.Inside.Y, 3);
        }
    }

    [Fact]
    public void Office_AnOutlineDrawnByHand_IsReportedAndTaggedAsDrawn()
    {
        // The Hall drawn by hand on A-ROOM: detection leaves that region to it.
        var (scene, labels, _) = Office();
        var hall = RoomDetect.Detect(scene).Rooms.Single(room => RoomDetect.Name(labels, room.Ring) == "Hall");
        scene.Keep.Add(hall.Ring);

        var rows = ReportedAndTagged(scene, labels);
        Assert.Equal(16, rows.Count);
        var drawn = Assert.Single(rows, row => !row.Reported.Detected);
        Assert.Equal("room-16", drawn.Reported.Id);
        Assert.Equal("Hall", drawn.Name);
        Assert.Equal(drawn.Reported.At.X, drawn.At.X, 6);
        Assert.Equal(drawn.Reported.At.Y, drawn.At.Y, 6);
        Assert.True(RoomDetect.Contains(hall.Ring, drawn.At));
        Assert.DoesNotContain(rows, row => row.Reported.Detected && row.Name == "Hall");
    }

    /// <summary>The DXF's door rectangles and window inserts, as the bake makes openings of them.</summary>
    static List<Schedules.Opening> OfficeOpenings()
    {
        var openings = new List<Schedules.Opening>();
        foreach (var e in Entities(OfficePath))
        {
            var door = On(e, "door") && e.Type == "LWPOLYLINE";
            var window = On(e, "window") && e.Type == "INSERT";
            if (!door && !window) continue;
            Assert.True(OpeningTypes.TryRead(door ? "door" : "window", null, null, null, out var record, out _));
            openings.Add(new Schedules.Opening
            {
                Id = openings.Count.ToString(CultureInfo.InvariantCulture),
                Record = record,
                X = e.Points.Average(p => p.X),
                Y = e.Points.Average(p => p.Y),
                Width = door ? e.Points.Max(p => p.X) - e.Points.Min(p => p.X) : 1200,
                Sill = door ? 0 : 900,
                Head = 2100
            });
        }
        return openings;
    }

    [Fact]
    public void Office_Schedules_MatchTheModel()
    {
        // The office smoke's model: 14 doors, 63 windows, the 16 detected rooms.
        var openings = OfficeOpenings();
        var next = new Dictionary<string, int>();
        var marks = Schedules.AssignMarks(openings, next);
        for (var i = 0; i < openings.Count; i++) openings[i].Mark = marks[i];
        var doors = openings.Where(o => o.Record.Kind == "door").ToList();
        var windows = openings.Where(o => o.Record.Kind == "window").ToList();
        Assert.Equal(14, doors.Count);
        Assert.Equal(63, windows.Count);
        Assert.Equal(Enumerable.Range(1, 14).Select(n => Schedules.Format("D", n)), Schedules.DoorTable(doors).Ids);
        Assert.Equal(Enumerable.Range(1, 63).Select(n => Schedules.Format("V", n)), Schedules.WindowTable(windows).Ids);

        // The smoke deletes two windows and adds one back: 62 rows, no mark moves or repeats.
        var kept = windows.Where(w => w.Mark != "V10" && w.Mark != "V20").ToList();
        kept.Add(new Schedules.Opening { Id = "new", Record = windows[0].Record, X = 0, Y = 0, Width = 1200, Sill = 900, Head = 2100 });
        var again = Schedules.AssignMarks(kept.Concat(doors).ToList(), next);
        for (var i = 0; i < kept.Count - 1; i++) Assert.Equal(kept[i].Mark, again[i]);
        Assert.Equal("V64", again[kept.Count - 1]);
        Assert.Equal(62, again.Take(kept.Count).Distinct().Count());

        // Rooms: every detected room once, named and sized as its plan tag.
        var (scene, labels, _) = Office();
        var found = RoomDetect.Detect(scene);
        var ids = RoomDetect.Match(found.Rooms, new List<KeyValuePair<string, List<Pt>>>(), "rd-");
        var rooms = found.Rooms.Select((room, i) => new Schedules.Room
        {
            Id = ids[i],
            Name = RoomDetect.Name(labels, room.Ring),
            AreaMm2 = room.Area
        }).ToList();
        var table = Schedules.RoomTable(rooms);
        Assert.Equal(16, table.Rows.Count);
        foreach (var room in rooms)
        {
            var row = table.Rows[table.Ids.IndexOf(room.Id)];
            Assert.Equal(room.Name, row[0]);
            Assert.Equal(OpeningTypes.RoomTag(room.AreaMm2), "ca. " + row[1]);
        }
        Assert.Equal(OpeningTypes.AreaText(found.Rooms.Sum(room => room.Area)), table.Total[1]);
    }

    /// <summary>
    /// D08, the door from the Hall into Wet Room (1.31 x 1.10 m). Wet Room's
    /// name overflows its room (2.02 m wide at 1:125, 3.23 m at 1:200, as the
    /// live office smoke measured it) and covers the door and the Hall beside
    /// it. The live run found no clear spot for D08 on either sheet. The mark
    /// starts on the Wet Room side, as it did live.
    /// </summary>
    [Theory]
    [InlineData(125, 2020.0)]
    [InlineData(200, 3230.0)]
    public void Office_D08_FindsAClearSpotBesideWetRoom(int scale, double nameWidth)
    {
        var (scene, labels, _) = Office();
        var wet = RoomDetect.Detect(scene).Rooms.Single(room => RoomDetect.Name(labels, room.Ring) == "Wet Room");
        var tagAt = wet.Inside;
        var cap = 2.5 * scale;
        var name = new RoomDetect.Box(tagAt.X - nameWidth / 2, tagAt.Y - 0.625 * cap, tagAt.X + nameWidth / 2, tagAt.Y + 0.625 * cap);

        var door = Entities(OfficePath).Single(e => On(e, "door") && e.Type == "LWPOLYLINE"
            && e.Points.All(p => p.X >= 9859 && p.X <= 9961 && p.Y >= 18934 && p.Y <= 19786));
        var markCap = Schedules.MarkMm * scale;
        var spot = new Schedules.MarkSpot
        {
            At = new Pt(door.Points.Average(p => p.X), door.Points.Average(p => p.Y)),
            Along = new Pt(0, 1),
            Out = new Pt(1, 0),
            HalfThick = 50,
            HalfWidth = 425,
            Hx = 1.1 * markCap,
            Hy = 0.6 * markCap,
            Gap = scale
        };
        Assert.True(Schedules.PlaceMark(spot, new[] { name }, scene.Walls, out var at), $"no clear spot at 1:{scale}");
        var box = Schedules.MarkBox(at, spot.Hx, spot.Hy);
        Assert.False(Schedules.Overlaps(box, name, spot.Gap), "D08 on Wet Room");
        Assert.False(Schedules.OnWalls(box, scene.Walls), "D08 on a wall");
        Assert.True(at.X < 9860, "on the Hall side of the door");
        Assert.True(Math.Abs(at.Y - spot.At.Y) <= spot.HalfWidth, "beside its opening");
    }

    [Fact]
    public void Office_TheDividerSplitsTheCorridor_TheHalvesKeepTheirOwnLabels()
    {
        var (scene, labels, rings) = Office();
        var found = RoomDetect.Detect(scene);
        // The DXF's corridor ring is the one holding the Brannskap label.
        var brannskap = labels.Single(l => l.Text == "Brannskap").At;
        var corridor = rings
            .Where(r => RoomDetect.Contains(r, brannskap))
            .OrderBy(r => Math.Abs(RoomDetect.Area(r)))
            .First();
        var halves = found.Rooms.Where(room => RoomDetect.Contains(corridor, room.Inside)).ToList();
        Assert.Equal(2, halves.Count);
        Assert.Equal(new[] { "Fax/kopi/printer", "Rom" },
            halves.Select(room => RoomDetect.Name(labels, room.Ring)).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // Without the divider it is one room, and the printer label names it.
        scene.Dividers.Clear();
        var whole = RoomDetect.Detect(scene).Rooms.Where(room => RoomDetect.Contains(corridor, room.Inside)).ToList();
        Assert.Single(whole);
        Assert.Equal("Fax/kopi/printer", RoomDetect.Name(labels, whole[0].Ring));
    }
}
