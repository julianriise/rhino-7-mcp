using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The render looks on the views Interior render and Exterior render make
/// (ForskInterior): Rendered with subtle shadows and the lines off, the hidden
/// roof drawn (a ceiling inside, the roof outside), and the look put back when
/// a saved render view is restored, since a Rhino 7 named view keeps no
/// display mode. The view picker lists the saved views and shows one (Show).
/// </summary>
internal static class ForskInteriorHost
{
    static CeilingConduit _conduit;
    static readonly EventHandler<ViewEventArgs> Modified = (_, args) => OnModified(args?.View);
    static readonly EventHandler<RhinoObjectEventArgs> Changed = (_, __) => _conduit?.Drop();
    static readonly EventHandler<RhinoReplaceObjectEventArgs> Replaced = (_, __) => _conduit?.Drop();
    /// <summary>Viewports waiting for a look on the next idle, and whether it is the exterior one.</summary>
    static readonly Dictionary<Guid, bool> Pending = new Dictionary<Guid, bool>();

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

    static readonly Dictionary<string, int> SessionRevision = new Dictionary<string, int>();

    /// <summary>
    /// Forsk Interior (or Forsk Exterior), imported from a patched Rendered
    /// export; an earlier revision is replaced. Rendered itself if the import fails.
    /// </summary>
    internal static DisplayModeDescription Ensure(bool exterior = false)
    {
        var name = exterior ? ForskInterior.ExteriorModeName : ForskInterior.ModeName;
        var existing = ForskWhiteHost.Find(name);
        if (existing != null && !ForskInterior.NeedsReimport(StoredRevision(name))) return existing;
        Func<string, string> look = exterior ? ForskInterior.PatchExterior : (Func<string, string>)ForskInterior.Patch;
        Func<string, string> patch = text => ForskInterior.WithFreshId(look(text), Guid.NewGuid());
        if (ForskWhiteHost.Import(name, patch, DisplayModeDescription.RenderedId))
            StoreRevision(name);
        return ForskWhiteHost.Find(name)
            ?? DisplayModeDescription.GetDisplayMode(DisplayModeDescription.RenderedId);
    }

    /// <summary>"ForskInteriorRevision", "ForskExteriorRevision".</summary>
    static string RevisionKey(string name) => name.Replace(" ", "") + "Revision";

    static int StoredRevision(string name)
    {
        if (SessionRevision.TryGetValue(name, out var seen) && seen >= ForskInterior.ModeRevision) return seen;
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return 0;
        try { return settings.GetInteger(RevisionKey(name), 0); }
        catch (Exception) { return 0; }
    }

    static void StoreRevision(string name)
    {
        SessionRevision[name] = ForskInterior.ModeRevision;
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return;
        try { settings.SetInteger(RevisionKey(name), ForskInterior.ModeRevision); }
        catch (Exception) { }
    }

    /// <summary>
    /// A perspective view whose camera is a saved render view's takes that
    /// look on the next idle: Rhino is still restoring the view when this fires.
    /// </summary>
    static void OnModified(RhinoView view)
    {
        try
        {
            var doc = view?.Document;
            if (doc == null || view is RhinoPageView) return;
            var vp = view.ActiveViewport;
            if (vp == null || !vp.IsPerspectiveProjection) return;
            foreach (var exterior in new[] { false, true })
            {
                var stored = doc.Strings.GetValue(KeyOf(exterior));
                if (string.IsNullOrEmpty(stored)) continue;
                if (IsLook(vp, exterior) || !IsSaved(doc, stored, vp)) continue;
                if (Pending.Count == 0) RhinoApp.Idle += ApplyPending;
                Pending[vp.Id] = exterior;
                return;
            }
        }
        catch (Exception)
        {
            // The view keeps its look. A restore never fails because of Forsk.
        }
    }

    static string KeyOf(bool exterior) => exterior ? ForskInterior.ExteriorViewsKey : ForskInterior.ViewsKey;

    static bool IsLook(RhinoViewport vp, bool exterior)
    {
        var name = vp?.DisplayMode?.EnglishName;
        return exterior ? ForskInterior.IsExteriorMode(name) : ForskInterior.IsMode(name);
    }

    static bool IsSaved(RhinoDoc doc, string stored, RhinoViewport vp)
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
        var pending = new Dictionary<Guid, bool>(Pending);
        Pending.Clear();
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;
        foreach (var view in doc.Views.GetViewList(true, false))
        {
            var vp = view?.ActiveViewport;
            if (vp == null || !pending.TryGetValue(vp.Id, out var exterior) || IsLook(vp, exterior)) continue;
            var mode = Ensure(exterior);
            if (mode == null) continue;
            vp.DisplayMode = mode;
            view.Redraw();
        }
    }

    /// <summary>Adds a named view a render saved to the document's interior (or exterior) views.</summary>
    internal static void Remember(RhinoDoc doc, string viewName, bool exterior = false)
    {
        if (doc == null || string.IsNullOrWhiteSpace(viewName)) return;
        var stored = doc.Strings.GetValue(KeyOf(exterior));
        var next = ForskInterior.AddView(stored, viewName);
        if (next != stored) doc.Strings.SetString(KeyOf(exterior), next);
    }

    /// <summary>The saved interior (or exterior) render views that still exist, in the order they were made.</summary>
    internal static List<string> Views(RhinoDoc doc, bool exterior)
    {
        var names = new List<string>();
        if (doc == null) return names;
        foreach (var name in ForskInterior.Views(doc.Strings.GetValue(KeyOf(exterior))))
            if (doc.NamedViews.FindByName(name) >= 0) names.Add(name);
        return names;
    }

    /// <summary>
    /// The view picker: the active model view (else a perspective one) takes the
    /// saved render view and its look. The reason when it cannot, else null.
    /// </summary>
    internal static string Show(RhinoDoc doc, string viewName, bool exterior)
    {
        if (doc == null) return "No file open.";
        var index = doc.NamedViews.FindByName(viewName);
        if (index < 0) return "There is no saved view called " + viewName + ".";
        var view = doc.Views.ActiveView;
        if (view == null || view is RhinoPageView)
            view = doc.Views.GetViewList(true, false).FirstOrDefault(v => v.ActiveViewport.IsPerspectiveProjection);
        if (view == null) return "No model view to show it in.";
        var vp = view.ActiveViewport;
        if (!doc.NamedViews.Restore(index, vp)) return "The view did not change.";
        vp.DisplayMode = Ensure(exterior);
        RhinoMCPFunctions.ShowRoofForRender(doc);
        doc.Views.ActiveView = view;
        view.Redraw();
        return null;
    }

    static double[] Xyz(Point3d p) => new[] { p.X, p.Y, p.Z };

    /// <summary>
    /// The hidden roof: A-ROOF is hidden by default, so an interior view would
    /// look up into the sky and an exterior one at a house with no roof. In a
    /// render view the hidden roof is drawn: plaster white as the ceiling inside,
    /// the roof's dark grey outside. A visible roof draws itself.
    /// </summary>
    sealed class CeilingConduit : DisplayConduit
    {
        readonly List<Mesh> _meshes = new List<Mesh>();
        readonly DisplayMaterial _plaster = new DisplayMaterial(Color.FromArgb(ForskInterior.Ceiling.R, ForskInterior.Ceiling.G, ForskInterior.Ceiling.B));
        readonly DisplayMaterial _roof = new DisplayMaterial(Color.FromArgb(ForskInterior.Roof.R, ForskInterior.Roof.G, ForskInterior.Roof.B));
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
                var look = viewport?.DisplayMode?.EnglishName;
                if (doc == null || viewport == null || !ForskInterior.IsRenderMode(look)) return;
                var roof = doc.Layers.FindName("A-ROOF");
                if (roof == null || roof.IsDeleted || roof.IsVisible) return;
                Ensure(doc, roof);
                var material = ForskInterior.IsExteriorMode(look) ? _roof : _plaster;
                foreach (var mesh in _meshes)
                    args.Display.DrawMeshShaded(mesh, material);
            }
            catch (Exception)
            {
                // A draw failure leaves the view without the roof. The next change rebuilds it.
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
