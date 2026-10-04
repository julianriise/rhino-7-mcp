using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Draw stair, pure: the foot and the end the mouse shows become the straight
/// stair add_stair makes. The rise is the floor to floor height, so the riser
/// count is fixed (ceil(rise / 180)) and the drawn length sets the going. The
/// foot snaps flush to a wall face, the climb to the face's direction, then to
/// 15 degree steps. The plan symbol is Stairs.PlanSymbol, the same marks the
/// print draws. No RhinoCommon.
/// </summary>
public static class StairDraw
{
    public const double ReachMm = 150;
    /// <summary>A drawn going this close to the default snaps to it.</summary>
    public const double GoingMagnetMm = 8;
    /// <summary>The climb within this many degrees of a wall face runs along it.</summary>
    public const double ParallelDeg = 10;
    /// <summary>The plan cut over the stair's floor, as the print has it.</summary>
    public const double PlanCutMm = 1200;
    /// <summary>The preview sizes the dot and the arrow for a 1:100 sheet.</summary>
    public const int PreviewScale = 100;
    public const double GoingMin = 150;
    public const double GoingMax = 600;

    /// <summary>One edge of a wall record, with the unit normal that points out of the wall.</summary>
    public sealed class Face
    {
        public Pt A, B, Free;
        public double Length => WallDraw.Dist(A, B);
    }

    /// <summary>What the stair is drawn from: the floor to floor rise, and the sizes the tool defaults to.</summary>
    public sealed class Setup
    {
        public double Rise = 3000;
        public double Width = Stairs.WidthDefault;
        public double RiserMax = Stairs.RiserMaxDefault;
        public double Going = Stairs.GoingDefault;
    }

    public sealed class FootHit
    {
        public Pt Foot;
        /// <summary>The face the stair stands against, or null when the foot is free.</summary>
        public Face Face;
    }

    public sealed class Draft
    {
        public bool Valid;
        public string Why;
        public Pt Foot;
        public Pt Dir;
        public Stairs.Spec Spec;
        public Stairs.Flight Flight;
        /// <summary>The going the mouse asked for did not fit 150..600 and was held at the limit.</summary>
        public bool Held;
        public string Against;
        public List<Pt> Footprint = new List<Pt>();
        public string Label = "";
        public string Dimension = "";
    }

    /// <summary>Every edge of every wall record, with the side that is free of wall.</summary>
    public static List<Face> Faces(IEnumerable<List<List<Pt>>> records)
    {
        var faces = new List<Face>();
        foreach (var rings in records ?? Enumerable.Empty<List<List<Pt>>>())
            for (var k = 0; k < (rings?.Count ?? 0); k++)
            {
                var ring = rings[k];
                // The wall is inside an outer loop and outside a hole; the loop's direction says which side that is.
                var solidLeft = (k == 0) == (RoomDetect.Area(ring) > 0);
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (WallDraw.Dist(a, b) < 1) continue;
                    var d = WallDraw.Unit(a, b);
                    faces.Add(new Face { A = a, B = b, Free = solidLeft ? new Pt(d.Y + 0.0, -d.X + 0.0) : new Pt(-d.Y + 0.0, d.X + 0.0) });
                }
            }
        return faces;
    }

    /// <summary>
    /// The foot. Near a wall face it sits flush against it, half the width
    /// out from the face on the free side, and locks to the face's end when
    /// that is within reach (a corner). Else the point on the 10 mm grid.
    /// </summary>
    public static FootHit SnapFoot(IList<Face> faces, Pt raw, double width, double reach, bool shift)
    {
        Face best = null;
        var bestPoint = raw;
        var bestDist = reach;
        foreach (var face in faces ?? new List<Face>())
        {
            var p = Nearest(face.A, face.B, raw);
            var d = WallDraw.Dist(p, raw);
            if (d > bestDist) continue;
            best = face;
            bestPoint = p;
            bestDist = d;
        }
        if (best == null)
        {
            var step = shift ? 1.0 : WallDraw.LengthStepMm;
            return new FootHit { Foot = new Pt(Math.Round(raw.X / step) * step, Math.Round(raw.Y / step) * step) };
        }
        var toA = WallDraw.Dist(bestPoint, best.A);
        var toB = WallDraw.Dist(bestPoint, best.B);
        if (Math.Min(toA, toB) <= reach) bestPoint = toA <= toB ? best.A : best.B;
        return new FootHit
        {
            Face = best,
            Foot = new Pt(WallDraw.Round(bestPoint.X + best.Free.X * width / 2.0), WallDraw.Round(bestPoint.Y + best.Free.Y * width / 2.0))
        };
    }

    /// <summary>
    /// The way up: along the face the foot stands against when the mouse is
    /// within 10 degrees of it, else the nearest 15 degree step; free with Shift.
    /// </summary>
    public static Pt Direction(Pt foot, Pt raw, Face face, bool shift, out bool alongFace)
    {
        alongFace = false;
        var length = WallDraw.Dist(foot, raw);
        if (length < 1e-9) return face == null ? new Pt(1, 0) : WallDraw.Unit(face.A, face.B);
        var v = WallDraw.Unit(foot, raw);
        if (shift) return v;
        if (face != null)
        {
            var d = WallDraw.Unit(face.A, face.B);
            foreach (var sign in new[] { 1.0, -1.0 })
            {
                var cos = Math.Max(-1, Math.Min(1, (v.X * d.X + v.Y * d.Y) * sign));
                if (Math.Acos(cos) * 180.0 / Math.PI > ParallelDeg) continue;
                alongFace = true;
                return new Pt(d.X * sign, d.Y * sign);
            }
        }
        return WallDraw.AngleDir(foot, raw);
    }

    /// <summary>
    /// The stair for a foot and the end the mouse shows. The drawn run (or the
    /// typed one) divides over the treads: that is the going, held to 150..600,
    /// and snapped to the default going when within 8 mm of it.
    /// </summary>
    public static Draft Plan(Setup setup, FootHit foot, Pt raw, bool shift, double? typedRun = null)
    {
        var draft = new Draft { Foot = foot.Foot };
        var dir = Direction(foot.Foot, raw, foot.Face, shift, out var along);
        draft.Dir = dir;
        draft.Against = along ? Against(foot.Face, dir) : null;
        var drawn = typedRun ?? Math.Max(0, (raw.X - foot.Foot.X) * dir.X + (raw.Y - foot.Foot.Y) * dir.Y);
        Stairs.Flight flight;
        try
        {
            var first = Stairs.Plan(setup.Rise, setup.RiserMax, setup.Going, setup.Width);
            var going = GoingFor(drawn, first.Treads, setup.Going, out var held);
            draft.Held = held;
            flight = Stairs.Plan(setup.Rise, setup.RiserMax, going, setup.Width);
        }
        catch (ArgumentException e)
        {
            draft.Why = e.Message;
            return draft;
        }
        draft.Flight = flight;
        draft.Spec = new Stairs.Spec
        {
            X = foot.Foot.X,
            Y = foot.Foot.Y,
            Dx = dir.X + 0.0,
            Dy = dir.Y + 0.0,
            Width = flight.Width,
            RiserMax = setup.RiserMax,
            Going = flight.Going,
            Against = draft.Against
        };
        draft.Footprint = Stairs.Footprint(draft.Spec, flight);
        draft.Label = Stairs.Label(flight);
        var comfort = Stairs.Comfort(flight);
        draft.Dimension = Stairs.Mm(flight.Run) + " mm" + (comfort == null ? "" : " · " + comfort);
        draft.Valid = drawn >= 10 || typedRun.HasValue;
        if (!draft.Valid) draft.Why = "Move the end farther from the start.";
        return draft;
    }

    /// <summary>Going = run / treads, whole millimetres, the default when within the magnet, held to the limits.</summary>
    public static double GoingFor(double run, int treads, double defaultGoing, out bool held)
    {
        held = false;
        if (treads < 1) return defaultGoing;
        var going = run / treads;
        if (Math.Abs(going - defaultGoing) <= GoingMagnetMm) return defaultGoing;
        going = Math.Round(going, MidpointRounding.AwayFromZero);
        if (going < GoingMin)
        {
            held = true;
            return GoingMin;
        }
        if (going > GoingMax)
        {
            held = true;
            return GoingMax;
        }
        return going;
    }

    /// <summary>The side that stands against the wall, looking up the stair: right when the free side is on its left.</summary>
    public static string Against(Face face, Pt dir)
    {
        if (face == null) return null;
        return face.Free.X * -dir.Y + face.Free.Y * dir.X > 0 ? "right" : "left";
    }

    /// <summary>The add_stair parameters. The rise stays auto, so the stair follows the walls like a chat-made one.</summary>
    public static JObject ToolParams(Draft draft)
    {
        var end = new Pt(WallDraw.Round(draft.Foot.X + draft.Dir.X * draft.Flight.Run), WallDraw.Round(draft.Foot.Y + draft.Dir.Y * draft.Flight.Run));
        var call = new JObject
        {
            ["from"] = new JArray(draft.Foot.X, draft.Foot.Y),
            ["to"] = new JArray(end.X, end.Y),
            ["width"] = draft.Flight.Width,
            ["going"] = draft.Flight.Going
        };
        if (draft.Against != null) call["against"] = draft.Against;
        return call;
    }

    /// <summary>One piece of the live symbol, in plan millimetres.</summary>
    public sealed class Piece
    {
        /// <summary>line, dot or text.</summary>
        public string Shape;
        public Pt A, B;
        public double Radius;
        public string Text;
        public bool Dashed;
    }

    /// <summary>The plan symbol (steps, walking line, break, "UP n × r/g") in plan coordinates.</summary>
    public static List<Piece> Symbol(Draft draft)
    {
        var pieces = new List<Piece>();
        if (draft?.Flight == null) return pieces;
        var left = new Pt(-draft.Dir.Y, draft.Dir.X);
        Pt At(double u, double v) => new Pt(
            draft.Foot.X + draft.Dir.X * u + left.X * v,
            draft.Foot.Y + draft.Dir.Y * u + left.Y * v);
        foreach (var mark in Stairs.PlanSymbol(draft.Flight, PlanCutMm, PreviewScale, Stairs.LabelHeight(draft.Flight, PreviewScale)))
            pieces.Add(new Piece
            {
                Shape = mark.Shape,
                A = At(mark.U0, mark.V0),
                B = At(mark.U1, mark.V1),
                Radius = mark.Radius,
                Text = mark.Text,
                Dashed = mark.Dashed
            });
        return pieces;
    }

    static Pt Nearest(Pt a, Pt b, Pt p)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var len2 = abx * abx + aby * aby;
        var s = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
        return new Pt(a.X + abx * s, a.Y + aby * s);
    }
}
