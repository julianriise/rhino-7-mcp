using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Forsk Interior on the views Jump inside makes (ForskInterior): a copy of
/// Rendered with the lines off, a ceiling drawn from the hidden roof, and the
/// look put back when a saved interior view is restored, since a Rhino 7
/// named view keeps no display mode.
/// </summary>
internal static class ForskInteriorHost
{
    static CeilingConduit _conduit;
    static readonly EventHandler<ViewEventArgs> Modified = (_, args) => OnModified(args?.View);
    static readonly EventHandler<RhinoObjectEventArgs> Changed = (_, __) => _conduit?.Drop();
    static readonly EventHandler<RhinoReplaceObjectEventArgs> Replaced = (_, __) => _conduit?.Drop();
    static readonly HashSet<Guid> Pending = new HashSet<Guid>();

    internal static void Start()
    {
        if (_conduit != null) return;
        _conduit = new CeilingConduit { Enabled = true };
        RhinoView.Modified += Modified;
        RhinoDoc.AddRhinoObject += Changed;
        RhinoDoc.DeleteRhinoObject += Changed;
        RhinoDoc.ReplaceRhinoObject += Replaced;
    }

    internal static void Stop()
    {
        if (_conduit == null) return;
        RhinoView.Modified -= Modified;
        RhinoDoc.AddRhinoObject -= Changed;
        RhinoDoc.DeleteRhinoObject -= Changed;
        RhinoDoc.ReplaceRhinoObject -= Replaced;
        _conduit.Enabled = false;
        _conduit = null;
    }

    /// <summary>Forsk Interior, made from Rendered the first time. Rendered itself if the copy fails.</summary>
    internal static DisplayModeDescription Ensure()
    {
        var existing = DisplayModeDescription.FindByName(ForskInterior.ModeName);
        if (existing != null && ForskInterior.IsMode(existing.EnglishName))
            return DisplayModeDescription.GetDisplayMode(existing.Id) ?? existing;
        var id = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.RenderedId, ForskInterior.ModeName);
        var mode = id == Guid.Empty ? null : DisplayModeDescription.GetDisplayMode(id);
        if (mode == null) return DisplayModeDescription.GetDisplayMode(DisplayModeDescription.RenderedId);
        var attrs = mode.DisplayAttributes;
        attrs.ShowCurves = false;
        attrs.ShowPoints = false;
        attrs.ShowAnnotations = false;
        attrs.ShowText = false;
        attrs.ShowLights = false;
        attrs.ShowClippingPlanes = false;
        attrs.ShowIsoCurves = false;
        attrs.ShowSurfaceEdges = false;
        attrs.ShowTangentEdges = false;
        attrs.ShowTangentSeams = false;
        attrs.ViewSpecificAttributes.DrawGrid = false;
        attrs.ViewSpecificAttributes.DrawWorldAxes = false;
        DisplayModeDescription.UpdateDisplayMode(mode);
        return DisplayModeDescription.GetDisplayMode(id) ?? mode;
    }

    /// <summary>
    /// A perspective view whose camera is a saved interior view's takes the
    /// look on the next idle: Rhino is still restoring the view when this fires.
    /// </summary>
    static void OnModified(RhinoView view)
    {
        try
        {
            var doc = view?.Document;
            if (doc == null || view is RhinoPageView) return;
            var stored = doc.Strings.GetValue(ForskInterior.ViewsKey);
            if (string.IsNullOrEmpty(stored)) return;
            var vp = view.ActiveViewport;
            if (vp == null || !vp.IsPerspectiveProjection || ForskInterior.IsMode(vp.DisplayMode?.EnglishName)) return;
            if (!IsSavedInterior(doc, stored, vp)) return;
            if (Pending.Count == 0) RhinoApp.Idle += ApplyPending;
            Pending.Add(vp.Id);
        }
        catch (Exception)
        {
            // The view keeps its look. A restore never fails because of Forsk.
        }
    }

    static bool IsSavedInterior(RhinoDoc doc, string stored, RhinoViewport vp)
    {
        var eye = Xyz(vp.CameraLocation);
        var target = Xyz(vp.CameraTarget);
        foreach (var name in ForskInterior.Views(stored))
        {
            var index = doc.NamedViews.FindByName(name);
            if (index < 0) continue;
            var saved = doc.NamedViews[index]?.Viewport;
            if (saved != null && ForskInterior.SameCamera(eye, target, Xyz(saved.CameraLocation), Xyz(saved.TargetPoint)))
                return true;
        }
        return false;
    }

    static void ApplyPending(object sender, EventArgs e)
    {
        RhinoApp.Idle -= ApplyPending;
        var ids = new List<Guid>(Pending);
        Pending.Clear();
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;
        var mode = Ensure();
        if (mode == null) return;
        foreach (var view in doc.Views.GetViewList(true, false))
        {
            var vp = view?.ActiveViewport;
            if (vp == null || !ids.Contains(vp.Id) || ForskInterior.IsMode(vp.DisplayMode?.EnglishName)) continue;
            vp.DisplayMode = mode;
            view.Redraw();
        }
    }

    /// <summary>Adds a named view Jump inside saved to the document's interior views.</summary>
    internal static void Remember(RhinoDoc doc, string viewName)
    {
        if (doc == null || string.IsNullOrWhiteSpace(viewName)) return;
        var stored = doc.Strings.GetValue(ForskInterior.ViewsKey);
        var next = ForskInterior.AddView(stored, viewName);
        if (next != stored) doc.Strings.SetString(ForskInterior.ViewsKey, next);
    }

    static double[] Xyz(Point3d p) => new[] { p.X, p.Y, p.Z };

    /// <summary>
    /// The ceiling: A-ROOF is hidden by default, so an interior view would look
    /// up into the sky. In a Forsk Interior view, the hidden roof is drawn in
    /// plaster white. A visible roof draws itself.
    /// </summary>
    sealed class CeilingConduit : DisplayConduit
    {
        readonly List<Mesh> _meshes = new List<Mesh>();
        readonly DisplayMaterial _plaster = new DisplayMaterial(Color.FromArgb(ForskInterior.Ceiling.R, ForskInterior.Ceiling.G, ForskInterior.Ceiling.B));
        uint _serial;
        bool _ready;

        public CeilingConduit()
        {
            SpaceFilter = ActiveSpace.ModelSpace;
        }

        public void Drop() => _ready = false;

        protected override void PostDrawObjects(DrawEventArgs args)
        {
            try
            {
                var viewport = args?.Viewport;
                var doc = args?.RhinoDoc;
                if (doc == null || viewport == null || !ForskInterior.IsMode(viewport.DisplayMode?.EnglishName)) return;
                var roof = doc.Layers.FindName("A-ROOF");
                if (roof == null || roof.IsDeleted || roof.IsVisible) return;
                Ensure(doc, roof);
                foreach (var mesh in _meshes)
                    args.Display.DrawMeshShaded(mesh, _plaster);
            }
            catch (Exception)
            {
                // A draw failure leaves the view without a ceiling. The next change rebuilds it.
            }
        }

        void Ensure(RhinoDoc doc, Layer roof)
        {
            if (_ready && _serial == doc.RuntimeSerialNumber) return;
            _meshes.Clear();
            _serial = doc.RuntimeSerialNumber;
            _ready = true;
            // The layer is off: FindByLayer still lists its objects, an enumerator does not.
            foreach (var obj in doc.Objects.FindByLayer(roof) ?? new RhinoObject[0])
            {
                var brep = obj?.Geometry as Brep ?? (obj?.Geometry as Extrusion)?.ToBrep(true);
                if (brep == null) continue;
                var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                if (meshes == null) continue;
                foreach (var mesh in meshes)
                    if (mesh != null && mesh.IsValid) _meshes.Add(mesh);
            }
        }
    }
}
