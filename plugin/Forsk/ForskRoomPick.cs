using System;
using Rhino;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace RhinoMCPPlugin.Forsk
{
    /// <summary>One click on a room's floor (its plate or its marker), for a tool that needs a room. Empty when cancelled.</summary>
    static class ForskRoomPick
    {
        public static Guid Pick(RhinoDoc doc, string prompt)
        {
            if (doc == null) return Guid.Empty;
            var go = new GetObject();
            go.SetCommandPrompt(prompt.TrimEnd('.'));
            go.GeometryFilter = ObjectType.AnyObject;
            go.SubObjectSelect = false;
            go.GroupSelect = false;
            go.EnablePreSelect(false, true);
            go.SetCustomGeometryFilter((obj, geometry, component) =>
            {
                var kind = obj?.Attributes?.GetUserString("forsk:kind");
                return string.Equals(kind, "room_plate", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kind, "room", StringComparison.OrdinalIgnoreCase);
            });
            if (go.Get() != GetResult.Object) return Guid.Empty;
            return go.Object(0).ObjectId;
        }
    }
}
