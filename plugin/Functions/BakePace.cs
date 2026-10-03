using System;
using System.Collections.Generic;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// The Generate 3D pass. Rhino view redraw stays off until the pass ends, so
/// an add does not repaint the model. Layer finds and opening markers are
/// remembered. A long stretch on the UI thread pumps once a slice is old,
/// which lets the Forsk page repaint. The window log gets one line of phase times.
/// </summary>
public static class BakePace
{
    public const string BuildingWalls = "Building walls…";
    public const string PlacingWindows = "Placing windows…";
    public const string PlacingDoors = "Placing doors…";

    static BakePhases _phases;
    static Action<string> _progress;
    static Action<string> _log;
    static RhinoDoc _doc;
    static bool _held;
    static bool _redrawWas = true;
    static bool _pumping;
    static bool _markersSwept;
    static string _shown;
    static int _sliceStart;
    static Dictionary<string, Layer> _layers;
    static Dictionary<string, int> _definitions;
    static List<RhinoObject> _markers;
    static List<RhinoObject> _extraMarkers;

    public static bool Active => _phases != null;
    public static bool HoldsRedraw => _held;
    public static bool MarkersSwept => Active && _markersSwept;

    public static void Begin(Action<string> progress, Action<string> log)
    {
        Clear();
        _phases = new BakePhases();
        _progress = progress;
        _log = log;
        _sliceStart = Environment.TickCount;
    }

    /// <summary>Redraw stays off until <see cref="End"/>. A second call does nothing.</summary>
    public static void Hold(RhinoDoc doc)
    {
        if (!Active || _held || doc == null) return;
        try
        {
            _redrawWas = doc.Views.RedrawEnabled;
            doc.Views.RedrawEnabled = false;
            _doc = doc;
            _held = true;
        }
        catch (Exception)
        {
            _held = false;
        }
    }

    public static void End(RhinoDoc doc)
    {
        var phases = _phases;
        var log = _log;
        if (phases == null) return;
        try
        {
            if (_held)
            {
                var restore = _redrawWas;
                var target = doc ?? _doc;
                _held = false;
                if (target != null)
                {
                    try { target.Views.RedrawEnabled = restore; }
                    catch (Exception) { }
                    try
                    {
                        using (phases.Time(BakePhases.Redraws))
                            target.Views.Redraw();
                    }
                    catch (Exception) { }
                }
            }
            try { log?.Invoke(phases.Line()); }
            catch (Exception) { }
        }
        finally
        {
            Clear();
        }
    }

    public static IDisposable Time(string name)
    {
        if (_phases == null || string.IsNullOrEmpty(name)) return Noop.Empty;
        return _phases.Time(name);
    }

    public static void Redraw(RhinoDoc doc)
    {
        if (doc == null || _held) return;
        if (_phases == null)
        {
            doc.Views.Redraw();
            return;
        }
        using (_phases.Time(BakePhases.Redraws))
            doc.Views.Redraw();
    }

    /// <summary>
    /// Show progress when the sentence changes, and pump the UI thread once
    /// the current slice is older than <see cref="BakePhases.SliceMs"/>.
    /// </summary>
    public static void Breathe(string progress)
    {
        if (_phases == null) return;
        var changed = !string.IsNullOrEmpty(progress) && !string.Equals(progress, _shown, StringComparison.Ordinal);
        var due = unchecked(Environment.TickCount - _sliceStart) >= BakePhases.SliceMs;
        if (!changed && !due) return;
        _phases.PauseClock();
        try
        {
            Pump();
            if (changed)
            {
                _shown = progress;
                try { _progress?.Invoke(progress); }
                catch (Exception) { }
                Pump();
            }
        }
        finally
        {
            _phases.ResumeClock();
            _sliceStart = Environment.TickCount;
        }
    }

    public static bool TryLayer(string name, out Layer layer)
    {
        layer = null;
        if (!Active || _layers == null || string.IsNullOrEmpty(name)) return false;
        return _layers.TryGetValue(name, out layer) && layer != null && !layer.IsDeleted;
    }

    public static void RememberLayer(string name, Layer layer)
    {
        if (!Active || layer == null || layer.IsDeleted || string.IsNullOrEmpty(name)) return;
        (_layers ??= new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase))[name] = layer;
    }

    public static bool TryDefinition(string token, out int index)
    {
        index = -1;
        if (!Active || _definitions == null || string.IsNullOrEmpty(token)) return false;
        return _definitions.TryGetValue(token, out index) && index >= 0;
    }

    public static void RememberDefinition(string token, int index)
    {
        if (!Active || index < 0 || string.IsNullOrEmpty(token)) return;
        (_definitions ??= new Dictionary<string, int>(StringComparer.Ordinal))[token] = index;
    }

    public static void MarkMarkersSwept()
    {
        if (Active) _markersSwept = true;
    }

    public static void NoteMarker(RhinoObject obj)
    {
        if (!Active || obj == null) return;
        (_extraMarkers ??= new List<RhinoObject>()).Add(obj);
    }

    /// <summary>
    /// Markers and opening blocks. The first call scans the file once. Markers
    /// added after that are appended, so a wall split does not walk the file again.
    /// </summary>
    public static IEnumerable<RhinoObject> Markers(RhinoDoc doc, Func<RhinoDoc, IEnumerable<RhinoObject>> scan)
    {
        if (!Active || scan == null) return scan == null ? Array.Empty<RhinoObject>() : scan(doc);
        if (_markers == null)
        {
            _markers = new List<RhinoObject>();
            foreach (var obj in scan(doc))
                if (obj != null) _markers.Add(obj);
        }
        if (_extraMarkers != null)
        {
            foreach (var extra in _extraMarkers)
            {
                if (extra == null) continue;
                var seen = false;
                for (var i = 0; i < _markers.Count; i++)
                {
                    if (_markers[i] != null && _markers[i].Id == extra.Id) { seen = true; break; }
                }
                if (!seen) _markers.Add(extra);
            }
            _extraMarkers.Clear();
        }
        return _markers;
    }

    /// <summary>One tight add of every solid. Redraw stays off for the whole list.</summary>
    public static List<Guid> AddBreps(RhinoDoc doc, IReadOnlyList<Brep> breps, IReadOnlyList<ObjectAttributes> attributes, string progress)
    {
        var count = Math.Min(breps?.Count ?? 0, attributes?.Count ?? 0);
        var ids = new List<Guid>(count);
        Hold(doc);
        for (var i = 0; i < count; i++)
        {
            Breathe(progress);
            ids.Add(doc.Objects.AddBrep(breps[i], attributes[i]));
        }
        return ids;
    }

    public static List<Guid> AddCurves(RhinoDoc doc, IReadOnlyList<Curve> curves, IReadOnlyList<ObjectAttributes> attributes, string progress)
    {
        var count = Math.Min(curves?.Count ?? 0, attributes?.Count ?? 0);
        var ids = new List<Guid>(count);
        Hold(doc);
        for (var i = 0; i < count; i++)
        {
            Breathe(progress);
            ids.Add(doc.Objects.AddCurve(curves[i], attributes[i]));
        }
        return ids;
    }

    public static bool Commit(RhinoObject obj)
    {
        if (obj == null) return false;
        if (_phases == null) return obj.CommitChanges();
        using (_phases.Time(BakePhases.Attributes))
            return obj.CommitChanges();
    }

    public static bool Modify(RhinoDoc doc, RhinoObject obj, ObjectAttributes attr, bool quiet)
    {
        if (doc == null || obj == null) return false;
        if (_phases == null) return doc.Objects.ModifyAttributes(obj, attr, quiet);
        using (_phases.Time(BakePhases.Attributes))
            return doc.Objects.ModifyAttributes(obj, attr, quiet);
    }

    public static bool Modify(RhinoDoc doc, Guid id, ObjectAttributes attr, bool quiet)
    {
        if (doc == null) return false;
        if (_phases == null) return doc.Objects.ModifyAttributes(id, attr, quiet);
        using (_phases.Time(BakePhases.Attributes))
            return doc.Objects.ModifyAttributes(id, attr, quiet);
    }

    static void Pump()
    {
        var app = Application.Instance;
        if (app == null || _pumping) return;
        _pumping = true;
        try { app.RunIteration(); }
        catch (Exception) { }
        finally { _pumping = false; }
    }

    sealed class Noop : IDisposable
    {
        public static readonly Noop Empty = new Noop();
        public void Dispose() { }
    }

    static void Clear()
    {
        _phases = null;
        _progress = null;
        _log = null;
        _doc = null;
        _held = false;
        _redrawWas = true;
        _pumping = false;
        _markersSwept = false;
        _shown = null;
        _layers = null;
        _definitions = null;
        _markers = null;
        _extraMarkers = null;
    }
}
