using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// R5: Add stair from the bar with nothing picked. Top view, the foot of
    /// the stair, then a point it climbs toward (an arrow follows the mouse),
    /// then add_stair from and to in one undo record. Esc adds nothing.
    /// </summary>
    public static class ForskStair
    {
        /// <summary>UI thread. ownUndo is false when a record is already open.</summary>
        public static JObject RunOnUi(bool ownUndo)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return ForskTools.Fail("No active document.");
            try
            {
                ForskSection.ActivateTop(doc);
                var first = new GetPoint();
                first.SetCommandPrompt("Foot of the stair");
                first.Constrain(Plane.WorldXY, false);
                if (first.Get() != GetResult.Point) return ForskTools.Fail("Stair cancelled.");
                var foot = first.Point();
                var second = new WayUp(foot);
                if (second.Get() != GetResult.Point) return ForskTools.Fail("Stair cancelled.");
                var up = second.Point();
                return Store(doc, foot, up, ownUndo);
            }
            catch (Exception e)
            {
                return ForskTools.Fail(e.Message);
            }
        }

        static JObject Store(RhinoDoc doc, Point3d foot, Point3d up, bool ownUndo)
        {
            uint record = 0;
            ownUndo = ownUndo && !doc.UndoRecordingIsActive;
            if (ownUndo) record = doc.BeginUndoRecord("Forsk: add_stair");
            ForskCalls.Enter();
            try
            {
                var added = new RhinoMCPFunctions().AddStair(new JObject
                {
                    ["from"] = new JArray(foot.X, foot.Y),
                    ["to"] = new JArray(up.X, up.Y)
                });
                return new JObject { ["status"] = "success", ["result"] = added };
            }
            catch (Exception e)
            {
                return ForskTools.Fail(e.Message);
            }
            finally
            {
                ForskCalls.Exit();
                if (ownUndo) doc.EndUndoRecord(record);
            }
        }

        /// <summary>The way up: an arrow from the foot to the mouse.</summary>
        sealed class WayUp : GetPoint
        {
            readonly Point3d _foot;

            public WayUp(Point3d foot)
            {
                _foot = foot;
                SetCommandPrompt("The way up");
                SetBasePoint(_foot, false);
                Constrain(Plane.WorldXY, false);
            }

            protected override void OnDynamicDraw(GetPointDrawEventArgs e)
            {
                var to = new Point3d(e.CurrentPoint.X, e.CurrentPoint.Y, _foot.Z);
                if (_foot.DistanceTo(to) > 1)
                    e.Display.DrawArrow(new Line(_foot, to), System.Drawing.Color.Black);
                base.OnDynamicDraw(e);
            }
        }
    }
}
