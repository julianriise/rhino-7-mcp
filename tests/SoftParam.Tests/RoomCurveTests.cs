using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// rooms_detect keeps one curve per room. A second outline with the same
/// forsk:id is deleted on the next run. The plate is the click; the curve stays.
/// </summary>
public class RoomCurveTests
{
    [Fact]
    public void AnOutlineAndItsMarker_KeepTheOutline()
    {
        var curves = Pair("rd-01", "R");
        var plan = RoomCurves.Plan(curves, new[] { "rd-01" }, new[] { "R" });
        Assert.Equal("outline", plan[0].Keep);
        Assert.Equal(new[] { "marker" }, plan[0].Delete);
    }

    [Fact]
    public void TwoCurvesWithOneId_KeepOne()
    {
        var curves = new[]
        {
            Curve("b", "rd-01", "R", detected: true, marker: false),
            Curve("a", "rd-01", "R", detected: true, marker: false)
        };
        var plan = RoomCurves.Plan(curves, new[] { "rd-01" }, new[] { "R" });
        Assert.Equal("a", plan[0].Keep);
        Assert.Equal(new[] { "b" }, plan[0].Delete);
    }

    [Fact]
    public void NoCurve_IsAnAdd()
    {
        var plan = RoomCurves.Plan(new RoomCurves.Curve[0], new[] { "rd-01" }, new[] { "R" });
        Assert.Null(plan[0].Keep);
        Assert.Empty(plan[0].Delete);
    }

    [Fact]
    public void TheNextRun_ReplacesTheOneCurve()
    {
        var curves = new[] { Curve("outline", "rd-01", "R", detected: true, marker: true) };
        var plan = RoomCurves.Plan(curves, new[] { "rd-01" }, new[] { "R" });
        Assert.Equal("outline", plan[0].Keep);
        Assert.Empty(plan[0].Delete);
    }

    [Fact]
    public void AHandDrawnOutline_LosesItsMarkerCopy_AndALoneMarkerStays()
    {
        var doubled = new[]
        {
            Curve("drawn", "", "R", detected: false, marker: false),
            Curve("marker", "", "R", detected: false, marker: true)
        };
        Assert.Equal(new[] { "marker" }, RoomCurves.Leftovers(doubled, new List<RoomCurves.Action>()));

        var lone = new[] { Curve("marker", "room-01", "R", detected: false, marker: true) };
        Assert.Empty(RoomCurves.Leftovers(lone, new List<RoomCurves.Action>()));

        var stale = new[] { Curve("old", "rd-09", "R", detected: true, marker: false) };
        Assert.Equal(new[] { "old" }, RoomCurves.Leftovers(stale, new List<RoomCurves.Action>()));
    }

    [Fact]
    public void SixteenRooms_EachLoseTheSecondOutline()
    {
        var curves = new List<RoomCurves.Curve>();
        var ids = new List<string>();
        var rings = new List<string>();
        for (var i = 1; i <= 16; i++)
        {
            var id = "rd-" + i.ToString("00");
            var ring = "ring-" + i.ToString("00");
            ids.Add(id);
            rings.Add(ring);
            curves.Add(Curve("outline-" + i, id, ring, detected: true, marker: false));
            curves.Add(Curve("marker-" + i, id, ring, detected: true, marker: true));
        }
        var plans = RoomCurves.Plan(curves, ids, rings);
        Assert.Equal(16, plans.Count);
        Assert.Equal(16, plans.Count(p => p.Keep != null && p.Keep.StartsWith("outline-")));
        Assert.Equal(16, plans.Sum(p => p.Delete.Count));
        Assert.All(plans, p => Assert.StartsWith("marker-", p.Delete[0]));
    }

    [Fact]
    public void RingKey_IgnoresStartDirectionAndTheClosingPoint()
    {
        var ring = new[]
        {
            new RoomDetect.Pt(0, 0), new RoomDetect.Pt(1000, 0),
            new RoomDetect.Pt(1000, 1000), new RoomDetect.Pt(0, 1000)
        };
        var closed = ring.Concat(new[] { ring[0] }).ToList();
        var turned = new[] { ring[2], ring[3], ring[0], ring[1] };
        var reversed = ring.Reverse().ToList();
        var key = RoomCurves.RingKey(ring);
        Assert.Equal(key, RoomCurves.RingKey(closed));
        Assert.Equal(key, RoomCurves.RingKey(turned));
        Assert.Equal(key, RoomCurves.RingKey(reversed));
        Assert.NotEqual(key, RoomCurves.RingKey(new[] { new RoomDetect.Pt(0, 0), new RoomDetect.Pt(500, 0), new RoomDetect.Pt(500, 500) }));
    }

    [Fact]
    public void Detection_ReplacesById_AndThePlateIsTheOnlyClick()
    {
        var detect = Source("RoomsDetect.cs");
        var plates = Source("RoomPlates.cs");
        Assert.Contains("RoomCurves.Plan(", detect);
        Assert.DoesNotContain("BakeRoomMarkers", detect);
        Assert.Contains("Objects.Unlock(", detect);
        Assert.Contains("doc.Objects.Lock(marker.Id, false)", plates);
        Assert.Contains("RoomPlate.Kind", plates);
        Assert.DoesNotContain("AddBrep", detect);
    }

    static RoomCurves.Curve Curve(string id, string forskId, string ring, bool detected, bool marker) =>
        new RoomCurves.Curve { Id = id, ForskId = forskId, Ring = ring, Detected = detected, Marker = marker };

    static RoomCurves.Curve[] Pair(string forskId, string ring) => new[]
    {
        Curve("outline", forskId, ring, detected: true, marker: false),
        Curve("marker", forskId, ring, detected: true, marker: true)
    };

    static string Source(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin", "Functions", file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new DirectoryNotFoundException("plugin/Functions/" + file);
    }
}
