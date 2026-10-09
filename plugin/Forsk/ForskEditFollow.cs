using System;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>
    /// Rhino's own edits outside a Forsk call, followed on the next idle, with or
    /// without the Forsk window open: a Delete of what the daylight map was drawn
    /// from marks the map (DaylightMap.AfterDelete), a deleted wall has the rooms
    /// detected again (RoomFollow.AfterDelete), and a door or window block
    /// moved with Drag, Move or the gumball takes its marker along its wall
    /// (OpeningDrag, FS-Z383WAWP). No layer or object change inside the events.
    /// </summary>
    public static class ForskEditFollow
    {
        static readonly OpeningDrag Drags = new OpeningDrag();
        static MapEdit _deletedEdit;
        static bool _wallDeleted;
        static bool _hooked;

        public static void Start()
        {
            if (_hooked) return;
            _hooked = true;
            RhinoDoc.DeleteRhinoObject += OnDeleted;
            RhinoDoc.ReplaceRhinoObject += OnReplaced;
            RhinoApp.Idle += OnIdle;
        }

        public static void Stop()
        {
            if (!_hooked) return;
            _hooked = false;
            RhinoDoc.DeleteRhinoObject -= OnDeleted;
            RhinoDoc.ReplaceRhinoObject -= OnReplaced;
            RhinoApp.Idle -= OnIdle;
        }

        static bool Outside(RhinoDoc doc) => ForskCalls.Depth == 0 && doc != null && !doc.UndoActive && !doc.RedoActive;

        static void OnDeleted(object sender, RhinoObjectEventArgs e)
        {
            var obj = e?.TheObject;
            var doc = obj?.Document;
            if (!Outside(doc) || obj.Attributes == null) return;
            var index = obj.Attributes.LayerIndex;
            var onRoomLayer = index >= 0 && index < doc.Layers.Count
                && string.Equals(doc.Layers[index].Name, "A-ROOM", StringComparison.OrdinalIgnoreCase);
            var kind = obj.Attributes.GetUserString("forsk:kind");
            var edit = DaylightMap.AfterDelete(kind, onRoomLayer);
            if (edit > _deletedEdit) _deletedEdit = edit;
            if (RoomFollow.AfterDelete(kind)) _wallDeleted = true;
        }

        static void OnReplaced(object sender, RhinoReplaceObjectEventArgs e)
        {
            var before = e?.OldRhinoObject;
            var after = e?.NewRhinoObject;
            var doc = after?.Document;
            if (!Outside(doc) || after.Attributes == null) return;
            var kind = after.Attributes.GetUserString("forsk:kind");
            if (string.Equals(kind, "opening_marker", StringComparison.OrdinalIgnoreCase))
            {
                Drags.MarkerMoved(after.Id.ToString());
                return;
            }
            if (!string.Equals(kind, "opening", StringComparison.OrdinalIgnoreCase) || before?.Geometry == null || after.Geometry == null) return;
            var a = before.Geometry.GetBoundingBox(true).Center;
            var b = after.Geometry.GetBoundingBox(true).Center;
            Drags.Moved(after.Attributes.GetUserString("forsk:marker_id"), a.X, a.Y, b.X, b.Y);
        }

        static void OnIdle(object sender, EventArgs e)
        {
            FollowRooms();
            MarkMap();
            FollowOpenings();
        }

        /// <summary>
        /// Rooms again after Rhino's Delete of a wall, in an undo record of their
        /// own: the first Undo puts the rooms back, the next the wall. The map is
        /// marked after, since detection redraws the rooms it was drawn from.
        /// </summary>
        static void FollowRooms()
        {
            if (!_wallDeleted) return;
            _wallDeleted = false;
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            var record = doc.BeginUndoRecord("Forsk: rooms after deleted wall");
            try
            {
                var envelope = ForskTools.Command("rooms_detect", new JObject());
                if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                    RhinoApp.WriteLine("Forsk: the rooms were not detected again after the wall was deleted. " + envelope?["message"]);
            }
            finally
            {
                doc.EndUndoRecord(record);
            }
            doc.Views.Redraw();
        }

        static void MarkMap()
        {
            if (_deletedEdit == MapEdit.None) return;
            var edit = _deletedEdit;
            _deletedEdit = MapEdit.None;
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null && RhinoMCPFunctions.MarkMapAfterEdit(doc, edit) > 0) doc.Views.Redraw();
        }

        static void FollowOpenings()
        {
            if (!Drags.Pending) return;
            var moves = Drags.Take();
            var doc = RhinoDoc.ActiveDoc;
            if (moves.Count == 0 || doc == null) return;
            var record = doc.BeginUndoRecord("Forsk: follow dragged opening");
            try
            {
                foreach (var move in moves)
                {
                    if (!Guid.TryParse(move.Marker, out var id)) continue;
                    var marker = doc.Objects.FindId(id);
                    if (marker?.Geometry == null) continue;
                    var at = marker.Geometry.GetBoundingBox(true).Center;
                    var envelope = ForskTools.Command("move_opening", new JObject
                    {
                        ["id"] = move.Marker,
                        ["to"] = new JArray(at.X + move.Dx, at.Y + move.Dy)
                    });
                    if (!string.Equals(envelope?["status"]?.ToString(), "success", StringComparison.OrdinalIgnoreCase))
                        RhinoApp.WriteLine("Forsk: the dragged opening stayed where it was on the plan. " + envelope?["message"]);
                }
            }
            finally
            {
                doc.EndUndoRecord(record);
            }
            doc.Views.Redraw();
        }
    }
}
