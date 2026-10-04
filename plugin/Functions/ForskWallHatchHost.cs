using System;
using System.Drawing;
using Rhino;
using Rhino.ApplicationSettings;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Sets the selection colour while the plugin is loaded. The selected mesh
/// is drawn in that colour by the display mode. A hatch over the faces is
/// not drawn: it was coplanar with the wall top and flickered while panning.
/// </summary>
internal static class ForskWallHatchHost
{
    static bool _reported;

    internal static void Start()
    {
        try
        {
            var colour = Color.FromArgb(ForskWallHatch.Red, ForskWallHatch.Green, ForskWallHatch.Blue);
            var current = AppearanceSettings.SelectedObjectColor;
            if (current.R != colour.R || current.G != colour.G || current.B != colour.B)
                AppearanceSettings.SelectedObjectColor = colour;
        }
        catch (Exception ex)
        {
            Report(ex.Message);
        }
    }

    internal static void Stop()
    {
    }

    internal static void Report(string message)
    {
        if (_reported) return;
        _reported = true;
        RhinoApp.WriteLine("Forsk wall hatch did not load: " + (message ?? "unknown error"));
    }
}
