using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoMCPPlugin.Functions;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Draw wall: click the corners of a wall run in the Top view, then one
    /// add_wall per segment inside one undo record. All the geometry is
    /// WallSketch; this only turns mouse points into sketch calls and draws
    /// what the sketch returns. Enter or a right-click finishes, Esc steps back
    /// one point (the last Esc cancels), C closes, a typed number is a length.
    /// </summary>
    public static class ForskDrawWall
    {
        public const string CommandName = "ForskDrawWall";

        /// <summary>UI thread. ownUndo is false when a record is already open.</summary>
        public static JObject RunOnUi(bool ownUndo)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return ForskTools.Fail("No active document.");
            try
            {
                ForskSection.ActivateTop(doc);
                var context = new RhinoMCPFunctions().ReadDrawContext(doc);
                var thickness = WallDraw.DefaultThickness(context.Thicknesses, RhinoMCPFunctions.LastDrawnThickness(doc));
                var sketch = Pick(WallDraw.TargetsFrom(context.Records), thickness);
                if (sketch == null) return ForskTools.Fail("Wall cancelled.");
                return Store(doc, sketch, ownUndo);
            }
            catch (Exception e)
            {
                return ForskTools.Fail(e.Message);
            }
        }

        /// <summary>The finished sketch, or null when the user cancelled or drew nothing.</summary>
        static WallSketch Pick(WallDraw.Targets targets, double thickness)
        {
            var sketch = new WallSketch(targets, thickness);
            while (true)
            {
                var getter = new DrawPoint(sketch);
                var result = getter.Get();
                switch (result)
                {
                    case GetResult.Point:
                        if (!sketch.Add(ToPt(getter.Point()), Shift(), out var why)) RhinoApp.WriteLine(why);
                        if (sketch.Closed) return sketch;
                        break;
                    case GetResult.Number:
                        if (!sketch.AddTyped(getter.Number(), out why)) RhinoApp.WriteLine(why);
                        break;
                    case GetResult.Option:
                        if (getter.TypeIndex(out var type))
                            sketch = new WallSketch(targets, type);
                        else if (!sketch.Close(out why))
                            RhinoApp.WriteLine(why);
                        else
                            return sketch;
                        break;
                    case GetResult.Nothing:
                        return sketch.Points.Count >= 2 ? sketch : null;
                    default:
                        if (!sketch.Back()) return null;
                        break;
                }
            }
        }

        static JObject Store(RhinoDoc doc, WallSketch sketch, bool ownUndo)
        {
            var segments = sketch.Segments();
            var calls = WallDraw.ToolCalls(segments, sketch.Thickness);
            if (calls.Count == 0) return ForskTools.Fail("Wall cancelled.");
            uint record = 0;
            ownUndo = ownUndo && !doc.UndoRecordingIsActive;
            if (ownUndo) record = doc.BeginUndoRecord("Forsk: draw_wall");
            var drawn = 0;
            var length = 0.0;
            string stopped = null;
            try
            {
                for (var i = 0; i < calls.Count; i++)
                {
                    var envelope = ForskTools.ExecuteAllowed("add_wall", calls[i]);
                    if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                    {
                        stopped = WallDrag.Plain(envelope?["message"]?.ToString());
                        break;
                    }
                    drawn++;
                    length += segments[i].Length;
                }
            }
            finally
            {
                if (ownUndo) doc.EndUndoRecord(record);
            }
            if (drawn == 0) return ForskTools.Fail(string.IsNullOrWhiteSpace(stopped) ? "No walls drawn." : stopped);
            RhinoMCPFunctions.RememberDrawnThickness(doc, sketch.Thickness);
            var message = WallDraw.Receipt(drawn, calls.Count, sketch.Thickness, length, sketch.Closed);
            if (stopped != null) message += " Stopped: " + stopped;
            return new JObject
            {
                ["status"] = "success",
                ["result"] = new JObject { ["message"] = message, ["count"] = drawn }
            };
        }

        static bool Shift()
        {
            return (Eto.Forms.Keyboard.Modifiers & Eto.Forms.Keys.Shift) == Eto.Forms.Keys.Shift;
        }

        static Pt ToPt(Point3d p) => new Pt(p.X, p.Y);

        static Point3d ToPoint(Pt p) => new Point3d(p.X, p.Y, 0);

        sealed class DrawPoint : GetPoint
        {
            static readonly System.Drawing.Color Blue = System.Drawing.Color.FromArgb(ForskWallHatch.Red, ForskWallHatch.Green, ForskWallHatch.Blue);
            static readonly System.Drawing.Color Red = System.Drawing.Color.FromArgb(196, 40, 40);
            static readonly System.Drawing.Color Grey = System.Drawing.Color.FromArgb(120, 120, 120);

            readonly WallSketch _sketch;
            readonly List<double> _types;
            int _typeOption = -1;

            public DrawPoint(WallSketch sketch)
            {
                _sketch = sketch;
                AcceptNothing(true);
                AcceptNumber(true, false);
                Constrain(Plane.WorldXY, false);
                if (sketch.Points.Count == 0)
                {
                    _types = WallDraw.Types(sketch.Thickness);
                    var index = _types.FindIndex(t => Math.Abs(t - sketch.Thickness) < 0.5);
                    _typeOption = AddOptionList("WallType", _types.Select(WallDraw.Mm).ToList(), Math.Max(index, 0));
                    SetCommandPrompt("Start of the wall, " + WallDraw.TypeName(sketch.Thickness) + " thick");
                    return;
                }
                SetBasePoint(ToPoint(sketch.Points[sketch.Points.Count - 1]), true);
                if (sketch.Points.Count >= 3) AddOption("Close");
                SetCommandPrompt("Next point or length. Enter finishes, Esc steps back");
            }

            /// <summary>True when the option was the wall type; type is its thickness.</summary>
            public bool TypeIndex(out double type)
            {
                type = _sketch.Thickness;
                var option = Option();
                if (option == null || option.Index != _typeOption || _types == null) return false;
                var index = option.CurrentListOptionIndex;
                if (index < 0 || index >= _types.Count) return false;
                type = _types[index];
                return true;
            }

            protected override void OnDynamicDraw(GetPointDrawEventArgs e)
            {
                var scale = PixelsPerMm(e.Viewport, e.CurrentPoint);
                if (scale > 0) _sketch.Reach = Math.Max(20, Math.Min(800, 14.0 / scale));
                var preview = _sketch.Hover(ToPt(e.CurrentPoint), Shift());

                foreach (var segment in _sketch.Segments())
                    Outline(e, WallDraw.Band(segment.From, segment.To, _sketch.Thickness), Blue);
                if (preview.Band != null)
                    Outline(e, preview.Band, preview.Valid ? Blue : Red);

                var color = preview.Valid ? Blue : Red;
                if (preview.HasFrom && preview.Length > 0.5)
                    Dimension(e, preview, color);
                if (preview.Kind != WallDraw.SnapKind.None && preview.Kind != WallDraw.SnapKind.Angle)
                    e.Display.DrawPoint(ToPoint(preview.Point), PointStyle.X, 6, color);
                base.OnDynamicDraw(e);
            }

            static void Outline(GetPointDrawEventArgs e, List<Pt> ring, System.Drawing.Color color)
            {
                var pts = ring.Select(ToPoint).ToList();
                pts.Add(pts[0]);
                e.Display.DrawPolyline(pts, color, 2);
            }

            static void Dimension(GetPointDrawEventArgs e, WallSketch.Preview preview, System.Drawing.Color color)
            {
                var a = ToPoint(preview.From);
                var b = ToPoint(preview.Point);
                var dir = b - a;
                var mid = new Point3d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, 0);
                e.Display.DrawLine(a, b, color, 2);
                e.Display.DrawArrowHead(b, dir, color, 12, 0);
                e.Display.DrawArrowHead(a, -dir, color, 12, 0);
                e.Display.Draw2dText(preview.Valid ? preview.Label : preview.Why, color, mid, true, 14);
            }

            static double PixelsPerMm(RhinoViewport viewport, Point3d at)
            {
                var toScreen = viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Screen);
                var p = at;
                var q = new Point3d(at.X + 1000, at.Y, at.Z);
                p.Transform(toScreen);
                q.Transform(toScreen);
                return new Point2d(p.X, p.Y).DistanceTo(new Point2d(q.X, q.Y)) / 1000.0;
            }
        }
    }
}
