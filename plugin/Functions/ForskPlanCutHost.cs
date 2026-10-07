using System;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// One locked clipping plane on a hidden layer, tagged forsk:plan_cut.
/// It clips the Top view only. Forsk White owns the switch: off deletes it.
/// The plane is updated in place. A second plane is not added.
/// </summary>
internal static class ForskPlanCutHost
{
    const double SamePointMm = 0.5;

    static readonly EventHandler<RhinoObjectEventArgs> ObjectChanged = (_, __) => Mark();
    static readonly EventHandler Idle = (_, __) => OnIdle();

    static bool _hooked;
    static bool _writing;
    static bool _dirty;
    static bool _reported;

    internal static void Start()
    {
        if (_hooked) return;
        _hooked = true;
        RhinoDoc.AddRhinoObject += ObjectChanged;
        RhinoDoc.DeleteRhinoObject += ObjectChanged;
        RhinoDoc.UndeleteRhinoObject += ObjectChanged;
        RhinoApp.Idle += Idle;
    }

    internal static void Stop()
    {
        if (!_hooked) return;
        _hooked = false;
        RhinoDoc.AddRhinoObject -= ObjectChanged;
        RhinoDoc.DeleteRhinoObject -= ObjectChanged;
        RhinoDoc.UndeleteRhinoObject -= ObjectChanged;
        RhinoApp.Idle -= Idle;
        _dirty = false;
    }

    internal static void Apply(RhinoDoc doc, bool polishOn)
    {
        if (doc == null || _writing) return;
        _writing = true;
        try
        {
            var changed = polishOn ? Ensure(doc) : DeleteAll(doc);
            if (changed) doc.Views.Redraw();
        }
        catch (Exception ex)
        {
            Report(ex.Message);
        }
        finally
        {
            _writing = false;
        }
    }

    static void Mark()
    {
        if (!_writing) _dirty = true;
    }

    static void OnIdle()
    {
        if (_writing) return;
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;
        // The cut follows the views: a viewport turned to a plan or away from one moves no object.
        var views = string.Join(",", Wanted(doc));
        if (!_dirty && views == _views) return;
        _dirty = false;
        _views = views;
        Apply(doc, ForskWhiteHost.Enabled());
    }

    static string _views;

    static bool Ensure(RhinoDoc doc)
    {
        var found = Find(doc);
        var changed = false;
        for (var i = 1; i < found.Count; i++)
            changed |= doc.Objects.Delete(found[i].Id, true);

        var keep = found.Count > 0 ? found[0] : null;
        var cut = ForskPlanCut.Describe(FloorTops(doc));
        var plane = new Plane(
            new Point3d(cut.OriginX, cut.OriginY, cut.Z),
            Vector3d.XAxis,
            new Vector3d(0, -1, 0));
        var wanted = Wanted(doc);
        if (wanted.Count == 0)
        {
            // No plan view now: the cut clips none. It stays (it is locked, and Delete refuses a locked object).
            if (keep != null) changed |= SyncViews(doc, keep, wanted);
            return changed;
        }

        if (keep != null && SamePlane(keep.ClippingPlaneGeometry, plane))
        {
            changed |= SyncViews(doc, keep, wanted);
            changed |= FixStamp(doc, keep);
            return changed;
        }

        if (keep != null) changed |= doc.Objects.Delete(keep.Id, true);
        changed |= Add(doc, plane, wanted) != Guid.Empty;
        return changed;
    }

    static bool DeleteAll(RhinoDoc doc)
    {
        var changed = false;
        foreach (var plane in Find(doc))
            changed |= doc.Objects.Delete(plane.Id, true);
        return changed;
    }

    static Guid Add(RhinoDoc doc, Plane plane, List<Guid> viewports)
    {
        var layer = HiddenLayer(doc);
        if (layer < 0)
        {
            Report("the cut layer was not added");
            return Guid.Empty;
        }
        var attr = new ObjectAttributes
        {
            LayerIndex = layer,
            Name = ForskPlanCut.ObjectName,
            Mode = ObjectMode.Locked
        };
        attr.SetUserString(ForskPlanCut.TagKey, ForskPlanCut.TagValue);
        var id = doc.Objects.AddClippingPlane(plane, ForskPlanCut.SizeMm, ForskPlanCut.SizeMm, viewports, attr);
        if (id == Guid.Empty)
        {
            Report("the clipping plane was not added");
            return Guid.Empty;
        }
        var added = doc.Objects.FindId(id);
        if (added != null && !added.IsLocked)
            doc.Objects.Lock(id, true);
        return id;
    }

    static bool SyncViews(RhinoDoc doc, ClippingPlaneObject plane, List<Guid> wanted)
    {
        var map = Viewports(doc);
        var want = new HashSet<Guid>(wanted);
        var changed = false;
        var current = plane.ClippingPlaneGeometry?.ViewportIds();
        if (current != null)
        {
            foreach (var id in current)
            {
                if (want.Contains(id)) continue;
                Rhino.Display.RhinoViewport viewport;
                if (!map.TryGetValue(id, out viewport)) continue;
                changed |= plane.RemoveClipViewport(viewport, true);
            }
        }
        var have = new HashSet<Guid>(plane.ClippingPlaneGeometry?.ViewportIds() ?? new Guid[0]);
        foreach (var id in wanted)
        {
            if (have.Contains(id)) continue;
            Rhino.Display.RhinoViewport viewport;
            if (!map.TryGetValue(id, out viewport)) continue;
            changed |= plane.AddClipViewport(viewport, true);
        }
        return changed;
    }

    static bool FixStamp(RhinoDoc doc, ClippingPlaneObject plane)
    {
        var layer = HiddenLayer(doc);
        if (layer < 0) return false;
        var attrs = plane.Attributes;
        var generated = attrs?.GetUserString("forsk:generated");
        var kind = attrs?.GetUserString("forsk:kind");
        var tagged = attrs?.GetUserString(ForskPlanCut.TagKey);
        var named = attrs != null && attrs.Name == ForskPlanCut.ObjectName;
        var placed = attrs != null && attrs.LayerIndex == layer;
        if (!named || !placed || tagged != ForskPlanCut.TagValue || generated == "1" || !string.IsNullOrEmpty(kind))
        {
            var copy = attrs?.Duplicate() ?? new ObjectAttributes();
            copy.LayerIndex = layer;
            copy.Name = ForskPlanCut.ObjectName;
            copy.Mode = ObjectMode.Locked;
            copy.SetUserString(ForskPlanCut.TagKey, ForskPlanCut.TagValue);
            copy.SetUserString("forsk:generated", null);
            copy.SetUserString("forsk:kind", null);
            var wrote = doc.Objects.ModifyAttributes(plane, copy, true);
            if (wrote && !plane.IsLocked) doc.Objects.Lock(plane.Id, true);
            return wrote;
        }
        if (!plane.IsLocked)
        {
            doc.Objects.Lock(plane.Id, true);
            return true;
        }
        return false;
    }

    static List<Guid> Wanted(RhinoDoc doc)
    {
        var slots = new List<ForskPlanCut.ViewSlot>();
        foreach (var view in doc.Views)
        {
            var viewport = view?.MainViewport;
            if (viewport == null) continue;
            slots.Add(new ForskPlanCut.ViewSlot(
                viewport.Id.ToString("D"),
                viewport.Name,
                view.GetType().Name,
                ForskTechnicalHost.LookOf(viewport) == ForskTechnical.Look.Plan));
        }
        var ids = ForskPlanCut.TopIds(slots);
        var guids = new List<Guid>(ids.Count);
        foreach (var id in ids)
        {
            Guid parsed;
            if (Guid.TryParse(id, out parsed)) guids.Add(parsed);
        }
        return guids;
    }

    static Dictionary<Guid, Rhino.Display.RhinoViewport> Viewports(RhinoDoc doc)
    {
        var map = new Dictionary<Guid, Rhino.Display.RhinoViewport>();
        foreach (var view in doc.Views)
        {
            var viewport = view?.MainViewport;
            if (viewport == null) continue;
            map[viewport.Id] = viewport;
        }
        return map;
    }

    internal static List<double> FloorTops(RhinoDoc doc)
    {
        var tops = new List<double>();
        foreach (var obj in Listed(doc))
        {
            var kind = obj.Attributes?.GetUserString("forsk:kind");
            if (!string.Equals(kind, "floor", StringComparison.OrdinalIgnoreCase)) continue;
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            tops.Add(box.Max.Z);
        }
        return tops;
    }

    static List<ClippingPlaneObject> Find(RhinoDoc doc)
    {
        var list = new List<ClippingPlaneObject>();
        foreach (var obj in Listed(doc))
        {
            var plane = obj as ClippingPlaneObject;
            if (plane == null) continue;
            var tag = obj.Attributes?.GetUserString(ForskPlanCut.TagKey);
            if (tag == ForskPlanCut.TagValue) list.Add(plane);
        }
        return list;
    }

    /// <summary>Every live object, hidden and locked ones too.</summary>
    internal static IEnumerable<RhinoObject> Listed(RhinoDoc doc)
    {
        var settings = new ObjectEnumeratorSettings
        {
            NormalObjects = true,
            LockedObjects = true,
            HiddenObjects = true,
            ActiveObjects = true,
            ReferenceObjects = false,
            DeletedObjects = false,
            IncludeLights = false,
            IncludeGrips = false,
            IdefObjects = false
        };
        return doc.Objects.GetObjectList(settings);
    }

    static int HiddenLayer(RhinoDoc doc)
    {
        for (var i = 0; i < doc.Layers.Count; i++)
        {
            var layer = doc.Layers[i];
            if (layer == null || layer.IsDeleted) continue;
            if (!layer.Name.Equals(ForskPlanCut.LayerName, StringComparison.OrdinalIgnoreCase)) continue;
            if (layer.IsVisible)
            {
                layer.IsVisible = false;
                doc.Layers.Modify(layer, layer.Index, true);
            }
            return layer.Index;
        }
        return doc.Layers.Add(new Layer
        {
            Name = ForskPlanCut.LayerName,
            IsVisible = false
        });
    }

    static bool SamePlane(ClippingPlaneSurface geom, Plane target)
    {
        if (geom == null) return false;
        var live = geom.Plane;
        if (live.Origin.DistanceTo(target.Origin) > SamePointMm) return false;
        return (live.ZAxis - target.ZAxis).Length < 1e-6;
    }

    static void Report(string message)
    {
        if (_reported) return;
        _reported = true;
        RhinoApp.WriteLine("Forsk plan cut did not load: " + (message ?? "unknown error"));
    }
}
