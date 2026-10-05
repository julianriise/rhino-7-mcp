using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    /// <summary>
    /// Draws the sample house on wall, door, window and label. Refuses a file
    /// that already has objects other than Forsk's plan cut. An empty file that is not millimetres becomes
    /// millimetres, so Generate 3D can run. Does not change the display mode.
    /// </summary>
    public static string OpenSampleHouse(RhinoDoc doc)
    {
        if (doc == null) return "Sample house · no document.";
        var everything = new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, IncludeLights = true };
        if (!SampleHouse.FileIsEmpty(doc.Objects.GetObjectList(everything)
                .Select(o => o.Attributes.GetUserString(ForskPlanCut.TagKey))))
            return "Sample house · the file already has objects.";
        var undo = 0u;
        try
        {
            undo = doc.BeginUndoRecord("Forsk: Open sample house");
            if (doc.ModelUnitSystem != UnitSystem.Millimeters)
                doc.ModelUnitSystem = UnitSystem.Millimeters;
            DrawSample(doc);
            FrameSample(doc);
            doc.Views.Redraw();
            return SampleHouse.Receipt();
        }
        finally
        {
            if (undo != 0) doc.EndUndoRecord(undo);
        }
    }

    static void DrawSample(RhinoDoc doc)
    {
        var wall = SampleLayer(doc, "wall", Color.FromArgb(30, 30, 30));
        var door = SampleLayer(doc, "door", Color.FromArgb(190, 90, 30));
        var window = SampleLayer(doc, "window", Color.FromArgb(30, 110, 190));
        var label = SampleLayer(doc, "label", Color.FromArgb(90, 90, 90));
        var n = 0;
        foreach (var rect in SampleHouse.Walls)
            AddSampleRect(doc, wall, "sample-wall-" + (++n).ToString("00", CultureInfo.InvariantCulture), rect);
        n = 0;
        foreach (var rect in SampleHouse.Doors)
            AddSampleRect(doc, door, "sample-door-" + (++n).ToString("00", CultureInfo.InvariantCulture), rect);
        n = 0;
        foreach (var rect in SampleHouse.Windows)
            AddSampleRect(doc, window, "sample-window-" + (++n).ToString("00", CultureInfo.InvariantCulture), rect);
        n = 0;
        foreach (var mark in SampleHouse.Labels)
        {
            var plane = Plane.WorldXY;
            plane.Origin = new Point3d(mark.X, mark.Y, 0);
            using (var text = PlanAnnotation(doc, mark.Text, plane, SampleHouse.LabelHeightMm))
            {
                if (text == null) throw new InvalidOperationException("Could not draw the label " + mark.Text + ".");
                var attr = new ObjectAttributes
                {
                    LayerIndex = label.Index,
                    Name = "sample-label-" + (++n).ToString("00", CultureInfo.InvariantCulture)
                };
                if (doc.Objects.AddText(text, attr) == Guid.Empty)
                    throw new InvalidOperationException("Could not draw the label " + mark.Text + ".");
            }
        }
    }

    static void AddSampleRect(RhinoDoc doc, Layer layer, string name, SampleHouse.Rect rect)
    {
        var curve = new PolylineCurve(new[]
        {
            new Point3d(rect.X0, rect.Y0, 0),
            new Point3d(rect.X1, rect.Y0, 0),
            new Point3d(rect.X1, rect.Y1, 0),
            new Point3d(rect.X0, rect.Y1, 0),
            new Point3d(rect.X0, rect.Y0, 0)
        });
        var attr = new ObjectAttributes { LayerIndex = layer.Index, Name = name };
        if (doc.Objects.AddCurve(curve, attr) == Guid.Empty)
            throw new InvalidOperationException("Could not draw " + name + ".");
    }

    static Layer SampleLayer(RhinoDoc doc, string name, Color color)
    {
        var index = doc.Layers.FindByFullPath(name, -1);
        if (index < 0) index = doc.Layers.Add(name, color);
        var layer = index < 0 ? null : doc.Layers.FindIndex(index);
        if (layer == null) throw new InvalidOperationException("Could not make the layer " + name + ".");
        return layer;
    }

    static void FrameSample(RhinoDoc doc)
    {
        var view = doc.Views.ActiveView;
        if (view == null) return;
        var box = new BoundingBox(
            new Point3d(-1000, -1000, 0),
            new Point3d(SampleHouse.WidthMm + 1000, SampleHouse.DepthMm + 1000, 0));
        view.ActiveViewport.ZoomBoundingBox(box);
    }
}
