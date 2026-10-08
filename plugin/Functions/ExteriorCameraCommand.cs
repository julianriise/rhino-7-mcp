using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// exterior_render: the Perspective view stands outside and looks straight at
/// the building from the north, east, south or west (north by default), level
/// at eye height with a 24 mm lens (ExteriorCamera), in the Forsk Exterior look
/// with the roof shown. The shot is kept as the named view "Exterior north",
/// replaced when asked again, and the view picker lists it.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("exterior_render", ModelView = true)]
    public JObject ExteriorRender(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var box = BuildingBox(doc);
        if (!box.IsValid) throw new InvalidOperationException("There is no building to look at yet. Generate 3D first.");
        var shot = ExteriorCamera.For(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y, box.Min.Z, box.Max.Z, parameters?["direction"]?.ToString())
            ?? throw new InvalidOperationException("There is no building to look at yet. Generate 3D first.");
        var view = doc.Views.GetViewList(true, false)
            .FirstOrDefault(v => v.ActiveViewport.IsPerspectiveProjection && v.ActiveViewport.Name == "Perspective")
            ?? doc.Views.GetViewList(true, false).FirstOrDefault(v => v.ActiveViewport.IsPerspectiveProjection)
            ?? throw new InvalidOperationException("No perspective view to move. Open the Perspective view first.");
        var vp = view.ActiveViewport;
        vp.ChangeToPerspectiveProjection(true, ExteriorCamera.LensMm);
        vp.Camera35mmLensLength = ExteriorCamera.LensMm;
        vp.SetCameraLocations(new Point3d(shot.TargetX, shot.TargetY, shot.TargetZ), new Point3d(shot.EyeX, shot.EyeY, shot.EyeZ));
        vp.CameraUp = Vector3d.ZAxis;
        // The materials as they are, the roof shown and soft shadows (Julian, 2026-10-08).
        InteriorMaterials(doc);
        var look = ForskInteriorHost.Ensure(exterior: true);
        if (look != null) vp.DisplayMode = look;
        doc.Views.ActiveView = view;
        view.Redraw();

        var name = ExteriorCamera.ViewName(shot.Side);
        // save false: the window's side card tries a shot; Confirm keeps it.
        var save = parameters?["save"]?.Type != JTokenType.Boolean || parameters["save"].Value<bool>();
        var active = false;
        if (save)
        {
            var old = doc.NamedViews.FindByName(name);
            if (old >= 0) doc.NamedViews.Delete(old);
            var index = doc.NamedViews.Add(name, vp.Id);
            active = index >= 0 && doc.NamedViews.Restore(index, vp);
            if (index >= 0) ForskInteriorHost.Remember(doc, name, exterior: true);
        }
        return new JObject
        {
            ["view"] = name,
            ["eye"] = new JArray(Math.Round(shot.EyeX, 1), Math.Round(shot.EyeY, 1), Math.Round(shot.EyeZ, 1)),
            ["target"] = new JArray(Math.Round(shot.TargetX, 1), Math.Round(shot.TargetY, 1), Math.Round(shot.TargetZ, 1)),
            ["lens_mm"] = ExteriorCamera.LensMm,
            ["direction"] = shot.Side,
            ["saved"] = save,
            ["active"] = active,
            ["look"] = vp.DisplayMode?.EnglishName,
            ["message"] = "Perspective looks at the building " + shot.From + " at eye height"
                + (save ? ", saved as the named view " + name + ". Orbit or walk to adjust before a render." : ".")
        };
    }

    /// <summary>The building's box: the walls' extent, up to the roof's top when there is a roof, hidden or not.</summary>
    private static BoundingBox BuildingBox(RhinoDoc doc)
    {
        var box = BoundingBox.Empty;
        foreach (var obj in EnumerateDocObjects(doc))
            if (IsForskGenerated(obj) && string.Equals(GetForskKind(obj), "wall", StringComparison.OrdinalIgnoreCase))
                box.Union(obj.Geometry.GetBoundingBox(true));
        if (!box.IsValid) return box;
        var roof = doc.Layers.FindName("A-ROOF");
        if (roof != null && !roof.IsDeleted)
            foreach (var obj in doc.Objects.FindByLayer(roof) ?? new Rhino.DocObjects.RhinoObject[0])
            {
                var top = obj?.Geometry?.GetBoundingBox(true);
                if (top.HasValue && top.Value.IsValid && top.Value.Max.Z > box.Max.Z)
                    box = new BoundingBox(box.Min, new Point3d(box.Max.X, box.Max.Y, top.Value.Max.Z));
            }
        return box;
    }
}
