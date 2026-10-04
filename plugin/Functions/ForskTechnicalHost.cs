using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Keeps plans and elevations on Forsk Technical and draws what the mode
/// cannot: the door and window symbols and the stair on a plan, the ground
/// line on an elevation. The pieces are built once per object and rebuilt
/// only when the document events say it changed (ForskTechnicalCache), on
/// idle, never while drawing. Opening blocks and stairs are not drawn on a
/// plan unless selected; their symbols stand in for them, as on the sheet.
/// </summary>
internal static class ForskTechnicalHost
{
    static readonly ForskTechnicalCache<ForskTechnicalLinework> Cache = new ForskTechnicalCache<ForskTechnicalLinework>();
    static readonly RhinoMCPFunctions Functions = new RhinoMCPFunctions();

    static readonly EventHandler<RhinoObjectEventArgs> Added = (_, args) => Changed(args?.TheObject, false);
    static readonly EventHandler<RhinoObjectEventArgs> Deleted = (_, args) => Changed(args?.TheObject, true);
    static readonly EventHandler<RhinoReplaceObjectEventArgs> Replaced = (_, args) => Changed(args?.NewRhinoObject, false);
    static readonly EventHandler<RhinoModifyObjectAttributesEventArgs> Modified =
        (_, args) => Changed(args?.RhinoObject, args?.NewAttributes, false);
    static readonly EventHandler<RhinoDoc.UserStringChangedArgs> StringChanged = (_, __) => _profileDirty = true;
    static readonly EventHandler Idle = (_, __) => OnIdle();

    static ForskTechnicalConduit _conduit;
    static DisplayModeDescription _mode;
    static string _modeSignature;
    static uint _docSerial;
    static PrintProfile _profile = PrintProfiles.Default;
    static bool _profileDirty = true;
    static double _cutZ;
    static BoundingBox _extent = BoundingBox.Empty;
    static double? _groundZ;
    static bool _working;
    static bool _reported;

    internal static void Start()
    {
        if (_conduit != null) return;
        RhinoDoc.AddRhinoObject += Added;
        RhinoDoc.DeleteRhinoObject += Deleted;
        RhinoDoc.UndeleteRhinoObject += Added;
        RhinoDoc.ReplaceRhinoObject += Replaced;
        RhinoDoc.ModifyObjectAttributes += Modified;
        RhinoDoc.UserStringChanged += StringChanged;
        RhinoApp.Idle += Idle;
        _conduit = new ForskTechnicalConduit { Enabled = true };
    }

    internal static void Stop()
    {
        if (_conduit == null) return;
        _conduit.Enabled = false;
        _conduit = null;
        RhinoDoc.AddRhinoObject -= Added;
        RhinoDoc.DeleteRhinoObject -= Deleted;
        RhinoDoc.UndeleteRhinoObject -= Added;
        RhinoDoc.ReplaceRhinoObject -= Replaced;
        RhinoDoc.ModifyObjectAttributes -= Modified;
        RhinoDoc.UserStringChanged -= StringChanged;
        RhinoApp.Idle -= Idle;
        Cache.Reset();
    }

    internal static bool Enabled()
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return true;
        return settings.GetBool(ForskTechnical.SettingKey, true);
    }

    internal static void SetEnabled(bool enabled)
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return;
        settings.SetBool(ForskTechnical.SettingKey, enabled);
    }

    internal static ForskTechnical.Look LookOf(RhinoViewport viewport)
    {
        if (viewport == null) return ForskTechnical.Look.Model;
        var dir = viewport.CameraDirection;
        return ForskTechnical.Classify(viewport.IsParallelProjection, dir.X, dir.Y, dir.Z);
    }

    /// <summary>The imported Forsk Technical for the document's print profile. Imported again when the profile or revision moved.</summary>
    internal static DisplayModeDescription Ensure(RhinoDoc doc)
    {
        Sync(doc);
        var signature = ForskTechnical.Signature(_profile);
        if (_mode != null && Fresh() && DisplayModeDescription.GetDisplayMode(_mode.Id) != null)
            return _mode;
        var existing = ForskWhiteHost.Find(ForskTechnical.ModeName);
        if (existing == null || ForskTechnical.NeedsReimport(StoredSignature(), _profile))
        {
            var profile = _profile;
            if (ForskWhiteHost.Import(ForskTechnical.ModeName, exported => ForskTechnical.Patch(exported, profile)))
                StoreSignature(signature);
            existing = ForskWhiteHost.Find(ForskTechnical.ModeName);
        }
        _mode = existing;
        _modeSignature = existing == null ? null : signature;
        return existing;
    }

    static bool Fresh()
    {
        return _modeSignature == ForskTechnical.Signature(_profile);
    }

    internal static bool IsTechnical(RhinoViewport viewport)
    {
        var id = viewport?.DisplayMode?.Id;
        return _mode != null && id.HasValue && id.Value == _mode.Id;
    }

    internal static bool Hides(RhinoObject obj)
    {
        return obj != null && Cache.IsHidden(obj.Id) && obj.IsSelected(false) <= 0;
    }

    internal static bool Current(RhinoDoc doc)
    {
        return doc != null && doc.RuntimeSerialNumber == _docSerial;
    }

    internal static IEnumerable<ForskTechnicalLinework> Plan => Cache.Values;

    internal static ForskTechnical.Stroke Ground(RhinoViewport viewport)
    {
        if (!_extent.IsValid || viewport == null) return null;
        var right = viewport.CameraX;
        return ForskTechnical.GroundLine(_extent.Min.X, _extent.Min.Y, _extent.Max.X, _extent.Max.Y, _groundZ, right.X, right.Y, _profile);
    }

    static string StoredSignature()
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return null;
        try { return settings.GetString(ForskTechnical.SignatureKey, ""); }
        catch (Exception) { return null; }
    }

    static void StoreSignature(string signature)
    {
        var settings = global::RhinoMCPPlugin.RhinoMCPPlugin.Instance?.Settings;
        if (settings == null) return;
        try { settings.SetString(ForskTechnical.SignatureKey, signature); }
        catch (Exception) { }
    }

    static void Changed(RhinoObject obj, bool gone)
    {
        Changed(obj, obj?.Attributes, gone);
    }

    static void Changed(RhinoObject obj, ObjectAttributes attributes, bool gone)
    {
        if (obj == null || attributes == null || !Current(obj.Document)) return;
        var kind = attributes.GetUserString("forsk:kind");
        if (!ForskTechnicalCache<ForskTechnicalLinework>.Matters(kind)) return;
        Guid.TryParse(attributes.GetUserString("forsk:host"), out var host);
        Cache.Changed(obj.Id, kind, host, gone);
    }

    /// <summary>A new document forgets the cache; a new profile makes it stale.</summary>
    static void Sync(RhinoDoc doc)
    {
        if (doc == null) return;
        if (doc.RuntimeSerialNumber != _docSerial)
        {
            _docSerial = doc.RuntimeSerialNumber;
            Cache.Reset();
            _profileDirty = true;
        }
        if (!_profileDirty) return;
        _profileDirty = false;
        _profile = RhinoMCPFunctions.ReadPrintProfile(doc);
        Cache.SetContext(ForskTechnical.Signature(_profile));
    }

    static void OnIdle()
    {
        if (_working) return;
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;
        _working = true;
        try
        {
            Sync(doc);
            var redraw = Retune(doc);
            if (AnyTechnical(doc)) redraw |= Refresh(doc);
            if (redraw) doc.Views.Redraw();
        }
        catch (Exception ex)
        {
            Report(ex.Message);
        }
        finally
        {
            _working = false;
        }
    }

    /// <summary>A view on a Forsk mode follows its projection: a plan or an elevation to Forsk Technical, the rest to Forsk White.</summary>
    static bool Retune(RhinoDoc doc)
    {
        var whiteOn = ForskWhiteHost.Enabled();
        if (!whiteOn) return false;
        var technicalOn = Enabled();
        var changed = false;
        foreach (var view in doc.Views)
        {
            if (view == null || view is RhinoPageView) continue;
            var viewport = view.MainViewport;
            var mode = viewport?.DisplayMode;
            if (mode == null || !ForskWhite.IsForskMode(mode.EnglishName)) continue;
            var wanted = ForskTechnical.Wanted(whiteOn, technicalOn, LookOf(viewport));
            if (wanted == mode.EnglishName && (wanted == ForskWhite.ModeName || IsTechnical(viewport) && Fresh())) continue;
            var target = wanted == ForskTechnical.ModeName ? Ensure(doc) : ForskWhiteHost.Loaded();
            if (target == null) continue;
            if (!ForskTechnical.Retunes(mode.EnglishName, target.EnglishName, mode.Id == target.Id)) continue;
            viewport.DisplayMode = target;
            changed = true;
        }
        return changed;
    }

    static bool AnyTechnical(RhinoDoc doc)
    {
        foreach (var view in doc.Views)
            if (view != null && !(view is RhinoPageView) && IsTechnical(view.MainViewport)) return true;
        return false;
    }

    /// <summary>Rebuild what is stale. True when anything drawn changed.</summary>
    static bool Refresh(RhinoDoc doc)
    {
        var version = Cache.Version;
        if (Cache.AllDirty)
        {
            Cache.BeginAll();
            _cutZ = ForskPlanCut.Describe(ForskPlanCutHost.FloorTops(doc)).Z;
            foreach (var obj in ForskPlanCutHost.Listed(doc))
            {
                var kind = obj?.Attributes?.GetUserString("forsk:kind");
                if (ForskTechnicalCache<ForskTechnicalLinework>.Hides(kind)) Cache.Hide(obj.Id);
                if (ForskTechnicalCache<ForskTechnicalLinework>.Draws(kind)) Build(doc, obj);
            }
        }
        else
        {
            foreach (var id in Cache.TakeDirty())
            {
                var obj = doc.Objects.FindId(id);
                if (obj == null || obj.IsDeleted) Cache.Remove(id);
                else Build(doc, obj);
            }
        }
        var changed = Cache.Version != version;
        if (Cache.GroundDirty) changed |= Ground(doc);
        return changed;
    }

    static void Build(RhinoDoc doc, RhinoObject obj)
    {
        List<ForskTechnical.Stroke> strokes;
        Guid host;
        try { strokes = Functions.ScreenSymbol(doc, obj, _cutZ, _profile, out host); }
        catch (Exception) { strokes = null; host = Guid.Empty; }
        if (strokes == null || strokes.Count == 0) Cache.Remove(obj.Id);
        else Cache.Put(obj.Id, host, new ForskTechnicalLinework(strokes));
    }

    /// <summary>The model's plan extent and ground, from the generated walls, floors and roofs.</summary>
    static bool Ground(RhinoDoc doc)
    {
        var extent = BoundingBox.Empty;
        var solids = new List<Sections.Solid>();
        foreach (var obj in ForskPlanCutHost.Listed(doc))
        {
            var kind = obj?.Attributes?.GetUserString("forsk:kind");
            if (!ForskTechnicalCache<ForskTechnicalLinework>.Grounds(kind)) continue;
            if (obj.Attributes.GetUserString("forsk:generated") != "1") continue;
            var box = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (!box.IsValid) continue;
            extent.Union(box);
            solids.Add(new Sections.Solid { Kind = kind, MinZ = box.Min.Z, MaxZ = box.Max.Z });
        }
        var ground = Sections.ModelHeights(solids, null, null).Ground;
        Cache.GroundDone();
        var changed = ground != _groundZ || extent.IsValid != _extent.IsValid
            || extent.IsValid && (extent.Min != _extent.Min || extent.Max != _extent.Max);
        _extent = extent;
        _groundZ = ground;
        return changed;
    }

    internal static void Report(string message)
    {
        if (_reported) return;
        _reported = true;
        RhinoApp.WriteLine("Forsk Technical did not load: " + (message ?? "unknown error"));
    }
}

/// <summary>One object's plan pieces, ready to draw: solid lines batched by ink and weight.</summary>
internal sealed class ForskTechnicalLinework
{
    const int DashedArcSegments = 12;

    readonly List<(Color Ink, int Px, Line[] Lines)> _solid = new List<(Color, int, Line[])>();
    readonly List<(Line Line, Color Ink)> _dashed = new List<(Line, Color)>();
    readonly List<(Arc Arc, Color Ink, int Px)> _arcs = new List<(Arc, Color, int)>();
    readonly List<(Circle Circle, Color Ink, int Px)> _dots = new List<(Circle, Color, int)>();
    readonly List<(string Text, Point3d At, Color Ink)> _texts = new List<(string, Point3d, Color)>();

    public ForskTechnicalLinework(IEnumerable<ForskTechnical.Stroke> strokes)
    {
        var solid = new Dictionary<(Color, int), List<Line>>();
        foreach (var stroke in strokes)
        {
            var a = new Point3d(stroke.X0, stroke.Y0, stroke.Z);
            var b = new Point3d(stroke.X1, stroke.Y1, stroke.Z);
            switch (stroke.Shape)
            {
                case "line":
                    if (stroke.Dashed) _dashed.Add((new Line(a, b), stroke.Ink));
                    else Batch(solid, stroke.Ink, stroke.Px).Add(new Line(a, b));
                    break;
                case "arc":
                    var arc = new Arc(a, new Point3d(stroke.Xm, stroke.Ym, stroke.Z), b);
                    if (!arc.IsValid) break;
                    if (!stroke.Dashed)
                    {
                        _arcs.Add((arc, stroke.Ink, stroke.Px));
                        break;
                    }
                    for (var i = 0; i < DashedArcSegments; i++)
                    {
                        var t0 = arc.AngleDomain.ParameterAt(i / (double)DashedArcSegments);
                        var t1 = arc.AngleDomain.ParameterAt((i + 1) / (double)DashedArcSegments);
                        _dashed.Add((new Line(arc.PointAt(t0), arc.PointAt(t1)), stroke.Ink));
                    }
                    break;
                case "dot":
                    if (stroke.Radius > 0) _dots.Add((new Circle(a, stroke.Radius), stroke.Ink, stroke.Px));
                    break;
                case "text":
                    if (!string.IsNullOrEmpty(stroke.Text)) _texts.Add((stroke.Text, a, stroke.Ink));
                    break;
            }
        }
        foreach (var pair in solid) _solid.Add((pair.Key.Item1, pair.Key.Item2, pair.Value.ToArray()));
    }

    static List<Line> Batch(Dictionary<(Color, int), List<Line>> solid, Color ink, int px)
    {
        if (!solid.TryGetValue((ink, px), out var lines))
        {
            lines = new List<Line>();
            solid[(ink, px)] = lines;
        }
        return lines;
    }

    public void Draw(DisplayPipeline display)
    {
        foreach (var batch in _solid) display.DrawLines(batch.Lines, batch.Ink, batch.Px);
        foreach (var dashed in _dashed) display.DrawDottedLine(dashed.Line, dashed.Ink);
        foreach (var arc in _arcs) display.DrawArc(arc.Arc, arc.Ink, arc.Px);
        foreach (var dot in _dots) display.DrawCircle(dot.Circle, dot.Ink, Math.Max(2, dot.Px));
        foreach (var text in _texts) display.Draw2dText(text.Text, text.Ink, text.At, true, 12);
    }
}

sealed class ForskTechnicalConduit : DisplayConduit
{
    bool _plan;

    public ForskTechnicalConduit()
    {
        SpaceFilter = ActiveSpace.ModelSpace;
    }

    protected override void PreDrawObjects(DrawEventArgs args)
    {
        try
        {
            _plan = ForskTechnicalHost.Current(args?.RhinoDoc)
                && ForskTechnicalHost.IsTechnical(args.Viewport)
                && ForskTechnicalHost.LookOf(args.Viewport) == ForskTechnical.Look.Plan;
        }
        catch (Exception ex)
        {
            _plan = false;
            ForskTechnicalHost.Report(ex.Message);
        }
    }

    protected override void PreDrawObject(DrawObjectEventArgs args)
    {
        if (_plan && ForskTechnicalHost.Hides(args?.RhinoObject)) args.DrawObject = false;
    }

    protected override void DrawForeground(DrawEventArgs args)
    {
        try
        {
            var viewport = args?.Viewport;
            var display = args?.Display;
            if (display == null || !ForskTechnicalHost.Current(args.RhinoDoc) || !ForskTechnicalHost.IsTechnical(viewport)) return;
            var look = ForskTechnicalHost.LookOf(viewport);
            if (look == ForskTechnical.Look.Plan)
            {
                foreach (var linework in ForskTechnicalHost.Plan) linework.Draw(display);
            }
            else if (look == ForskTechnical.Look.Elevation)
            {
                var ground = ForskTechnicalHost.Ground(viewport);
                if (ground != null)
                    display.DrawLine(new Point3d(ground.X0, ground.Y0, ground.Z), new Point3d(ground.X1, ground.Y1, ground.Z), ground.Ink, ground.Px);
            }
        }
        catch (Exception ex)
        {
            ForskTechnicalHost.Report(ex.Message);
        }
    }
}
