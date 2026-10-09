using System.Drawing;
using System.Globalization;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Forsk Technical without Rhino: which look a view gets, the ini patch from
/// the print pens, what the plan cache rebuilds, and that the screen pieces
/// carry the print set's roles (so its export layers) and pens.
/// </summary>
public class ForskTechnicalTests
{
    const string Technical = "Forsk Technical";
    const string White = "Forsk White";

    static string Patched(PrintProfile profile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaded-export.ini");
        return ForskTechnical.Patch(File.ReadAllText(path), profile);
    }

    [Theory]
    [InlineData(true, 0, 0, -1, ForskTechnical.Look.Plan)]
    [InlineData(true, 0.01, 0, -1, ForskTechnical.Look.Plan)]
    [InlineData(true, 0, 1, 0, ForskTechnical.Look.Elevation)]
    [InlineData(true, -1, 0, 0, ForskTechnical.Look.Elevation)]
    [InlineData(true, 0.7, 0.7, 0.01, ForskTechnical.Look.Elevation)]
    [InlineData(true, 1, 1, -1, ForskTechnical.Look.Model)]
    [InlineData(true, 0, 0, 1, ForskTechnical.Look.Model)]
    [InlineData(true, 0, 0, 0, ForskTechnical.Look.Model)]
    [InlineData(false, 0, 0, -1, ForskTechnical.Look.Model)]
    [InlineData(false, 0, 1, 0, ForskTechnical.Look.Model)]
    public void A_view_is_a_plan_an_elevation_or_the_model(bool parallel, double dx, double dy, double dz, ForskTechnical.Look look)
    {
        Assert.Equal(look, ForskTechnical.Classify(parallel, dx, dy, dz));
    }

    [Fact]
    public void Plans_and_elevations_take_technical_and_perspective_keeps_white()
    {
        Assert.Equal(Technical, ForskTechnical.Wanted(true, true, ForskTechnical.Look.Plan));
        Assert.Equal(Technical, ForskTechnical.Wanted(true, true, ForskTechnical.Look.Elevation));
        Assert.Equal(White, ForskTechnical.Wanted(true, true, ForskTechnical.Look.Model));
        Assert.Equal(White, ForskTechnical.Wanted(true, false, ForskTechnical.Look.Plan));
        Assert.Equal("Shaded", ForskTechnical.Wanted(false, true, ForskTechnical.Look.Plan));

        Assert.True(ForskTechnical.Retunes(White, Technical, false));
        Assert.True(ForskTechnical.Retunes(Technical, White, false));
        Assert.False(ForskTechnical.Retunes(Technical, Technical, true));
        Assert.True(ForskTechnical.Retunes(Technical, Technical, false));
        Assert.False(ForskTechnical.Retunes("Rendered", Technical, false));
        Assert.False(ForskTechnical.Retunes(Technical, "Shaded", false));

        Assert.True(ForskWhite.NeedsAssign(true, White, Technical, "RhinoView"));
        Assert.False(ForskWhite.NeedsAssign(true, Technical, Technical, "RhinoView"));
        Assert.True(ForskWhite.NeedsAssign(false, Technical, "Shaded", "RhinoView"));
        Assert.False(ForskWhite.NeedsAssign(true, White, Technical, "RhinoPageView"));
    }

    [Fact]
    public void The_setting_defaults_on_and_the_command_toggles_it()
    {
        Assert.Equal("ForskTechnical", ForskTechnical.SettingKey);
        var host = File.ReadAllText(Path.Combine(PluginDir(), "Functions", "ForskTechnicalHost.cs"));
        Assert.Contains("GetBool(ForskTechnical.SettingKey, true)", host, StringComparison.Ordinal);
        Assert.Contains("DisplayModeDescription.WireframeId", host, StringComparison.Ordinal);
        var command = File.ReadAllText(Path.Combine(PluginDir(), "Commands", "ForskTechnicalCommand.cs"));
        Assert.Contains("EnglishName => \"ForskTechnical\"", command, StringComparison.Ordinal);
        Assert.Contains("ForskWhiteHost.ApplyActive()", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.13, 1)]
    [InlineData(0.18, 1)]
    [InlineData(0.25, 1)]
    [InlineData(0.35, 2)]
    [InlineData(0.50, 3)]
    [InlineData(0.70, 4)]
    [InlineData(5.0, 6)]
    [InlineData(0, 1)]
    public void A_paper_pen_is_whole_screen_pixels(double mm, int px)
    {
        Assert.Equal(px, ForskTechnical.Px(mm));
    }

    [Fact]
    public void Technical_ini_is_white_flat_and_inked_with_the_default_pens()
    {
        var ini = Patched(PrintProfiles.Default);
        Assert.Equal(Technical, ForskWhite.Read(ini, "", "Name"));
        Assert.Equal("255,255,255", ForskWhite.Read(ini, "View settings", "SolidColor"));
        Assert.Equal("n", ForskWhite.Read(ini, "View settings", "DrawGrid"));
        Assert.Equal("n", ForskWhite.Read(ini, "View settings", "ShowClippingPlanes"));
        Assert.Equal("n", ForskWhite.Read(ini, "View settings", "ClippingShowXSurface"));
        Assert.Equal("n", ForskWhite.Read(ini, "View settings", "ClippingShowXEdges"));
        Assert.Equal("1", ForskWhite.Read(ini, "View settings", "ClippingEdgesUsage"));
        Assert.Equal("0,0,0", ForskWhite.Read(ini, "View settings", "ClippingEdgeColor"));
        Assert.Equal("3", ForskWhite.Read(ini, "View settings", "ClippingEdgeThickness"));
        Assert.Equal("n", ForskWhite.Read(ini, "Shading", "UseObjectMaterial"));
        Assert.Equal("n", ForskWhite.Read(ini, "Shading", "ShadeVertexColors"));
        Assert.Equal("n", ForskWhite.Read(ini, "Shading", "ShadeSurface"));
        Assert.Equal("n", ForskWhite.Read(ini, "Shading\\Material\\Front Material", "FlatShaded"));
        Assert.Equal("y", ForskWhite.Read(ini, "Shading\\Material\\Front Material", "OverrideObjectColor"));
        Assert.Equal("255,255,255", ForskWhite.Read(ini, "Shading\\Material\\Back Material", "Diffuse"));
        Assert.Equal("255,255,255", ForskWhite.Read(ini, "Lighting", "AmbientColor"));
        Assert.Equal("n", ForskWhite.Read(ini, "Lighting", "CastShadows"));
        Assert.Equal("1", ForskWhite.Read(ini, "Objects\\Surfaces", "EdgeThickness"));
        Assert.Equal("0,0,0", ForskWhite.Read(ini, "Objects\\Surfaces", "EdgeColor"));
        Assert.Equal("2", ForskWhite.Read(ini, "Objects\\Surfaces", "EdgeColorUsage"));
        Assert.Equal("n", ForskWhite.Read(ini, "Objects\\Surfaces", "ShowIsocurves"));
        Assert.Equal("n", ForskWhite.Read(ini, "Objects\\Meshes", "ShowMeshWires"));
    }

    [Fact]
    public void Technical_ini_follows_the_grey_and_hatched_profiles()
    {
        var grey = Patched(PrintProfiles.Grey);
        Assert.Equal("n", ForskWhite.Read(grey, "Shading", "ShadeSurface"));
        Assert.Equal("30,30,30", ForskWhite.Read(grey, "View settings", "ClippingEdgeColor"));
        Assert.Equal("110,110,110", ForskWhite.Read(grey, "Objects\\Surfaces", "EdgeColor"));

        var hatch = Patched(PrintProfiles.Hatched);
        Assert.Equal("n", ForskWhite.Read(hatch, "View settings", "ClippingShowXSurface"));
        Assert.Equal("2", ForskWhite.Read(hatch, "View settings", "ClippingEdgeThickness"));

        Assert.Equal("3:default", ForskTechnical.Signature(PrintProfiles.Default));
        Assert.False(ForskTechnical.NeedsReimport("3:default", PrintProfiles.Default));
        Assert.True(ForskTechnical.NeedsReimport("2:default", PrintProfiles.Default));
        Assert.True(ForskTechnical.NeedsReimport("1:default", PrintProfiles.Default));
        Assert.True(ForskTechnical.NeedsReimport("3:default", PrintProfiles.Grey));
        Assert.True(ForskTechnical.NeedsReimport(null, PrintProfiles.Default));
    }

    [Fact]
    public void A_marker_rebuilds_itself_and_a_wall_its_openings()
    {
        var cache = Fresh(out var a, out var b, out var wall);
        cache.Changed(a, "opening_marker", wall, false);
        Assert.Equal(new[] { a }, cache.TakeDirty());
        Assert.Empty(cache.TakeDirty());

        cache.Changed(wall, "wall", Guid.Empty, false);
        Assert.Equal(new[] { a }, cache.TakeDirty());
        Assert.True(cache.GroundDirty);

        cache.Changed(Guid.NewGuid(), "room", Guid.Empty, false);
        cache.Changed(Guid.NewGuid(), "dimension", Guid.Empty, false);
        Assert.False(cache.HasDirty);
        Assert.False(ForskTechnicalCache<string>.Matters("room"));
        Assert.True(cache.TryGet(b, out var stair));
        Assert.Equal("stair", stair);
    }

    [Fact]
    public void A_floor_or_another_profile_rebuilds_everything()
    {
        var cache = Fresh(out _, out _, out _);
        cache.Changed(Guid.NewGuid(), "floor", Guid.Empty, false);
        Assert.True(cache.AllDirty);
        Assert.True(cache.GroundDirty);
        Assert.Empty(cache.TakeDirty());

        cache = Fresh(out _, out _, out _);
        cache.SetContext("1:default");
        Assert.False(cache.AllDirty);
        cache.SetContext("1:grey");
        Assert.True(cache.AllDirty);

        cache = Fresh(out _, out _, out _);
        cache.Reset();
        Assert.Equal(0, cache.Count);
        Assert.True(cache.AllDirty);
    }

    [Fact]
    public void A_plan_hides_openings_and_stairs_until_they_are_deleted()
    {
        var cache = Fresh(out _, out var stair, out _);
        var block = Guid.NewGuid();
        cache.Changed(block, "opening", Guid.Empty, false);
        Assert.True(cache.IsHidden(block));
        Assert.True(cache.IsHidden(stair));
        cache.Changed(block, "opening", Guid.Empty, true);
        Assert.False(cache.IsHidden(block));

        cache.Changed(stair, "stair", Guid.Empty, true);
        Assert.False(cache.IsHidden(stair));
        Assert.Equal(new[] { stair }, cache.TakeDirty());
        var version = cache.Version;
        cache.Remove(stair);
        Assert.True(cache.Version > version);
        Assert.False(cache.TryGet(stair, out _));
    }

    /// <summary>A built cache: marker a on wall, stair b.</summary>
    static ForskTechnicalCache<string> Fresh(out Guid a, out Guid b, out Guid wall)
    {
        a = Guid.NewGuid();
        b = Guid.NewGuid();
        wall = Guid.NewGuid();
        var cache = new ForskTechnicalCache<string>();
        cache.SetContext("1:default");
        Assert.True(cache.AllDirty);
        cache.BeginAll();
        cache.Put(a, wall, "door");
        cache.Hide(b);
        cache.Put(b, Guid.Empty, "stair");
        cache.GroundDone();
        Assert.False(cache.HasDirty);
        return cache;
    }

    [Fact]
    public void Opening_strokes_are_the_sheet_symbol_on_A_SYMB_at_the_thin_pen()
    {
        Assert.True(OpeningTypes.TryRead("door", "door.hinged_single", "L", "in", out var record, out _));
        var frame = new OpeningTypes.PlanFrame { OuterHalf = 500, InnerHalf = 450, HalfThick = 100, Head = 2100, CutZ = 1200, YInward = 1, XLeft = 1 };
        var marks = OpeningTypes.PlanSymbol(record, "1:100", frame);
        OpeningTypes.AddWallFrame(marks, frame);
        // A wall along +y: the opening's x runs north, its y west.
        var strokes = ForskTechnical.FromOpening(marks, new ForskTechnical.Frame(1000, 2000, 0, 1, -1, 0), 1190, PrintProfiles.Default);

        Assert.NotEmpty(strokes);
        Assert.Contains(strokes, s => s.Shape == "arc");
        foreach (var stroke in strokes)
        {
            Assert.Equal("A-SYMB", SheetFlat.LayerFor(stroke.Role));
            Assert.Equal(PrintProfiles.Default.Thin.Mm, stroke.Mm);
            Assert.Equal(1, stroke.Px);
            Assert.Equal(1190, stroke.Z);
        }
        var leaf = marks.First(m => m.Part == "leaf");
        var drawn = strokes.First(s => s.Part == "leaf");
        Assert.Equal(1000 - leaf.Y0, drawn.X0, 6);
        Assert.Equal(2000 + leaf.X0, drawn.Y0, 6);

        var arc = marks.First(m => m.Shape == "arc");
        Assert.True(ForskTechnical.ArcMid(arc, out var mx, out var my));
        Assert.Equal(arc.Radius, Math.Sqrt((mx - arc.Cx) * (mx - arc.Cx) + (my - arc.Cy) * (my - arc.Cy)), 6);
    }

    [Fact]
    public void Stair_strokes_are_on_A_STAIR_with_the_outline_at_the_beyond_pen()
    {
        var spec = new Stairs.Spec { X = 1000, Y = 3350, Z = 0, Dx = 1, Dy = 0 };
        var flight = Stairs.Plan(2750, 180, 260, 900);
        var profile = PrintProfiles.Grey;
        var strokes = ForskTechnical.FromStair(spec, flight, 1200, 100, 1190, profile);

        Assert.Contains(strokes, s => s.Part == "outline");
        Assert.Contains(strokes, s => s.Dashed);
        foreach (var stroke in strokes)
        {
            Assert.Equal("A-STAIR", SheetFlat.LayerFor(stroke.Role));
            var outline = stroke.Part == "outline";
            Assert.Equal(outline ? profile.Beyond.Mm : profile.Thin.Mm, stroke.Mm);
            Assert.Equal(1, stroke.Px);
            var ink = stroke.Shape == "text" ? profile.Text
                : stroke.Dashed ? profile.Dashed
                : outline ? profile.Beyond.Color : profile.Thin.Color;
            Assert.Equal(ink.ToArgb(), stroke.Ink.ToArgb());
        }
    }

    [Fact]
    public void Elevation_ground_line_is_A_GRND_at_the_cut_pen_past_each_side()
    {
        var front = ForskTechnical.GroundLine(0, 0, 10000, 8000, -250, 1, 0, PrintProfiles.Default);
        Assert.NotNull(front);
        Assert.Equal("A-GRND", SheetFlat.LayerFor(front.Role));
        Assert.Equal(PrintProfiles.Default.Cut.Mm, front.Mm);
        Assert.Equal(3, front.Px);
        Assert.Equal(-1000, front.X0, 6);
        Assert.Equal(11000, front.X1, 6);
        Assert.Equal(4000, front.Y0, 6);
        Assert.Equal(-250, front.Z);

        var right = ForskTechnical.GroundLine(0, 0, 10000, 8000, 0, 0, 1, PrintProfiles.Hatched);
        Assert.Equal(-1000, right.Y0, 6);
        Assert.Equal(9000, right.Y1, 6);
        Assert.Equal(PrintProfiles.Hatched.Cut.Mm, right.Mm);

        Assert.Null(ForskTechnical.GroundLine(0, 0, 10000, 8000, null, 1, 0, PrintProfiles.Default));
        Assert.Null(ForskTechnical.GroundLine(0, 0, 10000, 8000, 0, 0, 0, PrintProfiles.Default));
    }

    [Fact]
    public void A_wall_contour_is_cached_cut_lines_and_a_speck_is_dropped()
    {
        var strokes = new List<ForskTechnical.Stroke>();
        ForskTechnical.AddContour(strokes, new double[] { 0, 0, 1000, 0, 1000, 200, 0, 200, 0, 0, 0.1, 0 }, 1190, PrintProfiles.Default);
        Assert.Equal(4, strokes.Count);
        Assert.Equal(1000, strokes[0].X1, 6);
        Assert.Equal(0, strokes[0].Y1, 6);
        Assert.Equal(200, strokes[1].Y1, 6);
        foreach (var stroke in strokes)
        {
            Assert.Equal("line", stroke.Shape);
            Assert.Equal("cut", stroke.Role);
            Assert.Equal(PrintProfiles.Default.Cut.Mm, stroke.Mm);
            Assert.Equal(1190, stroke.Z);
        }

        Assert.True(ForskTechnical.HidesInPlan("A-OPEN"));
        Assert.True(ForskTechnical.HidesInPlan("a-open::Block"));
        Assert.True(ForskTechnical.HidesInPlan("A-STAIR"));
        Assert.True(ForskTechnical.HidesInPlan("A-STAIR::Flight"));
        Assert.False(ForskTechnical.HidesInPlan("A-WALL"));
        Assert.False(ForskTechnical.HidesInPlan("A-OPENING"));
        Assert.False(ForskTechnical.HidesInPlan(null));
    }

    [Fact]
    public void The_plan_draw_does_not_visit_every_object_or_section_every_frame()
    {
        var host = File.ReadAllText(Path.Combine(PluginDir(), "Functions", "ForskTechnicalHost.cs"));
        Assert.DoesNotContain("PreDrawObject", host, StringComparison.Ordinal);
        Assert.Contains("GeometryFilter = ObjectType.None", host, StringComparison.Ordinal);
        Assert.Contains("ContourCall.Brep", host, StringComparison.Ordinal);
        Assert.Contains("ForskTechnical.AddContour", host, StringComparison.Ordinal);
        Assert.Contains("ForskTechnical.GroundEnds", host, StringComparison.Ordinal);
        var start = host.IndexOf("internal static void DrawPlan", StringComparison.Ordinal);
        var end = host.IndexOf("static string StoredSignature", StringComparison.Ordinal);
        var draw = host.Substring(start, end - start);
        Assert.DoesNotContain("CreateContourCurves", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("ContourCall", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("new List", draw, StringComparison.Ordinal);
    }

    [Fact]
    public void The_print_bake_and_the_screen_share_one_pen_rule()
    {
        Assert.Equal(PrintProfiles.Default.Cut.Mm, ForskTechnical.PenFor("cut", null, PrintProfiles.Default).Mm);
        Assert.Equal(PrintProfiles.Default.Beyond.Mm, ForskTechnical.PenFor("greyscale", null, PrintProfiles.Default).Mm);
        Assert.Equal(Sections.GroundPen(PrintProfiles.Grey).Color, ForskTechnical.PenFor("ground_line", null, PrintProfiles.Grey).Color);
        Assert.Equal(Color.FromArgb(130, 130, 130).ToArgb(), ForskTechnical.Ink(PrintProfiles.Grey.Thin, true, PrintProfiles.Grey).ToArgb());

        var plan = File.ReadAllText(Path.Combine(PluginDir(), "Functions", "PlanSymbols.cs"))
            + File.ReadAllText(Path.Combine(PluginDir(), "Functions", "PlanSymbolsRooms.cs"));
        Assert.Contains("ForskTechnical.PenFor(\"symbol\"", plan, StringComparison.Ordinal);
        Assert.Contains("ForskTechnical.PenFor(\"stair\"", plan, StringComparison.Ordinal);
        Assert.Contains("ForskTechnical.Ink(", plan, StringComparison.Ordinal);
        Assert.Contains("TryOpeningMarks", File.ReadAllText(Path.Combine(PluginDir(), "Functions", "ForskTechnicalScreen.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Decimal_comma_culture_does_not_reach_the_ini()
    {
        Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
        var ini = Patched(PrintProfiles.Hatched);
        Assert.Equal("2", ForskWhite.Read(ini, "View settings", "ClippingEdgeThickness"));
        Assert.Equal("3:hatch", ForskTechnical.Signature(PrintProfiles.Hatched));
        Assert.Equal(2, ForskTechnical.Px(0.35));
    }

    static string PluginDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "plugin");
            if (Directory.Exists(Path.Combine(path, "Functions"))) return path;
        }
        throw new DirectoryNotFoundException("plugin above " + AppContext.BaseDirectory);
    }
}
