using System.Drawing;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The selection colour, AppearanceSettings.SelectedObjectColor. The selected
/// mesh is drawn in it. A hatch is not drawn over the faces: that fill sat
/// on the wall top and flickered against the white shade while panning.
/// </summary>
public static class ForskWallHatch
{
    public const int Red = 41;
    public const int Green = 72;
    public const int Blue = 245;

    public static Color Colour => Color.FromArgb(Red, Green, Blue);
}
