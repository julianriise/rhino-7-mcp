using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Jump inside views take Forsk Interior (Julian, 2026-10-08): the materials
/// as they are, not Forsk White's white with black lines. Forsk keeps the look
/// on the view, and brings it back when the saved named view is restored.
/// </summary>
public class ForskInteriorTests
{
    [Fact]
    public void ForskWhite_LeavesAnInteriorViewOnItsLook()
    {
        Assert.False(ForskWhite.NeedsAssign(true, ForskInterior.ModeName, ForskWhite.ModeName, "RhinoView"));
        Assert.False(ForskWhite.NeedsAssign(false, ForskInterior.ModeName, ForskWhite.ModeName, "RhinoView"));
        Assert.True(ForskWhite.NeedsAssign(true, "Shaded", ForskWhite.ModeName, "RhinoView"));
    }

    [Fact]
    public void TheRoomTypePastel_StaysOffRealisticLooks()
    {
        Assert.True(RoomTypes.ShowInView(true, false, 0.3, 0.2, -0.4, ForskWhite.ModeName));
        Assert.False(RoomTypes.ShowInView(true, false, 0.3, 0.2, -0.4, ForskInterior.ModeName));
        Assert.False(RoomTypes.ShowInView(true, false, 0.3, 0.2, -0.4, "Rendered"));
        Assert.False(RoomTypes.ShowInView(true, false, 0.3, 0.2, -0.4, "Raytraced"));
    }

    [Fact]
    public void TheSavedViews_AreAListOfNames_EachOnce()
    {
        var stored = ForskInterior.AddView(null, "Living");
        stored = ForskInterior.AddView(stored, "Bedroom 1");
        stored = ForskInterior.AddView(stored, "living");
        Assert.Equal(new[] { "Living", "Bedroom 1" }, ForskInterior.Views(stored));
        Assert.Empty(ForskInterior.Views(""));
    }

    [Fact]
    public void ARestoredView_IsTheSavedCamera_WithinAMillimetre()
    {
        var eye = new[] { 3000.0, 3700, 1200 };
        var target = new[] { 3000.0, 9000, 1200 };
        Assert.True(ForskInterior.SameCamera(new[] { 3000.4, 3700, 1200 }, target, eye, target));
        Assert.False(ForskInterior.SameCamera(new[] { 3000.0, 3720, 1200 }, target, eye, target));
        Assert.False(ForskInterior.SameCamera(eye, new[] { 3000.0, 9000, 1500 }, eye, target));
    }

    const string RenderedExport = "[DisplayMode\\cae60bae-2d51-4299-abf7-a339fca86f3b]\nName=Rendered\n"
        + "[DisplayMode\\cae60bae-2d51-4299-abf7-a339fca86f3b\\Lighting]\nShadowBlur=0\nShadowIntensity=100\nShadowColor=0,0,0\n"
        + "NumSamples=4\nShadowMapSize=2048\nCastShadows=n\nTransparencyTolerance=40\nShowLights=n\n"
        + "[DisplayMode\\cae60bae-2d51-4299-abf7-a339fca86f3b\\Objects\\Curves]\nShowCurves=y\n"
        + "[DisplayMode\\cae60bae-2d51-4299-abf7-a339fca86f3b\\Objects\\Surfaces]\nShowIsocurves=y\nShowEdges=n\n";

    [Fact]
    public void ThePatch_TurnsSubtleShadowsOn_AndTheLinesOff()
    {
        var ini = ForskInterior.Patch(RenderedExport);
        Assert.Equal("Forsk Interior", ForskWhite.Read(ini, "", "Name"));
        Assert.Equal("y", ForskWhite.Read(ini, "Lighting", "CastShadows"));
        Assert.Equal("25", ForskWhite.Read(ini, "Lighting", "ShadowIntensity"));
        Assert.Equal("5", ForskWhite.Read(ini, "Lighting", "ShadowBlur"));
        Assert.Equal("1", ForskWhite.Read(ini, "Lighting", "NumSamples"));
        Assert.Equal("1024", ForskWhite.Read(ini, "Lighting", "ShadowMapSize"));
        // Glass casts no shadow.
        Assert.Equal("0", ForskWhite.Read(ini, "Lighting", "TransparencyTolerance"));
        Assert.Equal("n", ForskWhite.Read(ini, "Objects\\Curves", "ShowCurves"));
        Assert.Equal("n", ForskWhite.Read(ini, "Objects\\Surfaces", "ShowIsocurves"));
        // A key the export lacks stays absent.
        Assert.Null(ForskWhite.Read(ini, "View settings", "DrawGrid"));
    }

    [Fact]
    public void TheImport_TakesAFreshId_EverySectionAlike_AndKeepsWhatItDerivesFrom()
    {
        var ini = "[DisplayMode\\0a55df40-e595-43d0-8a4b-45ce93bb771d]\nDerivedFrom=cae60bae-2d51-4299-abf7-a339fca86f3b\n"
            + "[DisplayMode\\0a55df40-e595-43d0-8a4b-45ce93bb771d\\Lighting]\nCastShadows=y\n";
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var fresh = ForskInterior.WithFreshId(ini, id);
        Assert.DoesNotContain("0a55df40", fresh);
        Assert.Equal(2, fresh.Split("11111111-2222-3333-4444-555555555555").Length - 1);
        Assert.Contains("DerivedFrom=cae60bae-2d51-4299-abf7-a339fca86f3b", fresh);
        Assert.Equal("no sections", ForskInterior.WithFreshId("no sections", id));
    }

    [Fact]
    public void TheFirstCut_IsReplacedByTheShadowedMode()
    {
        Assert.Equal(1, ForskInterior.ModeRevision);
        Assert.True(ForskInterior.NeedsReimport(0));
        Assert.False(ForskInterior.NeedsReimport(ForskInterior.ModeRevision));
    }

    [Fact]
    public void FurnitureAndStairs_GetAMaterial_WallsAndFloorsKeepTheirs()
    {
        var layers = ForskInterior.LayerMaterials.Select(p => p.Key + "=" + p.Value);
        Assert.Equal(new[] { "A-FURN=wood", "A-FURN-FIXD=white", "A-STAIR=wood", "A-ROOF=plaster" }, layers);
    }
}
