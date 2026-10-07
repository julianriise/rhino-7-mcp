using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// jump_inside: the Perspective view moves into a room (InteriorCamera),
/// looking north, east, south or west (north by default), level at 1.2 m
/// above its floor with a 24 mm lens, and the shot is kept as a named view
/// called after the room, replaced when asked again.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("jump_inside", ModelView = true)]
    public JObject JumpInside(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var room = PickFurnitureRoom(doc, FurnitureRooms(doc), parameters?["room"]?.ToString(), null);
        var direction = parameters?["direction"]?.ToString();
        var shot = InteriorCamera.For(room.Outline, direction)
            ?? throw new InvalidOperationException("That room has no outline to stand in.");
        var view = doc.Views.GetViewList(true, false)
            .FirstOrDefault(v => v.ActiveViewport.IsPerspectiveProjection && v.ActiveViewport.Name == "Perspective")
            ?? doc.Views.GetViewList(true, false).FirstOrDefault(v => v.ActiveViewport.IsPerspectiveProjection)
            ?? throw new InvalidOperationException("No perspective view to move. Open the Perspective view first.");
        var floor = StairFloorTop(doc);
        var vp = view.ActiveViewport;
        vp.ChangeToPerspectiveProjection(true, InteriorCamera.LensMm);
        vp.Camera35mmLensLength = InteriorCamera.LensMm;
        var z = floor + InteriorCamera.EyeMm;
        vp.SetCameraLocations(new Point3d(shot.Target.X, shot.Target.Y, z), new Point3d(shot.Eye.X, shot.Eye.Y, z));
        vp.CameraUp = Vector3d.ZAxis;
        doc.Views.ActiveView = view;
        view.Redraw();

        var name = string.IsNullOrEmpty(room.Name) ? room.ScheduleId : room.Name;
        // save false: the window's direction card tries a shot; Save view keeps it.
        var save = parameters?["save"]?.Type != JTokenType.Boolean || parameters["save"].Value<bool>();
        if (save)
        {
            var old = doc.NamedViews.FindByName(name);
            if (old >= 0) doc.NamedViews.Delete(old);
            doc.NamedViews.Add(name, vp.Id);
        }
        return new JObject
        {
            ["view"] = name,
            ["room"] = room.ScheduleId,
            ["eye"] = new JArray(Math.Round(shot.Eye.X, 1), Math.Round(shot.Eye.Y, 1), Math.Round(z, 1)),
            ["target"] = new JArray(Math.Round(shot.Target.X, 1), Math.Round(shot.Target.Y, 1), Math.Round(z, 1)),
            ["lens_mm"] = InteriorCamera.LensMm,
            ["direction"] = shot.From.Substring("looking ".Length),
            ["saved"] = save,
            ["message"] = "Perspective is inside " + RoomWords(room) + ", " + shot.From + " at 1.2 m"
                + (save ? ", saved as the named view " + name + ". Orbit or walk to adjust before a render." : ".")
        };
    }
}
