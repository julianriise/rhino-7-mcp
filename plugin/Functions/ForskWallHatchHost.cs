using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Draws the wall hatch from DrawForeground while the plugin is loaded.
/// Model space only, so a layout page is left alone. Nothing is added to the file.
/// </summary>
internal static class ForskWallHatchHost
{
    internal const string Solid = "Solid";

    static ForskWallHatchConduit _conduit;
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
        if (_conduit != null) return;
        _conduit = new ForskWallHatchConduit { Enabled = true };
    }

    internal static void Stop()
    {
        if (_conduit == null) return;
        _conduit.Enabled = false;
        _conduit = null;
    }

    internal static void Report(string message)
    {
        if (_reported) return;
        _reported = true;
        RhinoApp.WriteLine("Forsk wall hatch did not load: " + (message ?? "unknown error"));
    }
}

sealed class ForskWallHatchConduit : DisplayConduit
{
    public ForskWallHatchConduit()
    {
        SpaceFilter = ActiveSpace.ModelSpace;
    }

    protected override void DrawForeground(DrawEventArgs args)
    {
        try
        {
            Draw(args);
        }
        catch (Exception ex)
        {
            ForskWallHatchHost.Report(ex.Message);
        }
    }

    static void Draw(DrawEventArgs args)
    {
        var doc = args?.RhinoDoc;
        var display = args?.Display;
        if (doc == null || display == null) return;
        var pattern = Pattern(doc);
        if (pattern < 0) return;
        var colour = Color.FromArgb(ForskWallHatch.Red, ForskWallHatch.Green, ForskWallHatch.Blue);
        var tol = doc.ModelAbsoluteTolerance;
        if (tol <= 0) tol = 0.01;
        foreach (var obj in Selected(doc))
        {
            if (obj?.Attributes == null) continue;
            var kind = obj.Attributes.GetUserString("forsk:kind");
            if (!ForskWallHatch.Draws(obj.IsSelected(false) > 0, kind)) continue;
            var brep = AsBrep(obj.Geometry);
            if (brep == null) continue;
            foreach (var face in brep.Faces)
                DrawFace(display, face, pattern, colour, tol);
        }
    }

    static void DrawFace(DisplayPipeline display, BrepFace face, int pattern, Color colour, double tol)
    {
        if (face?.Loops == null) return;
        var curves = new List<Curve>();
        foreach (var loop in face.Loops)
        {
            var curve = loop?.To3dCurve();
            if (curve != null && curve.IsClosed) curves.Add(curve);
        }
        if (curves.Count == 0) return;
        Hatch[] hatches;
        try
        {
            hatches = Hatch.Create(curves, pattern, 0, 1, tol);
        }
        catch (Exception)
        {
            return;
        }
        if (hatches == null) return;
        foreach (var hatch in hatches)
        {
            if (hatch == null) continue;
            display.DrawHatch(hatch, colour, colour);
        }
    }

    static int Pattern(RhinoDoc doc)
    {
        var found = doc.HatchPatterns?.FindName(ForskWallHatchHost.Solid);
        if (found == null)
        {
            ForskWallHatchHost.Report("no Solid hatch pattern");
            return -1;
        }
        return found.Index;
    }

    static Brep AsBrep(GeometryBase geometry)
    {
        var brep = geometry as Brep;
        if (brep != null) return brep;
        var extrusion = geometry as Extrusion;
        if (extrusion == null) return null;
        return extrusion.ToBrep(false);
    }

    static IEnumerable<RhinoObject> Selected(RhinoDoc doc)
    {
        var settings = new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            LockedObjects = false,
            HiddenObjects = false,
            ActiveObjects = true,
            ReferenceObjects = false,
            DeletedObjects = false,
            IncludeLights = false,
            IncludeGrips = false,
            IdefObjects = false,
            SelectedObjectsFilter = true
        };
        return doc.Objects.GetObjectList(settings);
    }
}
