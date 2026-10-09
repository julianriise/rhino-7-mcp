using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Flat pastel on each room plate, in the perspective view only.
/// A plan or an elevation stays the line drawing, and a Jump inside view
/// (Forsk Interior) shows the floor's material. Off draws nothing,
/// so the perspective view stays Forsk White. No material and no lights:
/// the mesh is drawn with its vertex colour.
/// </summary>
internal static class RoomTypeColorHost
{
    const string SettingKey = "ForskRoomTypeColors";

    static RoomTypeColorConduit _conduit;

    internal static void Start()
    {
        if (_conduit != null) return;
        _conduit = new RoomTypeColorConduit { Enabled = true };
    }

    internal static void Stop()
    {
        if (_conduit == null) return;
        _conduit.Enabled = false;
        _conduit = null;
    }

    internal static bool Enabled()
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return true;
        return settings.GetBool(SettingKey, true);
    }

    internal static void SetEnabled(bool enabled)
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings != null) settings.SetBool(SettingKey, enabled);
        Invalidate();
    }

    /// <summary>
    /// Drops the cached meshes. The cache otherwise keys only on the document
    /// serial, and a quiet attribute write during Generate 3D does not make
    /// the next redraw rebuild it. rooms_set_type and the end of the bake call this.
    /// </summary>
    internal static void Invalidate()
    {
        _conduit?.Drop();
    }

    sealed class RoomTypeColorConduit : DisplayConduit
    {
        readonly List<Mesh> _meshes = new List<Mesh>();
        uint _serial;
        bool _ready;

        public RoomTypeColorConduit()
        {
            SpaceFilter = ActiveSpace.ModelSpace;
            GeometryFilter = ObjectType.None;
        }

        public void Drop()
        {
            _ready = false;
            _meshes.Clear();
        }

        protected override void PostDrawObjects(DrawEventArgs args)
        {
            try
            {
                var doc = args?.RhinoDoc;
                var viewport = args?.Viewport;
                var display = args?.Display;
                if (doc == null || viewport == null || display == null) return;
                // Draw area shows the walls only: no floor colours.
                if (global::RhinoMCPPlugin.Forsk.ForskDrawArea.WallsOnly) return;
                var dir = viewport.CameraDirection;
                if (!RoomTypes.ShowInView(Enabled(), viewport.IsParallelProjection, dir.X, dir.Y, dir.Z, viewport.DisplayMode?.EnglishName)) return;
                Ensure(doc);
                foreach (var mesh in _meshes)
                    display.DrawMeshFalseColors(mesh);
            }
            catch (Exception)
            {
                // A draw failure leaves the view as it was. The next change rebuilds the meshes.
            }
        }

        void Ensure(RhinoDoc doc)
        {
            if (_ready && _serial == doc.RuntimeSerialNumber) return;
            _meshes.Clear();
            _serial = doc.RuntimeSerialNumber;
            _ready = true;
            var settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = false,
                ActiveObjects = true,
                ReferenceObjects = false,
                DeletedObjects = false,
                IncludeLights = false,
                IncludeGrips = false
            };
            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                if (!string.Equals(obj?.Attributes?.GetUserString("forsk:kind"), RoomPlate.Kind, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!(obj.Geometry is Brep brep)) continue;
                var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                if (meshes == null) continue;
                var colour = ColourOf(doc, obj);
                foreach (var mesh in meshes)
                {
                    if (mesh == null || !mesh.IsValid) continue;
                    mesh.VertexColors.CreateMonotoneMesh(colour);
                    mesh.Transform(Transform.Translation(0, 0, 1));
                    _meshes.Add(mesh);
                }
            }
        }

        static Color ColourOf(RhinoDoc doc, RhinoObject plate)
        {
            var stored = plate.Attributes.GetUserString(RoomTypes.Key);
            if (string.IsNullOrEmpty(stored)
                && Guid.TryParse(plate.Attributes.GetUserString("forsk:marker"), out var markerId))
            {
                var marker = doc.Objects.FindId(markerId);
                stored = marker?.Attributes?.GetUserString(RoomTypes.Key);
            }
            var rgb = RoomTypes.Colour(stored);
            return Color.FromArgb(rgb.R, rgb.G, rgb.B);
        }
    }
}
