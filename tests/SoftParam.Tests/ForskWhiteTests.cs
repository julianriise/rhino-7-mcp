using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// The ini patch and the view filter. No Rhino. The fixture is a real Shaded export.
/// </summary>
public class ForskWhiteTests
{
    static string Patched()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaded-export.ini");
        return ForskWhite.Patch(File.ReadAllText(path));
    }

    [Fact]
    public void White_ini_sets_the_ground_edges_grid_and_wires()
    {
        var ini = Patched();
        Assert.Equal("Forsk White", ForskWhite.Read(ini, "", "Name"));
        Assert.Equal("245,245,247", ForskWhite.Read(ini, "View settings", "SolidColor"));
        Assert.Equal("2", ForskWhite.Read(ini, "View settings", "FillMode"));
        Assert.Equal("n", ForskWhite.Read(ini, "View settings", "UseDocumentGrid"));
        Assert.Equal("n", ForskWhite.Read(ini, "View settings", "DrawGrid"));
        Assert.Equal("n", ForskWhite.Read(ini, "View settings", "DrawAxes"));
        Assert.Equal("n", ForskWhite.Read(ini, "View settings", "DrawWorldAxes"));
        Assert.Equal("0,0,0", ForskWhite.Read(ini, "Objects\\Surfaces", "EdgeColor"));
        Assert.Equal("0,0,0", ForskWhite.Read(ini, "Objects\\Surfaces", "NakedEdgeColor"));
        Assert.Equal("2", ForskWhite.Read(ini, "Objects\\Surfaces", "EdgeColorUsage"));
        Assert.Equal("1", ForskWhite.Read(ini, "Objects\\Surfaces", "EdgeThickness"));
        Assert.Equal("y", ForskWhite.Read(ini, "Shading", "ShadeVertexColors"));
        Assert.Equal("n", ForskWhite.Read(ini, "Objects\\Surfaces", "ShowIsocurves"));
        Assert.Equal("n", ForskWhite.Read(ini, "Objects\\Meshes", "ShowMeshWires"));
        Assert.Equal("n", ForskWhite.Read(ini, "Objects\\Surfaces", "ShowTangentEdges"));
        Assert.Equal("n", ForskWhite.Read(ini, "Objects\\Surfaces", "ShowTangentSeams"));
        Assert.Equal("y", ForskWhite.Read(ini, "Shading", "UseObjectMaterial"));
        Assert.Equal("n", ForskWhite.Read(ini, "Shading\\Material\\Front Material", "OverrideObjectTransparency"));
        Assert.Equal("n", ForskWhite.Read(ini, "Shading\\Material\\Front Material", "FlatShaded"));
        Assert.Equal("255,255,255", ForskWhite.Read(ini, "Shading\\Material\\Front Material", "Diffuse"));
        Assert.Equal("0", ForskWhite.Read(ini, "Shading\\Material\\Front Material", "Shine"));
        Assert.Equal("8,8,8", ForskWhite.Read(ini, "Shading\\Material\\Front Material", "Specular"));
        Assert.Equal("200,200,200", ForskWhite.Read(ini, "Lighting", "AmbientColor"));
        Assert.Equal("n", ForskWhite.Read(ini, "Lighting", "CastShadows"));
        Assert.Equal("1024", ForskWhite.Read(ini, "Lighting", "ShadowMapSize"));
        Assert.Equal("n", ForskWhite.Read(ini, "Lighting", "PerPixelLighting"));
        Assert.Equal("n", ForskWhite.Read(ini, "View settings", "ShowClippingPlanes"));
        Assert.Equal("3", ForskWhite.Read(ini, "View settings", "ClippingSurfaceUsage"));
        Assert.Equal("255,255,255", ForskWhite.Read(ini, "View settings", "ClippingSurfaceColor"));
        Assert.Equal("1", ForskWhite.Read(ini, "View settings", "ClippingEdgesUsage"));
        Assert.Equal("0,0,0", ForskWhite.Read(ini, "View settings", "ClippingEdgeColor"));
        Assert.Equal("0,0,0", ForskWhite.Read(ini, "Objects\\Technical", "TSiColor"));
        Assert.Equal("1", ForskWhite.Read(ini, "Objects\\Technical", "TSiThickness"));
        // Technical lines stay off. The back material and the x-ray flag stay as exported.
        Assert.Equal("0", ForskWhite.Read(ini, "Objects\\Technical", "TechnicalMask"));
        Assert.Equal("145,213,205", ForskWhite.Read(ini, "Shading\\Material\\Back Material", "Diffuse"));
        Assert.Equal("n", ForskWhite.Read(ini, "", "XrayAllObjects"));
        Assert.Equal("n", ForskWhite.Read(ini, "", "IgnoreHighlights"));
        Assert.Equal("0", ForskWhite.Read(ini, "Shading\\Material\\Front Material", "Transparency"));
    }

    [Fact]
    public void White_patch_does_not_invent_a_key_or_an_alpha()
    {
        var ini = "[DisplayMode\\abc]\nName=Shaded\nXrayAllObjects=n\n"
            + "[DisplayMode\\abc\\View settings]\nSolidColor=230,230,230\nWxColor=1,2,3,4\n";
        var patched = ForskWhite.Patch(ini);
        Assert.Equal("245,245,247", ForskWhite.Read(patched, "View settings", "SolidColor"));
        Assert.Equal("1,2,3,4", ForskWhite.Read(patched, "View settings", "WxColor"));
        Assert.DoesNotContain("ShowMeshWires", patched);
        Assert.Null(ForskWhite.Read(patched, "Objects\\Meshes", "ShowMeshWires"));

        var four = ForskWhite.Patch(
            "[DisplayMode\\abc]\nName=Shaded\n[DisplayMode\\abc\\View settings]\nSolidColor=230,230,230,128\n");
        Assert.Equal("245,245,247,128", ForskWhite.Read(four, "View settings", "SolidColor"));
    }

    [Fact]
    public void White_skips_a_layout_page_and_a_detail()
    {
        Assert.False(ForskWhite.AssignsDisplayMode("RhinoPageView"));
        Assert.False(ForskWhite.AssignsDisplayMode("DetailViewObject"));
        Assert.True(ForskWhite.AssignsDisplayMode("RhinoView"));
        Assert.False(ForskWhite.NeedsAssign(true, "Shaded", "RhinoPageView"));
        Assert.True(ForskWhite.NeedsAssign(true, "Shaded", "RhinoView"));
        Assert.False(ForskWhite.NeedsAssign(true, "Forsk White", "RhinoView"));
        Assert.True(ForskWhite.NeedsAssign(false, "Forsk White", "RhinoView"));
        Assert.False(ForskWhite.NeedsAssign(false, "Rendered", "RhinoView"));
        Assert.Equal("Shaded", ForskWhite.TargetMode(false));
        Assert.True(ForskWhite.ReassignsAfterCommand("4View"));
        Assert.True(ForskWhite.ReassignsAfterCommand("Open"));
        Assert.False(ForskWhite.ReassignsAfterCommand("Move"));
    }

    [Fact]
    public void White_replaces_an_older_import_and_keeps_shadows_off()
    {
        Assert.Equal(1, ForskWhite.ModeRevision);
        Assert.True(ForskWhite.NeedsReimport(0));
        Assert.False(ForskWhite.NeedsReimport(ForskWhite.ModeRevision));
        Assert.True(ForskWhite.NeedsReassign(true, "Forsk White", "RhinoView", false));
        Assert.False(ForskWhite.NeedsReassign(true, "Forsk White", "RhinoView", true));
        Assert.False(ForskWhite.NeedsReassign(true, "Rendered", "RhinoView", false));
        Assert.False(ForskWhite.NeedsReassign(true, "Forsk White", "RhinoPageView", false));
        Assert.False(ForskWhite.NeedsReassign(true, "Forsk White", "DetailViewObject", false));
        Assert.False(ForskWhite.NeedsReassign(false, "Forsk White", "RhinoView", false));

        var host = File.ReadAllText(Path.Combine(FunctionsDir(), "ForskWhiteHost.cs"));
        Assert.Contains("ForskWhite.NeedsReimport", host, StringComparison.Ordinal);
        Assert.Contains("ForskWhite.NeedsReassign", host, StringComparison.Ordinal);
        Assert.Contains("ForskWhiteRevision", host, StringComparison.Ordinal);
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
