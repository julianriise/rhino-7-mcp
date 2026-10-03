using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// F5.3 cross sections on Print. section_add stores a section (Sections.Def)
/// in the document; layout_pack draws its marker on the plan and its own
/// sheet: the model trimmed on the vertical plane through the HLD like the
/// plan, the solid poché of everything the plane cuts (walls, slabs, roof;
/// openings stay gaps), lines beyond the cut thin, the ground line, level
/// marks, the free height of each room it crosses, gesims and møne. Every
/// piece is a forsk:kind=drawing on S-DRAW::Section X with forsk:view
/// section_x, so the smoke reads them back against the model.
/// </summary>
public partial class RhinoMCPFunctions
{
    private const string NoSectionMessage = "No section. Give a room, from and to points, or a line_id.";

    private static List<Sections.Def> ReadSectionDefs(RhinoDoc doc)
    {
        if (doc == null) return new List<Sections.Def>();
        return Sections.Read(doc.Strings.GetValue(Sections.MetaSection, Sections.MetaKey));
    }

    private static void WriteSectionDefs(RhinoDoc doc, List<Sections.Def> defs)
    {
        if (defs == null || defs.Count == 0)
            doc.Strings.Delete(Sections.MetaSection, Sections.MetaKey);
        else
            doc.Strings.SetString(Sections.MetaSection, Sections.MetaKey, Sections.Write(defs));
    }

    [McpCommand("section_add")]
    public JObject SectionAdd(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");
        var defs = ReadSectionDefs(doc);
        var letter = (parameters?["letter"]?.ToString() ?? "").Trim().ToUpperInvariant();
        if (letter.Length == 0)
            letter = Sections.NextLetter(defs);
        if (letter == null || letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z')
            throw new InvalidOperationException("letter is one of A to Z.");
        var look = parameters?["look"]?.ToString();
        var axis = parameters?["axis"]?.ToString();
        if (!string.IsNullOrEmpty(axis) && axis != "long" && axis != "cross")
            throw new InvalidOperationException("axis is long or cross.");
        if (!string.IsNullOrEmpty(look) && !Sections.TryCompass(look, out _))
            throw new InvalidOperationException("look is north, south, east or west.");

        Sections.Def def = null;
        var roomQuery = parameters?["room"]?.ToString();
        if (TryReadPoint(parameters?["from"], out var from) && TryReadPoint(parameters?["to"], out var to))
            def = Sections.Along(letter, from, to, look, axis ?? "");
        else if (parameters?["line_id"] != null)
        {
            if (!Guid.TryParse(parameters["line_id"].ToString(), out var lineId) || !(doc.Objects.FindId(lineId)?.Geometry is Curve line))
                throw new InvalidOperationException("line_id is not a curve in this document.");
            def = Sections.Along(letter,
                new RoomDetect.Pt(line.PointAtStart.X, line.PointAtStart.Y),
                new RoomDetect.Pt(line.PointAtEnd.X, line.PointAtEnd.Y), look, axis ?? "");
        }
        else if (!string.IsNullOrWhiteSpace(roomQuery))
        {
            var rooms = PlanRooms(doc).Where(r => r.Ring != null).ToList();
            var room = MatchRoom(rooms, roomQuery);
            if (room == null)
            {
                var names = rooms.Select(r => r.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                throw new InvalidOperationException("No room named " + roomQuery.Trim() + ". Rooms: "
                    + (names.Count == 0 ? "none (run rooms_detect)" : string.Join(", ", names)) + ".");
            }
            if (!TryWallFootprint(doc, out var footprint))
                throw new InvalidOperationException(NothingToLayOutMessage);
            def = Sections.Through(letter, room.Inside, footprint, axis, look);
            def.Room = room.ScheduleId;
        }
        if (def == null)
            throw new InvalidOperationException(NoSectionMessage);
        // The viewport dialog's label. The marker and the sheet stay the letter.
        var givenName = parameters?["name"]?.ToString();
        if (!string.IsNullOrWhiteSpace(givenName))
            def.Name = givenName.Trim();

        var replaced = defs.RemoveAll(d => string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase)) > 0;
        defs.Add(def);
        WriteSectionDefs(doc, defs);
        // A sheet of the old line would print as if it were this one.
        if (replaced)
            RemoveLayoutPages(doc, Sections.View(letter), false);
        var record = Sections.Record(def);
        var where = string.IsNullOrEmpty(def.Room) ? "" : " through " + RoomName(doc, def.Room);
        return new JObject
        {
            ["section"] = record,
            ["sections"] = new JArray(ReadSectionDefs(doc).Select(Sections.Record)),
            ["view"] = Sections.View(letter),
            ["replaced"] = replaced,
            ["message"] = (replaced ? "Moved section " : "Added section ") + letter + "–" + letter + where
                + ". layout_pack views plan and " + Sections.View(letter) + " draws its marker and sheet."
        };
    }

    [McpCommand("section_clear")]
    public JObject SectionClear(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            throw new InvalidOperationException("No active document.");
        var defs = ReadSectionDefs(doc);
        var letter = (parameters?["letter"]?.ToString() ?? "").Trim().ToUpperInvariant();
        var doomed = defs.Where(d => letter.Length == 0 || string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var def in doomed)
            RemoveLayoutPages(doc, Sections.View(def.Letter), false);
        // Its marker leaves the plan now; the next plan bake would drop it too.
        var cleared = new HashSet<string>(doomed.Select(d => d.Letter), StringComparer.OrdinalIgnoreCase);
        var markers = EnumerateDocObjects(doc)
            .Where(o => string.Equals(GetForskKind(o), "drawing", StringComparison.OrdinalIgnoreCase)
                && string.Equals(o.Attributes.GetUserString("forsk:role"), "section_marker", StringComparison.Ordinal)
                && cleared.Contains(o.Attributes.GetUserString("forsk:section") ?? ""))
            .Select(o => o.Id).ToList();
        foreach (var id in markers)
            doc.Objects.Delete(id, true);
        defs.RemoveAll(doomed.Contains);
        WriteSectionDefs(doc, defs);
        var letters = doomed.Select(d => d.Letter).ToList();
        return new JObject
        {
            ["removed"] = new JArray(letters),
            ["sections"] = new JArray(defs.Select(Sections.Record)),
            ["message"] = letters.Count == 0
                ? "No section to remove."
                : "Removed section " + string.Join(", ", letters) + ". The plan marker goes on the next layout_pack."
        };
    }

    private static bool TryReadPoint(JToken token, out RoomDetect.Pt point)
    {
        point = default;
        if (!(token is JArray xy) || xy.Count < 2) return false;
        try
        {
            point = new RoomDetect.Pt(xy[0].ToObject<double>(), xy[1].ToObject<double>());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The room a user named: its id, then its name, then a name that contains the words.</summary>
    private static PlanRoom MatchRoom(List<PlanRoom> rooms, string query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return null;
        return rooms.FirstOrDefault(r => string.Equals(r.ScheduleId, q, StringComparison.OrdinalIgnoreCase))
            ?? rooms.FirstOrDefault(r => string.Equals(r.Name, q, StringComparison.OrdinalIgnoreCase))
            ?? rooms.FirstOrDefault(r => !string.IsNullOrEmpty(r.Name)
                && (r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || q.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private static string RoomName(RhinoDoc doc, string id)
    {
        var room = PlanRooms(doc).FirstOrDefault(r => string.Equals(r.ScheduleId, id, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(room?.Name) ? id : room.Name;
    }

    /// <summary>The XY box of the Forsk walls: what a room's section spans.</summary>
    private static bool TryWallFootprint(RhinoDoc doc, out RoomDetect.Box footprint)
    {
        footprint = default;
        var box = BoundingBox.Empty;
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!IsForskGenerated(obj) || !string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase)) continue;
            var one = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (one.IsValid) box.Union(one);
        }
        if (!box.IsValid) return false;
        footprint = new RoomDetect.Box(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y);
        return true;
    }

    /// <summary>
    /// A stored section as a drawing view: looking along its look, up Z, its
    /// pack placed on its own row of the drawing layers.
    /// </summary>
    private static bool TryGetSectionView(RhinoDoc doc, string view, out SheetViewSpec spec, out Sections.Def def)
    {
        spec = default;
        def = null;
        if (!Sections.TryLetter(view, out var letter)) return false;
        var defs = ReadSectionDefs(doc);
        var at = defs.FindIndex(d => string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return false;
        def = defs[at];
        spec = new SheetViewSpec
        {
            View = Sections.View(letter),
            Layer = "S-SECT-" + letter,
            Offset = new Vector3d(15000.0 * at, -30000, 0),
            Look = new Vector3d(def.Look.X, def.Look.Y, 0),
            Up = Vector3d.ZAxis
        };
        return true;
    }

    /// <summary>The vertical cut plane. Its normal is the look: the side a document clipping plane keeps.</summary>
    private static Plane SectionClip(Sections.Def def)
    {
        return new Plane(new Point3d(def.A.X, def.A.Y, 0), new Vector3d(def.Look.X, def.Look.Y, 0));
    }

    /// <summary>A model point on the sheet: the HLD map in full 3D (the plan's ToDrawing drops Z), then the pack's shift.</summary>
    private static Point3d ToSheet(Point3d point, Transform worldToHld, Vector3d delta)
    {
        if (worldToHld.IsValid && !worldToHld.IsIdentity)
            point.Transform(worldToHld);
        point += delta;
        point.Z = 0;
        return point;
    }

    /// <summary>
    /// Model-space cut loops as polylines in section coordinates (u along the
    /// line from A, z up), for the heights. Read before the loops move onto
    /// the sheet.
    /// </summary>
    private static List<List<RoomDetect.Pt>> SectionCoords(List<List<Curve>> groups, Sections.Def def)
    {
        var rings = new List<List<RoomDetect.Pt>>();
        foreach (var group in groups ?? new List<List<Curve>>())
            foreach (var curve in group ?? new List<Curve>())
            {
                var points = CurvePoints(curve);
                if (points.Count < 3) continue;
                rings.Add(points.Select(p => new RoomDetect.Pt(Sections.U(def, p.X, p.Y), p.Z)).ToList());
            }
        return rings;
    }

    /// <summary>A closed curve's vertices, or 4 points per span (16 to 128) when it is not a polyline. Z kept.</summary>
    private static List<Point3d> CurvePoints(Curve curve)
    {
        var points = new List<Point3d>();
        if (curve == null) return points;
        if (curve.TryGetPolyline(out Polyline poly) && poly != null && poly.Count >= 3)
            return poly.ToList();
        var ts = curve.DivideByCount(Math.Min(Math.Max(curve.SpanCount * 4, 16), 128), true);
        if (ts == null) return points;
        foreach (var t in ts)
            points.Add(curve.PointAt(t));
        return points;
    }

    /// <summary>What the section sheet drew, for the page record and the smoke.</summary>
    private sealed class SectionStats
    {
        public string Letter;
        public int CutLines;
        public int Beyond;
        public double? GroundZ;
        public JArray Levels = new JArray();
        public JArray FreeHeights = new JArray();
        public JArray CutWalls = new JArray();
        public JObject Poche = new JObject();
    }

    /// <summary>
    /// A section page's record: the line, what was cut (the walls, and the
    /// poché per solid), the ground, the levels and the free heights. The
    /// cut rule is on_cut_loop: an edge on a cut loop is cut (see Sections).
    /// </summary>
    private static JObject SectionPageRecord(RhinoDoc doc, string view, GreyscaleDrawing drawn)
    {
        TryGetSectionView(doc, view, out _, out var def);
        var stats = drawn.Section;
        return new JObject
        {
            ["letter"] = stats.Letter,
            ["line"] = def == null ? null : Sections.Record(def),
            ["cut_rule"] = "on_cut_loop",
            ["cut_lines"] = stats.CutLines,
            ["beyond"] = stats.Beyond,
            ["fills"] = drawn.Fills,
            ["cut_walls"] = stats.CutWalls,
            ["poche"] = stats.Poche,
            ["ground_z"] = stats.GroundZ.HasValue ? new JValue(Math.Round(stats.GroundZ.Value, 3)) : JValue.CreateNull(),
            ["levels"] = stats.Levels,
            ["free_heights"] = stats.FreeHeights
        };
    }

    /// <summary>
    /// The section's heavy cut outlines and thin lines beyond, the ground
    /// line, the level marks and each crossed room's free height. Cut is an
    /// HLD edge on a cut loop (Sections.IsCut); the loops themselves are the
    /// heavy line over the poché. Returns false when nothing was drawn so the
    /// caller keeps the plain curves.
    /// </summary>
    private bool TryBakeSectionLinework(
        RhinoDoc doc, Layer layer, int scale, Sections.Def def, string view,
        Transform worldToHld, Vector3d delta, List<WeightedCurve> visible, List<List<Curve>> fillGroups,
        List<List<RoomDetect.Pt>> cutUz, List<List<RoomDetect.Pt>> roofUz, IList<Sections.Solid> solids,
        double tol, ref BoundingBox box, ref int index, ref int count, SectionStats stats)
    {
        if (doc == null || layer == null || scale < 1 || def == null) return false;
        var pattern = SolidPatternIndex(doc);
        if (pattern < 0) return false;
        var stamp = new SymbolStamp { Extra = new Dictionary<string, string> { ["forsk:view"] = view, ["forsk:section"] = def.Letter } };
        var added = 0;

        var loops = new List<Curve>();
        foreach (var group in fillGroups ?? new List<List<Curve>>())
            loops.AddRange((group ?? new List<Curve>()).Where(c => c != null));
        var rings = PocheRings(fillGroups, tol).SelectMany(w => w).ToList();
        foreach (var item in visible ?? new List<WeightedCurve>())
        {
            var curve = item.Curve;
            if (curve == null || !curve.IsValid) continue;
            if (Sections.IsCut(EdgeSamples(curve), rings, 2.0)) continue;
            var n = AddStroke(doc, layer, curve, PenBeyond, scale, false, pattern, tol,
                "beyond", null, null, null, ref box, ref index, ref count, stamp);
            stats.Beyond += n;
            added += n;
        }
        foreach (var loop in loops)
        {
            var n = AddStroke(doc, layer, loop, PenCut, scale, false, pattern, tol,
                "cut", null, null, null, ref box, ref index, ref count, stamp);
            stats.CutLines += n;
            added += n;
        }
        if (added == 0 || !box.IsValid) return false;

        var d = Sections.Direction(def);
        Point3d Model(double u, double z) => new Point3d(def.A.X + d.X * u, def.A.Y + d.Y * u, z);
        double SheetY(double z) => ToSheet(Model(0, z), worldToHld, delta).Y;
        var left = box.Min.X;
        var right = box.Max.X;

        // Every room with an outline the line runs through, tagged on the plan or too small for its tag.
        var crossed = new List<(PlanRoom Room, double U, double FloorZ)>();
        foreach (var room in PlanRooms(doc))
        {
            if (room.Ring == null || room.Ring.Count < 3) continue;
            var ring = room.Ring.Select(p => new RoomDetect.Pt(p.X, p.Y)).ToList();
            if (!Sections.RoomSpan(ring, def, out var u0, out var u1)) continue;
            crossed.Add((room, 0.5 * (u0 + u1), room.Ring[0].Z));
        }
        // The same heights the facades print (FacadeSheet).
        var heights = Sections.ModelHeights(solids, crossed.Select(c => c.FloorZ), roofUz);
        var groundZ = heights.Ground;
        if (groundZ.HasValue)
        {
            var y = SheetY(groundZ.Value);
            var over = Sections.GroundOverMm * scale;
            using (var ground = new LineCurve(new Point3d(left - over, y, 0), new Point3d(right + over, y, 0)))
            {
                var extra = new SymbolStamp { Extra = new Dictionary<string, string>(stamp.Extra) { ["forsk:z"] = Mm(groundZ.Value) } };
                if (AddStroke(doc, layer, ground, Sections.GroundPen(PrintProfiles.Active), scale, false, pattern, tol,
                        "ground_line", null, null, null, ref box, ref index, ref count, extra) > 0)
                    stats.GroundZ = groundZ.Value;
            }
        }

        var valueHeight = Sections.ValueMm * scale;
        var marks = Sections.PlaceLevels(heights.Levels, SheetY, right, scale,
            text => ModelTextWidth(doc, text, valueHeight) / scale);
        foreach (var mark in marks)
        {
            var extra = new Dictionary<string, string>(stamp.Extra)
            {
                ["forsk:level_kind"] = mark.Level.Kind,
                ["forsk:level_z"] = Mm(mark.Level.Z),
                ["forsk:level_value"] = mark.Level.Value.ToString(CultureInfo.InvariantCulture)
            };
            using (var line = new LineCurve(Sheet(mark.LineA), Sheet(mark.LineB)))
                added += AddStroke(doc, layer, line, Sections.LevelPen(PrintProfiles.Active), scale, false, pattern, tol,
                    "level", "line", null, null, ref box, ref index, ref count, new SymbolStamp { Extra = extra });
            added += AddSolidTriangle(doc, layer, mark.Triangle, pattern, tol, "level", extra, ref box, ref index, ref count);
            if (AddSheetText(doc, layer, mark.Level.Text, new Plane(Sheet(mark.TextAt), Vector3d.XAxis, Vector3d.YAxis),
                    valueHeight, scale, "level", extra, ref box, ref index, ref count))
            {
                added++;
                stats.Levels.Add(new JObject
                {
                    ["kind"] = mark.Level.Kind,
                    ["z"] = Math.Round(mark.Level.Z, 3),
                    ["value"] = mark.Level.Value,
                    ["text"] = mark.Level.Text
                });
            }
        }

        foreach (var (room, u, floorZ) in crossed)
        {
            var free = Sections.FreeHeight(cutUz, u, floorZ, 1.0);
            if (!free.HasValue) continue;
            var extra = new Dictionary<string, string>(stamp.Extra)
            {
                ["forsk:room"] = room.ScheduleId,
                ["forsk:free_height"] = free.Value.ToString(CultureInfo.InvariantCulture),
                ["forsk:floor_z"] = Mm(floorZ)
            };
            var foot = ToSheet(Model(u, floorZ), worldToHld, delta);
            var head = ToSheet(Model(u, floorZ + free.Value), worldToHld, delta);
            using (var line = new LineCurve(foot, head))
                added += AddStroke(doc, layer, line, PenThin, scale, false, pattern, tol,
                    "free_height", "line", null, null, ref box, ref index, ref count, new SymbolStamp { Extra = extra });
            foreach (var end in new[] { foot, head })
            {
                var tick = PlanDims.TickMm * 0.5 * scale;
                using (var line = new LineCurve(end + new Vector3d(-tick, -tick, 0), end + new Vector3d(tick, tick, 0)))
                    added += AddStroke(doc, layer, line, PenThin, scale, false, pattern, tol,
                        "free_height", "tick", null, null, ref box, ref index, ref count, new SymbolStamp { Extra = extra });
            }
            // Reads bottom to top, left of its line, like a vertical dimension's value.
            var text = Sections.ClearHeightText(free.Value);
            var at = new Point3d(foot.X - (PlanDims.TextGapMm + 0.5 * Sections.ValueMm) * scale, 0.5 * (foot.Y + head.Y), 0);
            if (AddSheetText(doc, layer, text, new Plane(at, Vector3d.YAxis, -Vector3d.XAxis),
                    valueHeight, scale, "free_height", extra, ref box, ref index, ref count))
            {
                added++;
                stats.FreeHeights.Add(new JObject
                {
                    ["room"] = room.ScheduleId,
                    ["name"] = room.Name ?? "",
                    ["floor_z"] = Math.Round(floorZ, 3),
                    ["free_height"] = free.Value
                });
            }
        }
        return added > 0;
    }

    /// <summary>The floors, walls and roofs among the sources, as Sections.ModelHeights reads them.</summary>
    private static List<Sections.Solid> HeightSolids(IList<RhinoObject> sources)
    {
        var solids = new List<Sections.Solid>();
        foreach (var obj in sources ?? new List<RhinoObject>())
        {
            var bbox = obj?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!bbox.IsValid) continue;
            solids.Add(new Sections.Solid { Kind = GetForskKind(obj) ?? "", MinZ = bbox.Min.Z, MaxZ = bbox.Max.Z });
        }
        return solids;
    }

    private static Point3d Sheet(RoomDetect.Pt p) => new Point3d(p.X, p.Y, 0);

    private static string Mm(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>An edge's ends, quarters and middle, in drawing mm.</summary>
    private static List<RoomDetect.Pt> EdgeSamples(Curve curve)
    {
        var samples = new List<RoomDetect.Pt>();
        foreach (var f in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            Point3d p;
            if (curve.NormalizedLengthParameter(f, out var t)) p = curve.PointAt(t);
            else p = curve.PointAt(curve.Domain.ParameterAt(f));
            samples.Add(new RoomDetect.Pt(p.X, p.Y));
        }
        return samples;
    }

    /// <summary>A solid black triangle (an arrow or a level mark).</summary>
    private static int AddSolidTriangle(
        RhinoDoc doc, Layer layer, RoomDetect.Pt[] corners, int pattern, double tol, string role,
        IDictionary<string, string> stamps, ref BoundingBox box, ref int index, ref int count)
    {
        if (corners == null || corners.Length != 3) return 0;
        var outline = new PolylineCurve(new[] { Sheet(corners[0]), Sheet(corners[1]), Sheet(corners[2]), Sheet(corners[0]) });
        Hatch[] hatches = null;
        try { hatches = Hatch.Create(outline, pattern, 0.0, 1.0, Math.Max(tol, 0.01)); }
        catch (Exception) { hatches = null; }
        outline.Dispose();
        var added = 0;
        foreach (var hatch in hatches ?? new Hatch[0])
        {
            if (hatch == null) continue;
            var attr = DrawAttr(layer, FormatStableId("d", index), role, "arrow", null);
            foreach (var pair in stamps ?? new Dictionary<string, string>())
                attr.SetUserString(pair.Key, pair.Value);
            var hatchBox = hatch.GetBoundingBox(true);
            Guid id;
            try { id = doc.Objects.AddHatch(hatch, attr); }
            catch (Exception) { id = Guid.Empty; }
            hatch.Dispose();
            if (id == Guid.Empty) continue;
            if (hatchBox.IsValid) box.Union(hatchBox);
            index++;
            count++;
            added++;
        }
        return added;
    }

    /// <summary>1:1 sheet text of a paper height (model height = paper × scale), centred on its plane's origin.</summary>
    private static bool AddSheetText(
        RhinoDoc doc, Layer layer, string text, Plane plane, double height, int scale, string role,
        IDictionary<string, string> stamps, ref BoundingBox box, ref int index, ref int count)
    {
        var entity = PlanAnnotation(doc, text, plane, height);
        if (entity == null) return false;
        var attr = DrawAttr(layer, FormatStableId("d", index), role, "text", null);
        foreach (var pair in stamps ?? new Dictionary<string, string>())
            attr.SetUserString(pair.Key, pair.Value);
        Guid id;
        try { id = doc.Objects.AddText(entity, attr); }
        catch (Exception) { id = Guid.Empty; }
        finally { entity.Dispose(); }
        if (id == Guid.Empty) return false;
        var written = doc.Objects.FindId(id);
        if (written?.Geometry is TextEntity stored)
        {
            var model = stored.TextHeight * (stored.DimensionScale > 0 ? stored.DimensionScale : 1.0);
            var paper = OpeningTypes.PaperTextHeight(model, scale, doc.LayoutSpaceAnnotationScalingEnabled);
            written.Attributes.SetUserString("forsk:text_height", model.ToString("0.###", CultureInfo.InvariantCulture));
            written.Attributes.SetUserString("forsk:paper_height", paper.ToString("0.###", CultureInfo.InvariantCulture));
            written.CommitChanges();
            var stampBox = stored.GetBoundingBox(true);
            if (stampBox.IsValid) box.Union(stampBox);
        }
        index++;
        count++;
        return true;
    }

    /// <summary>
    /// Every stored section's marker on the plan (Sections.PlaceMarker): last,
    /// after the dimensions, so each end steps out past the tags, marks,
    /// symbols and chains already on the plan layer, and the next section's
    /// marker past this one.
    /// </summary>
    private int BakeSectionMarkers(
        RhinoDoc doc, Layer layer, int scale, Transform worldToHld, Vector3d delta, int pattern, double tol,
        ref BoundingBox box, ref int index, ref int count, ref PlanStats stats)
    {
        var defs = ReadSectionDefs(doc);
        if (defs.Count == 0) return 0;
        var outlines = PlanOutlines(doc, worldToHld, delta, tol);
        var taken = PlanObstacles(doc, layer).Select(o => o.Box).ToList();
        var height = Sections.LetterMm * scale;
        var added = 0;
        foreach (var def in defs)
        {
            var a = ToDrawing(new Point3d(def.A.X, def.A.Y, 0), worldToHld, delta);
            var b = ToDrawing(new Point3d(def.B.X, def.B.Y, 0), worldToHld, delta);
            var lookTo = ToDrawing(new Point3d(def.A.X + def.Look.X * 1000, def.A.Y + def.Look.Y * 1000, 0), worldToHld, delta) - a;
            lookTo.Z = 0;
            if (!lookTo.Unitize()) continue;
            var probe = PlanAnnotation(doc, def.Letter, Plane.WorldXY, height);
            var extent = probe?.GetBoundingBox(true) ?? BoundingBox.Empty;
            probe?.Dispose();
            var hx = extent.IsValid ? 0.5 * (extent.Max.X - extent.Min.X) : 0.35 * height;
            var hy = extent.IsValid ? 0.5 * (extent.Max.Y - extent.Min.Y) : 0.5 * height;
            var marker = Sections.PlaceMarker(
                new RoomDetect.Pt(a.X, a.Y), new RoomDetect.Pt(b.X, b.Y), new RoomDetect.Pt(lookTo.X, lookTo.Y),
                outlines, taken, scale, hx, hy);
            if (!marker.Clear) stats.SectionMarkersBlocked.Add(def.Letter);
            var stamps = new Dictionary<string, string> { ["forsk:section"] = def.Letter };
            var drawn = 0;
            foreach (var end in marker.Ends)
            {
                using (var stroke = new LineCurve(Sheet(end.StrokeA), Sheet(end.StrokeB)))
                    drawn += AddStroke(doc, layer, stroke, PenCut, scale, false, pattern, tol,
                        "section_marker", "stroke", null, null, ref box, ref index, ref count, new SymbolStamp { Extra = stamps });
                drawn += AddSolidTriangle(doc, layer, end.Arrow, pattern, tol, "section_marker", stamps, ref box, ref index, ref count);
                var letterStamps = new Dictionary<string, string>(stamps) { ["forsk:symbol"] = "letter" };
                if (AddSheetText(doc, layer, def.Letter, new Plane(Sheet(end.LetterAt), Vector3d.XAxis, Vector3d.YAxis),
                        height, scale, "section_marker", letterStamps, ref box, ref index, ref count))
                    drawn++;
                taken.AddRange(end.Boxes);
            }
            if (drawn > 0) stats.SectionMarkers.Add(def.Letter);
            added += drawn;
        }
        return added;
    }
}
