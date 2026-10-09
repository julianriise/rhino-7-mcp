using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Draw area: click a room's corners in the Top view, one by one, on the
    /// floor's height, usually along the walls' inner faces (Rhino's object
    /// snaps find the wall corners). Enter or C closes, Esc steps back one
    /// corner (the last Esc cancels). Or use a closed curve the user drew: one
    /// picked before Draw area, or picked with the Curve option; it becomes
    /// the area. While the tool runs the view shows the walls only. The
    /// outline goes to add_room_area; Redraw area replaces the selected room.
    /// </summary>
    public static class ForskDrawArea
    {
        /// <summary>True while Draw area runs: Forsk's own overlays (roof, room colours) stay off too.</summary>
        internal static bool WallsOnly { get; private set; }

        /// <summary>UI thread. replace: redraw the one selected room.</summary>
        public static JObject RunOnUi(bool ownUndo, bool replace)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return ForskTools.Fail("No active document.");
            var focus = new WallsOnlyConduit();
            try
            {
                var functions = new RhinoMCPFunctions();
                string roomId = null;
                if (replace)
                {
                    roomId = RhinoMCPFunctions.SelectedRoomId(doc);
                    if (roomId == null) return ForskTools.Fail("Select one room, then Redraw area.");
                }
                var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
                var z = functions.AreaHeight(doc, roomId);
                // A closed curve the user drew and picked first is the area (pick-then-act).
                var source = replace ? null : PickedCurve(doc);
                // One pass: the walls only, until the tool ends however it ends.
                WallsOnly = true;
                focus.Enabled = true;
                ForskSection.ActivateTop(doc);
                List<Point3d> corners;
                if (source != null) corners = Corners(source.Geometry as Curve, z, tol);
                else
                {
                    doc.Objects.UnselectAll();
                    corners = Pick(doc, z, tol, out source);
                }
                if (corners == null) return ForskTools.Fail("Area cancelled.");
                var points = new JArray();
                foreach (var p in corners) points.Add(new JArray(p.X, p.Y));
                var args = new JObject { ["points"] = points };
                if (replace)
                {
                    args["replace"] = true;
                    args["id"] = roomId;
                }
                var sourceId = source?.Id ?? Guid.Empty;
                return ForskTools.Write("add_room_area", () =>
                {
                    // The user's curve becomes the area, in the same undo record.
                    if (sourceId != Guid.Empty) doc.Objects.Delete(sourceId, true);
                    return functions.AddDrawnRoomArea(args);
                });
            }
            catch (Exception e)
            {
                return ForskTools.Fail(e.Message);
            }
            finally
            {
                focus.Enabled = false;
                WallsOnly = false;
                doc.Views.Redraw();
            }
        }

        /// <summary>The one selected object when it is a closed curve the user drew, else null.</summary>
        static RhinoObject PickedCurve(RhinoDoc doc)
        {
            var selected = doc.Objects.GetSelectedObjects(false, false).ToList();
            if (selected.Count != 1) return null;
            return IsUserOutline(selected[0]) ? selected[0] : null;
        }

        static bool IsUserOutline(RhinoObject obj)
        {
            return obj?.Geometry is Curve curve && curve.IsClosed
                && obj.Attributes?.GetUserString("forsk:generated") != "1";
        }

        /// <summary>A closed curve's corners on the floor's height: a polyline's own, else the curve cut into short lines.</summary>
        static List<Point3d> Corners(Curve curve, double z, double tol)
        {
            if (curve == null || !curve.IsClosed) return null;
            Polyline poly;
            if (!curve.TryGetPolyline(out poly))
            {
                var lines = curve.ToPolyline(tol, RhinoMath.ToRadians(5.0), 0, 0);
                if (lines == null || !lines.TryGetPolyline(out poly)) return null;
            }
            var corners = poly.Select(p => new Point3d(p.X, p.Y, z)).ToList();
            if (corners.Count > 1 && corners[0].DistanceTo(corners[corners.Count - 1]) <= tol) corners.RemoveAt(corners.Count - 1);
            return corners.Count >= 3 ? corners : null;
        }

        /// <summary>The corners clicked, or a drawn curve's (source set), or null when the user cancelled.</summary>
        static List<Point3d> Pick(RhinoDoc doc, double z, double tol, out RhinoObject source)
        {
            source = null;
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
                    case GetResult.Option when getter.CurveOption >= 0 && getter.OptionIndex() == getter.CurveOption:
                        var picked = PickCurve();
                        var outline = picked == null ? null : Corners(picked.Geometry as Curve, z, tol);
                        if (outline != null)
                        {
                            source = picked;
                            return outline;
                        }
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

        /// <summary>One click on a closed curve the user drew. Null when cancelled.</summary>
        static RhinoObject PickCurve()
        {
            var go = new GetObject();
            go.SetCommandPrompt("Pick the closed curve that outlines the room");
            go.GeometryFilter = ObjectType.Curve;
            go.GeometryAttributeFilter = GeometryAttributeFilter.ClosedCurve;
            go.SubObjectSelect = false;
            go.GroupSelect = false;
            go.EnablePreSelect(false, true);
            go.SetCustomGeometryFilter((obj, geometry, component) => IsUserOutline(obj));
            return go.Get() == GetResult.Object ? go.Object(0).Object() : null;
        }

        sealed class AreaPoint : GetPoint
        {
            static readonly System.Drawing.Color Blue = System.Drawing.Color.FromArgb(ForskWallHatch.Red, ForskWallHatch.Green, ForskWallHatch.Blue);

            readonly List<Point3d> _corners;

            /// <summary>The Curve option's index on the first corner, else -1.</summary>
            public int CurveOption { get; } = -1;

            public AreaPoint(List<Point3d> corners, Plane plane)
            {
                _corners = corners;
                AcceptNothing(true);
                Constrain(plane, false);
                if (corners.Count == 0)
                {
                    CurveOption = AddOption("Curve");
                    SetCommandPrompt("First corner of the room, on a wall's inner face, or Curve to use a closed curve");
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

        /// <summary>
        /// The walls only: every other Forsk object is culled from the views
        /// while Draw area runs. Nothing in the document changes (no layer,
        /// no hidden flag, no undo), so ending the tool, by Esc or an error
        /// too, is just switching the conduit off.
        /// </summary>
        sealed class WallsOnlyConduit : DisplayConduit
        {
            public WallsOnlyConduit()
            {
                SpaceFilter = ActiveSpace.ModelSpace;
            }

            protected override void ObjectCulling(CullObjectEventArgs e)
            {
                var attr = e?.RhinoObject?.Attributes;
                if (attr == null) return;
                if (RoomAreaPlan.HiddenWhileDrawing(attr.GetUserString("forsk:generated"), attr.GetUserString("forsk:kind")))
                    e.CullObject = true;
            }
        }
    }
}
