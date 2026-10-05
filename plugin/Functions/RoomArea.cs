using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoMCPPlugin.Forsk;
using Pt = RhinoMCPPlugin.Functions.RoomDetect.Pt;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Room areas drawn by hand (AI detection misses rooms): the clicked corners
/// become a closed outline on A-ROOM at the floor's height, which rooms_detect
/// takes as the user's word for that region. With replace, the selected
/// room's outline goes first. A new wall across a drawn room cuts that
/// outline into the rooms it makes (SplitDrawnRooms).
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("add_room_area", ModelView = true, Map = MapEdit.Opening)]
    public JObject AddRoomArea(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        var tol = Math.Max(doc.ModelAbsoluteTolerance, 1.0);
        var clicks = new List<Pt>();
        foreach (var token in parameters?["points"] as JArray ?? new JArray())
        {
            if (token is JArray xy && xy.Count >= 2)
                clicks.Add(new Pt(xy[0].Value<double>(), xy[1].Value<double>()));
        }
        if (!RoomAreaPlan.TryRing(clicks, tol, out var ring, out var why))
            throw new ArgumentException(why);

        var replace = parameters?["replace"]?.Type == JTokenType.Boolean && parameters["replace"].Value<bool>();
        RhinoObject old = replace ? ResolveRoom(doc, parameters) : null;
        double z;
        if (old != null)
        {
            var box = old.Geometry.GetBoundingBox(true);
            z = box.IsValid ? box.Min.Z : 0;
        }
        else RoomScene(doc, tol, new JArray(), out z);

        var layer = EnsureLayer(doc, ResolveRoomSourceLayer(doc, "A-ROOM")?.Name ?? "A-ROOM", Color.FromArgb(200, 180, 120));
        var attr = new ObjectAttributes { LayerIndex = layer.Index };
        // A redrawn room keeps the type the user gave it.
        if (old != null && string.Equals(old.Attributes.GetUserString(RoomTypes.SourceKey), RoomTypes.User, StringComparison.Ordinal))
        {
            attr.SetUserString(RoomTypes.Key, old.Attributes.GetUserString(RoomTypes.Key));
            attr.SetUserString(RoomTypes.SourceKey, RoomTypes.User);
        }
        var oldName = old?.Attributes.GetUserString(RoomNameKey) ?? old?.Name;
        if (old != null && !DeleteRoomCurve(doc, old.Id.ToString()))
            throw new InvalidOperationException("The selected room could not be replaced.");
        var id = doc.Objects.AddCurve(RoomOutline(ring, z), attr);
        if (id == Guid.Empty)
            throw new InvalidOperationException("Area not added. Rhino did not take the outline.");

        var rooms = RoomsDetect(new JObject());
        var area = Math.Abs(RoomDetect.Area(ring)) / 1000000.0;
        var name = doc.Objects.FindId(id)?.Attributes.GetUserString(RoomNameKey);
        if (string.IsNullOrWhiteSpace(name)) name = RoomDetect.UnnamedRoom(ForskSpeech.Norwegian);
        var squareMetres = area.ToString("0.0", CultureInfo.InvariantCulture);
        var message = old != null
            ? "Redrew " + (string.IsNullOrWhiteSpace(oldName) ? "the room" : oldName) + " as " + name + ", " + squareMetres + " m²."
            : "Added " + name + ", " + squareMetres + " m².";
        doc.Views.Redraw();
        return new JObject
        {
            ["id"] = id.ToString(),
            ["name"] = name,
            ["area_m2"] = Math.Round(area, 2, MidpointRounding.AwayFromZero),
            ["corners"] = ring.Count,
            ["replaced"] = old?.Id.ToString(),
            ["rooms"] = rooms?["count"] ?? 0,
            ["ok"] = true,
            ["message"] = message
        };
    }

    /// <summary>The area tool's outline: add_room_area, then the daylight map is out of date. Not a bridge command.</summary>
    public JObject AddDrawnRoomArea(JObject parameters)
    {
        var result = AddRoomArea(parameters);
        MarkMapAfterEdit(RhinoDoc.ActiveDoc, MapEdit.Opening);
        return result;
    }

    /// <summary>The one selected room's record id, or null when not exactly one room is selected.</summary>
    public static string SelectedRoomId(RhinoDoc doc)
    {
        try { return ResolveRoom(doc, null).Id.ToString(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The height a new area sits at: a redrawn room's own, else the lowest wall's, else 0.</summary>
    public double AreaHeight(RhinoDoc doc, string roomId)
    {
        if (Guid.TryParse(roomId, out var guid))
        {
            var box = doc.Objects.FindId(guid)?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Unset;
            if (box.IsValid) return box.Min.Z;
        }
        RoomScene(doc, Math.Max(doc.ModelAbsoluteTolerance, 1.0), new JArray(), out var z);
        return z;
    }

    private sealed class RoomCurveUndo
    {
        public Guid Id;
        public Curve Curve;
        public ObjectAttributes Attr;
        public List<Guid> Added = new List<Guid>();
    }

    /// <summary>
    /// Every outline drawn by hand on A-ROOM that the new wall cuts through
    /// becomes the rooms it makes: the largest piece keeps the outline (and
    /// its name, if no label says otherwise), the others are new outlines.
    /// Detected rooms are left to rooms_detect, which splits them itself.
    /// </summary>
    private List<RoomCurveUndo> SplitDrawnRooms(RhinoDoc doc, Pt from, Pt to, double thickness, double tol)
    {
        var undo = new List<RoomCurveUndo>();
        var roomLayer = ResolveRoomSourceLayer(doc, "A-ROOM");
        if (roomLayer == null) return undo;
        foreach (var obj in ObjectsOnLayer(doc, roomLayer.Name).ToList())
        {
            if (!(obj.Geometry is Curve curve) || !curve.IsClosed || IsRoomMarker(obj)) continue;
            var forskId = obj.Attributes.GetUserString("forsk:id") ?? obj.Attributes.GetUserString(RoomIdKey) ?? "";
            if (string.Equals(obj.Attributes.GetUserString(RoomSourceKey), DetectedRoomSource, StringComparison.Ordinal)
                || forskId.StartsWith(DetectedRoomPrefix, StringComparison.Ordinal))
                continue;
            var pts = LoopPoints(FlattenToWorldXY(curve, tol), tol);
            if (pts == null || pts.Count < 3) continue;
            var pieces = RoomAreaPlan.Split(PlanPoints(pts), from, to, thickness, tol);
            if (pieces == null) continue;
            var z = curve.GetBoundingBox(true).Min.Z;
            var item = new RoomCurveUndo { Id = obj.Id, Curve = curve.DuplicateCurve(), Attr = obj.Attributes.Duplicate() };
            if (obj.IsLocked) doc.Objects.Unlock(obj.Id, false);
            var kept = obj.Attributes.Duplicate();
            // The tag point and area are worked out again for the smaller room.
            kept.SetUserString(RoomAtKey, null);
            kept.SetUserString("forsk:area", null);
            if (!doc.Objects.Replace(obj.Id, RoomOutline(pieces[0], z)) || !doc.Objects.ModifyAttributes(obj.Id, kept, true))
                throw new InvalidOperationException("Could not split the room outline.");
            undo.Add(item);
            for (var i = 1; i < pieces.Count; i++)
            {
                var added = doc.Objects.AddCurve(RoomOutline(pieces[i], z), new ObjectAttributes { LayerIndex = obj.Attributes.LayerIndex });
                if (added != Guid.Empty) item.Added.Add(added);
            }
        }
        return undo;
    }

    private static void RestoreDrawnRooms(RhinoDoc doc, List<RoomCurveUndo> undo)
    {
        if (undo == null) return;
        foreach (var item in undo)
        {
            foreach (var id in item.Added)
                DeleteRoomCurve(doc, id.ToString());
            if (doc.Objects.FindId(item.Id) == null) continue;
            doc.Objects.Unlock(item.Id, false);
            doc.Objects.Replace(item.Id, item.Curve);
            doc.Objects.ModifyAttributes(item.Id, item.Attr, true);
        }
    }
}
