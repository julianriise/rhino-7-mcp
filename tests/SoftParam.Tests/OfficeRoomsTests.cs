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

    sealed class Entity
    {
        public string Type = "";
        public string Layer = "";
        public string Text = "";
        public double Height;
        public readonly List<Pt> Points = new List<Pt>();
        public Pt End;
    }

    static readonly string OfficePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "office_2D.dxf");

    /// <summary>ENTITIES of a DXF: layer, 10/20 points, 11/21 end, text, height.</summary>
    static List<Entity> Entities(string path)
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
                case "1": current.Text = Regex.Replace(value, @"\\U\+([0-9A-Fa-f]{4})",
                    m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()); break;
                case "40": current.Height = number; break;
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
    static List<Pt> OpenRing(List<Pt> points)
    {
        var ring = new List<Pt>();
        foreach (var p in points)
            if (ring.Count == 0 || Math.Abs(p.X - ring[^1].X) > 0.01 || Math.Abs(p.Y - ring[^1].Y) > 0.01)
                ring.Add(p);
        while (ring.Count > 1 && Math.Abs(ring[0].X - ring[^1].X) <= 0.01 && Math.Abs(ring[0].Y - ring[^1].Y) <= 0.01)
            ring.RemoveAt(ring.Count - 1);
        return ring;
    }

    static bool On(Entity e, string layer) => e.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The scene rooms_detect builds after the office bake: walls_from_layer
    /// makes one wall from every ring on the wall layer, and the divider is the
    /// space_divider line. Doors are cut in 3D; the wall rings have no gaps.
    /// </summary>
    static (RoomDetect.Scene Scene, List<RoomDetect.Label> Labels, List<List<Pt>> Rings) Office()
    {
        var entities = Entities(OfficePath);
        var scene = new RoomDetect.Scene();
        var rings = entities
            .Where(e => On(e, "wall") && e.Type == "LWPOLYLINE")
            .Select(e => OpenRing(e.Points))
            .Where(r => r.Count >= 3)
            .ToList();
        scene.Walls.Add(rings);
        foreach (var e in entities.Where(e => On(e, "space_divider") && e.Type == "LINE"))
            scene.Dividers.Add(new List<Pt> { e.Points[0], e.End });
        var labels = entities
            .Where(e => On(e, "label") && e.Type == "TEXT" && e.Points.Count > 0)
            .Select(e => new RoomDetect.Label(e.Text, e.Height, e.Points[0]))
            .ToList();
        return (scene, labels, rings);
    }

    [Fact]
    public void Fixture_IsTheOfficeSmokeDxf()
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(File.ReadAllBytes(OfficePath));
        Assert.Equal(OfficeSha256, Convert.ToHexString(hash).ToLowerInvariant());
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
