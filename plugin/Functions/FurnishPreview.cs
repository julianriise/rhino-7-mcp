using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// FU.7: the two furnish layouts as ghosts before anything is placed: the
/// usual one in blue, the creative one in orange, each piece as its plan
/// symbol on its floor. Nothing is added to the document. Placing a layout
/// or cancelling the card takes the ghosts away.
/// </summary>
public static class FurnishPreview
{
    public static readonly Color Usual = Color.FromArgb(41, 72, 245);
    public static readonly Color Other = Color.FromArgb(230, 120, 40);

    sealed class Ghost
    {
        public readonly List<Curve> Curves = new List<Curve>();
        public Color Colour;
    }

    sealed class Conduit : DisplayConduit
    {
        public List<Ghost> Ghosts = new List<Ghost>();

        protected override void DrawForeground(DrawEventArgs e)
        {
            try
            {
                foreach (var ghost in Ghosts)
                    foreach (var curve in ghost.Curves)
                        e.Display.DrawCurve(curve, ghost.Colour, 2);
            }
            catch (Exception)
            {
                // A ghost that will not draw never stops the view.
            }
        }

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            foreach (var ghost in Ghosts)
                foreach (var curve in ghost.Curves)
                    e.IncludeBoundingBox(curve.GetBoundingBox(false));
        }
    }

    static Conduit conduit;

    public static bool Showing => conduit != null && conduit.Enabled;

    /// <summary>Each layout's pieces in its colour, a little above the floor so the slab does not hide them.</summary>
    public static void Show(RhinoDoc doc, IEnumerable<(IEnumerable<Furnish.Item> Items, Color Colour)> layouts, double floor)
    {
        Hide(doc);
        var ghosts = new List<Ghost>();
        foreach (var (items, colour) in layouts)
        {
            var ghost = new Ghost { Colour = colour };
            foreach (var item in items)
                foreach (var mark in Furniture.Plan(item.Piece, Furniture.DetailScale))
                {
                    Point3d At(double x, double y)
                    {
                        var p = item.Frame.ToWorld(x, y);
                        return new Point3d(p.X, p.Y, floor + 10);
                    }
                    if (mark.Shape == "line")
                        ghost.Curves.Add(new LineCurve(At(mark.X0, mark.Y0), At(mark.X1, mark.Y1)));
                    else
                    {
                        double Rad(double a) => a * Math.PI / 180;
                        var mid = (mark.A0 + mark.A1) / 2;
                        ghost.Curves.Add(new ArcCurve(new Arc(
                            At(mark.Cx + mark.R * Math.Cos(Rad(mark.A0)), mark.Cy + mark.R * Math.Sin(Rad(mark.A0))),
                            At(mark.Cx + mark.R * Math.Cos(Rad(mid)), mark.Cy + mark.R * Math.Sin(Rad(mid))),
                            At(mark.Cx + mark.R * Math.Cos(Rad(mark.A1)), mark.Cy + mark.R * Math.Sin(Rad(mark.A1))))));
                    }
                }
            ghosts.Add(ghost);
        }
        conduit = new Conduit { Ghosts = ghosts, Enabled = true };
        doc?.Views.Redraw();
    }

    public static void Hide(RhinoDoc doc)
    {
        if (conduit == null) return;
        conduit.Enabled = false;
        conduit = null;
        doc?.Views.Redraw();
    }
}
