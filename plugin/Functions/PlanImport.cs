using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Plan import (F7): a detection of a floor plan in forsk.plan_import.v0 (mm,
/// y up), cleaned into review geometry for the 2D layers the bake reads: one
/// closed rectangle per wall, a footprint across the host wall per opening,
/// room outlines and their labels. Pure geometry, no RhinoCommon, so it tests
/// headless. The clean-up tidies what is safe to tidy and reports the rest:
/// nothing is dropped, left loose or unnamed without a line in the receipt.
/// </summary>
public static class PlanImport
{
    public const string Schema = "forsk.plan_import.v0";

    public sealed class Settings
    {
        /// <summary>A wall this close to an axis is squared to it. A real diagonal is far outside.</summary>
        public double SnapDeg = 3.0;
        /// <summary>Wall thickness is rounded to this.</summary>
        public double Round = 10.0;
        /// <summary>Collinear pieces merge when their centrelines are this close.</summary>
        public double MergeOffset = 20.0;
        /// <summary>Collinear pieces merge across a gap this small, or across one an opening fills.</summary>
        public double MergeGap = 50.0;
        /// <summary>Widest gap openings can bridge between two pieces.</summary>
        public double MaxBridge = 3000.0;
        /// <summary>An opening snaps to a wall when its centre is within this of the wall's face.</summary>
        public double OpeningReach = 150.0;
        /// <summary>A wall end this close to another wall runs to that wall's far face.</summary>
        public double JoinReach = 100.0;
        /// <summary>Shorter walls are dropped.</summary>
        public double MinLength = 100.0;
        /// <summary>An opening footprint stands this proud of each wall face, so the cut goes through.</summary>
        public double Proud = 10.0;
        /// <summary>Footprint depth of an opening that found no wall.</summary>
        public double LooseDepth = 250.0;
    }

    public sealed class Wall
    {
        public Pt A;
        public Pt B;
        public double Thickness;
        /// <summary>Thickness as detected, before rounding. The scale step rounds from this.</summary>
        public double Detected;
        public string Class;
        public int Pieces = 1;
        public bool Snapped;
        public bool Diagonal;
        public double Length => Dist(A, B);
    }

    public sealed class Opening
    {
        /// <summary>door or window once cleaned; as the file has it before.</summary>
        public string Kind;
        public Pt A;
        public Pt B;
        public double Width;
        /// <summary>Footprint size across the wall.</summary>
        public double Depth;
        /// <summary>Index of the host wall, or -1 when no wall was within reach.</summary>
        public int Host = -1;
        /// <summary>Why this opening needs a look, or null.</summary>
        public string Note;
    }

    public sealed class Room
    {
        /// <summary>Null when the detection has no name for it; rooms_detect then calls it Rom.</summary>
        public string Label;
        public List<Pt> Ring;
        public Pt At;
        public double Area;
        /// <summary>The walls do not close around it.</summary>
        public bool Outside;
    }

    public sealed class Plan
    {
        public List<Wall> Walls = new List<Wall>();
        public List<Opening> Openings = new List<Opening>();
        public List<Room> Rooms = new List<Room>();
        public string Vendor;
        /// <summary>detected, or anything else for a scale nobody measured.</summary>
        public string ScaleStatus;
        /// <summary>1:100, or null.</summary>
        public string ScaleRatio;
        /// <summary>Width on the plan of the image the detection was made from, top-left at (0, 0). 0 when the file does not say.</summary>
        public double ImageWidthMm;
    }

    public sealed class Result
    {
        public List<Wall> Walls = new List<Wall>();
        public List<Opening> Openings = new List<Opening>();
        public List<Room> Rooms = new List<Room>();
        /// <summary>Walls in the file.</summary>
        public int Detected;
        /// <summary>Pieces merged into a collinear neighbour.</summary>
        public int Merged;
        public int Snapped;
        public int Diagonal;
        /// <summary>Wall ends run to the wall they stop at.</summary>
        public int Joined;
        /// <summary>Wall ends run through an opening that sat past them.</summary>
        public int Extended;
        public int Doors;
        public int Windows;
        /// <summary>Openings that found no wall.</summary>
        public int Loose;
        public int Unlabelled;
        /// <summary>Rooms the walls do not close around.</summary>
        public int Outside;
        /// <summary>What was left out, each with its reason.</summary>
        public List<string> Dropped = new List<string>();
        /// <summary>What the user should look at, one line each.</summary>
        public List<string> Review = new List<string>();
    }

    /// <summary>Reads a forsk.plan_import.v0 file. Keys it does not know (wall_polygons, metrics) are ignored.</summary>
    public static Plan Parse(string json)
    {
        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch (Exception e)
        {
            throw new FormatException("The plan file is not JSON: " + e.Message);
        }
        var schema = root["schema"]?.ToString();
        if (schema != Schema)
            throw new FormatException("Not a " + Schema + " file" + (string.IsNullOrEmpty(schema) ? "." : " (schema " + schema + ")."));
        var units = root["units"]?.ToString();
        if (!string.IsNullOrEmpty(units) && units != "mm")
            throw new FormatException("plan_import units must be mm, not " + units + ".");
        var flip = string.Equals(root["y_axis"]?.ToString(), "down", StringComparison.OrdinalIgnoreCase) ? -1.0 : 1.0;

        var plan = new Plan
        {
            Vendor = root["source"]?["vendor"]?.ToString(),
            ScaleStatus = root["scale"]?["status"]?.ToString(),
            ScaleRatio = root["scale"]?["ratio"]?.ToString(),
            ImageWidthMm = root["image"]?["width_mm"]?.ToObject<double?>() ?? 0
        };

        var index = 0;
        foreach (var token in root["walls"] as JArray ?? new JArray())
        {
            index++;
            if (!TryPt(token["start"], flip, out var a) || !TryPt(token["end"], flip, out var b))
                throw new FormatException("Wall " + index + " has no start and end.");
            plan.Walls.Add(new Wall
            {
                A = a,
                B = b,
                Thickness = token["thickness"]?.ToObject<double?>() ?? 0,
                Class = token["class"]?.ToString()
            });
        }

        index = 0;
        foreach (var token in root["openings"] as JArray ?? new JArray())
        {
            index++;
            // A window is its two ends. A door is its leaf, hinge to closed, which lies in the wall.
            Pt a, b;
            if (!(TryPt(token["a"], flip, out a) && TryPt(token["b"], flip, out b))
                && !(TryPt(token["hinge"], flip, out a) && TryPt(token["closed"], flip, out b)))
                throw new FormatException("Opening " + index + " has neither a and b nor hinge and closed.");
            var width = token["opening_width"]?.ToObject<double?>() ?? token["width"]?.ToObject<double?>() ?? Dist(a, b);
            plan.Openings.Add(new Opening
            {
                Kind = (token["kind"]?.ToString() ?? "").ToLowerInvariant(),
                A = a,
                B = b,
                Width = width > 0 ? width : Dist(a, b)
            });
        }

        index = 0;
        foreach (var token in root["rooms"] as JArray ?? new JArray())
        {
            index++;
            var ring = new List<Pt>();
            foreach (var p in token["boundary"] as JArray ?? new JArray())
                if (TryPt(p, flip, out var at)) ring.Add(at);
            // Rom is the converter's word for no name. The text read off the plan comes second.
            var label = token["label"]?.ToString();
            if (string.IsNullOrWhiteSpace(label) || label == RoomDetect.DefaultRoomName)
                label = token["ocr_text"]?.Type == JTokenType.String ? token["ocr_text"].ToString() : null;
            plan.Rooms.Add(new Room { Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(), Ring = ring });
        }
        return plan;
    }

    static bool TryPt(JToken token, double flip, out Pt p)
    {
        p = default;
        if (!(token is JArray pair) || pair.Count < 2) return false;
        var x = pair[0].ToObject<double?>();
        var y = pair[1].ToObject<double?>();
        if (x == null || y == null) return false;
        p = new Pt(x.Value, y.Value * flip);
        return true;
    }

    /// <summary>
    /// Squares near-orthogonal walls, rounds thickness, merges collinear
    /// pieces (across an opening too, so the wall runs through it), hosts each
    /// opening on its nearest wall, and runs wall ends into the walls they stop
    /// at. A diagonal stays a diagonal. Every wall that goes and every opening
    /// or room that needs a look is in the result.
    /// </summary>
    public static Result Clean(Plan plan, Settings settings = null)
    {
        var s = settings ?? new Settings();
        var result = new Result { Detected = plan.Walls.Count };
        var walls = result.Walls;
        foreach (var w in plan.Walls)
        {
            var length = Dist(w.A, w.B);
            if (w.Thickness <= 0 || length < s.MinLength)
            {
                result.Dropped.Add("wall at " + At(Mid(w.A, w.B)) + ": "
                    + (w.Thickness <= 0 ? "no thickness" : Mm(length) + " mm long"));
                continue;
            }
            var wall = new Wall
            {
                A = w.A,
                B = w.B,
                Detected = w.Thickness,
                Thickness = RoundTo(w.Thickness, s.Round),
                Class = w.Class
            };
            Square(wall, s.SnapDeg);
            walls.Add(wall);
        }

        Merge(walls, plan.Openings, s, result);
        DropContained(walls, null, result);
        Host(walls, plan.Openings, s, result);
        Join(walls, s, result);
        DropContained(walls, result.Openings, result);

        foreach (var wall in walls)
        {
            // Lower-left end first, so the same plan gives the same geometry.
            if (wall.B.X < wall.A.X - 1e-9 || (Math.Abs(wall.B.X - wall.A.X) <= 1e-9 && wall.B.Y < wall.A.Y))
                (wall.A, wall.B) = (wall.B, wall.A);
            if (wall.Snapped) result.Snapped++;
            if (wall.Diagonal) result.Diagonal++;
        }

        Rooms(plan, result);
        Summarise(result);
        return result;
    }

    static void Square(Wall wall, double deg)
    {
        var dx = wall.B.X - wall.A.X;
        var dy = wall.B.Y - wall.A.Y;
        var ax = Math.Abs(dx);
        var ay = Math.Abs(dy);
        var off = Math.Atan2(Math.Min(ax, ay), Math.Max(ax, ay)) * 180.0 / Math.PI;
        if (off <= 1e-9) return;
        if (off > deg)
        {
            wall.Diagonal = true;
            return;
        }
        // Turned about its midpoint: the length stays, the ends move least.
        var mid = Mid(wall.A, wall.B);
        var half = Dist(wall.A, wall.B) / 2.0;
        if (ax >= ay)
        {
            var sign = dx >= 0 ? 1.0 : -1.0;
            wall.A = new Pt(mid.X - sign * half, mid.Y);
            wall.B = new Pt(mid.X + sign * half, mid.Y);
        }
        else
        {
            var sign = dy >= 0 ? 1.0 : -1.0;
            wall.A = new Pt(mid.X, mid.Y - sign * half);
            wall.B = new Pt(mid.X, mid.Y + sign * half);
        }
        wall.Snapped = true;
    }

    static void Merge(List<Wall> walls, List<Opening> openings, Settings s, Result result)
    {
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < walls.Count && !merged; i++)
            {
                for (var j = i + 1; j < walls.Count && !merged; j++)
                {
                    if (!TryMerge(walls[i], walls[j], openings, s, out var one)) continue;
                    walls[i] = one;
                    walls.RemoveAt(j);
                    result.Merged++;
                    merged = true;
                }
            }
        }
    }

    /// <summary>
    /// Two pieces are one wall when they are parallel on the same centreline,
    /// the same thickness within one rounding step, and overlap, nearly touch,
    /// or have openings filling the gap between them. A thickness change along
    /// a line is a real change and stays two walls.
    /// </summary>
    static bool TryMerge(Wall a, Wall b, List<Opening> openings, Settings s, out Wall merged)
    {
        merged = null;
        var p = a.Length >= b.Length ? a : b;
        var q = ReferenceEquals(p, a) ? b : a;
        if (Math.Abs(p.Thickness - q.Thickness) > s.Round + 1e-6) return false;
        var u = Unit(p.A, p.B);
        var n = Normal(u);
        if (Math.Abs(Cross(u, Unit(q.A, q.B))) > Sin(2.0)) return false;
        var offA = Dot(Sub(q.A, p.A), n);
        var offB = Dot(Sub(q.B, p.A), n);
        if (Math.Abs(offA) > s.MergeOffset || Math.Abs(offB) > s.MergeOffset) return false;

        var length = p.Length;
        var s0 = Dot(Sub(q.A, p.A), u);
        var s1 = Dot(Sub(q.B, p.A), u);
        var lo = Math.Min(s0, s1);
        var hi = Math.Max(s0, s1);
        var gap = Math.Max(lo - length, -hi);
        if (gap > s.MergeGap)
        {
            var from = lo > length ? length : hi;
            if (!Bridged(p, from, from + gap, openings, s)) return false;
        }

        // The longer piece sets direction, thickness and class; the centreline is the length-weighted mean.
        var weight = (hi - lo) / (length + hi - lo);
        var origin = Along(p.A, n, (offA + offB) / 2.0 * weight);
        merged = new Wall
        {
            A = Along(origin, u, Math.Min(0, lo)),
            B = Along(origin, u, Math.Max(length, hi)),
            Thickness = p.Thickness,
            Detected = p.Detected,
            Class = p.Class,
            Pieces = a.Pieces + b.Pieces,
            Snapped = a.Snapped || b.Snapped,
            Diagonal = p.Diagonal
        };
        return true;
    }

    /// <summary>Openings lying in the wall's line cover the gap from..to along it.</summary>
    static bool Bridged(Wall wall, double from, double to, List<Opening> openings, Settings s)
    {
        var gap = to - from;
        if (gap > s.MaxBridge || openings == null) return false;
        var covered = 0.0;
        foreach (var opening in openings)
        {
            if (!TryProject(opening, wall, s, out var lo, out var hi, out _)) continue;
            covered += Math.Max(0, Math.Min(hi, to) - Math.Max(lo, from));
        }
        return covered >= 0.8 * gap;
    }

    /// <summary>
    /// The opening's span along the wall's centreline, from the wall's A end,
    /// when it runs the wall's way and its centre is within reach of the wall's
    /// face. A window drawn across a wall is not that wall's window.
    /// </summary>
    static bool TryProject(Opening opening, Wall wall, Settings s, out double lo, out double hi, out double off)
    {
        lo = hi = off = 0;
        var u = Unit(wall.A, wall.B);
        if (Dist(opening.A, opening.B) > 1.0 && Math.Abs(Cross(u, Unit(opening.A, opening.B))) > Sin(30.0))
            return false;
        var centre = Sub(Mid(opening.A, opening.B), wall.A);
        off = Math.Abs(Dot(centre, Normal(u)));
        if (off > wall.Thickness / 2.0 + s.OpeningReach) return false;
        var at = Dot(centre, u);
        lo = at - opening.Width / 2.0;
        hi = at + opening.Width / 2.0;
        return true;
    }

    /// <summary>
    /// Each opening goes to the nearest wall it lies in. One that starts where
    /// a wall stops takes that wall with it: the wall is run through the
    /// opening, as the bake needs a wall to cut. One with no wall in reach
    /// stays where it was detected and is reported.
    /// </summary>
    static void Host(List<Wall> walls, List<Opening> detected, Settings s, Result result)
    {
        foreach (var opening in detected)
        {
            var kind = opening.Kind == "window" ? "window" : "door";
            if (kind == "window") result.Windows++;
            else result.Doors++;

            var best = -1;
            var bestOff = double.MaxValue;
            var bestInside = false;
            double bestLo = 0, bestHi = 0;
            for (var i = 0; i < walls.Count; i++)
            {
                if (!TryProject(opening, walls[i], s, out var lo, out var hi, out var off)) continue;
                var length = walls[i].Length;
                var inside = lo >= -1.0 && hi <= length + 1.0;
                var past = !inside
                    && ((lo >= -1.0 && lo <= length + s.MergeGap) || (hi <= length + 1.0 && hi >= -s.MergeGap));
                if (!inside && !past) continue;
                if (best >= 0 && bestInside && !inside) continue;
                if (best >= 0 && bestInside == inside && off >= bestOff) continue;
                best = i;
                bestOff = off;
                bestInside = inside;
                bestLo = lo;
                bestHi = hi;
            }

            var middle = Mid(opening.A, opening.B);
            var name = kind + " at " + At(middle);
            if (best < 0)
            {
                var nearest = Nearest(walls, middle);
                var note = nearest < 0 ? "no wall to sit in" : "no wall in reach, nearest face " + Mm(nearest) + " mm away";
                result.Openings.Add(new Opening
                {
                    Kind = kind,
                    A = opening.A,
                    B = opening.B,
                    Width = opening.Width,
                    Depth = s.LooseDepth,
                    Note = note
                });
                result.Loose++;
                result.Review.Add(name + ": " + note + ". Left where it was detected; it will not cut until a wall is drawn under it.");
                continue;
            }

            var wall = walls[best];
            var u = Unit(wall.A, wall.B);
            var a = Along(wall.A, u, bestLo);
            var b = Along(wall.A, u, bestHi);
            var wallLength = wall.Length;
            if (bestLo < -1.0)
            {
                wall.A = a;
                result.Extended++;
            }
            if (bestHi > wallLength + 1.0)
            {
                wall.B = b;
                result.Extended++;
            }

            var placed = new Opening
            {
                Kind = kind,
                A = a,
                B = b,
                Width = opening.Width,
                Depth = wall.Thickness + 2.0 * s.Proud,
                Host = best
            };
            if (wall.Diagonal)
                placed.Note = "on a diagonal wall: the bake cuts along X or Y only, so it will not cut square to this wall";
            else if (placed.Width <= placed.Depth)
                placed.Note = "narrower than its wall is thick: check its width after the bake";
            else if (opening.Kind != "window" && opening.Kind != "door")
                placed.Note = "a passage with no door drawn: imported as a door";
            if (placed.Note != null) result.Review.Add(name + ": " + placed.Note + ".");
            result.Openings.Add(placed);
        }
    }

    /// <summary>Distance from p to the nearest wall face, 0 inside a wall, -1 with no walls.</summary>
    static double Nearest(List<Wall> walls, Pt p)
    {
        var best = -1.0;
        foreach (var wall in walls)
        {
            var d = RoomDetect.Contains(Ring(wall), p) ? 0 : RoomDetect.Clearance(Ring(wall), p);
            if (best < 0 || d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// A wall end within reach of another wall runs to that wall's far face,
    /// so a corner is a full corner and the outline closes. A collinear
    /// neighbour a small gap away is met. Every move is measured on the walls
    /// as detected, then applied, so the order of the walls does not matter.
    /// </summary>
    static void Join(List<Wall> walls, Settings s, Result result)
    {
        var moves = new List<KeyValuePair<int, double>>();
        for (var i = 0; i < walls.Count; i++)
        {
            for (var end = 0; end < 2; end++)
            {
                var wall = walls[i];
                var at = end == 0 ? wall.A : wall.B;
                var outward = end == 0 ? Unit(wall.B, wall.A) : Unit(wall.A, wall.B);
                var best = double.NaN;
                for (var j = 0; j < walls.Count; j++)
                {
                    if (i == j || !TryReach(wall, at, outward, walls[j], s, out var move)) continue;
                    if (double.IsNaN(best) || Math.Abs(move) < Math.Abs(best)) best = move;
                }
                if (double.IsNaN(best) || Math.Abs(best) < 0.5 || wall.Length + best < s.MinLength) continue;
                moves.Add(new KeyValuePair<int, double>(2 * i + end, best));
            }
        }
        foreach (var move in moves)
        {
            var wall = walls[move.Key / 2];
            if (move.Key % 2 == 0) wall.A = Along(wall.A, Unit(wall.B, wall.A), move.Value);
            else wall.B = Along(wall.B, Unit(wall.A, wall.B), move.Value);
        }
        result.Joined = moves.Count;
    }

    /// <summary>How far the end at 'at' moves along 'outward' to meet 'other'. Negative trims an overshoot.</summary>
    static bool TryReach(Wall wall, Pt at, Pt outward, Wall other, Settings s, out double move)
    {
        move = 0;
        var u = Unit(other.A, other.B);
        var n = Normal(u);
        var rate = Dot(outward, n);
        var off = Dot(Sub(at, other.A), n);
        if (Math.Abs(rate) > Sin(10.0))
        {
            // Where this wall's centreline crosses the other wall's two faces.
            var l1 = (other.Thickness / 2.0 - off) / rate;
            var l2 = (-other.Thickness / 2.0 - off) / rate;
            var near = Math.Min(l1, l2);
            var far = Math.Max(l1, l2);
            if (near > s.JoinReach || far < -s.JoinReach) return false;
            var along = Dot(Sub(Along(at, outward, (near + far) / 2.0), other.A), u);
            var slack = wall.Thickness / 2.0 + s.JoinReach;
            if (along < -slack || along > other.Length + slack) return false;
            move = far;
            return true;
        }

        // Collinear neighbour: both bodies on one line, a small gap between the ends.
        if (Math.Abs(off) >= (wall.Thickness + other.Thickness) / 2.0) return false;
        var a = Dot(Sub(other.A, at), outward);
        var b = Dot(Sub(other.B, at), outward);
        var gap = Math.Min(a, b);
        if (gap <= 0 || gap > s.JoinReach) return false;
        move = gap + s.Proud;
        return true;
    }

    /// <summary>
    /// A wall wholly inside another is dropped: the bake reads a closed curve
    /// inside another as a hole in it.
    /// </summary>
    static void DropContained(List<Wall> walls, List<Opening> openings, Result result)
    {
        for (var i = walls.Count - 1; i >= 0; i--)
        {
            var holder = -1;
            for (var j = 0; j < walls.Count && holder < 0; j++)
            {
                if (i == j || !Inside(walls[i], walls[j])) continue;
                // Two copies of one wall hold each other: the later one goes.
                if (Inside(walls[j], walls[i]) && j > i) continue;
                holder = j;
            }
            if (holder < 0) continue;
            result.Dropped.Add("wall at " + At(Mid(walls[i].A, walls[i].B)) + ": inside a thicker wall");
            walls.RemoveAt(i);
            if (holder > i) holder--;
            if (openings == null) continue;
            foreach (var opening in openings)
            {
                if (opening.Host == i) opening.Host = holder;
                else if (opening.Host > i) opening.Host--;
            }
        }
    }

    static bool Inside(Wall inner, Wall outer)
    {
        var u = Unit(outer.A, outer.B);
        var n = Normal(u);
        var length = outer.Length;
        foreach (var corner in Ring(inner))
        {
            var d = Sub(corner, outer.A);
            var along = Dot(d, u);
            if (along < -1.0 || along > length + 1.0) return false;
            if (Math.Abs(Dot(d, n)) > outer.Thickness / 2.0 + 1.0) return false;
        }
        return true;
    }

    static void Rooms(Plan plan, Result result)
    {
        var footprints = RoomDetect.Footprints(result.Walls.Select(Ring).ToList(), 1.0);
        foreach (var room in plan.Rooms)
        {
            var ring = new List<Pt>();
            foreach (var p in room.Ring ?? new List<Pt>())
                if (ring.Count == 0 || Dist(ring[ring.Count - 1], p) > 1e-6) ring.Add(p);
            if (ring.Count > 1 && Dist(ring[0], ring[ring.Count - 1]) <= 1e-6) ring.RemoveAt(ring.Count - 1);
            var area = Math.Abs(RoomDetect.Area(ring));
            if (ring.Count < 3 || area <= 0 || !RoomDetect.TryInside(new List<List<Pt>> { ring }, out var at))
            {
                result.Dropped.Add("room " + (room.Label ?? "with no label") + ": its outline has no area");
                continue;
            }
            var outside = true;
            foreach (var footprint in footprints)
                if (RoomDetect.Contains(footprint, at)) outside = false;
            result.Rooms.Add(new Room { Label = room.Label, Ring = ring, At = at, Area = area, Outside = outside });
            if (room.Label == null) result.Unlabelled++;
            if (outside) result.Outside++;
        }
    }

    static void Summarise(Result result)
    {
        var rooms = result.Rooms;
        if (result.Unlabelled > 0)
        {
            var at = rooms.Where(r => r.Label == null).Select(r => At(r.At));
            result.Review.Add(Count(result.Unlabelled, "room") + " without a label (" + string.Join("; ", at)
                + "): each reads " + RoomDetect.DefaultRoomName + " until a text on the label layer names it.");
        }
        if (result.Outside > 0)
        {
            var names = result.Outside == rooms.Count
                ? "all " + Count(rooms.Count, "room")
                : string.Join(", ", rooms.Where(r => r.Outside).Select(r => r.Label ?? RoomDetect.DefaultRoomName + " at " + At(r.At)));
            result.Review.Add("The walls do not close around " + names
                + ". Draw the missing walls on the wall layer; until then the floor and roof follow the walls only.");
        }
        foreach (var dropped in result.Dropped)
            result.Review.Add("Dropped " + dropped + ".");
    }

    /// <summary>
    /// The one-line receipt: what came in, how the scale stands, and how much
    /// needs a look. The lines behind the counts are in Review.
    /// </summary>
    public static string Message(Result result, string scale)
    {
        var walls = Count(result.Walls.Count, "wall");
        if (result.Merged > 0 || result.Dropped.Count > 0)
            walls += " (" + result.Detected + " detected" + (result.Merged > 0 ? ", " + result.Merged + " merged" : "")
                + (result.Walls.Count + result.Merged < result.Detected ? ", " + (result.Detected - result.Merged - result.Walls.Count) + " dropped" : "") + ")";
        var text = "Imported " + walls + ", " + Count(result.Doors, "door") + ", " + Count(result.Windows, "window")
            + ", " + Count(result.Rooms.Count, "room") + ". " + scale;
        var review = new List<string>();
        if (result.Loose > 0) review.Add(Count(result.Loose, "opening") + " not on a wall");
        var noted = result.Openings.Count(o => o.Host >= 0 && o.Note != null);
        if (noted > 0) review.Add(Count(noted, "opening") + " to check");
        if (result.Unlabelled > 0) review.Add(Count(result.Unlabelled, "room") + " without a label");
        if (result.Outside > 0) review.Add("walls open around " + Count(result.Outside, "room"));
        if (result.Dropped.Count > 0) review.Add(result.Dropped.Count + " dropped");
        return text + (review.Count > 0 ? " Review: " + string.Join(", ", review) + "." : " Nothing to review.");
    }

    /// <summary>How the scale stands, for the receipt. status: detected, user, or unconfirmed.</summary>
    public static string ScaleLine(string status, string ratio)
    {
        var at = string.IsNullOrEmpty(ratio) ? "" : " " + ratio;
        if (status == "user") return "Scale set from two points and a known length.";
        if (status == "detected")
            return "Scale" + at + " read from the plan, not confirmed: check it with two points and a known length.";
        return "Scale not detected" + (at.Length > 0 ? " (assumed" + at + ")" : "") + ": set it with two points and a known length.";
    }

    /// <summary>The wall's footprint, counterclockwise: along the right face from A, back along the left.</summary>
    public static List<Pt> Ring(Wall wall)
    {
        return Band(wall.A, wall.B, wall.Thickness);
    }

    /// <summary>The opening's footprint across its wall: width along, depth across.</summary>
    public static List<Pt> Ring(Opening opening)
    {
        return Band(opening.A, opening.B, opening.Depth);
    }

    static List<Pt> Band(Pt a, Pt b, double across)
    {
        var n = Normal(Unit(a, b));
        var half = across / 2.0;
        return new List<Pt> { Along(a, n, -half), Along(b, n, -half), Along(b, n, half), Along(a, n, half) };
    }

    /// <summary>Where the plan's top-left corner sits in the model, and its scale from the detection's own mm.</summary>
    public sealed class Scale
    {
        public Pt Origin;
        public double Factor = 1.0;
    }

    /// <summary>
    /// The scale, from the detection's own mm, that puts the two points mm
    /// apart. Points picked in the model are read back through the scale the
    /// plan has now, so the same two plan features give the same answer however
    /// often the step runs: a repeat changes nothing.
    /// </summary>
    public static double FactorFor(Scale now, Pt p1, Pt p2, double mm, bool sourceFrame)
    {
        var apart = Dist(p1, p2);
        if (!sourceFrame) apart /= now.Factor;
        if (apart <= 1e-6) throw new ArgumentException("The two points are the same point.");
        if (mm <= 0) throw new ArgumentException("The known length must be above 0 mm.");
        return mm / apart;
    }

    /// <summary>Distance between the two points in the model as it is scaled now: the prefill for the known length.</summary>
    public static double Measure(Scale now, Pt p1, Pt p2, bool sourceFrame)
    {
        return sourceFrame ? Dist(p1, p2) * now.Factor : Dist(p1, p2);
    }

    /// <summary>p after the plan goes from its scale now to factor, about the plan's top-left corner.</summary>
    public static Pt Rescale(Scale now, double factor, Pt p)
    {
        var rel = factor / now.Factor;
        return new Pt(now.Origin.X + (p.X - now.Origin.X) * rel, now.Origin.Y + (p.Y - now.Origin.Y) * rel);
    }

    /// <summary>
    /// A wall rectangle at the new scale. One still as the import made it
    /// (four corners, square, its stamped thickness) keeps a round thickness:
    /// its centreline scales and the thickness is rounded again from the
    /// detected value, so a repeat does not drift. One the user has reshaped
    /// scales as drawn. thickness is what to stamp, or NaN to leave the stamp.
    /// </summary>
    public static List<Pt> RescaleWall(
        Scale now, double factor, IList<Pt> ring, double detected, double stamped, double round, out double thickness)
    {
        thickness = double.NaN;
        if (ring.Count == 4 && detected > 0 && stamped > 0)
        {
            var side = Dist(ring[1], ring[2]);
            var square = Math.Abs(Dot(Sub(ring[1], ring[0]), Sub(ring[2], ring[1])))
                <= 1e-3 * Math.Max(1.0, Dist(ring[0], ring[1]) * side);
            if (square && Math.Abs(side - stamped) <= 1.0 && Math.Abs(Dist(ring[3], ring[0]) - stamped) <= 1.0
                && Dist(ring[0], ring[1]) > 1e-6)
            {
                thickness = RoundTo(detected * factor, round);
                return Band(
                    Rescale(now, factor, Mid(ring[0], ring[3])),
                    Rescale(now, factor, Mid(ring[1], ring[2])),
                    thickness);
            }
        }
        var scaled = new List<Pt>(ring.Count);
        foreach (var p in ring) scaled.Add(Rescale(now, factor, p));
        return scaled;
    }

    /// <summary>100 from 1:100.</summary>
    public static bool TryRatio(string ratio, out double denominator)
    {
        denominator = 0;
        if (string.IsNullOrWhiteSpace(ratio)) return false;
        var parts = ratio.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var one) || one <= 0) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var many) || many <= 0) return false;
        denominator = many / one;
        return true;
    }

    /// <summary>
    /// Pixel size of a PNG and its dpi from the pHYs chunk (0 when the file
    /// does not say). The plan image is sized from these and the scale.
    /// </summary>
    public static bool TryPngSize(byte[] file, out int width, out int height, out double dpi)
    {
        width = height = 0;
        dpi = 0;
        byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (file == null || file.Length < 24) return false;
        for (var i = 0; i < signature.Length; i++)
            if (file[i] != signature[i]) return false;
        width = BigEndian(file, 16);
        height = BigEndian(file, 20);
        var at = 8;
        while (at + 12 <= file.Length)
        {
            var length = BigEndian(file, at);
            var type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            if (type == "IDAT" || type == "IEND" || length < 0) break;
            // pHYs: pixels per unit in x, in y, then 1 when the unit is the metre.
            if (type == "pHYs" && at + 17 <= file.Length && file[at + 16] == 1)
                dpi = Math.Round(BigEndian(file, at + 8) * 0.0254, 1);
            at += 12 + length;
        }
        return width > 0 && height > 0;
    }

    static int BigEndian(byte[] bytes, int at)
    {
        return (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
    }

    /// <summary>Width in model mm of an image px wide, scanned at dpi, of a drawing at 1:denominator.</summary>
    public static double ImageWidthMm(int px, double dpi, double denominator)
    {
        return px / dpi * 25.4 * denominator;
    }

    static double RoundTo(double value, double step)
    {
        if (step <= 0) return value;
        return Math.Max(step, Math.Round(value / step, MidpointRounding.AwayFromZero) * step);
    }

    static string Count(int n, string noun)
    {
        return n + " " + noun + (n == 1 ? "" : "s");
    }

    static string Mm(double mm)
    {
        return Math.Round(mm).ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>A place on the plan in metres, as the receipt names it.</summary>
    static string At(Pt p)
    {
        return (p.X / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + ", "
            + (p.Y / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " m";
    }

    static double Sin(double degrees) => Math.Sin(degrees * Math.PI / 180.0);
    static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static Pt Mid(Pt a, Pt b) => new Pt((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
    static Pt Sub(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);
    static Pt Along(Pt p, Pt direction, double by) => new Pt(p.X + direction.X * by, p.Y + direction.Y * by);
    static Pt Normal(Pt u) => new Pt(-u.Y, u.X);
    static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
    static double Cross(Pt a, Pt b) => a.X * b.Y - a.Y * b.X;

    static Pt Unit(Pt a, Pt b)
    {
        var length = Dist(a, b);
        return length <= 1e-12 ? new Pt(1, 0) : new Pt((b.X - a.X) / length, (b.Y - a.Y) / length);
    }
}
