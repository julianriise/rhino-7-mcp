using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Draw area: click a room's corners in the Top view, one by one, on the
    /// floor's height (Rhino's object snaps find the wall corners). Enter or C
    /// closes, Esc steps back one corner (the last Esc cancels). The outline
    /// goes to add_room_area; Redraw area replaces the selected room with it.
    /// </summary>
    public static class ForskDrawArea
    {
        /// <summary>UI thread. replace: redraw the one selected room.</summary>
        public static JObject RunOnUi(bool ownUndo, bool replace)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return ForskTools.Fail("No active document.");
            try
            {
                var functions = new RhinoMCPFunctions();
                string roomId = null;
                if (replace)
                {
                    roomId = RhinoMCPFunctions.SelectedRoomId(doc);
                    if (roomId == null) return ForskTools.Fail("Select one room, then Redraw area.");
                }
                ForskSection.ActivateTop(doc);
                var z = functions.AreaHeight(doc, roomId);
                doc.Objects.UnselectAll();
                var corners = Pick(z);
                if (corners == null) return ForskTools.Fail("Area cancelled.");
                var points = new JArray();
                foreach (var p in corners) points.Add(new JArray(p.X, p.Y));
                var args = new JObject { ["points"] = points };
                if (replace)
                {
                    args["replace"] = true;
                    args["id"] = roomId;
                }
                return ForskTools.Write("add_room_area", () => functions.AddDrawnRoomArea(args));
            }
            catch (Exception e)
            {
                return ForskTools.Fail(e.Message);
            }
        }

        /// <summary>The corners clicked, or null when the user cancelled.</summary>
        static List<Point3d> Pick(double z)
        {
            var corners = new List<Point3d>();
            var plane = new Plane(new Point3d(0, 0, z), Vector3d.ZAxis);
            while (true)
            {
                var getter = new AreaPoint(corners, plane);
                switch (getter.Get())
                {
                    case GetResult.Point:
                        var p = getter.Point();
                        p.Z = z;
                        if (corners.Count >= 3 && p.DistanceTo(corners[0]) < 1.0) return corners;
                        corners.Add(p);
                        break;
                    case GetResult.Option:
                    case GetResult.Nothing:
                        if (corners.Count >= 3) return corners;
                        if (corners.Count == 0) return null;
                        RhinoApp.WriteLine(RoomAreaPlan.TooFew);
                        break;
                    default:
                        if (corners.Count == 0) return null;
                        corners.RemoveAt(corners.Count - 1);
                        break;
                }
            }
        }

        sealed class AreaPoint : GetPoint
        {
            static readonly System.Drawing.Color Blue = System.Drawing.Color.FromArgb(ForskWallHatch.Red, ForskWallHatch.Green, ForskWallHatch.Blue);

            readonly List<Point3d> _corners;

            public AreaPoint(List<Point3d> corners, Plane plane)
            {
                _corners = corners;
                AcceptNothing(true);
                Constrain(plane, false);
                if (corners.Count == 0)
                {
                    SetCommandPrompt("First corner of the room");
                    return;
                }
                SetBasePoint(corners[corners.Count - 1], true);
                if (corners.Count >= 3)
                {
                    AddOption("Close");
                    SetCommandPrompt("Next corner. Enter or C closes the area, Esc steps back");
                }
                else SetCommandPrompt("Next corner. Esc steps back");
            }

            protected override void OnDynamicDraw(GetPointDrawEventArgs e)
            {
                var pts = _corners.ToList();
                var at = e.CurrentPoint;
                if (pts.Count > 0) at.Z = pts[0].Z;
                pts.Add(at);
                if (pts.Count >= 3) pts.Add(pts[0]);
                if (pts.Count >= 2) e.Display.DrawPolyline(pts, Blue, 2);
                foreach (var c in _corners)
                    e.Display.DrawPoint(c, PointStyle.RoundSimple, 5, Blue);
                base.OnDynamicDraw(e);
            }
        }
    }
}
