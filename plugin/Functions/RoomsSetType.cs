using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The user sets a room's type. Source becomes user, so a later guess cannot replace it.
/// One room: the id, or the selection. Perspective colours pick the new type up on the next draw.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("rooms_set_type")]
    public JObject RoomsSetType(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No file open.");
        if (!AcceptRoomType(parameters?["room_type"]?.ToString(), out var key))
            throw new InvalidOperationException("Room type must be one of: " + string.Join(", ", RoomTypes.All) + ".");

        var hadId = !string.IsNullOrWhiteSpace(parameters?["id"]?.ToString());
        var rooms = RoomTargets(doc, parameters);
        if (rooms.Count == 0)
        {
            throw new InvalidOperationException(hadId
                ? "No room found."
                : ListSelected(doc).Count == 0
                    ? "Nothing is selected. Click the room in Rhino, then say it again."
                    : "Select a room.");
        }
        // Every room picked (Julian, 2026-10-09). Refusing more than one made the chat
        // try again with a room id it guessed, and confirm a room that was not picked.
        var names = new List<string>();
        foreach (var marker in rooms)
        {
            var attr = marker.Attributes.Duplicate();
            attr.SetUserString(RoomTypes.Key, key);
            attr.SetUserString(RoomTypes.SourceKey, RoomTypes.User);
            // A plated room's marker is locked so the plate is the click. Attributes still have to change.
            var locked = marker.IsLocked;
            if (locked) doc.Objects.Unlock(marker.Id, false);
            var wrote = doc.Objects.ModifyAttributes(marker, attr, true);
            if (locked) doc.Objects.Lock(marker.Id, false);
            if (!wrote) throw new InvalidOperationException("The room's type was not stored.");
            CopyRoomTypeToPlates(doc, marker.Id, key);
            var name = attr.GetUserString(RoomNameKey);
            if (string.IsNullOrWhiteSpace(name)) name = marker.Name;
            names.Add(string.IsNullOrWhiteSpace(name) ? "Room" : name.Trim());
        }

        RoomTypeColorHost.Invalidate();
        doc.Views.Redraw();
        var ids = new JArray();
        foreach (var marker in rooms) ids.Add(marker.Id.ToString());
        return new JObject
        {
            ["id"] = rooms[0].Id.ToString(),
            ["ids"] = ids,
            ["count"] = rooms.Count,
            ["room_type"] = key,
            ["room_type_source"] = RoomTypes.User,
            ["message"] = RoomTypes.SetSentence(names, key)
        };
    }

    [McpCommand("rooms_colors", ReadOnly = true)]
    public JObject RoomsColors(JObject parameters)
    {
        var on = parameters?["on"];
        if (on != null && on.Type != JTokenType.Null)
            RoomTypeColorHost.SetEnabled(on.Value<bool>());
        var enabled = RoomTypeColorHost.Enabled();
        RhinoDoc.ActiveDoc?.Views.Redraw();
        return new JObject
        {
            ["on"] = enabled,
            ["message"] = enabled
                ? "Perspective floors are coloured by room type."
                : "Perspective floors are white."
        };
    }

    /// <summary>A stored key, or a label word such as soverom. Anything else is refused.</summary>
    static bool AcceptRoomType(string text, out string key)
    {
        if (RoomTypes.TryParse(text, out key)) return true;
        var mapped = RoomTypes.FromLabel(text);
        if (mapped == RoomTypes.Unassigned) return false;
        key = mapped;
        return true;
    }

    static List<RhinoObject> RoomTargets(RhinoDoc doc, JObject parameters)
    {
        var found = new List<RhinoObject>();
        var seen = new HashSet<Guid>();
        void Add(RhinoObject obj)
        {
            var room = AsRoom(doc, obj);
            if (room == null || !seen.Add(room.Id)) return;
            found.Add(room);
        }

        var id = parameters?["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(id))
        {
            id = id.Trim();
            if (Guid.TryParse(id, out var guid))
                Add(doc.Objects.FindId(guid));
            foreach (var obj in EnumerateDocObjects(doc))
            {
                var room = AsRoom(doc, obj);
                if (room == null) continue;
                var forsk = room.Attributes.GetUserString("forsk:id");
                var roomId = room.Attributes.GetUserString(RoomIdKey);
                var name = room.Attributes.GetUserString(RoomNameKey) ?? room.Name;
                if (string.Equals(forsk, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(roomId, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                    Add(room);
            }
            return found;
        }

        foreach (var obj in ListSelected(doc))
            Add(obj);
        return found;
    }

    /// <summary>The room marker a plate, a marker, or a named room curve stands for.</summary>
    static RhinoObject AsRoom(RhinoDoc doc, RhinoObject obj)
    {
        if (obj == null) return null;
        var handle = ResolveRoomHandle(doc, obj);
        return IsRoomRecord(handle) ? handle : null;
    }

    static void CopyRoomTypeToPlates(RhinoDoc doc, Guid markerId, string key)
    {
        var marker = markerId.ToString();
        foreach (var obj in EnumerateDocObjects(doc))
        {
            if (!string.Equals(GetForskKind(obj), RoomPlate.Kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(obj.Attributes.GetUserString("forsk:marker"), marker, StringComparison.OrdinalIgnoreCase)) continue;
            var attr = obj.Attributes.Duplicate();
            attr.SetUserString(RoomTypes.Key, key);
            attr.SetUserString(RoomTypes.SourceKey, RoomTypes.User);
            doc.Objects.ModifyAttributes(obj, attr, true);
        }
    }
}
