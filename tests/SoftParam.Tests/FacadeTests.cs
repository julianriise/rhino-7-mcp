using System;
using System.IO;
using RhinoMCPPlugin.Functions;
using Xunit;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace SoftParam.Tests;

/// <summary>
/// v3 P4: the four facades at section quality: a heavy ground line, the
/// level marks (±0, gesims, møne) from the same heights the section sheet
/// reads, inked by the print profile.
/// </summary>
public class FacadeTests
{
    // The garage: an 8 × 4 m wall band 3000 high on a 400 mm slab (top at 0), a 200 mm flat roof.
    static readonly Sections.Solid[] Garage =
    {
        new Sections.Solid { Kind = "floor", MinZ = -400, MaxZ = 0 },
        new Sections.Solid { Kind = "wall", MinZ = 0, MaxZ = 3000 },
        new Sections.Solid { Kind = "roof", MinZ = 3000, MaxZ = 3200 }
    };

    static List<Pt> Rect(double u0, double u1, double z0, double z1) =>
        new List<Pt> { new Pt(u0, z0), new Pt(u1, z0), new Pt(u1, z1), new Pt(u0, z1) };

    static string[] Texts(Sections.Heights heights) => heights.Levels.Select(l => l.Kind + " " + l.Text).ToArray();

    [Fact]
    public void TheGarage_SouthFacade_HasTheSameMarksAsASectionThroughIt()
    {
        // A cross section cuts the roof 4 m wide; the south facade sees it 8 m wide.
        var section = Sections.ModelHeights(Garage, new[] { 0.0 }, new List<List<Pt>> { Rect(0, 4000, 3000, 3200) });
        var facade = Sections.ModelHeights(Garage, new[] { 0.0 }, new List<List<Pt>> { Rect(-4000, 4000, 3000, 3200) });
        Assert.Equal(new[] { "ground Ground -400", "floor Ground floor ±0", "gesims,mone Eaves/Ridge +3200" }, Texts(section));
        Assert.Equal(Texts(section), Texts(facade));
        Assert.Equal(-400, facade.Ground!.Value);
    }

    [Fact]
    public void WithNoSlab_TheGroundIsTheLowestWallBase_AndTheRoomsGiveTheFloor()
    {
        var walls = new[] { new Sections.Solid { Kind = "wall", MinZ = 0, MaxZ = 2800 } };
        var heights = Sections.ModelHeights(walls, new[] { 0.0 }, null);
        Assert.Equal(0, heights.Ground!.Value);
        Assert.Equal(new[] { "floor Ground floor ±0", "ground Ground ±0" }, Texts(heights));
    }

    [Fact]
    public void APitchedRoof_GivesGesimsAndMone_Apart()
    {
        var gable = new List<List<Pt>> { new List<Pt> { new Pt(0, 3000), new Pt(4000, 5000), new Pt(8000, 3000) } };
        var heights = Sections.ModelHeights(Garage.Take(2), new[] { 0.0 }, gable);
        Assert.Equal(new[] { "ground Ground -400", "floor Ground floor ±0", "gesims Eaves +3000", "mone Ridge +5000" }, Texts(heights));
    }

    [Fact]
    public void TheGroundLine_SpansTheFacadePlus1000mmEachSide()
    {
        var (x0, x1) = Sections.FacadeGround(-4100, 4100);
        Assert.Equal(-5100, x0);
        Assert.Equal(5100, x1);
        Assert.Equal(1000, Sections.FacadeGroundOverMm);
    }

    [Fact]
    public void TheLevelMarks_StayClearOfTheFacadesBox()
    {
        var heights = Sections.ModelHeights(Garage, new[] { 0.0 }, new List<List<Pt>> { Rect(-4100, 4100, 3000, 3200) });
        const int scale = 100;
        const double right = 4100;
        var marks = Sections.PlaceLevels(heights.Levels, z => z, right, scale, null);
        Assert.Equal(3, marks.Count);
        foreach (var mark in marks)
        {
            Assert.True(mark.LineA.X > right && mark.TextBox.MinX > right, mark.Level.Text);
            Assert.All(mark.Triangle, p => Assert.True(p.X > right));
        }
    }

    [Fact]
    public void TheGroundLineAndTheLevelMarks_TakeTheProfilesCutAndThinPens()
    {
        foreach (var profile in PrintProfiles.All)
        {
            Assert.Equal(profile.Cut, Sections.GroundPen(profile));
            Assert.Equal(profile.Thin, Sections.LevelPen(profile));
        }
        Assert.NotEqual(Sections.GroundPen(PrintProfiles.Default), Sections.GroundPen(PrintProfiles.Grey));
    }

    [Fact]
    public void TheFacadeAndTheSectionSheet_ReadTheSameHeightsHelper()
    {
        var section = File.ReadAllText(Path.Combine(FunctionsDir(), "SectionSheet.cs"));
        var facade = File.ReadAllText(Path.Combine(FunctionsDir(), "FacadeSheet.cs"));
        Assert.Contains("Sections.ModelHeights(", section);
        Assert.Contains("Sections.ModelHeights(", facade);
        Assert.Contains("Sections.PlaceLevels(", facade);
        // No second copy of the rule: the levels are built in Sections only.
        foreach (var file in Directory.GetFiles(FunctionsDir(), "*.cs"))
        {
            if (Path.GetFileName(file) == "Sections.cs") continue;
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Sections.Levels(", text);
            Assert.DoesNotContain("Sections.RoofHeights(", text);
            Assert.DoesNotContain("SectionHeights(", text);
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
