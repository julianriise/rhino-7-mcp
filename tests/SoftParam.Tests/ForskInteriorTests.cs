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

    [Fact]
    public void FurnitureAndStairs_GetAMaterial_WallsAndFloorsKeepTheirs()
    {
        var layers = ForskInterior.LayerMaterials.Select(p => p.Key + "=" + p.Value);
        Assert.Equal(new[] { "A-FURN=wood", "A-FURN-FIXD=white", "A-STAIR=wood" }, layers);
    }
}
