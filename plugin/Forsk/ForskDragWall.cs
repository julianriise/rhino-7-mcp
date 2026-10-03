using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Commands;
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
    /// Drag one straight wall along its normal. The preview and the dimension
    /// are drawn and then dropped. Release calls move_wall once. Esc changes nothing.
    /// </summary>
    public static class ForskDragWall
    {
        public const string CommandName = "ForskDragWall";
        const double Reach = 100000;

        public sealed class Outcome
        {
            public bool Cancelled;
            public string Line;
            public string Toward;
            public double Mm;
            public string Label;
            public string OuterSide;
            public bool Norwegian;
        }

        /// <summary>Command line. Pick, then one move_wall, which opens its own undo record.</summary>
        public static Result Run(RhinoDoc doc)
        {
            var outcome = Pick(doc);
            if (outcome.Cancelled)
            {
                if (!string.IsNullOrEmpty(outcome.Line)) RhinoApp.WriteLine(outcome.Line);
                return Result.Cancel;
            }
            if (string.IsNullOrEmpty(outcome.Toward))
            {
                if (!string.IsNullOrEmpty(outcome.Line)) RhinoApp.WriteLine(outcome.Line);
                return Result.Success;
            }
            var envelope = ForskTools.ExecuteAllowed("move_wall", new JObject
            {
                ["toward"] = outcome.Toward,
                ["distance_mm"] = outcome.Mm
            });
            RhinoApp.WriteLine(Words(outcome, envelope));
            return string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)
                ? Result.Success
                : Result.Failure;
        }

        /// <summary>UI thread. Does not move the wall. The window commits with its own record.</summary>
        public static Outcome Pick(RhinoDoc doc)
        {
            var nb = ForskSpeech.Norwegian;
            var outcome = new Outcome { Norwegian = nb };
            if (doc == null)
            {
                outcome.Cancelled = true;
                outcome.Line = "No active document.";
                return outcome;
            }
            var host = Host(doc, nb);
            if (host == null)
            {
                outcome.Cancelled = true;
                outcome.Line = Text("wall.drag.cancel", nb);
                return outcome;
            }
            doc.Objects.UnselectAll();
            host.Select(true);
            doc.Views.Redraw();

            var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
            RhinoMCPFunctions.SelectedDrag picked;
            try
            {
                picked = new RhinoMCPFunctions().SelectedWall(doc);
            }
            catch (Exception e)
            {
                outcome.Line = WallDrag.Plain(e.Message);
                return outcome;
            }
            var why = WallDrag.Check(picked.Graph, picked.Rings, picked.Run, tol, nb);
            if (why != null)
            {
                outcome.Line = why;
                return outcome;
            }
            outcome.Label = picked.Label;
            outcome.OuterSide = Outer(picked.Graph, picked.Run, tol);
            new DragPoint(picked, outcome.OuterSide, tol, nb).Run(outcome);
            return outcome;
        }

        /// <summary>The D3 sentence, or the tool's refusal with the point taken off.</summary>
        public static string Words(Outcome outcome, JObject envelope)
        {
            if (outcome == null) return "";
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                return WallDrag.Plain(envelope?["message"]?.ToString());
            var followed = (envelope["result"]?["followed"] as JArray)?.Count ?? 0;
            return WallDrag.Receipt(outcome.Label, outcome.Toward, outcome.Mm, outcome.OuterSide, outcome.Norwegian, followed);
        }

        static RhinoObject Host(RhinoDoc doc, bool nb)
        {
            var walls = RhinoMCPFunctions.ListSelected(doc).Where(o => RhinoMCPFunctions.IsHostWall(doc, o)).ToList();
            if (walls.Count == 1) return walls[0];
            var go = new GetObject();
            go.SetCommandPrompt(Text("wall.drag.pick", nb));
            go.GeometryFilter = ObjectType.AnyObject;
            go.SubObjectSelect = false;
            go.GroupSelect = false;
            go.EnablePreSelect(false, true);
            go.SetCustomGeometryFilter((obj, geometry, component) => RhinoMCPFunctions.IsHostWall(doc, obj));
            if (go.Get() != GetResult.Object) return null;
            return go.Object(0).Object();
        }

        static string Outer(WallJoins.Graph graph, WallEdit.Run run, double tol)
        {
            if (graph?.Names == null || run == null) return null;
            var index = graph.Find(run, tol);
            if (index < 0 || index >= graph.Names.Count) return null;
            return WallFollowPlan.IsSide(graph.Names[index], out var side) ? side : null;
        }

        static string Text(string key, bool nb) => ForskText.Get(nb && ForskText.Has(key + ".nb") ? key + ".nb" : key);

        /// <summary>The rubber band is the snapped move, not the free mouse point.</summary>
        sealed class DragPoint : GetPoint
        {
            readonly RhinoMCPFunctions.SelectedDrag _pick;
            readonly WallEdit.Run _run;
            readonly Pt _origin;
            readonly int _outward;
            readonly bool _nb;
            readonly double _tol;
            readonly List<Point3d[]> _old;
            readonly string[] _stepNames = { "10", "50", "100" };
            int _step = WallDrag.DefaultStep;
            int _mouseSign;
            double _shown = double.NaN;
            int _shownStep = -1;
            string _prompt;
            bool _refused;
            string _why;
            List<Point3d[]> _moved;
            Point3d[] _red;

            public DragPoint(RhinoMCPFunctions.SelectedDrag pick, string outerSide, double tol, bool nb)
            {
                _pick = pick;
                _run = pick.Run;
                _outward = WallDrag.Outward(outerSide, _run);
                _nb = nb;
                _tol = tol;
                _origin = WallJoins.Middle(_run);
                _old = Loops(pick.Graph.Shape);
                SetBasePoint(new Point3d(_origin.X, _origin.Y, 0), false);
                var n = _run.Normal;
                Constrain(
                    new Point3d(_origin.X - n.X * Reach, _origin.Y - n.Y * Reach, 0),
                    new Point3d(_origin.X + n.X * Reach, _origin.Y + n.Y * Reach, 0));
                AcceptNumber(true, true);
                AcceptNothing(false);
                AddStep();
                Say(WallDrag.FormatDimension(0, _outward, _nb));
            }

            public void Run(Outcome outcome)
            {
                var result = Ask();
                if (result == GetResult.Point && Math.Abs(Along(Point())) < 1)
                    result = Ask();
                if (result == GetResult.Cancel || result == GetResult.Nothing)
                {
                    outcome.Cancelled = true;
                    outcome.Line = Text("wall.drag.cancel", _nb);
                    return;
                }
                double by;
                if (result == GetResult.Number)
                {
                    var sign = _mouseSign != 0 ? _mouseSign : _outward;
                    by = WallDrag.Typed(Number(), sign);
                    if (Math.Abs(by) < 1)
                    {
                        outcome.Line = WallDrag.NotMoved(_nb);
                        return;
                    }
                }
                else if (result == GetResult.Point)
                {
                    by = WallDrag.Snap(Along(Point()), _step);
                    if (Math.Abs(by) < _step)
                    {
                        outcome.Line = WallDrag.NotMoved(_nb);
                        return;
                    }
                }
                else
                {
                    outcome.Cancelled = true;
                    outcome.Line = Text("wall.drag.cancel", _nb);
                    return;
                }
                if (!WallJoins.TryMove(_pick.Records, _pick.Graph, _run, by, _tol, out _, out var why))
                {
                    outcome.Line = WallDrag.Plain(why);
                    return;
                }
                outcome.Toward = WallEdit.Heading(_run, by);
                outcome.Mm = Math.Abs(by);
            }

            GetResult Ask()
            {
                while (true)
                {
                    var result = Get(true);
                    if (result != GetResult.Option) return result;
                    var index = Option().CurrentListOptionIndex;
                    if (index >= 0 && index < WallDrag.Steps.Length) _step = WallDrag.Steps[index];
                    ClearCommandOptions();
                    AddStep();
                }
            }

            void AddStep()
            {
                var index = Array.IndexOf(WallDrag.Steps, _step);
                if (index < 0) index = 0;
                AddOptionList("Step", _stepNames, index);
            }

            double Along(Point3d point) => WallDrag.Along(_origin, new Pt(point.X, point.Y), _run.Normal);

            protected override void OnDynamicDraw(GetPointDrawEventArgs e)
            {
                var snapped = WallDrag.Snap(Along(e.CurrentPoint), _step);
                if (snapped > 0) _mouseSign = 1;
                else if (snapped < 0) _mouseSign = -1;
                if (snapped != _shown || _step != _shownStep)
                {
                    _shown = snapped;
                    _shownStep = _step;
                    Prepare(snapped);
                }
                var blue = System.Drawing.Color.FromArgb(ForskWallHatch.Red, ForskWallHatch.Green, ForskWallHatch.Blue);
                var grey = System.Drawing.Color.FromArgb(160, 160, 160);
                foreach (var loop in _old) e.Display.DrawPolyline(loop, grey, 1);
                if (_moved != null)
                    foreach (var loop in _moved) e.Display.DrawPolyline(loop, blue, 2);
                if (_red != null)
                    e.Display.DrawPolyline(_red, System.Drawing.Color.FromArgb(196, 40, 40), 2);
                var label = WallDrag.FormatDimension(snapped, _outward, _nb);
                Say(_refused ? _why : label);
                DrawDimension(e, snapped, label, blue);
                base.OnDynamicDraw(e);
            }

            void Prepare(double by)
            {
                _refused = false;
                _why = null;
                _moved = null;
                _red = null;
                if (Math.Abs(by) < _step) return;
                if (WallJoins.TryMove(_pick.Records, _pick.Graph, _run, by, _tol, out var moved, out var why))
                    _moved = Loops(moved.Shape);
                else
                {
                    _refused = true;
                    _why = WallDrag.Plain(why);
                    _red = Shift(WallEdit.Band(_run), by);
                }
            }

            void DrawDimension(GetPointDrawEventArgs e, double by, string label, System.Drawing.Color color)
            {
                var ends = WallDrag.Measure(_run, by);
                var a = new Point3d(ends.From.X, ends.From.Y, 0);
                var b = new Point3d(ends.To.X, ends.To.Y, 0);
                var mid = new Point3d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, 0);
                if (a.DistanceTo(b) > 0.5)
                {
                    var dir = b - a;
                    e.Display.DrawLine(a, b, color, 2);
                    e.Display.DrawArrowHead(b, dir, color, 12, 0);
                    e.Display.DrawArrowHead(a, -dir, color, 12, 0);
                }
                e.Display.Draw2dText(label, color, mid, true, 14);
            }

            void Say(string prompt)
            {
                if (prompt == _prompt) return;
                _prompt = prompt;
                SetCommandPrompt(prompt ?? "");
            }

            Point3d[] Shift(List<Pt> ring, double by)
            {
                if (ring == null || ring.Count == 0) return null;
                var pts = new Point3d[ring.Count + 1];
                for (var i = 0; i < ring.Count; i++)
                    pts[i] = new Point3d(ring[i].X + _run.Normal.X * by, ring[i].Y + _run.Normal.Y * by, 0);
                pts[ring.Count] = pts[0];
                return pts;
            }

            static List<Point3d[]> Loops(List<List<Pt>> rings)
            {
                var loops = new List<Point3d[]>();
                if (rings == null) return loops;
                foreach (var ring in rings)
                {
                    if (ring == null || ring.Count < 2) continue;
                    var pts = new Point3d[ring.Count + 1];
                    for (var i = 0; i < ring.Count; i++)
                        pts[i] = new Point3d(ring[i].X, ring[i].Y, 0);
                    pts[ring.Count] = pts[0];
                    loops.Add(pts);
                }
                return loops;
            }
        }
    }
}
