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
    /// WF: click a face of a wall, then drag it. An end changes the length, a
    /// side moves the wall, and with the Thickness option a side moves alone.
    /// The preview is drawn and dropped; a click, Enter or a typed number
    /// calls edit_wall_face once, for what the dimension showed. Esc changes
    /// nothing. With a wall selected the click goes straight to its face
    /// (pick, then act); with none, the click on a wall is the face.
    /// </summary>
    public static class ForskFaceDrag
    {
        public const string CommandName = "ForskEditWallFace";

        public sealed class Outcome
        {
            public bool Cancelled;
            public string Line;
            /// <summary>The edit_wall_face arguments, or null when nothing is to change.</summary>
            public JObject Args;
            public bool Norwegian;
        }

        /// <summary>Command line: pick and drag, then one edit_wall_face, which opens its own undo record.</summary>
        public static Result Run(RhinoDoc doc)
        {
            var outcome = Pick(doc, null);
            if (outcome.Args == null)
            {
                if (!string.IsNullOrEmpty(outcome.Line)) RhinoApp.WriteLine(outcome.Line);
                return outcome.Cancelled ? Result.Cancel : Result.Success;
            }
            var envelope = ForskTools.ExecuteAllowed("edit_wall_face", outcome.Args);
            RhinoApp.WriteLine(Words(envelope));
            return string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase)
                ? Result.Success
                : Result.Failure;
        }

        /// <summary>The tool's sentence, or its refusal with the point taken off.</summary>
        public static string Words(JObject envelope)
        {
            if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                return WallDrag.Plain(envelope?["message"]?.ToString());
            return envelope["result"]?["message"]?.ToString() ?? "";
        }

        /// <summary>UI thread. Changes nothing: the caller commits Args with its own record. only limits the faces offered.</summary>
        public static Outcome Pick(RhinoDoc doc, WallFace.Kind? only)
        {
            var nb = ForskSpeech.Norwegian;
            var outcome = new Outcome { Norwegian = nb };
            if (doc == null)
            {
                outcome.Cancelled = true;
                outcome.Line = "No active document.";
                return outcome;
            }
            var prompt = Text(only == WallFace.Kind.Side ? "wall.face.pick.side" : "wall.face.pick", nb);
            if (!TryFace(doc, prompt, out var host, out var point, out var view))
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
            if (picked.Graph == null || !WallSplit.Straight(picked.Graph, tol, out _))
            {
                outcome.Line = WallDrag.Curved(nb);
                return outcome;
            }
            // In a plan view every click lands on the top, so the face is read from plan position alone.
            var top = host.Geometry?.GetBoundingBox(true).Max.Z ?? 0;
            double? z = PlanView(view) ? (double?)null : point.Z;
            if (!WallFace.TryPick(picked.Graph, new Pt(point.X, point.Y), z, top, tol, out var hit, out var why, only))
            {
                outcome.Line = WallDrag.Plain(why);
                return outcome;
            }
            new FacePoint(picked, hit, tol, nb, only == WallFace.Kind.Side).Run(outcome, host.Id);
            return outcome;
        }

        /// <summary>
        /// The wall and the point clicked on it. With one wall selected the
        /// click is on that wall; with none, the click picks the wall too.
        /// </summary>
        static bool TryFace(RhinoDoc doc, string prompt, out RhinoObject host, out Point3d point, out RhinoView view)
        {
            host = null;
            point = Point3d.Unset;
            view = null;
            var walls = RhinoMCPFunctions.ListSelected(doc).Where(o => RhinoMCPFunctions.IsHostWall(doc, o)).ToList();
            if (walls.Count != 1)
            {
                var go = new GetObject();
                go.SetCommandPrompt(prompt);
                go.GeometryFilter = ObjectType.AnyObject;
                go.SubObjectSelect = false;
                go.GroupSelect = false;
                go.EnablePreSelect(false, true);
                go.SetCustomGeometryFilter((obj, geometry, component) => RhinoMCPFunctions.IsHostWall(doc, obj));
                if (go.Get() != GetResult.Object) return false;
                var picked = go.Object(0);
                host = picked.Object();
                point = picked.SelectionPoint();
                view = go.View();
                if (point.IsValid) return host != null;
            }
            else host = walls[0];
            var brep = host?.Geometry as Brep ?? (host?.Geometry as Extrusion)?.ToBrep();
            if (brep == null) return false;
            var gp = new GetPoint();
            gp.SetCommandPrompt(prompt);
            gp.Constrain(brep, -1, -1, false);
            if (gp.Get() != GetResult.Point) return false;
            point = gp.Point();
            view = gp.View();
            return true;
        }

        static bool PlanView(RhinoView view)
        {
            var vp = view?.ActiveViewport;
            if (vp == null || !vp.IsParallelProjection) return false;
            var dir = vp.CameraDirection;
            return dir.IsValid && dir.Length > 0 && Math.Abs(dir.Z) / dir.Length > 0.99;
        }

        static string Text(string key, bool nb) => ForskText.Get(nb && ForskText.Has(key + ".nb") ? key + ".nb" : key);

        /// <summary>The rubber band is the snapped edit along the face's way out, not the free mouse point.</summary>
        sealed class FacePoint : GetPoint
        {
            readonly RhinoMCPFunctions.SelectedDrag _pick;
            readonly WallFace.Hit _hit;
            readonly Pt _origin;
            readonly bool _nb;
            readonly double _tol;
            readonly bool _moveOnly;
            readonly List<Point3d[]> _old;
            readonly string[] _stepNames = { "10", "50", "100" };
            int _step = WallDrag.DefaultStep;
            bool _thickness;
            double _shown = double.NaN;
            int _shownStep = -1;
            bool _shownThickness;
            bool _refused;
            /// <summary>How far an end that met another wall went, mm, when it stopped short of the drag.</summary>
            double? _reached;
            string _why;
            List<Point3d[]> _after;

            public FacePoint(RhinoMCPFunctions.SelectedDrag pick, WallFace.Hit hit, double tol, bool nb, bool moveOnly)
            {
                _pick = pick;
                _hit = hit;
                _nb = nb;
                _tol = tol;
                _moveOnly = moveOnly;
                _origin = hit.Middle;
                _old = Loops(pick.Graph.Shape);
                SetBasePoint(new Point3d(_origin.X, _origin.Y, 0), false);
                AcceptNumber(true, true);
                AcceptNothing(true);
                AddOptions();
                SetCommandPrompt(WallFace.Prompt(hit, nb));
            }

            public void Run(Outcome outcome, Guid host)
            {
                var waited = false;
                while (true)
                {
                    var result = Ask();
                    var typed = result == GetResult.Number ? WallFace.Typed(_hit, Number(), _thickness) : 0;
                    var along = result == GetResult.Point ? Along(Point()) : 0;
                    var end = WallDrag.Finish(Name(result), along, _shown, typed, _step, waited, out var by);
                    if (end == WallDrag.DragEnd.Wait)
                    {
                        waited = true;
                        continue;
                    }
                    if (end == WallDrag.DragEnd.Cancel)
                    {
                        outcome.Cancelled = true;
                        outcome.Line = Text("wall.drag.cancel", _nb);
                        return;
                    }
                    if (end == WallDrag.DragEnd.Still)
                    {
                        outcome.Line = Text("wall.face.not", _nb);
                        return;
                    }
                    if (!Try(by, out _, out var why))
                    {
                        outcome.Line = WallDrag.Plain(why);
                        return;
                    }
                    var args = new JObject
                    {
                        ["id"] = host.ToString(),
                        ["at"] = new JArray(_hit.Middle.X, _hit.Middle.Y),
                        ["face"] = _hit.Kind == WallFace.Kind.End ? "end" : "side"
                    };
                    if (_thickness) args["thickness_mm"] = _hit.Run.Thickness + by;
                    else args["distance_mm"] = by;
                    outcome.Args = args;
                    return;
                }
            }

            /// <summary>The edit for by mm out through the face, as edit_wall_face will make it.</summary>
            bool Try(double by, out WallJoins.Moved moved, out string why)
            {
                if (_hit.Kind == WallFace.Kind.End)
                    return WallFace.TryStretch(_pick.Records, _pick.Graph, _hit, by, _tol, out moved, out why);
                if (_thickness)
                    return WallFace.TryThicken(_pick.Records, _pick.Graph, _hit, _hit.Run.Thickness + by, _tol, out moved, out _, out why);
                return WallJoins.TryMove(_pick.Records, _pick.Graph, _hit.Run, WallFace.Across(_hit, by), _tol, out moved, out why);
            }

            static string Name(GetResult result)
            {
                if (result == GetResult.Point) return "point";
                if (result == GetResult.Number) return "number";
                if (result == GetResult.Nothing) return "nothing";
                return "cancel";
            }

            GetResult Ask()
            {
                while (true)
                {
                    var result = Get(true);
                    if (result != GetResult.Option) return result;
                    var option = Option();
                    if (option.EnglishName == "Step")
                    {
                        var index = option.CurrentListOptionIndex;
                        if (index >= 0 && index < WallDrag.Steps.Length) _step = WallDrag.Steps[index];
                    }
                    else if (option.EnglishName == "Thickness")
                        _thickness = !_thickness;
                    ClearCommandOptions();
                    AddOptions();
                }
            }

            void AddOptions()
            {
                var index = Array.IndexOf(WallDrag.Steps, _step);
                if (index < 0) index = 0;
                AddOptionList("Step", _stepNames, index);
                // The Thickness pill: a side moves alone, growing away from the face across.
                if (_hit.Kind != WallFace.Kind.End && !_moveOnly)
                    AddOption("Thickness", _thickness ? "On" : "Off");
            }

            double Along(Point3d point) => WallDrag.Along(_origin, new Pt(point.X, point.Y), _hit.Out);

            protected override void OnDynamicDraw(GetPointDrawEventArgs e)
            {
                var snapped = WallDrag.Snap(Along(e.CurrentPoint), _step);
                if (snapped != _shown || _step != _shownStep || _thickness != _shownThickness)
                {
                    _shown = snapped;
                    _shownStep = _step;
                    _shownThickness = _thickness;
                    Prepare(snapped);
                }
                var blue = System.Drawing.Color.FromArgb(ForskWallHatch.Red, ForskWallHatch.Green, ForskWallHatch.Blue);
                var grey = System.Drawing.Color.FromArgb(160, 160, 160);
                var red = System.Drawing.Color.FromArgb(196, 40, 40);
                foreach (var loop in _old) e.Display.DrawPolyline(loop, grey, 1);
                if (_after != null)
                    foreach (var loop in _after) e.Display.DrawPolyline(loop, blue, 2);
                // The prompt is set once: setting it from here swallows the click.
                var label = _refused ? _why : WallFace.Dimension(_hit, _reached ?? snapped, _thickness, _nb);
                var ink = _refused ? red : blue;
                var a = new Point3d(_origin.X, _origin.Y, 0);
                var b = new Point3d(_origin.X + _hit.Out.X * snapped, _origin.Y + _hit.Out.Y * snapped, 0);
                if (a.DistanceTo(b) > 0.5)
                {
                    var dir = b - a;
                    e.Display.DrawLine(a, b, ink, 2);
                    e.Display.DrawArrowHead(b, dir, ink, 12, 0);
                }
                e.Display.Draw2dText(label, ink, b, true, 14);
                base.OnDynamicDraw(e);
            }

            void Prepare(double by)
            {
                _refused = false;
                _why = null;
                _after = null;
                _reached = null;
                if (Math.Abs(by) < _step) return;
                if (Try(by, out var moved, out var why))
                {
                    _after = Loops(moved.Shape);
                    _reached = moved.Reached;
                }
                else
                {
                    _refused = true;
                    _why = WallDrag.Plain(why);
                }
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
