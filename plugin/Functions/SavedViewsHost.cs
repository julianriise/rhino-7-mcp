using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// Saved views (SavedViews) in the document: Rhino named views, saved in the
/// .3dm, plus the display mode each was saved with in the document strings.
/// Every call runs on the UI thread, from the window's view picker.
/// </summary>
internal static class SavedViewsHost
{
    /// <summary>The picker's saved views: the file's named views without the render views.</summary>
    internal static List<string> Names(RhinoDoc doc)
    {
        if (doc == null) return new List<string>();
        return SavedViews.Listed(AllNames(doc), Renders(doc));
    }

    static IEnumerable<string> AllNames(RhinoDoc doc)
    {
        for (var i = 0; i < doc.NamedViews.Count; i++)
            yield return doc.NamedViews[i]?.Name;
    }

    static IEnumerable<string> Renders(RhinoDoc doc)
    {
        return ForskInteriorHost.Views(doc, false).Concat(ForskInteriorHost.Views(doc, true));
    }

    /// <summary>The model view the picker works in: the active one, else the first model view.</summary>
    static RhinoView ModelView(RhinoDoc doc)
    {
        var view = doc.Views.ActiveView;
        if (view == null || view is RhinoPageView)
            view = doc.Views.GetViewList(true, false).FirstOrDefault(v => !(v is RhinoPageView));
        return view;
    }

    /// <summary>The saved view the active model viewport shows now (same eye and target), or null.</summary>
    internal static string Shown(RhinoDoc doc, IList<string> names)
    {
        var view = doc?.Views.ActiveView;
        if (view == null || view is RhinoPageView || names == null || names.Count == 0) return null;
        var vp = view.MainViewport;
        var eye = Xyz(vp.CameraLocation);
        var target = Xyz(vp.CameraTarget);
        foreach (var name in names)
        {
            var index = doc.NamedViews.FindByName(name);
            var saved = index < 0 ? null : doc.NamedViews[index]?.Viewport;
            if (saved == null || saved.IsParallelProjection != vp.IsParallelProjection) continue;
            if (ForskInterior.SameCamera(eye, target, Xyz(saved.CameraLocation), Xyz(saved.TargetPoint))) return name;
        }
        return null;
    }

    /// <summary>
    /// Save current view: the active model viewport as a new named view,
    /// "View n", with its display mode. The line for the chat.
    /// </summary>
    internal static string Save(RhinoDoc doc, out string name)
    {
        name = null;
        if (doc == null) return "No file open.";
        var view = ModelView(doc);
        if (view == null) return "No model view to save.";
        var names = Names(doc);
        var already = Shown(doc, names);
        if (already != null && view == doc.Views.ActiveView)
        {
            name = already;
            return "This view is already saved as " + already + ".";
        }
        name = SavedViews.NextName(AllNames(doc));
        var vp = view.MainViewport;
        if (doc.NamedViews.Add(name, vp.Id) < 0)
        {
            name = null;
            return "The view could not be saved.";
        }
        var mode = vp.DisplayMode;
        if (mode != null) doc.Strings.SetString(SavedViews.ModesSection, name, mode.Id.ToString());
        return "Saved this view as " + name + ". To rename it, use Rename or delete… in the view picker.";
    }

    /// <summary>
    /// A saved view in the active model viewport, with the display mode it was
    /// saved with (else the Forsk look for its projection) and the plan cut.
    /// The reason when it cannot, else null.
    /// </summary>
    internal static string Show(RhinoDoc doc, string name)
    {
        if (doc == null) return "No file open.";
        var index = doc.NamedViews.FindByName(name);
        if (index < 0) return "There is no saved view called " + name + ".";
        var view = ModelView(doc);
        if (view == null) return "No model view to show it in.";
        var vp = view.MainViewport;
        if (!doc.NamedViews.Restore(index, vp)) return "The view did not change.";
        var mode = SavedMode(doc, name) ?? ForskWhiteHost.ModeFor(doc, ForskTechnicalHost.LookOf(vp));
        if (mode != null) vp.DisplayMode = mode;
        // A render look needs the roof on; any other look hides the roof a render turned on.
        if (ForskInterior.IsRenderMode(vp.DisplayMode?.EnglishName)) RhinoMCPFunctions.ShowRoofForRender(doc);
        else RhinoMCPFunctions.HideRoofAfterRender(doc);
        doc.Views.ActiveView = view;
        ForskPlanCutHost.Apply(doc, ForskWhiteHost.Enabled());
        view.Redraw();
        return null;
    }

    static DisplayModeDescription SavedMode(RhinoDoc doc, string name)
    {
        var stored = doc.Strings.GetValue(SavedViews.ModesSection, name);
        return Guid.TryParse(stored, out var id) ? DisplayModeDescription.GetDisplayMode(id) : null;
    }

    /// <summary>
    /// The saved views card's Save: emptied names delete, new names rename.
    /// The receipt line for the chat, or null when nothing changed.
    /// </summary>
    internal static string Apply(RhinoDoc doc, IList<string> names, IList<string> typed)
    {
        if (doc == null) return "No file open.";
        // A view deleted in Rhino since the card opened is skipped.
        var live = new List<string>();
        var liveTyped = new List<string>();
        for (var i = 0; i < (names?.Count ?? 0); i++)
        {
            if (doc.NamedViews.FindByName(names[i]) < 0) continue;
            live.Add(names[i]);
            liveTyped.Add(typed != null && i < typed.Count ? typed[i] : names[i]);
        }
        var reserved = AllNames(doc).Where(n => !live.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var changes = SavedViews.Edits(live, liveTyped, reserved, out var refused);
        int renamed = 0, deleted = 0;
        foreach (var change in changes)
        {
            var index = doc.NamedViews.FindByName(change.From);
            if (index < 0) continue;
            var mode = doc.Strings.GetValue(SavedViews.ModesSection, change.From);
            if (change.Deletes)
            {
                if (!doc.NamedViews.Delete(index)) continue;
                if (mode != null) doc.Strings.Delete(SavedViews.ModesSection, change.From);
                deleted++;
                continue;
            }
            if (!doc.NamedViews.Rename(index, change.To)) continue;
            if (mode != null)
            {
                doc.Strings.Delete(SavedViews.ModesSection, change.From);
                doc.Strings.SetString(SavedViews.ModesSection, change.To, mode);
            }
            renamed++;
        }
        var parts = new List<string>();
        if (renamed > 0) parts.Add(renamed == 1 ? "Renamed 1 view" : "Renamed " + renamed + " views");
        if (deleted > 0) parts.Add((parts.Count > 0 ? "deleted " : "Deleted ") + (deleted == 1 ? "1 view" : deleted + " views"));
        var line = parts.Count == 0 ? null : string.Join(", ", parts) + ".";
        if (refused.Count > 0)
            line = (line == null ? "" : line + " ") + string.Join(", ", refused) + (refused.Count == 1 ? " is" : " are") + " already taken, so that view kept its name.";
        return line;
    }

    static double[] Xyz(Point3d p) => new[] { p.X, p.Y, p.Z };
}
