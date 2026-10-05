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
    /// Draw stair: in the Top view, click the foot of the stair (it snaps flush
    /// to a wall face), then move to set the way up and the run. The preview is
    /// the plan symbol from the floor to floor height; the click, or a typed
    /// run, adds the stair with add_stair in one undo record. The sizes and
    /// the snapping are StairDraw; this only feeds it mouse points. W sets
    /// the width. Esc adds nothing.
    /// </summary>
    public static class ForskStair
    {
        public const string CommandName = "ForskDrawStair";
        /// <summary>add_stair refuses a width under 500.</summary>
        const double WidthLo = 500;

        static readonly System.Drawing.Color Blue = System.Drawing.Color.FromArgb(ForskWallHatch.Red, ForskWallHatch.Green, ForskWallHatch.Blue);
        static readonly System.Drawing.Color Red = System.Drawing.Color.FromArgb(196, 40, 40);

        /// <summary>UI thread. ownUndo is false when a record is already open.</summary>
        public static JObject RunOnUi(bool ownUndo)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return ForskTools.Fail("No active document.");
            try
            {
                ForskSection.ActivateTop(doc);
                var context = new RhinoMCPFunctions().ReadDrawContext(doc);
                var faces = StairDraw.Faces(context.Records);
                var setup = new StairDraw.Setup { Rise = context.Rise };
                var width = new OptionDouble(Stairs.WidthDefault, WidthLo, Stairs.SizeHi);
                var draft = Pick(faces, setup, width);
                if (draft == null) return ForskTools.Fail("Stair cancelled.");
                return Store(doc, draft, ownUndo);
            }
            catch (Exception e)
            {
                return ForskTools.Fail(e.Message);
            }
        }

        /// <summary>The finished stair, or null when the user cancelled.</summary>
        static StairDraw.Draft Pick(List<StairDraw.Face> faces, StairDraw.Setup setup, OptionDouble width)
        {
            StairDraw.FootHit foot = null;
            while (foot == null)
            {
                setup.Width = width.CurrentValue;
                var first = new FootPoint(faces, setup, width);
                switch (first.Get())
                {
                    case GetResult.Point:
                        foot = StairDraw.SnapFoot(faces, ToPt(first.Point()), first.Reach, Shift());
                        break;
                    case GetResult.Option:
                        break;
                    default:
                        return null;
                }
            }
            while (true)
            {
                setup.Width = width.CurrentValue;
                // The foot stays on the face. Plan shifts it by half the current width when the climb runs along the face.
                var way = new WayUp(foot, setup, width);
                var result = way.Get();
                double? typed = null;
                if (result == GetResult.Option) continue;
                if (result == GetResult.Number) typed = way.Number();
                else if (result != GetResult.Point) return null;
                var draft = StairDraw.Plan(setup, foot, ToPt(way.Point()), Shift(), typed);
                if (draft.Valid) return draft;
                RhinoApp.WriteLine(draft.Why);
            }
        }

        static JObject Store(RhinoDoc doc, StairDraw.Draft draft, bool ownUndo)
        {
            uint record = 0;
            ownUndo = ownUndo && !doc.UndoRecordingIsActive;
            if (ownUndo) record = doc.BeginUndoRecord("Forsk: draw_stair");
            try
            {
                return ForskTools.ExecuteAllowed("add_stair", StairDraw.ToolParams(draft));
            }
            finally
            {
                if (ownUndo) doc.EndUndoRecord(record);
            }
        }

        static bool Shift()
        {
            return (Eto.Forms.Keyboard.Modifiers & Eto.Forms.Keys.Shift) == Eto.Forms.Keys.Shift;
        }

        static Pt ToPt(Point3d p) => new Pt(p.X, p.Y);

        static Point3d ToPoint(Pt p) => new Point3d(p.X, p.Y, 0);

        /// <summary>mm per pixel at a point, so the reach is the same on screen at any zoom.</summary>
        static double ReachAt(RhinoViewport viewport, Point3d at)
        {
            var toScreen = viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Screen);
            var p = at;
            var q = new Point3d(at.X + 1000, at.Y, at.Z);
            p.Transform(toScreen);
            q.Transform(toScreen);
            var perMm = new Point2d(p.X, p.Y).DistanceTo(new Point2d(q.X, q.Y)) / 1000.0;
            return perMm <= 0 ? StairDraw.ReachMm : Math.Max(20, Math.Min(800, 14.0 / perMm));
        }

        /// <summary>The foot: the snap shows as a cross, and the footprint's first step as a bar across the width.</summary>
        sealed class FootPoint : GetPoint
        {
            readonly List<StairDraw.Face> _faces;
            readonly StairDraw.Setup _setup;

            public double Reach { get; private set; } = StairDraw.ReachMm;

            public FootPoint(List<StairDraw.Face> faces, StairDraw.Setup setup, OptionDouble width)
            {
                _faces = faces;
                _setup = setup;
                Constrain(Plane.WorldXY, false);
                AddOptionDouble("Width", ref width);
                SetCommandPrompt("Foot of the stair, " + Stairs.Mm(setup.Width) + " wide");
            }

            protected override void OnDynamicDraw(GetPointDrawEventArgs e)
            {
                Reach = ReachAt(e.Viewport, e.CurrentPoint);
                var hit = StairDraw.SnapFoot(_faces, ToPt(e.CurrentPoint), Reach, Shift());
                e.Display.DrawPoint(ToPoint(hit.Foot), PointStyle.X, 6, Blue);
                if (hit.Face != null)
                    e.Display.DrawLine(ToPoint(hit.Face.A), ToPoint(hit.Face.B), Blue, 3);
                base.OnDynamicDraw(e);
            }
        }

        /// <summary>The way up: the live plan symbol and a blue dimension along the run.</summary>
        sealed class WayUp : GetPoint
        {
            readonly StairDraw.FootHit _foot;
            readonly StairDraw.Setup _setup;

            public WayUp(StairDraw.FootHit foot, StairDraw.Setup setup, OptionDouble width)
            {
                _foot = foot;
                _setup = setup;
                SetBasePoint(ToPoint(foot.Foot), true);
                Constrain(Plane.WorldXY, false);
                AcceptNumber(true, false);
                AddOptionDouble("Width", ref width);
                SetCommandPrompt("The way up: click where the stair ends, or type its length");
            }

            protected override void OnDynamicDraw(GetPointDrawEventArgs e)
            {
                var draft = StairDraw.Plan(_setup, _foot, ToPt(e.CurrentPoint), Shift());
                if (draft.Flight == null)
                {
                    e.Display.Draw2dText(draft.Why, Red, ToPoint(_foot.Foot), true, 14);
                    base.OnDynamicDraw(e);
                    return;
                }
                var color = draft.Valid ? Blue : Red;
                var ring = draft.Footprint.Select(ToPoint).ToList();
                ring.Add(ring[0]);
                e.Display.DrawPolyline(ring, color, 2);
                foreach (var piece in StairDraw.Symbol(draft)) Draw(e, piece, color);
                Dimension(e, draft, color);
                base.OnDynamicDraw(e);
            }

            static void Draw(GetPointDrawEventArgs e, StairDraw.Piece piece, System.Drawing.Color color)
            {
                switch (piece.Shape)
                {
                    case "line":
                        if (piece.Dashed) e.Display.DrawDottedLine(ToPoint(piece.A), ToPoint(piece.B), color);
                        else e.Display.DrawLine(ToPoint(piece.A), ToPoint(piece.B), color, 1);
                        break;
                    case "dot":
                        e.Display.DrawCircle(new Circle(ToPoint(piece.A), Math.Max(1, piece.Radius)), color, 2);
                        break;
                    case "text":
                        e.Display.Draw2dText(piece.Text, color, ToPoint(piece.A), true, 12);
                        break;
                    default:
                        break;
                }
            }

            static void Dimension(GetPointDrawEventArgs e, StairDraw.Draft draft, System.Drawing.Color color)
            {
                // Beside the stair, on the side away from a wall, so the line clears the walking line and the label.
                var side = draft.Against == "left" ? -1.0 : 1.0;
                var offset = draft.Flight.Width / 2.0 + 200;
                var left = new Vector3d(-draft.Dir.Y, draft.Dir.X, 0) * (side * offset);
                var a = ToPoint(draft.Foot) + left;
                var b = a + new Vector3d(draft.Dir.X, draft.Dir.Y, 0) * draft.Flight.Run;
                var mid = new Point3d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, 0);
                var dir = b - a;
                if (dir.Length > 0.5)
                {
                    e.Display.DrawLine(a, b, color, 2);
                    e.Display.DrawArrowHead(b, dir, color, 12, 0);
                    e.Display.DrawArrowHead(a, -dir, color, 12, 0);
                }
                e.Display.Draw2dText(draft.Valid ? draft.Dimension : draft.Why, color, mid, true, 14);
            }
        }
    }
}
