using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// show_view: the view picker. The active model viewport takes the view's
/// camera (ViewPicker), its look (Forsk White or Forsk Technical), and a zoom
/// to the selection, else the building, else everything. The viewport layout
/// never changes. A plan view gets the 1.2 m plan cut whatever it is named.
/// </summary>
public partial class RhinoMCPFunctions
{
    [McpCommand("show_view", ModelView = true)]
    public JObject ShowView(JObject parameters)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) throw new InvalidOperationException("No active document.");
        var id = (parameters?["view"]?.ToString() ?? "").Trim().ToLowerInvariant();
        var camera = ViewPicker.For(id)
            ?? throw new InvalidOperationException("Unknown view. Use perspective, plan, north, east, south or west.");
        var view = doc.Views.ActiveView;
        if (view == null || view is RhinoPageView)
            view = doc.Views.GetViewList(true, false).FirstOrDefault(v => !(v is RhinoPageView))
                ?? throw new InvalidOperationException("No model viewport to show the view in.");
        var vp = view.MainViewport;
        if (camera.Parallel) vp.ChangeToParallelProjection(true);
        else vp.ChangeToPerspectiveProjection(true, 50);
        vp.SetCameraDirection(new Vector3d(camera.Dx, camera.Dy, camera.Dz), false);
        vp.CameraUp = new Vector3d(camera.Ux, camera.Uy, camera.Uz);

        var mode = ForskWhiteHost.ModeFor(doc, ForskTechnicalHost.LookOf(vp));
        if (mode != null) vp.DisplayMode = mode;
        // Back from a render: the roof it turned on is hidden again.
        HideRoofAfterRender(doc);

        var zoom = "selection";
        var box = BoundingBox.Empty;
        foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
        {
            var b = obj?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (b.IsValid) box.Union(b);
        }
        if (!box.IsValid)
        {
            zoom = "building";
            box = ClayBoundingBox(CollectLayoutClay(doc, true, out _));
        }
        if (box.IsValid) vp.ZoomBoundingBox(box);
        else
        {
            zoom = "everything";
            vp.ZoomExtents();
        }
        doc.Views.ActiveView = view;
        ForskPlanCutHost.Apply(doc, ForskWhiteHost.Enabled());
        view.Redraw();

        var label = ViewPicker.Label(id);
        var shows = vp.DisplayMode?.EnglishName ?? "";
        return new JObject
        {
            ["view"] = id,
            ["label"] = label,
            ["viewport"] = vp.Name,
            ["display"] = shows,
            ["zoom"] = zoom,
            ["message"] = label + " in the " + vp.Name + " viewport, zoomed to the " + zoom
                + (shows.Length > 0 ? ", " + shows + "." : ".")
        };
    }
}
